#!/usr/bin/env python3
"""
Slice the Gemini-generated contact sheet in Asset/ into real sprite frames.

The source is not a usable sprite sheet: it is an *illustration of* one. Its
problems are different from the previous sheet's, so most of this file is about
those:

  * "Transparency" is a drawn grey checkerboard, not an alpha channel — every
    pixel of the file is opaque. Backgrounds are found by chroma, not colour.
  * There are no per-frame captions to locate frames by, and neighbouring poses
    touch, so frames are found by clustering ink columns.
  * The hanging and climbing rows include *props* — a horizontal bar and vertical
    wall poles — drawn in the same yellow as the character. Colour cannot
    separate them; straightness can.
  * The wall rows disagree about which side the pole is on, so half the frames
    face the wrong way. Facing is normalised here rather than in the app.

Usage:  .venv/bin/python tools/extract_sprites.py [--size 96] [--qa]
"""

from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))

import numpy as np
from PIL import Image

# Everything that survives a change of sheet layout. This script supplies only
# the geometry of one specific sheet, plus the prop removal below, which no
# other sheet has needed.
import spritekit as kit
from spritekit import (
    background_mask, blobs_of, checker_tones, cut_frame, decolour, despeckle,
    frame_columns, frame_masks, iou, mask_of, stabilise, typical_frames,
    write_app_icon,
)

ROOT = Path(__file__).resolve().parent.parent
# One sheet per character; this script is the Pikachu one.
CHARACTER = "pikachu"
SOURCE = ROOT / "Asset" / "pikachu-sheet.png"
OUT = ROOT / "Assets" / "sprites" / CHARACTER

SHEET = (1408, 768)              # w, h — every constant below is measured from this

# The checkerboard is two low-saturation greys; the character is saturated
# yellow with near-black outlines. Chroma alone would drop those outlines, so
# background is "low chroma AND close to one of the two checker tones".
#
# The tones are measured per band rather than written down here. Two sheets in
# and the generator has already shifted them by 15 — enough that a hardcoded
# value sat right on the edge of the tolerance and let a scatter of seam pixels
# through as ink. See checker_tones().

# Bands, measured from the sheet: each is the checkerboard rectangle of one row,
# stopping short of the dark title strip above it and of the dark margins at
# x<12 / x>1396. The lower two rows are split by a dark divider at x 694-714.
#
# The boxes must sit *inside* the checkerboard, not merely contain the ink: the
# background is found by flooding inwards from the window border, so a border
# that lands on a title strip gives the flood nothing to start from and the
# whole row comes out as one giant "character".
#
# name, (x0, x1, y0, y1), frames, anchor, prop
#   anchor "bottom" — feet on the ground.
#   anchor "top"    — hands on the ceiling; the feet may dangle.
#   anchor "wall"   — back against a left-hand wall, vertically centred.
#   prop   "h"/"v"  — a bar/pole to erase before anything else looks at the row.
BANDS = [
    ("idle",      ( 12, 1396,  35, 140), 13, "bottom", None),
    ("run",       ( 12, 1396, 188, 303),  8, "bottom", None),
    ("hang",      ( 12,  694, 355, 538),  6, "top",    "h"),
    ("climb_alt", (714, 1396, 355, 538),  6, "wall",   "v"),
    ("climb",     ( 12,  694, 586, 761),  6, "wall",   "v"),
    ("climbidle", (714, 1396, 586, 761),  7, "wall",   "v"),
]

# The sheet's "Climb Wall Left" and "Climb Wall Right" rows are two takes of the
# same cycle, not two animations: once each frame is turned to face its wall they
# are the same climb. So they play as one, which is worth checking rather than
# assuming — on the previous sheet the two takes disagreed about the body's
# angle, a single loop had to cross between them, and the worst transition fell
# to 0.53: a visible hitch every 1.2 seconds. Only one row was used.
#
# On this sheet they agree. Merged and reordered, eleven frames run at a mean
# 0.87 and a worst 0.83 — better than either take alone, and nearly twice as
# long a cycle. If a future sheet regresses, the numbers the script prints will
# say so, and the fix is to put the weaker row back in a DROP set.
MERGE = {"climb_alt": "climb"}

# Animations whose source frames are not a real sequence, so reordering them
# into the smoothest loop loses nothing and removes visible pops. `climb` needs
# it because it is two takes concatenated: reordering interleaves them instead
# of playing one and then the other.
REORDER = {"run", "climb"}

