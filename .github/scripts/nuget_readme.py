#!/usr/bin/env python3
"""Writes and checks the nuget.org copy of README.md, which the pack puts in the package.

Usage:
  nuget_readme.py generate <README.md> <output>   writes the nuget.org copy; when the copy fails the lint, writes
                                                  nothing (and removes an older output) and exits with 1
  nuget_readme.py lint <file>                     checks a file; exits with 1 and the reasons when it fails

nuget.org renders CommonMark through Markdig, with no raw HTML and no GitHub alerts, and shows images only from a list
of hosts (https://learn.microsoft.com/en-us/nuget/nuget-org/package-readme-on-nuget-org). README.md is written for
GitHub, so the copy changes it as follows; code fences and every other line stay as they are.
- An HTML block (a line that starts with a tag, up to the next blank line) becomes Markdown: <div> and <p> make
  paragraphs, <h1> to <h6> headings, <a> links and <img> images; <span> keeps only its text. Any other tag is an error.
  Markdown cannot size an image, so an <img> with a width or height in pixels (a thumbnail) becomes a link to the image.
- An alert (> [!NOTE], > [!IMPORTANT], ...) becomes a plain blockquote that starts with the alert's name in bold.
- A relative link becomes an absolute GitHub URL: #anchor goes to the README on the repository page, a path to the
  file on main. A relative image becomes its raw.githubusercontent.com URL.

The lint fails on raw HTML, an alert, an image that is not an https URL on a host that nuget.org allows (inline, or
through a link reference definition), and a relative link, outside code fences and code spans.
"""

import html.parser
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit

REPOSITORY = "https://github.com/agentcathq/agentcat-csharp-sdk"
RAW = "https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main"

# The hosts that nuget.org shows images from, as its readme page lists them, and its GitHub Actions badge form.
IMAGE_HOSTS = frozenset({
    "api.codacy.com", "api.codeclimate.com", "api.dependabot.com", "api.reuse.software", "api.travis-ci.com",
    "app.codacy.com", "app.deepsource.com", "avatars.githubusercontent.com", "badgen.net", "badges.gitter.im",
    "camo.githubusercontent.com", "caniuse.bitsofco.de", "cdn.jsdelivr.net", "cdn.syncfusion.com", "ci.appveyor.com",
    "circleci.com", "cloudback.it", "codecov.io", "codefactor.io", "coveralls.io", "dev.azure.com", "devpod.sh",
    "flat.badgen.net", "gitlab.com", "i.imgur.com", "img.shields.io", "infragistics.com", "isitmaintained.com",
    "media.githubusercontent.com", "opencollective.com", "raw.github.com", "raw.githubusercontent.com", "snyk.io",
    "sonarcloud.io", "travis-ci.com", "travis-ci.org", "user-images.githubusercontent.com",
})
GITHUB_BADGE = re.compile(r"/[^/]+/[^/]+/(?:actions/)?workflows/[^?#]+/badge\.svg")

FENCE = re.compile(r" {0,3}(`{3,}|~{3,})")
HTML_START = re.compile(r" {0,3}<(?:!--|/?[A-Za-z][A-Za-z0-9-]*(?=[\s/>]|$))")
HTML_TAG = re.compile(r"<(?:!--|/?[A-Za-z][A-Za-z0-9-]*(?=[\s/>]|$))")
ALERT = re.compile(r"(?P<quote> {0,3}> ?)\[!(?P<kind>NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", re.IGNORECASE)
CODE_SPAN = re.compile(r"(`+)(?!`).*?(?<!`)\1(?!`)")
DESTINATION = re.compile(r"\]\(\s*(<?)([^)\s>]+)")
REFERENCE = re.compile(r" {0,3}\[(?P<label>[^\]]+)\]:\s*<?(?P<url>[^\s>]+)")
# ![alt][label], ![alt][] and ![alt]: an image whose URL is a link reference definition (an inline image has "(").
REFERENCE_IMAGE = re.compile(r"!\[(?P<alt>(?:\\.|[^\[\]\\])*)\](?:\[(?P<label>(?:\\.|[^\[\]\\])*)\])?(?![(\[])")
SCHEME = re.compile(r"[A-Za-z][A-Za-z0-9+.-]*:")
LINK_SCHEME = re.compile(r"(?:https?|mailto):", re.IGNORECASE)
PIXELS = re.compile(r"\s*\d+(?:px)?\s*")


