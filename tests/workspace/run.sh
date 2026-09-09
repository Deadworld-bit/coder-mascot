#!/usr/bin/env bash
#
# Tests for the workspace half of the session watch: the Claude Code hook and
# the summary reader it writes for.
#
#     bash tests/workspace/run.sh
#
# Runs against a throwaway HOME, so it never touches your real ~/.claude.
set -u

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../workspace" && pwd)"
WORK="$(mktemp -d -t mascot-tests.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

export HOME="$WORK/home"
mkdir -p "$HOME"

pass=0
fail=0

ok() {
  if [ "$2" = "$3" ]; then
    echo "PASS  $1"
    pass=$((pass + 1))
  else
    echo "FAIL  $1"
    echo "        want: $3"
    echo "        got:  $2"
    fail=$((fail + 1))
  fi
}

fire() { echo "$2" | node "$SRC/mascot-hook.js" "$1" >/dev/null 2>&1; }
sum() { node "$SRC/mascot-summary.js" "$@"; }

# Pull one value out of the summary JSON. The expression is evaluated against
# the parsed object, so tests can ask for e.g. sessions.find(...).status.
J() {
  node -e '
    let s = "";
    process.stdin.on("data", (d) => (s += d)).on("end", () => {
      const o = JSON.parse(s);
      console.log(eval("o." + process.argv[1]));
    });' "$1"
}

# Move a record back in time, to test the staleness rules without waiting.
age() {
  node -e '
    const fs = require("fs");
    const f = `${process.env.HOME}/.claude/mascot/sessions/${process.argv[1]}.json`;
    const r = JSON.parse(fs.readFileSync(f));
    r.at -= Number(process.argv[2]);
    fs.writeFileSync(f, JSON.stringify(r));' "$1" "$2"
}

echo "== a hang while thinking =="
# The reported bug: a session that hangs before its first tool call. No tool
# hook ever fires, so under the old rules it stayed "idle" forever.
fire SessionStart '{"session_id":"t1"}'
fire UserPromptSubmit '{"session_id":"t1","cwd":"/home/coder/workspace/projects/AdminHub"}'
ok "UserPromptSubmit marks the session running" "$(sum | J 'running')" "1"
ok "  ...and records the thinking phase" "$(sum | J 'sessions[0].phase')" "thinking"

age t1 300
ok "5 min of thinking silence is stalled" "$(sum 600 180 | J 'stalled')" "1"
ok "  ...and one shared threshold missed it" "$(sum 600 600 | J 'stalled')" "0"

echo
echo "== a long tool is not a false alarm =="
# The opposite mistake: silence with a build running is normal, and reporting
# it is the false alarm that made a short threshold unusable in the first place.
fire PreToolUse '{"session_id":"t2","tool_name":"Bash","cwd":"/home/coder/workspace/projects/AdminHub"}'
age t2 300
ok "a 5 min build is still just running" \
  "$(sum 600 180 | J 'sessions.find(x=>x.id==="t2").status')" "running"
age t2 400
ok "an 11 min build finally stalls" \
  "$(sum 600 180 | J 'sessions.find(x=>x.id==="t2").status')" "stalled"
ok "  ...reported as the tool phase" \
  "$(sum 600 180 | J 'sessions.find(x=>x.id==="t2").phase')" "tool"

fire PostToolUse '{"session_id":"t2","tool_name":"Bash"}'
ok "PostToolUse returns to thinking" \
  "$(sum | J 'sessions.find(x=>x.id==="t2").phase')" "thinking"

echo
echo "== a prompt waiting for you =="
# A subagent's tool traffic shares the session id with the main loop, so without
# the ordering guard it overwrites the one signal the feature exists for.
fire Notification '{"session_id":"t3","message":"Claude needs your permission to run git push"}'
fire PreToolUse '{"session_id":"t3","tool_name":"Read"}'
ok "subagent traffic cannot erase a prompt" "$(sum | J 'waiting')" "1"
fire Stop '{"session_id":"t1"}'
ok "Stop returns the session to idle" \
  "$(sum | J 'sessions.find(x=>x.id==="t1").status')" "idle"

echo
echo "== records left by a workspace restart =="
# $HOME survives a workspace stop/start while no Claude does, so without this
# the first thing you see after every reconnect is a fabricated alert — from
# the app whose whole job is telling you about reconnects.
fire UserPromptSubmit '{"session_id":"t4"}'
ok "a record from this boot is reported" "$(sum | J 'sessions.some(x=>x.id==="t4")')" "true"

node -e '
  const fs = require("fs");
  const f = `${process.env.HOME}/.claude/mascot/sessions/t4.json`;
  const r = JSON.parse(fs.readFileSync(f));
  r.bootAt -= 3600;
  fs.writeFileSync(f, JSON.stringify(r));'
ok "a record from a previous boot is dropped" "$(sum | J 'sessions.some(x=>x.id==="t4")')" "false"

