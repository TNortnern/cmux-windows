<#
.SYNOPSIS
  End-to-end keyboard check: sends real keystrokes to a terminal-focused cmux window and
  verifies tab/split/close shortcuts via `cmux rpc`. Also checks that the shell, not cmux,
  receives plain Ctrl+letter chords.

.NOTES
  Uses SendInput, so the cmux window must stay in the foreground while it runs (~30 s).
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [string] $Cli = (Join-Path (Split-Path $AppExe) "cmux.exe")
)
$ErrorActionPreference = "Stop"

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Kb {
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential, Size = 40)] struct IN { public uint type; public KI ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  static void K(ushort vk, bool up) { var i = new IN[1]; i[0].type = 1; i[0].ki.vk = vk; i[0].ki.flags = up ? 2u : 0u; SendInput(1, i, Marshal.SizeOf<IN>()); }
  public static void Chord(params ushort[] keys) { foreach (var k in keys) K(k, false); for (int j = keys.Length - 1; j >= 0; j--) K(keys[j], true); }
}
"@
$CTRL = 0x11; $SHIFT = 0x10; $ALT = 0x12; $TAB = 0x09; $PLUS = 0xBB; $MINUS = 0xBD
$T = 0x54; $W = 0x57; $K1 = 0x31

function Rpc([string[]] $a) { (& $Cli rpc @a) -join "`n" }
function PaneCount([int] $surface) { @((Rpc "PANE.LIST", "surfaceIndex=$surface" | ConvertFrom-Json).panes).Count }
function Focus-App {
    # Windows' focus-stealing protection can refuse SetForegroundWindow from a background process;
    # a lone Alt tap unlocks it. Never send keys unless cmux really is the foreground window.
    for ($i = 0; $i -lt 10; $i++) {
        if ([Kb]::GetForegroundWindow() -eq $script:hwnd) { return }
        [Kb]::Chord($ALT); [Kb]::SetForegroundWindow($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 300
    }
    throw "could not bring cmux to the foreground; keystroke results would be meaningless"
}
function Press([scriptblock] $chord) { Focus-App; Start-Sleep -Milliseconds 200; & $chord; Start-Sleep -Milliseconds 1500 }

Get-Process cmuxw -ErrorAction SilentlyContinue | Stop-Process -Force
$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "cmux"
if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
$p = Start-Process $AppExe -PassThru
Start-Sleep 8
$script:hwnd = (Get-Process -Id $p.Id).MainWindowHandle

$results = @()
function Check([string] $name, [bool] $ok, [string] $detail) { $script:results += [pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail } }

try {
    $panes0 = PaneCount 1
    Press { [Kb]::Chord($ALT, $SHIFT, $PLUS) }
    $panes1 = PaneCount 1
    Check "Alt+Shift+= splits (terminal focused)" ($panes1 -eq $panes0 + 1) "$panes0 -> $panes1 panes"

    Press { [Kb]::Chord($ALT, $SHIFT, $MINUS) }
    $panes2 = PaneCount 1
    Check "Alt+Shift+- splits" ($panes2 -eq $panes1 + 1) "$panes1 -> $panes2 panes"

    Press { [Kb]::Chord($CTRL, $SHIFT, $W) }
    $panes3 = PaneCount 1
    Check "Ctrl+Shift+W closes pane" ($panes3 -eq $panes2 - 1) "$panes2 -> $panes3 panes"

    Press { [Kb]::Chord($CTRL, $SHIFT, $T) }
    $tab2 = Rpc "PANE.LIST", "surfaceIndex=2"
    Check "Ctrl+Shift+T opens a new tab" ($tab2 -notmatch "error") "tab 2 exists"

    Press { [Kb]::Chord($CTRL, $K1) }
    $sel1 = (Rpc "PANE.LIST" | ConvertFrom-Json).surface.id
    $tab1 = (Rpc "PANE.LIST", "surfaceIndex=1" | ConvertFrom-Json).surface.id
    Check "Ctrl+1 selects tab 1" ($sel1 -eq $tab1) "selected=$sel1"

    Press { [Kb]::Chord($CTRL, $TAB) }
    $sel2 = (Rpc "PANE.LIST" | ConvertFrom-Json).surface.id
    $tab2id = (Rpc "PANE.LIST", "surfaceIndex=2" | ConvertFrom-Json).surface.id
    Check "Ctrl+Tab selects next tab" ($sel2 -eq $tab2id) "selected=$sel2"

    # Plain Ctrl+W must reach the shell (readline kill-word), not close anything.
    $before = PaneCount 2
    Press { [Kb]::Chord($CTRL, $W) }
    Check "Ctrl+W is left to the shell" ((PaneCount 2) -eq $before) "panes unchanged: $before"

    Check "app still alive" (-not $p.HasExited) ""
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
$results | Format-Table -AutoSize | Out-String -Width 200
if ($results.Pass -contains $false) { exit 1 }
