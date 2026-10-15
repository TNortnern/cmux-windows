<#
.SYNOPSIS
  End-to-end check that a split and a new tab open in the focused pane's current directory.

.DESCRIPTION
  Starts cmuxw.exe with a clean profile, `cd`s the focused pane into a test folder via
  `cmux rpc PANE.WRITE`, then splits and opens a new tab and reads each new pane's
  working directory back with PANE.LIST. Exits 1 on any mismatch.

.EXAMPLE
  pwsh tools/bench/cwd-check.ps1 -AppExe ..\cmux-new\cmuxw.exe
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [string] $Cli = (Join-Path (Split-Path $AppExe) "cmux.exe"),
    [switch] $Daemon
)
$ErrorActionPreference = "Stop"

function Rpc([string[]] $a) { (& $Cli rpc @a) -join "`n" }
function Panes([int] $surface) { (Rpc "PANE.LIST", "surfaceIndex=$surface" | ConvertFrom-Json).panes }

$target = Join-Path ([IO.Path]::GetTempPath()) "cmux cwd check $(Get-Random)"   # space on purpose
New-Item -ItemType Directory $target | Out-Null

Get-Process cmuxw, cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "cmux"
if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
if ($Daemon) {
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    '{ "useSessionDaemon": true }' | Set-Content -Encoding utf8 (Join-Path $dataDir "settings.json")
}

$p = Start-Process $AppExe -PassThru
$results = @()
try {
    Start-Sleep 8
    $first = (Panes 1)[0]
    Rpc "PANE.WRITE", "paneId=$($first.id)", "text=cd `"$target`"", "submit=true" | Out-Null
    Start-Sleep 3   # let the prompt redraw and report its cwd (OSC 9;9)

    $results += [pscustomobject]@{ Step = "focused pane after cd"; Cwd = (Panes 1)[0].workingDirectory }

    Rpc "SPLIT.RIGHT" | Out-Null; Start-Sleep 3
    $split = Panes 1 | Where-Object { $_.id -ne $first.id } | Select-Object -First 1
    $results += [pscustomobject]@{ Step = "split right"; Cwd = $split.workingDirectory }

    Rpc "PANE.FOCUS", "paneId=$($first.id)" | Out-Null; Start-Sleep 1
    Rpc "SURFACE.CREATE" | Out-Null; Start-Sleep 3
    $results += [pscustomobject]@{ Step = "new tab"; Cwd = (Panes 2)[0].workingDirectory }
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Get-Process cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
}

$norm = { param($x) if ($x) { $x.TrimEnd('\').ToLowerInvariant() } }
$results | ForEach-Object { $_ | Add-Member Pass ((& $norm $_.Cwd) -eq (& $norm $target)) }
"Target: $target  (mode: $(if ($Daemon) { 'daemon' } else { 'in-process' }))"
$results | Format-Table -AutoSize | Out-String -Width 200
Remove-Item -Recurse -Force $target
if ($results.Pass -contains $false) { exit 1 }
