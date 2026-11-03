#!/usr/bin/env python3
"""The release steps of .github/workflows/release.yml, on Directory.Build.props, CHANGELOG.md, the public API files and
the v* tags of the repository. RELEASING.md has the steps.

Usage:
  release.py prepare <patch | minor | major | X.Y.Z> <YYYY-MM-DD>
      picks the next version, writes it into the VersionPrefix of Directory.Build.props, turns the [Unreleased] section
      of CHANGELOG.md into the section of that version with the date, moves the entries of PublicAPI.Unshipped.txt to
      PublicAPI.Shipped.txt, and prints the version. patch, minor and major count from the newest v* tag. With no tag
      yet, the first release is the version that Directory.Build.props holds, whatever the input word. An exact version
      must be newer than the newest tag. An [Unreleased] section with no entry stops the step: a release needs a change.
  release.py status
      prints '<version> publish' when the version of Directory.Build.props has no tag and HEAD is its release commit,
      the commit that adds its dated section to CHANGELOG.md, else '<version> skip'. A later commit gets a warning when
      the version still has no tag: only the release commit publishes. A version to publish must also have no unshipped
      public API and must not pin the AgentCat.Api whose Native AOT warnings tests/AgentCat.Aot/check_warnings.py
      allows, or the step fails.
  release.py notes <X.Y.Z>
      prints the body of the CHANGELOG.md section of that version.

--root <folder>, before the command, names another repository folder (default: this one). A failed step exits with 1
and the reason, and a usage error with 2.
"""

import datetime
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "AgentCat.Aot"))
import check_warnings  # noqa: E402

USAGE = ("usage: release.py [--root <folder>] prepare <patch | minor | major | X.Y.Z> <YYYY-MM-DD> | status | "
         "notes <X.Y.Z>")

# The repository URL that the CHANGELOG.md links point to.
REPOSITORY = "https://github.com/agentcathq/agentcat-csharp-sdk"

UNRELEASED = "## [Unreleased]"
VERSION_PART = r"(?:0|[1-9]\d*)"
VERSION = re.compile(rf"{VERSION_PART}\.{VERSION_PART}\.{VERSION_PART}")
VERSION_PREFIX = re.compile(r"^(?P<indent>[ \t]*)<VersionPrefix>[^<]*</VersionPrefix>[ \t]*$", re.MULTILINE)
LINK = re.compile(r"\[[^\]]+\]: ")


class ReleaseError(Exception):
    pass


def paths(root):
    """CHANGELOG.md, PublicAPI.Unshipped.txt, PublicAPI.Shipped.txt, Directory.Build.props, Directory.Packages.props."""
    root = Path(root)
    api = root / "src" / "AgentCat"
    return (root / "CHANGELOG.md", api / "PublicAPI.Unshipped.txt", api / "PublicAPI.Shipped.txt",
            root / "Directory.Build.props", root / "Directory.Packages.props")


def parse_version(text):
    """(major, minor, patch) of a release version MAJOR.MINOR.PATCH with no pre-release or build part, else None."""
    if not VERSION.fullmatch(text):
        return None
    return tuple(int(part) for part in text.split("."))


def show(version):
    return ".".join(str(part) for part in version)


def newest_tag(tags):
    """The newest of the v<version> tags; other tags, such as v1.0.0-beta.1, do not count."""
    versions = [version for tag in tags if tag.startswith("v") and (version := parse_version(tag[1:]))]
    return max(versions, default=None)


def next_version(word, current, newest):
    """The version of the release that word asks for: see the usage."""
    exact = parse_version(word)
    if exact is None and word not in ("patch", "minor", "major"):
        raise ReleaseError(f"'{word}' is not patch, minor, major or a version X.Y.Z")
    if newest is None:
        if exact is not None and exact != current:
            raise ReleaseError(f"The first release is the version of Directory.Build.props, {show(current)}: set it "
                               f"there to release {show(exact)}")
        return current
    major, minor, patch = newest
    following = {"patch": (major, minor, patch + 1), "minor": (major, minor + 1, 0), "major": (major + 1, 0, 0)}
    version = exact or following[word]
    if version <= newest:
        raise ReleaseError(f"{show(version)} is not newer than the newest tag, v{show(newest)}")
    return version


def props_version(props):
    """The release version of the text of Directory.Build.props: its VersionPrefix, with no VersionSuffix."""
    values = {}
    for element in ET.fromstring(props).iter():
        name = element.tag.rsplit("}", 1)[-1]
        if name in ("VersionPrefix", "VersionSuffix") and not element.get("Condition"):
            values[name] = (element.text or "").strip()
    if values.get("VersionSuffix"):
        raise ReleaseError(f"Directory.Build.props sets the VersionSuffix '{values['VersionSuffix']}': a release has "
                           f"none")
    version = parse_version(values.get("VersionPrefix", ""))
    if version is None:
        raise ReleaseError(f"The VersionPrefix of Directory.Build.props is not a version X.Y.Z: "
                           f"'{values.get('VersionPrefix', '')}'")
    return version


