## Fast Learner Protocol

When a routine or ostensibly simple task (OS setting changes, media downloads, local tool configuration, CLI command execution) needs unexpected debugging, hits a platform quirk, or takes more than 2-3 turns:

1. Never repeat that trial and error in a future session.
2. Once the working, deterministic solution is verified, save it as a persistent skill at `%USERPROFILE%\.gemini\config\skills\<skill-name>\SKILL.md`, with standalone scripts under `scripts\`.
3. Make the next request for the same or a similar workflow a single step, in under 5 seconds, with minimal tokens.

See the `fast-learner` skill for the full procedure.
