#!/usr/bin/env python3
"""The packaging probe: a project from the official MCP server template, with the packed AgentCat package added.

Usage:
  probe.py package <nupkg>                                     checks the contents of the packed package and its readme
  probe.py template <SDK version>                              prints the mcpserver template that `dotnet new` uses
  probe.py prepare <project folder>                            adds WithAgentCat to the template's Program.cs and pins the RID
  probe.py restored <project folder> <version> <packed folder> checks that the restore took the packed AgentCat
  probe.py run <executable> <version>                          runs the server against a fake API and checks the event

The fake API, the driver helpers and the event checks are the ones of the AOT job (../AgentCat.Aot/drive.py); the
readme is checked with .github/scripts/nuget_readme.py, which writes it at pack time.
"""

import difflib
import json
import os
import re
import subprocess
import sys
import tempfile
import traceback
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPOSITORY / "tests" / "AgentCat.Aot"))
sys.path.insert(0, str(REPOSITORY / ".github" / "scripts"))
import drive  # noqa: E402
import nuget_readme  # noqa: E402

PROJECT_ID = "proj_package_probe"
TOOL = "get_random_number"
RID = "linux-x64"
TOOL_CHAIN = ".WithTools<RandomNumberTools>()"
WITH_AGENTCAT = ".WithAgentCat(\"" + PROJECT_ID + "\")"


def package(nupkg):
    """The contents of the packed package that every consumer depends on, and the readme that nuget.org shows."""
    with zipfile.ZipFile(nupkg) as archive:
        names = set(archive.namelist())
        missing = [n for n in ("AgentCat.nuspec", "README.md", "lib/net8.0/AgentCat.dll", "lib/net10.0/AgentCat.dll",
                               "lib/netstandard2.0/AgentCat.dll") if n not in names]
        if missing:
            sys.exit(f"{nupkg} lacks: {', '.join(missing)}")
        nuspec = ET.fromstring(archive.read("AgentCat.nuspec"))
        readme = archive.read("README.md").decode("utf-8")
    print(f"{nupkg}: contents OK")

    named = [e.text for e in nuspec.iter() if e.tag.rsplit("}", 1)[-1] == "readme"]
    if named != ["README.md"]:
        sys.exit(f"The nuspec must name the readme README.md once, found {named!r}")
    expected = nuget_readme.convert((REPOSITORY / "README.md").read_text(encoding="utf-8"))
    if readme != expected:
        diff = difflib.unified_diff(expected.split("\n"), readme.split("\n"), "nuget_readme.py README.md", "packed README.md",
                                    lineterm="", n=1)
        sys.exit("The packed README.md is not the nuget.org copy of README.md:\n" + "\n".join(list(diff)[:40]))
    problems = nuget_readme.lint(readme)
    if problems:
        sys.exit("The packed README.md fails the nuget.org readme lint:\n"
                 + "\n".join(f"  line {number}: {reason}: {line.strip()}" for number, reason, line in problems))
    print(f"{nupkg}: README.md at the root is the nuget.org copy of README.md ({readme.count(chr(10)) + 1} lines), "
          "with no raw HTML, alert, relative link or image from a host that nuget.org does not show")


def template(sdk_version):
    """The mcpserver template that `dotnet new` resolves on this SDK (its package decides the template's
    ModelContextProtocol version), from the template engine's cache that `dotnet new list` fills."""
    home = Path(os.environ.get("DOTNET_CLI_HOME") or Path.home())
    cache = home / ".templateengine" / "dotnetcli" / sdk_version / "templatecache.json"
    try:
        templates = json.loads(cache.read_text(encoding="utf-8-sig")).get("TemplateInfo") or []
    except (OSError, ValueError) as error:
        print(f"::warning::The template package is not logged: {cache} cannot be read ({error})")
        return
    found = [t for t in templates if "mcpserver" in (t.get("ShortNameList") or [])]
    for t in found:
        print(f"mcpserver template {t.get('Identity')} from {t.get('MountPointUri')}")
    if not found:
        print(f"::warning::The template package is not logged: {cache} lists no mcpserver template")


