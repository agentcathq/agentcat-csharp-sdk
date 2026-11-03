"""Tests of release.py: the prepare, status and notes steps of release.yml."""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
import release  # noqa: E402

REPOSITORY = release.REPOSITORY

CHANGELOG = f"""# Changelog

Intro.

## [Unreleased]

### Added

- Thing two.

## [1.0.0] - 2026-01-02

### Added

- Thing one.

[Unreleased]: {REPOSITORY}/compare/v1.0.0...HEAD
[1.0.0]: {REPOSITORY}/releases/tag/v1.0.0
"""
DATED = f"""# Changelog

Intro.

## [Unreleased]

## [1.1.0] - 2026-11-03

### Added

- Thing two.

## [1.0.0] - 2026-01-02

### Added

- Thing one.

[Unreleased]: {REPOSITORY}/compare/v1.1.0...HEAD
[1.1.0]: {REPOSITORY}/compare/v1.0.0...v1.1.0
[1.0.0]: {REPOSITORY}/releases/tag/v1.0.0
"""
FIRST = f"""# Changelog

## [Unreleased]

First release.

### Added

- Thing one.

[Unreleased]: {REPOSITORY}/commits/main
"""
PROPS = """<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <VersionPrefix>1.0.0</VersionPrefix>
    <AssemblyVersion>$([System.Version]::Parse('$(VersionPrefix)').Major).0.0.0</AssemblyVersion>
  </PropertyGroup>
</Project>
"""
PACKAGES = """<Project>
  <ItemGroup>
    <PackageVersion Include="ModelContextProtocol" Version="1.0.0" />
    <PackageVersion Include="AgentCat.Api" Version="{api}" />
  </ItemGroup>
</Project>
"""
UNSHIPPED = "#nullable enable\nB.Two() -> void\nA.One() -> void\n*REMOVED*C.Old() -> void\n"
SHIPPED = "#nullable enable\nC.Three() -> void\nC.Old() -> void\n"
EMPTY_API = "#nullable enable\n"


def v(text):
    version = release.parse_version(text)
    assert version is not None, text
    return version


class Versions(unittest.TestCase):
    def test_versions_parse_only_as_major_minor_patch(self):
        self.assertEqual(release.show(v("1.20.3")), "1.20.3")
        self.assertEqual(release.show(v("0.0.0")), "0.0.0")
        for bad in ("", "1", "1.2", "1.2.3.4", "01.2.3", "1.02.3", "1.2.03", "1.2.3-beta.1", "1.2.3+build", "v1.2.3",
                    " 1.2.3", "1.2.x", "-1.2.3"):
            self.assertIsNone(release.parse_version(bad), bad)

    def test_the_newest_tag_ignores_tags_that_are_not_versions(self):
        tags = ["v1.2.0", "v1.10.0", "v1.9.9", "v2.0.0-beta.1", "1.11.0", "attempt-1"]
        self.assertEqual(release.newest_tag(tags), v("1.10.0"))
        self.assertIsNone(release.newest_tag(["v1.0.0-beta.1", "attempt-1"]))

    def test_words_count_from_the_newest_tag(self):
        current, newest = v("1.2.3"), v("1.2.3")
        self.assertEqual(release.next_version("patch", current, newest), v("1.2.4"))
        self.assertEqual(release.next_version("minor", current, newest), v("1.3.0"))
        self.assertEqual(release.next_version("major", current, newest), v("2.0.0"))
        self.assertEqual(release.next_version("1.5.0", current, newest), v("1.5.0"))

    def test_an_exact_version_must_be_newer_than_the_newest_tag(self):
        with self.assertRaisesRegex(release.ReleaseError, r"^1\.2\.3 is not newer than the newest tag, v1\.2\.3$"):
            release.next_version("1.2.3", v("1.2.3"), v("1.2.3"))
        with self.assertRaises(release.ReleaseError):
            release.next_version("1.0.9", v("1.2.3"), v("1.2.3"))
        with self.assertRaisesRegex(release.ReleaseError, r"'1\.3' is not patch, minor, major or a version X\.Y\.Z"):
            release.next_version("1.3", v("1.2.3"), v("1.2.3"))

    def test_the_first_release_is_the_props_version(self):
        for word in ("patch", "minor", "major", "1.0.0"):
            self.assertEqual(release.next_version(word, v("1.0.0"), None), v("1.0.0"), word)
        with self.assertRaisesRegex(release.ReleaseError, r"The first release is the version of Directory\.Build\.props, "
                                                          r"1\.0\.0: set it there to release 1\.1\.0"):
            release.next_version("1.1.0", v("1.0.0"), None)
        with self.assertRaises(release.ReleaseError):
            release.next_version("next", v("1.0.0"), None)


