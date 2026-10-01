$ErrorActionPreference = 'Stop'
# AppManager install (lives in DEV\scripts; build first with the root build.ps1).
# Deploys/points to the PROGRAM directory: %LOCALAPPDATA%\AppManager.
# Registers the logon task pointing at am-engine.exe in the deploy directory.
$deployDir = Join-Path $env:LOCALAPPDATA 'AppManager'
$engine    = Join-Path $deployDir 'am-engine.exe'
$taskName  = "AppManager"

if (-not (Test-Path $engine)) {
  throw "am-engine.exe not found in $deployDir. Run build.ps1 (repo root) first."
}

# 1) 清理指向旧 exe 的同名任务，稍后重建指向新引擎
$old = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$oldExec = if ($old) { ($old.Actions | Select-Object -First 1 -ExpandProperty Execute) } else { $null }
if ($old -and $oldExec -and $oldExec -ne $engine) {
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
  Write-Output "removed outdated task 'AppManager' (was -> $oldExec)"
}

# 2) 注册登录任务 -> 部署目录的无窗口引擎（Set 优先原地更新，Register 兜底）
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$action   = New-ScheduledTaskAction -Execute $engine -WorkingDirectory $deployDir
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
$desc = "AppManager: engine silent start + resident standby (no tray, no window). Deploy: $deployDir; CLI: am.exe; stop: am stop"

$reg = $null
for ($i = 0; $i -lt 6; $i++) {
  $reg = Register-ScheduledTask -TaskName $taskName -Trigger $trigger -Action $action -Description $desc -Settings $settings -Force -ErrorAction SilentlyContinue
  if ($reg) { break }
  Start-Sleep 2
}
if (-not $reg) { throw "failed to register task after retries" }

Write-Output "=== AppManager installed ==="
Write-Output ("  task: '" + $reg.TaskName + "'  State=" + $reg.State + "  -> " + $engine)
Write-Output ("  program dir: " + $deployDir)
Write-Output ("  dev dir: " + (Split-Path $PSScriptRoot -Parent))
Write-Output ("  config: " + (Join-Path $deployDir 'config.json'))
Write-Output "  BEHAVIOR: at logon the engine silently starts every enabled item, re-hides popups during the silent window, then stays resident (no tray, no window)."
Write-Output "  - add a program:   am add <exePath> [--name n] [--title t] [--time 30s]   (takes effect next logon)"
Write-Output "  - see programs:    am scan / list"
Write-Output "  - show a window:   am show <name>"
Write-Output "  - start/stop:      am run / am stop"
