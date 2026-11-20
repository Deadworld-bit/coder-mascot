# Plan: one window, one look

Status: **All six phases built (12–13/10/2026), waiting for the check on the PC.** Nothing is committed yet.
Approved mockup: https://claude.ai/artifact/8Z2sMebZyESeA9Xr3AMCXV (Mockup v1, approved 12/10/2026)
20/11/2026: the Captures page was removed along with the screen-capture tracking; ignore it in D1 and the checks below.

## Why

The app grew two shells: a 620px Dashboard with five tabs and a 1280px Branches
window, plus separate Repository, Diagram and Connect windows. Several controls
were never themed, so a dark app shows light 17px Windows scrollbars, a white
dropdown and a light right-click menu, and the shared button template centres
its content, which is why the Branches rail rows and group headings sit at
different indents.

## Decisions (12/10/2026)

| # | Decision | Why |
|---|---|---|
| D1 | One main window with a left nav: Overview, Projects, Notes, Ports, Captures, Settings | Fewer windows; one way to get around |
| D2 | Refine the existing dark palette (`UI/Theme.xaml`); no light theme | The tokens are sound; the problem is unthemed controls |
| D3 | Add, edit and connect forms open as a drawer inside the window | Replaces ProjectDialog, DiagramDialog and SetupWindow |
| D4 | Deploy branches move to the bottom of the branch list | The Deployed strip at the top already shows them |
| D5 | Settings get one Apply bar, shown only when something changed | One rule for every setting instead of a mix |
| D6 | The mascot menu and the tray menu carry the same items in the same order | Today they differ |

## Phases

Each phase ends with a build the owner can run on the PC. Nothing here can run
in the workspace, so every phase is compile-checked, linted
(`python3 tests/xaml/lint.py`) and reviewed, then tried on the PC.

### Phase 1: the design system (no layout changes)
- [x] Button template honours `HorizontalContentAlignment` (fixes the ragged rail)
- [x] Thin dark scrollbars everywhere (implicit `ScrollBar` style)
- [x] Dark `ComboBox` / `ComboBoxItem`, editable and not
- [x] Dark `ToolTip`
- [x] Dark `ContextMenu` / `MenuItem` / `Separator` in `UI/Menus.xaml`, used by the chrome windows and the mascot
- [x] `TextBox` focus ring and a placeholder (`ui:Field.Hint`), used on the filter boxes
- [x] Dark `CheckBox` and `RadioButton`; keyed `Switch` and `Segment` styles for later phases
- [x] Dark native title bar on the chrome windows (`UI/DarkChrome.cs`)
- [x] Build, lint, review; publish `dist/CoderMascot.exe`

Built 12/10/2026 14:30, sha256 `348047AA…`. One code review: nothing that throws; four visible
issues fixed (editable dropdown click area, ellipsis on long values, a keyboard-only focus ring
for checkboxes and radio buttons, dim text on disabled menu items). No security review: no input,
file or network handling changed.

**Check on the PC** (none of this can run in the workspace):
- [ ] Right-click the mascot: the menu is dark, Diagrams submenu included. If it is still light, merge `UI/Menus.xaml` in `App.xaml` instead.
- [ ] Branches rail: rows and group headings start at the same left edge.
- [ ] Scrollbars in Branches and the Dashboard are thin and dark.
- [ ] The sort and Messages dropdowns are dark; a long value ends in "…" rather than being cut.
- [ ] Repository dialog: clicking beside the text in the Group / branch boxes places the caret.
- [ ] Right-click an empty text box: Cut and Copy look dim.
- [ ] Title bars of the five windows are dark.
- [ ] "Filter branches  (Ctrl+F)" lines up with the caret.
- [ ] Tab onto a checkbox or radio button: a blue ring appears; clicking one shows no ring.

### Phase 2: the main window shell
- [x] `MainWindow` with the nav rail (collapsible), title area and page host
- [x] Dashboard tabs become pages: Overview (was Status), Notes, Ports, Captures, Settings
- [x] Tray and mascot "Dashboard…" open the main window; the old DashboardWindow goes

