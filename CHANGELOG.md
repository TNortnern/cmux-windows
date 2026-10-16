# Changelog

## 1.1.2

### Session restore
- Reopening cmux brings back every workspace, tab, split and pane folder. This is now verified
  end to end: across clean closes, tabs you never opened in a session, and crashes. Shells are ended
  when cmux closes and start fresh in the same folders.
- A folder change in any pane now triggers an autosave. Before, only the focused pane's folder did,
  so a crash could lose where background panes were.
- The layout is snapshotted when Windows signs out, shuts down or restarts.
- The **Reopen workspaces, tabs, splits and folders on startup** opt-out now works. It existed in
  Settings but was never read.
- Removed the "Confirm close" setting, which was never implemented.

## 1.1.1

### Fixes
- **The window could open off-screen or minimized and never appear.** This hit multi-monitor and
  mixed-DPI setups, or anyone who closed or autosaved cmux while it was minimized (that saved the
  Windows `-32000` "minimized" position). Restored positions are now checked against the current
  monitors. If the title bar wouldn't be visible, the window opens centered on the primary screen.
  A minimized window saves its normal position instead.
- **Launching cmux while it's already running now brings the existing window to the front** (and
  restores it if minimized) instead of starting a second instance that competed for the same
  session file and control pipe.

## 1.1.0

### Highlights
- **New tabs and splits open in the focused pane's current directory**, including PowerShell
  (prompt hook, OSC 9;9), cmd.exe, and paths with spaces. This is verified end to end, with and
  without the session daemon.
- **About 75% less memory:** 320 → 74 MB idle and 505 → 122 MB with 12 busy panes. The tab-switch
  leak is fixed, and rendering uses software by default (the GPU stays available in Settings). See
  [docs/PERFORMANCE.md](docs/PERFORMANCE.md).
- **Keyboard shortcuts now work while a terminal has focus.** They follow Windows Terminal
  conventions (see the README). Ctrl+letter chords go to your shell again (Ctrl+W, Ctrl+D, …).
  AltGr characters on European layouts are no longer swallowed.

### Tabs and panes
- Ctrl+1..9 selects tabs. Tabs can be reordered by dragging. Tab rename and duplicate work.
- Directional pane focus (Alt+Arrow), keyboard resize (Alt+Shift+Arrow), and equalize that gives
  N panes equal space.
- Typing `exit` closes the pane. Closing the last pane closes the tab. Splitter positions survive
  layout rebuilds.
- Session restore is lazy: only the visible tab starts its shells. Layout is autosaved.

### Fixes
- The CLI hung against the app: the control pipe never read requests.
- Closing a pane never killed its shell (`Dispose` short-circuited `Kill`). Child processes now die
  with their pane (Job Object).
- OSC sequences terminated with `ESC \` were dropped (titles and cwd reports).
- With the session daemon on, closing a tab left its shells running in the daemon.
- Crash races between the PTY read thread and WPF. Notifications and the sidebar are now updated on
  the UI thread.
- Daemon responses could be matched to the wrong request.
- With the session daemon on, the first pane after a cold start was never persisted. Terminal
  queries (cursor position, device attributes) were answered twice.
- `settings.json` keys are matched case-insensitively, so hand-edited PascalCase keys no longer
  get silently ignored.

### Security
See [SECURITY.md](SECURITY.md). Summary:
- Per-user pipe ACLs and anti-squatting.
- UNC and remote cwd rejected.
- Links limited to `http`/`https`.
- ConPTY handles no longer inheritable.
- Approval gate for every AI agent action, and model output no longer typed into shells.
- Transcripts off by default, and broader secret redaction.

### Changes in behaviour
- The session daemon is now **opt-in** (Settings → Behavior). It roughly doubles per-pane memory.
- Software rendering is the default (Settings → Behavior → *Software rendering*, restart required).
- AI agent bash tool and AGENTS.md auto-discovery are off by default.
- Default scrollback is 5,000 lines (configurable; the setting now takes effect).

### Maintenance
- Dependencies updated (CommunityToolkit.Mvvm 8.4.2, ProtectedData 10.0.13, test SDK 18.x).
  System.Management and WebView2 were removed.
- No known vulnerable packages. CI fails the build if one appears.
- CI publishes app + daemon + CLI in one zip with a SHA-256, and creates a GitHub Release on `v*` tags.
- New app manifest (Per-Monitor V2 DPI, long paths). Version is set once in `Directory.Build.props`.
- `cmux rpc <COMMAND> key=value…` gives raw access to the control pipe for scripting.
- Tests went from 53 to 252, plus end-to-end scripts in `tools/bench/` covering keys, cwd, daemon, leaks and RAM.
