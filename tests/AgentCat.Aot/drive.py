#!/usr/bin/env python3
"""Runs a published AOT app against a fake AgentCat events API and checks the events it sends.

Usage: drive.py stdio|http <target framework> <app executable> <AgentCat package version>

stdio: initialize, tools/list, an echo call and a boom call over stdin; then stdin closes and the app must exit 0.
http:  one stateless tools/call POST with a query string and a sentinel header; then SIGTERM, and the app must exit 0.

The app runs with its own HOME (so the AgentCat log file lands in a temporary folder), AGENTCAT_API_URL pointing at
the fake API and diagnostics off. In both modes the AgentCat log must have a sent line for every event and no warning,
error or failure line. Prints the events and the AgentCat log; on any failure, or an exception in the driver, also the
reasons and the app's output, kills the app if it still runs, and exits 1.
"""

import json
import os
import queue
import re
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

EVENTS_PATH = "/v1/public/events/"
TOOL_CALL = "mcp:tools/call"
PROTOCOL_VERSION = "2025-06-18"
RESPONSE_TIMEOUT = 30
EVENT_TIMEOUT = 15
EXIT_TIMEOUT = 30

STDIO_PROJECT = "proj_aot_stdio"
HTTP_PROJECT = "proj_aot_http"
BOOM_MESSAGE = "boom from Helper.Deep.ThrowAsync"
# The in-app frames of the boom event, innermost first, with the values the JIT gives.
BOOM_FRAMES = [("ThrowAsync", "AotStdioServer.Helper+Deep"), ("Boom", "AotStdioServer.Outer+Inner")]
# .NET 8 has no DiagnosticMethodInfo, and under Native AOT its StackFrame.GetMethod() returns null for these methods, so
# AgentCat drops the nameless frames and leaves frames out; the backend then groups by message. The stack text names
# both methods, with dots for nested types.
FRAMELESS_FRAMEWORKS = {"net8.0"}
BOOM_STACK_METHODS = ["AotStdioServer.Helper.Deep.<ThrowAsync>", "AotStdioServer.Outer.Inner.<Boom>"]
SENTINEL_HEADER = "x-agentcat-sentinel"
SENTINEL_VALUE = "s3ntinel-value"
QUERY = "?probe=sentinel-query"
TIMESTAMP = re.compile(r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$")
LOG_FILE = "agentcat.log"
LOG_PROBLEM = re.compile(r"\b(?:Warning|WARN|Error|ERROR|Failed|failed)\b")
CAPTURE_OFF = "AgentCat HTTP request capture is off"


class Failures:
    def __init__(self):
        self.items = []

    def check(self, condition, message):
        if not condition:
            self.items.append(message)
        return condition

    def equal(self, actual, expected, what):
        return self.check(actual == expected, f"{what}: expected {expected!r}, got {actual!r}")


class FakeApi:
    """Records every request and answers 202, like the AgentCat events API.

    Like that API, it speaks HTTP/1.1 and keeps each connection open for the next request. The HTTP/1.0 default of
    BaseHTTPRequestHandler closes the connection after each response, but .NET keeps such a connection in its pool:
    a POST sent on it just after the close fails with "The response ended prematurely", and .NET does not retry a
    request that has a body.
    """

    def __init__(self):
        self.requests = []
        self.lock = threading.Lock()
        owner = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length) if length else b""
                with owner.lock:
                    owner.requests.append({"path": self.path, "headers": dict(self.headers.items()), "body": body})
                self.send_response(202)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def events(self):
        with self.lock:
            requests = list(self.requests)
        result = []
        for request in requests:
            try:
                result.append(json.loads(request["body"]))
            except ValueError:
                result.append(None)
        return requests, result

    def wait_for_tool_calls(self, count):
        deadline = time.monotonic() + EVENT_TIMEOUT
        while time.monotonic() < deadline:
            _, events = self.events()
            if sum(1 for e in events if isinstance(e, dict) and e.get("event_type") == TOOL_CALL) >= count:
                return
            time.sleep(0.1)

    def close(self):
        self.server.shutdown()


def environment(api, home):
    env = dict(os.environ)
    env.update({
        "HOME": str(home),
        "AGENTCAT_API_URL": api.url,
        "AGENTCAT_DISABLE_DIAGNOSTICS": "true",
        "DOTNET_NOLOGO": "1",
    })
    for name in ("AGENTCAT_ENABLED", "MCPCAT_API_URL", "AGENTCAT_PROJECT_ID"):
        env.pop(name, None)
    return env


