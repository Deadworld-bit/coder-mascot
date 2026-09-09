#!/usr/bin/env node
/**
 * Aggregate the per-session state files into one small JSON summary.
 *
 * The desktop mascot runs this over `coder ssh` and reads stdout, so this is
 * the wire format. Keep it small and keep it stable.
 *
 * Usage:  node mascot-summary.js [toolStaleSeconds] [thinkingStaleSeconds]
 */

const fs = require("fs");
const os = require("os");
const path = require("path");

const DIR = path.join(os.homedir(), ".claude", "mascot", "sessions");

// How long a session may sit silent before we call it stalled — two numbers,
// because the two kinds of silence mean opposite things.
//
// With a tool in flight, silence is normal: a build or a test run legitimately
// produces no hook events for many minutes, so be generous. While Claude is
// *thinking*, silence is a model call that is either slow or dead, and one
// number safe for a 15-minute build is far too patient to catch that. Using
// the generous number for both is a large part of why a real hang went
// unreported.
const TOOL_STALE = Math.max(60, parseInt(process.argv[2] || "600", 10) || 600);
const THINK_STALE = Math.max(30, parseInt(process.argv[3] || "180", 10) || 180);

// Drop sessions we haven't heard from in this long — a crashed Claude never
// fires SessionEnd, and its file would otherwise linger forever.
const ABANDONED = 24 * 60 * 60;

// Never return an unbounded list; the desktop only shows one line anyway.
const MAX_SESSIONS = 20;

/** When this container started; see the same function in mascot-hook.js. */
function bootAt() {
  try {
    const btime = Number(/^btime (\d+)$/m.exec(fs.readFileSync("/proc/stat", "utf8"))[1]);
    const raw = fs.readFileSync("/proc/1/stat", "utf8");
    const ticks = Number(raw.slice(raw.lastIndexOf(")") + 2).split(" ")[19]);
    if (!Number.isFinite(btime) || !Number.isFinite(ticks)) return null;
    return btime + Math.floor(ticks / 100);
  } catch {
    return null;
  }
}

const BOOT = bootAt();

/**
 * Was this record written by the workspace that is running now?
 *
 * A record with no stamp, or one written on a platform we can't read a boot
 * time from, counts as current. Being wrong in that direction costs one stale
 * record surviving to the 24h cutoff; being wrong in the other direction hides
 * live sessions — a watchdog reporting all-clear because it threw the evidence
 * away. A few seconds of slack absorbs clock jitter.
 */
function fromThisBoot(r) {
  if (BOOT === null || r.bootAt == null) return true;
  return Math.abs(r.bootAt - BOOT) <= 5;
}

function main() {
  const now = Math.floor(Date.now() / 1000);
  const sessions = [];

  let names = [];
  try {
    names = fs.readdirSync(DIR).filter((f) => f.endsWith(".json"));
  } catch {
    // No directory yet: the hook has never run. That's a valid empty answer,
    // not an error — say so explicitly so the mascot can tell the difference
    // between "no sessions" and "not installed".
    process.stdout.write(JSON.stringify({ ok: true, installed: false, now, sessions: [] }));
    return;
  }

  for (const name of names) {
    try {
      const r = JSON.parse(fs.readFileSync(path.join(DIR, name), "utf8"));
      const age = now - (r.at || 0);
      if (age > ABANDONED) continue;

      // A workspace stop/start leaves every file behind while no Claude
      // survives it. Reporting those verbatim means the first thing the user
      // sees after every reconnect is a fabricated "Claude needs you" — from
      // the very app whose job is to tell them about reconnects.
      if (!fromThisBoot(r)) continue;

      // Records written before `phase` existed have none. Treat those as the
      // patient case, so an upgrade in progress can't invent stalls.
      const patience = r.phase === "thinking" ? THINK_STALE : TOOL_STALE;

      let status = r.status;
      if (status === "running" && age > patience) {
        status = "stalled";
      } else if (status === "waiting" && age > TOOL_STALE * 3) {
        // A prompt nobody ever answered, on a session that's since gone.
        // Left alone this pins the mascot at "needs you" for a full day.
        continue;
      }

      sessions.push({
        id: r.id,
        status,
        phase: r.phase || null,
        tool: r.tool || null,
        cwd: r.cwd ? path.basename(r.cwd).slice(0, 60) : null,
        message: r.message || null,
        idleFor: age,
        // How long the turn that just ended took. Only meaningful once the
        // session goes idle; while it's still running the desktop shows elapsed
        // time itself rather than waiting 30s for the next poll to say so.
        ranFor:
          r.runSince && r.at >= r.runSince ? Math.min(r.at - r.runSince, ABANDONED) : null,
      });

      if (sessions.length >= MAX_SESSIONS) break;
    } catch {
      /* skip an unreadable or half-written file */
    }
  }

  const count = (s) => sessions.filter((x) => x.status === s).length;

  process.stdout.write(
    JSON.stringify({
      ok: true,
      installed: true,
      now,
      waiting: count("waiting"),
      running: count("running"),
      stalled: count("stalled"),
      idle: count("idle"),
      sessions,
    })
  );
}

main();
