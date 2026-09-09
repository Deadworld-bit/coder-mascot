#!/usr/bin/env python3
"""
Charizard's contact sheet -> sprite frames.

Just the geometry; the machinery is in spritekit.py.

What is particular to this sheet:

  * A grid, not a strip: six panels in two columns with a gutter down the
    middle, each panel one to three rows of frames.
  * Titles and frame numbers are white text printed straight onto the
    checkerboard, and "HOVERING" sits *beside* the first hover frame rather
    than above it — so there is no row of pixels that separates them and only
    colour can. That is what `lettering` turns on, and it is only safe because
    a clean Charizard frame contains exactly zero pale grey pixels.
  * Every pose is airborne except IDLE, which decides the anchoring: an
    airborne pose has nothing to rest on, and the same `fly` frames play along
    the ceiling and along the floor, so they cannot be anchored to either
    without floating a window's height off the other.

Usage:  .venv/bin/python tools/extract_charizard.py [--size 96] [--qa]
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spritekit import Panel, Sheet, run_grid

ROOT = Path(__file__).resolve().parent.parent

# The two panel columns. Measured: ink stops at x=502 on the left and resumes
# at x=526 on the right.
L0, L1 = 6, 502
R0, R1 = 526, 1018

# Rows of frames, top to bottom. Each row spans both columns, but the columns
# hold different animations at the same height — read in columns, not rows.
ROWS = [(28, 110), (113, 197), (199, 279), (278, 370), (387, 464), (472, 547)]


def box(col: tuple[int, int], row: int, count: int):
    return (col[0], col[1], ROWS[row][0], ROWS[row][1], count)


LEFT, RIGHT = (L0, L1), (R0, R1)

SHEET = Sheet(
    character="charizard",
    source=ROOT / "Asset" / "charizard-sheet.png",
    size=(1024, 559),
    panels=[
        Panel("idle",    [box(LEFT, 0, 5), box(LEFT, 1, 5), box(LEFT, 2, 3)]),
        Panel("ascend",  [box(LEFT, 3, 4)]),
        Panel("descend", [box(LEFT, 4, 4), box(LEFT, 5, 4)]),
        Panel("fly",     [box(RIGHT, 0, 4), box(RIGHT, 1, 4)]),
        Panel("hover",   [box(RIGHT, 2, 4), box(RIGHT, 3, 4)]),
        Panel("flyidle", [box(RIGHT, 4, 4), box(RIGHT, 5, 4)]),
    ],
    # None of these was drawn as a recorded sequence, and each is a loop with no
    # privileged first frame, so reordering into the smoothest cycle costs
    # nothing. IDLE is the exception: it holds a blink that shuffling destroys.
    reorder={"fly", "hover", "flyidle", "ascend", "descend"},
    lettering=True,
)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=96, help="output frame size (px)")
    ap.add_argument("--qa", action="store_true", help="also write a contact sheet")
    args = ap.parse_args()

    return run_grid(SHEET, ROOT / "Assets" / "sprites", args.size,
                    ROOT / "Assets" / "qa-charizard.png" if args.qa else None)


if __name__ == "__main__":
    raise SystemExit(main())
