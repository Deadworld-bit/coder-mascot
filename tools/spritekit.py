#!/usr/bin/env python3
"""
Shared machinery for turning an AI-generated contact sheet into sprite frames.

Two characters now come from two sheets with nothing in common but their
problems: a drawn checkerboard standing in for transparency, poses that touch,
captions and titles that are not artwork, and frames that were each drawn
separately rather than as a cycle. Everything that survives a change of layout
lives here; each sheet's own script supplies only its geometry.

Not a general library — every threshold in it was measured against real sheets,
and they are commented with what happens when they are wrong.
"""

from __future__ import annotations

from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image

CHROMA_BG = 26                   # max-min channel spread below this may be background
CHECKER_TOL = 46                 # |Δ| sum to the nearest sampled checker tone


# ---------------------------------------------------------------- background

def checker_tones(win: np.ndarray) -> np.ndarray | None:
    """
    The greys this band's checkerboard is drawn in, sampled from its own border.

    Measured rather than hardcoded because the generator's palette drifts between
    sheets, and a tone that has drifted to the edge of CHECKER_TOL doesn't fail —
    it lets a scatter of seam pixels through as ink, which then weld themselves
    onto whatever they touch.

    Returns the two tones plus three blends between them. The blends matter: the
    sheet softens the seam where two squares meet, and a 50/50 mix sits ~75 from
    *either* tone, well outside any tolerance narrow enough to spare the
    character's own dark outlines.

    Everything here is a median, because the border is not purely background —
    a pose can touch it, and black outlines are low-chroma too. A handful of
    those cannot move a median.
    """
    ring = np.concatenate([win[0], win[-1], win[:, 0], win[:, -1]])
    grey = ring[(ring.max(1) - ring.min(1)) < CHROMA_BG]
    if len(grey) < 50:
        return None

    lum = grey.mean(1)
    mid = np.median(lum)
    dark, light = grey[lum <= mid], grey[lum > mid]
    if len(dark) < 10 or len(light) < 10:
        return None

    return np.linspace(np.median(dark, 0), np.median(light, 0), 5)


def background_mask(rgb: np.ndarray, tones: np.ndarray) -> np.ndarray:
    """Pixels that look like the drawn checkerboard."""
    chroma = rgb.max(2) - rgb.min(2)
    d = np.abs(rgb[:, :, None, :] - tones[None, None, :, :]).sum(3).min(2)
    return (chroma < CHROMA_BG) & (d < CHECKER_TOL)


def flood_background(is_bg: np.ndarray) -> np.ndarray:
    """Background reachable from the window border. Interior darks are spared."""
    h, w = is_bg.shape
    seen = np.zeros((h, w), bool)
    q: deque[tuple[int, int]] = deque()

    for x in range(w):
        for y in (0, h - 1):
            if is_bg[y, x] and not seen[y, x]:
                seen[y, x] = True
                q.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if is_bg[y, x] and not seen[y, x]:
                seen[y, x] = True
                q.append((y, x))

    while q:
        y, x = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and is_bg[ny, nx] and not seen[ny, nx]:
                seen[ny, nx] = True
                q.append((ny, nx))
    return seen


def dilate(mask: np.ndarray, n: int = 2) -> np.ndarray:
    """Grow a mask by n pixels in 4-connectivity."""
    out = mask.copy()
    for _ in range(n):
        g = out.copy()
        g[1:, :] |= out[:-1, :]
        g[:-1, :] |= out[1:, :]
        g[:, 1:] |= out[:, :-1]
        g[:, :-1] |= out[:, 1:]
        out = g
    return out


# ------------------------------------------------------------------- framing