class ConversionError(Exception):
    pass


def convert(text):
    """The nuget.org copy of a README, with \\n line ends."""
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    out = []
    fence = None
    i = 0
    while i < len(lines):
        line = lines[i]
        if fence is not None:
            out.append(line)
            if _closes(fence, line):
                fence = None
            i += 1
            continue
        opened = FENCE.match(line)
        if opened:
            fence = opened.group(1)
            out.append(line)
            i += 1
            continue
        if HTML_START.match(line):
            end = i
            while end < len(lines) and lines[end].strip():
                end += 1
            out.extend(_html_block("\n".join(lines[i:end]), i + 1))
            i = end
            continue
        alert = ALERT.match(line)
        if alert:
            quote = alert.group("quote")
            out.append(f"{quote}**{alert.group('kind').capitalize()}**")
            out.append(quote.rstrip())
            i += 1
            continue
        out.append("".join(part if code else _absolute_destinations(part) for code, part in _segments(line)))
        i += 1
    return "\n".join(out)


def lint(text):
    """What nuget.org would not show as written, as (line number, reason, line) tuples."""
    lines = list(_prose_lines(text))
    definitions = {}
    for _, _, prose in lines:
        definition = REFERENCE.match(prose)
        if definition:
            definitions.setdefault(_label(definition.group("label")), definition.group("url"))
    problems = []
    for number, line, prose in lines:
        if ALERT.match(line):
            problems.append((number, "a GitHub alert", line))
        if HTML_TAG.search(prose):
            problems.append((number, "raw HTML", line))
        for match in DESTINATION.finditer(prose):
            url = match.group(2)
            reason = _image_problem(url) if _is_image(prose, match.start()) else _link_problem(url)
            if reason:
                problems.append((number, reason, line))
        for image in REFERENCE_IMAGE.finditer(prose):
            url = definitions.get(_label(image.group("label") or image.group("alt")))
            reason = _image_problem(url) if url is not None else None
            if reason:
                problems.append((number, reason, line))
        reference = REFERENCE.match(prose)
        if reference and not LINK_SCHEME.match(reference.group("url")):
            problems.append((number, f"a relative link reference ({reference.group('url')})", line))
    return problems


def _prose_lines(text):
    """(line number, line, line without its code spans) of each line outside code fences."""
    fence = None
    for number, line in enumerate(text.replace("\r\n", "\n").split("\n"), 1):
        if fence is not None:
            if _closes(fence, line):
                fence = None
            continue
        opened = FENCE.match(line)
        if opened:
            fence = opened.group(1)
            continue
        yield number, line, "".join(part for code, part in _segments(line) if not code)


def _label(label):
    """A link label as CommonMark matches it: case-insensitive, with whitespace collapsed."""
    return " ".join(label.split()).casefold()


def _closes(fence, line):
    match = FENCE.match(line)
    return (match is not None and match.group(1)[0] == fence[0] and len(match.group(1)) >= len(fence)
            and not line[match.end():].strip())


def _segments(line):
    """The line as (is a code span, text) parts."""
    position = 0
    for span in CODE_SPAN.finditer(line):
        yield False, line[position:span.start()]
        yield True, span.group(0)
        position = span.end()
    yield False, line[position:]


def _is_image(text, bracket):
    """Whether the ']' at bracket ends the text of an image, that is, its '[' follows a '!'."""
    depth = 0
    for i in range(bracket, -1, -1):
        if text[i] not in "[]" or (i > 0 and text[i - 1] == "\\"):
            continue
        depth += 1 if text[i] == "]" else -1
        if depth == 0:
            return i > 0 and text[i - 1] == "!"
    return False


def _absolute_destinations(text):
    def replace(match):
        return f"]({match.group(1)}{_absolute(match.group(2), _is_image(text, match.start()))}"
    return DESTINATION.sub(replace, text)


def _absolute(url, image):
    if SCHEME.match(url) or url.startswith("//"):
        return url
    if url.startswith("#"):
        return REPOSITORY + url
    path = url
    while path.startswith("./"):
        path = path[2:]
    path = path.lstrip("/")
    return f"{RAW}/{path}" if image else f"{REPOSITORY}/blob/main/{path}"


