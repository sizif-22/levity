# Making the assistant faster

Most of the waiting in a Levity chat is not the AI thinking. It is shell start-ups and one model round trip per step.
A typical slow session looked like this (measured from saved chats): 29 shell commands averaging 8 seconds each, 24 single-file reads
(24 separate round trips), and 130,000 or more input tokens on the very first message.

## What Levity does for you

- **Fast / Deep switch** (top bar). Fast (the default) asks the assistant for low effort: quicker and cheaper, enough for most tasks.
  Deep asks for high effort for hard problems. The assistant restarts on the same conversation when you switch, so context is kept.
  You can only switch between answers.
- **One-line efficiency hint** added to the first message of each conversation: read several files at once, prefer one command that does
  the whole job, search with `rg` or `git grep`, do not re-verify what already worked. You never see it in the chat.
- **Step timings.** A tool step that took 3 seconds or more shows its time, so slow steps stand out.

## The one thing you do: skip your PowerShell profile for the assistant's shells

Every command the assistant runs starts a PowerShell. If your profile loads a prompt theme (oh-my-posh), predictions or shell
integrations, each command pays for them: about 7 seconds each on one machine, and over 40 seconds when the shell had to wait.
Levity sets `LEVITY_AGENT=1` for everything the assistant starts. Put this as the **first line** of your profile (`$PROFILE`, and the Windows
PowerShell profile too if you use both):

```powershell
if ($env:LEVITY_AGENT) { return }
```

Your own terminals are unaffected, because only the assistant's shells have the variable. Levity never edits your profile.

## Trim what the assistant reads on every turn

Everything in the assistant's tool list, skills and global rules is sent with each turn. Remove skills you do not use, keep rules short,
and archive old folders under `~/.gemini` (large backups and old conversation data do not help). Fewer input tokens means faster turns
and lower cost.

## Memory

Levity gives memory back when you are not using it:

- **The web view sleeps when the sidebar is hidden** (after 3 seconds, and only if nothing is running that needs it) and wakes when you open it again or a command or answer starts. The host also compacts itself about 10 seconds after hiding.
- **The assistant starts with your first message, not at launch,** and is stopped after 10 quiet minutes. The next message starts it again and **resumes the same conversation**; only that first message is a little slower (the status shows "Starting the assistant..."). Set `LEVITY_AGENT_IDLE_MINUTES=0` in `.env` to keep it running.

Measured with `tests/perf/Measure-Memory.ps1` (working set of Levity, every web view process and the assistant), before and after:

| State | Before | After |
|---|---|---|
| Just opened | 636 MB | 477 MB (the assistant is not running yet) |
| Hidden for 5 seconds | 630 MB | 401 MB |
| Hidden for 45 seconds | **629 MB** | **36 MB** |
| Opened again | 645 MB | 132 to 151 MB |
| Hidden for 30 minutes (page thrown away completely) | 629 MB | **21 MB** |

These are working-set numbers, the same method as Task Manager's memory column. Pages Windows takes out of the working set are not lost: they return on demand, which is why the page is quick to wake. Run the script before a release; `tests/perf/budget.json` holds the limits and the script fails if one is exceeded. `-Attach` takes one reading of the Levity that is already running.

- **After 30 hidden minutes the page is thrown away completely** (`LEVITY_WEBVIEW_RELEASE_MINUTES`, `0` = never). Opening the sidebar rebuilds it from your conversation in under a second; opening from this state costs about as much as the first open after starting Levity, then it settles again.
- **Long days stay light:** the stored conversation merges the assistant's streaming fragments (they were about 80% of its size) and drops the oldest turns above about 3 MB; the page keeps the latest 300 rows (the phone too); only the last 20 finished jobs are kept; `startup.log` and `debug.log` rotate at 1 MB and the conversation traffic is only logged with `LEVITY_DEBUG_LOG=1`.

### What was measured and not kept

Measured with the same script, clean runs, sidebar pinned:

| Option | Visible | Hidden for 45 s | Decision |
|---|---|---|---|
| nothing (current) | 467 MB | 37 MB | |
| WebView2 `--in-process-gpu` | 429 MB | 37 MB | **not on by default**: about 40 MB less while visible, nothing when hidden, and it is an unsupported Chromium flag; to try it set the environment variable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--in-process-gpu` before starting Levity |
| WebView2 `--disable-gpu` | 445 MB | 48 MB | no: smaller gain and it adds a process |
| .NET `GCConserveMemory`, non-concurrent GC, no PGO | 466 MB | same | no effect |
| ReadyToRun (precompiled) build | 473 MB | 36 MB | no effect on memory (and a bigger file) |
| Removing WinForms (tray icon, folder picker) | not built | not built | the host is only 11 MB when hidden and WinForms is mostly mapped, not resident; the saving while visible would be a few MB at the price of rewriting the tray, so it is not worth the risk |