def set_props_version(props, version):
    """The text of Directory.Build.props with version as its VersionPrefix, and nothing else changed."""
    lines = VERSION_PREFIX.findall(props)
    if len(lines) != 1:
        raise ReleaseError(f"Directory.Build.props has {len(lines)} <VersionPrefix> lines, not one")
    return VERSION_PREFIX.sub(lambda m: f"{m.group('indent')}<VersionPrefix>{show(version)}</VersionPrefix>", props)


def date_changelog(changelog, version, date, previous):
    """The text of CHANGELOG.md with the [Unreleased] entries under '## [<version>] - <date>', an empty [Unreleased]
    section above it, and the links of both sections."""
    if not is_date(date):
        raise ReleaseError(f"'{date}' is not a date YYYY-MM-DD")
    lines = changelog.replace("\r\n", "\n").split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if UNRELEASED not in lines:
        raise ReleaseError(f"CHANGELOG.md has no {UNRELEASED} section")
    start = lines.index(UNRELEASED)
    if not any(line.startswith("- ") for line in lines[start + 1:section_end(lines, start)]):
        raise ReleaseError(f"The {UNRELEASED} section of CHANGELOG.md has no entry: add the changes of the release "
                           f"first")
    link = next((i for i, line in enumerate(lines) if line.startswith("[Unreleased]: ")), None)
    if link is None:
        raise ReleaseError("CHANGELOG.md has no [Unreleased] link")
    if previous is None:
        version_link = f"[{show(version)}]: {REPOSITORY}/releases/tag/v{show(version)}"
    else:
        version_link = f"[{show(version)}]: {REPOSITORY}/compare/v{show(previous)}...v{show(version)}"
    out = []
    for i, line in enumerate(lines):
        if i == start:
            out += [UNRELEASED, "", f"## [{show(version)}] - {date}"]
        elif i == link:
            out += [f"[Unreleased]: {REPOSITORY}/compare/v{show(version)}...HEAD", version_link]
        else:
            out.append(line)
    return "\n".join(out) + "\n"


def dated_section(changelog, version):
    """The (date, body) of the '## [<version>] - <date>' section, the body without its blank edges, or None when the
    section is missing or its date is not valid."""
    lines = changelog.replace("\r\n", "\n").split("\n")
    heading = f"## [{show(version)}] - "
    start = next((i for i, line in enumerate(lines) if line.startswith(heading)), None)
    if start is None:
        return None
    date = lines[start][len(heading):].strip()
    if not is_date(date):
        return None
    return date, "\n".join(lines[start + 1:section_end(lines, start)]).strip("\n")


def section_end(lines, start):
    """The index of the line after the section that starts at start: the next '## ' heading or the first link line."""
    return next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ") or LINK.match(lines[i])),
                len(lines))


def is_date(text):
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", text):
        return False
    try:
        datetime.date.fromisoformat(text)
    except ValueError:
        return False
    return True


def api_entries(api_file_text):
    """The API lines of a PublicAPI file, without its #nullable header and blank lines."""
    return [line for line in api_file_text.replace("\r\n", "\n").split("\n") if line.strip() and not line.startswith("#")]


def ship_api(unshipped, shipped):
    """The new texts of PublicAPI.Unshipped.txt and PublicAPI.Shipped.txt once the unshipped entries ship."""
    moving = api_entries(unshipped)
    removed = {line[len("*REMOVED*"):] for line in moving if line.startswith("*REMOVED*")}
    added = [line for line in moving if not line.startswith("*REMOVED*")]
    kept = [line for line in api_entries(shipped) if line not in removed]
    shipped_header = [line for line in shipped.replace("\r\n", "\n").split("\n") if line.startswith("#")]
    unshipped_header = [line for line in unshipped.replace("\r\n", "\n").split("\n") if line.startswith("#")]
    return ("\n".join(unshipped_header or ["#nullable enable"]) + "\n",
            "\n".join((shipped_header or ["#nullable enable"]) + sorted(set(kept + added))) + "\n")


def api_pin_problem(api_version):
    """Why the AgentCat.Api pin must not ship, or None."""
    # CHANGELOG.md and README.md say that a Native AOT or trimmed app gets no warnings from AgentCat. With this pin
    # it gets IL2026 and IL3050 from AgentCat.Api's generated converter, which the AOT job allows only for this pin.
    if api_version != check_warnings.API_VERSION_WITH_WARNINGS:
        return None
    return (f"The release needs {check_warnings.API_PACKAGE} 0.0.5 or later: Directory.Packages.props pins "
            f"{api_version}, whose generated converter gives Native AOT and trimmed apps IL2026 and IL3050 warnings, "
            f"and CHANGELOG.md and README.md say that AgentCat gives none")