def prepare(folder):
    folder = Path(folder)
    program = folder / "Program.cs"
    text = program.read_text()
    if text.count(TOOL_CHAIN) != 1:
        sys.exit(f"Program.cs must contain {TOOL_CHAIN} once; the template changed:\n{text}")
    text = "using AgentCat;\n" + text.replace(TOOL_CHAIN, TOOL_CHAIN + "\n    " + WITH_AGENTCAT)
    program.write_text(text)

    project = next(folder.glob("*.csproj"))
    xml = project.read_text()
    # The template lists six RIDs; restoring them downloads a runtime pack for each.
    xml, count = re.subn(r"<RuntimeIdentifiers>[^<]*</RuntimeIdentifiers>", f"<RuntimeIdentifier>{RID}</RuntimeIdentifier>", xml)
    if count != 1:
        sys.exit(f"{project.name} must have one RuntimeIdentifiers element, found {count}:\n{xml}")
    project.write_text(xml)
    print(program.read_text())


def restored(folder, version, packed):
    folder, packed = Path(folder), Path(packed).resolve()
    assets = json.loads((folder / "obj" / "project.assets.json").read_text())
    libraries = {k.split("/")[0].lower(): k.split("/")[1] for k in assets["libraries"] if "/" in k}
    if libraries.get("agentcat") != version:
        sys.exit(f"The restore resolved AgentCat {libraries.get('agentcat')!r}, not the packed {version!r}")
    metadata = json.loads((folder / "packages" / "agentcat" / version / ".nupkg.metadata").read_text())
    source = Path(metadata.get("source", "")).resolve()
    if source != packed:
        sys.exit(f"AgentCat {version} came from {metadata.get('source')!r}, not the packed folder {str(packed)!r}")
    print(f"AgentCat {version} restored from {source}")
    print(f"ModelContextProtocol {libraries.get('modelcontextprotocol')} (the template's, not the AgentCat floor)")


def run(executable, version):
    failures = drive.Failures()
    api = drive.FakeApi()
    processes = []
    with tempfile.TemporaryDirectory(prefix="agentcat-probe-") as folder:
        home = Path(folder)
        try:
            call_tool(str(Path(executable).resolve()), version, api, home, failures, processes)
            drive.check_log(failures, home, api.events()[1])
        except Exception:
            failures.check(False, "the driver stopped on an exception:\n" + traceback.format_exc())
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait()
            api.close()
        _, events = api.events()
        print(f"{'FAILED' if failures.items else 'OK'}: template server, {len(events)} event(s)")
        for item in failures.items:
            print(f"  - {item}")
        print(json.dumps(events, indent=2))
        for name in (drive.LOG_FILE, "stderr.txt") if failures.items else (drive.LOG_FILE,):
            if (home / name).exists():
                print(f"--- {name} ---")
                print((home / name).read_text(errors="replace"))
        if failures.items:
            sys.exit(1)


def call_tool(executable, version, api, home, failures, processes):
    with open(home / "stderr.txt", "wb") as stderr:
        process = subprocess.Popen([executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr,
                                   env=drive.environment(api, home), cwd=home)
    processes.append(process)
    reader = drive.StdoutReader(process.stdout)
    frames = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": drive.PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "probe-driver", "version": "1.0.0"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": TOOL, "arguments": {"min": 1, "max": 100}}},
    ]
    for frame in frames:
        process.stdin.write((json.dumps(frame) + "\n").encode())
        process.stdin.flush()
    responses = reader.wait_for([1, 2])
    failures.equal(sorted(responses), [1, 2], "JSON-RPC responses received")
    api.wait_for_tool_calls(1)
    process.stdin.close()
    failures.equal(drive.wait_exit(process), 0, "exit code after stdin closed")

    # A first call without a session_id gets AgentCat's session_id issuance text, not the tool result; the event keeps the
    # tool's own response.
    result = (responses.get(2) or {}).get("result") or {}
    failures.check(drive.text_of(result), f"{TOOL}: the call returned no text: {result!r}")

    requests, events = api.events()
    drive.check_requests(failures, requests, version)
    event = drive.find_tool_call(failures, events, TOOL)
    if event is not None:
        drive.check_common(failures, event, PROJECT_ID, version, TOOL)
        failures.check(event.get("is_error") is not True, f"{TOOL}: is_error is {event.get('is_error')!r}")
        texts = drive.text_of(event.get("response"))
        failures.check(texts is not None and len(texts) == 1 and texts[0].isdigit(), f"{TOOL}: response.content texts: {texts!r}")


if __name__ == "__main__":
    commands = {"package": (package, 1), "template": (template, 1), "prepare": (prepare, 1), "restored": (restored, 3),
                "run": (run, 2)}
    if len(sys.argv) < 2 or sys.argv[1] not in commands or len(sys.argv) - 2 != commands[sys.argv[1]][1]:
        sys.exit(__doc__)
    commands[sys.argv[1]][0](*sys.argv[2:])
