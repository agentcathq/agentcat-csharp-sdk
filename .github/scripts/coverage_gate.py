#!/usr/bin/env python3
"""Fail when the union line coverage of the given Cobertura reports is below a threshold.

Usage: coverage_gate.py THRESHOLD REPORT [REPORT ...]

A line counts as covered when any report covers it. Lines are keyed by file path and line number.
"""
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def read(path):
    """Return {file: {line: hits}} for one Cobertura report."""
    root = ET.parse(path).getroot()
    lines = defaultdict(dict)
    for cls in root.iter("class"):
        name = cls.get("filename")
        for line in cls.iter("line"):
            number = int(line.get("number"))
            hits = int(line.get("hits"))
            lines[name][number] = max(lines[name].get(number, 0), hits)
    return lines


def main(argv):
    if len(argv) < 3:
        sys.exit(__doc__)
    threshold = float(argv[1])
    union = defaultdict(dict)
    for path in argv[2:]:
        lines = read(path)
        total = sum(len(v) for v in lines.values())
        if total == 0:
            sys.exit(f"{path} contains no lines.")
        covered = sum(1 for v in lines.values() for h in v.values() if h > 0)
        print(f"{path}: {covered}/{total} lines = {100.0 * covered / total:.2f} %")
        for name, per_file in lines.items():
            for number, hits in per_file.items():
                union[name][number] = max(union[name].get(number, 0), hits)

    total = sum(len(v) for v in union.values())
    if total == 0:
        sys.exit("The reports contain no lines.")
    covered = sum(1 for v in union.values() for h in v.values() if h > 0)
    percent = 100.0 * covered / total
    print(f"union: {covered}/{total} lines = {percent:.2f} %")

    worst = sorted(
        ((sum(1 for h in v.values() if h == 0), len(v), name) for name, v in union.items()),
        reverse=True,
    )[:10]
    print("Files with the most uncovered lines:")
    for missed, count, name in worst:
        print(f"  {name}: {missed} of {count}")

    if percent < threshold:
        sys.exit(f"Line coverage {percent:.2f} % is below the {threshold:.2f} % gate.")
    print(f"Line coverage meets the {threshold:.2f} % gate.")


if __name__ == "__main__":
    main(sys.argv)
