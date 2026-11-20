# Log sources — plan

Let the mascot read application logs that the user is allowed to see but the
mascot (and Claude) may not fetch from the server: a folder the user pastes log
files into, and later a log table reached by connection string.

## Status

| Phase | What | Status |
|---|---|---|
| 1 | Folder source, text/JSON parser, **Logs** page | Built and reviewed 23/10/2026 — **waiting for the PC checklist** (WPF can't run in the workspace) |
| 2 | SQL Server source (Serilog preset + custom column mapping, DPAPI) | Built and reviewed 23/10/2026 — **waiting for the PC checklist and `tests/LogDbTests`** |
| 3 | MongoDB source | Built and reviewed 23/10/2026 — **waiting for the PC checklist and `tests/LogDbTests`** |
| 2+3b | Table paging, server-side level filter and search; table poll every 5 minutes | Built, reviewed and fixed 23/10/2026 — **waiting for the PC checklist and `tests/LogDbTests`** |
| 4 | Alerts: new errors make the mascot speak up | Not started |

## Open questions (defaults taken — confirm or correct)

| # | Question | Default used until answered |
|---|---|---|
| Q1 | View/filter logs, alert on new errors, or both? | Both. Viewer first (P1), alerts last (P4). |
| Q2 | Real log samples per format | Formats read off our own repos' logging config (table below). Redacted samples still wanted for P2 table layouts. |
| Q3 | Can the PC reach the log databases, with a read-only login? | Unknown. Owner said "Continue" on 23/10, so P2/P3 are built on the assumption that the app (not Claude) may connect from the PC. Nothing connects to a database from the workspace. |
| Q4 | Other stores beyond SQL Server and MongoDB? | None until one is named. |
| Q5 | Where the drop folder lives | Any local folder **outside** the Mutagen-synced tree, so production logs never land on the shared Coder server. |

## Decisions

- **D1 — one entry shape.** Every source yields `LogEntry` (time, level, one-line
  message, detail, the text as written, file, line). The page never knows where
  an entry came from.
- **D2 — the folder is polled, not watched.** Every `logPollSeconds` (default 5)
  the folder is listed and a file is re-read only when its length, write time or
  creation time changed. `FileSystemWatcher` was the alternative; it misses events
  on buffer overflow, dies with a deleted-and-recreated folder, and a paste
  produces a burst of events anyway. A listing every few seconds costs nothing.
- **D3 — bounded reads.** Per file: the last 32 MB, the newest 10,000 entries and
  at most 16 M characters held, lines cut at 64 KB (marked `…`), a continuation
  (stack trace) or a JSON entry's detail cut at 500 lines / 32 KB, a rendered
  template at 33 K characters. Per folder: the 30 newest matching files,
  subfolders 3 levels deep, junctions and hidden/system files skipped, and
  reading stops once 64 MB of files, 50,000 entries or 32 M characters are in
  (older files are listed, not read). Across all folders: 96 M characters. Every
  cut is said on the page. *(Byte and character budgets added 23/10 after both
  reviews showed the entry count alone let a few huge entries hold gigabytes.)*
- **D4 — nothing from a log is written to disk.** Entries live in memory only;
  the clipboard gets an entry only when the user copies it. (The README's "holds
  no company data" no longer holds; reworded.)
- **D5 — drive-letter folders only.** No UNC shares (an offline share blocks the
  poll for the SMB timeout) and no drive roots (a recursive listing of `C:\` every
  few seconds). The form also refuses a mapped network drive (same reason) and
  warns about a OneDrive folder; no commas (Explorer splits its argument at them).
  A network drive hand-written into `config.json` still passes — accepted.
- **D6 — ambiguous dates read day-first** (`05/10/2026` = 5 October), the
  Vietnamese convention, unless the second number can't be a month
  (`10/13/2026` reads as US).
- **D7 — time-only lines** (`[10:15:30 INF] …`, Serilog's console template) take
  their date from the file name (`log-20261023.txt`); without one they keep their
  line as the message and have no time.
- **D11 — `docker logs -t`.** Docker stamps every line. When the text after its
  stamp is itself a header, the file is treated as a docker log: the service's
  time wins, docker's fills in when there is none, and every other stamped line is
  continuation (stored without docker's stamp). A plain ISO log never trips this.
- **D12 — a level word after the first word counts only when written as a level**
  (`[ERROR]`, `|INFO|`, `ERROR`), so "Call API error: timeout" stays Unknown with
  its message intact.
- **D13 — JSON entries:** the detail pane shows the rendered message, exception and
  properties; Copy gives the line as written. A JSON entry takes no continuation
  lines; a non-CLEF JSON line under a text entry is that entry's continuation.
- **D14 — log text never reaches the crash log.** A file that throws while being
  read becomes "Part of this file couldn't be read.", is cached under its stamp
  (not re-parsed every poll), and is logged once by exception type and stack only.
  One failing folder never freezes the others.
- **D15 — Copy keeps the entry out of Win+V history and the cloud clipboard**
  (`CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` = 0). Unverified
  until the PC check.
- **D8 — no databases run in the workspace** (owner, 07/10). P2/P3 tests are
  written here and run by the owner on the PC.
- **D9 (P2) — connection strings encrypted with DPAPI** (CurrentUser scope),
  masked in the UI, never logged.
- **D10 (P2) — polite reads:** read-only login, `SELECT` only, identifiers checked
  against the catalog and quoted, cursor on the identity `Id` (indexed), row cap,
  short timeout, `ApplicationIntent=ReadOnly`.

## Formats read in Phase 1

Taken from the logging config of our own services, not guessed:

| Source | Shape |
|---|---|
| eContract signserver | Serilog `CompactJsonFormatter` (CLEF): `{"@t":…,"@mt":…,"@l":…,"@x":…, props}` — the message is rendered from `@mt` + properties (`@r` for formatted tokens); absent `@l` = Information |
| mbi-zns-care | Serilog text: `2026-10-23 10:15:30.123 +07:00 [INF] message` |
| id-mbi-api | Custom: `2026-10-23 10:15:30.123 [Information] Category: message`, exception on following lines |
| Containers / console | `[10:15:30 INF] message`; ASP.NET `info: Category[0]` + indented message; `docker logs -t` (stamp on every line, see D11) |
| Others | NLog `…|INFO|logger|msg`, log4net `… [12] ERROR logger - msg`, Serilog `JsonFormatter`, generic JSON lines (`time`/`level`/`msg`, pino numbers), `dd/MM/yyyy HH:mm:ss` |
| Anything else | One entry per line, level guessed from a leading level word |

Encodings: UTF-8 (with or without BOM), UTF-16 LE/BE (BOM, or detected by
zero-byte pattern). CRLF or LF.

## Phase 1 — folder source + Logs page

- [x] `Core/LogModel.cs` — `LogSeverity`, `LogEntry`, `LogFileRead`, `LogFolderView` (23/10)
- [x] `Core/LogParse.cs` — header recognition, levels, CLEF/JSON, continuation, caps (23/10)
- [x] `Core/LogFile.cs` — bounded read, encoding sniff, tail window, line cap, date from name, folder listing (23/10)
- [x] `Core/LogSource.cs` — config entry, folder-path rule, normalisation (23/10)
- [x] `Core/LogMonitor.cs` — poll loop, per-file cache by stamp, merge across files (23/10)
- [x] `CoderConfig.logSources` / `logPollSeconds` + `SetLogSources`; `LogSource.cs` added to the 4 test projects that compile `CoderConfig.cs` (23/10)
- [x] `tests/LogTests` — Linux oracle, now **150 checks**; mutants killed: month-first dates, CLEF read by plain keys, `fromMiddle` ignored, any word as level, half emoji kept at a cut, inner throw guards removed (23/10). The outer catch in `Reader.Feed` is a backstop no test reaches — the inner guards stop every known input first.
- [x] App wiring (`LogMonitor` started/disposed with the other monitors) + `UI/ILogHost.cs` + `IDashboardHost.Logs` (23/10)
- [x] `UI/LogsPage` + `UI/LogSourceForm` drawer + nav entry (frontend agent, 23/10); `UI/DesktopActions.OpenFolder`; `tests/xaml/lint.py` WANTS += `ItemContainerStyle`
- [x] Release build 0 warnings / 0 errors; LogTests, MenuTests, DiagramTests, CaptureTests, CiTests, PerimeterTests, xaml lint all pass (23/10)
- [x] One code review + one security review (report-only, on a scratch git mirror), one fix pass (23/10) — see *Review outcome*
- [x] README: "Reading logs" chapter, page list, the "no company data" sentence reworded, LogTests in Tests (23/10)
- [x] dist exe — `dist/CoderMascot.exe` 23/10 10:53, sha256 `22CB4D16441642CE…`; previous (13/10, `1BF65F06…`) kept in `~/.cache/coder-mascot-logs/bak-2026-10-13/` (23/10)
- [ ] PC checklist below — owner
- [ ] Commit on the PC — owner (no git in the workspace; a re-sync eats uncommitted work)

### Review outcome (23/10)

Fixed: raw JSON shown instead of the parsed exception (code HIGH); one hostile
line — a `+14:30` zone, a lone surrogate from a cut emoji, an escaped `\uD800` —
freezing every folder and flooding crash.log (security HIGH); template token walls
multiplying a line into megabytes, JSON detail uncapped (security HIGH); memory
bounded by entries only (both, MEDIUM); `docker logs -t` split apart (code
MEDIUM); level words mid-sentence (code MEDIUM); thousands of `{…}` lines each
throwing (security MEDIUM); quadratic template scan; merge trimming silently;
nested sources sharing the file cache; stale listing stamps; selection/scroll
identity by line number; list keeping the old scroll offset; search missing the
first line; a failed config write reported as saved (`CoderConfig.Save` now
returns bool); renamed folder keeping the old default name; Open-folder failure
shown under another folder; control/bidi characters in list rows; commas in
folder paths; network drives and OneDrive in the form; screen-reader row text.

Accepted, not fixed: IIS W3C logs (UTC with no zone) read as local time; a file
swapped for a symlink between listing and reading (needs local write access);
forged times and levels inside a log are the service's log injection — the list
shows no year, so a future-dated line sits on top; the Logs page is on screen for
the workstation capture agent like any other window; a network drive hand-written
into the config. Pre-existing, not touched: a wrong-typed value in `config.json`
makes the whole file fall back to defaults, and the next save overwrites it.

### PC checklist (Phase 1)

With `dist/CoderMascot.exe` from 23/10:

1. **Logs** sits after Projects in the left nav with its icon; the badge counts errors.
2. **Add folder** → drawer. Browse works; a folder that doesn't exist is created on
   Save; the same folder twice is refused; a mapped network drive is refused; a
   OneDrive folder shows the warning.
3. Paste an eContract signserver `log-YYYYMMDD.txt` (CLEF) → entries within ~5 s.
   Pick an error: the pane shows the message, the exception with real line breaks
   and the properties; **Copy** puts the raw JSON line on the clipboard.
4. Paste an id-mbi-api `log-YYYYMMDD.txt` → levels and categories right, stack
   traces under their errors.
5. Filters: level, search (Ctrl+F; Esc clears), file, Newest/Oldest. Append to a
   pasted file while an entry is selected and the list is scrolled: selection and
   place are kept.
6. A file over 32 MB → the note "Only the last 32 MB of … was read".
7. Copy an entry, then Win+V: the entry is **not** in the clipboard history (D15).
8. **Open folder** opens Explorer on that folder. **Edit** keeps the list order;
   **Remove** asks in the form first.
9. Restart the app: the folders are remembered, the entries are re-read (nothing
   was stored).
10. Narrow the window below ~760 px: the folder pane narrows; below ~640 px of
    pane the search drops to its own line.

## Phases 2 + 3 — SQL Server and MongoDB tables

Built together: both are "a table read by connection string" and share one reader
contract, one row mapping and one monitor loop.

### Decisions

- **D16 — packages:** `Microsoft.Data.SqlClient` 7.0.0 (already used by AdminHub and
  id-mbi-api; unlike 5.2 it no longer drags in Azure.Identity/MSAL) and
  `MongoDB.Driver` 3.11.1. DPAPI comes with the Windows Desktop runtime.
- **D17 — connection strings are sealed with DPAPI** (CurrentUser, app-specific
  entropy) as `"connection": "dpapi:…"`. The form never shows a saved one again;
  editing means leaving it empty (keep) or pasting a new one. A plain-text
  connection string hand-written into the config is not used: the source shows
  "stored as plain text — paste it again in Edit". The display label (`host`:
  server / database) is stored in clear; it holds no credentials.
- **D18 — reads are SELECT/find only, polite and bounded.** SQL Server: identifiers
  checked against `INFORMATION_SCHEMA` and bracket-quoted, values parameterised,
  `WITH (NOLOCK)` so a reader never blocks the service writing its logs (a dirty
  read of a log row is harmless), long columns cut server-side
  (`LEFT(CAST(… AS nvarchar(max)), n)`), `TOP (n)`, connect ≤ 15 s, command 15 s,
  `ApplicationIntent=ReadOnly`, no T-SQL newer than SQL Server 2008 (pre-2016
  servers exist here). MongoDB: projection of the mapped fields, `maxTime` 15 s,
  server selection 10 s, `secondaryPreferred`.
- **D19 — incremental by id.** First read: the newest 1,000 rows. Then each poll
  (`logTablePollSeconds` — renamed `logTablePollMinutes` by D27: default 5
  minutes, was 60 seconds) reads rows after the last id in batches of 500 (at most
  4 per poll). The id must be a whole number (a scaled decimal is not) **and an
  identity or a one-column unique index**; otherwise *window* mode: the newest
  1,000 by time, replaced each poll — only when the time column leads an index and
  isn't text. Neither → refused with the reason ("would scan the whole table on
  every poll"). A view is trusted (its indexes can't be seen). MongoDB uses `_id`
  (ObjectId or number); window mode there needs an index on the time field.
  *(Index checks added 23/10 after the security review.)*
- **D24 — when the id goes wrong.** An empty incremental read is followed by a
  one-row "newest id" query; if that is below the cursor (TRUNCATE, reseed,
  recreated table) the table is read afresh and the page says so. Four full
  batches in one poll (> ~33 rows/s at 60 s) → skip to the newest 1,000 and say
  some rows were skipped. A cursor that doesn't parse reads the newest again.
  ObjectId cursors start 5 s early (same-second ObjectIds from different writer
  processes aren't ordered); rows held are skipped by key. Residual: writers whose
  clocks disagree by more than 5 s can still lose a row.
- **D25 — polite to a struggling server.** A failed read backs off: poll × 2ⁿ up
  to an hour, and the reader is reopened (its table shape may be stale). Showing
  the page or saving a folder wakes only the folders; **Reload** reads the table
  on screen early, at most once every 15 s. Up to 4 tables are read at a time, so
  one dead server holds up only its own table. All tables together hold at most
  32 M characters (8 M each when there are four or fewer).
- **D20 — per table:** 10,000 entries and 8 M characters held, oldest dropped
  first, said on the page.
- **D26 — connection strings that attach a database file (`AttachDBFilename`,
  `User Instance`) are refused** (a write). `TrustServerCertificate=True`, an
  `Encrypt` that isn't Mandatory/Strict, MongoDB without TLS or with `tlsInsecure`
  get a warning after Test connection — not a block (our own appsettings use
  `TrustServerCertificate=True`). `Connect Timeout=0` (no limit) becomes 15 s.
  The `host` label refuses a server name with `=`, `;`, `@` or a space in it (a
  missing `;` can glue `Password=…` onto it); a long one is cut, not replaced.
  MongoDB URLs are parsed with `new MongoUrl` — `MongoUrl.Create` caches every URL,
  password included, for the life of the process.
- **D21 — errors never carry secrets.** Readers translate driver exceptions into
  fixed sentences (MongoDB's can quote the whole connection string, password
  included); SQL Server messages are passed through with anything after
  `password=` / `pwd=` removed. The crash log gets type + stack only.
- **D22 — a failed poll keeps what was read** and says why above the list; a
  source that never read shows the reason instead.
- **D23 — tests:** pure parts (SQL text, quoting, column guessing, row mapping,
  monitor state machine with a fake reader) run on Linux. `tests/LogDbTests`
  exercises the real readers against servers named in environment variables —
  the owner runs it on the PC (D8).

### Column mapping

Roles: id, time, level, message, exception, properties, logger. Presets guessed from
column names (case-insensitive): Serilog MSSqlServer (`Id, TimeStamp, Level,
Message, Exception, Properties`), NLog/log4net (`Logged`/`Date`, `Logger`),
Serilog MongoDB (`Timestamp`/`UtcTimeStamp`, `RenderedMessage`, `Properties`),
and common custom names (`CreatedDate`, `Content`, `NgayTao`, `NoiDung`…). The
form shows the guess and lets every role be changed or left out. `utc`: the time
column holds UTC without saying so.

Properties: Serilog's XML (`<properties><property key='…'>`) and JSON (`LogEvent`,
Mongo sub-documents) become `key: value` lines under the entry.

### Checklist

- [x] Plan (this section) (23/10)
- [x] `Core/LogSource.cs` — table kinds, columns, sealed connection, normalisation (23/10)
- [x] `Core/LogTable.cs` — reader contract, row → entry, column guessing; `LogParse.TimeOf/LevelOf` (log4net's 10000–120000 levels too) (23/10)
- [x] `Core/LogSql.cs` — quoting, table names, catalog resolution, query text (23/10)
- [x] `Core/SqlLogReader.cs`, `Core/MongoLogReader.cs`, `Core/LogSecrets.cs` + `Core/DpapiSecretBox.cs`; packages in the csproj (23/10)
- [x] `Core/LogMonitor.cs` — table loop beside the folder loop, `Publish` composes both in config order (23/10)
- [x] `tests/LogTests` — **210 checks** pass (60 new: SQL text and quoting incl. a hostile column name, guesses for Serilog/NLog/log4net/hand-made/Mongo, row mapping incl. an XXE attempt, normalisation, the table loop with a fake reader); 3 more mutants killed (brackets not doubled, no dedupe, a failed read dropping entries) (23/10)
- [x] `tests/LogDbTests` — builds (0 warnings); refuses to run without `LOGTEST_SCRATCH=yes`; **not run** (owner, PC) (23/10)
- [x] App wiring (`OpenLogTable`, DPAPI box, `logTablePollSeconds` (renamed `logTablePollMinutes` by D27)) + `ILogHost.Seal/HostOf/ProbeAsync` (23/10)
- [x] README: "Reading a log table", LogDbTests usage (23/10)
- [x] UI: kind picker, connection (PasswordBox), table/columns, Test + preview; page differences for tables (frontend agent, 23/10)
- [x] Build 0 warnings; LogTests **232 checks**, MenuTests, DiagramTests, CaptureTests, CiTests, PerimeterTests, xaml lint pass; LogDbTests builds and refuses without the flag (23/10)
- [x] One code review + one security review (report-only, scratch mirror `~/.cache/coder-mascot-logs/p2-orig`), one fix pass; 6 more mutants killed (no reset, no skip-ahead, no Reload gap, no backoff, window compared with what is held, no date guard) (23/10)
- [x] README, dist exe — `dist/CoderMascot.exe` 23/10 12:00, sha256 `B33E6E25933DFD26…`, 157.2 MB (+10.2 MB for the two drivers); Phase 1 exe kept in `~/.cache/coder-mascot-logs/bak-2026-10-23-p1/` (23/10)
- [ ] `tests/LogDbTests` against scratch databases — owner, PC
- [ ] PC checklist below — owner
- [ ] Commit on the PC — owner

### Review outcome (23/10)

Code review: 0 HIGH, 4 MEDIUM — identity reset freezing a table, a backlog never
caught up, Reload/page/save forcing every table read, a find-only MongoDB login
unable to add its collection (`listCollections` refused) — all fixed. Security
review: 0 CRITICAL/HIGH, 2 MEDIUM — MongoDB results held whole in memory
(now streamed, batch of 100, each document cut as it arrives; the probe keeps field
names only) and impolite polling (now index checks, backoff, Reload-only forcing)
— both fixed. LOW, fixed: decimal(18,2) ids, 0001-01-01 times, window churn under
the size cap, window keys carrying whole messages, Mongo preview always UTC, table
names with dots, plain-text connections kept on Edit, Mongo database edits keeping
the old label and fields, long host labels, a preview not dimmed after a change
mid-probe, stale table shapes, `Connect Timeout=0`, the double Mongo query in
window mode, projection path collisions, renames restarting a read, tables read
one at a time, `@after` typed `decimal` against an int key, driver exceptions in
the crash log (now type + stack only), the owner test printing a bad URI, the
README putting the URI in shell history, a host label carrying text after a
missing `;`, `AttachDBFilename`. Accepted: connection-string TLS choices are the
user's (warned, not blocked); writers' clocks > 5 s apart on MongoDB; a view's
indexes can't be checked; a time column that is text can't drive window mode.

### PC checklist (Phases 2 + 3)

With `dist/CoderMascot.exe` from 23/10 12:00:

1. Run `tests/LogDbTests` against a scratch SQL Server database and a scratch MongoDB
   (README → Tests). All checks should pass; it drops what it creates.
2. **Add source → SQL Server table**: paste a read-only connection string, **Test
   connection** → "Connected to server / db", plus a warning line if it trusts any
   certificate. Pick the log table: the roles are guessed, five rows preview.
   Save. The page shows the newest 1,000 rows; new rows arrive within a minute.
3. The same for an id-mbi-api / eContract log table if one exists; for a table
   with no identity and no index on its time column, Test says why it is refused.
4. **Add source → MongoDB collection** with a find-only login: the collection can
   be typed even if it isn't listed; fields guessed; preview; Save.
5. `config.json`: the source holds `"connection": "dpapi:…"` and no password
   anywhere. Copy the file to another Windows account → the source says it was
   saved elsewhere and asks for the string again.
6. Wrong password → the page shows SQL Server's "Login failed…" or "MongoDB didn't
   accept the sign-in", never the string; the next tries come further apart.
7. Edit a table source and rename it → the entries stay (no re-read). Change the
   table → read afresh.
8. Reload on a table twice quickly → only one read (watch the server's activity
   if you can); switching pages doesn't query the database.

## Paging and search for tables (23/10)

The owner, 23/10: "when read log from db set time scan to every 5 mins and add
paging and search bar for better view and performance".

Phases 2 and 3 held up to 10,000 entries per table and filtered them on the page:
a long list, a search that saw only what had been fetched, and a poll every
minute. The table page is now a window onto the server — one page at a time,
newest first, with the level and the search done by the database — and the
background poll slows to five minutes. Folders are untouched.

### Decisions

- **D27 — the table poll is every 5 minutes, under a new key.** The setting is
  `logTablePollMinutes`: default 5, at least 1, at most 60 (D19 had
  `logTablePollSeconds`, default 60, 15–3600). It has a new name because the build
  that polled every 60 seconds wrote `"logTablePollSeconds": 60` into every config
  on any save, where it would have outlived the new default. The old key is now
  ignored and disappears on the next save. The poll feeds only the source list's
  status and error count. Page 1 follows it (D31), so new rows can show up to five
  minutes late; **Reload** reads at once.
- **D28 — one server page per table; folders unchanged.** For a table or
  collection the Logs page lists one page at a time, newest first, read from the
  server: 50, 100 or 200 rows, 100 by default, not remembered between runs. The
  pager under the list is **Newest**, **‹ Newer**, "Page n", **Older ›** and the
  page-size box (in a narrow window it moves under the status line); for a table
  the sort button and the file box are hidden. A folder is already whole in
  memory, so it still holds everything, filters as you type and has the
  Newest/Oldest button. D20's caps and D24's notes (ids went backwards → read
  afresh; rows arriving faster than 2,000 a poll → skip to the newest) now concern
  only the background read. The page holds only the page on screen and reads the
  table itself, so nothing is missing from it, and those two notes are no longer
  shown on it.
- **D29 — paging is keyset, not OFFSET.** Each page is the rows below the last row
  of the page above, by id — or by time for a table read by time (D19's window
  mode, which has no id). With OFFSET a new row would shift every page by one;
  keyset never skips or repeats a row while rows arrive. OFFSET/FETCH also needs
  SQL Server 2012 (some servers here are older) and rescans every page above.
  Ties: ids are unique, times are not. For a table read by time, rows that share
  the page's oldest time are all taken onto that page (up to 500; the page says
  when there were more), so the next page starts cleanly below that time. That
  extra query runs only when the row after the page shares the page's last time.
  The boundary goes to the server as a typed parameter, so a datetime2 boundary
  isn't rounded.
- **D30 — level and search run on the server (tables only).** Level, SQL Server:
  `LogSql.Page()` writes the names in the usual casings (Information, INFO, info)
  and the numbers of the scales (Microsoft.Extensions.Logging/NLog 0–5, pino
  10–60, log4net 10000–120000) into the SQL as literals, generated from the app's
  own level tables (`LogParse.LevelNamesFrom` / `LevelNumbersFrom`), never from
  user input; a number column is filtered by range. Search: the text as typed
  goes in the one parameter `@q`, `LIKE … ESCAPE '\'`, with `%`, `_`, `[` and the
  escape character escaped so they are literal; the message, exception,
  properties and logger columns are CAST to nvarchar(max) so an xml column can be
  searched. Case follows the column's collation. No T-SQL newer than 2008 (D18).
  MongoDB: level is `$in` on the names plus `$gte/$lte` on the numbers; search is
  `$regex` with every ASCII non-alphanumeric escaped, option `i`, and looks at text
  values only: a field stored as a sub-document (Serilog's `Properties`) isn't
  searched, and the search box's tooltip says so for a collection. The search runs
  on Enter, not on each keystroke — a `LIKE '%…%'` is a scan, and one per
  keystroke would hit production. While the box differs from what was searched
  the status line says "Enter searches for …"; emptying the box, or Esc, drops the
  search at once.
- **D31 — when a page is read.** On a user action only: opening the table on the
  page, turning a page, changing the level or the page size, Enter in the search
  box, **Reload**. A level or page-size change is read 0.4 s after the last one, so
  arrowing through the list doesn't send a query per step; Enter again (or held
  down) while the same search is being read doesn't start it over. **Reload** also
  polls the table at once (still not twice within 15 s, D25) and reads the page on
  screen again, at most every 5 s. A page button pressed while a page is being read
  is ignored. The one automatic read is page 1 with no level and no search, when
  the background poll finds new rows; it keeps the reader's place in the list. A
  filtered page or an older page is not re-read by itself and shows a note instead
  — "New rows have arrived since this page was read. Reload reads them." / "Newer
  rows have arrived since. Newest shows them." — so a search is never repeated
  against production every five minutes on its own. A level or a search can't use
  an index: the server reads back from the newest row until the page is full, so a
  rare match on a large table is slow, and the cost is bounded. SQL Server runs a
  filtered page query with `OPTION (MAXDOP 1)` — one core, so the server's own work
  goes on beside it — and a page has a 20-second budget for all its queries
  together (a command timeout counts each read on its own). MongoDB gives each
  query `maxTimeMS` 15 s, which the server counts across the whole query. When the
  server was slow to answer a page (stopped for time, or no answer), that table
  isn't asked again for 30 seconds, and the page says so. A page that can't be read
  says why in place of the rows; reading the page on screen again (Reload, or the
  automatic read) that fails keeps the page and says why above the list (D22, for
  pages).
- **D32 — a separate page reader per table; stale answers dropped.**
  `LogMonitor.PageAsync` reads through its own reader for each table, not the
  poll's: a failed poll closes its reader (D25), and that must not cut a page off
  mid-read. The table loop prunes the page readers when a source changes. A page
  reader is kept after a slow answer, so the next try doesn't reconnect and sign in
  again; it is closed after any other failure, after 5 minutes unused (a MongoDB
  client keeps connections and heartbeats open) and on exit. A table edited to read
  differently under the same id (other columns, connection) starts again from its
  newest page. On the page, a cursor stack remembers where each page began, an ask
  counter drops an answer that is no longer the latest, and each ask has its own
  cancellation, so a slow page can't overwrite a newer one.

### Checklist

- [x] `logTablePollMinutes` (default 5, 1–60) replaces `logTablePollSeconds` (23/10)
- [x] Core page API — `LogSql.Page()`, `LogParse.LevelNamesFrom/LevelNumbersFrom`, `SqlLogReader.PageAsync`, `MongoLogReader.PageAsync` (typed boundary parameters; MongoDB `$in` + `$gte/$lte`, escaped `$regex`, option `i`) (23/10)
- [x] `Core/LogMonitor.cs` `PageAsync` — a page reader per table, pruned when a source changes, closed on failure and on exit (23/10)
- [x] `UI/LogsPage.xaml(.cs)` — pager and page state: cursor stack, ask counter, one cancellation per ask; app build 0 warnings, 0 errors, WPF not run here (23/10)
- [x] `tests/LogTests` — **278 checks** (was 232), all pass; it compiles `MongoLogReader.cs` with the MongoDB.Driver package to check the filters without a server; six deliberate breakages each caught; 288 checks after the fix pass (23/10)
- [x] `tests/LogDbTests` page checks written — SQL Server and MongoDB: pages follow on, errors/warnings by the server, search in the exception and in xml properties, `%` `_` `[` and regex characters literal, rows sharing a boundary time kept together, a number level column by range; **not run** (owner, PC) (23/10)
- [x] README: "Reading a log table" — poll default, **Pages, levels and search** (23/10)
- [x] Code review + security review, fix pass (23/10)
- [x] Column guess reads snake_case (`created_date` = CreatedDate) and an audit table's `action`/`changes`; LogTests 291 (23/10)
- [ ] `tests/LogDbTests` (page checks included) against scratch databases — owner, PC
- [ ] PC checklist below — owner
- [ ] Commit on the PC — owner

### Review outcome (23/10)

Code review: no HIGH. MEDIUM, fixed: the saved 60 outliving the default (new key,
D27); MongoDB sub-documents not searchable (said in the tooltip and the README); a
table edited under the same id kept its old pages and cursors (pages reset; an id
cursor sent as a time is refused). LOW, fixed: the first page's status showed the
previous source's; "new rows" decided by the newest row, not a new view object (a
rename or a failed poll is not news); a NULL time on the row after the page no
longer offers an empty next page, and MongoDB string times page; the tie query
only when needed; ntext/text level columns compared via nvarchar; MongoDB: a
reader closed mid-read reported as such, NUL and other control characters dropped
from the search; the keyboard handed on when a pager button turns itself off;
pager race and after-stop edges; narrow layout. Not done: `CancelAsync` instead of
`Cancel` on the UI thread — disposing a source while `CancelAsync`'s callbacks run
is unsafe, and `Cancel` only sends an attention packet.

Security review: no injection, no secret in messages or the crash log, no write
path. MEDIUM, fixed: the cost of a filtered scan (`MAXDOP 1`, 20 s per page); query
storms (a repeated or same-search Enter ignored, a 30 s rest after a slow answer,
Reload 5 s, a 0.4 s debounce). LOW, fixed: the MongoDB tie query sorted by the time
index instead of `_id` (no in-memory sort); idle page readers closed, kept on slow
answers. Not done: `AllowDiskUse=false` (servers before MongoDB 4.4 reject the
option, and the tie sort now uses the index); capping the rows a search examines
with a `TOP(@scan)` window and paging by window — left as a possible follow-up;
the work is bounded by one core, 20 s and the 30 s rest.

Tests: LogTests 288 checks (was 278), all pass; six deliberate breakages of the
fixes each caught. LogDbTests is unchanged by the fix pass and still to be run on
the PC.

### PC checklist (paging)

1. Open a SQL Server table on the Logs page: the 100 newest rows and "Page 1". The
   sort button and the file box are not there.
2. **Older ›**: page 2 continues exactly below page 1 — compare the "#id" in the
   detail pane of the last row on page 1 with the first on page 2. **‹ Newer** and
   **Newest** come back.
3. Level **Errors only**: only errors, and the pager still works.
4. Type a word in the search box and press Enter: only rows holding it. A term
   with `%` or `_` in it matches those characters literally. Esc clears the search
   at once.
5. Type in the search box without pressing Enter: the status line says "Enter
   searches for …".
6. Change the page size to 50 and to 200 rows.
7. Leave page 1, with no level and no search, open for more than five minutes
   while the service logs: it refreshes in place and keeps your place in the list.
8. On page 2, or on a filtered page, when the service has logged since: the note
   says new rows have arrived and the page is not re-read by itself. **Reload**
   re-reads the page on screen.
9. A table read by time (no id column) pages without gaps or repeats.
10. The same on a MongoDB collection: paging, Errors only, search (case ignored).
11. Folder sources behave as before: filter as you type, the Newest/Oldest sort
    button, no pager.
12. `config.json`: `logTablePollMinutes` is 5, or there is no such line and 5
    applies. An old `logTablePollSeconds` line is ignored and is gone after the
    next save.
13. Narrow the window: the pager moves under the status line.

## Phase 4 — alerts (outline)

New `Error`/`Fatal` entries since the last look → one bubble per source per
sweep ("eContract Live: 3 new errors"), counts only — never message text on
screen — through `AlertGate`; nav badge on Logs.
