# Levity

Desktop sidebar and phone app for the **Antigravity CLI** (`agy`). Formerly "Antigravity Sidebar".

- **Desktop**: tray widget that slides in from the right edge (`Win + Alt + A`). WPF + WebView2.
- **Phone**: React/Vite app on Vercel. It talks to your PC through a tiny relay, end-to-end encrypted. **No port is opened on your PC.**
- **Project review**: one click runs `agy` over every project under a root folder (default `D:\`) to refresh README, `AGENTS.md` and `.agent/skills/`.

```
 phone (Vercel app) ──wss──▶  relay (Cloudflare Worker)  ◀──wss── PC (Levity.exe)
        └──────────── AES-256-GCM, key only on phone + PC ────────────┘
```

Vercel only serves static files and cannot hold WebSockets, so the relay is a separate ~100-line Cloudflare Worker (`relay/`). The relay forwards opaque encrypted frames between a room's PC and phones. It never holds a key.

## Run the desktop app

```powershell
.\run.ps1        # or Start-Sidebar.bat, or publish\Levity.exe
```

Build: `dotnet publish -c Release -r win-x64 --self-contained false -o publish` (needs the .NET 8 desktop runtime).

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
2. Scan it with the phone camera. The phone shows a 4-digit code and asks for access.
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
wwwroot/                   desktop sidebar UI (WebView2)
relay/                     Cloudflare Worker + Durable Object (room per key)
web/                       React + Vite phone app (deploy to Vercel)
```

Tests: `cd web && npm test` (crypto: replay, tamper, reflection, staleness). The desktop host classes and the phone app were also verified end to end against a local relay.
