#!/usr/bin/env bash
#
# Install the mascot's Claude Code hooks in this Coder workspace.
#
# Run this INSIDE the workspace (not on your desktop):
#     bash ~/workspace/projects/coder-mascot/workspace/install-hooks.sh
#
# It copies two scripts to ~/.claude/mascot/ and registers four hooks in
# ~/.claude/settings.json. It is idempotent, it merges rather than replaces (any
# hooks you already have are kept), and it writes a timestamped backup first.
#
# To undo:  bash install-hooks.sh --uninstall
set -euo pipefail

DEST="$HOME/.claude/mascot"
SETTINGS="$HOME/.claude/settings.json"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOOK="$DEST/mascot-hook.js"

# Hooks that tell the mascot what a session is doing.
#
# Notification is the one that matters most: Claude Code fires it when it wants
# your confirmation. UserPromptSubmit matters nearly as much and is easy to miss
# — without it a session that hangs before its first tool call never leaves
# "idle", and the stall detection has nothing to fire on.
EVENTS=(SessionStart UserPromptSubmit PreToolUse PostToolUse Notification Stop SubagentStop SessionEnd)

uninstall=false
[[ "${1:-}" == "--uninstall" ]] && uninstall=true

command -v node >/dev/null || { echo "node not found — required for the hooks"; exit 1; }
command -v python3 >/dev/null || { echo "python3 not found — required to edit settings.json"; exit 1; }

if [[ -f "$SETTINGS" ]]; then
  backup="$SETTINGS.bak.$(date +%Y%m%d-%H%M%S)"
  # -p: settings.json may be 0600 and can hold env secrets; don't widen it.
  cp -p "$SETTINGS" "$backup"
  echo "backed up settings.json -> $backup"
fi

if $uninstall; then
  # Remove the scripts before touching settings.json. If settings.json turns
  # out to be malformed, python exits non-zero, set -e aborts, and the user
  # would otherwise be unable to uninstall until they hand-fix it.
  rm -rf "$DEST"
  echo "removed $DEST"
else
  # 700: the session records carry project paths and Claude's prompt text.
  mkdir -p -m 700 "$DEST"
  cp "$SRC/mascot-hook.js" "$SRC/mascot-summary.js" "$DEST/"
  echo "installed scripts -> $DEST"
fi

UNINSTALL="$uninstall" HOOK="$HOOK" SETTINGS="$SETTINGS" EVENTS="${EVENTS[*]}" python3 <<'PY'
import json, os, stat, sys

path = os.environ["SETTINGS"]
hook = os.environ["HOOK"]
remove = os.environ["UNINSTALL"] == "true"
events = os.environ["EVENTS"].split()

try:
    with open(path) as f:
        cfg = json.load(f)
except FileNotFoundError:
    cfg = {}
except json.JSONDecodeError as e:
    sys.exit(f"settings.json is not valid JSON ({e}) — fix it before running this")

hooks = cfg.setdefault("hooks", {})
changed = 0

for event in events:
    entries = hooks.setdefault(event, [])

    # Drop any previous version of our hook, so re-running never duplicates it.
    for entry in entries:
        before = len(entry.get("hooks", []))
        entry["hooks"] = [h for h in entry.get("hooks", [])
                          if "mascot-hook.js" not in str(h.get("command", ""))]
        changed += before - len(entry["hooks"])

    # Leave behind any group we just emptied — but keep other people's groups.
    entries[:] = [e for e in entries if e.get("hooks")]

    if not remove:
        entries.append({
            "matcher": "*",
            "hooks": [{"type": "command", "command": f'node "{hook}" {event}'}],
        })
        changed += 1

    if not entries:
        hooks.pop(event, None)

# Write-then-rename. This is the user's GLOBAL Claude config: a crash or a
# full disk partway through a truncating write would leave it empty, and the
# only recovery would be a .bak they'd have to know about.
#
# The temp file is created with the destination's own mode, and chmod'ed again
# because open() is subject to the umask. settings.json may be 0600 and hold API
# keys in its env block; os.replace carries the temp file's mode onto the
# destination, so a plain open(tmp, "w") would quietly publish those keys at
# 0644 to everything else running in the workspace.
try:
    mode = stat.S_IMODE(os.stat(path).st_mode)
except FileNotFoundError:
    mode = 0o600

tmp = path + ".tmp"
fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
with os.fdopen(fd, "w") as f:
    json.dump(cfg, f, indent=2)
    f.flush()
    os.fsync(f.fileno())
os.chmod(tmp, mode)
os.replace(tmp, path)

print(f"{'removed' if remove else 'registered'} {changed} hook entries in {path}")
PY

if $uninstall; then
  echo "Done. Restart any running Claude sessions for this to take effect."
else
  echo
  echo "Done. Restart any running Claude sessions to pick up the hooks."
  echo "Check it works:  node $DEST/mascot-summary.js"
fi
