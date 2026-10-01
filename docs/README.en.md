# AppManager

<p align="center">
  <img src="../assets/logo.svg" alt="AppManager logo" width="120"><br>
  <strong>Windows apps that start at logon <em>completely silently</em> — no tray icon, no visible window, no console popup.<br>
  Just background processes, restored when you ask for them.</strong>
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
| `am.exe` | `/target:exe` (console) | CLI: discover programs, manage config, control the engine |
| `am-engine.exe` | `/target:winexe` (no window) | Engine: silently starts apps at logon, polls to hide windows, then stands by |

A **scheduled task** (registered by `scripts\install_am.ps1`) launches the engine windowlessly at logon.
The CLI and the engine have **no IPC** between them — they coordinate only via `config.json`, the pid file, and the OS process table.

```
  Task Planner ──logon──▶ am-engine.exe (no window) ◀──hide/show──▶ your apps
                              │
                              │ spawn / kill (pid file)
                              │
                           am.exe (CLI)
```

## Features

- **Silent start** — each item is started on demand, then its windows are hidden (matched by process name, optionally filtered by title substring)
- **Per-item monitor time** — a unified poll-hide monitor duration per app item (default 30s); script items have no monitor time (launch-only); `--time` accepts `15s`, `30`, `5000ms`
- **Continuous monitor** — after logon the engine re-hides windows every 250ms for the whole monitor window, so late popups get caught too
- **One-line restore** — `am show <name>` brings a hidden window back
- **Program discovery** — `am scan [keyword]` reads the registry Uninstall keys and infers the real exe
- **Interactive add** — bare `am add` drops into a step-by-step Q&A
- **Script launchers** — manage `.ahk/.ps1/.bat/.vbs/.py/.lua/...` scripts; launchers auto-match + learn
- **Zero footprint** — no tray, no window, no IPC; the engine idles after the monitor window
- **Single-instance engine** — guaranteed by a named mutex, restart-safe

## Quick start

Prereq: Windows + .NET Framework 4.x (ships `csc.exe`)

### 1. Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Outputs `am.exe` and `am-engine.exe` to `%LOCALAPPDATA%\AppManager\`.

### 2. Install

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install_am.ps1
```

Registers a per-user AtLogon windowless task and cleans up deprecated tasks.

### 3. Manage programs

```bash
am scan Office                    # find installed programs
am add "C:\path\app.exe"         # minimal add (default 30s, hide all windows)
am add "C:\path\app.exe" --name MyApp --title MainWindow --time 30s
am add                           # interactive (Q&A)
am list                          # all items + live state (APPS / SCRIPTS sections)
am show MyApp                    # restore window
am remove MyApp                  # remove entry
am start                         # one-shot: start + hide, then exit
am run                           # spawn the resident engine
am stop                          # stop the engine
```

New items take effect at next logon, or immediately via `am stop && am run`.

### 4. Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall_am.ps1
```

## Project layout

```
C:\project\AppManager\              ← dev / source
├── src\
│   ├── shared\am_shared.cs    # shared core: config model, P/Invoke, DoPass, Launchers
│   ├── cli\am_cli.cs          # CLI entry point
│   └── engine\am_engine.cs    # engine entry point
├── scripts\
│   ├── install_am.ps1         # register the logon task
│   └── uninstall_am.ps1       # uninstall
├── docs\README.en.md           # this file (English)
├── build.ps1                   # build → deploy to AppData
└── README.md                   # main README (Chinese)

%LOCALAPPDATA%\AppManager\          ← deployed / runtime
├── am.exe                # CLI
├── am-engine.exe         # engine (no window)
├── config.json            # config
├── am.log                # log
└── am-engine.pid         # PID
```

The compiled exes keep working even if the sources are deleted.

## Scripts & launchers

am manages more than `.exe` files — it also manages **scripts**: `.ahk / .ps1 / .psm1 / .bat / .cmd / .vbs / .js / .py`, and so on.
A script is not executable on its own; it must be run through a **host / interpreter (launcher)**.

### Built-in launchers (auto-matched, nothing to learn)

| Script | Launcher id | Actual launch command |
|---|---|---|
| `*.ahk` | autohotkey | `AutoHotkey64.exe <script>` |
| `*.ps1` `*.psm1` | powershell | `powershell -NoProfile -ExecutionPolicy Bypass -File <script>` |
| `*.bat` `*.cmd` | cmd | `cmd /d /c <script>` |
| `*.vbs` `*.js` `*.jse` | wscript | `wscript <script>` (no window) |
| `*.py` `*.pyw` | python | `python -B <script>` |

