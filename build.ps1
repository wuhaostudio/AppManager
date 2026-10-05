$ErrorActionPreference='Continue'
# Build script (lives in the DEV directory: C:\project\AppManager).
# Compiles src\shared + src\cli -> am.exe and src\shared + src\engine ->
# am-engine.exe, output to the DEPLOY directory (%LOCALAPPDATA%\AppManager).
$devDir   = $PSScriptRoot
$deployDir = Join-Path $env:LOCALAPPDATA 'AppManager'
$csc64    = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw       = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$refs     = @("$fw\System.dll","$fw\System.Runtime.Serialization.dll","$fw\System.Core.dll")
$refArg   = ($refs | Where-Object { Test-Path $_ }) -join ','
# the engine links the picker UI (src/ui/am_picker.cs): WinForms + Drawing
$refsGui  = @("$fw\System.Windows.Forms.dll","$fw\System.Drawing.dll")
$refGui   = ($refsGui | Where-Object { Test-Path $_ }) -join ','

if (-not (Test-Path $csc64)) { throw "csc.exe not found at $csc64 (install .NET Framework 4.x developer components)" }
$shared   = Join-Path $devDir 'src\shared\am_shared.cs'
if (-not (Test-Path $shared)) { throw "src\shared\am_shared.cs not found under $devDir" }
New-Item $deployDir -ItemType Directory -Force | Out-Null

function BuildExe($name, $target, $csFiles, $references) {
  $out = Join-Path $deployDir "$name.exe"
  Remove-Item $out -Force -ErrorAction SilentlyContinue
  $files = @($shared) + ($csFiles | ForEach-Object { Join-Path $devDir $_ })
  & $csc64 /nologo "/target:$target" "/out:$out" "/reference:$references" $files 2>&1 | ForEach-Object { Write-Output $_ }
  if (Test-Path $out) {
    $i = Get-Item $out
    Write-Output ("BUILD OK -> $out ($($i.Length) bytes, $($i.LastWriteTime))")
  } else { Write-Output "BUILD FAILED -> $name.exe" }
}

Write-Output "=== build dir: $devDir ==="
Write-Output "=== deploy dir: $deployDir ==="
Write-Output "building am.exe (CLI, console /target:exe) ..."
BuildExe 'am' 'exe' @('src\cli\am_cli.cs') $refArg
Write-Output "building am-engine.exe (engine + picker UI, GUI /target:winexe) ..."
BuildExe 'am-engine' 'winexe' @('src\engine\am_engine.cs', 'src\ui\am_picker.cs') ($refArg + ',' + $refGui)
Write-Output "done. Run scripts\install_am.ps1 to (re)point the logon task."