def frame_columns(fg: np.ndarray, expected: int) -> list[tuple[int, int]]:
    """
    Split a band into `expected` frames from its ink columns.

    Gap-finding alone fails in both directions: a flung-out tail is a detached
    blob, which over-segments, and neighbouring poses touch, which
    under-segments. So find the gaps, then converge on the count from whichever
    side we land on — close the narrowest gap, or cut the widest run at its
    thinnest column. Both moves take the least-damaging option available, which
    is the best that can be said without knowing where the frames really are.
    """
    cols = fg.sum(0)
    runs, start = [], None
    for x, v in enumerate(cols):
        if v > 1 and start is None:
            start = x
        elif v <= 1 and start is not None:
            runs.append((start, x))
            start = None
    if start is not None:
        runs.append((start, len(cols)))

    # Discard slivers before counting. The panel dividers clip the window border
    # and leave a tall thin strip at x=0 or x=w-1 that is exactly as wide as a
    # limb; counting it as a frame stops the split loop one merge short, so a
    # real pair of poses stays fused while a strip of nothing gets its own file.
    runs = [r for r in runs if r[1] - r[0] >= 4]
    if runs:
        weight = [int(fg[:, a:b].sum()) for a, b in runs]
        floor = 0.15 * float(np.median(weight))
        runs = [r for r, wgt in zip(runs, weight) if wgt >= floor]

    while len(runs) > expected:
        gaps = [runs[i + 1][0] - runs[i][1] for i in range(len(runs) - 1)]
        i = int(np.argmin(gaps))
        runs[i:i + 2] = [(runs[i][0], runs[i + 1][1])]

    while len(runs) < expected and runs:
        i = int(np.argmax([b - a for a, b in runs]))
        a, b = runs[i]
        if b - a < 16:
            break
        # Cut where the two poses touch most weakly, ignoring the outer thirds
        # so a merged pair is halved rather than shaved.
        lo, hi = a + (b - a) // 3, b - (b - a) // 3
        cut = lo + int(np.argmin(cols[lo:hi]))
        runs[i:i + 1] = [(a, cut), (cut, b)]

    return runs


# ------------------------------------------------------------------- cutting

def blobs_of(mask: np.ndarray) -> list[list[tuple[int, int]]]:
    h, w = mask.shape
    seen = np.zeros((h, w), bool)
    out = []
    for sy in range(h):
        for sx in range(w):
            if not mask[sy, sx] or seen[sy, sx]:
                continue
            blob, q = [], deque([(sy, sx)])
            seen[sy, sx] = True
            while q:
                y, x = q.popleft()
                blob.append((y, x))
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        ny, nx = y + dy, x + dx
                        if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                            seen[ny, nx] = True
                            q.append((ny, nx))
            out.append(blob)
    return out


def drop_text(mask: np.ndarray, rgb: np.ndarray, lum: int = 170, chroma: int = 25) -> np.ndarray:
    """
    Remove lettering drawn over the artwork.

    Two things make this harder than it sounds. A caption printed across a wing
    is one connected region with the character, so the blob-level test in
    decolour() keeps the pair and the sprite ships with "02" stamped on it. And
    the letters are white with a dark outline, so deleting only what is bright
    leaves a hollow stencil behind — the outline is not bright enough to match
    and not detached enough for decolour to claim, because it touches the
    character directly.

    So: seed on the white fill, then grow through anything colourless. Letters
    are white-on-grey throughout, so the flood consumes the whole glyph and
    stops dead at the character, whose every pixel has colour in it. The only
    thing lost is the character's own dark outline where a letter was sitting on
    top of it, which was unrecoverable anyway.

    Only safe where the character has no bright grey of its own — measured, not
    assumed. A clean Charizard frame contains exactly zero pixels matching the
    seed test, while a panel title contains hundreds at chroma 4 or less. Do not
    reach for this on a sheet whose character has white in it: the Pikachu
    frames have white eyes, and this would blind them.
    """
    ch = rgb.max(2) - rgb.min(2)
    colourless = ch < chroma
    seeds = (rgb.mean(2) > lum) & colourless & mask

    if not seeds.any():
        return mask

    grown = seeds.copy()
    while True:
        wider = dilate(grown, 1) & colourless & mask
        if wider.sum() == grown.sum():
            return mask & ~grown
        grown = wider


def despeckle(mask: np.ndarray, min_size: int = 200) -> np.ndarray:
    """
    Drop crumbs.

    The checkerboard's squares meet in a blurry seam that no colour test claims
    for either tone, which leaves a few hundred 30-60px specks scattered through
    the gaps between poses. They are invisible in the output but they are not
    harmless: projected onto columns they bridge one frame to the next, and the
    band then splits into half as many frames as it has.
    """
    out = np.zeros_like(mask)
    for blob in blobs_of(mask):
        if len(blob) >= min_size:
            for y, x in blob:
                out[y, x] = True
    return out