# ---------------------------------------------------------------------- props

def find_prop(fg: np.ndarray, axis: str) -> list[tuple[int, int]]:
    """
    Locate the bar or the wall poles: runs of rows/columns that are ink almost
    all the way across the band.

    The prop is drawn in the character's own yellow, so no colour test can find
    it. What separates them is that a prop is *straight and full-length* and a
    Pikachu is neither.
    """
    span = fg.mean(1) if axis == "h" else fg.mean(0)
    hits = span > 0.80
    runs, start = [], None
    for i, v in enumerate(hits):
        if v and start is None:
            start = i
        elif not v and start is not None:
            runs.append((start, i))
            start = None
    if start is not None:
        runs.append((start, len(hits)))

    # A prop is thin; anything thicker crossing the whole band is not a prop.
    # The 1-2px runs are the dark divider between panels catching the window
    # border, not artwork.
    return [(a, b) for a, b in runs if 3 <= b - a <= 12]


def erase_prop(fg: np.ndarray, axis: str) -> tuple[np.ndarray, list[tuple[int, int]]]:
    """
    Remove the prop, keeping whatever the character has drawn on top of it.

    A prop pixel survives only where the character continues past the prop on
    *both* sides — that is the one arrangement in which what lies between them
    might be a limb crossing rather than the prop itself. Where the character is
    on one side only (its whole body, hanging below the bar or pressed against
    the pole) there is nothing to preserve and the prop goes.

    Requiring emptiness on both sides instead is the obvious rule and the wrong
    one: it leaves a full-height pole standing in every frame the character
    straddles, and that one frame then sets the shared canvas for every
    animation, shrinking the mascot to fit a stick.
    """
    out = fg.copy()
    runs = find_prop(fg, axis)

    # The prop's painted edges are anti-aliased against the checkerboard, and
    # those blend pixels read as ink but fall below the "spans the whole band"
    # test, so they sit just outside the detected run. Probing there finds the
    # prop's own fringe on every column and concludes the character is present
    # everywhere — which erases nothing at all. Skip past the fringe.
    feather, look = 3, 4
    bands = []

    for a, b in runs:
        lo, hi = max(0, a - feather), b + feather
        bands.append((lo, hi))
        if axis == "h":
            before = fg[max(0, lo - look):lo].any(0)
            after = fg[hi:hi + look].any(0)
            out[lo:hi, ~(before & after)] = False
        else:
            before = fg[:, max(0, lo - look):lo].any(1)
            after = fg[:, hi:hi + look].any(1)
            out[~(before & after), lo:hi] = False

    # What survives between two paws is a stub of bar with nothing holding it —
    # too big for the speckle filter, and downstream it becomes a stick floating
    # beside the character. Anything left lying entirely within the prop's own
    # band is the prop.
    for blob in blobs_of(out):
        v = [y for y, _ in blob] if axis == "h" else [x for _, x in blob]
        if any(lo <= min(v) and max(v) < hi for lo, hi in bands):
            for y, x in blob:
                out[y, x] = False

    return out, runs


# ---------------------------------------------------------------------- main

