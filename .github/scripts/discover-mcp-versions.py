#!/usr/bin/env python3
"""Chooses the ModelContextProtocol versions that the mcp-versions CI job tests.

The base is the ModelContextProtocol PackageVersion in Directory.Packages.props.
The list is the base plus the newest patch of every major.minor at or above it,
taken from the stable X.Y.Z versions in the NuGet flat container. Every chosen
version must also exist for ModelContextProtocol.AspNetCore, because the HTTP
end-to-end lanes use it.

Prints the list, writes `versions=<JSON array>` to $GITHUB_OUTPUT and a table to
$GITHUB_STEP_SUMMARY when those are set. Exits with 1 and an error message when
the base, the NuGet data or an AspNetCore version is missing or unreadable.
"""

import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

PACKAGE = "ModelContextProtocol"
HTTP_PACKAGE = "ModelContextProtocol.AspNetCore"
FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer/{id}/index.json"
PROPS = Path(__file__).resolve().parents[2] / "Directory.Packages.props"
STABLE = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
ATTEMPTS = 4
TIMEOUT_SECONDS = 30


class DiscoveryError(Exception):
    pass


def stable_key(version):
    match = STABLE.match(version)
    return tuple(int(part) for part in match.groups()) if match else None


def local_name(tag):
    return tag.rsplit("}", 1)[-1]


def read_base(props_path):
    try:
        root = ET.parse(props_path).getroot()
    except (OSError, ET.ParseError) as error:
        raise DiscoveryError(f"Cannot read {props_path}: {error}") from error
    entries = [
        element
        for element in root.iter()
        if local_name(element.tag) == "PackageVersion"
        and (element.get("Include") or "").strip().lower() == PACKAGE.lower()
    ]
    if len(entries) != 1:
        raise DiscoveryError(
            f"Expected exactly one PackageVersion for {PACKAGE} in {props_path}, found {len(entries)}."
        )
    version = (entries[0].get("Version") or "").strip()
    if stable_key(version) is None:
        raise DiscoveryError(
            f"The {PACKAGE} PackageVersion in {props_path} is '{version}'. The base must be a stable X.Y.Z version."
        )
    return version


def fetch_versions(package_id):
    url = FLAT_CONTAINER.format(id=package_id.lower())
    for attempt in range(1, ATTEMPTS + 1):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "agentcat-csharp-sdk-ci"})
            with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:
                payload = json.load(response)
            break
        except urllib.error.HTTPError as error:
            retryable = error.code == 429 or error.code >= 500
            if not retryable or attempt == ATTEMPTS:
                raise DiscoveryError(f"GET {url} failed: HTTP {error.code} {error.reason}") from error
            reason = f"HTTP {error.code}"
        except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError) as error:
            if attempt == ATTEMPTS:
                raise DiscoveryError(f"GET {url} failed after {ATTEMPTS} attempts: {error}") from error
            reason = str(error)
        delay = 5 * attempt
        print(f"GET {url} failed ({reason}); retrying in {delay}s", file=sys.stderr)
        time.sleep(delay)

    versions = payload.get("versions") if isinstance(payload, dict) else None
    if not isinstance(versions, list) or not versions or not all(isinstance(v, str) for v in versions):
        raise DiscoveryError(f"GET {url} returned no readable 'versions' list.")
    return versions


def choose(base, published):
    base_key = stable_key(base)
    stable = {}
    for version in published:
        key = stable_key(version)
        if key is not None:
            stable[key] = version
    if base_key not in stable:
        raise DiscoveryError(
            f"The base version {PACKAGE} {base} from Directory.Packages.props is not a published stable version on NuGet."
        )
    newest_per_minor = {}
    for key in stable:
        if key >= base_key and key > newest_per_minor.get(key[:2], (-1, -1, -1)):
            newest_per_minor[key[:2]] = key
    chosen = {base_key, *newest_per_minor.values()}
    return [stable[key] for key in sorted(chosen)], [stable[key] for key in sorted(stable) if key >= base_key]


def require_http_package(chosen, http_published):
    available = set(http_published)
    missing = [version for version in chosen if version not in available]
    if missing:
        names = ", ".join(missing)
        raise DiscoveryError(
            f"NuGet has no {HTTP_PACKAGE} {names}, so the HTTP end-to-end lanes cannot run on "
            f"{PACKAGE} {names}. Every version under test needs both packages."
        )


def append(path_variable, text):
    path = os.environ.get(path_variable)
    if path:
        with open(path, "a", encoding="utf-8") as file:
            file.write(text)


def summary_table(base, chosen):
    lines = [
        f"### {PACKAGE} versions under test",
        "",
        f"Base: `{base}` (the `{PACKAGE}` PackageVersion in `Directory.Packages.props`)",
        "",
        "| Version | Reason |",
        "| --- | --- |",
    ]
    newest = {}
    for version in chosen:
        newest[stable_key(version)[:2]] = version
    for version in chosen:
        key = stable_key(version)
        reasons = []
        if version == base:
            reasons.append("base")
        if newest[key[:2]] == version:
            reasons.append(f"newest {key[0]}.{key[1]}.x")
        lines.append(f"| `{version}` | {', '.join(reasons)} |")
    return "\n".join(lines) + "\n"


def main():
    base = read_base(PROPS)
    published = fetch_versions(PACKAGE)
    chosen, considered = choose(base, published)
    if not chosen:
        raise DiscoveryError("No versions to test.")
    require_http_package(chosen, fetch_versions(HTTP_PACKAGE))

    matrix = json.dumps(chosen, separators=(",", ":"))
    print(f"Base {PACKAGE} version (Directory.Packages.props): {base}")
    print(f"Stable {PACKAGE} versions at or above the base: {' '.join(considered)}")
    print(f"Versions to test: {matrix}")
    append("GITHUB_OUTPUT", f"versions={matrix}\n")
    append("GITHUB_STEP_SUMMARY", summary_table(base, chosen))


if __name__ == "__main__":
    try:
        main()
    except DiscoveryError as error:
        print(f"::error title=MCP version discovery failed::{error}")
        append("GITHUB_STEP_SUMMARY", f"### {PACKAGE} version discovery failed\n\n{error}\n")
        sys.exit(1)