### Learning (extensions not built in)

For scripts the built-ins don't cover (e.g. `.lua`, `.pl`), define the mapping once with `am launchers learn`; afterwards the **same extension auto-matches** without `--launcher`:

```bash
# learn one mapping (.lua → luajit)
am launchers learn .lua luajit "C:\Tools\luajit.exe"

# adding .lua no longer needs --launcher (hits the learned table)
am add "C:\Scripts\x.lua"

# view the learned table
am launchers list

# no longer needed, drop the mapping
am launchers forget .lua
```

### Resolution order

`am add xxx.<ext>` without `--launcher` looks for a host in this order:

1. **Learned table** (`extLaunchers` in `config.json`)
2. **The 5 built-ins** (`.ahk/.ps1/.bat/.vbs/.py`)
3. Neither → **the write is refused with an error**, suggesting `learn` or installing the interpreter

### Usage examples

```bash
# built-in (auto-matched, no --launcher needed)
am add "C:\Scripts\WinRemap.ahk"             # auto autohotkey
am add "C:\Scripts\setup.ps1"                # auto powershell

# explicit (overrides auto-match)
am add "C:\Scripts\WinRemap.ahk" --launcher autohotkey

# auto-match after learning
am launchers learn .lua luajit "C:\Tools\luajit.exe"
am add "C:\Scripts\game.lua"                 # auto luajit
```

### Behavior of script-type items

- **Launch only, no window hiding**: host processes (cmd/powershell/AutoHotkey64) are usually shared with other tools, so force-hiding them is dangerous and pointless (an AHK hotkey agent has no main window anyway)
- the SCRIPTS section of `am list` shows host process state (RUNNING / not-started); no monitor time for scripts
- `--time` (monitor duration) does not apply to script items

To keep "start AND hide windows", use a regular `.exe` item (app type) instead.

## am add parameters

```
am add <exe|script> [--name n] [--title t] [--time duration] [--launcher id]
```

| Param | Required | Description |
|---|---|---|
| `<exe\|script>` | yes | target path (.exe or script) |
| `--name` | no | entry name (defaults to the file name) |
| `--title` | no | window title keyword (empty = hide all windows of the process) |
| `--time` | no | monitor duration: `30s` / `30` (<1000 = seconds) / `5000ms`; default 30s; ignored for scripts |
| `--launcher` | no | launcher id (built-in id or learned id); auto-matched when omitted for scripts |

Bare `am add` with no arguments → interactive Q&A mode.

## Configuration (config.json)

```json
{
  "items": [
    {
      "name": "FeiShu",
      "exe": "C:\\Users\\AIUX\\AppData\\Local\\Feishu\\Feishu.exe",
      "processName": "Feishu",
      "windowTitle": "",
      "silentWindowMs": 0,
      "launcher": "",
      "hostExe": "",
      "hostArgs": "",
      "script": false,
      "enabled": true
    }
  ],
  "extLaunchers": [
    { "ext": ".lua", "id": "luajit", "host": "C:\\Tools\\luajit.exe", "args": "{script}", "proc": "luajit" }
  ]
}
```

| Field | Description |
|---|---|
| `name` | entry name (used by `am show <name>` / `am remove <name>`) |
| `exe` | target path (.exe or script) |
| `processName` | process to track (for scripts: the host process name) |
| `windowTitle` | window title keyword; empty = hide all windows |
| `silentWindowMs` | monitor duration in ms; 0 = default 30000 (30s); scripts always 0 |
| `launcher` | launcher id (built-in id / learned id; empty for exe type) |
| `hostExe` | host exe path (for custom launchers; filled in automatically for built-ins) |
| `hostArgs` | args template (`{script}` = target path) |
| `script` | true = script item (launch only, no hiding) |
| `enabled` | whether the item is active |
| `extLaunchers[]` | learned extension→host mapping table |

## Monitor timing

`--time` is the **entire monitoring window** (default 30s, app items only). The engine starts any item
that is not already running, then for T seconds it polls every 250ms and hides any visible window.
There is **no** distinction between "already running" vs "cold-started" — the window may pop up
at any moment during T and gets caught on the next tick.

```
logon → start-if-needed → for T seconds: hide visible windows every 250ms → standby
```

Accepted `--time` formats: `30s` = 30s; `30` (<1000) = 30s; `30000` (>=1000) = 30000ms.
If a window surfaces late (e.g. a slow cold start), just set a larger T for that item.

## License

MIT
