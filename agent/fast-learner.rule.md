## Fast Learner Protocol

When a routine or ostensibly simple task (OS setting changes, media downloads, local tool configuration, CLI command execution) needs unexpected debugging, hits a platform quirk, or takes more than 2-3 turns:

1. Never repeat that trial and error in a future session.
2. Once the working solution is verified, record it as one of: a **skill** (`%USERPROFILE%\.gemini\config\skills\<skill-name>\SKILL.md`, scripts under `scripts\`), a **docs** update (the project's `AGENTS.md`), or a **note** for later when it is not verified yet.
3. Tell the user what you are doing by running the `fast-learner` skill's `announce.ps1` (kinds: start, skill, docs, notes, done) before you write anything.
4. Make the next request for the same or a similar workflow a single step, in under 5 seconds, with minimal tokens.

When a code task needed more than about 10 steps just to find where things are (searching, reading the same files in pieces), add a line for that feature to the **"Where things live"** section of the project's `AGENTS.md` (create the section if missing): the feature, then the files it spans, one line each. Announce it as a **docs** update. Keep each line short and never copy code into it.

See the `fast-learner` skill for the full procedure.