def wait_exit(process):
    try:
        return process.wait(timeout=EXIT_TIMEOUT)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        return None


def check_requests(failures, requests, version):
    user_agent = f"agentcat-csharp/{version} dotnet/"
    for request in requests:
        headers = {k.lower(): v for k, v in request["headers"].items()}
        failures.equal(request["path"], EVENTS_PATH, "request path")
        failures.check(headers.get("content-type", "").startswith("application/json"),
                       f"Content-Type: {headers.get('content-type')!r}")
        failures.check("content-length" in headers and "transfer-encoding" not in headers,
                       f"the body must be sent with Content-Length, not chunked: {sorted(headers)}")
        failures.check(headers.get("user-agent", "").startswith(user_agent),
                       f"User-Agent {headers.get('user-agent')!r} does not start with {user_agent!r}")


def check_common(failures, event, project, version, tool):
    failures.check(str(event.get("id")).startswith("evt_"), f"{tool}: id {event.get('id')!r} does not start with evt_")
    failures.check(TIMESTAMP.match(str(event.get("timestamp"))), f"{tool}: timestamp {event.get('timestamp')!r}")
    failures.equal(event.get("project_id"), project, f"{tool}: project_id")
    failures.equal(event.get("sdk_language"), "C#", f"{tool}: sdk_language")
    failures.equal(event.get("agentcat_version"), version, f"{tool}: agentcat_version")
    failures.check(isinstance(event.get("session_id"), str) and event["session_id"], f"{tool}: no session_id")
    parameters = event.get("parameters")
    if failures.check(isinstance(parameters, dict), f"{tool}: parameters is not an object: {parameters!r}"):
        request = parameters.get("request")
        if failures.check(isinstance(request, dict), f"{tool}: parameters.request is not an object: {request!r}"):
            failures.equal((request.get("params") or {}).get("name"), tool, f"{tool}: parameters.request.params.name")
        failures.check(isinstance(parameters.get("extra"), dict), f"{tool}: parameters.extra is not an object")


def text_of(response):
    content = response.get("content") if isinstance(response, dict) else None
    if not isinstance(content, list):
        return None
    return [block.get("text") for block in content if isinstance(block, dict) and block.get("type") == "text"]


def find_tool_call(failures, events, tool):
    matches = [e for e in events if isinstance(e, dict) and e.get("event_type") == TOOL_CALL and e.get("resource_name") == tool]
    failures.equal(len(matches), 1, f"number of {TOOL_CALL} events for {tool}")
    return matches[0] if matches else None


class StdoutReader:
    def __init__(self, stream):
        self.messages = queue.Queue()
        self.lines = []
        self.thread = threading.Thread(target=self._read, args=(stream,), daemon=True)
        self.thread.start()

    def _read(self, stream):
        for raw in stream:
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            self.lines.append(line)
            try:
                self.messages.put(json.loads(line))
            except ValueError:
                pass

    def wait_for(self, ids):
        responses = {}
        deadline = time.monotonic() + RESPONSE_TIMEOUT
        while set(ids) - set(responses):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            try:
                message = self.messages.get(timeout=remaining)
            except queue.Empty:
                break
            if isinstance(message, dict) and message.get("id") in ids:
                responses[message["id"]] = message
        return responses


