# Levity roadmap: a Windows assistant that understands the PC and stays small

Goal: **understand Windows and fix, update and change things in it with ease**, safely, from the PC or the phone, while using as little memory as possible when idle.

How to read this: every item has a **why** (a number from real use), a **design**, an **exit test** (how we know it works) and an **effort**. Effort is in working days for one developer. Nothing here is built yet except where it says "exists".

## 1. Where we start (measured)

| Measure | Today | Source |
|---|---|---|
| Levity total memory, sidebar hidden in the tray | **261 MB** (host 56 MB + six WebView2 processes 205 MB) | measured on the running app |
| The assistant (`agy`), always running even when idle | **107 MB** | measured |
| Total when idle | **about 370 MB** | sum |
| One simple task: change the wallpaper | **769 s, 29 shell commands, about 8 s per shell start**, 133k input tokens on the first message | stored chats |
| A plan task with 24 separate file reads | 447 s, about 50 model round trips | stored chats |

What the code does today that costs memory: `agy` starts at launch and never stops; the WebView2 stays fully alive while hidden; the chat history list in the host grows without a limit; WinForms is loaded only for the tray icon, folder picker and screen size; nothing tells .NET or WebView2 to stay small.

## 2. Rules that apply to every feature

1. **Nothing costs memory when idle.** New features load on first use and release afterwards. The memory check (item M0) is run before each release; a feature that raises the idle number is not done.
2. **Every new action has an undo or a clear "this cannot be undone" label** (items T1, W3).
3. **Commands the PC builds are chosen from fixed lists**, as `/run` and `/git` already do. Free text from the UI or the phone never becomes part of a command.
4. **Tests ship with the feature** in the existing suites (xUnit, sidebar jsdom, Vitest, repository checks). Anything that touches Windows runs against real Windows in `tests/Levity.Tests`.
5. **Phone and PC get the same feature**, or the doc says which one is PC-only.

## 3. Components

All work lands in one of these. This is the "system alignment" for this project.

| Component | What it is |
|---|---|
| **Host** | `Levity.exe` (WPF): runs the assistant, jobs, tray, settings, history, all Windows access |
| **Sidebar** | `wwwroot` page inside WebView2 |
| **Phone app** | `web/`, talks to the host through the encrypted relay |
| **Relay** | `relay/` Cloudflare Worker, forwards sealed messages only |
| **Windows MCP shim** (new) | a tiny native program the assistant starts as an MCP server; it only forwards calls to the host |

## 4. Workstream A: small and fast (do first)

Target after this workstream: **idle in the tray at or below 80 MB total (host plus anything it started), from about 370 MB.** Sidebar open with a chat at or below 220 MB. Diagram open at or below 300 MB. Sidebar shows in under 1.5 s from the tray.

| # | Item | Design | Expected saving | Effort |
|---|---|---|---|---|
| **M0** | **Memory measurement** (**done**) | `tests/perf/Measure-Memory.ps1`: starts a published build, runs a script (idle, send a message, open a diagram, hide, wait), samples working set and private bytes of the host, every WebView2 process and `agy` at each step, prints a table and fails if a budget in `tests/perf/budget.json` is exceeded. Run before each release; later in CI on a Windows runner. | none, but makes every other number real | 1 |
| **M1** | **Quiet when hidden** (**done**) | When the sidebar hides: call WebView2 `TrySuspend()` and set `MemoryUsageTargetLevel = Low`; resume on show. In the host, run a compacting GC and trim the working set after 10 s hidden. | 60 to 120 MB (renderer and GPU stop working and shrink) | 1 |
| **M2** | **Start the assistant only when needed, stop it when idle** (**done**) | `agy` starts on the first message, not at launch. After 10 minutes with no turn and no job, stop it; the next message resumes the same conversation (`--conversation`, already supported). The first message after an idle stop is slower by `agy` start time (measure it; show "Starting the assistant..."). | about 107 MB while idle | 1 to 2 |
| **M3** | **Release the web view after a long hide** (**done**) | After 30 minutes hidden, dispose the WebView2 entirely and create it again on show; the host already replays the conversation to a new page (the `ready` snapshot). Phone sessions are unaffected because they talk to the host. | the remaining 150 to 200 MB of WebView2 | 2 |
| **M4** | **Bound what we keep** (**done**) | Chat history: merge streaming text pieces into one event per step before storing; cap the host list and the page (keep the latest 300 messages in the page, "Show earlier" loads the rest from the host). Job output: keep the last 200 KB per job, publish only changed jobs. Diagram: drop the drawn picture when the sheet closes. | removes growth over long days; keeps big chats responsive | 2 |
| **M5** | **Runtime settings** (**measured, nothing worth keeping; see PERFORMANCE.md**) | Try and keep only what measures better: workstation non-concurrent GC, `GCConserveMemory`, tiered compilation settings, ReadyToRun build (faster start, less JIT memory). WebView2 arguments: renderer limit, no crash reporter, no background networking. Each setting is kept only if M0 shows a gain and nothing breaks. | 10 to 30 MB host, a few WebView2 processes fewer | 2 |
| **M6** | **Drop WinForms** (**measured, skipped: not worth the risk; see PERFORMANCE.md**) | Replace `NotifyIcon`, the folder picker and `Screen` with direct Windows calls and the built-in WPF folder dialog; stop loading the WinForms assemblies. | about 8 to 15 MB | 3 |

