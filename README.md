# Levity: an AI assistant for Windows, built for developers

[![CI](https://github.com/sizif-22/levity/actions/workflows/ci.yml/badge.svg)](https://github.com/sizif-22/levity/actions/workflows/ci.yml)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![License: PolyForm Noncommercial](https://img.shields.io/badge/license-PolyForm%20Noncommercial-lightgrey)

Levity is an AI coding assistant that lives in your Windows tray. Press **Win + Alt + A** and it slides in beside your editor, already working in the repo you last chose. Ask it to fix a bug, explain a codebase or write tests; run `git`, build and test commands without spending a single token; leave a dev server running in the background; and check on all of it from your phone.

It is the developer interface for the **Antigravity CLI** (`agy`), which provides the AI. Levity adds what a terminal chat lacks: a persistent sidebar, a project switcher, a message queue, saved chats per repo, background jobs, an IDE shortcut, phone access and skills that make repeated tasks faster. (Formerly "Antigravity Sidebar".)

## Built for how developers work

- **Repo-aware.** It finds your projects by their markers (`.git`, `package.json`, `*.csproj`, `Cargo.toml`, `go.mod`, ...) and lists them as pinned, most used and recently active. One click switches the assistant to another repo; **Choose folder...** works for any directory, and **No project** gives a general assistant for non-repo tasks. The path bar always shows where it is working.
- **Commands without the AI.** `/cmd{$git status}` and `/cmd{$npm test}` run straight in PowerShell: instant, free, output and exit code in the chat. `cd` moves the whole assistant to another folder. Drop the `$` (or add text after it, `/cmd{$npm test} summarize the failures`) when you want the AI to run it and reason about the result.
- **Dev servers and watchers stay out of the way.** Anything still running after 3 seconds, or anything started with `-b` (`/cmd{$bun run dev -b}`), moves to a **Jobs panel** with live output and a Stop button that ends the whole process tree. Your chat stays free.
- **Never blocked while it thinks.** Type the next request and it is **queued**. Hit **Stop** to cancel a wrong turn without losing the conversation.
- **One chat per task, saved per repo.** Reopen yesterday's debugging session and the assistant remembers it, in its original folder.
- **Straight to your editor.** One click opens any project in the Antigravity IDE, with no AI round trip.
- **Docs that stay true.** **Project review** has the agent refresh `README.md`, `AGENTS.md` and project skills across many repos. It edits docs and skills only, never source, configs or `.env`, and never commits. A report-only mode writes nothing.
- **It learns your environment.** The bundled **Fast Learner** skill has the assistant save a working fix whenever a simple task took too long (a flaky CLI flag, a Windows quirk), so next time it takes one step. Drop your own skills into `agent/skills`.
- **Your phone is a remote.** Start a build, walk away, and watch the job output, stop it or ask a follow-up from your phone. End-to-end encrypted, and **no port is opened on your PC**.
- **Hackable and tested.** A small, typed codebase (C#, TypeScript), configuration through `.env`, and a CI that runs the relay, phone app, sidebar UI and service tests on every push.

## A typical session

```text
/cmd{$git status}                      instant status, no tokens
Why does the login test fail?          the assistant investigates in this repo
/cmd{$npm test} summarize failures     runs it, then the AI explains the output
/cmd{$bun run dev -b}                  dev server starts in the Jobs panel
(walk away; watch it or stop it from your phone)
/cmd{$cd ../other-service}             switch the assistant to another repo
```

## Quick start

```powershell
git clone https://github.com/sizif-22/levity.git
cd levity
.\Setup.ps1 -Skills -Shortcuts     # checks tools, creates .env, builds, installs the skills, adds shortcuts
.\publish\Levity.exe               # then press Win + Alt + A
```

You need Windows 10 or 11, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build, and the `agy` CLI installed and signed in. Node.js and a Cloudflare account are only needed for phone access. Full guide: **[docs/RUNNING.md](docs/RUNNING.md)**.

## Documentation

| Guide | What is in it |
|---|---|
| [Running Levity](docs/RUNNING.md) | prerequisites, build, every `.env` setting, where data lives, troubleshooting |
| [Using Levity](docs/USING.md) | developer workflows, folders, queue and stop, `/cmd` commands, background jobs, chat history, project review |
| [Phone access](docs/PHONE.md) | relay and web app setup, pairing, security model |
| [Testing](docs/TESTING.md) | the test suites and CI |

## Safety

The assistant runs with `--dangerously-skip-permissions`: it can read and change files and run commands in the folder it works in **without asking each time**. Choose its folder deliberately, use **No project** for tasks that do not need a repo, and pair only phones you own, because a paired phone can do anything the sidebar can. Phone traffic is encrypted end to end and your PC accepts no incoming connections; the details are in [docs/PHONE.md](docs/PHONE.md#security-model).

## Configuration

Copy `.env.example` to `.env` (every value is optional): relay and phone-app addresses, the default projects folder, paths to `agy` and the IDE, sidebar width. Environment variables win over the file, and anything you change in the app is saved in `settings.json` and wins over the `.env` defaults. `.env` is git-ignored.

## Under the hood

```
 Win + Alt + A                       ┌─ agy (Antigravity CLI): the AI, one process per chat
        │                            │
 Levity.exe (WPF + WebView2) ────────┤  PowerShell: direct /cmd{$...} commands and jobs
  sidebar UI, queue, saved chats     │
        │                            └─ %APPDATA%\AntigravitySidebar: settings, chats
        └─ outbound wss ─▶ relay (Cloudflare Worker) ◀─ wss ─ phone web app
```

```
Services/      agy process manager, relay client and crypto, jobs, chat store, project scanner, settings
wwwroot/       the sidebar UI (WebView2)
relay/         Cloudflare Worker + Durable Object, one room per key
web/           React + Vite phone app (any static host)
agent/         skills and the Fast Learner rule (installed by Install-Skills.ps1)
tests/         C# service tests, sidebar UI tests, repository checks
docs/          guides
Setup.ps1, Install-Skills.ps1, Run-Tests.ps1, Create-DesktopShortcut.ps1, run.ps1
```

`.\Run-Tests.ps1` runs every suite, and GitHub Actions runs them on each push to `main` or `staging` and on pull requests. See [docs/TESTING.md](docs/TESTING.md).

## License

[PolyForm Noncommercial License 1.0.0](LICENSE). Anyone may use, modify and share Levity for noncommercial purposes: personal use, learning, research, hobby projects, nonprofits and similar. **Commercial use, including selling it or using it to make money, is not permitted** without a separate license from the author. This makes the source available but not "open source" in the OSI sense.

Third-party code keeps its own licenses: `wwwroot/marked.min.js` (MIT), `wwwroot/highlight.min.js` and `github-dark.min.css` (BSD-3-Clause), and the npm and NuGet dependencies listed in `web/package.json`, `relay/package.json` and `AntigravitySidebar.csproj`.
