$ErrorActionPreference = 'Stop'
# StartManager install (lives in DEV\scripts; build first with the root build.ps1).
# Deploys/points to the PROGRAM directory: %LOCALAPPDATA%\StartManager.
#
# ONE task only:
#   StartManager - logon trigger: run sm-engine.exe once. The engine starts the
#                  configured apps, hides their windows for the monitor window,
#                  then EXITS. There is no keep-alive, no resident process.
#
# NOTE on variable names: `-File` runs load the user profile, and a profile can
# leave session-wide *constrained* variables behind (this machine's profile
# dot-sources a proxy helper declaring [ValidateSet(...)]$Action, which makes
# any later `$action = ...` assignment fail with "cannot be validated").
# So: never name a local $Action here, and keep the names task-specific.
$deployDir = Join-Path $env:LOCALAPPDATA 'StartManager'
$engine    = Join-Path $deployDir 'sm-engine.exe'
$taskName  = "StartManager"

if (-not (Test-Path $engine)) {
  throw "sm-engine.exe not found in $deployDir. Run build.ps1 (repo root) first."
}

# 0) 清理旧安装遗留的任务：旧 AppManager 任务、旧的 5 分钟看护任务、旧 StartManager 看护任务。
#    新版本只有一个 logon 任务，旧任务留着会导致重复拉起 / 常驻。
foreach ($legacyTask in @('AppManager', 'AppManagerKeepAlive', 'StartManagerKeepAlive')) {
  if (Get-ScheduledTask -TaskName $legacyTask -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $legacyTask -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "removed legacy task '$legacyTask'"
  }
}

# 1) 登录任务 -> 部署目录的无窗口引擎（Register 覆盖同名任务）
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$taskAction   = New-ScheduledTaskAction -Execute $engine -WorkingDirectory $deployDir
# 引擎只跑一次 pass 就退出；给一个执行时限，防止某个目标卡住时任务永远占着。
# StartWhenAvailable/电池设置保证笔记本上不会因为电源状态错过登录启动。
$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
$taskDesc = "StartManager: logon silent startup (one pass, then exit). Deploy: $deployDir; CLI: sm.exe"

$registered = $null
for ($i = 0; $i -lt 6; $i++) {
  $registered = Register-ScheduledTask -TaskName $taskName -Trigger $logonTrigger -Action $taskAction -Description $taskDesc -Settings $taskSettings -Force -ErrorAction SilentlyContinue
  if ($registered) { break }
  Start-Sleep 2
}
if (-not $registered) { throw "failed to register task after retries" }

Write-Output "=== StartManager installed ==="
Write-Output ("  task: '" + $registered.TaskName + "'  State=" + $registered.State + "  -> " + $engine)
Write-Output ("  program dir: " + $deployDir)
Write-Output ("  dev dir: " + (Split-Path $PSScriptRoot -Parent))
Write-Output ("  config: " + (Join-Path $deployDir 'config.json'))
Write-Output "  BEHAVIOR: at logon the engine starts every ENABLED item, hides its windows for the"
Write-Output "            monitor window, then exits. No resident process, no window restore, no guard,"
Write-Output "            no hotkey, no keep-alive."
Write-Output "  - add/update:      sm add <exePath> [--name n] [--args s] [--title t] [--time 30s]"
Write-Output "  - edit config:     sm update [name]"
Write-Output "  - show config:     sm list"
Write-Output "  - remove:          sm remove <name>"

# 2) 让 `sm` 在新终端里可直接使用：部署目录加入每用户 PATH（旧的 AppManager 条目移除）。
#    当前已打开的终端不会自动刷新 PATH，需要新开一个终端。
$legacyDeploy = Join-Path $env:LOCALAPPDATA 'AppManager'
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($null -eq $userPath) { $userPath = '' }
$userParts = @($userPath -split ';' | Where-Object { $_ -ne '' -and $_ -ne $legacyDeploy })
if ($userParts -notcontains $deployDir) {
  $userParts += $deployDir
  [Environment]::SetEnvironmentVariable('PATH', ($userParts -join ';'), 'User')
  Write-Output "  PATH: added $deployDir (open a new terminal to use 'sm')"
} else {
  Write-Output "  PATH: $deployDir already present"
}
