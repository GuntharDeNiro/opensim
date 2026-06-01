#!/usr/bin/env python3
r"""Summarize in-world LSL Compatibility Lab output from an OpenSim log.

Usage:
    python3 doc/script-engine-regression/report.py OpenSim.log
    py -3 doc\script-engine-regression\report.py OpenSim.log
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path


LAB_PREFIX = "[LSL Compatibility Lab]"
PASS_RE = re.compile(r"\bPASS\s+(.+)$")
FAIL_RE = re.compile(r"\bFAIL\s+([^:]+)(?::\s*(.*))?$")


def load_manifest() -> dict:
    manifest_path = Path(__file__).with_name("manifest.json")
    with manifest_path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def extract_lab_results(log_path: Path) -> tuple[set[str], list[tuple[str, str]]]:
    passes: set[str] = set()
    failures: list[tuple[str, str]] = []

    with log_path.open("r", encoding="utf-8", errors="replace") as handle:
        for raw_line in handle:
            if LAB_PREFIX not in raw_line:
                continue

            line = raw_line.strip()
            pass_match = PASS_RE.search(line)
            if pass_match:
                passes.add("PASS " + pass_match.group(1).strip())
                continue

            fail_match = FAIL_RE.search(line)
            if fail_match:
                failures.append((fail_match.group(1).strip(), (fail_match.group(2) or "").strip()))

    return passes, failures


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("Usage: report.py <OpenSim.log>", file=sys.stderr)
        return 2

    log_path = Path(argv[1])
    if not log_path.exists():
        print(f"Log file not found: {log_path}", file=sys.stderr)
        return 2

    manifest = load_manifest()
    passes, failures = extract_lab_results(log_path)

    required = manifest.get("cases", [])
    missing = []
    for case in required:
        expected = case.get("requiredPass", "")
        if expected not in passes:
            missing.append((case.get("id", "unknown"), expected))

    print(f"LSL regression suite: {manifest.get('suite', 'unknown')}")
    print(f"Log: {log_path}")
    print(f"Observed PASS lines: {len(passes)}")
    print(f"Observed FAIL lines: {len(failures)}")
    print()

    if missing:
        print("Missing required passes:")
        for case_id, expected in missing:
            print(f"  - {case_id}: {expected}")
    else:
        print("All required manifest passes were observed.")

    if failures:
        print()
        print("Failures:")
        for name, detail in failures:
            suffix = f": {detail}" if detail else ""
            print(f"  - {name}{suffix}")

    return 1 if missing or failures else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
