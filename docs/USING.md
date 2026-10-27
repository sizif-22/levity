# Using Levity

## Open it

`Win + Alt + A` slides the sidebar in from the right edge; the tray icon has the same actions (show, pin, restart the engine, review projects, phone access, start with Windows). The pin keeps it on top; unpinned it hides when you click away. Light or dark follows your Windows theme.

## Developer workflows

| I want to | Do this |
|---|---|
| See repo state or run git quickly | `/cmd{$git status}`, `/cmd{$git log -5}`: instant, no tokens |
| Run tests and understand failures | `/cmd{$npm test} summarize the failures` |
| Keep a dev server or watcher running | `/cmd{$bun run dev -b}`; watch or stop it in **Jobs**, on the PC or the phone |
| Work on another repo | click the folder bar and pick it, or `/cmd{$cd ../other-repo}` |
| Open the code in the editor | the **Open in Antigravity IDE** button, next to each project |
| Ask something unrelated to any repo | folder bar, **No project** |
| Return to an earlier investigation | clock icon, **Open**: same folder, same conversation |
| Teach the assistant a repeatable task | write a skill in `agent/skills/<name>/SKILL.md` and run `.\Install-Skills.ps1`, or let **Fast Learner** create one |
| Keep README, `AGENTS.md` and skills current | grid icon, **Project review** |

Direct `/cmd{$...}` commands run in Windows PowerShell with the repo as the working directory. A bare `cd` is handled by Levity (the assistant follows you); chained lines such as `cd .. && dir` go to PowerShell as written.

## Choose where the assistant works

The **folder bar** under the header always shows the full path the assistant is working in. Click it to:

- open a project from the list (pinned, frequently used, recently active, all),
- **Choose folder...** for any folder on the PC,
- pick **No project** for a general chat in your user folder (settings, downloads, anything that is not a repo).

Folders you pick by hand are remembered. The same list is available on the phone. **Open in Antigravity IDE** (button on the welcome screen and next to each project) opens a folder in the IDE instantly, with no AI round trip.

## Chat

- Replies stream in with Markdown and highlighted, copyable code. Reasoning and tool activity (`run_command`, file reads and so on) show as pills.
- Quick chips insert the agent's slash commands (`/plan`, `/goal`, `/boost`, `/compact`, `/help`).
- **Queue:** messages sent while the assistant is busy wait above the box and run in order. Remove any with the x.
- **Stop:** the red square next to Send stops the current response and keeps the conversation, so you can steer and continue.

## Run a command without spending tokens

- `/cmd{$git status}` (aliases `/cli`, `/run`, `/sh`; also `/cmd $git status`): the `$` runs it directly in PowerShell in the current folder, with no AI. Output and exit code appear in the chat. `cd` changes the working folder (the path bar follows).
- `/cmd{git status}` without the `$`, or with text after the brace (`/cmd{$npm test} summarize the failures`): the agent runs it and can reason about the result.
- **Background jobs:** a `$` command still running after 3 seconds moves to the **Jobs** panel (terminal icon) with live output and a Stop button, so the chat stays free. Add `-b` to start it in the background at once: `/cmd{$bun run dev -b}` or `/cmd{$bun run dev} -b`. Stopping a job ends everything it started, including child processes. Jobs also show on the phone.
- The command word is highlighted in the box as you type; the `-b` flag is dimmed.

## Chat history

Every chat is saved on the PC automatically (`%APPDATA%\AntigravitySidebar\sessions`). The clock icon lists them. **Open** brings the chat back, switches to its folder and resumes the same agent conversation, so the AI keeps its context. **Delete** (tap twice) removes it. The phone shows and opens the same list. The open chat can't be deleted; start a new one first.

## Project review

Header grid icon (or tray, **Review All Projects...**). It keeps README, `AGENTS.md` and project skills current across many projects:

- Scans the projects folder two levels deep for project markers (`.git`, `package.json`, `*.csproj`, `pubspec.yaml`, `Cargo.toml`, `go.mod`, ...).
- **Update docs**: the agent edits documentation and skill files only (never source, configs or `.env`; no commits). **Report only**: nothing is written.
- Runs one project at a time (20 minutes each) with a progress banner, per-project summaries and **Stop**.
- It uses one agent session per project, so tick only the projects you want.
- The prompt is `ProjectSyncService.DefaultPrompt`; override it with `SyncPrompt` in `settings.json`.

## Skills that make it faster over time

`agent/skills` includes **Fast Learner**: when a simple task (downloading an image, changing the wallpaper, a CLI quirk) takes unexpectedly long, the agent saves the working solution as a reusable skill so the next run is one quick step. Install with `.\Install-Skills.ps1`. More in [RUNNING.md](RUNNING.md#6-agent-skills).
