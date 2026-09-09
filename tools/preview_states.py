#!/usr/bin/env python3
"""
Compose a layout preview of the mascot window in each state.

This is NOT a screenshot — the app only runs on Windows and this repo is built
on Linux. It re-draws the same sprite frames at the same coordinates the XAML
uses, so it shows composition and state mapping, not real rendering. Fonts,
anti-aliasing and the drop shadow will differ from the real thing.

Coordinates are duplicated from UI/MascotWindow.xaml; if you move things there,
this will drift. It exists to sanity-check the state->animation mapping.

Usage:  .venv/bin/python tools/preview_states.py
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SPRITES = ROOT / "Assets" / "sprites"
OUT = ROOT / "Assets" / "preview-states.png"

W, H = 320, 215
SPRITE_BOX = (198, 92, 112, 112)     # Canvas.Left, Top, Width, Height
BADGE = (292, 96, 18)                # Left, Top, diameter
BUBBLE = (6, 6, 236)                 # Left, Top, Width

# state label, animation, frame index, badge colour, bubble title, bubble body
STATES = [
    ("Connected", "idle", 0, "#4ADE80", None, None),
    ("Starting", "run", 3, "#38BDF8", "Workspace starting", "Agent is connecting…"),
    ("AutoStopSoon", "run", 7, "#FBBF24", "Auto-stop coming up",
     "Coder will auto-stop this workspace in 12 min."),
    ("AgentLost", "hang", 5, "#EF4444", "Workspace agent lost",
     "Agent disconnected — sessions are unreachable."),
]


def font(size: int) -> ImageFont.ImageFont:
    try:
        return ImageFont.load_default(size=size)
    except TypeError:                       # very old Pillow
        return ImageFont.load_default()


def wrap(text: str, f: ImageFont.ImageFont, width: int) -> list[str]:
    """Greedy wrap — WPF does this itself via TextWrapping="Wrap"."""
    lines, cur = [], ""
    for word in text.split():
        trial = f"{cur} {word}".strip()
        if cur and f.getlength(trial) > width:
            lines.append(cur)
            cur = word
        else:
            cur = trial
    if cur:
        lines.append(cur)
    return lines


def panel(anim: str, frame: int, badge: str, title: str | None, body: str | None) -> Image.Image:
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)

    if title:
        x, y, bw = BUBBLE
        d.rounded_rectangle([x, y, x + bw, y + 58], radius=12,
                            fill=(32, 37, 49, 242), outline=(255, 255, 255, 77))
        d.text((x + 12, y + 10), title, font=font(13), fill=(255, 255, 255, 255))
        if body:
            f = font(11)
            for i, line in enumerate(wrap(body, f, bw - 24)):
                d.text((x + 12, y + 28 + i * 14), line, font=f, fill=(199, 206, 219, 255))

    sx, sy, sw, sh = SPRITE_BOX
    path = SPRITES / anim / f"{frame + 1:02d}.png"
    if not path.exists():
        raise SystemExit(f"missing {path} — re-run tools/extract_sprites.py")

    sprite = Image.open(path).convert("RGBA").resize((sw, sh), Image.LANCZOS)
    im.alpha_composite(sprite, (sx, sy))

    bx, by, bd = BADGE
    rgb = tuple(int(badge[i:i + 2], 16) for i in (1, 3, 5))
    d.ellipse([bx, by, bx + bd, by + bd], fill=rgb + (255,), outline=(0, 0, 0, 89), width=2)

    return im


def main() -> int:
    if not SPRITES.exists():
        print("run tools/extract_sprites.py first")
        return 1

    pad = 12
    sheet = Image.new("RGBA", (len(STATES) * (W + pad) + pad, H + 46), (238, 240, 244, 255))
    d = ImageDraw.Draw(sheet)

    for i, (label, anim, frame, badge, title, body) in enumerate(STATES):
        x = pad + i * (W + pad)
        d.rectangle([x, 34, x + W, 34 + H], fill=(226, 230, 238, 255))
        d.text((x, 12), f"{label}  ->  {anim}", font=font(14), fill=(30, 34, 44, 255))
        sheet.alpha_composite(panel(anim, frame, badge, title, body), (x, 34))

    sheet.save(OUT)
    print(f"preview -> {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
