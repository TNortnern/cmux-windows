<#
.SYNOPSIS
  End-to-end session restore check: workspaces, tabs, splits and every pane's folder come back
  after closing cmux; shells do not survive the close (in-process mode).

.DESCRIPTION
  Builds 2 workspaces / 3 tabs / 4 panes, cds each pane into its own folder, then:
    1. closes cmux normally and reopens it            -> layout + folders restored
    2. closes again WITHOUT visiting every tab, reopens -> still restored (lazy tabs keep folders)
    3. cds a pane, waits for autosave, kills cmux hard -> the new folder survives a crash
    4. same for a background (unfocused) pane
    5. with "Reopen workspaces..." turned off, cmux starts fresh
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [string] $Cli = (Join-Path (Split-Path $AppExe) "cmux.exe"),
    [int] $AutoSaveSeconds = 5
)
$ErrorActionPreference = "Stop"
function Rpc([string[]] $a) { (& $Cli rpc @a) -join "`n" }
function Wait-App { for ($i = 0; $i -lt 40; $i++) { try { if ((Rpc "STATUS") -match "version") { return } } catch {}; Start-Sleep -Milliseconds 500 }; throw "app did not come up" }
function Panes([int] $ws, [int] $tab) { (Rpc "PANE.LIST", "workspaceIndex=$ws", "surfaceIndex=$tab" | ConvertFrom-Json).panes }
function Set-PaneDir([string] $paneId, [string] $dir) { Rpc "PANE.WRITE", "paneId=$paneId", "text=cd `"$dir`"", "submit=true" | Out-Null }
function Select-Tab([int] $ws, [int] $tab) { Rpc "SURFACE.SELECT", "workspaceIndex=$ws", "surfaceIndex=$tab" | Out-Null; Start-Sleep 3 }
function Norm($p) { if ($p) { $p.TrimEnd('\').ToLowerInvariant() } }

$root = Join-Path ([IO.Path]::GetTempPath()) "cmux restore $(Get-Random)"
$dirs = @{}; foreach ($k in "A", "B", "C", "D", "E") { $dirs[$k] = (New-Item -ItemType Directory (Join-Path $root "dir $k")).FullName }

$results = @()
function Check([string] $name, [bool] $ok, [string] $detail = "") { $script:results += [pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail } }

function Shells-Of([int] $appPid) {
    @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $appPid -and $_.Name -match "pwsh|powershell|cmd" })
}

# Expected layout: ws1/tab1 = [A, B] (split), ws1/tab2 = [C], ws2/tab1 = [D]
function Verify([string] $phase) {
    $ws = Rpc "WORKSPACE.LIST" | ConvertFrom-Json
    Check "${phase}: 2 workspaces" (@($ws).Count -eq 2) "$(@($ws).Count)"
    Check "${phase}: ws1 has 2 tabs" ($ws[0].surfaces -eq 2) "$($ws[0].surfaces)"
    Select-Tab 1 1
    $t1 = Panes 1 1
    Check "${phase}: ws1/tab1 has 2 panes" (@($t1).Count -eq 2) "$(@($t1).Count)"
    Check "${phase}: ws1/tab1 folders" (((Norm $t1[0].workingDirectory) -eq (Norm $dirs.A)) -and ((Norm $t1[1].workingDirectory) -eq (Norm $dirs.B))) "$($t1.workingDirectory -join ' | ')"
    Select-Tab 1 2
    $t2 = Panes 1 2
    Check "${phase}: ws1/tab2 folder" ((Norm $t2[0].workingDirectory) -eq (Norm $dirs.C)) "$($t2[0].workingDirectory)"
    Rpc "WORKSPACE.SELECT", "index=2" | Out-Null; Start-Sleep 3
    $w2 = Panes 2 1
    Check "${phase}: ws2 folder" ((Norm $w2[0].workingDirectory) -eq (Norm $dirs.D)) "$($w2[0].workingDirectory)"
    Rpc "WORKSPACE.SELECT", "index=1" | Out-Null; Start-Sleep 1
}

Get-Process cmuxw, cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$dataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "cmux"
if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
New-Item -ItemType Directory -Force $dataDir | Out-Null
"{ `"autoSaveIntervalSeconds`": $AutoSaveSeconds }" | Set-Content -Encoding utf8 (Join-Path $dataDir "settings.json")