Built 12/10/2026 15:24, sha256 `955D1E3E…`. The window is 1180×780 (shrunk to fit smaller screens),
min 760×520. Nav: Overview, Projects, Notes, Ports, Captures, then Settings and Collapse, each with a
count. Projects still opens the Branches window until Phase 3. One code review: nothing that throws;
fixed the Projects count (it now includes the background watch, not only repositories opened in
Branches), a Notes page that squeezed note text to nothing on narrow windows (the nav now folds
below 1000px and the lists pane narrows), a clipped focus ring in the lists pane, and the notes
footer running past the 1080px content edge. No security review: no input, file or network
handling changed.

**Check on the PC:**
- [ ] Tray or mascot "Dashboard…" opens the new window, title bar fully on screen.
- [ ] Nav: the selected item has the blue bar; counts show on Overview (leftovers, red), Notes, Ports.
- [ ] Projects opens the Branches window; after a few minutes its count matches the outstanding branches.
- [ ] Collapse: icons stay centred, tooltips name each item, a red dot shows on Overview when something is left running.
- [ ] Narrow the window below about 1000px: the nav folds; widen it again: it opens.
- [ ] Overview: two columns when wide; drag across about 860px — one column, no flicker; workspace buttons drop under the facts.
- [ ] Content stops at about 1080px on a maximised window and stays left-aligned (Overview, Notes, Ports, footer).
- [ ] Notes: pick a list, add a note, filter, rename and delete a list; at the narrowest window the note text is still readable.
- [ ] Tab through the Notes lists: the focus ring is whole on all four sides.
- [ ] Ports: the PORT / PROCESS / ACTIONS columns line up; System ports switch text is light, not black.
- [ ] Settings: everything still saves as before.

### Phase 3: Projects page
- [x] Branches window content moves into the Projects page
- [x] Repository pane with search; header with Edit / Re-read / Fetch
- [x] Deployed and Diagrams in one band; filter chips, sort dropdown, filter box
- [x] Trace shows on hover or focus; timeline toggle; deploy branches last (D4)
- [x] "Branches…" in the menus opens this page; the old BranchesWindow goes

Built 12/10/2026 15:58, sha256 `18B2E5E6…`. `UI/ProjectsPage` (a UserControl) replaces BranchesWindow;
it reads git the first time the page is shown, not when the window opens. The timeline starts on only
when the page is at least 1240px wide (a window of about 1450px); the switch and Trace turn it on, and
it shrinks to fit rather than running off the edge. One code review: nothing that throws, no
behaviour lost (diagram opening still goes through the same safety check); fixed the timeline
clipping on windows under about 1170px, a toolbar that overflowed at the narrowest width, a branch-row
hover border that never showed (already broken in the old window), and a clipped focus ring on the
first repository row. Two wordings changed from "this window" to "this page" (empty state and header
detail) — for the BA to confirm. The menu label stays "Branches…" until Phase 5.

**Check on the PC:**
- [ ] Projects opens without an error; dropdowns, scrollbars, the Timeline switch and a diagram chip's right-click menu are dark.
- [ ] Opening the main window starts no git work; the first visit to Projects reads; a second visit keeps the state; closing mid-read is clean.
- [ ] Tray or mascot "Branches…" opens the main window on Projects, also when it is already open on another page.
- [ ] Search: matches stay under their group with groups open; clearing restores the folds; the folds you saved are unchanged afterwards; Esc in the box clears it.
- [ ] Shortcuts with focus in the nav: F5 re-reads, Ctrl+F jumps to the branch filter, Esc widens (filter, then trace, then chip) and never closes the window; neither does anything on other pages.
- [ ] Deploy branches are last in the list; the Deployed and Diagrams band sits side by side and stacks when the window narrows below about 1170px.
- [ ] Trace appears on hover and on Tab, stays on the traced row, keeps keyboard focus, and turns the timeline on.
- [ ] Timeline at window widths 760, 1000 and 1180: nothing runs off the right edge; a dragged width comes back after off/on.
- [ ] Hovering a branch row lights its border.
- [ ] The remove-diagram confirmation sits over the main window. (The repository and diagram forms are drawers since Phase 4.)
- [ ] At the minimum height (520px) the branch list still shows a few rows.