def prepare(root, word, date):
    """The prepare step: see the usage. Returns the version."""
    changelog_path, unshipped_path, shipped_path, props_path, packages_path = paths(root)
    problem = api_pin_problem(check_warnings.pinned_api_version(packages_path))
    if problem:
        raise ReleaseError(problem)
    props = read(props_path)
    current = props_version(props)
    newest = newest_tag(tags(root))
    version = next_version(word, current, newest)
    changelog = date_changelog(read(changelog_path), version, date, newest)
    unshipped, shipped = ship_api(read(unshipped_path), read(shipped_path))
    if version != current:
        write(props_path, set_props_version(props, version))
    write(changelog_path, changelog)
    write(unshipped_path, unshipped)
    write(shipped_path, shipped)
    return show(version)


def status(root):
    """The status step: see the usage. Returns ('<version> publish' or '<version> skip', a warning or None)."""
    changelog_path, unshipped_path, _, props_path, packages_path = paths(root)
    version = props_version(read(props_path))
    tagged = f"v{show(version)}" in tags(root)
    if tagged or dated_section(read(changelog_path), version) is None:
        return f"{show(version)} skip", None
    previous = parent_changelog(root)
    if previous is not None and dated_section(previous, version) is not None:
        return f"{show(version)} skip", (f"{show(version)} has a dated section in CHANGELOG.md but no tag "
                                         f"v{show(version)}: only the run of its release commit publishes it, so "
                                         f"re-run the failed jobs of that run")
    problems = []
    left = api_entries(read(unshipped_path))
    if left:
        problems.append(f"src/AgentCat/PublicAPI.Unshipped.txt has {len(left)} entries: the release pull request "
                        f"ships them ('release.py prepare')")
    problem = api_pin_problem(check_warnings.pinned_api_version(packages_path))
    if problem:
        problems.append(problem)
    if problems:
        raise ReleaseError(f"{show(version)} cannot be published: " + " ".join(problems))
    return f"{show(version)} publish", None


def notes(root, text):
    """The notes step: see the usage. Returns the body of the section."""
    version = parse_version(text)
    if version is None:
        raise ReleaseError(f"'{text}' is not a version X.Y.Z")
    section = dated_section(read(paths(root)[0]), version)
    if section is None:
        raise ReleaseError(f"CHANGELOG.md has no dated section for {text}")
    return section[1]


def tags(root):
    """The tags of the repository at root."""
    return git(root, "tag", "--list").stdout.split()


def parent_changelog(root):
    """The CHANGELOG.md of the first parent of HEAD, or None when HEAD has no parent or that commit has no
    CHANGELOG.md."""
    if git(root, "rev-parse", "--verify", "--quiet", "HEAD^1", check=False).returncode != 0:
        return None
    if git(root, "cat-file", "-e", "HEAD^1:CHANGELOG.md", check=False).returncode != 0:
        return None
    return git(root, "show", "HEAD^1:CHANGELOG.md").stdout


def git(root, *arguments, check=True):
    try:
        result = subprocess.run(["git", *arguments], cwd=root, capture_output=True, text=True, encoding="utf-8")
    except OSError as error:
        raise ReleaseError(f"Cannot run git: {error}") from error
    if check and result.returncode != 0:
        raise ReleaseError(f"git {' '.join(arguments)} failed: {result.stderr.strip()}")
    return result


def read(path):
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        raise ReleaseError(f"Cannot read {path.name}: {error}") from error


def write(path, text):
    path.write_text(text, encoding="utf-8", newline="\n")


def run(arguments):
    """Runs the step that arguments name: (exit code, stdout text, stderr text)."""
    root = ROOT
    if arguments[:1] == ["--root"] and len(arguments) >= 2:
        root, arguments = Path(arguments[1]), arguments[2:]
    actions = os.environ.get("GITHUB_ACTIONS") == "true"
    try:
        match arguments:
            case ["prepare", word, date]:
                return 0, prepare(root, word, date), ""
            case ["status"]:
                result, warning = status(root)
                return 0, result, (("::warning::" if actions else "release: warning: ") + warning) if warning else ""
            case ["notes", version]:
                return 0, notes(root, version), ""
            case _:
                return 2, "", USAGE
    except ReleaseError as error:
        return 1, "", ("::error::" if actions else "release: ") + str(error)


def main(arguments):
    code, out, problem = run(arguments)
    if out:
        print(out)
    if problem:
        print(problem, file=sys.stderr)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