class Props(unittest.TestCase):
    def test_the_version_is_the_version_prefix(self):
        self.assertEqual(release.props_version(PROPS), v("1.0.0"))
        conditioned = PROPS.replace("</PropertyGroup>", "<VersionSuffix Condition=\"'$(Ci)' == ''\">dev</VersionSuffix>"
                                                        "</PropertyGroup>")
        self.assertEqual(release.props_version(conditioned), v("1.0.0"))

    def test_a_version_suffix_or_a_bad_prefix_is_not_a_release_version(self):
        suffixed = PROPS.replace("</VersionPrefix>", "</VersionPrefix>\n    <VersionSuffix>beta.1</VersionSuffix>")
        with self.assertRaisesRegex(release.ReleaseError, "sets the VersionSuffix 'beta.1': a release has none"):
            release.props_version(suffixed)
        self.assertEqual(release.props_version(suffixed.replace("beta.1", "")), v("1.0.0"))
        with self.assertRaisesRegex(release.ReleaseError, "is not a version X.Y.Z: '1.0'"):
            release.props_version(PROPS.replace("1.0.0", "1.0"))
        with self.assertRaisesRegex(release.ReleaseError, "is not a version X.Y.Z: ''"):
            release.props_version("<Project />")

    def test_setting_the_version_changes_only_the_version_prefix_line(self):
        self.assertEqual(release.set_props_version(PROPS, v("1.0.1")), PROPS.replace(
            "<VersionPrefix>1.0.0</VersionPrefix>", "<VersionPrefix>1.0.1</VersionPrefix>"))
        with self.assertRaisesRegex(release.ReleaseError, "has 0 <VersionPrefix> lines, not one"):
            release.set_props_version("<Project />", v("1.0.1"))


class Changelog(unittest.TestCase):
    def test_dating_moves_the_entries_under_the_version_and_links_both_sections(self):
        self.assertEqual(release.date_changelog(CHANGELOG, v("1.1.0"), "2026-11-03", v("1.0.0")), DATED)
        self.assertEqual(release.dated_section(DATED, v("1.1.0")), ("2026-11-03", "### Added\n\n- Thing two."))
        self.assertEqual(release.dated_section(DATED, v("1.0.0")), ("2026-01-02", "### Added\n\n- Thing one."))

    def test_the_first_release_links_its_tag(self):
        dated = release.date_changelog(FIRST, v("1.0.0"), "2026-11-03", None)
        self.assertEqual(dated, f"""# Changelog

## [Unreleased]

## [1.0.0] - 2026-11-03

First release.

### Added

- Thing one.

[Unreleased]: {REPOSITORY}/compare/v1.0.0...HEAD
[1.0.0]: {REPOSITORY}/releases/tag/v1.0.0
""")
        self.assertEqual(release.dated_section(dated, v("1.0.0")),
                         ("2026-11-03", "First release.\n\n### Added\n\n- Thing one."))

    def test_dating_refuses_an_empty_section_a_bad_date_and_a_missing_section_or_link(self):
        with self.assertRaisesRegex(release.ReleaseError, r"The ## \[Unreleased\] section of CHANGELOG.md has no entry"):
            release.date_changelog(DATED, v("1.2.0"), "2026-11-03", v("1.1.0"))
        for date in ("2026-11-3", "2026-13-01", "2026-02-30", "03-11-2026"):
            with self.assertRaisesRegex(release.ReleaseError, "is not a date YYYY-MM-DD"):
                release.date_changelog(CHANGELOG, v("1.1.0"), date, v("1.0.0"))
        with self.assertRaisesRegex(release.ReleaseError, r"has no ## \[Unreleased\] section"):
            release.date_changelog("# Changelog\n", v("1.1.0"), "2026-11-03", None)
        with self.assertRaisesRegex(release.ReleaseError, r"has no \[Unreleased\] link"):
            release.date_changelog(FIRST.replace("[Unreleased]: ", "Unreleased: "), v("1.0.0"), "2026-11-03", None)

    def test_a_section_without_a_valid_date_is_not_dated(self):
        self.assertIsNone(release.dated_section(DATED.replace("2026-11-03", "YYYY-MM-DD"), v("1.1.0")))
        self.assertIsNone(release.dated_section(DATED.replace("## [1.1.0] - 2026-11-03", "## [1.1.0]"), v("1.1.0")))
        self.assertIsNone(release.dated_section(DATED, v("9.9.9")))
        self.assertIsNone(release.dated_section(DATED.replace("[1.1.0]", "[1.1.0-beta.1]"), v("1.1.0")))

    def test_the_real_changelog_can_be_dated(self):
        changelog = (SCRIPTS.parents[1] / "CHANGELOG.md").read_text(encoding="utf-8")
        self.assertEqual(changelog.count(release.UNRELEASED + "\n"), 1)
        self.assertEqual(sum(line.startswith("[Unreleased]: ") for line in changelog.split("\n")), 1)

    def test_the_real_props_hold_a_release_version(self):
        props = (SCRIPTS.parents[1] / "Directory.Build.props").read_text(encoding="utf-8")
        release.set_props_version(props, release.props_version(props))
        self.assertRegex(release.check_warnings.pinned_api_version(SCRIPTS.parents[1] / "Directory.Packages.props"),
                         release.VERSION)


