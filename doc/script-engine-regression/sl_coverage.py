#!/usr/bin/env python3
r"""Compare local ILSL_Api functions with the official Second Life LSL index.

By default this fetches the Second Life Wiki category page and follows category
pagination. For offline use, pass --source with a saved HTML page or a plain text
file containing one llFunction name per line.

Usage:
    python3 doc/script-engine-regression/sl_coverage.py
    python3 doc/script-engine-regression/sl_coverage.py --source Category_LSL_Functions.html
    py -3 doc\script-engine-regression\sl_coverage.py --json-out coverage.json
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
from pathlib import Path
from urllib.parse import urljoin
from urllib.request import Request, urlopen


OFFICIAL_CATEGORY_URL = "https://wiki.secondlife.com/wiki/Category:LSL_Functions"
FUNCTION_RE = re.compile(r"\bll[A-Za-z0-9_]+\b")
INTERFACE_METHOD_RE = re.compile(r"\b(?:LSL_[A-Za-z]+|void|int|string|double|float|bool)\s+(ll[A-Za-z0-9_]+)\s*\(")
LINK_RE = re.compile(r"<a\b[^>]*href=[\"'](?P<href>[^\"']+)[\"'][^>]*>(?P<text>.*?)</a>", re.IGNORECASE | re.DOTALL)
TAG_RE = re.compile(r"<[^>]+>")


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def interface_path() -> Path:
    return repo_root() / "OpenSim" / "Region" / "ScriptEngine" / "Shared" / "Api" / "Interface" / "ILSL_Api.cs"


def local_lsl_functions() -> set[str]:
    source = interface_path().read_text(encoding="utf-8", errors="replace")
    return set(INTERFACE_METHOD_RE.findall(source))


def clean_link_text(text: str) -> str:
    return html.unescape(TAG_RE.sub("", text)).strip()


def fetch(url: str) -> str:
    request = Request(url, headers={"User-Agent": "OpenSim-LSL-coverage/1.0"})
    with urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8", errors="replace")


def extract_functions_from_html(page: str) -> set[str]:
    functions: set[str] = set()
    for match in LINK_RE.finditer(page):
        text = clean_link_text(match.group("text"))
        href = html.unescape(match.group("href"))

        for candidate in (text, href.rsplit("/", 1)[-1]):
            candidate = html.unescape(candidate).replace("_", "")
            if candidate.startswith("Ll") and len(candidate) > 2:
                candidate = "ll" + candidate[2:]
            if FUNCTION_RE.fullmatch(candidate):
                functions.add(candidate)

    return functions


def next_category_links(page: str, base_url: str) -> list[str]:
    links: list[str] = []
    for match in LINK_RE.finditer(page):
        text = clean_link_text(match.group("text")).lower()
        href = html.unescape(match.group("href"))
        if "next page" in text and href:
            links.append(urljoin(base_url, href))
    return links


def official_lsl_functions() -> set[str]:
    visited: set[str] = set()
    pending = [OFFICIAL_CATEGORY_URL]
    functions: set[str] = set()

    while pending:
        url = pending.pop(0)
        if url in visited:
            continue
        visited.add(url)

        page = fetch(url)
        functions.update(extract_functions_from_html(page))
        for next_url in next_category_links(page, url):
            if next_url not in visited:
                pending.append(next_url)

    return functions


def functions_from_source(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8", errors="replace")
    if "<html" in text.lower() or "<a " in text.lower():
        return extract_functions_from_html(text)
    return set(FUNCTION_RE.findall(text))


def make_report(local: set[str], official: set[str]) -> dict:
    return {
        "officialSource": OFFICIAL_CATEGORY_URL,
        "localCount": len(local),
        "officialCount": len(official),
        "implementedSecondLifeFunctions": sorted(local & official),
        "missingSecondLifeFunctions": sorted(official - local),
        "localOnlyFunctions": sorted(local - official),
    }


def print_report(report: dict) -> None:
    print("Second Life LSL coverage")
    print(f"Official source: {report['officialSource']}")
    print(f"Local ILSL_Api functions: {report['localCount']}")
    print(f"Official SL functions: {report['officialCount']}")
    print(f"Implemented official functions: {len(report['implementedSecondLifeFunctions'])}")
    print(f"Missing official functions: {len(report['missingSecondLifeFunctions'])}")
    print(f"Local-only functions: {len(report['localOnlyFunctions'])}")
    print()

    if report["missingSecondLifeFunctions"]:
        print("Missing official functions:")
        for name in report["missingSecondLifeFunctions"]:
            print(f"  - {name}")
        print()

    if report["localOnlyFunctions"]:
        print("Local-only functions:")
        for name in report["localOnlyFunctions"]:
            print(f"  - {name}")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Compare local ILSL_Api with the official Second Life LSL function index.")
    parser.add_argument("--source", help="Optional saved Second Life Wiki HTML or plain text llFunction list.")
    parser.add_argument("--json-out", help="Optional output path for the full coverage report as JSON.")
    args = parser.parse_args(argv[1:])

    local = local_lsl_functions()
    if args.source:
        official = functions_from_source(Path(args.source))
    else:
        official = official_lsl_functions()

    report = make_report(local, official)
    print_report(report)

    if args.json_out:
        Path(args.json_out).write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    return 1 if report["missingSecondLifeFunctions"] else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
