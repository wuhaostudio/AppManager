@echo off
rem Compile the `sm update` editor harness (model + keys + line editor).
rem No UI frameworks / GAC UIA: the minimal product only links sm_shared + the CLI.
set CSP=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set DEV=C:\project\AppManager
if not exist "%DEV%\tests\bin" mkdir "%DEV%\tests\bin"
"%CSP%" /nologo /codepage:65001 /target:exe /main:UpdateTest /out:"%DEV%\tests\bin\update_test.exe" ^
 /r:"%FW%\System.dll" ^
 /r:"%FW%\System.Core.dll" ^
 /r:"%FW%\System.Runtime.Serialization.dll" ^
 "%DEV%\src\shared\sm_shared.cs" ^
 "%DEV%\src\cli\sm_cli.cs" "%DEV%\src\cli\sm_update.cs" "%DEV%\src\cli\sm_style.cs" ^
 "%DEV%\tests\update_test.cs"
if exist "%DEV%\tests\bin\update_test.exe" (echo BUILD-OK) else (echo BUILD-FAILED)