def keep_character(mask: np.ndarray) -> np.ndarray:
    """
    Keep the character and its detached parts, drop anything that bled in.

    The run and hang cycles draw the tail and the dangling feet as separate
    blobs, so "largest blob only" would amputate them. Keep the biggest blob plus
    any other decent-sized blob that does not touch a side border — bleed from
    the next pose always does, the character normally doesn't.
    """
    h, w = mask.shape
    parts = sorted(blobs_of(mask), key=len, reverse=True)
    if not parts:
        return np.zeros((h, w), bool)

    biggest = len(parts[0])
    out = np.zeros((h, w), bool)

    for i, blob in enumerate(parts):
        if i:
            if len(blob) < 0.02 * biggest:
                continue
            if any(x == 0 or x == w - 1 for _, x in blob):
                continue
        for y, x in blob:
            out[y, x] = True
    return out


def decolour(mask: np.ndarray, rgb: np.ndarray, floor: int = 35) -> np.ndarray:
    """
    Drop grey debris.

    The checkerboard's seams clump into blobs too big for the speckle filter, and
    one panel of the sheet has a grey ghost drawn into it. Both survive every
    shape-based test and neither is a Pikachu: the character is saturated yellow
    throughout, including its brown stripes and black ear tips, which are dark
    but still attached to something yellow.
    """
    chroma = rgb.max(2) - rgb.min(2)
    out = np.zeros_like(mask)
    for blob in blobs_of(mask):
        ys = np.fromiter((y for y, _ in blob), int)
        xs = np.fromiter((x for _, x in blob), int)
        if chroma[ys, xs].mean() < floor:
            continue
        out[ys, xs] = True
    return out


def frame_masks(fg: np.ndarray, runs: list[tuple[int, int]]) -> list[np.ndarray] | None:
    """
    One full-band mask per frame.

    Assignment is by blob, not by cropping at the run boundaries, because
    neighbouring poses overlap in x: in the hanging row one character's tail
    reaches 25px into the next one's column range. Cropping there amputates a
    tail on every lap; assigning whole blobs by their centroid keeps both poses
    intact and lets them interleave.

    A blob that genuinely fuses two poses has to be cut somewhere, and shows up
    here as a run with nothing assigned to it. Only then is a vertical cut made,
    and only through that one blob.
    """
    parts = [b for b in blobs_of(fg) if len(b) >= 200]
    if not parts:
        return None

    # A blob that is far bigger than the rest is two poses drawn touching, not
    # one large pose. Handing it to a single run puts two characters in one
    # frame and leaves the next frame empty, so cut it at the run boundaries
    # instead. Everything else is assigned whole, which is the point: only
    # genuinely fused art ever gets a straight edge through it.
    # Size a pose from the row's total ink and its known frame count, not from
    # the median blob: in the wall rows most blobs are fused pairs, so the median
    # *is* a fused pair and nothing ever looks oversized.
    typical = float(sum(len(b) for b in parts)) / len(runs)

    owners: list[list[np.ndarray]] = [[] for _ in runs]
    for blob in parts:
        ys = np.fromiter((y for y, _ in blob), int)
        xs = np.fromiter((x for _, x in blob), int)

        if len(blob) > 1.6 * typical:
            for i, (a, b) in enumerate(runs):
                sel = (xs >= a) & (xs < b)
                if sel.sum() >= 200:
                    owners[i].append(np.stack([ys[sel], xs[sel]]))
            continue

        cx = float(xs.mean())
        i = min(range(len(runs)),
                key=lambda k: 0 if runs[k][0] <= cx < runs[k][1]
                else min(abs(cx - runs[k][0]), abs(cx - runs[k][1])))
        owners[i].append(np.stack([ys, xs]))

    out = []
    for parts_here in owners:
        m = np.zeros_like(fg)
        for ys, xs in parts_here:
            m[ys, xs] = True
        if m.sum() < 200:
            return None
        out.append(m)

    return out


