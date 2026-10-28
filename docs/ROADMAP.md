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
| **M0** | **Memory measurement** | `tests/perf/Measure-Memory.ps1`: starts a published build, runs a script (idle, send a message, open a diagram, hide, wait), samples working set and private bytes of the host, every WebView2 process and `agy` at each step, prints a table and fails if a budget in `tests/perf/budget.json` is exceeded. Run before each release; later in CI on a Windows runner. | none, but makes every other number real | 1 |
| **M1** | **Quiet when hidden** | When the sidebar hides: call WebView2 `TrySuspend()` and set `MemoryUsageTargetLevel = Low`; resume on show. In the host, run a compacting GC and trim the working set after 10 s hidden. | 60 to 120 MB (renderer and GPU stop working and shrink) | 1 |
| **M2** | **Start the assistant only when needed, stop it when idle** | `agy` starts on the first message, not at launch. After 10 minutes with no turn and no job, stop it; the next message resumes the same conversation (`--conversation`, already supported). The first message after an idle stop is slower by `agy` start time (measure it; show "Starting the assistant..."). | about 107 MB while idle | 1 to 2 |
| **M3** | **Release the web view after a long hide** | After 30 minutes hidden, dispose the WebView2 entirely and create it again on show; the host already replays the conversation to a new page (the `ready` snapshot). Phone sessions are unaffected because they talk to the host. | the remaining 150 to 200 MB of WebView2 | 2 |
| **M4** | **Bound what we keep** | Chat history: merge streaming text pieces into one event per step before storing; cap the host list and the page (keep the latest 300 messages in the page, "Show earlier" loads the rest from the host). Job output: keep the last 200 KB per job, publish only changed jobs. Diagram: drop the drawn picture when the sheet closes. | removes growth over long days; keeps big chats responsive | 2 |
| **M5** | **Runtime settings** | Try and keep only what measures better: workstation non-concurrent GC, `GCConserveMemory`, tiered compilation settings, ReadyToRun build (faster start, less JIT memory). WebView2 arguments: renderer limit, no crash reporter, no background networking. Each setting is kept only if M0 shows a gain and nothing breaks. | 10 to 30 MB host, a few WebView2 processes fewer | 2 |
| **M6** | **Drop WinForms** | Replace `NotifyIcon`, the folder picker and `Screen` with direct Windows calls and the built-in WPF folder dialog; stop loading the WinForms assemblies. | about 8 to 15 MB | 3 |

Order inside the workstream: M0, M1, M2, M4, M3, M5, M6. M0 to M2 alone should remove about 250 MB at idle.

Risks: a suspended or recreated WebView2 can lose focus or flicker (test the show and hide paths with the existing hotkey tests and add a real-window check); `agy` start time is unknown, so M2 needs the measurement before it is switched on by default (keep a setting "Keep the assistant ready" for people who prefer speed).

## 5. Workstream B: understand and change Windows

Your assistant already supports extra tools (`mcp_config.json`) and you already have small Windows skills (wallpaper, audio). The plan turns that into one tested, fast, undoable toolbox.

### B1. Machine profile (2 to 3 days)

- **Why:** the wallpaper chat began by listing drives and searching folders; that is discovery the PC can answer instantly.
- **Design:** host builds a small profile (Windows edition and build, CPU, RAM, disks and free space, default shell and versions, `winget` list, dev tools found, power plan, display, key folders) and keeps it in `%APPDATA%\Levity\machine.json`. It refreshes in the background after a hash check of what changed and on demand. The first message of a conversation carries a short summary (a few hundred tokens, replacing what the assistant would otherwise discover).
- **Exit test:** wallpaper task no longer starts with drive and folder discovery; profile builds in under 3 s and adds no idle memory (built, written, released).

### B2. Windows toolbox as an MCP server (1 to 2 weeks)

- **Why:** each tool call replaces a model-written PowerShell script plus a shell start (about 8 s).
- **Design:** a native-AOT shim (`levity-windows-mcp.exe`, a few MB, starts instantly) is registered in `mcp_config.json`. It speaks MCP over standard input and forwards every call over a named pipe to the host, which does the work in-process (no second .NET runtime, no extra memory). Only a running Levity answers; otherwise tools report "Levity is not running".
- **First eight tools:** `get_setting` / `set_setting` for wallpaper, theme, power plan and display; `startup_apps` (list, enable, disable); `services` (list, start, stop, set start type); `scheduled_tasks`; `environment` (variables, PATH with duplicate and broken-entry detection); `winget` (search, install, upgrade, list outdated); `disk_usage` (largest folders, safe-to-clear suggestions); `network` (adapters, DNS, ping, port in use and by what).
- **Safety:** each tool declares whether it reads or changes things. Changes go through the change journal (B3). Parameters are validated; no tool accepts a command string.
- **Exit test:** the wallpaper task finishes in **under 60 s with 3 steps or fewer**; every tool has a unit test on real Windows and a refusal test for hostile input.

### B3. Change journal with undo (4 to 5 days)