def _image_problem(url):
    parts = urlsplit(url)
    if parts.scheme != "https" or not parts.hostname:
        return f"an image that is not an absolute https URL ({url})"
    host = parts.hostname.lower()
    if host == "github.com":
        return None if GITHUB_BADGE.fullmatch(parts.path) else f"an image from github.com that is not a workflow badge ({url})"
    if host not in IMAGE_HOSTS:
        return f"an image from {host}, which nuget.org does not show ({url})"
    return None


def _link_problem(url):
    return None if LINK_SCHEME.match(url) else f"a relative link ({url})"


def _escape(text):
    return re.sub(r"([\\`*_\[\]<])", r"\\\1", text)


def _inline(parts):
    return re.sub(r"\s+", " ", "".join(parts)).strip()


class _HtmlBlock(html.parser.HTMLParser):
    BLOCKS = {"div", "p", "h1", "h2", "h3", "h4", "h5", "h6"}

    def __init__(self, line):
        super().__init__(convert_charrefs=True)
        self.line = line
        self.blocks = []
        self.parts = []
        self.heading = 0
        self.links = []

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if tag in self.BLOCKS:
            self._flush()
            self.heading = int(tag[1]) if tag[0] == "h" else 0
        elif tag == "a":
            href = attributes.get("href")
            if not href:
                raise ConversionError(f"line {self.line}: <a> without href")
            self.links.append((len(self.parts), _absolute(href, image=False)))
        elif tag == "img":
            src = attributes.get("src")
            if not src:
                raise ConversionError(f"line {self.line}: <img> without src")
            alt = _escape(attributes.get("alt") or "")
            url = _absolute(src, image=True)
            sized = any(PIXELS.fullmatch(attributes.get(name) or "") for name in ("width", "height"))
            self.parts.append(f"[{alt or url}]({url})" if sized else f"![{alt}]({url})")
        elif tag != "span":
            raise ConversionError(f"line {self.line}: the nuget.org copy has no Markdown for <{tag}>")

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag != "img":
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        if tag in self.BLOCKS:
            self._flush()
            self.heading = 0
        elif tag == "a":
            if not self.links:
                raise ConversionError(f"line {self.line}: </a> without <a>")
            start, href = self.links.pop()
            text = _inline(self.parts[start:])
            del self.parts[start:]
            self.parts.append(f"[{text}]({href})")
        elif tag not in ("span", "img"):
            raise ConversionError(f"line {self.line}: the nuget.org copy has no Markdown for </{tag}>")

    def handle_data(self, data):
        self.parts.append(_escape(data))

    def handle_comment(self, data):
        pass

    def finish(self):
        self.close()
        if self.links:
            raise ConversionError(f"line {self.line}: <a> without </a>")
        self._flush()
        return self.blocks

    def _flush(self):
        text = _inline(self.parts)
        self.parts = []
        if text:
            self.blocks.append(("#" * self.heading + " " if self.heading else "") + text)


def _html_block(block, line):
    """The Markdown lines of an HTML block, its paragraphs and headings separated by blank lines."""
    parser = _HtmlBlock(line)
    parser.feed(block)
    lines = []
    for markdown in parser.finish():
        if lines:
            lines.append("")
        lines.append(markdown)
    return lines


def _report(path, problems):
    # path: error: text is MSBuild's canonical error form, so a failing pack shows each line as an error.
    for number, reason, line in problems:
        print(f"{path}: error: line {number} of the nuget.org readme has {reason}: {line.strip()}", file=sys.stderr)


def generate(source, output):
    source, output = Path(source), Path(output)
    output.unlink(missing_ok=True)
    try:
        text = convert(source.read_text(encoding="utf-8"))
    except ConversionError as error:
        print(f"{source}: error: {error}", file=sys.stderr)
        return 1
    problems = lint(text)
    if problems:
        _report(source, problems)
        return 1
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8", newline="\n")
    print(f"{output}: the nuget.org copy of {source}")
    return 0


def main(arguments):
    if len(arguments) == 3 and arguments[0] == "generate":
        return generate(arguments[1], arguments[2])
    if len(arguments) == 2 and arguments[0] == "lint":
        path = Path(arguments[1])
        problems = lint(path.read_text(encoding="utf-8"))
        _report(path, problems)
        return 1 if problems else 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
