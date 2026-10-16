<#
.SYNOPSIS
  End-to-end window placement checks: the main window must always open on a visible monitor,
  a second launch must bring the running window forward (and exit), and closing while
  minimized must not save the -32000 sentinel.
#>
param([Parameter(Mandatory)] [string] $AppExe)
$ErrorActionPreference = "Stop"

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WinChk {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref R r, uint flags);
  public static bool TitleBarOnScreen(IntPtr h) { R r; GetWindowRect(h, out r); var t = new R { L = r.L, T = r.T, Rt = r.Rt, B = r.T + 40 }; return MonitorFromRect(ref t, 0) != IntPtr.Zero; }
}
"@

$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "cmux"
$sessionFile = Join-Path $dataDir "session.json"
$results = @()
function Check([string] $name, [bool] $ok, [string] $detail = "") { $script:results += [pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail } }
function Stop-All { Get-Process cmuxw, cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1 }
function Start-App { $p = Start-Process $AppExe -PassThru; Start-Sleep 6; $p.Refresh(); $p }
function Rect($p) { $r = New-Object WinChk+R; [void][WinChk]::GetWindowRect($p.MainWindowHandle, [ref]$r); "($($r.L),$($r.T))-($($r.Rt),$($r.B))" }
function Write-Session([double] $x, [double] $y) {
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    @{ window = @{ x = $x; y = $y; width = 1000; height = 700; isMaximized = $false; sidebarWidth = 280; sidebarVisible = $true } } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $sessionFile
}

try {
    foreach ($case in @(@{ Name = "minimized sentinel (-32000)"; X = -32000; Y = -32000 },
                        @{ Name = "monitor that no longer exists (9000, 9000)"; X = 9000; Y = 9000 })) {
        Stop-All; if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
        Write-Session $case.X $case.Y
        $p = Start-App
        Check "saved $($case.Name) -> opens on screen" ([WinChk]::TitleBarOnScreen($p.MainWindowHandle) -and -not [WinChk]::IsIconic($p.MainWindowHandle)) (Rect $p)
    }

    # Second launch while the first is minimized: first comes back, second exits.
    [void][WinChk]::ShowWindow($p.MainWindowHandle, 6)   # SW_MINIMIZE
    Start-Sleep 1
    $second = Start-Process $AppExe -PassThru
    Start-Sleep 6
    $p.Refresh()
    Check "second launch exits" $second.HasExited
    Check "running window restored from minimized" (-not [WinChk]::IsIconic($p.MainWindowHandle)) (Rect $p)
    Check "running window brought to foreground" ([WinChk]::GetForegroundWindow() -eq $p.MainWindowHandle)
    Check "only one cmuxw process" (@(Get-Process cmuxw).Count -eq 1)

    # Close while minimized: saved position must not be the sentinel.
    [void][WinChk]::ShowWindow($p.MainWindowHandle, 6); Start-Sleep 1
    [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000)
    $saved = (Get-Content $sessionFile -Raw | ConvertFrom-Json).window
    Check "close while minimized saves a real position" ($saved.x -gt -20000 -and $saved.y -gt -20000) "x=$($saved.x) y=$($saved.y)"
}
finally { Stop-All }

$results | Format-Table -AutoSize | Out-String -Width 200
if ($results.Pass -contains $false) { exit 1 }