Order inside the workstream: M0, M1, M2, M4, M3, M5, M6. M0 to M2 alone should remove about 250 MB at idle.

**Result of M0 to M2 (measured, working set):** hidden in the tray after 45 seconds **629 MB down to 36 MB** (goal was 80 MB or less, met); just opened 636 MB down to 477 MB; opened again about 135 to 150 MB. Details and the method are in [PERFORMANCE.md](PERFORMANCE.md). M3 then took the hidden state from 36 MB to **21 MB** (the page is gone, 0 web view processes). M4 bounded history, logs, jobs and the page. M5 and M6 were measured and nothing was kept, with the numbers in PERFORMANCE.md. **Workstream A is finished.**

Risks: a suspended or recreated WebView2 can lose focus or flicker (test the show and hide paths with the existing hotkey tests and add a real-window check); `agy` start time is unknown, so M2 needs the measurement before it is switched on by default (keep a setting "Keep the assistant ready" for people who prefer speed).

## 5. Workstream B: understand and change Windows

Your assistant already supports extra tools (`mcp_config.json`) and you already have small Windows skills (wallpaper, audio). The plan turns that into one tested, fast, undoable toolbox.

### B1. Machine profile (**done**)

- **Why:** the wallpaper chat began by listing drives and searching folders; that is discovery the PC can answer instantly.
- **Design:** host builds a small profile (Windows edition and build, CPU, RAM, disks and free space, default shell and versions, `winget` list, dev tools found, power plan, display, key folders) and keeps it in `%APPDATA%\Levity\machine.json`. It refreshes in the background after a hash check of what changed and on demand. The first message of a conversation carries a short summary (a few hundred tokens, replacing what the assistant would otherwise discover).
- **Exit test:** wallpaper task no longer starts with drive and folder discovery; profile builds in under 3 s and adds no idle memory (built, written, released).

### B2. Windows toolbox as an MCP server (**done**: 34 tools, see [WINDOWS.md](WINDOWS.md))

- **Why:** each tool call replaces a model-written PowerShell script plus a shell start (about 8 s).
- **Design as built (better than the plan):** `agy mcp add` also accepts HTTP servers, so the toolbox is an MCP server inside Levity itself (loopback only, secret token, no browser origins). No shim program and no second process, so zero extra memory; it only runs while you have the switch on. Registration uses the assistant's own `agy mcp add` command, never a hand edit of its files.
- **Tools:** the full list is in the catalogue below, built in three waves, each tool with its own tests.
- **Safety:** each tool declares whether it reads or changes things. Changes go through the change journal (B3). Parameters are validated; no tool accepts a command string.
- **Exit test:** the wallpaper task finishes in **under 60 s with 3 steps or fewer**; every tool has a unit test on real Windows and a refusal test for hostile input.

#### B2 tool catalogue

Every tool is typed (named parameters, no command strings), says whether it only reads or also changes, and every change goes through the journal (B3). "Admin" means it needs the elevated helper (B8) until then it reports what it would do and why it cannot.