def run_stdio(tfm, executable, version, api, home, failures, processes):
    with open(home / "stderr.txt", "wb") as stderr:
        process = subprocess.Popen([executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr,
                                   env=environment(api, home), cwd=home)
    processes.append(process)
    reader = StdoutReader(process.stdout)
    frames = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "aot-driver", "version": "1.0.0"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
        {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "echo", "arguments": {"text": "hello aot"}}},
        {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "boom", "arguments": {}}},
    ]
    try:
        for frame in frames:
            process.stdin.write((json.dumps(frame) + "\n").encode())
            process.stdin.flush()
    except BrokenPipeError:
        failures.check(False, "the app closed its stdin before the requests were written")

    responses = reader.wait_for([1, 2, 3, 4])
    failures.equal(sorted(responses), [1, 2, 3, 4], "JSON-RPC responses received")
    api.wait_for_tool_calls(2)
    try:
        process.stdin.close()
    except BrokenPipeError:
        pass
    code = wait_exit(process)
    failures.equal(code, 0, "exit code after stdin closed")
    reader.thread.join(timeout=5)
    failures.check(not reader.thread.is_alive(), "stdout was still open 5 s after the app exited")

    for line in reader.lines:
        failures.check(line.startswith("{") and '"jsonrpc"' in line, f"stdout carries a line that is not an MCP frame: {line!r}")

    tools = [t.get("name") for t in ((responses.get(2) or {}).get("result") or {}).get("tools", [])]
    # get_more_tools is the tool AgentCat adds to the listing.
    failures.equal(sorted(tools), ["boom", "echo", "get_more_tools"], "tools/list")
    echo_text = text_of((responses.get(3) or {}).get("result"))
    failures.check(echo_text is not None and "echo: hello aot" in echo_text, f"echo result: {echo_text!r}")
    boom_result = (responses.get(4) or {}).get("result") or {}
    failures.equal(boom_result.get("isError"), True, "boom result isError")

    requests, events = api.events()
    check_requests(failures, requests, version)

    # No ASP.NET Core in a stdio app, so no HTTP capture.
    for event in events:
        extra = (event.get("parameters") or {}).get("extra") if isinstance(event, dict) else None
        failures.check(not isinstance(extra, dict) or "requestInfo" not in extra,
                       f"{(event or {}).get('resource_name')}: parameters.extra has requestInfo: {extra!r}")

    echo = find_tool_call(failures, events, "echo")
    if echo is not None:
        check_common(failures, echo, STDIO_PROJECT, version, "echo")
        failures.check(echo.get("is_error") is not True, f"echo: is_error is {echo.get('is_error')!r}")
        arguments = ((echo.get("parameters") or {}).get("request") or {}).get("params", {}).get("arguments")
        failures.equal((arguments or {}).get("text"), "hello aot", "echo: parameters.request.params.arguments.text")
        failures.equal(text_of(echo.get("response")), ["echo: hello aot"], "echo: response.content texts")
        failures.equal(echo.get("identify_actor_given_id"), "aot-user", "echo: identify_actor_given_id")
        failures.equal(echo.get("identify_actor_name"), "AOT User", "echo: identify_actor_name")
        failures.equal(echo.get("identify_data"), {"plan": "aot"}, "echo: identify_data")
        failures.equal((echo.get("tags") or {}).get("lane"), "aot", "echo: tags.lane")
        failures.equal(echo.get("properties"), {"tool": "echo"}, "echo: properties")

    boom = find_tool_call(failures, events, "boom")
    if boom is not None:
        check_common(failures, boom, STDIO_PROJECT, version, "boom")
        failures.equal(boom.get("is_error"), True, "boom: is_error")
        error = boom.get("error")
        if failures.check(isinstance(error, dict), f"boom: error is not an object: {error!r}"):
            failures.equal(error.get("type"), "System.InvalidOperationException", "boom: error.type")
            failures.equal(error.get("message"), BOOM_MESSAGE, "boom: error.message")
            frames = error.get("frames")
            if tfm in FRAMELESS_FRAMEWORKS:
                failures.check(frames is None, f"boom: error.frames on {tfm} Native AOT: {frames!r}")
                stack = error.get("stack") or ""
                for method in BOOM_STACK_METHODS:
                    failures.check(method in stack, f"boom: error.stack does not name {method}: {stack!r}")
            elif failures.check(isinstance(frames, list) and frames, f"boom: error.frames is empty or missing: {frames!r}"):
                in_app = [(f.get("function"), f.get("module")) for f in frames if isinstance(f, dict) and f.get("in_app")]
                failures.equal(in_app, BOOM_FRAMES, "boom: in-app frames (function, module), innermost first")
        failures.equal(text_of(boom.get("response")), ["An error occurred invoking 'boom'."], "boom: response.content texts")


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_listening(process, port):
    deadline = time.monotonic() + RESPONSE_TIMEOUT
    while time.monotonic() < deadline and process.poll() is None:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=1):
                return True
        except OSError:
            time.sleep(0.2)
    return False


def rpc_result(body):
    """The JSON-RPC message of a JSON or server-sent-events response body."""
    text = body.decode("utf-8", "replace").strip()
    if text.startswith("{"):
        return json.loads(text)
    for line in text.splitlines():
        if line.startswith("data:"):
            return json.loads(line[len("data:"):].strip())
    return None


