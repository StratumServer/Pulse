#!/usr/bin/env python3
"""Check Grafana dashboard JSON for the mistakes that fail silently.

Grafana does not complain about a dashboard with overlapping panels, a panel
running off the right edge of the grid or two panels sharing an id. It just
draws something wrong, or drops a panel, and leaves you wondering. So these
files get checked here instead.

    python3 contrib/grafana/check-dashboard.py contrib/grafana/*.json ...

Exits nonzero and prints one line per violation.
"""

import json
import pathlib
import sys

COLUMNS = 24

# The dashboards this script exists to check live in the repository, so that is
# the only place it will read from: a stray argument cannot walk it out to an
# arbitrary file.
REPO = pathlib.Path(__file__).resolve().parent.parent.parent


def inside_repo(path):
    try:
        return pathlib.Path(path).resolve().is_relative_to(REPO)
    except OSError:
        return False


def panels(dashboard):
    """Every panel with a flag for whether it sits on the dashboard's own grid.

    A collapsed row carries its children in its own "panels" list, and those
    are positioned relative to the row, so they are not part of the same grid.
    """
    for panel in dashboard.get("panels", []):
        yield panel, True
        for nested in panel.get("panels", []):
            yield nested, False


def describe(panel):
    return f"id {panel.get('id', '?')} ({panel.get('title', 'untitled')})"


def check(path):
    problems = []
    with open(path, encoding="utf-8") as handle:
        dashboard = json.load(handle)

    seen_ids = {}
    boxes = []
    for panel, on_grid in panels(dashboard):
        pid = panel.get("id")
        if pid is None:
            problems.append(f"{describe(panel)} has no id")
        elif pid in seen_ids:
            problems.append(f"panel id {pid} used twice: {seen_ids[pid]} and {panel.get('title')}")
        else:
            seen_ids[pid] = panel.get("title")

        pos = panel.get("gridPos")
        if not pos:
            problems.append(f"{describe(panel)} has no gridPos")
            continue
        x, y, w, h = (pos.get(k) for k in ("x", "y", "w", "h"))
        if None in (x, y, w, h):
            problems.append(f"{describe(panel)} has an incomplete gridPos: {pos}")
            continue
        if w < 1 or h < 1:
            problems.append(f"{describe(panel)} is {w}x{h}, both must be at least 1")
        if x < 0 or y < 0:
            problems.append(f"{describe(panel)} sits at {x},{y}, neither may be negative")
        if x + w > COLUMNS:
            problems.append(f"{describe(panel)} runs off the grid: x {x} + w {w} > {COLUMNS}")
        if on_grid:
            boxes.append((panel, x, y, w, h))

    for i, (a, ax, ay, aw, ah) in enumerate(boxes):
        for b, bx, by, bw, bh in boxes[i + 1:]:
            if ax < bx + bw and bx < ax + aw and ay < by + bh and by < ay + ah:
                problems.append(f"{describe(a)} overlaps {describe(b)}")

    return problems


def main(paths):
    if not paths:
        print(__doc__)
        return 2

    failed = False
    for path in paths:
        if not inside_repo(path):
            print(f"{path}: outside the repository, refusing to read it")
            failed = True
            continue
        try:
            problems = check(path)
        except (OSError, json.JSONDecodeError) as e:
            print(f"{path}: {e}")
            failed = True
            continue
        for problem in problems:
            print(f"{path}: {problem}")
        failed = failed or bool(problems)
        if not problems:
            print(f"{path}: ok")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
