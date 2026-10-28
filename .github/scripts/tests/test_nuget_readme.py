"""Tests of nuget_readme.py: the nuget.org copy of the real README.md, the conversions and the lint."""

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
import nuget_readme  # noqa: E402

README = SCRIPTS.parents[1] / "README.md"
REPOSITORY = "https://github.com/agentcathq/agentcat-csharp-sdk"
RAW = "https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main"


def reasons(text):
    return [reason for _, reason, _ in nuget_readme.lint(text)]


def html_block_lines(lines):
    """The indexes of the lines of README.md that belong to HTML blocks."""
    inside, fence, found = False, None, set()
    for i, line in enumerate(lines):
        if fence is None and nuget_readme.FENCE.match(line):
            fence = nuget_readme.FENCE.match(line).group(1)
        elif fence is not None and nuget_readme._closes(fence, line):
            fence = None
        elif fence is None and (inside or nuget_readme.HTML_START.match(line)):
            inside = bool(line.strip())
            if inside:
                found.add(i)
    return found


class RealReadme(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = README.read_text(encoding="utf-8")
        cls.copy = nuget_readme.convert(cls.source)
        cls.lines = cls.copy.split("\n")

    def test_the_source_fails_the_lint_and_the_copy_passes_it(self):
        found = reasons(self.source)
        self.assertIn("raw HTML", found)
        self.assertIn("a GitHub alert", found)
        self.assertEqual(nuget_readme.lint(self.copy), [])

    def test_the_header_becomes_an_image_a_heading_of_links_and_a_paragraph_of_badges(self):
        self.assertEqual(self.lines[0], f"![AgentCat — see exactly how agents experience your product]({RAW}/docs/static/og-image.png)")
        self.assertEqual(self.lines[1], "")
        self.assertEqual(self.lines[2], " · ".join([
            f"### [Getting Started]({REPOSITORY}#getting-started)",
            f"[Features]({REPOSITORY}#why-use-agentcat-)",
            "[Docs](https://docs.agentcat.com)",
            "[Website](https://agentcat.com)",
            f"[Open Source]({REPOSITORY}#free-for-open-source)",
            "[Schedule a Demo](https://book.agentcat.com/meet)",
        ]))
        self.assertEqual(self.lines[3], "")
        badges = self.lines[4]
        self.assertTrue(badges.startswith("[![NuGet version](https://img.shields.io/nuget/vpre/AgentCat.svg)](https://www.nuget.org/packages/AgentCat) "))
        self.assertEqual(badges.count("[!["), 6)
        self.assertIn(f"[![CI]({REPOSITORY}/actions/workflows/ci.yml/badge.svg)]({REPOSITORY}/actions/workflows/ci.yml)", badges)

    def test_the_note_becomes_a_plain_blockquote(self):
        self.assertNotIn("[!NOTE]", self.copy)
        start = self.lines.index("> **Note**")
        self.assertEqual(self.lines[start + 1], ">")
        self.assertTrue(self.lines[start + 2].startswith("> AgentCat for C# is compatible with"))

    def test_the_architecture_image_and_the_cats_become_markdown(self):
        self.assertIn(f"![AgentCat architecture — the AgentCat SDK inside your MCP server sends analytics to your observability "
                      f"vendors and session replay to the AgentCat dashboard]({RAW}/docs/static/architecture.png)", self.lines)
        # The cats are 80 x 80 pixel thumbnails in README.md; Markdown cannot size an image, so they become links.
        self.assertIn(f"[bibi]({RAW}/docs/cats/bibi.png) [zelda]({RAW}/docs/cats/zelda.jpg) [void]({RAW}/docs/cats/void.jpg)",
                      self.lines)

    def test_every_other_line_is_unchanged_and_in_order(self):
        source = self.source.split("\n")
        skipped = html_block_lines(source)
        kept = [line for i, line in enumerate(source) if i not in skipped and not nuget_readme.ALERT.match(line)]
        remaining = iter(self.lines)
        missing = [line for line in kept if not any(line == other for other in remaining)]
        self.assertEqual(missing, [])
        self.assertEqual(self.copy.count("```"), self.source.count("```"))

    def test_generate_writes_the_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "obj" / "nuget" / "README.md"
            result = run("generate", str(README), str(output))
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(output.read_text(encoding="utf-8"), self.copy)


class Generate(unittest.TestCase):
    def test_raw_html_left_in_the_copy_fails_the_pack_and_writes_nothing(self):
        # Inline HTML inside a paragraph is not an HTML block, so the copy keeps it and the lint stops the pack.
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "README.md"
            source.write_text(README.read_text(encoding="utf-8") + "\nThe <b>bold</b> claim.\n", encoding="utf-8")
            output = Path(folder) / "out" / "README.md"
            output.parent.mkdir()
            output.write_text("an older copy", encoding="utf-8")
            result = run("generate", str(source), str(output))
            self.assertEqual(result.returncode, 1)
            self.assertIn(f"{source}: error: line ", result.stderr)
            self.assertIn("of the nuget.org readme has raw HTML: The <b>bold</b> claim.", result.stderr)
            self.assertFalse(output.exists())

    def test_an_html_tag_without_markdown_fails_the_pack(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "README.md"
            source.write_text("# Title\n\n<details>\n<summary>More</summary>\n</details>\n", encoding="utf-8")
            output = Path(folder) / "README.out.md"
            result = run("generate", str(source), str(output))
            self.assertEqual(result.returncode, 1)
            self.assertIn("line 3: the nuget.org copy has no Markdown for <details>", result.stderr)
            self.assertFalse(output.exists())

    def test_lint_command(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "README.md"
            path.write_text("# Title\n\n> [!IMPORTANT]\n> Text\n", encoding="utf-8")
            result = run("lint", str(path))
            self.assertEqual(result.returncode, 1)
            self.assertIn("line 3 of the nuget.org readme has a GitHub alert", result.stderr)
            path.write_text("# Title\n\n> **Important**\n> Text\n", encoding="utf-8")
            self.assertEqual(run("lint", str(path)).returncode, 0)


class Convert(unittest.TestCase):
    def test_relative_links_and_images_become_absolute(self):
        self.assertEqual(nuget_readme.convert('<p><a href="#usage">Usage</a> <a href="./docs/guide.md">Guide</a></p>'),
                         f"[Usage]({REPOSITORY}#usage) [Guide]({REPOSITORY}/blob/main/docs/guide.md)")
        self.assertEqual(nuget_readme.convert('<img src="docs/static/a.png" alt="A">'), f"![A]({RAW}/docs/static/a.png)")
        self.assertEqual(nuget_readme.convert("See [usage](#usage), ![a](docs/a.png) and [b](https://b.example/x)."),
                         f"See [usage]({REPOSITORY}#usage), ![a]({RAW}/docs/a.png) and [b](https://b.example/x).")

    def test_code_is_left_alone(self):
        text = "Use `[x](#y)` here.\n\n```html\n<div align=\"center\">\n\n<img src=\"a.png\">\n```\n> `[!NOTE]`"
        self.assertEqual(nuget_readme.convert(text), text)

    def test_alerts_keep_their_name(self):
        self.assertEqual(nuget_readme.convert("> [!WARNING]\n> Careful."), "> **Warning**\n>\n> Careful.")
        self.assertEqual(nuget_readme.convert(">[!tip]\n>Short."), ">**Tip**\n>\n>Short.")

    def test_headings_paragraphs_and_spans(self):
        text = '<h2 align="center">\n  <span>A</span> &amp; <a href="https://b.example">B_c</a>\n</h2>\n<p>One *two*</p>'
        self.assertEqual(nuget_readme.convert(text), "## A & [B\\_c](https://b.example)\n\nOne \\*two\\*")

    def test_a_percentage_width_keeps_the_image(self):
        self.assertEqual(nuget_readme.convert('<img src="https://i.imgur.com/a.png" alt="a" width="50%">'),
                         "![a](https://i.imgur.com/a.png)")
        self.assertEqual(nuget_readme.convert('<img src="https://i.imgur.com/a.png" height="40px">'),
                         "[https://i.imgur.com/a.png](https://i.imgur.com/a.png)")

    def test_comments_are_dropped(self):
        self.assertEqual(nuget_readme.convert("<!-- hidden -->\n\nText"), "\nText")


class Lint(unittest.TestCase):
    def test_raw_html(self):
        self.assertEqual(reasons('<div align="center">'), ["raw HTML"])
        self.assertEqual(reasons("A <br> B"), ["raw HTML"])
        self.assertEqual(reasons("</p>"), ["raw HTML"])
        self.assertEqual(reasons("<!-- note -->"), ["raw HTML"])

    def test_text_that_is_not_html(self):
        self.assertEqual(reasons("at the cost of < 1% more, a <= b, and <https://agentcat.com>"), [])
        self.assertEqual(reasons("Return `new ValueTask<T>(value)` or ``a `<b>` c``."), [])
        self.assertEqual(reasons("```csharp\nvar x = new List<string>();\n<div>\n```\nText"), [])
        self.assertEqual(reasons("~~~\n<div>\n```\n<p>\n~~~"), [])

    def test_alerts(self):
        for kind in ("NOTE", "TIP", "IMPORTANT", "WARNING", "CAUTION", "note"):
            self.assertEqual(reasons(f"> [!{kind}]"), ["a GitHub alert"])

    def test_images(self):
        self.assertEqual(reasons("![a](https://raw.githubusercontent.com/o/r/main/a.png)"), [])
        self.assertEqual(reasons("[![b](https://img.shields.io/nuget/v/AgentCat.svg)](https://www.nuget.org/packages/AgentCat)"), [])
        self.assertEqual(reasons("![ci](https://github.com/o/r/actions/workflows/ci.yml/badge.svg)"), [])
        self.assertEqual(reasons("![ci](https://github.com/o/r/workflows/ci/badge.svg?branch=main)"), [])
        self.assertEqual(reasons("![a](docs/a.png)"), ["an image that is not an absolute https URL (docs/a.png)"])
        self.assertEqual(reasons("![a](http://img.shields.io/a.svg)"), ["an image that is not an absolute https URL (http://img.shields.io/a.svg)"])
        self.assertEqual(reasons("![a](https://badge.fury.io/js/x.svg)"),
                         ["an image from badge.fury.io, which nuget.org does not show (https://badge.fury.io/js/x.svg)"])
        self.assertEqual(reasons("![a](https://github.com/o/r/raw/main/a.png)"),
                         ["an image from github.com that is not a workflow badge (https://github.com/o/r/raw/main/a.png)"])

    def test_reference_style_images(self):
        fury = "an image from badge.fury.io, which nuget.org does not show (https://badge.fury.io/x.svg)"
        self.assertEqual(reasons("![logo][ref]\n\n[ref]: https://badge.fury.io/x.svg"), [fury])
        self.assertEqual(reasons("![logo]\n\n[LOGO]: https://badge.fury.io/x.svg"), [fury])
        self.assertEqual(reasons("[![b][ci]](https://a.example)\n\n[ci]:  http://img.shields.io/a.svg"),
                         ["an image that is not an absolute https URL (http://img.shields.io/a.svg)"])
        self.assertEqual(reasons("![Logo][]\n\n[logo]: https://img.shields.io/a.svg"), [])
        # Without a definition, ![text] is plain text; a link that uses a definition is no image.
        self.assertEqual(reasons("![not an image] and [a link][ref]\n\n[ref]: https://badge.fury.io/x.svg"), [])
        self.assertEqual(reasons("![logo][ref]\n\n```\n[ref]: https://badge.fury.io/x.svg\n```"), [])

    def test_links(self):
        self.assertEqual(reasons("[a](https://a.example) [b](http://b.example) [c](mailto:c@example.com)"), [])
        self.assertEqual(reasons("[a](#usage)"), ["a relative link (#usage)"])
        self.assertEqual(reasons("[a](docs/a.md)"), ["a relative link (docs/a.md)"])
        self.assertEqual(reasons("[![b](https://img.shields.io/b.svg)](LICENSE)"), ["a relative link (LICENSE)"])
        self.assertEqual(reasons("[a]: docs/a.md"), ["a relative link reference (docs/a.md)"])
        self.assertEqual(reasons("[a]: https://a.example"), [])

    def test_line_numbers(self):
        self.assertEqual([number for number, _, _ in nuget_readme.lint("# T\n\n<p>\n\n```\n<p>\n```\n<p>")], [3, 8])


def run(*arguments):
    return subprocess.run([sys.executable, str(SCRIPTS / "nuget_readme.py"), *arguments], capture_output=True, text=True)


if __name__ == "__main__":
    unittest.main()
