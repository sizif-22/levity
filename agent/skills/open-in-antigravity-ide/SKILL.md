---
name: open-in-antigravity-ide
description: >-
  Use this skill when asked to open a project, repository, or directory in
  Antigravity IDE on Windows, or when troubleshooting why an Antigravity IDE
  window failed to appear or take foreground focus.
---

# Open in Antigravity IDE (Windows)

This skill provides the verified procedure for opening workspaces or directories in Antigravity IDE on Windows, accounting for running instances and Windows OS focus constraints.

---

## 1. CLI Commands & Aliases

On Windows, both `ag` and `antigravity` are registered as aliases or CLI wrappers pointing to:
- `%LOCALAPPDATA%\Programs\Antigravity IDE\bin\antigravity.cmd`

Both commands accept standard VS Code CLI flags (`-n`, `-r`, `--status`, etc.).

---

## 2. Key Challenges & Windows Behavior

1. **Existing Running Instances:**
   If Antigravity IDE is already open with another project, running `ag <path>` without flags simply sends the environment to the running main process and exits immediately with code 0. It often does not open a visible new window unless explicitly instructed.
2. **Foreground Focus Lock (Focus Stealing Prevention):**
   Windows blocks background CLI processes from popping GUI windows directly over the user's active foreground window. Even when successfully launched, the new window will sit behind current applications or flash its icon on the Windows Taskbar.

---

## 3. Reliable Launch Procedures

### A. Force Open in a New Window (Recommended)
Always pass the `-n` / `--new-window` flag with the target directory path:

```powershell
ag -n "<absolute-or-relative-path>"
# or
antigravity -n "<absolute-or-relative-path>"
```

### B. Reuse the Current Active Window
If the user prefers switching the current window to the new folder:

```powershell
ag -r "<absolute-or-relative-path>"
```

---

## 4. Verification

After executing the command, verify that the window was created and the workspace folder is indexed by checking process and workspace status:

```powershell
ag --status
```

Look at the **`Workspace Stats:`** section near the bottom of the output:
```text
Workspace Stats: 
|  Window (<folder-name> - Antigravity IDE)
|    Folder (<folder-name>): <file-count> files
```

If the folder is listed under `Workspace Stats:`, the IDE has loaded the workspace.

---

## 5. User Communication Guideline

Because Windows prevents background processes from stealing focus, always notify the user after running the command:
1. Confirm that the command was executed and the workspace is loaded.
2. Remind the user to click the **Antigravity IDE** icon on their **Windows Taskbar** or press <kbd>Alt</kbd> + <kbd>Tab</kbd> to bring the window into focus.
