#!/usr/bin/env python3
"""The checks of release.yml and the release-prep step (RELEASING.md).

Usage:
  release.py check <tag> <version>   the tag is v<version>, CHANGELOG.md has a '## [<version>] - <date>' section
                                     with a date and entries, PublicAPI.Unshipped.txt has no entries, and
                                     Directory.Packages.props does not pin the AgentCat.Api whose Native AOT warnings
                                     tests/AgentCat.Aot/check_warnings.py allows; writes version= and prerelease= to
                                     $GITHUB_OUTPUT when it is set
  release.py notes <version>         prints the entries of the CHANGELOG.md section of <version>
  release.py prepare [YYYY-MM-DD]    for the release commit: fills the date (default: today, UTC) of the CHANGELOG.md
                                     section of the version in Directory.Build.props, and moves the entries of
                                     PublicAPI.Unshipped.txt to PublicAPI.Shipped.txt

--root <folder>, before the command, names another repository folder (default: this one). Exits with 1 and the
reasons when a check fails.
"""

import argparse
import datetime
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "AgentCat.Aot"))
import check_warnings  # noqa: E402

PLACEHOLDER = "YYYY-MM-DD"
VERSION = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?")
HEADING = re.compile(r"## \[(?P<version>[^\]]+)\](?: - (?P<date>\S+))?\s*")
REFERENCE = re.compile(r"\[[^\]]+\]:\s")


class ReleaseError(Exception):
    pass


def paths(root):
    """CHANGELOG.md, PublicAPI.Unshipped.txt, PublicAPI.Shipped.txt, Directory.Build.props, Directory.Packages.props."""
    root = Path(root)
    api = root / "src" / "AgentCat"
    return (root / "CHANGELOG.md", api / "PublicAPI.Unshipped.txt", api / "PublicAPI.Shipped.txt",
            root / "Directory.Build.props", root / "Directory.Packages.props")


def section(changelog, version):
    """The (date, entries) of the section of version in the CHANGELOG text: the date is None when the heading has none.
    Raises ReleaseError when the section is missing or appears twice."""
    lines = changelog.replace("\r\n", "\n").split("\n")
    starts = [i for i, line in enumerate(lines) if (m := HEADING.fullmatch(line)) and m.group("version") == version]
    if not starts:
        raise ReleaseError(f"CHANGELOG.md has no '## [{version}] - {PLACEHOLDER}' section")
    if len(starts) > 1:
        raise ReleaseError(f"CHANGELOG.md has {len(starts)} '## [{version}]' sections")
    start = starts[0]
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    body = lines[start + 1:end]
    # The link references at the end of the file belong to no section.
    while body and (not body[-1].strip() or REFERENCE.match(body[-1])):
        body.pop()
    while body and not body[0].strip():
        body.pop(0)
    return HEADING.fullmatch(lines[start]).group("date"), "\n".join(body)


def entries(api_file_text):
    """The API lines of a PublicAPI file, without its #nullable header and blank lines."""
    return [line for line in api_file_text.replace("\r\n", "\n").split("\n") if line.strip() and not line.startswith("#")]


def check(tag, version, changelog, unshipped, api_version):
    """The reasons the tag must not publish, as a list (empty when it may). api_version is the pinned AgentCat.Api."""
    problems = []
    if not VERSION.fullmatch(version):
        problems.append(f"The package version '{version}' is not a SemVer 2 version")
    if tag != f"v{version}":
        problems.append(f"The tag {tag} does not match the package version {version} (VersionPrefix and VersionSuffix in "
                        f"Directory.Build.props): tag v{version}, or change the version")
    try:
        date, body = section(changelog, version)
    except ReleaseError as error:
        problems.append(str(error))
    else:
        if date is None or date == PLACEHOLDER:
            problems.append(f"The '## [{version}]' section of CHANGELOG.md has no date ({date or 'none'}): run "
                            f"'release.py prepare' in the release commit")
        elif not _is_date(date):
            problems.append(f"The date of the '## [{version}]' section of CHANGELOG.md is not a YYYY-MM-DD date: {date}")
        if not body.strip():
            problems.append(f"The '## [{version}]' section of CHANGELOG.md has no entries")
    left = entries(unshipped)
    if left:
        problems.append(f"src/AgentCat/PublicAPI.Unshipped.txt has {len(left)} entries: run 'release.py prepare' in "
                        f"the release commit, which moves them to PublicAPI.Shipped.txt")
    # CHANGELOG.md and README.md say that a Native AOT or trimmed app gets no warnings from AgentCat. With this pin
    # it gets IL2026 and IL3050 from AgentCat.Api's generated converter, which the AOT job allows only for this pin.
    if api_version == check_warnings.API_VERSION_WITH_WARNINGS:
        problems.append(f"The release needs {check_warnings.API_PACKAGE} 0.0.5 or later: Directory.Packages.props pins "
                        f"{api_version}, whose generated converter gives Native AOT and trimmed apps IL2026 and IL3050 "
                        f"warnings, and CHANGELOG.md and README.md say that AgentCat gives none")
    return problems