| Wave | Tool | What it can do | Admin | Notes |
|---|---|---|---|---|
| **1** (weeks 2 to 3) | `settings` | wallpaper, light or dark theme, power plan, display scale and brightness | no | the wallpaper task is the benchmark |
| 1 | `startup_apps` | list, enable, disable (registry run keys, startup folder, startup tasks) | no for your own | shows what each entry is and its start-up cost |
| 1 | `services` | list, start, stop, set start type | **yes** to change | read-only list works without admin |
| 1 | `scheduled_tasks` | list, enable, disable, run now | partly | |
| 1 | `environment` | variables, PATH with duplicate, missing and over-long entries flagged | no for user scope, **yes** for machine scope | |
| 1 | `winget` | search, install, upgrade, list outdated, uninstall | some installs | |
| 1 | `disk_usage` | largest folders, caches, safe-to-clear suggestions | no | never deletes without an explicit list and a journal entry |
| 1 | `network` | adapters, DNS, ping, which program uses a port | no | |
| **2** (weeks 5 to 7) | `processes` | top CPU and memory users, what owns a window or a port, end a process | no for yours | asks before ending anything it did not start |
| 2 | `printers` | list, default printer, clear a stuck queue | queue clearing may | |
| 2 | `event_log` | recent errors by source, search by event ID | no | feeds PC Doctor (B4) |
| 2 | `hosts_file` | read, add, remove entries | **yes** to change | every change journalled with the original file |
| 2 | `firewall` | list rules, find what blocks a program or port, add or remove an allow rule | **yes** to change | |
| 2 | `defender` | scan status, exclusions list, quick scan | **yes** for exclusions | |
| **3** (later) | `default_apps` | **guided**: opens the exact Settings page and says what to click | no | Windows protects default-app choices with a hash, so silent changes are not reliable and would break on updates; this is a limit of Windows, not of Levity |
| 3 | `registry` | read a key; change only keys that a runbook or tool names | by key | no free-form registry writes |
| 3 | `certificates` | list, check expiry, find the one a failing site needs | **yes** to install | |

### B3. Change journal with undo (**done**)

- **Why:** this is what makes "change things with ease" safe.
- **Design:** before any change, the host records the previous value (registry export of the key, setting value, file copy, service start type) and the exact undo. A "Windows changes" list on the PC and phone shows what changed, when, and a **Revert** button. Journal entries are plain JSON files, kept for 30 days.
- **Exit test:** change, then revert, for each toolbox tool, in a test that checks the original value is restored exactly.

### B4. PC Doctor (1 week) — DONE (scan, rules, fixes, sheets on PC and phone)

- **Why:** finds problems before you ask, with no tokens.
- **Design:** native scan (disk space, pending and failed updates, failed services, startup load, event-log errors in the last 24 hours grouped by source, driver problems, DNS and network) producing ranked findings. Each finding has a **Fix** button that hands the assistant a precise, small task or runs a toolbox tool directly.
- **Built:** `/doctor` chip on the PC and the phone. Checks: disk space, memory, pending restart, uptime, start-up load, System event log (unexpected shutdown, blue screen, disk and hardware errors, repeating service failures), DNS and gateway, PATH, temp folder size, Defender real-time, Windows Update service. Fixes are a read-only tool, a Settings page, or a precise task for the assistant ("do not change anything yet"). A fix that changes Windows (clean PATH) only runs from the PC after a second press, and is journaled and revertible.
- **Exit test:** a real scan of this PC takes about 6 s with no assistant call (tested); each rule has a test with made-up facts.

### B5. Runbooks (about 1 day each, ongoing)

- **Why:** consistent, correct fixes for the problems that come back.
- **Design:** skills (the format you already use) selected from the error text or event ID. First ten: Windows Update stuck, port already in use, PATH and long-path errors, WSL not starting, Docker Desktop, Wi-Fi and Bluetooth, slow boot, git credentials, certificates, disk full. Each runbook has a check, a fix, an undo, and a test script that runs on a clean machine.

### B6. See the screen (1 to 2 weeks)

- **Design:** a hotkey and a button send a screenshot of the active window to the assistant ("explain this error"); a second step reads the Windows UI Automation tree for text of dialogs and controls so the assistant can reason without guessing from pixels. Screenshots are held in memory, sent once, never stored. The phone can request a screenshot of the PC.
- **Risk:** privacy; it is only ever on demand and shows a preview before sending.

### B7. New-PC setup and drift check (1 week)

- **Design:** export apps (`winget`), selected settings, environment variables, PATH and the PowerShell profile into a git repository you own; re-apply on another PC with a dry run first; show what changed on this PC since the last export.

### B8. Administrator changes (**done, by a different design**)