def run_http(tfm, executable, version, api, home, failures, processes):
    port = free_port()
    env = environment(api, home)
    env["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    with open(home / "stdout.txt", "wb") as stdout, open(home / "stderr.txt", "wb") as stderr:
        process = subprocess.Popen([executable], stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, env=env, cwd=home)
    processes.append(process)
    url = f"http://127.0.0.1:{port}/{QUERY}"
    if failures.check(wait_listening(process, port), f"the app did not listen on port {port}"):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                           "params": {"name": "echo", "arguments": {"text": "hello http"}}}).encode()
        request = urllib.request.Request(url, data=body, method="POST", headers={
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": PROTOCOL_VERSION,
            "X-AgentCat-Sentinel": SENTINEL_VALUE,
        })
        try:
            with urllib.request.urlopen(request, timeout=RESPONSE_TIMEOUT) as response:
                message = rpc_result(response.read())
            echo_text = text_of((message or {}).get("result"))
            failures.check(echo_text is not None and "echo: hello http" in echo_text, f"echo result: {message!r}")
        except (OSError, ValueError) as error:
            failures.check(False, f"tools/call POST failed: {error!r}")
        api.wait_for_tool_calls(1)

    if process.poll() is None:
        process.send_signal(signal.SIGTERM)
    code = wait_exit(process)
    failures.equal(code, 0, "exit code after SIGTERM")

    requests, events = api.events()
    check_requests(failures, requests, version)
    echo = find_tool_call(failures, events, "echo")
    if echo is not None:
        check_common(failures, echo, HTTP_PROJECT, version, "echo")
        failures.equal(text_of(echo.get("response")), ["echo: hello http"], "echo: response.content texts")
        info = ((echo.get("parameters") or {}).get("extra") or {}).get("requestInfo")
        if failures.check(isinstance(info, dict), f"echo: parameters.extra.requestInfo is not an object: {info!r}"):
            failures.equal(info.get("url"), url, "echo: requestInfo.url")
            headers = info.get("headers")
            if failures.check(isinstance(headers, dict), f"echo: requestInfo.headers is not an object: {headers!r}"):
                failures.equal(headers.get(SENTINEL_HEADER), SENTINEL_VALUE, f"echo: requestInfo.headers[{SENTINEL_HEADER!r}]")
                names = list(headers)
                failures.equal(names, sorted(n.lower() for n in names), "echo: header names lower-case and sorted")


def check_log(failures, home, events):
    """The AgentCat log of a clean run: a sent line for every event, and no warning, error or failure line."""
    path = home / LOG_FILE
    if not failures.check(path.exists(), f"no {LOG_FILE} in the app's HOME"):
        return
    lines = path.read_text(errors="replace").splitlines()
    for event in events:
        if isinstance(event, dict) and event.get("id"):
            sent = f"Successfully sent event {event['id']} "
            failures.check(any(sent in line for line in lines), f"{LOG_FILE} has no '{sent.strip()}' line")
    for line in lines:
        failures.check(CAPTURE_OFF not in line, f"{LOG_FILE}: {line}")
        failures.check(CAPTURE_OFF in line or not LOG_PROBLEM.search(line), f"{LOG_FILE}: {line}")


def main():
    if len(sys.argv) != 5 or sys.argv[1] not in ("stdio", "http"):
        sys.exit(__doc__)
    app, tfm, executable, version = sys.argv[1], sys.argv[2], str(Path(sys.argv[3]).resolve()), sys.argv[4]
    failures = Failures()
    api = FakeApi()
    processes = []
    with tempfile.TemporaryDirectory(prefix=f"agentcat-aot-{app}-") as folder:
        home = Path(folder)
        try:
            (run_stdio if app == "stdio" else run_http)(tfm, executable, version, api, home, failures, processes)
            check_log(failures, home, api.events()[1])
        except Exception:
            failures.check(False, "the driver stopped on an exception:\n" + traceback.format_exc())
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait()
            api.close()

        _, events = api.events()
        print(f"{'FAILED' if failures.items else 'OK'}: {app} app on {tfm}, {len(events)} event(s)")
        for item in failures.items:
            print(f"  - {item}")
        print("Events received:")
        print(json.dumps(events, indent=2))
        names = (LOG_FILE, "stderr.txt", "stdout.txt") if failures.items else (LOG_FILE,)
        for name in names:
            path = home / name
            if path.exists():
                print(f"--- {name} ---")
                print(path.read_text(errors="replace"))
        if failures.items:
            sys.exit(1)


if __name__ == "__main__":
    main()
