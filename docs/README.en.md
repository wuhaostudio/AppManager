# StartManager

<p align="center">
  <img src="../assets/logo.svg" alt="StartManager logo" width="120"><br>
  <strong>Windows apps that start at logon <em>completely silently</em> — no visible window, no console popup.<br>
  It does one thing: at logon, start the configured apps and hide their windows; the engine then exits.</strong>
</p>

![GitHub stars](https://img.shields.io/github/stars/wuhaostudio/AppManager?style=flat-square)
![GitHub forks](https://img.shields.io/github/forks/wuhaostudio/AppManager?style=flat-square)
![GitHub issues](https://img.shields.io/github/issues/wuhaostudio/AppManager?style=flat-square)
![Language C#](https://img.shields.io/badge/language-C%23-68267B?style=flat-square)
![Platform Windows](https://img.shields.io/badge/platform-Windows-0078D6?style=flat-square)
![Runtime .NET Framework 4.x](https://img.shields.io/badge/runtime-.NET%20Framework%204.x-512BD4?style=flat-square)
![License MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)

> 📄 **Languages | 语言**: [中文](../README.md) · [English](README.en.md)

---

## How it works

Two small .NET executables compiled with the stock `csc.exe` — no Visual Studio, no project files, no extra dependencies:

| Executable | Target | Role |
|---|---|---|
| `sm.exe` | `/target:exe` (console) | Config CLI (command `sm`): add / update / remove / list startup items, teach script launchers |
| `sm-engine.exe` | `/target:winexe` (no window) | Runs one silent logon pass, then exits |

One **scheduled task** (registered by `scripts\install_sm.ps1`): `StartManager` (runs `sm-engine.exe` windowlessly when the user logs on).

The CLI and the engine have **no IPC**; they only share `config.json` in the deploy directory. The engine does not stay resident, does not listen for hotkeys, has no tray icon and exposes no UI:

```
  Logon ──▶ StartManager task ──▶ sm-engine.exe (no window)
                                      │
                                      │ start items that aren't running + poll-hide
                                      ▼
                                  configured apps / scripts
                                      │
                                   exits when done
```

## Features

- **Silent logon start** — for every enabled item: start it if it is not running, then hide its windows
- **Monitor window** — one monitor duration per app item (default 30s); windows are re-hidden every 250ms, so late popups are caught too
- **Real windows only** — only the app's own main window (unowned, non-TOOLWINDOW, non-zero rect) is touched; renderer hosts, tray hosts and IME helpers are left alone
- **Native window hiding, tray icons preserved** — hiding goes through the app's own "minimize to tray" path (`SC_MINIMIZE`); windows that stay on the taskbar are fully hidden via `SW_HIDE`
- **Enable / disable** — an item can be disabled: it is not started and not hidden, but stays in the config
- **Script launchers (built-in + learned)** — built-in hosts for `.ahk/.ps1/.bat/.vbs/.py`; `sm launchers learn` teaches a new extension (e.g. `.lua → luajit`), and `sm add xxx.lua` then auto-matches the host; scripts are launch-only (no window hiding)
- **Launch arguments** — `.exe` items may carry a command line; for scripts the host/args template comes from the launcher and can be switched in `sm update`
- **Configuration only** — no resident engine, no hotkey, no popup, no window restore, no process guard, no keep-alive, no automatic relaunch
- **Zero dependencies** — no third-party libraries, no UI frameworks; the background process runs only briefly at logon

## Quick start

Prerequisite: Windows + .NET Framework 4.x (stock `csc.exe`)

### 1. Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs `sm.exe` and `sm-engine.exe` to `%LOCALAPPDATA%\StartManager\`.

### 2. Install

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install_sm.ps1
```

Registers the logon task `StartManager` and adds the deploy directory to the per-user PATH (a new terminal can then run `sm`). Legacy `AppManager*` / `StartManagerKeepAlive` tasks are removed.

### 3. Configure

```bash
sm add "C:\path\app.exe"                       # add/update a startup item (default 30s)
sm add "C:\path\app.exe" --name MyApp --title MainWindow --time 30s
sm add "C:\Scripts\WinRemap.ahk" --name WinRemap   # script: auto-matched to autohotkey
sm launchers learn .lua luajit "C:\Tools\luajit.exe"  # teach a new extension
sm add "C:\Scripts\game.lua" --name GameLua        # auto-matched from the learned table
sm list                                        # show the config
sm update                                      # interactive editor for all items
sm update MyApp                                # edit just that item
sm remove MyApp                                # remove an item
```

Changes take effect at the **next logon**. To verify immediately, run the task by hand:

```powershell
schtasks /run /tn StartManager
```

### 4. Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall_sm.ps1
```

Stops a running pass, removes the task, deletes `%LOCALAPPDATA%\StartManager\`, and drops that directory from the per-user PATH.

## Commands

| Command | Purpose |
|---|---|
| `sm add <exe\|script> [--name n] [--args s] [--title t] [--time duration] [--launcher L]` | add an item; an item with the same name is updated in place; scripts auto-match a launcher |
| `sm launchers [list\|learn\|forget]` | show built-in / learned mappings; teach a new extension; forget a learned mapping |
| `sm update [name]` | interactive config editor (item list → field form) |
| `sm remove <name>` | remove an item |
| `sm list` | show the whole config |
| `sm help` | help |

There is no `sm start` / `sm run` / `sm stop` / `sm scan` / `sm hotkey`: StartManager only handles the one silent startup at logon.

`--time` accepts `30s`, `30` (<1000 means seconds) and `5000ms`; `0` means the default 30s. `--time`/`--title`/`--args` apply to `.exe` items only; a script's host arguments come from the launcher.

## Scripts & launchers

Scripts (`.ahk / .ps1 / .bat / .vbs / .py` …) are not executables: they need a **host interpreter (launcher)**. StartManager ships common mappings and can learn new extensions.

### Built-in launchers (auto-matched)

| Script | launcher | Actual command |
|---|---|---|
| `*.ahk` | autohotkey | `AutoHotkey64.exe <script>` |
| `*.ps1` `*.psm1` | powershell | `powershell -NoProfile -ExecutionPolicy Bypass -File <script>` |
| `*.bat` `*.cmd` | cmd | `cmd /d /c <script>` |
| `*.vbs` `*.js` `*.jse` | wscript | `wscript <script>` (windowless) |
| `*.py` `*.pyw` | python | `python -B <script>` |

### Learning (extensions not built in)

```bash
# teach a mapping: .lua → luajit
sm launchers learn .lua luajit "C:\Tools\luajit.exe"

# afterwards the extension auto-matches; no --launcher needed
sm add "C:\Scripts\game.lua" --name GameLua

# show built-in + learned mappings
sm launchers list

# forget a learned mapping
sm launchers forget .lua
```

`sm launchers learn` arguments: `<ext> <id> <host> [args template] [proc]`; use `{script}` for the script path in the args template, and `proc` for the host process name to track (defaults to the id).

### Resolution order

For `sm add <script>` without `--launcher`:

1. **explicit `--launcher <id>`** (highest)
2. **learned table** (`extLaunchers` in `config.json`)
3. **the five built-ins**
4. none → reject the add, telling you to `learn` an extension or pass `--launcher`

### Script item behavior

- **Launch only, no window hiding**: host processes (AutoHotkey / powershell / cmd) are usually shared with other tools, so force-hiding them is dangerous and pointless
- Script items have no monitor duration; `sm list` shows `-` in the MONITOR column
- The launcher can be switched in `sm update` (host / args / process name are rewritten automatically)
- Editing a script's path re-resolves the host from the current launcher (or the extension)

## Configuration (config.json)

```json
{
  "items": [
    {
      "name": "Feishu",
      "exe": "C:\\Users\\AIUX\\AppData\\Local\\Feishu\\Feishu.exe",
      "args": "",
      "processName": "Feishu",
      "windowTitle": "",
      "silentWindowMs": 0,
      "enabled": true,
      "script": false,
      "launcher": "",
      "hostExe": "",
      "hostArgs": ""
    },
    {
      "name": "WinRemap",
      "exe": "C:\\Scripts\\WinRemap.ahk",
      "args": "",
      "processName": "AutoHotkey64",
      "windowTitle": "",
      "silentWindowMs": 0,
      "enabled": true,
      "script": true,
      "launcher": "autohotkey",
      "hostExe": "C:\\Program Files\\AutoHotkey\\v2\\AutoHotkey64.exe",
      "hostArgs": "{script}"
    }
  ],
  "extLaunchers": [
    {
      "ext": ".lua",
      "id": "luajit",
      "host": "C:\\Tools\\luajit.exe",
      "args": "{script}",
      "proc": "luajit"
    }
  ]
}
```

| Field | Description |
|---|---|
| `name` | item name (used by `sm remove` / `sm update`) |
| `exe` | app: the `.exe` to launch; script: the script file path |
| `args` | app command line; empty for script items (host args live in `hostArgs`) |
| `processName` | process to track; apps derive it from the `exe` file name, scripts from the launcher's `proc`; read-only in `sm update` |
| `windowTitle` | app window-title keyword; empty = every real window; unused by scripts |
| `silentWindowMs` | app monitor duration in ms; 0 = default 30000 (30s); unused by scripts |
| `enabled` | whether the item is active; disabled items are neither started nor hidden |
| `script` | true = script item (launch only, no window hiding) |
| `launcher` | launcher id used by a script (built-in or learned) |
| `hostExe` / `hostArgs` | host exe and args template resolved by the launcher (`{script}` = script path); read-only in `sm update` |
| `extLaunchers[]` | learned mapping table: `ext → id / host / args / proc` |

Window state, process-guard state, pid files and stop markers are **no longer produced** by the new version; old state files can simply be deleted.

## Monitor timing

`--time` is the **entire monitoring window** (default 30s). The engine first starts every enabled item that is not running, then re-hides visible windows every 250ms for T seconds (script items are excluded from hiding):

```
Logon → start items that aren't running → hide app windows every 250ms for T (SC_MINIMIZE → SW_HIDE fallback) → engine exits
```

It does not distinguish "already running" from "cold start": a window that pops up at any point during T is caught on the next tick. If a window appears late (slow cold start), increase that item's `--time`.

## Project layout

```
C:\project\AppManager\              ← dev / source (repo root; product name: StartManager)
├── src\
│   ├── shared\sm_shared.cs         # config model, launcher resolution/learning, P/Invoke, DoPass
│   ├── cli\sm_cli.cs               # CLI entry (add / update / remove / list / launchers)
│   ├── cli\sm_update.cs            # sm update two-level table editor
│   ├── cli\sm_style.cs             # colour + CJK-width table rendering
│   └── engine\sm_engine.cs         # logon pass (exits when done)
├── tests\
│   ├── update_test.cs              # config editor + launcher resolution tests (92)
│   └── build_update.cmd            # test compile script
├── scripts\
│   ├── install_sm.ps1              # register the logon task + update PATH
│   └── uninstall_sm.ps1            # remove task + program dir + PATH entry
├── docs\README.en.md               # this file
├── build.ps1                       # build → deploy to %LOCALAPPDATA%\StartManager
└── README.md                       # Chinese main README

%LOCALAPPDATA%\StartManager\        ← deploy / runtime
├── sm.exe                 # CLI
├── sm-engine.exe          # logon pass (no window)
├── config.json            # config
└── sm.log                 # log
```

The compiled exes keep working after the source tree is removed.

## Upgrading from the old version

The old version (`am` / AppManager) had a hotkey picker, a resident engine, window restore, a process guard and keep-alive; all of that is gone. When upgrading:

1. Run the new `scripts\install_sm.ps1`: it removes the legacy `AppManager` / `AppManagerKeepAlive` / `StartManagerKeepAlive` tasks;
2. Re-register your startup items: the old `autostart` / `hotkey` fields are no longer used. `script` / `launcher` / `hostExe` / `hostArgs` are supported again, but the simplest path is to re-add the script (`sm add "C:\Scripts\WinRemap.ahk" --name WinRemap`) and let the built-in / learned table resolve the host;
3. Old state files (`sm-engine.pid`, `sm-engine.stop.txt`, `sm-winstate.json`, `sm-guard.json`) can be deleted;
4. The old program directory `%LOCALAPPDATA%\AppManager\` is no longer used.

## License

MIT