- **Why:** many Windows fixes need administrator rights; Levity runs as you, and a phone cannot click a UAC prompt.
- **Design as built (better than the plan):** no service at all. Levity starts an elevated copy of itself that runs exactly one tool from a short-lived request file, and Windows shows its own approval prompt on the PC: nothing stays running with extra rights, and the person at the PC approving it is the approval. Only offered when Levity is installed in Program Files (an elevated program in a user-writable folder could be replaced). The elevated run refuses anything that is not an administrator tool, any request older than 5 minutes, and any request or result path outside Levity's elevation folder. A security review is still advisable before a public release, and the installer must put Levity in Program Files.
- **Risk:** this is the most sensitive piece; it ships last and off by default.

## 6. Workstream C: trust and "walk away"

| # | Item | Design | Why | Effort |
|---|---|---|---|---|
| **T1** | **Undo checkpoint per turn** (**done**, git only; see USING.md) | Before each assistant turn in a git repo, record a snapshot (a private ref, working tree untouched). "What changed" shows the diff since the snapshot; **Undo that turn** restores it. Outside git, snapshots of the files the turn touched. | removes the fear of unattended runs | 2 to 3 |
| **T2** | **Push notifications** | Phone web push through the relay for: answer finished, job finished or failed, question waiting. Nothing readable passes through the relay (a generic "Levity has news"; the phone then fetches the details through the encrypted channel). | the "walk away" promise needs it | 1 to 2 |
| **T3** | **Approvals from the phone** | Run the assistant in a mode that asks before risky actions (it already has permission tools) and show "run X? allow or deny" on the phone and PC. Safe actions stay automatic. | the honest answer to running with permissions skipped | 3 to 5, depends on what the assistant exposes (spike first) |
| **T4** | **Speed and cost panel** (**done**: /speed) | Per message and per project: time, tokens, slowest steps. The data is already in the stored chats. Flags slow shell starts and repeated work, and includes a read-only size report of `~/.gemini` (what adds input tokens). | your own slowness problem, made visible | 2 |
| **T5** | **Scheduled tasks and triggers** | "Every morning review open PRs", "when a job fails, diagnose it". Stored as small definitions, run by the host's timer (no extra process), each run shown in a history. | an always-on PC can do this; a chat window cannot | 4 to 6 |

## 7. Reach: the other features we discussed

These were in the first list of ideas. Each follows rule 1 (no idle memory): they load only while used.

