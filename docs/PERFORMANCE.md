# Memory: how it's measured and what changed in 1.1.0

All numbers are **private bytes of `cmuxw.exe`** (shells excluded), self-contained win-x64 Release
publish, measured on Windows 11 (Intel Arc graphics), 2026-10-15.

| Scenario | 1.0.6 (upstream) | 1.1.0 (default) | 1.1.0 with GPU rendering |
|---|---:|---:|---:|
| Idle, 1 tab / 1 pane | 320 MB | **74 MB (−77%)** | 264 MB |
| 4 tabs × 3 panes, each pane printed 20,000 lines × 150 chars | 505 MB | **122 MB (−76%)** | 317 MB |
| …then 60 tab switches | 496 MB | **122 MB (−75%)** | 364 MB |

1.0.6 numbers are the average of two runs. Threads at idle: 124 (1.0.6) → 20 (1.1.0).

### Software rendering is the default

Most of a WPF app's memory is not .NET at all. With Direct3D rendering, WPF initializes the GPU
driver inside the process; on Intel Arc that's ~190 MB private and ~100 driver threads. 1.1.0
renders with WPF's software renderer by default (Settings → Behavior → *Software rendering*).

Cost check: maximized window, two panes, each flooded with 30,000 colored 180-column lines,
two runs per mode:

| | GPU | Software |
|---|---:|---:|
| Private memory | 289–306 MB | 102–104 MB |
| App CPU time during the 25 s flood | 12.4 s / 19.7 s | 14.8 s / 16.0 s |

There was no measurable CPU penalty for terminal output. If a very large or 4K window ever
feels slow, turn the setting off.

## Leak check: tab switching

Live objects after a forced GC (`dotnet-gcdump`), 3 tabs + splits, clean profile:

| Live `TerminalControl` objects | start | 200 switches | 400 switches | 600 switches |
|---|---:|---:|---:|---:|
| 1.0.6 | 7 | 357 | 707 | (keeps growing) |
| 1.1.0 | 2 | 52 | 48 | 48 (bounded; GC heap flat at 7.4 MB) |

In 1.0.6 every tab switch orphaned the visible panes' controls. They stayed subscribed to their sessions,
kept a cursor timer running, and repainted on every output chunk until the app was closed.

## Where the memory goes

The managed (.NET) heap is only ~5–12 MB. The rest is native: WPF's render pipeline, DirectWrite,
and in GPU mode the graphics driver (see above).

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
| Software rendering by default | GPU driver never initializes in-process (−190 MB, −100 threads) |
| Agent services created only when the agent is enabled | nothing loaded when it's off |

## Reproduce

```powershell
# RAM scenarios (wipes %LOCALAPPDATA%\cmux; opens and closes cmux windows). -GpuRendering for GPU mode.
pwsh tools/bench/ram-bench.ps1 -AppExe <publish>\cmuxw.exe -Label mybuild

# Leak check (needs: dotnet tool install -g dotnet-gcdump)
pwsh tools/bench/leak-check.ps1 -AppExe <publish>\cmuxw.exe

# New tab / split open in the focused pane's folder (in-process and daemon)
pwsh tools/bench/cwd-check.ps1 -AppExe <publish>\cmuxw.exe
pwsh tools/bench/cwd-check.ps1 -AppExe <publish>\cmuxw.exe -Daemon

# Real keystrokes: tab/split/close shortcuts work with a terminal focused (keep the window in front)
pwsh tools/bench/keys-check.ps1 -AppExe <publish>\cmuxw.exe

# Session daemon: a shell survives closing and reopening cmux
pwsh tools/bench/daemon-check.ps1 -AppExe <publish>\cmuxw.exe

# Workspaces, tabs, splits and folders survive close, unvisited tabs and crashes; opt-out works
pwsh tools/bench/restore-check.ps1 -AppExe <publish>\cmuxw.exe

# Window always opens on a visible monitor; a second launch focuses the running window
pwsh tools/bench/window-check.ps1 -AppExe <publish>\cmuxw.exe
```

`keys-check` drives real keystrokes, so it needs cmux to be the foreground window. Windows sometimes
refuses that to a background script. The script verifies the foreground before typing, but expect an
occasional first-chord miss on a busy desktop; rerun before treating it as a bug.

Note: the upstream 1.0.6 control pipe never reads requests (the CLI hangs), so the 1.0.6 numbers
above were measured with only the 1.1.0 pipe transport files (`NamedPipeServer.cs`,
`NamedPipeClient.cs`, `PipeLineReader.cs`, `PipeSecurityFactory.cs`) dropped in, to make it scriptable.
No memory-related code was changed in that baseline.
