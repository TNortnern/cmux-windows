# cmux windows

A dark, keyboard-first terminal multiplexer for Windows. Tabs, split panes, workspaces, and a sidebar that shows git branch, working directory, and agent notifications, built natively with WPF and the Windows ConPTY API.

cmux windows is a Windows port of the cmux workflow. It is a fork of [mkurman/cmux-windows](https://github.com/mkurman/cmux-windows) (MIT).

---

## Install

1. Download `cmux-windows-<version>-win-x64.zip` and its `.sha256` file from the [Releases](../../releases) page.
2. Optional: verify the download with `Get-FileHash -Algorithm SHA256 cmux-windows-<version>-win-x64.zip` and compare it with the `.sha256` file.
3. Unzip the archive anywhere and run `cmuxw.exe`.

The archive is self-contained and includes the .NET runtime, so you don't need to install .NET.

> **SmartScreen:** the binaries are not code-signed yet. Windows may show "Windows protected your PC" the first time. Click **More info**, then **Run anyway**. Check the SHA256 first if you want to be sure the file is the one published here.

### Requirements

- Windows 10 version 1809 or later, or Windows 11
- x64 processor

### What's in the archive

| File | Purpose |
|---|---|
| `cmuxw.exe` | The desktop app |
| `cmux.exe` | Command-line client for automation (see [CLI usage](#cli-usage)) |
| `cmux-daemon.exe` | Optional session daemon (see [Session daemon](#session-daemon-optional)) |

Keep the three files in the same folder. Add that folder to `PATH` if you want `cmux` available everywhere.

---

## Features

- **Workspaces** with a sidebar showing the git branch, current directory, and unread notifications.
- **Tabs (surfaces)** inside each workspace.
- **Split panes**, right or down, with keyboard focus movement and zoom.
- **New tabs and splits open in the current directory.** This works through shell integration:
  - PowerShell and `cmd.exe`: cmux installs a prompt hook that reports the directory (OSC 9;9).
  - `cmd.exe` also works without the hook, because cmux can read its live directory from the process.
  - Git Bash: add this to `~/.bashrc` so the prompt reports a Windows path:
    `PROMPT_COMMAND='printf "\e]9;9;%s\e\\" "$(cygpath -w "$PWD")"'`
  - WSL: Linux paths are not Windows paths, so a split from a WSL pane opens in your Windows home directory.
  - Other shells still work; new tabs just start in the configured directory.
- **Notifications** from coding agents (OSC 9, 99, 777) with unread tracking and Windows toast alerts.
- **Command palette**, search, snippets, and command history.
- **Themes and fonts** for the terminal, with custom colors.
- **Session restore**: window layout, workspaces, tabs, and working directories come back on startup.

---

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| **Tabs** | |
| `Ctrl+Shift+T` | New tab (in current pane's folder) |
| `Ctrl+Shift+W` | Close pane (closes tab if last pane) |
| `Ctrl+Tab / Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+1..8 / Ctrl+9` | Go to tab N / last tab |
| **Panes** | |
| `Alt+Shift+=` | Split right (in current pane's folder) |
| `Alt+Shift+-` | Split down (in current pane's folder) |
| `Alt+Shift+D` | Split along longer side |
| `Alt+Arrow` | Focus pane in direction |
| `Alt+Shift+Arrow` | Resize pane |
| `Ctrl+Shift+Z` | Zoom pane toggle |
| `Ctrl+Shift+E` | Equalize panes |
| **Terminal** | |
| `Ctrl+Shift+C / Ctrl+Shift+V` | Copy / paste |
| `Ctrl+Shift+F` | Search in terminal |
| `Ctrl+Backspace` | Delete previous word |
| **Workspaces** | |
| `Ctrl+Shift+N` | New workspace |
| `Ctrl+Alt+1..9` | Jump to workspace |
| **View and tools** | |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+Shift+B` | Toggle sidebar |
| `Ctrl+Shift+I` | Notification panel |
| `Ctrl+Shift+U` | Jump to latest unread |
| `Ctrl+Shift+S` | Session vault |
| `Ctrl+Shift+L` | Command logs |
| `Ctrl+Shift+H` | Insert last command |
| `Ctrl+Alt+H` | Command history picker |
| `Ctrl+Shift+A` | Agent chat (when enabled) |
| `Ctrl+,` | Settings |

All shortcuts work while a terminal has focus. Ctrl+letter chords are left for your shell (Ctrl+W, Ctrl+D, etc.).

---

## Session daemon (optional)

By default each terminal pane runs in the cmux process. When the window closes, its shells close with it.

Turn on **Settings > Behavior > Session Daemon** to run shells in `cmux-daemon.exe` instead. Shells then keep running after cmux closes. The trade-off is memory: each pane uses more RAM in daemon mode. The setting takes effect the next time cmux starts.

The daemon is off by default and never starts unless you enable it.

---

## AI agent (optional, off by default, approval required)

cmux can host an AI assistant that runs commands in your shell. This is **off by default**. Enable it in **Settings > Agent**, then configure a provider and API key. Nothing is sent to a model until you do.

When the agent is enabled:

- It can only run commands after you approve each one.
- API keys are stored with Windows DPAPI (per-user encryption) in `%LOCALAPPDATA%\cmux\secrets.json`, never in plain text.
- Conversation history is kept only if you turn on conversation memory.

---

## Privacy

- **Terminal transcripts are off by default.** Turn on "Capture On Close" or "Capture On Clear" in Settings > Behavior if you want them saved. Transcripts are scrubbed for common secrets (tokens, passwords, `Authorization` headers, and similar) before they are written.
- **Command logs** record commands you run in cmux so you can search history. Secrets are redacted before they are stored. Logs older than the retention period are deleted automatically (90 days by default; set it to 0 to keep them).
- **Logs and data live in `%LOCALAPPDATA%\cmux`.** The diagnostic log is `%LOCALAPPDATA%\cmux\logs\cmux.log` (rotated at 1 MB). It never contains terminal output or command lines.
- **No telemetry.** cmux makes no network calls except the ones you configure (the AI agent and its tools).

To delete everything cmux has stored, close the app and remove `%LOCALAPPDATA%\cmux`.

---

## Build from source

Requirements:

- Windows 10 1809 or later, or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

```powershell
git clone https://github.com/<your-fork>/cmux-windows.git
cd cmux-windows

dotnet build Cmux.sln -c Debug     # warnings are errors
dotnet test tests/Cmux.Tests        # unit tests
dotnet run --project src/Cmux/Cmux.csproj -c Debug
```

### Release build

Build the app, the session daemon, and the CLI into one folder:

```powershell
dotnet publish src/Cmux/Cmux.csproj -c Release -r win-x64 --self-contained -o publish/app
dotnet publish src/Cmux.Daemon/Cmux.Daemon.csproj -c Release -r win-x64 --self-contained -o publish/app
dotnet publish src/Cmux.Cli/Cmux.Cli.csproj -c Release -r win-x64 --self-contained -o publish/app
```

The result is `publish/app/cmuxw.exe`, `publish/app/cmux-daemon.exe`, and `publish/app/cmux.exe`.

NuGet packages are pinned by lock files (`packages.lock.json`). CI restores with `dotnet restore Cmux.sln --locked-mode`, so a dependency change must update the lock files in the same commit.

---

## CLI usage

`cmux.exe` talks to the running app over the named pipe `\\.\pipe\cmux`.

```powershell
# Send a notification (for example, from an agent hook)
cmux notify --title "Claude Code" --body "Waiting for input"

# Workspaces
cmux workspace list
cmux workspace create --name "My Project"
cmux workspace select --index 0

# Tabs and panes
cmux surface create
cmux split right
cmux split down

# Status
cmux status
```

---

## Screenshots

<details>
  <summary>Open screenshots</summary>

  <p><strong>Main workspace view</strong></p>
  <img src="assets/screenshots/1.jpg" alt="cmux main workspace" width="1000" />

  <p><strong>Snippets panel</strong></p>
  <img src="assets/screenshots/2.jpg" alt="cmux snippets panel" width="700" />

  <p><strong>Command logs window</strong></p>
  <img src="assets/screenshots/3.jpg" alt="cmux command logs" width="1000" />
</details>

---

## Project layout

```text
src/
  Cmux/         WPF desktop app (views, controls, themes)            -> cmuxw.exe
  Cmux.Core/    terminal engine, models, services, persistence, IPC
  Cmux.Daemon/  optional session daemon                              -> cmux-daemon.exe
  Cmux.Cli/     command-line client                                  -> cmux.exe
tests/
  Cmux.Tests/   unit tests
```

---

## Credits

cmux windows is based on [mkurman/cmux-windows](https://github.com/mkurman/cmux-windows) by Mariusz Kurman, released under the MIT License. See [LICENSE](LICENSE).