| # | Item | Design | Why | Memory note | Effort |
|---|---|---|---|---|---|
| **L1** (DONE) | **Voice input on the phone** | Microphone button next to the message box using the phone browser's built-in speech recognition; the text lands in the box for you to check before sending. Falls back to hiding the button where the browser has no support. | Dictating a task is faster than typing on a phone | none: no model is bundled; the browser vendor's service hears the audio, so the setting says so | 1 to 2 |
| **L2** | **Send a photo or file from the phone** | Pick or take a photo, it is sealed and sent in chunks through the relay, written straight to `%LOCALAPPDATA%\Levity\inbox` and the assistant is told its path. Size limit (10 MB), file type check, deleted after 7 days. | "Here is a screenshot of the error", a whiteboard, a log file | streamed to disk in chunks; a file is never held whole in memory | 3 to 4 |
| **L3** | **Hotkey on selected text anywhere in Windows** | A second global hotkey copies the current selection (and restores your clipboard), opens the sidebar with the text shown and buttons: Explain, Fix, Rewrite, Translate. Nothing is sent until you press a button. Apps on a deny list (password managers, banking) are skipped. | A Windows-only advantage over any terminal wrapper | the hotkey hook already exists; no extra process | 3 |
| **L4** | **`/preview`: open the PC's dev server on the phone** | Starts a tunnel (`cloudflared`, which you already use) to a small password-protected proxy in front of the dev server, shows a link and a QR code, and stops everything when you close it or after 2 hours. | See the app you are building on the real phone | proxy and tunnel exist only while a preview is open | 4 to 5 |
| **L5** | **Other backends** (Claude Code, Codex, Gemini CLI) | One interface (start, send, stream events, stop, resume) with a small adapter per tool and a list of what each supports (effort levels, resume, permission prompts). The sidebar and phone only see normalised events. Backend picked per project. | Removes the single biggest strategic risk (one vendor's tool) and widens who can use Levity | one process at a time, as now | 7 to 10 for the interface plus the first extra adapter |
| **L6** | **Several tasks in parallel** | Each task gets its own git worktree and its own assistant process, shown as lanes; finish a lane by merging or discarding it. Default limit of 2 lanes. | Review one change while another is being built | **each assistant process is about 107 MB**, so lanes are capped by a memory budget and idle lanes are stopped (M2) | 8 to 10 (after T1 and L5) |

Not planned unless you ask: a bundled offline speech model (large and slow), and a Mac or Linux version.

## 7b. Loose ends from earlier work

Small items that are not features but should not be forgotten:

| Item | Who | What |
|---|---|---|
| Shell profile guard | **you** | Add `if ($env:LEVITY_AGENT) { return }` as the first line of your PowerShell profile(s) so the assistant's shells skip your prompt theme (about 7 s per command). Levity never edits your profile. See [PERFORMANCE.md](PERFORMANCE.md). |
| Trim `~/.gemini` | you, with a read-only size report from us | `antigravity-backup`, old `brain` data and unused skills add to the input tokens on every turn. A report script that lists sizes (changes nothing) is part of item T4. |
| Phone app redeploy | you | The phone app only picks up changes after a Vercel redeploy of `web/`. |
| Old WebView folder | you | `%LOCALAPPDATA%\AntigravitySidebar` can be deleted after the rename to Levity. |
| `git push` or `pull` asking for credentials | note | They run as plain commands, so a prompt does not appear in the Jobs panel. Use `/cmd$-i git push` (the `-i` flag) when a prompt is likely. |
| "Review all my projects" threshold | decision | Shown with one project or more today; change to two or more if you prefer. |
| Diagram font | decision | Plus Jakarta Sans now; Nunito is a one-line change. |

## 8. Order and calendar

| Phase | Weeks | Contents | You can see |
|---|---|---|---|
| **1. Small and measured** | 1 | M0, M1, M2, M4 | idle memory table before and after; chat stays fast on long days |
| **2. Foundation** | 2 to 4 | B1, B2 (wave 1, eight tools), B3, T4; then M3, M5 | wallpaper in under 60 s; a "Windows changes" list with Revert; the speed panel |
| **3. Trust and quick wins** | 4 to 5 | T1, T2, T3 (after the spike), L1 voice | undo that turn; notifications; allow or deny on the phone; dictate a task |
| **4. Proactive** | 6 to 8 | B2 wave 2, B4, B5 (first ten), T5, L2 attachments, L3 hotkey on selected text, M6 | PC Doctor; scheduled tasks; photo to the assistant; "explain this" anywhere |
| **5. Reach** | 9 to 12 | B6, B7, B8, L4 preview | screen help; new-PC setup; admin fixes; app on your phone |
| **6. Bigger bets** | 13 to 16 | L5 other backends, L6 parallel tasks | choose the tool per project; two tasks at once |

Dependencies: B2 needs B3 for any tool that changes things (build them together); B8 needs B2 and B3; T3 needs the permission spike; M3 needs M4 (a recreated page replays a bounded history); L6 needs T1 and L5; L2 and L4 need relay message-size limits checked first.

## 9. How we know it worked

| Measure | Today | Goal | Phase |
|---|---|---|---|
| Idle memory, tray (host plus helpers) | about 370 MB | **at most 80 MB** | 1 and 2 |
| Sidebar open, one chat | about 260 MB plus agy | at most 220 MB | 1 and 2 |
| Show from tray | not measured | under 1.5 s | 1 |
| Change the wallpaper | 769 s, 29 commands | **under 60 s, 3 steps or fewer** | 2 |
| Input tokens on a simple first message | 133k | under 40k | 2 |
| Find and offer a fix for a common Windows problem | manual | PC Doctor finds it in under 10 s with no tokens | 4 |
| Any system change can be undone | no | every toolbox change has a working Revert | 2 |

## 10. First steps and open questions

1. Build M0 (measurement), then M1 and M2, and publish the before and after table.
2. Spike (half a day): confirm how `agy` loads an MCP server and what its permission mode exposes. This needs a change to `~/.gemini/config/mcp_config.json`, so it needs your approval; the spike can use a throwaway test entry first.
3. Decide: should "Keep the assistant ready" (no idle stop) be on or off by default? The plan assumes **off** to save memory.
4. Decide: is Levity only for you and your team, or a public product? That sets how much signing, installer and security review B8 needs.
