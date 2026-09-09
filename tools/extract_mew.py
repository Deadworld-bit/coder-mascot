#!/usr/bin/env python3
"""
Mew's contact sheet -> sprite frames.

Just the geometry; the machinery is in spritekit.py.

What is particular to this sheet:

  * The panel titles sit on their own rows, with a clear band of background
    between them and the frames below. So they are excluded by *geometry* — the
    row boxes simply start under them — and `lettering` stays off.

    That matters more than it sounds. Mew is pale: a quarter of its pixels have
    less colour in them than the threshold the lettering removal floods
    through, so turning that on here would not stop at the character, it would
    eat into it. Charizard could afford it; Mew cannot.

  * The panels are not on a shared column grid. Some span the full width, some
    are a left/right pair, and the pairs do not line up with each other — so
    every box carries its own coordinates rather than indexing a lattice.

  * The tail is long, thin and flung well clear of the body, so it detaches
    into its own blob and lands several columns away. Frames are found by
    counting *bodies*, and the tail rejoins its owner in frame_masks.

  * Two panels are both labelled DESCEND. They are two takes of one cycle, and
    they merge — the numbers below say whether that was the right call.

  * PSYCHIC ATTACK and ENERGY PROJECTILE are deliberately skipped. Their glow
    effects overlap into a single connected blob spanning the whole row, so
    there are no frame boundaries to find, and nothing in the mascot plays an
    attack. The artwork is still in the sheet if that ever changes.

Usage:  .venv/bin/python tools/extract_mew.py [--size 96] [--qa]
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spritekit import Panel, Sheet, run_grid

ROOT = Path(__file__).resolve().parent.parent

SHEET = Sheet(
    character="mew",
    source=ROOT / "Asset" / "mew-sheet.png",
    size=(1024, 559),
    # x0, x1, y0, y1, frames — each measured from the sheet, and each sitting
    # below its panel's title so the lettering is never in the window.
    panels=[
        Panel("idle",    [(2, 1022, 24, 88, 15)]),      # IDLE FLOAT
        Panel("fly",     [(2,  660, 120, 163, 7)]),     # FLYING
        Panel("hover",   [(2,  505, 195, 278, 8)]),     # HOVERING
        Panel("ascend",  [(510, 1022, 195, 278, 7)]),   # ASCEND
        Panel("descend", [(2,  440, 296, 364, 6)]),     # DESCEND, take one
        Panel("descend2",[(505, 1022, 296, 364, 4)]),   # DESCEND, take two
        Panel("flyidle", [(2,  510, 384, 451, 8)]),     # HOVER IDLE
    ],
    merge={"descend2": "descend"},
    # Every one of these is a loop the generator drew pose by pose, so there is
    # no true order to lose — only a least-jarring one to find. `idle` keeps its
    # order for the same reason as the other characters': it holds a blink.
    reorder={"fly", "hover", "flyidle", "ascend", "descend"},
    lettering=False,
    # See Sheet.outlier_floor: the tail makes every frame look unlike its
    # neighbours, so the usual floor discards good poses.
    outlier_floor=0.60,
)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=96, help="output frame size (px)")
    ap.add_argument("--qa", action="store_true", help="also write a contact sheet")
    args = ap.parse_args()

    return run_grid(SHEET, ROOT / "Assets" / "sprites", args.size,
                    ROOT / "Assets" / "qa-mew.png" if args.qa else None)


if __name__ == "__main__":
    raise SystemExit(main())