try {
    # --- build the layout ---------------------------------------------------------------
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 3
    Rpc "WORKSPACE.SELECT", "index=1" | Out-Null
    Rpc "PANE.WRITE", "paneId=$((Panes 1 1)[0].id)", "text=echo setup", "submit=true" | Out-Null
    & $Cli rpc WORKSPACE.CREATE "name=second" | Out-Null; Start-Sleep 3
    Rpc "WORKSPACE.SELECT", "index=1" | Out-Null; Start-Sleep 1
    Set-PaneDir (Panes 1 1)[0].id $dirs.A; Start-Sleep 2
    Rpc "SPLIT.RIGHT" | Out-Null; Start-Sleep 3
    Set-PaneDir ((Panes 1 1) | Where-Object { $_.focused })[0].id $dirs.B; Start-Sleep 2
    Rpc "SURFACE.CREATE" | Out-Null; Start-Sleep 3
    Set-PaneDir (Panes 1 2)[0].id $dirs.C; Start-Sleep 2
    Rpc "WORKSPACE.SELECT", "index=2" | Out-Null; Start-Sleep 3
    Set-PaneDir (Panes 2 1)[0].id $dirs.D; Start-Sleep 3
    Rpc "WORKSPACE.SELECT", "index=1" | Out-Null; Start-Sleep 1
    $script:wsNames = ((Rpc "WORKSPACE.LIST" | ConvertFrom-Json).name -join ",")

    # --- 1. normal close + reopen -----------------------------------------------------
    $appPid = $p.Id
    [void]$p.CloseMainWindow(); [void]$p.WaitForExit(20000)
    Start-Sleep 2
    Check "close: app exited" $p.HasExited
    Check "close: no shells left running" ((Shells-Of $appPid).Count -eq 0)
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 4
    $names = (Rpc "WORKSPACE.LIST" | ConvertFrom-Json).name -join ","
    Check "reopen 1: workspace names kept" ($names -eq $script:wsNames) "$names"
    Verify "reopen 1"

    # --- 2. close without visiting ws2 / tab2, reopen ---------------------------------
    [void]$p.CloseMainWindow(); [void]$p.WaitForExit(20000); Start-Sleep 1
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 4
    # only ws1/tab1 is visited before the next close
    [void]$p.CloseMainWindow(); [void]$p.WaitForExit(20000); Start-Sleep 1
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 4
    Verify "reopen 2 (unvisited tabs)"

    # --- 3. crash after a cd ------------------------------------------------------------
    Select-Tab 1 1
    Set-PaneDir (Panes 1 1)[0].id $dirs.E
    Start-Sleep ($AutoSaveSeconds + 4)
    Stop-Process -Id $p.Id -Force; Start-Sleep 1
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 4
    Select-Tab 1 1
    $after = (Panes 1 1)[0].workingDirectory
    Check "crash: folder changed before the crash survives" ((Norm $after) -eq (Norm $dirs.E)) "$after"

    # --- 4. crash after a cd in a BACKGROUND (unfocused) pane ----------------------------
    $panes = Panes 1 1
    Rpc "PANE.FOCUS", "paneId=$($panes[0].id)" | Out-Null; Start-Sleep 1
    Set-PaneDir $panes[1].id $dirs.A      # pane 2 was in B; move it while pane 1 has focus
    Start-Sleep ($AutoSaveSeconds + 4)
    Stop-Process -Id $p.Id -Force; Start-Sleep 1
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 4
    Select-Tab 1 1
    $bg = (Panes 1 1)[1].workingDirectory
    Check "crash: background pane's new folder survives" ((Norm $bg) -eq (Norm $dirs.A)) "$bg"

    # --- 5. opt-out: restore disabled -> fresh single workspace --------------------------
    [void]$p.CloseMainWindow(); [void]$p.WaitForExit(20000); Start-Sleep 1
    $settingsFile = Join-Path $dataDir "settings.json"
    $cfg = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $cfg | Add-Member -Force restoreSessionOnStartup $false
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $settingsFile
    $p = Start-Process $AppExe -PassThru; Wait-App; Start-Sleep 3
    $wsOff = @(Rpc "WORKSPACE.LIST" | ConvertFrom-Json)
    Check "opt-out: starts with one fresh workspace" ($wsOff.Count -eq 1 -and $wsOff[0].surfaces -eq 1) "$($wsOff.Count) ws, $($wsOff[0].surfaces) tab(s)"
}
finally {
    Get-Process cmuxw, cmux-daemon -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
    for ($i = 0; $i -lt 20 -and (Test-Path $root); $i++) { try { Remove-Item -Recurse -Force $root -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 } }
}

$results | Format-Table -AutoSize | Out-String -Width 220
if ($results.Pass -contains $false) { exit 1 }
