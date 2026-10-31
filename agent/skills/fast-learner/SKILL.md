---
name: fast-learner
description: >-
  Autonomous capability that detects when simple or routine tasks (OS settings, downloads, tool quirks, CLI workflows)
  require prolonged troubleshooting or trial-and-error (>2-3 attempts or excessive time/tokens), then records what was
  learned as a reusable skill, a project-docs update, or a note for later, and tells the user which one it is doing.
---

# Fast Learner

Whenever a routine, simple, or OS-level task takes significant time, effort, or tokens, turn what you learned into something reusable so the next run is one quick step. **Always tell the user what you are doing** (section 3): they see it as a notice in Levity.

---

## 1. Trigger Criteria

Activate when:
- A routine task (changing an OS setting, calling an API, downloading media, handling CLI quirks) took multiple failed tool calls or back-and-forth debugging (more than 2 attempts).
- The solution required discovering non-obvious platform behavior (Windows 11 COM APIs vs legacy Win32, obscure CLI flags, credential handshakes).
- The user expresses frustration with task duration (for example "this took 500+ seconds").

---

## 2. Choose one outcome

| Outcome | When | Where it goes |
|---|---|---|
| **skill** | The fix is verified, deterministic and reusable across projects | `$env:USERPROFILE\.gemini\config\skills\<skill-name>\SKILL.md`, with scripts under `scripts\` |
| **docs** | The learning belongs to the current project (its commands, setup, gotchas) | A short "Learned" section in the project's `AGENTS.md` (create it if missing). Documentation only, never source code |
| **notes** | The solution is not verified yet, uncertain, or a one-off not worth a skill | Saved for later by `announce.ps1` (section 3). Do not create a skill |

If a note from an earlier task now has a verified solution, promote it to a skill.

### Feature map ("Where things live")
Trigger: a code task took more than about 10 steps only to locate the code (many greps, the same file read in slices). The cost was exploration, not the change itself, so record the map once.
1. Open the project's `AGENTS.md` and find the `## Where things live` section (create it at the end if missing).
2. Add or update one line per feature: `- <feature>: path/a.ts (what it does), path/b.tsx (what it does), ...`. List the files a change to that feature touches end to end (UI, server action, backend function, schema, limits or config, tests).
3. Names and paths only: no code, no secrets, no line numbers (they go stale). Fix a line that was wrong instead of adding a second one.
4. Announce it as a **docs** update (section 3), then continue.

### Making a skill
1. Identify the ground truth: what failed first, and the minimal command or script that works every time.
2. Write `SKILL.md` with YAML front matter (`name` matching the folder, a third-person `description` saying exactly when to use it), the root cause, and copy-paste commands. If the automation needs more than 3-4 lines, put it in a script under `scripts\`.
3. Run the script once to confirm it exits with code 0 without manual changes.

---

## 3. Tell the user (required)

Run the announcement script, in PowerShell. It shows a notice in Levity (sidebar, phone, and a Windows notification when the sidebar is hidden) and keeps a log of skills, docs updates and notes.

```powershell
$a = "$env:USERPROFILE\.gemini\config\skills\fast-learner\scripts\announce.ps1"
& $a -Kind start -Message "Wallpaper took 8 attempts; saving what worked"   # when you decide to learn
& $a -Kind skill -Name windows-wallpaper-manager -Message "Set wallpaper via IDesktopWallpaper"
& $a -Kind docs  -Path AGENTS.md -Message "Added the build command and the port gotcha"
& $a -Kind notes -Message "yt-dlp needs --cookies-from-browser for this site; not verified yet"
& $a -Kind done  -Message "Saved 1 skill"                                   # when finished
```

Rules:
- Send `start` once, as soon as you decide to learn (before the work), and `done` once at the end.
- Send exactly one of `skill`, `docs` or `notes` for each thing you save, **before** you write it.
- Keep each message to one short sentence. Use at most 3-4 announcements in total, to save tokens.
- Notes are appended to `learned.jsonl` next to this skill. To reuse an earlier note, search it first with `Select-String -Path "$env:USERPROFILE\.gemini\config\skills\fast-learner\learned.jsonl" -Pattern "<keyword>"`.

---

## 4. Confirm to the user

After `done`, say in one sentence what was learned and where it is saved, so the next request can be handled in a single step.
