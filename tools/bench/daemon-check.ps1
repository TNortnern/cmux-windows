<#
.SYNOPSIS
  End-to-end check of the opt-in session daemon: a shell survives closing and reopening cmux.

.DESCRIPTION
  Enables UseSessionDaemon, sets a shell variable in the pane, closes the app window normally,
  relaunches, then asks the same pane to echo the variable. Passes if the reattached shell still
  has it (same process) and the daemon is cleaned up when the pane is closed.
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [string] $Cli = (Join-Path (Split-Path $AppExe) "cmux.exe")
)
$ErrorActionPreference = "Stop"
function Rpc([string[]] $a) { (& $Cli rpc @a) -join "`n" }
function Wait-App { for ($i = 0; $i -lt 40; $i++) { try { if ((Rpc "STATUS") -match "version") { return } } catch {}; Start-Sleep -Milliseconds 500 }; throw "app did not come up" }

Get-Process cmuxw, cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "cmux"
if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
New-Item -ItemType Directory -Force $dataDir | Out-Null
'{ "useSessionDaemon": true }' | Set-Content -Encoding utf8 (Join-Path $dataDir "settings.json")

$marker = "cmux-persist-$(Get-Random)"
$results = @()
function Check([string] $name, [bool] $ok, [string] $detail) { $script:results += [pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail } }

try {
    $p = Start-Process $AppExe -PassThru
    Wait-App; Start-Sleep 3
    $pane = (Rpc "PANE.LIST" | ConvertFrom-Json).panes[0].id
    $daemon = Get-Process cmux-daemon -ErrorAction SilentlyContinue
    Check "daemon started" ($null -ne $daemon) "pid=$($daemon.Id)"
    Rpc "PANE.WRITE", "paneId=$pane", "text=`$env:CMUX_MARK='$marker'", "submit=true" | Out-Null
    Start-Sleep 2

    $p.CloseMainWindow() | Out-Null
    if (-not $p.WaitForExit(15000)) { throw "app did not close" }
    Check "daemon survives app close" ($null -ne (Get-Process -Id $daemon.Id -ErrorAction SilentlyContinue)) ""

    $p = Start-Process $AppExe -PassThru
    Wait-App; Start-Sleep 4
    $pane2 = (Rpc "PANE.LIST" | ConvertFrom-Json).panes[0].id
    Check "same pane restored" ($pane2 -eq $pane) "$pane2"
    Rpc "PANE.WRITE", "paneId=$pane2", "text=echo VALUE=`$env:CMUX_MARK", "submit=true" | Out-Null
    Start-Sleep 2
    $tail = Rpc "PANE.READ", "paneId=$pane2", "lines=40"
    Check "shell state survived (same process)" ($tail -match "VALUE=$marker") ""

    Rpc "PANE.WRITE", "paneId=$pane2", "text=exit", "submit=true" | Out-Null
    Start-Sleep 4
    $left = Rpc "PANE.LIST" | ConvertFrom-Json
    Check "exit replaced the last pane with a fresh one" (@($left.panes).Count -eq 1 -and $left.panes[0].id -ne $pane2) ""
    Check "app still alive" (-not $p.HasExited) ""
}
finally {
    Get-Process cmuxw, cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
}
$results | Format-Table -AutoSize | Out-String -Width 200
if ($results.Pass -contains $false) { exit 1 }
