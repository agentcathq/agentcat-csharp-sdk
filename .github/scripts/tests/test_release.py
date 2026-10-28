"""Tests of release.py: the tag check of release.yml, the release notes and the release-prep step."""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
import release  # noqa: E402

CHANGELOG = """# Changelog

## [Unreleased]

## [1.1.0-preview.2] - {date}

First line.

### Added

- Thing two.

## [1.0.0] - 2026-01-02

### Added

- Thing one.

[Unreleased]: https://github.com/o/r/compare/v1.1.0-preview.2...HEAD
[1.1.0-preview.2]: https://github.com/o/r/releases/tag/v1.1.0-preview.2
[1.0.0]: https://github.com/o/r/releases/tag/v1.0.0
"""
DATED = CHANGELOG.format(date="2026-10-24")
UNDATED = CHANGELOG.format(date="YYYY-MM-DD")
SHIPPED_NOTHING = "#nullable enable\n"
UNSHIPPED = "#nullable enable\nB.Two() -> void\nA.One() -> void\n"
API_OK = "0.0.5"
PACKAGES = """<Project>
  <ItemGroup>
    <PackageVersion Include="ModelContextProtocol" Version="1.0.0" />
    <PackageVersion Include="AgentCat.Api" Version="{api}" />
  </ItemGroup>
</Project>
"""
PROPS = """<Project>
  <PropertyGroup>
    <VersionPrefix>1.1.0</VersionPrefix>
    <VersionSuffix>preview.2</VersionSuffix>
  </PropertyGroup>
</Project>
"""


class Check(unittest.TestCase):
    def test_a_matching_tag_passes(self):
        self.assertEqual(release.check("v1.1.0-preview.2", "1.1.0-preview.2", DATED, SHIPPED_NOTHING, API_OK), [])
        self.assertEqual(release.check("v1.0.0", "1.0.0", DATED, SHIPPED_NOTHING, API_OK), [])

    def test_a_mismatched_tag_fails(self):
        problems = release.check("v1.1.0", "1.1.0-preview.2", DATED, SHIPPED_NOTHING, API_OK)
        self.assertEqual(len(problems), 1)
        self.assertIn("The tag v1.1.0 does not match the package version 1.1.0-preview.2", problems[0])
        self.assertEqual(len(release.check("1.1.0-preview.2", "1.1.0-preview.2", DATED, SHIPPED_NOTHING, API_OK)), 1)
        self.assertEqual(len(release.check("v1.1.0-preview.20", "1.1.0-preview.2", DATED, SHIPPED_NOTHING, API_OK)), 1)

    def test_a_missing_changelog_section_fails(self):
        self.assertEqual(release.check("v1.2.0", "1.2.0", DATED, SHIPPED_NOTHING, API_OK),
                         ["CHANGELOG.md has no '## [1.2.0] - YYYY-MM-DD' section"])

    def test_an_unfilled_date_fails(self):
        problems = release.check("v1.1.0-preview.2", "1.1.0-preview.2", UNDATED, SHIPPED_NOTHING, API_OK)
        self.assertEqual(len(problems), 1)
        self.assertIn("section of CHANGELOG.md has no date (YYYY-MM-DD)", problems[0])
        no_date = DATED.replace("## [1.1.0-preview.2] - 2026-10-24", "## [1.1.0-preview.2]")
        problems = release.check("v1.1.0-preview.2", "1.1.0-preview.2", no_date, SHIPPED_NOTHING, API_OK)
        self.assertIn("has no date (none)", problems[0])
        for wrong in ("2026-13-01", "2026-02-30", "24-10-2026", "2026-10-4"):
            text = CHANGELOG.format(date=wrong)
            self.assertEqual(release.check("v1.1.0-preview.2", "1.1.0-preview.2", text, SHIPPED_NOTHING, API_OK),
                             [f"The date of the '## [1.1.0-preview.2]' section of CHANGELOG.md is not a YYYY-MM-DD date: {wrong}"])

    def test_an_empty_or_repeated_section_fails(self):
        empty = "## [2.0.0] - 2026-10-24\n\n## [1.0.0] - 2026-01-02\n\n- One.\n"
        self.assertEqual(release.check("v2.0.0", "2.0.0", empty, SHIPPED_NOTHING, API_OK),
                         ["The '## [2.0.0]' section of CHANGELOG.md has no entries"])
        twice = DATED + "\n## [1.0.0] - 2026-01-03\n\n- Again.\n"
        self.assertEqual(release.check("v1.0.0", "1.0.0", twice, SHIPPED_NOTHING, API_OK), ["CHANGELOG.md has 2 '## [1.0.0]' sections"])

    def test_unshipped_public_api_fails(self):
        problems = release.check("v1.1.0-preview.2", "1.1.0-preview.2", DATED, UNSHIPPED, API_OK)
        self.assertEqual(len(problems), 1)
        self.assertIn("PublicAPI.Unshipped.txt has 2 entries", problems[0])

    def test_the_agentcat_api_whose_aot_warnings_are_allowed_fails(self):
        problems = release.check("v1.1.0-preview.2", "1.1.0-preview.2", DATED, SHIPPED_NOTHING, "0.0.4")
        self.assertEqual(problems, ["The release needs AgentCat.Api 0.0.5 or later: Directory.Packages.props pins 0.0.4, "
                                    "whose generated converter gives Native AOT and trimmed apps IL2026 and IL3050 "
                                    "warnings, and CHANGELOG.md and README.md say that AgentCat gives none"])
        self.assertEqual(release.check("v1.1.0-preview.2", "1.1.0-preview.2", DATED, SHIPPED_NOTHING, "0.0.6"), [])

    def test_the_gate_follows_the_aot_allowance(self):
        # One constant decides both: the AOT job allows AgentCat.Api's warnings for exactly the pin the release refuses.
        allowed = release.check_warnings.API_VERSION_WITH_WARNINGS
        self.assertEqual(release.check_warnings.verdict("IL2026", release.check_warnings.API_ORIGIN, "net8.0", allowed)[0], True)
        self.assertEqual(len(release.check("v1.0.0", "1.0.0", DATED, SHIPPED_NOTHING, allowed)), 1)

    def test_a_version_that_is_not_semver_fails(self):
        problems = release.check("v1.0", "1.0", "## [1.0] - 2026-10-24\n\n- One.\n", SHIPPED_NOTHING, API_OK)
        self.assertEqual(problems, ["The package version '1.0' is not a SemVer 2 version"])