def cut_frame(rgb: np.ndarray, keep: np.ndarray) -> Image.Image | None:
    """One window of the sheet plus its already-cleaned ink mask -> an RGBA frame."""
    solid = keep_character(keep)
    if solid.sum() < 200:
        return None

    alpha = np.zeros(solid.shape, float)
    alpha[solid] = 1.0

    # Feather only where the shape meets erased space, so the character's own
    # dark outlines stay fully opaque. Ramping on colour distance instead turns
    # every outline translucent and washes it toward white.
    edge = solid & dilate(~solid, 1) & ~dilate(~solid, 2)
    alpha[edge] = 0.85

    out = np.dstack([rgb, np.rint(alpha * 255)]).astype(np.uint8)

    ys, xs = np.where(alpha > 0.05)
    if len(ys) == 0:
        return None
    return Image.fromarray(out[ys.min():ys.max() + 1, xs.min():xs.max() + 1], "RGBA")


# --------------------------------------------------------------- stabilising

def mask_of(im: Image.Image) -> np.ndarray:
    return np.asarray(im)[..., 3] > 40


def typical_frames(stabilised: list[Image.Image], floor: float = 0.80) -> list[int]:
    """
    Which frames belong to the same animation as the rest of the row.

    Score each frame by how well it matches its *best* neighbour, not its
    average one — a frame at the far end of a legitimate cycle looks nothing
    like the far end of the other side, and that is not a defect.

    Measured across every row of this sheet, real frames score 0.90–1.04 of the
    row's median and the one bad frame scores 0.60. What it catches is a pose the
    generator drew with a translucent grey artifact welded to the character's
    tail: too grey to keep, too attached to separate, and it drags the worst
    transition in that animation from 0.75 down to 0.50.
    """
    masks = [mask_of(f) for f in stabilised]
    n = len(masks)

    # Below this there isn't enough evidence to call anything an outlier. On a
    # four-frame ascend the other three are near-duplicates at 0.95, so the
    # median sits at 0.95 and the fourth — the wings-up extreme, the actual flap
    # — scores 0.66 and gets thrown away. Losing a quarter of a short cycle
    # costs far more than keeping one odd pose.
    if n < 6:
        return list(range(n))

    best = [max(iou(masks[i], masks[j]) for j in range(n) if j != i) for i in range(n)]
    med = float(np.median(best))
    if med <= 0:
        return list(range(n))

    return [i for i, v in enumerate(best) if v >= floor * med]


def iou(a: np.ndarray, b: np.ndarray) -> float:
    u = (a | b).sum()
    return float((a & b).sum() / u) if u else 0.0


