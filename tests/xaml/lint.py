#!/usr/bin/env python3
"""
Checks on the XAML that the compiler cannot make.

    python3 tests/xaml/lint.py

Two faults live here, and both of them build cleanly and then throw on the
user's machine the first time a window is measured:

  1. A resource whose declared type is not the type the property needs. A
     Setter does *not* run a type converter on a value that came from a markup
     extension, so `<sys:String x:Key="FaceUi">` behind
     `Value="{StaticResource FaceUi}"` on a FontFamily reaches WPF as a String
     and throws InvalidCastException. Written inline the same text converts
     fine, which is what makes it so easy to write.

  2. A {StaticResource} key that is not in scope where it is used -- either
     misspelt, or in a dictionary that window forgot to merge. StaticResource
     resolves at load time, not build time, so this is also a run-time crash.

Both shipped once. See README, "Colour and spacing".
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# What each property needs. Only properties we actually point resources at --
# an incomplete map is silent, so it is checked against real use below.
WANTS = {
    "Background": "Brush", "BorderBrush": "Brush", "CaretBrush": "Brush",
    "Fill": "Brush", "Foreground": "Brush", "OpacityMask": "Brush",
    "SelectionBrush": "Brush", "Stroke": "Brush",
    "FontSize": "Double", "Height": "Double", "MaxHeight": "Double",
    "MaxWidth": "Double", "MinHeight": "Double", "MinWidth": "Double",
    "Opacity": "Double", "StrokeThickness": "Double", "Width": "Double",
    "BorderThickness": "Thickness", "Margin": "Thickness", "Padding": "Thickness",
    "CornerRadius": "CornerRadius",
    "FontFamily": "FontFamily",
    "Effect": "Effect", "ItemTemplate": "DataTemplate",
    "Style": "Style", "BasedOn": "Style", "Template": "ControlTemplate",
}

# What each declared element yields.
GIVES = {
    "SolidColorBrush": "Brush", "LinearGradientBrush": "Brush",
    "RadialGradientBrush": "Brush", "ImageBrush": "Brush",
    "Boolean": "Boolean", "Color": "Color", "CornerRadius": "CornerRadius",
    "DataTemplate": "DataTemplate", "Double": "Double",
    "DropShadowEffect": "Effect", "FontFamily": "FontFamily",
    "Int32": "Int32", "String": "String", "Style": "Style",
    "Thickness": "Thickness", "ControlTemplate": "ControlTemplate",
    "BitmapImage": "ImageSource", "PathGeometry": "Geometry",
    "StreamGeometry": "Geometry",
}

KEY = re.compile(r"<(?:\w+:)?(\w+)\b[^>]*?\bx:Key=\"([^\"]+)\"")
# A Style's own TargetType, and the key it is BasedOn -- a Style may only be
# based on one whose TargetType is the same or a base class. Mismatched, WPF
# throws at load time, not build time.
STYLE = re.compile(
    r"<Style\b(?=[^>]*?\bx:Key=\"(?P<key>[^\"]+)\")"
    r"(?=[^>]*?\bTargetType=\"(?:\{x:Type\s*)?(?:\w+:)?(?P<target>\w+))"
    r"[^>]*?\bBasedOn=\"\{StaticResource (?P<base>[^}\"]+)\}\""
)
MERGE = re.compile(r"<ResourceDictionary\b[^>]*?\bSource=\"([^\"]+)\"")
USE_ATTR = re.compile(r"\b([\w.]+)=\"\{StaticResource ([^}\"]+)\}\"")
USE_SETTER = re.compile(
    r"<Setter\b[^>]*?\bProperty=\"([\w.]+)\"[^>]*?\bValue=\"\{StaticResource ([^}\"]+)\}\""
)

problems = []
checked = 0


def xamls():
    for p in sorted(ROOT.rglob("*.xaml")):
        rel = p.relative_to(ROOT)
        if rel.parts[0] in ("bin", "obj"):
            continue
        yield rel, p.read_text(encoding="utf-8")


files = dict(xamls())

# key -> declared element, per file
declared = {rel: dict((k, t) for t, k in KEY.findall(s)) for rel, s in files.items()}


def resolve(rel):
    """Path of a Source= reference, which is relative to the referring file."""
    for src in MERGE.findall(files[rel]):
        if src.startswith("pack:") or ";component" in src:
            continue  # a dictionary out of another assembly (Wpf.Ui)
        # A leading "/" is root-relative -- the form the windows actually use --
        # not a filesystem absolute path.
        cand = (ROOT / src.lstrip("/")) if src.startswith("/") else (ROOT / rel).parent / src
        try:
            yield cand.resolve().relative_to(ROOT)
        except (ValueError, OSError):
            continue


def in_scope(rel, seen=None):
    """Keys a file can see: its own, its merges (transitively), and App.xaml's."""
    seen = seen if seen is not None else set()
    if rel in seen or rel not in files:
        return {}
    seen.add(rel)
    keys = dict(declared[rel])
    for dep in resolve(rel):
        keys.update(in_scope(dep, seen))
    return keys