class Notes(unittest.TestCase):
    def test_the_section_without_its_heading_the_next_section_or_the_link_references(self):
        self.assertEqual(release.section(DATED, "1.1.0-preview.2"), ("2026-10-24", "First line.\n\n### Added\n\n- Thing two."))
        self.assertEqual(release.section(DATED, "1.0.0"), ("2026-01-02", "### Added\n\n- Thing one."))
        self.assertEqual(release.section(DATED, "Unreleased"), (None, ""))

    def test_the_real_changelog(self):
        changelog = (SCRIPTS.parents[1] / "CHANGELOG.md").read_text(encoding="utf-8")
        date, body = release.section(changelog, "1.0.0")
        self.assertTrue(date == release.PLACEHOLDER or release._is_date(date), date)
        self.assertTrue(body.startswith("First release of the AgentCat SDK for C#."), body)
        self.assertNotIn("[Unreleased]:", body)


class Prepare(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        (self.root / "src" / "AgentCat").mkdir(parents=True)
        self.changelog, self.unshipped, self.shipped, self.props, _ = release.paths(self.root)
        self.changelog.write_text(UNDATED, encoding="utf-8")
        self.unshipped.write_text(UNSHIPPED, encoding="utf-8")
        self.shipped.write_text("#nullable enable\nC.Three() -> void\nC.Old() -> void\n", encoding="utf-8")
        self.props.write_text(PROPS, encoding="utf-8")

    def tearDown(self):
        self.folder.cleanup()

    def test_prepare_fills_the_date_and_ships_the_api_and_the_check_then_passes(self):
        self.unshipped.write_text(UNSHIPPED + "*REMOVED*C.Old() -> void\n", encoding="utf-8")
        before = release.check("v1.1.0-preview.2", "1.1.0-preview.2", self.changelog.read_text(),
                               self.unshipped.read_text(), API_OK)
        self.assertEqual(len(before), 2)

        version, _ = release.prepare(self.root, "2026-10-24")

        self.assertEqual(version, "1.1.0-preview.2")
        self.assertEqual(self.changelog.read_text(encoding="utf-8"), DATED)
        self.assertEqual(self.unshipped.read_text(encoding="utf-8"), "#nullable enable\n")
        self.assertEqual(self.shipped.read_text(encoding="utf-8"),
                         "#nullable enable\nA.One() -> void\nB.Two() -> void\nC.Three() -> void\n")
        after = release.check("v1.1.0-preview.2", version, self.changelog.read_text(), self.unshipped.read_text(), API_OK)
        self.assertEqual(after, [])

    def test_prepare_twice_changes_nothing_more(self):
        release.prepare(self.root, "2026-10-24")
        files = [path.read_text(encoding="utf-8") for path in (self.changelog, self.unshipped, self.shipped)]
        _, done = release.prepare(self.root, "2026-10-25")
        self.assertEqual([path.read_text(encoding="utf-8") for path in (self.changelog, self.unshipped, self.shipped)], files)
        self.assertIn("CHANGELOG.md: '## [1.1.0-preview.2]' is already dated 2026-10-24", done)

    def test_prepare_needs_the_section(self):
        self.changelog.write_text(DATED.replace("1.1.0-preview.2", "1.1.0-preview.1"), encoding="utf-8")
        with self.assertRaisesRegex(release.ReleaseError, r"no '## \[1.1.0-preview.2\] - YYYY-MM-DD' section"):
            release.prepare(self.root, "2026-10-24")

    def test_a_stable_version_has_no_suffix(self):
        self.props.write_text(PROPS.replace("preview.2", ""), encoding="utf-8")
        self.assertEqual(release.props_version(self.props.read_text()), "1.1.0")

    def test_the_real_props(self):
        props = (SCRIPTS.parents[1] / "Directory.Build.props").read_text(encoding="utf-8")
        self.assertRegex(release.props_version(props), release.VERSION)
        self.assertRegex(release.check_warnings.pinned_api_version(SCRIPTS.parents[1] / "Directory.Packages.props"),
                         release.VERSION)


class CommandLine(unittest.TestCase):
    """The check as release.yml runs it."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        (self.root / "src" / "AgentCat").mkdir(parents=True)
        changelog, unshipped, _, _, self.packages = release.paths(self.root)
        changelog.write_text(DATED, encoding="utf-8")
        unshipped.write_text(SHIPPED_NOTHING, encoding="utf-8")
        self.packages.write_text(PACKAGES.format(api=API_OK), encoding="utf-8")
        self.output = self.root / "github_output"
        self.output.write_text("", encoding="utf-8")

    def tearDown(self):
        self.folder.cleanup()

    def run_check(self, tag, version):
        environment = dict(os.environ, GITHUB_ACTIONS="true", GITHUB_OUTPUT=str(self.output))
        return subprocess.run([sys.executable, str(SCRIPTS / "release.py"), "--root", str(self.root), "check", tag, version],
                              capture_output=True, text=True, env=environment)

    def test_a_matching_tag_writes_the_outputs(self):
        result = self.run_check("v1.1.0-preview.2", "1.1.0-preview.2\n")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.output.read_text(encoding="utf-8"), "version=1.1.0-preview.2\nprerelease=true\n")
        self.assertEqual(self.run_check("v1.0.0", "1.0.0").returncode, 0)
        self.assertTrue(self.output.read_text(encoding="utf-8").endswith("version=1.0.0\nprerelease=false\n"))

    def test_the_pin_in_directory_packages_props_is_read(self):
        self.packages.write_text(PACKAGES.format(api="0.0.4"), encoding="utf-8")
        result = self.run_check("v1.1.0-preview.2", "1.1.0-preview.2")
        self.assertEqual(result.returncode, 1)
        self.assertIn("::error::The release needs AgentCat.Api 0.0.5 or later: Directory.Packages.props pins 0.0.4", result.stdout)
        self.assertEqual(self.output.read_text(encoding="utf-8"), "")

    def test_a_mismatched_tag_fails_with_an_annotation_and_no_outputs(self):
        result = self.run_check("v1.1.0", "1.1.0-preview.2")
        self.assertEqual(result.returncode, 1)
        self.assertIn("::error::The tag v1.1.0 does not match the package version 1.1.0-preview.2", result.stdout)
        self.assertEqual(self.output.read_text(encoding="utf-8"), "")

    def test_notes(self):
        result = subprocess.run([sys.executable, str(SCRIPTS / "release.py"), "--root", str(self.root), "notes", "1.0.0"],
                                capture_output=True, text=True)
        self.assertEqual((result.returncode, result.stdout), (0, "### Added\n\n- Thing one.\n"))


if __name__ == "__main__":
    unittest.main()
