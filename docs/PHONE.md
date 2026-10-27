# Use Levity from your phone

The phone app talks to your PC through a small relay. Nothing listens on your PC: both ends connect outward, and every message is end-to-end encrypted.

```
 phone (web app) ──wss──▶  relay (Cloudflare Worker)  ◀──wss── PC (Levity.exe)
        └──────────── AES-256-GCM, key only on phone + PC ────────────┘
```

The phone app is static files, which cannot hold WebSockets, so the relay is a separate ~100-line Cloudflare Worker (`relay/`). It forwards opaque encrypted frames between a room's PC and phones and never holds a key.

On the phone you can chat, stop a response, queue messages, run `/cmd{$...}` commands, watch and stop background jobs, reopen saved chats, start project reviews and view architecture diagrams (pinch to zoom, tap a component to open its file on the PC). Choosing a folder with the Windows folder picker stays on the PC.

## Set up (once)

1. **Relay** (Cloudflare, free plan is enough):
   ```bash
   cd relay && npm install && npx wrangler login && npx wrangler deploy
   ```
   Note the address it prints, e.g. `wss://levity-relay.<you>.workers.dev` (use `wss://`).
2. **Phone app** (any static host; Vercel shown): import the repo, set **Root Directory** to `web`, framework **Vite**. Or `cd web && npx vercel --prod`. No environment variables are needed.
3. In Levity: header phone icon, paste the relay address and the phone app address (or set `LEVITY_RELAY_URL` and `LEVITY_APP_URL` in `.env`), then **Save**.

## Pair a phone

1. Phone icon, **Pair a new phone**. A QR code appears (valid 5 minutes, single use).
2. On the phone open the Levity web app (on iPhone, add it to the Home Screen first and open it from there) and tap **Scan QR code**. Scan from inside the app, not with the Camera app: the Camera opens the link in a separate browser sheet whose storage the app cannot see. The phone shows a 4-digit code and asks for access. If the camera is unavailable, use **Paste a link instead**.
3. On the PC an approval dialog shows the phone name and the same code. Approve only if they match.
4. The PC hands that phone its own random key. Add the page to the Home Screen if you like.

Revoke any phone from the same panel; it is cut off immediately. Optional app passcode: phone, gear icon.

## Security model

| Threat | Mitigation |
|---|---|
| Someone on your network or the internet reaches your PC | Nothing listens. The PC only makes outbound connections. |
| Relay or web host is compromised or snoops | Keys never reach them. Frames are AES-256-GCM encrypted; the room id is a hash of the key. They cannot read chats or send commands. |
| Someone finds the QR | It expires in 5 minutes, works once, and a phone is trusted only after you approve it on the PC. |
| Replay or reflection of captured frames | Per-session counters, a 2-minute clock window, and a separate AAD per direction. |
| Lost or stolen phone | Revoke that device on the PC. Each phone has its own key. |
| Malware reading the phone's key | Stored as a non-extractable WebCrypto key in IndexedDB; optional passcode on top. |
| Keys at rest on the PC | Encrypted with Windows DPAPI (current user) in `settings.json`. |

Not covered: a malicious web deployment could serve altered JavaScript, so only deploy `web/` from this repo, from an account you control. **Anyone you pair can run commands on this PC** (the agent runs with `--dangerously-skip-permissions`), so pair only your own phones. The relay address is not secret and has no login, so someone could use your Worker's quota, but cannot read or inject anything.
