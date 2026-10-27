# Levity

[![CI](https://github.com/sizif-22/levity/actions/workflows/ci.yml/badge.svg)](https://github.com/sizif-22/levity/actions/workflows/ci.yml)

Desktop sidebar and phone app for the **Antigravity CLI** (`agy`). Formerly "Antigravity Sidebar".

- **Desktop**: tray widget that slides in from the right edge (`Win + Alt + A`). WPF + WebView2.
- **Phone**: React/Vite app on Vercel. It talks to your PC through a tiny relay, end-to-end encrypted. **No port is opened on your PC.**
- **Project review**: one click runs `agy` over every project under a root folder (default: your user folder, set `LEVITY_PROJECTS_ROOT`) to refresh README, `AGENTS.md` and `.agent/skills/`.

```
 phone (Vercel app) ──wss──▶  relay (Cloudflare Worker)  ◀──wss── PC (Levity.exe)
        └──────────── AES-256-GCM, key only on phone + PC ────────────┘
```

Vercel only serves static files and cannot hold WebSockets, so the relay is a separate ~100-line Cloudflare Worker (`relay/`). The relay forwards opaque encrypted frames between a room's PC and phones. It never holds a key.

## Quick start

```powershell
git clone https://github.com/sizif-22/levity.git
cd levity
.\Setup.ps1 -Skills -Shortcuts     # checks tools, creates .env, builds, installs agent skills, adds shortcuts
.\publish\Levity.exe               # or Win + Alt + A once it is running
```

Needs Windows 10/11, the .NET 8 SDK and the `agy` CLI. The full guide, with prerequisites, every setting, phone setup and troubleshooting, is in **[docs/RUNNING.md](docs/RUNNING.md)**.

## Configuration

Copy `.env.example` to `.env` (all values optional): relay and phone-app addresses, projects folder, paths to `agy` and the IDE, sidebar width. Environment variables win over the file, and anything you change in the app is saved in `settings.json` and wins over the `.env` defaults. `.env` is git-ignored.

## Agent skills

`agent/skills` ships skills for the agent, including **Fast Learner**: when a simple task (downloading an image, changing the wallpaper, a CLI quirk) takes unexpectedly long, the agent saves the working solution as a reusable skill so the next run is one quick step. Install with `.\Install-Skills.ps1` (`-Force` to replace, `-Uninstall` to remove). Details in [docs/RUNNING.md](docs/RUNNING.md#6-agent-skills).

## Tests and CI

`.\Run-Tests.ps1` runs every suite: the relay (integration tests against the real Worker), the phone app, the desktop sidebar UI, the C# services (including real PowerShell jobs and encryption compatibility with the phone app), and repository checks for scripts, skills, config and docs. GitHub Actions (`.github/workflows/ci.yml`) runs the same suites on every push to `main` or `staging` and on pull requests. Details: [docs/TESTING.md](docs/TESTING.md).

## Set up phone access (once)

1. **Relay** (Cloudflare, free plan is enough):
   ```bash
   cd relay && npm install && npx wrangler login && npx wrangler deploy
   ```
   Note the address it prints, e.g. `wss://levity-relay.<you>.workers.dev` (use `wss://`).
2. **Phone app** (Vercel): import the repo, set **Root Directory** to `web`, framework **Vite**. Or `cd web && npx vercel --prod`.
3. In Levity: header phone icon → paste the relay address (the Vercel address is prefilled; change it if you redeploy elsewhere) → Save.

## Pair a phone

1. Phone icon → **Pair a new phone**. A QR code appears (valid 5 minutes, single use).
2. On the phone open the Levity web app (on iPhone, add it to the Home Screen first and open it from there) and tap **Scan QR code**. Scan from inside the app, not with the Camera app: the Camera opens the link in a separate browser sheet whose storage the app cannot see. The phone shows a 4-digit code and asks for access. If the camera is unavailable, use **Paste a link instead**.
3. On the PC an approval dialog shows the phone name and the same code. Approve only if they match.
4. The PC hands that phone its own random key. Add the page to the Home Screen if you like.

Revoke any phone from the same panel; it is cut off immediately. Optional app passcode: phone → gear icon.

## Security model

| Threat | Mitigation |
|---|---|
| Someone on your network or the internet reaches your PC | Nothing listens. The PC only makes outbound connections. |
| Relay or Vercel is compromised or snoops | Keys never reach them. Frames are AES-256-GCM encrypted; the room id is a hash of the key. They cannot read chats or send commands. |
| Someone finds the QR | It expires in 5 minutes, works once, and a phone is trusted only after you approve it on the PC. |
| Replay or reflection of captured frames | Per-session counters, a 2-minute clock window, and a separate AAD per direction. |
| Lost or stolen phone | Revoke that device on the PC. Each phone has its own key. |
| Malware reading the phone's key | Stored as a non-extractable WebCrypto key in IndexedDB; optional passcode on top. |
| Keys at rest on the PC | Encrypted with Windows DPAPI (current user) in `settings.json`. |

Not covered: a malicious Vercel deployment could serve altered JavaScript. Only deploy `web/` from this repo, from an account you control. Anyone you pair can run commands on this PC (`agy --dangerously-skip-permissions`), so pair only your own phones. The relay address is not secret and has no login, so someone could use your Worker's quota, but cannot read or inject anything.

## Run a CLI command

- `/cmd{$git status}` (aliases `/cli`, `/run`, `/sh`; also `/cmd $git status`): the `$` runs it directly in PowerShell in the current folder, with no AI and no tokens. `cd` changes the working folder.
- `/cmd{git status}` without the `$`, or any text after the brace (`/cmd{$npm test} summarize failures`): the agent runs it.
- **Background jobs:** a `$` command still running after 3 s moves to the **Jobs** panel (terminal icon in the header) with live output and a Stop button. Add `-b` to start it in the background at once: `/cmd{$bun run dev -b}` or `/cmd{$bun run dev} -b`. Jobs show on the phone too.
- **Queue and stop:** messages sent while Levity is busy are queued (shown above the box, removable) and sent in order. The red square next to Send stops the AI's current response and keeps the conversation.

Works from the phone too.

## Chat history

Every chat is saved on the PC automatically (`%APPDATA%\AntigravitySidebar\sessions`). The clock icon in the header lists them: **Open** brings the chat back, switches to its folder and resumes the same agent conversation, so the AI keeps its context. **Delete** (tap twice) removes it. The phone shows and opens the same list. The open chat can't be deleted.

## Project review

Header grid icon (or tray → **Review All Projects...**):

- Scans the root two levels deep for project markers (`.git`, `package.json`, `*.csproj`, `pubspec.yaml`, `Cargo.toml`, `go.mod`, ...).
- Groups projects: **Pinned**, **Frequently used** (by prompts sent), **Recently active** (git activity), then the rest. Pin from any row.
- **Update docs**: agy edits documentation and skill files only (never source, configs, `.env`; no commits). **Report only**: nothing is written.
- Runs one project at a time (20 min timeout each) with progress banner, per-project summaries and Stop.
- Prompt: `ProjectSyncService.DefaultPrompt`; override with `SyncPrompt` in settings.json. The skills path `.agent/skills/<name>/SKILL.md` is an assumed convention.

## Layout

```
Services/
  AgyProcessManager.cs     spawns agy (stream-json), one per chat or review job
  MobileHub.cs             device registry, pairing window + approvals, one relay link per phone
  RelayClient.cs           outbound WebSocket to the relay, reconnects, replay protection
  RelayCrypto.cs           AES-GCM framing shared with the phone app
  ProjectScanner.cs / ProjectSyncService.cs
  TrayIconManager.cs, HotkeyManager.cs, AppSettings.cs, NativeMethods.cs
  JobManager.cs, DirectShell.cs, CommandShortcut.cs   /cmd{$...} direct commands and background jobs
  SessionStore.cs          saved chats
  DotEnv.cs                .env loader
wwwroot/                   desktop sidebar UI (WebView2)
relay/                     Cloudflare Worker + Durable Object (room per key)
web/                       React + Vite phone app (deploy to Vercel or any static host)
agent/                     agent skills and the Fast Learner rule (installed by Install-Skills.ps1)
docs/RUNNING.md, docs/TESTING.md   how to build, configure, run and test
tests/                     C# service tests, sidebar UI tests, repository checks
.github/workflows/ci.yml   CI
Setup.ps1, Install-Skills.ps1, Create-DesktopShortcut.ps1, run.ps1
.env.example               configuration template
```

Tests: `cd web && npm test` (crypto: replay, tamper, reflection, staleness). The desktop host classes and the phone app were also verified end to end against a local relay.

## License

[PolyForm Noncommercial License 1.0.0](LICENSE). Anyone may use, modify and share Levity for noncommercial purposes (personal use, learning, research, hobby projects, nonprofits and similar). **Commercial use, including selling it or using it to make money, is not permitted** without a separate license from the author. This makes the source available but not "open source" in the OSI sense.

Third-party code keeps its own licenses: `wwwroot/marked.min.js` (MIT), `wwwroot/highlight.min.js` and `github-dark.min.css` (BSD-3-Clause), and the npm and NuGet dependencies listed in `web/package.json`, `relay/package.json` and `AntigravitySidebar.csproj`.
