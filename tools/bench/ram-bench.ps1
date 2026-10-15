<#
.SYNOPSIS
  Reproducible RAM benchmark for cmux-windows.

.DESCRIPTION
  Launches a built cmuxw.exe, drives it with `cmux rpc` over the \\.\pipe\cmux control
  pipe (JSON argument form, supported by every version), and samples memory of the
  cmux processes (app + daemon, NOT the shells, which are reported separately).

  Scenarios:
    1. idle        - 1 tab / 1 pane, 15 s after launch
    2. loaded      - 4 tabs x 3 panes, each pane printed 20k lines of 150 chars
    3. switching   - after 60 tab switches (exposes leaked/orphaned controls)

.EXAMPLE
  pwsh tools/bench/ram-bench.ps1 -AppExe .\src\Cmux\bin\Release\net10.0-windows10.0.17763.0\cmuxw.exe -Label after
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [string] $Label = "run",
    # CLI used to drive the app; must support `cmux rpc` (any build's app understands it).
    [string] $Cli = (Join-Path (Split-Path $AppExe) "cmux.exe"),
    [string] $OutDir = (Join-Path $PSScriptRoot "results"),
    [switch] $KeepData
)

$ErrorActionPreference = "Stop"
$AppExe = (Resolve-Path $AppExe).Path
$Cli = (Resolve-Path $Cli).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$dataDir = Join-Path $env:LOCALAPPDATA "cmux"

function Send-Cmux([string] $Command, [hashtable] $CmdArgs = @{}) {
    # Uses the cmux CLI's `rpc` subcommand (JSON on the wire, understood by old and new builds).
    $cliArgs = @("rpc", $Command) + @($CmdArgs.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    $job = Start-Job { param($cli, $a) & $cli @a 2>&1 } -ArgumentList $Cli, $cliArgs
    if (-not (Wait-Job $job -Timeout 15)) { Stop-Job $job; Remove-Job $job -Force; throw "cmux rpc $Command timed out" }
    $out = (Receive-Job $job) -join "`n"; Remove-Job $job -Force
    if ($out -match "Could not connect") { throw $out }
    return $out
}

function Wait-Pipe {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        try { if (Send-Cmux "STATUS") { return } } catch { Start-Sleep -Milliseconds 250 }
    }
    throw "cmux pipe never came up"
}

function Get-Sample([string] $Scenario) {
    # Settle: give the GC and render loop a moment so we sample steady state.
    Start-Sleep -Seconds 5
    $procs = Get-Process -Name "cmuxw", "cmux-daemon" -ErrorAction SilentlyContinue
    $app = $procs | Where-Object Name -eq "cmuxw"
    $daemon = $procs | Where-Object Name -eq "cmux-daemon"
    $appIds = @($procs.Id)
    $shells = Get-CimInstance Win32_Process | Where-Object { $appIds -contains $_.ParentProcessId -and $_.Name -ne "conhost.exe" }
    [pscustomobject]@{
        Label             = $Label
        Scenario          = $Scenario
        AppPrivateMB      = [math]::Round(($app | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB, 1)
        AppWorkingSetMB   = [math]::Round(($app | Measure-Object WorkingSet64 -Sum).Sum / 1MB, 1)
        DaemonPrivateMB   = [math]::Round(($daemon | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB, 1)
        TotalCmuxPrivMB   = [math]::Round(($procs | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB, 1)
        AppHandles        = ($app | Measure-Object HandleCount -Sum).Sum
        AppThreads        = ($app | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum
        ShellProcesses    = @($shells).Count
    }
}

# --- clean start -------------------------------------------------------------
Get-Process -Name "cmuxw", "cmux-daemon" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
if (-not $KeepData -and (Test-Path $dataDir)) { Remove-Item -Recurse -Force $dataDir }

$results = @()
$proc = Start-Process -FilePath $AppExe -PassThru
try {
    Wait-Pipe
    Start-Sleep -Seconds 10
    $results += Get-Sample "1-idle (1 tab, 1 pane)"

    # 4 tabs x 3 panes. Pane output: 20k lines x 150 chars (fills scrollback).
    $flood = '1..20000|%{[string]::new([char]65,150)}'
    for ($t = 0; $t -lt 4; $t++) {
        if ($t -gt 0) { Send-Cmux "SURFACE.CREATE" | Out-Null; Start-Sleep -Milliseconds 800 }
        Send-Cmux "SPLIT.RIGHT" | Out-Null; Start-Sleep -Milliseconds 800
        Send-Cmux "SPLIT.DOWN" | Out-Null;  Start-Sleep -Milliseconds 800
    }
    Start-Sleep -Seconds 4
    for ($s = 0; $s -lt 4; $s++) {
        Send-Cmux "SURFACE.SELECT" @{ surfaceIndex = "$($s + 1)" } | Out-Null
        Start-Sleep -Milliseconds 300
        foreach ($p in (Send-Cmux "PANE.LIST" @{ surfaceIndex = "$($s + 1)" } | ConvertFrom-Json).panes) {
            Send-Cmux "PANE.WRITE" @{ paneId = "$($p.id)"; text = $flood; submit = "true" } | Out-Null
        }
    }
    Start-Sleep -Seconds 25   # let the floods finish
    $results += Get-Sample "2-loaded (4 tabs x 3 panes, 20k-line floods)"

    for ($i = 0; $i -lt 60; $i++) {
        Send-Cmux "SURFACE.SELECT" @{ surfaceIndex = "$(($i % 4) + 1)" } | Out-Null
        Start-Sleep -Milliseconds 150
    }
    $results += Get-Sample "3-after 60 tab switches"
}
finally {
    Get-Process -Name "cmuxw", "cmux-daemon" -ErrorAction SilentlyContinue | Stop-Process -Force
}

$csv = Join-Path $OutDir "$Label.csv"
$results | Export-Csv -NoTypeInformation $csv
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
Write-Output "Saved: $csv"
