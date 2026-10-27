# Testing

One command runs everything CI runs:

```powershell
.\Run-Tests.ps1                      # all suites
.\Run-Tests.ps1 -Install             # npm ci first (fresh clone)
.\Run-Tests.ps1 -Only relay,web      # just some suites
```

## Suites

| Suite | Where | What it checks | Needs |
|---|---|---|---|
| **relay** | `relay/` | typecheck, `wrangler` bundle, and integration tests that boot the real Worker locally (`wrangler dev`) and use real WebSockets: presence events, targeted and broadcast frames, room isolation, host replacement, bad and oversized frames, the 8-phone limit | Node 22+ |
| **web** (phone app) | `web/` | typecheck and production build; Node-runner logic tests (crypto: replay, tamper, reflection, staleness, chat reducer); Vitest + jsdom component tests (pairing link safety, PIN, Jobs, History, command highlight) | Node 22+ |
| **sidebar** (desktop UI) | `tests/sidebar/` | the real `wwwroot` page in jsdom with a fake WebView2 bridge, driven by the same messages the PC sends: chat and markdown rendering, no HTML injection, `/cmd` highlighting, queue, Stop, jobs, history, review Stop | Node 22+ |
| **desktop** (services) | `tests/Levity.Tests/` | xUnit (including the architecture diagram: safe project reading and secret redaction, evidence checks, the Mermaid compiler against hostile input, and the drawing itself in a real headless Edge or Chrome using the vendored Mermaid): `/cmd` parsing, `cd` and `-b` handling, `.env` loading, settings and defaults, chat session store, project scanner, encryption (including a compatibility check against the phone app's TypeScript), and real PowerShell jobs: output, exit codes, Stop, killing orphaned child processes | .NET 8 SDK, Windows, Node (interop test), Edge or Chrome (diagram drawing test; set `LEVITY_REQUIRE_BROWSER=1` to fail instead of skip when none is installed, as CI does) |
| **repo** | `tests/repo/Validate-Repo.ps1` | all PowerShell scripts parse, skills are well formed and portable, `Install-Skills.ps1` installs/skips/forces/uninstalls, `.env.example` matches the code and docs, no secrets or personal config tracked, license present, doc links work | PowerShell 7 |

## Running one suite by hand

```powershell
cd relay;           npm ci; npm run typecheck; npm run bundle; npm test
cd web;             npm ci; npm run build; npm test
cd tests/sidebar;   npm ci; npm test
dotnet test tests/Levity.Tests/Levity.Tests.csproj
pwsh tests/repo/Validate-Repo.ps1
```

The relay tests use port 8799 (set `RELAY_TEST_PORT` to change it).

## CI

`.github/workflows/ci.yml` runs these suites as parallel jobs on every push to `main` or `staging` and on pull requests into them. A final **CI passed** job succeeds only if every suite passed, so it is the one check to require in branch protection (Settings, Branches, add a rule for `main` and `staging`, require **CI passed**). On pushes the Windows build is attached to the run as an artifact for 7 days.

## What is not covered

- The WPF window itself (tray icon, hotkey, slide animation, WebView2 hosting) needs a desktop session and is checked by hand.
- Anything that calls the `agy` CLI for real (chat turns, project review) needs the CLI and an account; CI does not have them. The code around it (prompt building, queueing, cancel handling) is covered.
- The deployed Cloudflare Worker and Vercel app are not tested; the same code is tested locally.
