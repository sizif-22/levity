# Running Levity

Levity is a Windows desktop sidebar (and optional phone app) for the **Antigravity CLI** (`agy`).
This guide takes you from a fresh clone to a working app.

## 1. What you need

| Tool | Needed for | Get it |
|---|---|---|
| Windows 10 or 11 (x64) | everything | |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | building the desktop app | to only *run* a build, the .NET 8 Desktop Runtime is enough |
| [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | the sidebar UI | already included in Windows 11 and current Windows 10 |
| `agy` (Antigravity CLI) | the AI itself | install it, then check `agy --help` works |
| [Node.js 20+](https://nodejs.org) | **only** the phone app and relay | skip if you only want the desktop sidebar |
| Cloudflare account (free) | **only** phone access (relay) | skip for desktop only |
| A static host (Vercel, Netlify, Cloudflare Pages, ...) | **only** phone access (web app) | skip for desktop only |

## 2. Quick start (desktop)

```powershell
git clone https://github.com/sizif-22/levity.git
cd levity
.\Setup.ps1 -Skills -Shortcuts      # checks tools, creates .env, builds, installs skills, makes shortcuts
.\publish\Levity.exe                # or press Win + Alt + A after it starts
```

`Setup.ps1` options: `-Skills` installs the agent skills, `-Shortcuts` adds Desktop and Start Menu shortcuts, `-Web` also builds the phone app.

### Manual build

```powershell
copy .env.example .env
dotnet publish -c Release -r win-x64 --self-contained false -o publish
copy .env publish\.env              # Levity reads .env next to Levity.exe
.\publish\Levity.exe
```

For development use `dotnet run` (it reads `.env` from the build output folder; set `LEVITY_ENV_FILE` to point at your own file).

## 3. Using it

- `Win + Alt + A` shows or hides the sidebar; the tray icon has the same actions.
- The **folder bar** under the header shows where the agent works. Click it to switch projects, **Choose folder...** for any folder, or **No project** for a general chat.
- Type a message and press Enter. Messages sent while the AI is busy are **queued**; the red square stops the current response.
- `/cmd{$git status}` runs a command directly in PowerShell, with no AI and no tokens. `cd` changes the working folder. Add `-b` to start a long command in the background (`/cmd{$npm run dev -b}`), then watch or stop it in the **Jobs** panel. Without the `$` the agent runs the command.
- The clock icon is **Chat history**: every chat is saved and can be reopened or deleted.
- The grid icon **reviews projects**: one agent session per project to refresh README, `AGENTS.md` and skills. It uses tokens, so pick projects first.

## 4. Configuration (`.env`)

Copy `.env.example` to `.env`. All values are optional.

| Variable | Meaning | Default |
|---|---|---|
| `LEVITY_RELAY_URL` | Your relay address (`wss://...`) | empty, set it in the app |
| `LEVITY_APP_URL` | Where you host the phone app | empty, set it in the app |
| `LEVITY_PROJECTS_ROOT` | Folder scanned for projects | your user folder |
| `AGY_PATH` | Full path to `agy.exe` | `%LOCALAPPDATA%\agy\bin\agy.exe`, then `PATH` |
| `ANTIGRAVITY_IDE_PATH` | Full path to `antigravity.cmd` | `%LOCALAPPDATA%\Programs\Antigravity IDE\bin`, then `PATH` |
| `LEVITY_DIAGRAM_EFFORT` | Assistant effort for architecture diagrams: `low`, `medium`, `high` | `low` |
| `LEVITY_DIAGRAM_TIMEOUT` | Seconds to wait for a diagram (30 to 1800) | `300` |
| `LEVITY_SIDEBAR_WIDTH` | Sidebar width in px | `460` |
| `LEVITY_SETTINGS_DIR` | Settings, chat history and WebView cache | `%APPDATA%\Levity` |
| `LEVITY_SKILLS_DIR` | Target for `Install-Skills.ps1` | `%USERPROFILE%\.gemini\config\skills` |
| `LEVITY_ENV_FILE` | Use this file instead of searching | not set |

How it is read: `LEVITY_ENV_FILE`, then `.env` next to `Levity.exe`, then `.env` in the settings folder. A variable already set in your shell wins over the file. The relay, app address and projects folder are **first-run defaults**: once you change them in the app they are saved in `settings.json` and win from then on. Never commit your `.env` (it is git-ignored).

## 5. Phone access (optional)

The phone talks to your PC through a small relay. Nothing listens on your PC; both ends connect outward and messages are end-to-end encrypted (see the security model in [PHONE.md](PHONE.md#security-model)). The steps below are the short version; [PHONE.md](PHONE.md) has the full guide.

1. **Relay** (Cloudflare Worker, free plan is enough):
   ```powershell
   cd relay
   npm install
   npx wrangler login
   npx wrangler deploy
   ```
   Note the printed address and use it with `wss://`, e.g. `wss://levity-relay.<you>.workers.dev`.
2. **Phone app**: build and host the `web` folder on any static host.
   - Vercel: import the repo, **Root Directory** `web`, framework **Vite**.
   - Anywhere else: `cd web && npm ci && npm run build`, then upload `web/dist`. The host must serve `web/vercel.json`'s security headers or equivalent, and must use HTTPS (the phone camera and crypto need it).
3. In Levity open the **phone icon**, paste the relay address and the phone app address (or put them in `.env`), then **Save**.
4. **Pair**: *Pair a new phone* shows a QR code. Open the phone app on the phone and scan it from inside the app. Approve the request on the PC after checking both screens show the same 4-digit code.

Revoke a phone from the same panel at any time. After you change the web app, redeploy it so phones get the new version.

## 6. Agent skills

`agent/skills` holds skills for the agent, including **Fast Learner**: when a simple task takes unexpectedly long, the agent saves the working solution as a new skill so the next run is instant. `Install-Skills.ps1` copies them to the agent's global skills folder and adds the Fast Learner rule to its global `GEMINI.md`.

```powershell
.\Install-Skills.ps1             # install (skips ones you already have)
.\Install-Skills.ps1 -Force      # replace existing copies
.\Install-Skills.ps1 -Uninstall  # remove them and the rule
```

Start a new chat afterwards. Included skills: `fast-learner`, `windows-wallpaper-manager`, `open-in-antigravity-ide`.
The rule is global for the agent, so it applies in every folder, not only Levity's.

## 7. Where things are stored

`%APPDATA%\Levity` (or `LEVITY_SETTINGS_DIR`):

- `settings.json`: settings; paired-phone keys are encrypted with Windows DPAPI for your user.
- `sessions\`: saved chats, one JSON file each. Delete a file to delete that chat.

`publish\*.log` hold local debug output and can contain your prompts. Logs are git-ignored; do not share them.

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| "Could not locate agy.exe" | Install `agy`, or set `AGY_PATH` in `.env`. |
| Blank sidebar | Install the WebView2 Runtime. |
| Nothing happens on `Win + Alt + A` | Another app owns the hotkey. Use the tray icon. |
| Phone says "PC offline" | Levity must be running on the PC. It reconnects by itself. |
| Phone app shows old screens | Redeploy the `web` build. |
| Build fails with a .NET error | Check `dotnet --list-sdks` shows an 8.x SDK. |
| A background job will not stop | Use its Stop button in **Jobs**. Closing Levity also stops all jobs. |

## 9. Uninstall

Delete the `publish` folder and the shortcuts, run `.\Install-Skills.ps1 -Uninstall` if you installed the skills, and delete `%APPDATA%\Levity` to remove saved data.

## 10. License

[PolyForm Noncommercial 1.0.0](../LICENSE): free for personal, educational, research and other noncommercial use; commercial use is not permitted.
