$ErrorActionPreference = 'Stop'
# StartManager uninstall (lives in DEV\scripts; the DEV dir is the repo root).
# Stops a running logon pass, removes the StartManager task (plus legacy tasks),
# deletes the program directory (%LOCALAPPDATA%\StartManager) and drops it from
# the per-user PATH. The dev dir (sources + scripts) is kept.
$deployDir = Join-Path $env:LOCALAPPDATA 'StartManager'
$taskName  = "StartManager"

Write-Output "=== Uninstalling StartManager (program dir: $deployDir) ==="

# 1) stop a pass that may still be running (the engine exits by itself when done)
Stop-Process -Name 'sm-engine' -Force -ErrorAction SilentlyContinue

# 2) remove the StartManager task + any legacy task from older versions
foreach ($tn in @($taskName, 'StartManagerKeepAlive', 'AppManager', 'AppManagerKeepAlive')) {
    $d = Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue
    if ($d) { Write-Output "task unregistered: $tn" }
}

# 3) delete the program dir (dev dir stays untouched)
Remove-Item -Path $deployDir -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $deployDir) { Write-Output "WARNING: dir still exists (locked?): $deployDir" } else { Write-Output "program dir removed: $deployDir" }

# 4) drop the deploy dir from the per-user PATH (install_sm.ps1 added it)
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($userPath) {
    $pathParts = @($userPath -split ';' | Where-Object { $_ -ne '' -and $_ -ne $deployDir })
    [Environment]::SetEnvironmentVariable('PATH', ($pathParts -join ';'), 'User')
    Write-Output "removed from user PATH: $deployDir"
}
Write-Output "done. Sources and scripts remain in: " + (Split-Path $PSScriptRoot -Parent)
