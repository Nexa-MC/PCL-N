#!/usr/bin/env python3
"""Validate and print the conservative Alpha.6 physical-acceptance ledger."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_LEDGER = Path(__file__).with_name("alpha6-status.json")
STATES = {"pending", "partial", "blocked-external", "accepted"}
PRIORITIES = {"P0", "P1", "P2"}


def load_and_validate(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    errors: list[str] = []
    if data.get("schema") != 1:
        errors.append("schema must be 1")
    items = data.get("items")
    if not isinstance(items, list) or not items:
        errors.append("items must be a non-empty array")
        items = []
    seen: set[str] = set()
    for index, item in enumerate(items):
        prefix = f"items[{index}]"
        item_id = item.get("id")
        if not isinstance(item_id, str) or not item_id:
            errors.append(f"{prefix}.id must be non-empty")
        elif item_id in seen:
            errors.append(f"{prefix}.id is duplicated: {item_id}")
        else:
            seen.add(item_id)
        if item.get("priority") not in PRIORITIES:
            errors.append(f"{prefix}.priority is invalid")
        if item.get("state") not in STATES:
            errors.append(f"{prefix}.state is invalid")
        remaining = item.get("remaining")
        if item.get("state") == "accepted" and remaining:
            errors.append(f"{prefix} is accepted but still has remaining work")
        if item.get("state") != "accepted" and not remaining:
            errors.append(f"{prefix} is not accepted and must describe remaining work")
        evidence = item.get("evidence")
        if not isinstance(evidence, list) or not evidence:
            errors.append(f"{prefix}.evidence must be a non-empty array")
        else:
            for relative in evidence:
                if not isinstance(relative, str) or Path(relative).is_absolute() or not (ROOT / relative).is_file():
                    errors.append(f"{prefix}.evidence does not name a repository file: {relative!r}")
    expected_overall = "accepted" if items and all(item.get("state") == "accepted" for item in items) else "not-accepted"
    if data.get("overall") != expected_overall:
        errors.append(f"overall must be {expected_overall!r} for the item states")
    if errors:
        raise ValueError("\n".join(errors))
    return data


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ledger", type=Path, default=DEFAULT_LEDGER)
    parser.add_argument("--require-accepted", action="store_true")
    args = parser.parse_args(argv)
    try:
        data = load_and_validate(args.ledger)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        print(f"Alpha.6 ledger invalid: {error}", file=sys.stderr)
        return 2
    counts = {state: 0 for state in STATES}
    for item in data["items"]:
        counts[item["state"]] += 1
        print(f"{item['priority']} {item['state']:16} {item['id']}: {item['summary']}")
    print(f"overall: {data['overall']} ({counts['accepted']}/{len(data['items'])} accepted)")
    return 1 if args.require_accepted and data["overall"] != "accepted" else 0


if __name__ == "__main__":
    raise SystemExit(main())
