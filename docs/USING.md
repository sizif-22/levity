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
| Understand how a project fits together | `/diagram`: components, verified relationships, click to open files |

Direct `/cmd{$...}` commands run in Windows PowerShell with the repo as the working directory. A bare `cd` is handled by Levity (the assistant follows you); chained lines such as `cd .. && dir` go to PowerShell as written.

## Architecture diagrams

Draws how a project fits together, so you or a new teammate can understand it quickly. Open it with the diagram button on a project in the Projects list, the **/diagram** chip, or by typing `/diagram`. It works on the PC and on the phone.

1. **Preview.** Levity reads the project folder on your PC and shows what it would send before anything leaves it: how many files are listed, how many source excerpts, the size, and how many secret-looking values were removed. Nothing is sent until you press **Generate**.
2. **Draw.** Your assistant (the same `agy` service your chats use) returns a structured graph of components and relationships. It runs in an empty temporary folder, cannot use tools, and is stopped after a time limit. It uses tokens like a chat message; a project of about 140 files cost roughly 43 thousand and took 40 seconds. Results vary a little from run to run.
3. **Check.** Levity validates the answer itself. Every file link is checked against the real project, and every relationship is checked against the code: a **solid** arrow means a file on one side references the other, a **dotted** arrow is inferred (plausible, but nothing in the code that was read shows it), and the README can document one too. The assistant's own claim never makes an arrow solid.
4. **Use.** Pan and zoom the picture. Tapping a component opens its file in the Antigravity IDE (the PC looks the file up itself; the phone only ever sends a component number). A list of components and relationships sits under the picture. If the group boxes cannot be laid out, the groups are shown by colour instead.

Other actions: **Regenerate** (the preview appears again), **Copy Mermaid**, **Save to docs/ARCHITECTURE.md** and **Delete**. Saving writes a marked section into `docs/ARCHITECTURE.md` and never touches the rest of the file, so you can run it again to refresh the diagram. A diagram is remembered per project and marked as out of date when the project has new commits.

**What is read and what is not.** Files tracked by git (or the folder's files if it is not a repository), minus folders such as `node_modules` and `bin`. Files like `.env`, keys, certificates and credentials are never read. Links and junctions are never followed outside the project. Values that look like secrets (tokens, passwords, connection strings, private keys) are replaced with `[REDACTED]` before anything is sent. This is a safety net, so keep real secrets out of source files anyway.

**Limits.** At most 34 components, 48 relationships and 10 groups. Relationships are verified for TypeScript and JavaScript imports, C# type usage and Python imports; for other languages the picture is still drawn but its arrows are all shown as inferred. Settings: `LEVITY_DIAGRAM_EFFORT` and `LEVITY_DIAGRAM_TIMEOUT` in `.env`.

## Choose where the assistant works

The **folder bar** under the header always shows the full path the assistant is working in. Click it to:

- open a project from the list (pinned, frequently used, recently active, all),
- **Choose folder...** for any folder on the PC,
- pick **No project** for a general chat in your user folder (settings, downloads, anything that is not a repo).

Folders you pick by hand are remembered. The same list is available on the phone. **Open in Antigravity IDE** (button on the welcome screen and next to each project) opens a folder in the IDE instantly, with no AI round trip.

## Chat

- Replies stream in with Markdown and highlighted, copyable code. Reasoning and tool activity (`run_command`, file reads and so on) show as pills.
- Two chips sit above the box: **/diagram** (draw this project's architecture) and **/cmd** (run a command; see below).
- **Queue:** messages sent while the assistant is busy wait above the box and run in order. Remove any with the x.
- **Stop:** the red square next to Send stops the current response and keeps the conversation, so you can steer and continue.

## Run a command without spending tokens

- `/cmd{$git status}` (aliases `/cli`, `/run`, `/sh`; also `/cmd $git status`): the `$` runs it directly in PowerShell in the current folder, with no AI. Output and exit code appear in the chat. `cd` changes the working folder (the path bar follows).
- `/cmd{git status}` without the `$`, or with text after the brace (`/cmd{$npm test} summarize the failures`): the agent runs it and can reason about the result.
- **Background jobs:** a `$` command still running after 3 seconds moves to the **Jobs** panel (terminal icon) with live output and a Stop button, so the chat stays free. Add `-b` to start it in the background at once: `/cmd{$bun run dev -b}` or `/cmd{$bun run dev} -b`. Stopping a job ends everything it started, including child processes. Jobs also show on the phone.
- The command word is highlighted in the box as you type; the `-b` flag is dimmed.

## Chat history

Every chat is saved on the PC automatically (`%APPDATA%\Levity\sessions`). The clock icon lists them. **Open** brings the chat back, switches to its folder and resumes the same agent conversation, so the AI keeps its context. **Delete** (tap twice) removes it. The phone shows and opens the same list. The open chat can't be deleted; start a new one first.

## Project review

Header grid icon (or tray, **Review All Projects...**). It keeps README, `AGENTS.md` and project skills current across many projects:

- Scans the projects folder two levels deep for project markers (`.git`, `package.json`, `*.csproj`, `pubspec.yaml`, `Cargo.toml`, `go.mod`, ...).
- **Update docs**: the agent edits documentation and skill files only (never source, configs or `.env`; no commits). **Report only**: nothing is written.
- Runs one project at a time (20 minutes each) with a progress banner, per-project summaries and **Stop**.
- It uses one agent session per project, so tick only the projects you want.
- The prompt is `ProjectSyncService.DefaultPrompt`; override it with `SyncPrompt` in `settings.json`.

## Skills that make it faster over time

`agent/skills` includes **Fast Learner**: when a simple task (downloading an image, changing the wallpaper, a CLI quirk) takes unexpectedly long, the assistant records what it learned so the next run is one quick step. It chooses one of three outcomes:

| Outcome | When | Where it goes |
|---|---|---|
| **Skill** | the fix is verified and reusable | `%USERPROFILE%\.gemini\config\skills\<name>\` |
| **Docs** | the learning belongs to this project | a short "Learned" section in the project's `AGENTS.md` |
| **Note for later** | not verified yet, or not worth a skill | `learned.jsonl` next to the skill, to be promoted later |

**You are told when it happens.** A card appears inside the reply ("Fast Learner · Learning", "Saving a skill: windows-wallpaper-manager", "Updating docs", "Taking a note for later", "Finished"), with a toast. If the sidebar is hidden you get a Windows notification. The phone shows the same cards, and they are kept when you reopen the chat.

How it works: the assistant runs the skill's small `announce.ps1` script before it saves anything, and Levity recognises that call in the agent's activity. If a skill is written without an announcement, Levity still notices the write into a skills folder and shows a "Skills" card. Re-run `.\Install-Skills.ps1 -Name fast-learner -Force` after updating Levity to refresh the skill the assistant uses. More in [RUNNING.md](RUNNING.md#6-agent-skills).