# Never the other way round: an unreadable boot time must not hide live work.
node -e '
  const fs = require("fs");
  const f = `${process.env.HOME}/.claude/mascot/sessions/t4.json`;
  const r = JSON.parse(fs.readFileSync(f));
  delete r.bootAt;
  fs.writeFileSync(f, JSON.stringify(r));'
ok "an unstamped record is kept" "$(sum | J 'sessions.some(x=>x.id==="t4")')" "true"

fire UserPromptSubmit '{"session_id":"t5"}'
ok "bootAt is recorded as a number" \
  "$(node -e 'console.log(typeof JSON.parse(require("fs").readFileSync(`${process.env.HOME}/.claude/mascot/sessions/t5.json`)).bootAt)')" \
  "number"

echo
echo "== how long the turn took =="
# The desktop announces "Claude finished — 12 min", so the turn has to be timed
# from where the *turn* started, not from where the session started. Pushing
# only the clocks back is how a 20-minute turn is simulated without waiting.
back() {
  node -e '
    const fs = require("fs");
    const f = `${process.env.HOME}/.claude/mascot/sessions/${process.argv[1]}.json`;
    const r = JSON.parse(fs.readFileSync(f));
    for (const k of process.argv.slice(3)) if (r[k]) r[k] -= Number(process.argv[2]);
    fs.writeFileSync(f, JSON.stringify(r));' "$@"
}

fire SessionStart '{"session_id":"t7","cwd":"/home/coder/workspace/projects/demo"}'
back t7 7200 startedAt          # the terminal has been open two hours
fire UserPromptSubmit '{"session_id":"t7"}'
back t7 1200 runSince           # ...and this turn began twenty minutes ago

fire PreToolUse '{"session_id":"t7","tool_name":"Bash"}'
fire PostToolUse '{"session_id":"t7","tool_name":"Bash"}'
fire Stop '{"session_id":"t7"}'

ok "a finished turn is idle" "$(sum | J 'sessions.find(x=>x.id==="t7").status')" "idle"
ok "the turn is timed, not the session" \
  "$(sum | J 'sessions.filter(x=>x.id==="t7").map(s=>s.ranFor>=1195&&s.ranFor<1300)[0]')" "true"

fire UserPromptSubmit '{"session_id":"t7"}'
ok "a new turn restarts the clock" \
  "$(sum | J 'sessions.find(x=>x.id==="t7").ranFor <= 1')" "true"

echo
echo "== hygiene =="
ok "records are 0600" "$(stat -c %a "$HOME/.claude/mascot/sessions/t5.json")" "600"
ok "sessions dir is 0700" "$(stat -c %a "$HOME/.claude/mascot/sessions")" "700"
ok "long cwd is capped" \
  "$(fire UserPromptSubmit "{\"session_id\":\"t6\",\"cwd\":\"$(printf 'x%.0s' {1..5000})\"}"; \
     node -e 'console.log(JSON.parse(require("fs").readFileSync(`${process.env.HOME}/.claude/mascot/sessions/t6.json`)).cwd.length)')" \
  "200"

# Registered on PreToolUse: anything printed here lands in the user's session.
ok "closed stdin prints nothing" "$(node "$SRC/mascot-hook.js" Stop </dev/null 2>&1)" ""

# settings.json can hold API keys in its env block, so a user who chmod 600'd it
# must get it back at 600. os.replace carries the temp file's mode across, and
# open() is subject to the umask — so this is easy to regress and invisible when
# it happens.
PERM="$WORK/perm"
mkdir -p "$PERM/.claude"
echo '{"env":{"SECRET":"x"}}' > "$PERM/.claude/settings.json"
chmod 600 "$PERM/.claude/settings.json"
HOME=$PERM bash "$SRC/install-hooks.sh" >/dev/null 2>&1
ok "installing hooks does not widen settings.json" \
  "$(stat -c %a "$PERM/.claude/settings.json")" "600"
ok "  ...and the hooks did get registered" \
  "$(node -e 'const c=require(process.argv[1]);console.log(Object.keys(c.hooks||{}).length>0)' \
       "$PERM/.claude/settings.json")" "true"
ok "  ...without dropping what was already in there" \
  "$(node -e 'const c=require(process.argv[1]);console.log(c.env.SECRET)' \
       "$PERM/.claude/settings.json")" "x"

# "Not installed" has to be distinguishable from "nothing is wrong".
EMPTY="$WORK/empty"
mkdir -p "$EMPTY"
ok "no hooks yet => installed:false" \
  "$(HOME=$EMPTY node "$SRC/mascot-summary.js" | J 'installed')" "false"

echo
if [ "$fail" = 0 ]; then
  echo "ALL PASS ($pass checks)"
else
  echo "$fail FAILED ($pass passed)"
fi
[ "$fail" = 0 ]