class Api(unittest.TestCase):
    def test_shipping_moves_the_entries_and_drops_the_removed_ones(self):
        self.assertEqual(release.ship_api(UNSHIPPED, SHIPPED),
                         (EMPTY_API, "#nullable enable\nA.One() -> void\nB.Two() -> void\nC.Three() -> void\n"))
        self.assertEqual(release.ship_api(EMPTY_API, SHIPPED), (EMPTY_API, "#nullable enable\nC.Old() -> void\n"
                                                                           "C.Three() -> void\n"))

    def test_the_agentcat_api_whose_aot_warnings_are_allowed_cannot_ship(self):
        allowed = release.check_warnings.API_VERSION_WITH_WARNINGS
        self.assertEqual(release.check_warnings.verdict("IL2026", release.check_warnings.API_ORIGIN, "net8.0", allowed)[0],
                         True)
        self.assertEqual(release.api_pin_problem(allowed),
                         f"The release needs AgentCat.Api 0.0.5 or later: Directory.Packages.props pins {allowed}, whose "
                         f"generated converter gives Native AOT and trimmed apps IL2026 and IL3050 warnings, and "
                         f"CHANGELOG.md and README.md say that AgentCat gives none")
        self.assertIsNone(release.api_pin_problem("0.0.6"))


class Steps(unittest.TestCase):
    """The steps as release.yml runs them, in a repository with tags."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        (self.root / "src" / "AgentCat").mkdir(parents=True)
        self.changelog, self.unshipped, self.shipped, self.props, self.packages = release.paths(self.root)
        self.changelog.write_text(CHANGELOG, encoding="utf-8")
        self.unshipped.write_text(UNSHIPPED, encoding="utf-8")
        self.shipped.write_text(SHIPPED, encoding="utf-8")
        self.props.write_text(PROPS, encoding="utf-8")
        self.packages.write_text(PACKAGES.format(api="0.0.6"), encoding="utf-8")
        self.git("init", "-q")
        self.git("commit", "-q", "--allow-empty", "-m", "start")

    def tearDown(self):
        self.folder.cleanup()

    @staticmethod
    def environment(**values):
        """The environment without the GIT_* variables of a git hook, such as GIT_DIR, which point git at the outer
        repository."""
        return {**{key: value for key, value in os.environ.items() if not key.startswith("GIT_")}, **values}

    def git(self, *arguments):
        subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.com", *arguments], cwd=self.root,
                       check=True, env=self.environment(GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1"))

    def step(self, *arguments, actions=False):
        return subprocess.run([sys.executable, str(SCRIPTS / "release.py"), "--root", str(self.root), *arguments],
                              capture_output=True, text=True, env=self.environment(GITHUB_ACTIONS=str(actions).lower()))

    def assert_step(self, result, code, out, err=""):
        self.assertEqual((result.returncode, result.stdout, result.stderr), (code, out, err))

    def test_prepare_then_status_then_notes(self):
        self.git("tag", "v1.0.0")
        self.assert_step(self.step("status"), 0, "1.0.0 skip\n")

        self.assert_step(self.step("prepare", "minor", "2026-11-03"), 0, "1.1.0\n")

        self.assertEqual(self.props.read_text(encoding="utf-8"), PROPS.replace("1.0.0", "1.1.0"))
        self.assertEqual(self.changelog.read_text(encoding="utf-8"), DATED)
        self.assertEqual(self.unshipped.read_text(encoding="utf-8"), EMPTY_API)
        self.assertEqual(self.shipped.read_text(encoding="utf-8"),
                         "#nullable enable\nA.One() -> void\nB.Two() -> void\nC.Three() -> void\n")
        self.assert_step(self.step("status"), 0, "1.1.0 publish\n")
        self.assert_step(self.step("notes", "1.1.0"), 0, "### Added\n\n- Thing two.\n")

        self.git("tag", "v1.1.0")
        self.assert_step(self.step("status"), 0, "1.1.0 skip\n")

    def test_the_first_release_is_the_props_version_and_links_its_tag(self):
        self.changelog.write_text(FIRST, encoding="utf-8")
        self.git("tag", "v1.0.0-beta.1")
        self.assert_step(self.step("status"), 0, "1.0.0 skip\n")

        self.assert_step(self.step("prepare", "patch", "2026-11-03"), 0, "1.0.0\n")

        self.assertEqual(self.props.read_text(encoding="utf-8"), PROPS)
        self.assertIn(f"[1.0.0]: {REPOSITORY}/releases/tag/v1.0.0\n", self.changelog.read_text(encoding="utf-8"))
        self.assert_step(self.step("status"), 0, "1.0.0 publish\n")

    def test_only_the_release_commit_publishes(self):
        self.git("tag", "v1.0.0")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "work")
        self.assert_step(self.step("prepare", "minor", "2026-11-03"), 0, "1.1.0\n")
        self.git("commit", "-q", "-am", "Release v1.1.0")
        self.assert_step(self.step("status"), 0, "1.1.0 publish\n")

        self.changelog.write_text(DATED.replace("## [Unreleased]\n", "## [Unreleased]\n\n- Thing three.\n"),
                                  encoding="utf-8")
        self.git("commit", "-q", "-am", "Later work")

        self.assert_step(self.step("status", actions=True), 0, "1.1.0 skip\n",
                         "::warning::1.1.0 has a dated section in CHANGELOG.md but no tag v1.1.0: only the run of its "
                         "release commit publishes it, so re-run the failed jobs of that run\n")
        self.git("tag", "v1.1.0", "HEAD^1")
        self.assert_step(self.step("status"), 0, "1.1.0 skip\n")

    def test_prepare_stops_without_an_entry_and_changes_nothing(self):
        self.changelog.write_text(DATED, encoding="utf-8")
        self.git("tag", "v1.1.0")
        files = [path.read_text(encoding="utf-8") for path in (self.changelog, self.unshipped, self.shipped, self.props)]

        self.assert_step(self.step("prepare", "patch", "2026-11-04", actions=True), 1, "",
                         "::error::The ## [Unreleased] section of CHANGELOG.md has no entry: add the changes of the "
                         "release first\n")

        self.assertEqual([path.read_text(encoding="utf-8")
                          for path in (self.changelog, self.unshipped, self.shipped, self.props)], files)

    def test_prepare_stops_on_the_agentcat_api_whose_aot_warnings_are_allowed(self):
        self.packages.write_text(PACKAGES.format(api=release.check_warnings.API_VERSION_WITH_WARNINGS), encoding="utf-8")
        result = self.step("prepare", "patch", "2026-11-03")
        self.assertEqual(result.returncode, 1)
        self.assertIn("release: The release needs AgentCat.Api 0.0.5 or later", result.stderr)
        self.assertEqual(self.changelog.read_text(encoding="utf-8"), CHANGELOG)

    def test_status_fails_for_a_version_to_publish_that_cannot_ship(self):
        self.changelog.write_text(DATED, encoding="utf-8")
        self.props.write_text(PROPS.replace("1.0.0", "1.1.0"), encoding="utf-8")
        self.packages.write_text(PACKAGES.format(api=release.check_warnings.API_VERSION_WITH_WARNINGS), encoding="utf-8")

        result = self.step("status")

        self.assertEqual((result.returncode, result.stdout), (1, ""))
        self.assertIn("release: 1.1.0 cannot be published: src/AgentCat/PublicAPI.Unshipped.txt has 3 entries",
                      result.stderr)
        self.assertIn("The release needs AgentCat.Api 0.0.5 or later", result.stderr)

    def test_notes_need_a_dated_section(self):
        self.assert_step(self.step("notes", "1.0.0"), 0, "### Added\n\n- Thing one.\n")
        self.assert_step(self.step("notes", "1.1.0"), 1, "", "release: CHANGELOG.md has no dated section for 1.1.0\n")
        self.assert_step(self.step("notes", "1.1"), 1, "", "release: '1.1' is not a version X.Y.Z\n")

    def test_unknown_arguments_get_the_usage(self):
        for arguments in ((), ("check", "v1.0.0", "1.0.0"), ("prepare", "patch"), ("status", "now")):
            self.assert_step(self.step(*arguments), 2, "", release.USAGE + "\n")


if __name__ == "__main__":
    unittest.main()