- **Why:** this is what makes "change things with ease" safe.
- **Design:** before any change, the host records the previous value (registry export of the key, setting value, file copy, service start type) and the exact undo. A "Windows changes" list on the PC and phone shows what changed, when, and a **Revert** button. Journal entries are plain JSON files, kept for 30 days.
- **Exit test:** change, then revert, for each toolbox tool, in a test that checks the original value is restored exactly.

### B4. PC Doctor (1 week)

- **Why:** finds problems before you ask, with no tokens.
- **Design:** native scan (disk space, pending and failed updates, failed services, startup load, event-log errors in the last 24 hours grouped by source, driver problems, DNS and network) producing ranked findings. Each finding has a **Fix** button that hands the assistant a precise, small task or runs a toolbox tool directly.
- **Exit test:** scan under 10 s with no assistant call; each check has a test with a captured sample.

### B5. Runbooks (about 1 day each, ongoing)

- **Why:** consistent, correct fixes for the problems that come back.
- **Design:** skills (the format you already use) selected from the error text or event ID. First ten: Windows Update stuck, port already in use, PATH and long-path errors, WSL not starting, Docker Desktop, Wi-Fi and Bluetooth, slow boot, git credentials, certificates, disk full. Each runbook has a check, a fix, an undo, and a test script that runs on a clean machine.

### B6. See the screen (1 to 2 weeks)

- **Design:** a hotkey and a button send a screenshot of the active window to the assistant ("explain this error"); a second step reads the Windows UI Automation tree for text of dialogs and controls so the assistant can reason without guessing from pixels. Screenshots are held in memory, sent once, never stored. The phone can request a screenshot of the PC.
- **Risk:** privacy; it is only ever on demand and shows a preview before sending.

### B7. New-PC setup and drift check (1 week)

- **Design:** export apps (`winget`), selected settings, environment variables, PATH and the PowerShell profile into a git repository you own; re-apply on another PC with a dry run first; show what changed on this PC since the last export.

### B8. Elevated helper for admin-only fixes (1 to 2 weeks, after B2 and B3)

- **Why:** many Windows fixes need administrator rights; Levity runs as you, and a phone cannot click a UAC prompt.
- **Design:** a small service that accepts only a fixed list of operations from Levity over a protected pipe (caller checked), each operation approved on the PC or phone and written to the journal. No general "run as admin" and no command strings. Needs a security review before release and signed binaries.
- **Risk:** this is the most sensitive piece; it ships last and off by default.

## 6. Workstream C: trust and "walk away"

| # | Item | Design | Why | Effort |
|---|---|---|---|---|
| **T1** | **Undo checkpoint per turn** | Before each assistant turn in a git repo, record a snapshot (a private ref, working tree untouched). "What changed" shows the diff since the snapshot; **Undo that turn** restores it. Outside git, snapshots of the files the turn touched. | removes the fear of unattended runs | 2 to 3 |
| **T2** | **Push notifications** | Phone web push through the relay for: answer finished, job finished or failed, question waiting. Nothing readable passes through the relay (a generic "Levity has news"; the phone then fetches the details through the encrypted channel). | the "walk away" promise needs it | 1 to 2 |
| **T3** | **Approvals from the phone** | Run the assistant in a mode that asks before risky actions (it already has permission tools) and show "run X? allow or deny" on the phone and PC. Safe actions stay automatic. | the honest answer to running with permissions skipped | 3 to 5, depends on what the assistant exposes (spike first) |
| **T4** | **Speed and cost panel** | Per message and per project: time, tokens, slowest steps. The data is already in the stored chats. Flags slow shell starts and repeated work. | your own slowness problem, made visible | 2 |
| **T5** | **Scheduled tasks and triggers** | "Every morning review open PRs", "when a job fails, diagnose it". Stored as small definitions, run by the host's timer (no extra process), each run shown in a history. | an always-on PC can do this; a chat window cannot | 4 to 6 |

## 7. Later

Other backends (Claude Code, Codex, Gemini CLI) to reduce dependence on one tool; voice input on the phone; send a photo or file from the phone; `/preview` (open the PC's dev server on the phone through a tunnel, with a password); a hotkey on selected text anywhere in Windows; several tasks in parallel, each in its own git worktree.

## 8. Order and calendar

| Phase | Weeks | Contents | You can see |
|---|---|---|---|
| **1. Small and measured** | 1 | M0, M1, M2, M4 | idle memory table before and after; chat stays fast on long days |
| **2. Foundation** | 2 to 4 | B1, B2 (first eight tools), B3, T4; then M3, M5 | wallpaper in under 60 s; a "Windows changes" list with Revert; the speed panel |
| **3. Trust** | 4 to 5 | T1, T2, T3 (after the spike) | undo that turn; notifications; allow or deny on the phone |
| **4. Proactive** | 6 to 8 | B4, B5 (first ten), T5, M6 | PC Doctor; scheduled tasks |
| **5. Reach** | 9 to 12 | B6, B7, B8, later items | screen help; new-PC setup; admin fixes |

Dependencies: B2 needs B3 for any tool that changes things (build them together); B8 needs B2 and B3; T3 needs the permission spike; M3 needs M4 (a recreated page replays a bounded history).

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
