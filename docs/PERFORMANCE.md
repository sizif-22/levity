# Making the assistant faster

Most of the waiting in a Levity chat is not the AI thinking. It is shell start-ups and one model round trip per step.
A typical slow session looked like this (measured from saved chats): 29 shell commands averaging 8 seconds each, 24 single-file reads
(24 separate round trips), and 130,000 or more input tokens on the very first message.

## What Levity does for you

- **Fast / Deep switch** (top bar). Fast (the default) asks the assistant for low effort: quicker and cheaper, enough for most tasks.
  Deep asks for high effort for hard problems. The assistant restarts on the same conversation when you switch, so context is kept.
  You can only switch between answers.
- **One-line efficiency hint** added to the first message of each conversation: read several files at once, prefer one command that does
  the whole job, search with `rg` or `git grep`, do not re-verify what already worked. You never see it in the chat.
- **Step timings.** A tool step that took 3 seconds or more shows its time, so slow steps stand out.

## The one thing you do: skip your PowerShell profile for the assistant's shells

Every command the assistant runs starts a PowerShell. If your profile loads a prompt theme (oh-my-posh), predictions or shell
integrations, each command pays for them: about 7 seconds each on one machine, and over 40 seconds when the shell had to wait.
Levity sets `LEVITY_AGENT=1` for everything the assistant starts. Put this as the **first line** of your profile (`$PROFILE`, and the Windows
PowerShell profile too if you use both):

```powershell
if ($env:LEVITY_AGENT) { return }
```

Your own terminals are unaffected, because only the assistant's shells have the variable. Levity never edits your profile.

## Trim what the assistant reads on every turn

Everything in the assistant's tool list, skills and global rules is sent with each turn. Remove skills you do not use, keep rules short,
and archive old folders under `~/.gemini` (large backups and old conversation data do not help). Fewer input tokens means faster turns
and lower cost.
