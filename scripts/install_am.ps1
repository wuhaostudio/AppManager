$ErrorActionPreference = 'Stop'
# AppManager install (lives in DEV\scripts; build first with the root build.ps1).
# Deploys/points to the PROGRAM directory: %LOCALAPPDATA%\AppManager.
# Registers two tasks, both pointing at am-engine.exe in the deploy directory:
#   AppManager          - logon trigger: silent start + resident standby
#   AppManagerKeepAlive - every 5 min: revive the engine if it died (hotkey)
#
# NOTE on variable names: `-File` runs load the user profile, and a profile can
# leave session-wide *constrained* variables behind (this machine's profile
# dot-sources a proxy helper declaring [ValidateSet(...)]$Action, which makes
# any later `$action = ...` assignment fail with "cannot be validated").
# So: never name a local $Action here, and keep the names task-specific.
$deployDir = Join-Path $env:LOCALAPPDATA 'AppManager'
$engine    = Join-Path $deployDir 'am-engine.exe'
$taskName  = "AppManager"

if (-not (Test-Path $engine)) {
  throw "am-engine.exe not found in $deployDir. Run build.ps1 (repo root) first."
}

# 1) 清理指向旧 exe 的同名任务，稍后重建指向新引擎
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$existingExec = if ($existingTask) { ($existingTask.Actions | Select-Object -First 1 -ExpandProperty Execute) } else { $null }
if ($existingTask -and $existingExec -and $existingExec -ne $engine) {
  Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
  Write-Output "removed outdated task 'AppManager' (was -> $existingExec)"
}

# 2) 注册登录任务 -> 部署目录的无窗口引擎（Register 覆盖同名任务）
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$taskAction   = New-ScheduledTaskAction -Execute $engine -WorkingDirectory $deployDir
# ExecutionTimeLimit 必须为 0（无限）：默认值是 PT72H，任务计划程序会在引擎
# 常驻 72 小时后强制结束它 —— 引擎一死热键就静默失效，直到下次登录。
# StartWhenAvailable/电池设置保证笔记本上不会因为电源状态把常驻引擎停掉。
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
$taskDesc = "AppManager: engine silent start + resident standby (no tray, no window). Deploy: $deployDir; CLI: am.exe; stop: am stop"

$registered = $null
for ($i = 0; $i -lt 6; $i++) {
  $registered = Register-ScheduledTask -TaskName $taskName -Trigger $logonTrigger -Action $taskAction -Description $taskDesc -Settings $taskSettings -Force -ErrorAction SilentlyContinue
  if ($registered) { break }
  Start-Sleep 2
}
if (-not $registered) { throw "failed to register task after retries" }

# 3) 看护任务：每 5 分钟跑一次 `am-engine.exe --keepalive`
#    引擎已常驻 -> 被单实例互斥体挡回并静默退出；引擎不在 -> 立即补位（跳过启动 pass，
#    不会重新拉起用户手动关掉的应用、也不会藏掉正在用的窗口）。
#    `am stop` 会写停用标记，标记存在期间看护不拉起；`am run` / 登录启动会清掉标记。
$keepAliveName = "AppManagerKeepAlive"
$keepAliveAction = New-ScheduledTaskAction -Execute $engine -Argument "--keepalive" -WorkingDirectory $deployDir
$keepAliveTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
# 看护实例可能变成常驻引擎，所以同样不能有执行时限（默认 PT72H 会把常驻引擎强杀）。
# MultipleInstances 用 Parallel 而不是 IgnoreNew：看护补位成功后那个任务实例会一直"运行"
# （它就是引擎进程），IgnoreNew 会把后续每次触发都拒掉并在任务历史里记 0x800710E0
# "拒绝请求"（功能不受影响，但历史里全是假失败）。Parallel 下每次触发都起一个进程，
# 引擎已常驻时被单实例互斥体挡回、瞬间以 0 退出，历史干净。
$keepAliveSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances Parallel -ExecutionTimeLimit ([TimeSpan]::Zero)
$keepAliveDesc = "AppManager keep-alive: runs 'am-engine.exe --keepalive' every 5 minutes; exits at once when the engine is already resident, so the hotkey survives an unexpected engine death. Hold-off: 'am stop'."

$keepAliveRegistered = $null
for ($i = 0; $i -lt 6; $i++) {
  $keepAliveRegistered = Register-ScheduledTask -TaskName $keepAliveName -Trigger $keepAliveTrigger -Action $keepAliveAction -Description $keepAliveDesc -Settings $keepAliveSettings -Force -ErrorAction SilentlyContinue
  if ($keepAliveRegistered) { break }
  Start-Sleep 2
}
if (-not $keepAliveRegistered) { Write-Output "WARNING: keep-alive task '$keepAliveName' could not be registered (hotkey recovery then needs 'am run')" }

Write-Output "=== AppManager installed ==="
Write-Output ("  task: '" + $registered.TaskName + "'  State=" + $registered.State + "  -> " + $engine)
if ($keepAliveRegistered) { Write-Output ("  keep-alive task: '" + $keepAliveRegistered.TaskName + "'  every 5 min -> " + $engine + " --keepalive") }
Write-Output ("  program dir: " + $deployDir)
Write-Output ("  dev dir: " + (Split-Path $PSScriptRoot -Parent))
Write-Output ("  config: " + (Join-Path $deployDir 'config.json'))
Write-Output "  BEHAVIOR: at logon the engine silently starts every enabled item, re-hides popups during the silent window, then stays resident (no tray, no window)."
Write-Output "  - add a program:   am add <exePath> [--name n] [--title t] [--time 30s] [--no-autostart]   (takes effect next logon)"
Write-Output "  - see programs:    am scan / list"
Write-Output "  - edit config:     am update [name]      (pick an item, then edit its fields: name / path / logon auto-start / ...)"
Write-Output "  - start/restore:   am start <name>"
Write-Output "  - start/stop:      am run / am stop   (am stop also holds the keep-alive watchdog off until the next am run)"
Write-Output "  - hotkey:          am hotkey (set) / am hotkey clear — pops the app picker; dead hotkey => check 'am list' engine line"