APP = Path("App.xaml")
app_keys = in_scope(APP) if APP in files else {}

for rel, src in files.items():
    scope = dict(app_keys)
    scope.update(in_scope(rel))
    flat = re.sub(r"\s+", " ", src)

    uses = [(p, k.strip(), None) for p, k in USE_SETTER.findall(flat)]
    for line_no, line in enumerate(src.splitlines(), 1):
        for prop, k in USE_ATTR.findall(line):
            if prop != "Value":  # the Setter form is collected above
                uses.append((prop, k.strip(), line_no))

    for prop, key, where in uses:
        checked += 1
        at = f"{rel}:{where}" if where else f"{rel}"
        if key not in scope:
            problems.append(
                f"{at}: {prop}=\"{{StaticResource {key}}}\" -- key not in scope here. "
                f"Misspelt, or the dictionary that defines it is not merged."
            )
            continue
        tag = scope[key]
        want, give = WANTS.get(prop), GIVES.get(tag)
        if want and give and want != give:
            problems.append(
                f"{at}: {prop} needs a {want}, but {key} is declared <{tag}> ({give}). "
                f"A markup extension bypasses the type converter -- "
                f"InvalidCastException when the element is measured."
            )
        elif want and give is None:
            problems.append(f"{at}: {key} is <{tag}>, which this check does not know. "
                            f"Add it to GIVES so {prop} is actually verified.")
        elif want is None:
            problems.append(f"{at}: property {prop} is not in WANTS, so its resource "
                            f"type is unverified. Add it.")

# Style BasedOn: the base's TargetType must match. Only exact matches are
# accepted here -- every style in this project targets a concrete control, so a
# genuine base-class case would be a deliberate change worth reading.
targets = {}
for rel, src in files.items():
    for m in re.finditer(r"<Style\b(?=[^>]*?\bx:Key=\"([^\"]+)\")"
                         r"[^>]*?\bTargetType=\"(?:\{x:Type\s*)?(?:\w+:)?(\w+)", src):
        targets[m.group(1)] = m.group(2)

for rel, src in files.items():
    for m in STYLE.finditer(re.sub(r"\s+", " ", src)):
        key, target, base = m.group("key"), m.group("target"), m.group("base").strip()
        checked += 1
        if base in targets and targets[base] != target:
            problems.append(
                f"{rel}: style {key} targets {target} but is BasedOn {base}, "
                f"which targets {targets[base]} -- WPF throws when the "
                f"dictionary is loaded."
            )

print(f"{checked} resource uses checked across {len(files)} files")
for p in problems:
    print(f"FAIL  {p}")
if problems:
    print(f"\n{len(problems)} FAILED")
    sys.exit(1)
print("\nALL PASS")
