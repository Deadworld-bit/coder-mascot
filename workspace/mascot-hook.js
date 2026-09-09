#!/usr/bin/env node
/**
 * Claude Code hook -> per-session state file, for the desktop mascot.
 *
 * Runs inside the Coder workspace on every hook event. Deliberately tiny and
 * fail-silent: this is registered on PreToolUse and PostToolUse, so anything
 * that throws here prints a stack trace to the user twice per tool call.
 *
 * One file per session rather than one shared file, because several Claude
 * sessions fire hooks concurrently and a read-modify-write on a shared file
 * would drop events. Nothing here ever needs a lock.
 *
 * Usage (from settings.json):  node mascot-hook.js <EventName>
 */

const fs = require("fs");
const os = require("os");
const path = require("path");

const DIR = path.join(os.homedir(), ".claude", "mascot", "sessions");

/** Everything user- or repo-controlled gets a hard cap before it's stored. */
const cap = (v, n) => (v == null ? null : String(v).slice(0, n) || null);

/**
 * Hook event -> what the session is doing, and whether it has a tool in flight.
 *
 * The phase is the whole point of the stall detection. Silence while a tool is
 * running is expected — a 15-minute build produces no events at all — but
 * silence while Claude is *thinking* is either a slow model call or a wedged
 * one, and waiting ten minutes to say so is waiting nine minutes too long.
 * The reader applies a different patience to each.
 */
function stateFor(event) {
  switch (event) {
    case "Notification":
      // Claude Code fires this when it wants the user: a permission prompt, or
      // it has been idle waiting for input. This is the "needs you" signal.
      return { status: "waiting", phase: null };

    case "UserPromptSubmit":
      // The turn has started and nothing has been called yet. Without this the
      // status stays "idle" from the previous Stop, and a session that hangs
      // before its first tool call — a dead API request, a wedged MCP server —
      // is never flagged at all. That is the gap that made a real hang
      // invisible: no tool ever ran, so no tool hook ever fired.
      return { status: "running", phase: "thinking" };

    case "PreToolUse":
      return { status: "running", phase: "tool" };

    case "PostToolUse":
    case "SubagentStop":
      // The tool is done; anything after this is Claude thinking again.
      return { status: "running", phase: "thinking" };

    case "Stop":
      // Finished responding — the turn is over and it's the user's move.
      return { status: "idle", phase: null };

    case "SessionStart":
      return { status: "idle", phase: null };

    default:
      return null;
  }
}

/**
 * When this container last started, as a unix timestamp.
 *
 * The problem being solved is that a workspace stop/start leaves every session
 * file behind while no Claude survives it — so on reconnect the mascot reports
 * a "Claude needs you" that nothing is behind. A record stamped with the boot
 * it was written under can be recognised as predating this one.
 *
 * Three approaches were tried and two were wrong, so they're worth naming:
 *
 *   - process.ppid is the hook's parent, but Claude runs hooks through a shell
 *     and whether that shell survives or execs itself away is shell- and
 *     platform-dependent. It is either Claude or a pid that is already dead,
 *     with no way to tell which — and reading it as "dead" silently discards a
 *     live session, which is a watchdog reporting all-clear.
 *   - Walking up the tree looking for a process named "claude" matches the
 *     shell instead, because the shell's own command line contains the path to
 *     this script, which lives under ~/.claude.
 *   - os.uptime() is the *host* kernel's, not the container's. In this
 *     workspace it reads 34 days across restarts that happened this morning.
 *
 * PID 1's start time is the container's own, needs no name matching, and can't
 * mistake a shell for anything. /proc/stat's btime is the host boot epoch and
 * /proc/1/stat field 22 is PID 1's start in clock ticks since that epoch, so
 * the sum is absolute and stable for the life of the container.
 */
function bootAt() {
  try {
    const btime = Number(/^btime (\d+)$/m.exec(fs.readFileSync("/proc/stat", "utf8"))[1]);
    const raw = fs.readFileSync("/proc/1/stat", "utf8");
    // comm is parenthesised and can contain spaces, so count from the last ')'.
    const ticks = Number(raw.slice(raw.lastIndexOf(")") + 2).split(" ")[19]);
    if (!Number.isFinite(btime) || !Number.isFinite(ticks)) return null;
    return btime + Math.floor(ticks / 100);
  } catch {
    // Not Linux, or no procfs. Recording nothing is the safe answer: the reader
    // treats an unknown boot as current, so an unrecognised platform costs a
    // stale-file check, not every session.
    return null;
  }
}

function record(event, data) {
  const id = String(data.session_id || process.env.CLAUDE_SESSION_ID || "unknown")
    .replace(/[^A-Za-z0-9_.-]/g, "_")
    .slice(0, 128) || "unknown";

  fs.mkdirSync(DIR, { recursive: true, mode: 0o700 });
  const file = path.join(DIR, `${id}.json`);

  if (event === "SessionEnd") {
    fs.rmSync(file, { force: true });
    return;
  }

  const next = stateFor(event);
  if (next === null) return;

  let prev = {};
  try {
    prev = JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    /* first event for this session */
  }

  const now = Math.floor(Date.now() / 1000);

  // Two ordering guards. Hook processes are independent and can finish out of
  // order, and a subagent's tool traffic shares the session id with the main
  // loop — without these, a session parked at a permission prompt gets its
  // "waiting" overwritten by a subagent's "running" and the whole point of the
  // feature is lost.
  if (prev.at && prev.at > now) return;
  if (prev.status === "waiting" && next.status === "running") return;

  // When the current turn began. Not the same as startedAt, which is the
  // session's first ever event: what you want to be told when Claude finishes
  // is how long *this* piece of work took, not how long the terminal has been
  // open. Only a transition into "running" restarts it, so the several hook
  // events within one turn don't keep resetting the clock.
  const runSince =
    next.status === "running" && prev.status !== "running"
      ? now
      : prev.runSince || null;

  const out = {
    id,
    status: next.status,
    phase: next.phase,
    event,
    tool: cap(data.tool_name, 120) || (next.phase === "tool" ? prev.tool : null) || null,
    cwd: cap(data.cwd, 200) || prev.cwd || null,
    message: event === "Notification" ? cap(data.message, 200) : null,
    at: now,
    startedAt: prev.startedAt || now,
    runSince,
    // Which run of the workspace this was written under, so files left behind
    // by a restart can be told from live sessions.
    bootAt: bootAt(),
  };

  // Write-then-rename so a reader never sees a half-written file. 0600: these
  // records carry project paths and Claude's prompt text.
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(out), { mode: 0o600 });
  fs.renameSync(tmp, file);
}

function main() {
  const event = process.argv[2] || "unknown";
  let input = "";

  // No error listener means an EPIPE on stdin throws "Unhandled 'error' event"
  // and prints a stack trace into the user's session.
  process.stdin.on("error", () => process.exit(0));
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (c) => (input += c));

  process.stdin.on("end", () => {
    try {
      let data = {};
      try {
        data = JSON.parse(input) || {};
      } catch {
        /* a malformed payload still tells us the session is alive */
      }
      record(event, data);
    } catch {
      /* never let a status update break the session it is reporting on */
    }
    process.exitCode = 0;
  });

  // If stdin never closes, don't hang the hook forever.
  setTimeout(() => process.exit(0), 4000).unref();
}

main();