### Phase 4: drawers
- [x] Repository form, diagram form and Connect move into a right-hand drawer (D3)
- [x] ProjectDialog, DiagramDialog and SetupWindow go

Built 12/10/2026 16:30, sha256 `CD95C17C…`. `UI/Drawer.cs` defines the drawer contract; the three
forms are now `UI/ProjectForm`, `UI/DiagramForm` and `UI/ConnectForm` inside the main window. While a
drawer is open the rest of the window is disabled, Tab stays inside it, Esc or × cancel, and Enter
still presses Save or Connect. Clicking the dimmed area does nothing, so a half-filled form isn't
lost. One drawer at a time. "Connect to Coder…" (tray, mascot, Overview's Connect…, and first run) opens the
main window with the Connect drawer. One code review: nothing that throws or hangs, no form logic
lost, and the token is handled exactly as before. Fixed: the drawer now always tidies up even if a
form's cancel step fails; focus falls back to the page when the element it came from was rebuilt by
the save; "Connect…" while another form is open now says why nothing happened. Changed on purpose:
Esc now closes Connect (the old window had no Esc), and closing the main window discards an open form.

**Check on the PC:**
- [ ] First run (no config, or rename the config file): the main window opens on Overview with the Connect drawer, no error.
- [ ] Tray and mascot "Connect to Coder…" work with the main window closed, minimised and open on another page.
- [ ] Connect: success shows for 1.5 s, then the drawer closes; the multi-workspace pick still re-runs Connect; Esc or "Not now" during the wait is clean.
- [ ] With a repository form open, tray "Connect…" brings the window forward and the drawer header says to finish or close the form first.
- [ ] The drawer slides in (about 150 ms) with no flash; widths 620 (repository) and 480 (diagram, connect); resizing down to 760 keeps it inside the window.
- [ ] Behind the drawer nothing can be clicked or tabbed to; F5 and Ctrl+F do nothing; the dimmed look is acceptable.
- [ ] Tab and Shift+Tab cycle inside the drawer; Esc closes it (an open dropdown takes the first Esc); Enter in a text box saves; Enter in Group, Branch or Env still adds rather than saves.
- [ ] After closing, the keyboard is back where it was (for example on "+ Add"), or at the top of the page after a save.
- [ ] Repository form: add, edit, remove (confirmation over the main window), cancel; Browse and Find pickers sit over the main window; the list keeps its order after an edit.
- [ ] Diagram form: link, edit, duplicate warning, Browse.
- [ ] Dropdowns inside the drawer (group, branch, environment, workspace) are dark and open in the right place.
- [ ] Close the window or quit from the tray while a repository check, a Find scan or a Connect check is running: no error.

### Phase 5: menus
- [x] Mascot and tray menus share one item list (D6), including Diagrams
- [x] Tray menu drawn dark (WinForms renderer)

Built 12/10/2026 17:08, sha256 `E6F3C47C…`. `Core/AppMenu.cs` builds the one list; both menus are
rebuilt from it each time they open and every command goes through one handler in App. Order:
status line (and "Session watch: not set up" when missing) · I know about this / Messages ▸ ·
**Open Coder Mascot…**, Projects…, Diagrams ▸, New sticky note, Check now, Open Coder dashboard,
Connect to Coder…, Edit config… · Show mascot (one per character), Walk around the edges,
Click-through, Start with Windows · Quit. Renamed: "Dashboard…" → "Open Coder Mascot…",
"Branches…" → "Projects…". `UI/TrayMenuRenderer.cs` draws the tray menu with the Theme.xaml colours.
Tests: `tests/MenuTests` (31 checks) and DiagramTests pass. One code review: fixed a tray menu that
would not open on the first right-click (WinForms cancels an empty strip before it can be filled),
its placement when a row comes or goes, one shared http(s) check for every "open Coder" path, tray
row height and font to match the WPF menu, and tray errors now go to the crash log. Changed on
purpose: Walk around the edges and Click-through now apply to every character from either menu.

**Check on the PC:**
- [ ] Right-click the tray icon once: the menu opens first time, dark, above the taskbar.
- [ ] Mascot and tray menus show the same items in the same order, including the status line and Diagrams.
- [ ] Tray menu: dark background and border, hover highlight, blue ticks, chevrons on Messages and Diagrams (nested levels too), dim disabled rows, bold "Open Coder Mascot…", rows about as tall as the mascot menu's.
- [ ] A workspace name with "_" (mascot menu) and a repository or diagram name with "&" (tray) show literally.
- [ ] Every item works from both menus: Messages, Open Coder Mascot…, Projects…, a diagram, New sticky note, Check now, Open Coder dashboard, Connect…, Edit config…, Quit.
- [ ] Show mascot off and on from both menus (with two characters if you use them, and during a full-screen app).
- [ ] Click-through from the mascot shows the warning; from the tray it doesn't; turning it off from the tray works.
- [ ] Walk around the edges and Start with Windows tick and untick from both menus; the Settings page agrees afterwards.
- [ ] A diagram that fails to open: a bubble from the mascot menu, a balloon from the tray.
- [ ] The mascot stays put while its menu is open; double-click on the mascot or the tray icon opens the main window.

### Phase 6: Settings and clean-up
- [x] Settings page per the mockup: section index, toggles, segmented Messages, Apply bar (D5)
- [x] README rewritten for the new window; old window code removed

Built 13/10/2026 08:56, sha256 `1BF65F06…`. Settings is full-bleed like Notes: a SECTIONS index on
the left (Messages, Startup, Warnings, Config file) that scrolls the page and follows it; Messages
is a three-way segmented control with a line describing the choice; the two checkboxes are
switches; the four thresholds keep their ranges and say "not a number — it will stay as it was"
when a box doesn't parse. One rule for every setting (D5): nothing saves on change; an Apply bar
("N changes not applied", Discard, Apply, Ctrl+S) appears only when something differs, the
Settings nav item shows the same count, and Apply writes only what changed. A startup change
Windows refuses stays pending with the reason. A change made from a menu updates the page unless
that control has an edit pending. Closing the window with changes pending asks Apply? Yes / No /
Cancel; quitting or logging off does not ask. One code review: fixed a crash and lost notes when
quitting or reopening the window while that question was on screen (it is now asked after the
close is cancelled, not during it), a refused startup change being dropped on close, "Saved."
showing when nothing was written, and the section index not reaching "Config file".
README: the UI chapters are rewritten for the main window, drawers, the shared menu and the new
Settings page; MenuTests added to Tests, with a note that `tests/ConfigTests` does not build (it
lists `Core/BranchView.cs`, `Core/WorkspaceView.cs` and `Core/INoteCipher.cs`, which are missing).
Old window code: the old windows went in Phases 2–4; a sweep found no type or style left unused.
New wording for the owner to confirm: "Pop-ups, and the mascot comes to get you." / "No pop-ups;
the mascot still comes to get you." / "Badge colour only.", "EVERYTHING ELSE", the config-file
sentence, the Apply bar and the close question.

**Check on the PC:**
- [ ] Settings: the SECTIONS index jumps to each section (heading at the top) and follows when you scroll, "Config file" included at the end.
- [ ] Messages segments: click and arrow keys move the choice; the line under it changes.
- [ ] The two switches and the four boxes: the Apply bar appears on the first change, the count goes up and down, and the Settings nav badge shows the same count from other pages.
- [ ] Type letters in a box: its range turns red with "not a number"; Apply puts the saved value back; "85,5" works in a comma-decimal locale.
- [ ] Apply: "Saved." for about 2 s; values out of range come back clamped; Ctrl+S does the same (not while a drawer is open). Discard puts everything back.
- [ ] Change Messages or Start with Windows from the tray while the page is open: an untouched control follows; one you edited keeps your edit.
- [ ] Close the window with a change pending: Yes applies and closes, No discards and closes, Cancel stays.
- [ ] With that question on screen: tray Quit exits cleanly (notes saved); tray "Open Coder Mascot…" doesn't crash.
- [ ] Quit from the tray, or log off, with a change pending: no question.

## Not in scope
- Sticky notes and the speech bubble keep their own look (they are desktop objects, not app chrome).
- No new features; every page shows what it shows today.
