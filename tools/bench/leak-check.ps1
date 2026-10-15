<#
.SYNOPSIS
  Leak check for tab switching: counts live TerminalControl / TerminalSession objects
  (after a forced GC, via dotnet-gcdump) before and after many tab switches.

.EXAMPLE
  pwsh tools/bench/leak-check.ps1 -AppExe ..\cmux-new\cmuxw.exe
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [string] $Cli = (Join-Path (Split-Path $AppExe) "cmux.exe"),
    [int] $Switches = 200
)
$ErrorActionPreference = "Stop"
$work = Join-Path ([IO.Path]::GetTempPath()) "cmux-leak-$(Get-Random)"
New-Item -ItemType Directory $work | Out-Null

function Rpc([string[]] $a) { & $Cli rpc @a | Out-Null }

function Count-Objects([int] $procId, [string] $tag) {
    $dump = Join-Path $work "$tag.gcdump"
    dotnet-gcdump collect -p $procId -o $dump | Out-Null
    $report = dotnet-gcdump report $dump | Out-String
    $pick = { param($name) ($report -split "`n" | Where-Object { $_ -match "^\s*[\d,]+\s+[\d,]+\s+$([regex]::Escape($name))\s" } |
        ForEach-Object { [int](($_.Trim() -split '\s+')[1] -replace ',', '') } | Select-Object -First 1) }
    $heap = ($report -split "`n" | Where-Object { $_ -match "GC Heap bytes" } | Select-Object -First 1).Trim().Split(' ')[0] -replace ',', ''
    [pscustomobject]@{
        When              = $tag
        TerminalControls  = & $pick "Cmux.Controls.TerminalControl"
        TerminalSessions  = & $pick "Cmux.Core.Terminal.TerminalSession"
        GcHeapMB          = [math]::Round([double]$heap / 1MB, 1)
        PrivateMB         = [math]::Round((Get-Process -Id $procId).PrivateMemorySize64 / 1MB, 1)
    }
}

Get-Process cmuxw -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
# Fresh start: no restored layout from a previous run.
$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "cmux"
if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
$p = Start-Process $AppExe -PassThru
Start-Sleep 8
for ($t = 0; $t -lt 3; $t++) { Rpc "SURFACE.CREATE"; Start-Sleep -Milliseconds 600; Rpc "SPLIT.RIGHT"; Start-Sleep -Milliseconds 600 }
Start-Sleep 3
$rows = @(Count-Objects $p.Id "before")
for ($i = 0; $i -lt $Switches; $i++) { Rpc "SURFACE.SELECT", "surfaceIndex=$(($i % 4) + 1)"; Start-Sleep -Milliseconds 100 }
Start-Sleep 3
$rows += Count-Objects $p.Id "after-$Switches-switches"
for ($i = 0; $i -lt $Switches; $i++) { Rpc "SURFACE.SELECT", "surfaceIndex=$(($i % 4) + 1)"; Start-Sleep -Milliseconds 100 }
Start-Sleep 3
$rows += Count-Objects $p.Id "after-$(2 * $Switches)-switches"
for ($i = 0; $i -lt $Switches; $i++) { Rpc "SURFACE.SELECT", "surfaceIndex=$(($i % 4) + 1)"; Start-Sleep -Milliseconds 100 }
Start-Sleep 3
$rows += Count-Objects $p.Id "after-$(3 * $Switches)-switches"
Stop-Process -Id $p.Id -Force
$rows | Format-Table -AutoSize | Out-String -Width 200
Remove-Item -Recurse -Force $work
