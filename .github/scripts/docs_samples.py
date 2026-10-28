#!/usr/bin/env python3
"""Turns every C# fence of README.md into a source file of tests/AgentCat.DocsSamples, which compiles them.

Usage: docs_samples.py   (no arguments; run it before `dotnet build tests/AgentCat.DocsSamples`)

Every ```csharp fence becomes one file in obj/fences of that project, in its own namespace. The leading `using`
lines of the fence stay at the top of the file, its statements become the body of an async method, and the types
that follow the statements (a top-level program declares them last) stay at namespace level. `#line` directives
point compiler messages at the README lines; they name the README by its full path, because the compiler opens it.

The info string of a fence says what it is:
- ```csharp: a complete program. It compiles with the implicit usings of an ASP.NET Core project when it uses
  `WebApplication`, and with those of a console project otherwise, as `dotnet new` creates them.
- ```csharp fragment: statements that continue the ASP.NET Core server of Getting Started. They compile with the
  implicit usings of an ASP.NET Core project, where a `WebApplicationBuilder builder` is in scope.
Any other word after `csharp`, and a C# fence under another language name (`cs`, `c#`), is an error, so no fence
is left out of the build by accident.

The script also checks that one fence holds the <code> sample of the XML docs of AgentCatOptions.RedactEvent
exactly, line for line, as one block (indented as a whole). Exits with 1 and the reasons when a check fails.
"""

import re
import sys
from pathlib import Path
from xml.sax.saxutils import unescape

ROOT = Path(__file__).resolve().parents[2]
README = ROOT / "README.md"
OPTIONS = ROOT / "src" / "AgentCat" / "AgentCatOptions.cs"
OUTPUT = ROOT / "tests" / "AgentCat.DocsSamples" / "obj" / "fences"

CONSOLE_USINGS = [
    "System",
    "System.Collections.Generic",
    "System.IO",
    "System.Linq",
    "System.Net.Http",
    "System.Threading",
    "System.Threading.Tasks",
]
WEB_USINGS = CONSOLE_USINGS + [
    "System.Net.Http.Json",
    "Microsoft.AspNetCore.Builder",
    "Microsoft.AspNetCore.Hosting",
    "Microsoft.AspNetCore.Http",
    "Microsoft.AspNetCore.Routing",
    "Microsoft.Extensions.Configuration",
    "Microsoft.Extensions.DependencyInjection",
    "Microsoft.Extensions.Hosting",
    "Microsoft.Extensions.Logging",
]

FENCE_OPEN = re.compile(r"^(\s*)(`{3,}|~{3,})(.*)$")
USING = re.compile(r"^using\s+(?:static\s+)?([\w.]+)\s*;\s*$|^using\s+\w+\s*=\s*[^;]+;\s*$")
TYPE_START = re.compile(
    r"^(?:\[|(?:(?:public|internal|file|sealed|abstract|static|partial|readonly)\s+)*(?:class|record|struct|interface|enum)\b)"
)
CSHARP_NAMES = {"csharp", "cs", "c#"}
KINDS = {"": "program", "fragment": "fragment"}


class Fence:
    def __init__(self, line, info, lines):
        self.line = line  # README line number of the first content line
        self.info = info
        self.lines = lines


def read_fences(path, errors):
    """Every fenced block of the Markdown file, with the indentation of its fence removed."""
    lines = path.read_text(encoding="utf-8").split("\n")
    fences = []
    i = 0
    while i < len(lines):
        match = FENCE_OPEN.match(lines[i])
        if not match:
            i += 1
            continue
        indent, marker, info = len(match.group(1)), match.group(2), match.group(3).strip()
        close = re.compile(r"^\s*" + re.escape(marker[0]) + "{" + str(len(marker)) + r",}\s*$")
        j = i + 1
        while j < len(lines) and not close.match(lines[j]):
            j += 1
        if j == len(lines):
            errors.append(f"{path.name}:{i + 1}: the fence that starts here is never closed")
            return fences
        body = [line[indent:] if line[:indent].strip() == "" else line for line in lines[i + 1 : j]]
        fences.append(Fence(i + 2, info, body))
        i = j + 1
    return fences