def stabilise(frames: list[Image.Image], reorder: bool) -> tuple[list[Image.Image], dict]:
    """
    Turn a pile of independently-drawn poses into something that reads as one
    animation.

    The generator produced each frame separately, so they disagree on scale and
    position. Played back that reads as jitter, not motion. Three passes fix it:

      1. normalise scale by ink area, so the character stops pulsing,
      2. align frames to each other by cross-correlation rather than by bounding
         box (a bbox includes the tail, which flails, so bbox alignment actively
         injects jitter),
      3. optionally reorder into the smoothest cycle — legitimate only where the
         source genuinely isn't a sequence, so there is no true order to lose.
    """
    stats: dict = {}

    areas = [float(mask_of(f).sum()) for f in frames]
    target = float(np.median(areas))
    stats["area_ratio_before"] = max(areas) / max(min(areas), 1)

    scaled = []
    for f, a in zip(frames, areas):
        k = float(np.sqrt(target / max(a, 1.0)))
        k = min(max(k, 0.80), 1.25)
        if abs(k - 1.0) > 0.01:
            f = f.resize((max(1, round(f.width * k)), max(1, round(f.height * k))), Image.LANCZOS)
        scaled.append(f)

    side = max(max(f.width, f.height) for f in scaled) + 28
    stacks = []
    for f in scaled:
        m = mask_of(f)
        ys, xs = np.where(m)
        cy, cx = (ys.mean(), xs.mean()) if len(ys) else (f.height / 2, f.width / 2)

        canvas = np.zeros((side, side, 4), np.uint8)
        oy, ox = int(round(side / 2 - cy)), int(round(side / 2 - cx))
        oy = max(0, min(side - f.height, oy))
        ox = max(0, min(side - f.width, ox))
        canvas[oy:oy + f.height, ox:ox + f.width] = np.asarray(f)
        stacks.append(canvas)

    for _ in range(2):
        masks = [s[..., 3] > 40 for s in stacks]
        ref = np.mean([m.astype(float) for m in masks], axis=0) > 0.35

        moved = []
        for s, m in zip(stacks, masks):
            best, best_v = (0, 0), -1.0
            for dy in range(-10, 11, 2):
                for dx in range(-10, 11, 2):
                    v = iou(np.roll(np.roll(m, dy, 0), dx, 1), ref)
                    if v > best_v:
                        best_v, best = v, (dy, dx)
            dy, dx = best
            moved.append(np.roll(np.roll(s, dy, 0), dx, 1) if (dy or dx) else s)
        stacks = moved

    masks = [s[..., 3] > 40 for s in stacks]
    n = len(stacks)
    order = list(range(n))

    def cycle_cost(o: list[int]) -> float:
        return sum(1 - iou(masks[o[i]], masks[o[(i + 1) % len(o)]]) for i in range(len(o)))

    stats["cost_before"] = cycle_cost(order)

    if reorder and n > 3:
        # Greedy nearest-neighbour, then 2-opt. n <= 14, so this is instant.
        remaining = set(range(1, n))
        greedy = [0]
        while remaining:
            last = greedy[-1]
            nxt = max(remaining, key=lambda j: iou(masks[last], masks[j]))
            greedy.append(nxt)
            remaining.remove(nxt)

        improved = True
        while improved:
            improved = False
            for i in range(1, n - 1):
                for j in range(i + 1, n):
                    cand = greedy[:i] + greedy[i:j + 1][::-1] + greedy[j + 1:]
                    if cycle_cost(cand) < cycle_cost(greedy) - 1e-9:
                        greedy, improved = cand, True
        order = greedy

    stats["cost_after"] = cycle_cost(order)
    ious = [iou(masks[order[i]], masks[order[(i + 1) % n]]) for i in range(n)]
    stats["iou_mean"] = float(np.mean(ious))
    stats["iou_min"] = float(np.min(ious))

    out = [Image.fromarray(stacks[i], "RGBA") for i in order]
    return out, stats


# ------------------------------------------------------------------ app icon

