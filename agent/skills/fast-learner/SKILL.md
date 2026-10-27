---
name: fast-learner
description: >-
  Autonomous capability that detects when simple or routine tasks (OS settings, downloads, tool quirks, CLI workflows)
  require prolonged troubleshooting or trial-and-error (>2-3 attempts or excessive time/tokens), and codifies the
  verified solution into a permanent, reusable skill under ~/.gemini/config/skills/.
---

# Fast Learner

The **Fast Learner** feature ensures that whenever a routine, simple, or OS-level task takes significant time, effort, or tokens to solve, it is immediately synthesized into a reusable, zero-friction skill.

---

## 1. Trigger Criteria

Activate the Fast Learner workflow automatically when:
- A routine task (e.g. changing an OS setting, querying an API, downloading specific media, handling CLI quirks) took multiple failed tool calls or back-and-forth debugging (>2 attempts).
- The solution required discovering non-obvious platform behavior (e.g. Windows 11 COM APIs vs legacy Win32, obscure CLI flags, credential handshakes).
- The user expresses frustration with task duration (e.g. "this took 500+ seconds").

---

## 2. Learning Procedure

When a task completes after overcoming friction:

1. **Identify the Ground Truth:**
   - What was the initial failure mode? (e.g. legacy API silent failure, incorrect query format).
   - What is the minimal, deterministic code/command that works 100% of the time?

2. **Package into an Executable Skill:**
   - Target location: `$env:USERPROFILE\.gemini\config\skills\<skill-name>\`
   - Create `SKILL.md` with:
     - Clear YAML frontmatter (`name`, concise third-person `description` explaining exact trigger conditions).
     - Explanation of the platform quirk or root issue.
     - Copy-paste ready CLI commands or script invocations.
   - If automation requires more than 3-4 lines of code, create a dedicated standalone script under `scripts/` (e.g. `scripts/run.ps1` or `scripts/helper.py`).

3. **Verify the Script:**
   - Execute the helper script once to ensure it runs with code 0 and requires zero manual adjustments.

4. **Confirm to the User:**
   - Briefly state that the skill has been codified and saved to the global skills registry, ready for future 1-turn instant execution.