def kind_of(fence, errors):
    """'program' or 'fragment' for a C# fence, None for any other fence."""
    words = fence.info.split()
    language = words[0] if words else ""
    if language.lower() not in CSHARP_NAMES:
        return None
    if language != "csharp":
        errors.append(f"README.md:{fence.line - 1}: write the C# fence as ```csharp, not ```{language}")
        return None
    rest = " ".join(words[1:])
    if rest not in KINDS:
        errors.append(f"README.md:{fence.line - 1}: unknown fence word '{rest}' (allowed: nothing, or 'fragment')")
        return None
    return KINDS[rest]


def generate(number, fence, kind):
    """The C# source of one fence."""
    lines = fence.lines
    k = 0
    while k < len(lines) and (USING.match(lines[k]) or not lines[k].strip()):
        k += 1
    head, rest = lines[:k], lines[k:]
    split = next((n for n, line in enumerate(rest) if TYPE_START.match(line)), len(rest))
    statements, types = rest[:split], rest[split:]

    own = {m.group(1) for m in (USING.match(line) for line in head) if m and m.group(1)}
    web = kind == "fragment" or any(re.search(r"\bWebApplication\b", line) for line in statements)
    implicit = [f"using {name};" for name in (WEB_USINGS if web else CONSOLE_USINGS) if name not in own]
    parameters = "string[] args" + (", WebApplicationBuilder builder" if kind == "fragment" else "")

    source = implicit + [f'#line {fence.line} "{README}"'] + head + ["#line default", ""]
    source += [f"namespace DocsSamples.Fence{number:02};", ""]
    source += ["internal static class Sample", "{", f"    internal static async Task RunAsync({parameters})", "    {"]
    source += [f'#line {fence.line + k} "{README}"'] + statements + ["#line default"]
    source += ["        await Task.CompletedTask;", "    }", "}", ""]
    if types:
        source += [f'#line {fence.line + k + split} "{README}"'] + types
    return "\n".join(source) + "\n"


def xml_sample(path):
    """The lines of the <code> sample in the XML docs of AgentCatOptions.RedactEvent."""
    lines = path.read_text(encoding="utf-8").split("\n")
    target = next((n for n, line in enumerate(lines) if re.search(r"\bRedactEvent\s*\{\s*get;", line)), None)
    if target is None:
        return None
    start = target
    while start > 0 and lines[start - 1].lstrip().startswith("///"):
        start -= 1
    doc = [re.sub(r"^\s*/// ?", "", line) for line in lines[start:target]]
    try:
        first, last = doc.index("<code>"), doc.index("</code>")
    except ValueError:
        return None
    return [unescape(line, {"&quot;": '"', "&apos;": "'"}) for line in doc[first + 1 : last]]


def holds(fence_lines, sample):
    """True when the fence holds the sample as one block, every line with the same added indentation."""
    for start in range(len(fence_lines) - len(sample) + 1):
        first = fence_lines[start]
        indent = first[: len(first) - len(first.lstrip())]
        if all(
            fence_lines[start + n] == indent + line if line else fence_lines[start + n].strip() == ""
            for n, line in enumerate(sample)
        ):
            return True
    return False


def main():
    errors = []
    fences = read_fences(README, errors)
    csharp = [(fence, kind) for fence in fences if (kind := kind_of(fence, errors))]
    if not csharp:
        errors.append("README.md has no ```csharp fence; the extraction found nothing to compile")

    sample = xml_sample(OPTIONS)
    if not sample:
        errors.append(f"{OPTIONS.relative_to(ROOT)}: no <code> sample in the XML docs of RedactEvent")
    elif not any(holds(fence.lines, sample) for fence, _ in csharp):
        errors.append(
            "README.md has no ```csharp fence that holds the <code> sample of AgentCatOptions.RedactEvent exactly "
            f"({len(sample)} lines, indented as a whole)"
        )

    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1

    OUTPUT.mkdir(parents=True, exist_ok=True)
    for old in OUTPUT.glob("*.cs"):
        old.unlink()
    for number, (fence, kind) in enumerate(csharp, start=1):
        name = f"Fence{number:02}.cs"
        (OUTPUT / name).write_text(generate(number, fence, kind), encoding="utf-8")
        print(f"README.md:{fence.line - 1}\t{kind}\t{name}")
    programs = sum(1 for _, kind in csharp if kind == "program")
    print(f"{len(csharp)} C# fences ({programs} programs, {len(csharp) - programs} fragments); "
          "the RedactEvent sample matches the XML docs")
    return 0


if __name__ == "__main__":
    sys.exit(main())