def write_app_icon(frame: Image.Image, path: Path) -> Path:
    """Build a multi-resolution .ico for the exe and window from one frame."""
    trimmed = frame.crop(frame.getbbox() or (0, 0, frame.width, frame.height))

    # Pillow silently drops any requested size larger than the source, and a
    # source frame is only ~110px — so without this upscale the file ships
    # without its 128 and 256 entries and Explorer's large views look blurry.
    side = max(256, max(trimmed.size))
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(trimmed, ((side - trimmed.width) // 2, (side - trimmed.height) // 2))

    sizes = [(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    square.save(path, sizes=sizes)

    written = set(Image.open(path).info["sizes"])
    if written != set(sizes):
        raise SystemExit(f"app.ico missing sizes: {sorted(set(sizes) - written)}")

    return path


# ------------------------------------------------------------------ grid sheets

class Panel:
    """
    One animation on a contact sheet: where its frames are, and how many.

    `boxes` is a list of (x0, x1, y0, y1, count) — an animation can be spread
    over several rows, and on these sheets it usually is. Each box must sit
    *inside* the checkerboard: the background is found by flooding inwards from
    the box border, so a border sitting on a title gives the flood nothing to
    start from and the whole row comes out as one giant "character".
    """

    def __init__(self, name: str, boxes: list[tuple[int, int, int, int, int]],
                 anchor: str = "centre"):
        self.name = name
        self.boxes = boxes
        self.anchor = anchor


class Sheet:
    """
    A whole sheet, and the handful of things that differ between them.

    `lettering` is the one that needs care. Some sheets print titles and frame
    numbers over the artwork, where only colour can separate them; others put
    them on their own rows, where geometry can. Reach for the colour route only
    when the character has no pale grey of its own — see drop_text.
    """

    def __init__(self, character: str, source: Path, size: tuple[int, int],
                 panels: list[Panel], reorder: set[str],
                 merge: dict[str, str] | None = None, lettering: bool = False,
                 outlier_floor: float = 0.80):
        self.character = character
        self.source = source
        self.size = size
        self.panels = panels
        self.reorder = reorder
        self.merge = merge or {}
        self.lettering = lettering

        # How far below its animation's median a frame may score before it is
        # treated as a mistake rather than a pose. The default separates
        # cleanly on a compact character — real frames land at 0.90+ of the
        # median and a defective one at 0.60. A character with a large moving
        # appendage has no such gap: Mew's tail whips right across the frame,
        # which depresses every silhouette comparison, and at the default floor
        # five perfectly good poses get thrown away. Loosen it there, and only
        # there.
        self.outlier_floor = outlier_floor


def _extract_box(rgb: np.ndarray, box, label: str, lettering: bool) -> list[Image.Image] | None:
    x0, x1, y0, y1, expected = box
    win = rgb[y0:y1, x0:x1]

    tones = checker_tones(win)
    if tones is None:
        print(f"{label}: found no checkerboard along this row's border — "
              f"the box has drifted, or the sheet's background has changed")
        return None

    bg = background_mask(win, tones)

    # `& ~bg` alongside the flood: the flood cannot reach a pocket of
    # checkerboard sealed by the character — between a wing and the body — and
    # that patch would ship as a square of grey tiling stuck to the sprite.
    fg = decolour(despeckle(~flood_background(bg) & ~bg), win)

    if lettering:
        # Order matters. The lettering is white with a dark outline, and the
        # dark part is neither bright enough for drop_text nor detached enough
        # for decolour — while the fill is there, outline and character are one
        # region. Take the fill out first, then the colour test can finally see
        # the leftover outline. The other way round, sprites ship wearing a
        # hollow stencil of the panel title.
        fg = despeckle(decolour(drop_text(fg, win), win), min_size=120)

    runs = frame_columns(fg, expected)
    if len(runs) != expected:
        print(f"{label}: split into {len(runs)} frames, expected {expected}")
        return None

    masks = frame_masks(fg, runs)
    if masks is None:
        print(f"{label}: could not assign a character to every frame")
        return None

    frames = []
    for i, m in enumerate(masks):
        f = cut_frame(win, m)
        if f is None:
            print(f"{label}: no character in frame {i + 1}")
            return None
        frames.append(f)

    return frames


def run_grid(sheet: Sheet, out_root: Path, size: int = 96, qa: Path | None = None) -> int:
    """Cut a whole sheet into `Assets/sprites/<character>/<animation>/NN.png`."""
    if not sheet.source.exists():
        print(f"source not found: {sheet.source}")
        return 1

    rgb = np.asarray(Image.open(sheet.source).convert("RGB")).astype(int)

    # Wrong coordinates do not fail, they find *some* ink and produce a pile of
    # plausible-looking garbage. This turns that into a loud failure.
    if (rgb.shape[1], rgb.shape[0]) != sheet.size:
        print(f"source is {rgb.shape[1]}x{rgb.shape[0]}, expected "
              f"{sheet.size[0]}x{sheet.size[1]} — the coordinates no longer apply")
        return 1

    gathered: dict[str, tuple[str, list[Image.Image]]] = {}

    for panel in sheet.panels:
        frames: list[Image.Image] = []
        for i, box in enumerate(panel.boxes):
            got = _extract_box(rgb, box, f"{panel.name} box {i}", sheet.lettering)
            if got is None:
                return 1
            frames.extend(got)

        dest = sheet.merge.get(panel.name, panel.name)
        if dest in gathered:
            gathered[dest][1].extend(frames)
        else:
            gathered[dest] = (panel.anchor, frames)

    collected = []
    for name, (anchor, frames) in gathered.items():
        # A pose resembling nothing else in its own animation is the generator
        # having drawn something odd — see typical_frames.
        probe, _ = stabilise(frames, reorder=False)
        keep = typical_frames(probe, sheet.outlier_floor)
        if len(keep) < len(frames):
            dropped = [i + 1 for i in range(len(frames)) if i not in keep]
            print(f"{name}: dropped frame {dropped} — matches nothing else in the row")
            frames = [frames[i] for i in keep]

        frames, st = stabilise(frames, reorder=(name in sheet.reorder))
        print(f"{name}: {len(frames)} frames, area spread "
              f"{st['area_ratio_before']:.2f}x -> 1.00x, "
              f"IoU mean {st['iou_mean']:.2f} min {st['iou_min']:.2f}"
              + (f", cycle cost {st['cost_before']:.2f} -> {st['cost_after']:.2f}"
                 if name in sheet.reorder else ""))
        collected.append((name, anchor, frames))

    # ---- one scale, one canvas ------------------------------------------
    #
    # Normalise on ink area rather than bounding box: a spread wing or a flung
    # tail swings the box far more than it changes the size of the animal, so
    # box-based sizing shrinks the character every time it moves.
    medians = {n: float(np.median([mask_of(f).sum() for f in fr])) for n, _, fr in collected}
    target = float(np.median(list(medians.values())))

    normalised = []
    for name, anchor, frames in collected:
        k = float(np.sqrt(target / max(medians[name], 1.0)))
        if abs(k - 1.0) > 0.01:
            frames = [f.resize((max(1, round(f.width * k)), max(1, round(f.height * k))),
                               Image.LANCZOS) for f in frames]
            print(f"{name}: body scaled {k:.2f}x to match the other animations")
        normalised.append((name, anchor, frames))

    # Never clip to keep the body large: on the first sheet that cut the ears
    # off most of a cycle. Control on-screen size with `mascotSize` instead.
    extents = []
    for _, _, frames in normalised:
        for f in frames:
            b = f.getbbox()
            if b:
                extents.append(max(b[2] - b[0], b[3] - b[1]))
    side = int(max(extents)) + 8

    qa_rows = []

    for name, anchor, frames in normalised:
        dest = out_root / sheet.character / name
        dest.mkdir(parents=True, exist_ok=True)
        for old in dest.glob("*.png"):
            old.unlink()

        # Anchor the group once, from its shared bounding box, so every frame of
        # this animation shifts together and the stabilised alignment survives.
        boxes = [f.getbbox() for f in frames if f.getbbox()]
        gx0 = min(b[0] for b in boxes); gy0 = min(b[1] for b in boxes)
        gx1 = max(b[2] for b in boxes); gy1 = max(b[3] for b in boxes)

        pad = 4
        ox = (side - (gx1 - gx0)) // 2 - gx0
        oy = ((side - (gy1 - gy0)) // 2 - gy0 if anchor == "centre"
              else (pad - gy0) if anchor == "top"
              else (side - pad - (gy1 - gy0)) - gy0)

        scaled = []
        for i, f in enumerate(frames, 1):
            canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
            canvas.paste(f, (ox, oy))
            canvas = canvas.resize((size, size), Image.LANCZOS)
            canvas.save(dest / f"{i:02d}.png")
            scaled.append(canvas)

        print(f"{name}: shared canvas {side}x{side} -> {size}px, anchor {anchor}")
        qa_rows.append((name, scaled))

    if qa and qa_rows:
        write_qa_sheet(qa_rows, size, qa)

    return 0


def write_qa_sheet(rows, size: int, path: Path) -> None:
    """
    Every frame over both a light and a dark backdrop.

    Both, because a dark halo is invisible against a dark backdrop and a light
    one is invisible against a light backdrop — checking on either alone is how
    a fringe ships.
    """
    cols = max(len(r) for _, r in rows)
    gap = 8
    sheet = Image.new("RGBA", (cols * size, len(rows) * (size + gap)), (255, 255, 255, 255))
    px = sheet.load()
    for y in range(sheet.height):
        for x in range(cols * size):
            if (x // 12 + y // 12) % 2 == 0:
                px[x, y] = (208, 208, 208, 255)
            if y % (size + gap) > size // 2:
                px[x, y] = (24, 24, 28, 255) if (x // 12 + y // 12) % 2 == 0 else (44, 44, 50, 255)

    for r, (_, row) in enumerate(rows):
        for c, img in enumerate(row):
            sheet.alpha_composite(img, (c * size, r * (size + gap)))

    sheet.save(path)
    print(f"QA sheet -> {path}  (top half of each row = light bg, bottom = dark)")
