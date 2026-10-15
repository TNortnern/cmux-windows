# Memory: how it's measured and what changed in 1.1.0

All numbers are **private bytes of `cmuxw.exe`** (shells excluded), self-contained win-x64 Release
publish, measured on Windows 11 (Intel Arc graphics), 2026-10-15. Two runs per build, averaged.

| Scenario | 1.0.6 (upstream) | 1.1.0 | Change |
|---|---:|---:|---:|
| Idle, 1 tab / 1 pane | 320 MB | 263 MB | **−18%** |
| 4 tabs × 3 panes, each pane printed 20,000 lines × 150 chars | 505 MB | 328 MB | **−35%** |
| …then 60 tab switches | 496 MB | 356 MB | **−28%** |

Working set moves the same way (idle 390 → 320 MB, loaded 582 → 397 MB).

## Leak check: tab switching

Live objects after a forced GC (`dotnet-gcdump`), 3 tabs + splits, clean profile:

| Live `TerminalControl` objects | start | 200 switches | 400 switches | 600 switches |
|---|---:|---:|---:|---:|
| 1.0.6 | 7 | 357 | 707 | (keeps growing) |
| 1.1.0 | 2 | 54 | 48 | 48 (bounded) |

In 1.0.6 every tab switch orphaned the visible panes' controls. They stayed subscribed to their sessions,
kept a cursor timer running, and repainted on every output chunk until the app was closed.

## Where the memory goes

The managed (.NET) heap is only ~5–12 MB at idle. Most of the remaining ~260 MB is native:
WPF's render pipeline and the **GPU driver** that Direct3D loads into the process (on Intel Arc,
`igc64.dll` alone maps ~80 MB and brings its own thread pool). WPF loads the driver even in
software-render mode, so cmux can't opt out of it. That baseline is shared by every WPF app.

What cmux *does* control, and what 1.1.0 changed:

| Change | Effect |
|---|---|
| Terminal cell packed from 20 to 12 bytes | −40% per screen and per scrollback row |
| Scrollback rows trimmed to their last non-blank cell; evicted rows reused | typical shell output uses a fraction of a 200-col row |
| Scrollback default 10,000 → 5,000 lines, and the setting is now actually used | it was ignored before |
| Detach controls on tab switch / pane close; prune the control cache | fixes the leak above |
| Hidden tabs and zoom-hidden panes don't render; cursor blinks only in the focused pane | no idle repaint |
| Shared, capped, frozen brush cache; merged background runs | fewer allocations per frame |
| Lazy restore: only the selected tab of the selected workspace starts shells at launch | fewer shells and buffers at startup |
| Session daemon is opt-in (was always attempted) | in-process mode keeps one buffer per pane, not two |
| WMI (System.Management) replaced with a Toolhelp snapshot | no WMI stack in-process, no 5-second WMI queries |
| WebView2 package and dead browser control removed | smaller install |
| Agent services created only when the agent is enabled | nothing loaded when it's off |

## Reproduce

```powershell
# RAM scenarios (wipes %LOCALAPPDATA%\cmux; opens and closes cmux windows)
pwsh tools/bench/ram-bench.ps1 -AppExe <publish>\cmuxw.exe -Label mybuild

# Leak check (needs: dotnet tool install -g dotnet-gcdump)
pwsh tools/bench/leak-check.ps1 -AppExe <publish>\cmuxw.exe

# New tab / split open in the focused pane's folder (in-process and daemon)
pwsh tools/bench/cwd-check.ps1 -AppExe <publish>\cmuxw.exe
pwsh tools/bench/cwd-check.ps1 -AppExe <publish>\cmuxw.exe -Daemon
```

Note: the upstream 1.0.6 control pipe never reads requests (the CLI hangs), so the 1.0.6 numbers
above were measured with only the 1.1.0 pipe transport files (`NamedPipeServer.cs`,
`NamedPipeClient.cs`, `PipeLineReader.cs`, `PipeSecurityFactory.cs`) dropped in, to make it scriptable.
No memory-related code was changed in that baseline.