def prepare(root, date):
    """Fills the date of the version's CHANGELOG section and moves the unshipped API to shipped; returns what it did."""
    changelog_path, unshipped_path, shipped_path, props_path, _ = paths(root)
    if not _is_date(date):
        raise ReleaseError(f"Not a YYYY-MM-DD date: {date}")
    version = props_version(props_path.read_text(encoding="utf-8"))
    done = []

    changelog = changelog_path.read_text(encoding="utf-8")
    current, _ = section(changelog, version)
    if current is None or current == PLACEHOLDER:
        lines = changelog.split("\n")
        for i, line in enumerate(lines):
            if (m := HEADING.fullmatch(line)) and m.group("version") == version:
                lines[i] = f"## [{version}] - {date}"
        changelog_path.write_text("\n".join(lines), encoding="utf-8", newline="")
        done.append(f"CHANGELOG.md: '## [{version}] - {date}'")
    else:
        done.append(f"CHANGELOG.md: '## [{version}]' is already dated {current}")

    unshipped = unshipped_path.read_text(encoding="utf-8")
    shipped = shipped_path.read_text(encoding="utf-8")
    moving = entries(unshipped)
    removed = {line[len("*REMOVED*"):] for line in moving if line.startswith("*REMOVED*")}
    added = [line for line in moving if not line.startswith("*REMOVED*")]
    kept = [line for line in entries(shipped) if line not in removed]
    header = [line for line in shipped.replace("\r\n", "\n").split("\n") if line.startswith("#")] or ["#nullable enable"]
    shipped_path.write_text("\n".join(header + sorted(set(kept + added))) + "\n", encoding="utf-8",
                            newline="\n")
    unshipped_header = [line for line in unshipped.replace("\r\n", "\n").split("\n") if line.startswith("#")]
    unshipped_path.write_text("\n".join(unshipped_header or ["#nullable enable"]) + "\n", encoding="utf-8", newline="\n")
    done.append(f"PublicAPI: {len(added)} entries shipped, {len(removed)} removed")
    return version, done


def props_version(props):
    """VersionPrefix-VersionSuffix from the text of Directory.Build.props."""
    values = {}
    for element in ET.fromstring(props).iter():
        name = element.tag.rsplit("}", 1)[-1]
        if name in ("VersionPrefix", "VersionSuffix") and not element.get("Condition"):
            values[name] = (element.text or "").strip()
    if not values.get("VersionPrefix"):
        raise ReleaseError("Directory.Build.props has no VersionPrefix")
    suffix = values.get("VersionSuffix")
    return values["VersionPrefix"] + (f"-{suffix}" if suffix else "")


def _is_date(text):
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", text):
        return False
    try:
        datetime.date.fromisoformat(text)
    except ValueError:
        return False
    return True


def _error(message):
    prefix = "::error::" if os.environ.get("GITHUB_ACTIONS") == "true" else "error: "
    print(prefix + message, file=sys.stdout if prefix == "::error::" else sys.stderr)


def main(arguments):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=str(ROOT))
    commands = parser.add_subparsers(dest="command", required=True)
    command = commands.add_parser("check")
    command.add_argument("tag")
    command.add_argument("version")
    command = commands.add_parser("notes")
    command.add_argument("version")
    command = commands.add_parser("prepare")
    command.add_argument("date", nargs="?", default=datetime.datetime.now(datetime.timezone.utc).date().isoformat())
    options = parser.parse_args(arguments)
    changelog_path, unshipped_path, _, _, packages_path = paths(options.root)

    try:
        if options.command == "check":
            version = options.version.strip()
            problems = check(options.tag, version, changelog_path.read_text(encoding="utf-8"),
                             unshipped_path.read_text(encoding="utf-8"), check_warnings.pinned_api_version(packages_path))
            for problem in problems:
                _error(problem)
            if problems:
                return 1
            outputs = f"version={version}\nprerelease={'true' if '-' in version else 'false'}\n"
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                    output.write(outputs)
            print(f"{options.tag}: version {version}, CHANGELOG.md section dated, no unshipped public API, "
                  f"{check_warnings.API_PACKAGE} past {check_warnings.API_VERSION_WITH_WARNINGS}")
        elif options.command == "notes":
            _, body = section(changelog_path.read_text(encoding="utf-8"), options.version)
            print(body)
        else:
            version, done = prepare(options.root, options.date)
            print(f"Release prep for {version}:")
            for line in done:
                print(f"  {line}")
    except ReleaseError as error:
        _error(str(error))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