def extract_band(rgb: np.ndarray, box, expected: int, prop: str | None,
                 name: str) -> list[Image.Image] | None:
    x0, x1, y0, y1 = box
    win = rgb[y0:y1, x0:x1]

    tones = checker_tones(win)
    if tones is None:
        print(f"{name}: found no checkerboard along this band's border — the box "
              f"has drifted off the row, or the sheet's background has changed")
        return None

    bg = background_mask(win, tones)

    # The dark divider between the sheet's panels clips the window border as a
    # 1-2px line of solid "ink" running the full height of the band. No pose is
    # a straight line that long, and left alone it survives every filter here,
    # gets mirrored along with the frame it touches, and ships as a black bar
    # down the mascot's face. Clear it, and keep clearing inward while the line
    # is still too solid to be the edge of a drawing.
    for edge, inward in ((0, 1), (bg.shape[1] - 1, -1)):
        for step in range(2):
            i = edge + inward * step
            if (~bg[:, i]).mean() <= (0.9 if step == 0 else 0.4):
                break
            bg[:, i] = True

    ring = np.concatenate([bg[0], bg[-1], bg[:, 0], bg[:, -1]])
    if ring.mean() < 0.5:
        print(f"{name}: only {ring.mean():.0%} of this band's border is checkerboard — "
              f"the box has drifted onto a title strip and the flood fill will find nothing")
        return None

    # `& ~bg` is the belt to the flood's braces. The flood alone leaves any
    # checkerboard it cannot reach — the strip sealed between a wall pole and the
    # belly pressed against it, closed off at both ends by a paw — and that patch
    # ships as a square of grey tiling glued to the character's back. Nothing the
    # character is drawn in matches a checker tone: its outlines are near-black
    # and its highlights are white, both far outside CHECKER_TOL.
    fg = decolour(despeckle(~kit.flood_background(bg) & ~bg), win)

    prop_runs: list[tuple[int, int]] = []
    if prop:
        cleaned, prop_runs = erase_prop(fg, prop)
        if not prop_runs:
            print(f"{name}: expected a {'bar' if prop == 'h' else 'pole'} in this row "
                  f"and found none — BANDS no longer matches the sheet")
            return None

        # Erasing the prop opens channels the first flood was walled out of, so
        # run it again — this time what it reaches is the sheet's own background
        # showing through where the prop used to be.
        fg = despeckle(cleaned & ~bg & ~kit.flood_background(bg | ~cleaned))
        fg = decolour(fg, win)

    runs = frame_columns(fg, expected)
    if len(runs) != expected:
        print(f"{name}: split into {len(runs)} frames, expected {expected}")
        return None

    masks = frame_masks(fg, runs)
    if masks is None:
        print(f"{name}: could not assign a character to every frame")
        return None

    frames = []
    for i, m in enumerate(masks):
        f = cut_frame(win, m)
        if f is None:
            print(f"{name}: no character in frame {i + 1} (x {runs[i][0]}-{runs[i][1]})")
            return None

        # The wall rows disagree about which side of the character the pole is
        # on, so half of them face the wrong way. Normalise every wall frame to
        # "pole on the left" here: the app then mirrors once, per screen edge,
        # instead of carrying two near-identical animations.
        if prop == "v":
            xs = np.nonzero(m.any(0))[0]
            near = [p for p in prop_runs
                    if xs.min() - 30 <= (p[0] + p[1]) / 2 <= xs.max() + 30]
            if near:
                pole = min(near, key=lambda p: abs((p[0] + p[1]) / 2 - xs.mean()))
                if xs.mean() < (pole[0] + pole[1]) / 2:
                    f = f.transpose(Image.FLIP_LEFT_RIGHT)

        frames.append(f)

    return frames


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=96, help="output frame size (px)")
    ap.add_argument("--qa", action="store_true", help="also write a contact sheet")
    args = ap.parse_args()

    if not SOURCE.exists():
        print(f"source not found: {SOURCE}")
        return 1

    rgb = np.asarray(Image.open(SOURCE).convert("RGB")).astype(int)

    # The band coordinates are measured from one specific layout. Regenerate the
    # sheet at another size and they go quietly wrong: wrong bands still find
    # *some* ink and yield a pile of plausible-looking garbage frames. Colours no
    # longer need a guard here — each band measures its own (see checker_tones)
    # and says so if it can't.
    if (rgb.shape[1], rgb.shape[0]) != SHEET:
        print(f"source is {rgb.shape[1]}x{rgb.shape[0]}, expected {SHEET[0]}x{SHEET[1]} — "
              f"the BANDS coordinates in this script no longer apply")
        return 1

    collected: dict[str, tuple[str, list[Image.Image]]] = {}

    for name, box, expected, anchor, prop in BANDS:
        frames = extract_band(rgb, box, expected, prop, name)
        if frames is None:
            return 1

        dest = MERGE.get(name, name)
        if dest in collected:
            collected[dest][1].extend(frames)
        else:
            collected[dest] = (anchor, frames)

    bands_out = []
    for name, (anchor, frames) in collected.items():
        # Screen for a frame that doesn't belong before committing to an order.
        # Reordering is off for the probe so the indices still line up with the
        # frames going in.
        probe, _ = stabilise(frames, reorder=False)
        keep = typical_frames(probe)
        if len(keep) < len(frames):
            dropped = [i + 1 for i in range(len(frames)) if i not in keep]
            print(f"{name}: dropped frame {dropped} — matches nothing else in the row")
            frames = [frames[i] for i in keep]

        frames, st = stabilise(frames, reorder=(name in REORDER))
        print(f"{name}: {len(frames)} frames, area spread "
              f"{st['area_ratio_before']:.2f}x -> 1.00x, "
              f"IoU mean {st['iou_mean']:.2f} min {st['iou_min']:.2f}"
              + (f", cycle cost {st['cost_before']:.2f} -> {st['cost_after']:.2f}"
                 if name in REORDER else ""))
        bands_out.append((name, anchor, frames))

    # ---- one scale for every animation ----------------------------------
    #
    # Sizing each animation to its own bounding box makes the character change
    # size when it starts running — the run set's box is far larger than idle's
    # purely because of one frame with an outstretched tail. So normalise the
    # *body* (ink area, which the tail barely affects) across animations, then
    # give them all a single shared canvas.
    medians = {n: float(np.median([mask_of(f).sum() for f in fr])) for n, _, fr in bands_out}
    target = float(np.median(list(medians.values())))

    normalised = []
    for name, anchor, frames in bands_out:
        k = float(np.sqrt(target / max(medians[name], 1.0)))
        if abs(k - 1.0) > 0.01:
            frames = [f.resize((max(1, round(f.width * k)), max(1, round(f.height * k))),
                               Image.LANCZOS) for f in frames]
            print(f"{name}: body scaled {k:.2f}x to match the other animations")
        normalised.append((name, anchor, frames))

    # The canvas must contain every frame whole. An earlier version sized this
    # from the 88th percentile to keep the body large, betting that clipping one
    # tail tip was less visible than shrinking everything. That bet was wrong —
    # it cut the ears off most of the run cycle. Never clip; control the
    # on-screen size with `mascotSize` instead.
    extents = []
    for _, _, frames in normalised:
        for f in frames:
            b = f.getbbox()
            if b:
                extents.append(max(b[2] - b[0], b[3] - b[1]))
    side = int(max(extents)) + 8

    qa_rows = []

    for name, anchor, frames in normalised:
        dest = OUT / name
        dest.mkdir(parents=True, exist_ok=True)
        for old in dest.glob("*.png"):
            old.unlink()

        # Anchor the group once, from its shared bounding box, so every frame of
        # this animation shifts together and the stabilised alignment survives.
        boxes = [f.getbbox() for f in frames if f.getbbox()]
        gx0 = min(b[0] for b in boxes); gy0 = min(b[1] for b in boxes)
        gx1 = max(b[2] for b in boxes); gy1 = max(b[3] for b in boxes)

        # Keep a few pixels clear of every edge; content flush against the border
        # reads as cut off.
        pad = 4
        if anchor == "wall":
            # Back against a left-hand wall. The app mirrors this whole frame for
            # the right-hand wall, which is why the art only has to exist once.
            ox = pad - gx0
            oy = (side - (gy1 - gy0)) // 2 - gy0
        else:
            ox = (side - (gx1 - gx0)) // 2 - gx0
            oy = (pad - gy0) if anchor == "top" else (side - pad - (gy1 - gy0)) - gy0

        scaled = []
        for i, f in enumerate(frames, 1):
            canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
            canvas.paste(f, (ox, oy))
            canvas = canvas.resize((args.size, args.size), Image.LANCZOS)
            canvas.save(dest / f"{i:02d}.png")
            scaled.append(canvas)

        print(f"{name}: shared canvas {side}x{side} -> {args.size}px, anchor {anchor}")
        qa_rows.append((name, scaled))

        if name == "idle":
            print(f"app icon -> {write_app_icon(frames[0], ROOT / 'Assets' / 'app.ico')}")

    if args.qa and qa_rows:
        cols = max(len(r) for _, r in qa_rows)
        s = args.size
        gap = 8
        # Half light, half dark: a dark halo is invisible on a dark backdrop and
        # a light halo is invisible on a light one, so show every frame on both.
        sheet = Image.new("RGBA", (cols * s, len(qa_rows) * (s + gap)), (255, 255, 255, 255))
        px = sheet.load()
        for y in range(sheet.height):
            for x in range(cols * s):
                if (x // 12 + y // 12) % 2 == 0:
                    px[x, y] = (208, 208, 208, 255)
                if y % (s + gap) > s // 2:
                    px[x, y] = (24, 24, 28, 255) if (x // 12 + y // 12) % 2 == 0 else (44, 44, 50, 255)

        for r, (_, row) in enumerate(qa_rows):
            for c, img in enumerate(row):
                sheet.alpha_composite(img, (c * s, r * (s + gap)))

        qa = ROOT / "Assets" / "qa-contact-sheet.png"
        sheet.save(qa)
        print(f"QA sheet -> {qa}  (top half of each row = light bg, bottom = dark)")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
