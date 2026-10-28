#!/usr/bin/env python3
"""Fails a Native AOT publish log on any trim or AOT warning that is not allowed.

Usage: check_warnings.py <publish log>

Allowed, by exact code and origin:
- IL2026 and IL3050 from Microsoft.AspNetCore.Http.Json.JsonOptions.CreateDefaultTypeResolver on net8.0: ASP.NET
  Core 8's own warnings, which every net8.0 ASP.NET Core app gets, with or without AgentCat.
- IL2026 and IL3050 from AgentCat.Api.Model.PublishEventRequestJsonConverter.Read, only while Directory.Packages.props
  pins AgentCat.Api 0.0.4: that version's generated converter reads with JsonSerializer.Deserialize<T> and no type
  metadata. AgentCat never calls Read. With any other pin these warnings fail like every other one.

Every other IL warning fails, AgentCat's own included. Prints each warning with its verdict. Two controls keep the
check from passing on a log it cannot read:
- The log must show that native code was generated, so a publish that silently stopped being Native AOT fails.
- Every line that names an IL code next to "warning" must parse; and while the 0.0.4 allowance holds, at least one of
  its lines must be seen, since that converter warns in every app that reads JSON.
"""

import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

PROPS = Path(__file__).resolve().parents[2] / "Directory.Packages.props"
API_PACKAGE = "AgentCat.Api"
API_VERSION_WITH_WARNINGS = "0.0.4"
API_ORIGIN = "AgentCat.Api.Model.PublishEventRequestJsonConverter.Read"
ASPNETCORE_ORIGIN = "Microsoft.AspNetCore.Http.Json.JsonOptions.CreateDefaultTypeResolver"
JSON_CODES = {"IL2026", "IL3050"}
NATIVE_CODE_LINE = "Generating native code"

WARNING = re.compile(r"\bwarning (IL\d{4}): ")
LOOSE_WARNING = re.compile(r"(?i)\bwarning\b.*\bIL\d{4}\b")
PROJECT_SUFFIX = re.compile(r" \[(?P<project>[^\]]*?)(?:::TargetFramework=(?P<tfm>[^\]]+))?\]$")
ANALYSIS = re.compile(r"\s*:?\s*(?:Trim|AOT|Single-file) analysis$")
TOOLS = ("ILC", "ILLink")
AGENTCAT = re.compile(r"^AgentCat\.|[/\\]AgentCat(?:\.Api)?\.dll$")


def pinned_api_version(props=PROPS):
    """The AgentCat.Api version that Directory.Packages.props pins. The release check (.github/scripts/release.py)
    reads it here too, so the allowance and the release gate follow one pin."""
    root = ET.parse(props).getroot()
    versions = [e.get("Version") for e in root.iter()
                if e.tag.rsplit("}", 1)[-1] == "PackageVersion" and e.get("Include") == API_PACKAGE]
    if len(versions) != 1:
        sys.exit(f"Expected one {API_PACKAGE} PackageVersion in {props}, found {len(versions)}")
    return versions[0]


def parse(line):
    """(code, origin, target framework, text) of an IL warning line, or None."""
    match = WARNING.search(line)
    if not match:
        return None
    location = line[:match.start()].strip()
    text = line[match.end():]
    tfm = None
    suffix = PROJECT_SUFFIX.search(text)
    if suffix:
        tfm = suffix.group("tfm")
        text = text[:suffix.start()]
    # The trim and AOT analysis of ILC and ILLink names the member that holds the problem before the message, after
    # the tool name or, when symbols have it, a source location. Other warnings name only a file.
    file = ANALYSIS.sub("", location).rstrip(" :")
    if (file in TOOLS or ANALYSIS.search(location)) and ": " in text:
        origin, text = text.split(": ", 1)
        if file not in TOOLS:
            text = f"{text} (at {file})"
    else:
        origin = file
    return match.group(1), origin, tfm, text


def member(origin):
    return origin.split("(", 1)[0]


def verdict(code, origin, tfm, api_version):
    if code in JSON_CODES and member(origin) == ASPNETCORE_ORIGIN and tfm == "net8.0":
        return True, "allowed: ASP.NET Core 8 framework warning"
    if code in JSON_CODES and member(origin) == API_ORIGIN:
        if api_version == API_VERSION_WITH_WARNINGS:
            return True, f"allowed: {API_PACKAGE} {API_VERSION_WITH_WARNINGS} generated converter"
        return False, (f"NOT allowed: Directory.Packages.props pins {API_PACKAGE} {api_version}, and the allowance "
                       f"for {API_ORIGIN} holds only for {API_VERSION_WITH_WARNINGS}")
    if AGENTCAT.search(origin):
        return False, "NOT allowed: an AgentCat warning"
    return False, "NOT allowed"


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    log = Path(sys.argv[1]).read_text(errors="replace").splitlines()
    if not any(NATIVE_CODE_LINE in line for line in log):
        sys.exit(f"The publish log has no '{NATIVE_CODE_LINE}' line: this was not a Native AOT publish.")

    api_version = pinned_api_version()
    seen = Counter()
    unparsed = []
    for line in log:
        parsed = parse(line)
        if parsed:
            code, origin, tfm, text = parsed
            seen[(code, origin, tfm, text)] += 1
        elif LOOSE_WARNING.search(line):
            unparsed.append(line)

    failed = 0
    api_lines = 0
    print(f"{API_PACKAGE} pinned at {api_version}; {sum(seen.values())} IL warning line(s)")
    for (code, origin, tfm, text), count in sorted(seen.items()):
        allowed, reason = verdict(code, origin, tfm, api_version)
        failed += 0 if allowed else count
        api_lines += count if allowed and member(origin) == API_ORIGIN else 0
        print(f"{count:>3} x {code} [{tfm or '?'}] {origin}: {text}")
        print(f"        {reason}")
    for line in unparsed:
        print(f"Unparsed warning line: {line}")
    if api_version == API_VERSION_WITH_WARNINGS and api_lines == 0:
        print(f"No {API_ORIGIN} warning was parsed, though {API_PACKAGE} {API_VERSION_WITH_WARNINGS} gives it in every "
              f"app that reads JSON: the warning lines are not in the form this check reads")
        failed += 1
    failed += len(unparsed)
    if failed:
        print(f"{failed} trim or AOT warning problem(s)")
        sys.exit(1)
    print("No trim or AOT warning outside the allowed ones")


if __name__ == "__main__":
    main()
