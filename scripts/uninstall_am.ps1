$ErrorActionPreference = 'Stop'
# AppManager uninstall (lives in DEV\scripts; the DEV dir is the repo root).
# Stops the engine, removes the AppManager logon task, and deletes the program
# directory (%LOCALAPPDATA%\AppManager). The dev dir (sources + scripts) is kept.
$deployDir = Join-Path $env:LOCALAPPDATA 'AppManager'
$cli       = Join-Path $deployDir 'am.exe'
$taskName  = "AppManager"

Write-Output "=== Uninstalling AppManager (program dir: $deployDir) ==="

# 1) Gracefully stop the engine via CLI, fallback kill by name
if (Test-Path $cli) {
    try { & $cli stop | Out-Null } catch { }
}
Start-Sleep -Milliseconds 500
Stop-Process -Name 'am-engine' -Force -ErrorAction SilentlyContinue

# 2) Remove the AppManager scheduled tasks (main + keep-alive)
foreach ($tn in @($taskName, "AppManagerKeepAlive")) {
    $d = Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue
    if ($d) { Write-Output "task unregistered: $tn" } else { Write-Output "task: $tn not found (nothing to remove)" }
}

# 3) Delete the program dir (dev dir stays untouched)
Remove-Item -Path $deployDir -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $deployDir) { Write-Output "WARNING: dir still exists (locked?): $deployDir" } else { Write-Output "program dir removed: $deployDir" }
Write-Output "done. Sources and scripts remain in: " + (Split-Path $PSScriptRoot -Parent)
