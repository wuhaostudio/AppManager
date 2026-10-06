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

Two **scheduled tasks** (registered by `scripts\install_am.ps1`) run the engine windowlessly: `AppManager` (launches it at logon) and `AppManagerKeepAlive` (a 5-minute backstop that fills in whenever the engine is gone, so the hotkey cannot stay dead for long after an external kill).
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
- **Global hotkey picker** — one `am hotkey` sets a global shortcut (e.g. `Ctrl+0`) that pops a semi-transparent app picker: arrows step one row at a time (no skipped rows; at a section edge the next press crosses into the other section), Enter opens, Esc closes; TOPMOST is auto-released ~1.5s after opening so it never blocks you
- **Main window only** — opening and hiding both target the app's own main window: Chromium/Electron render hosts, tray hosts and IME helper windows are never shown or minimized by am
- **Native window hiding, tray icons preserved** — hiding goes through the app's own "minimize to tray" path (`SC_MINIMIZE`), so tray-capable apps (IM clients etc.) keep their tray icons; apps that end up on the taskbar are then fully hidden via `SW_HIDE` as a fallback. Closing the window is the app's own minimize-to-tray, so the process and its tray icon survive
- **One-line restore / on-demand start** — `am start <name>`: starts the item if it is not running, otherwise brings its **main window** back (the same logic as pressing Enter in the picker)
- **Program discovery** — `am scan [keyword]` reads the registry Uninstall keys and infers the real exe
- **Interactive add** — bare `am add` drops into a step-by-step Q&A (including "start it silently at logon?")
- **Table-style config editing** — `am update [name]` works in two levels: first pick an app/script from the one-row-per-item list, then edit that item's fields (a `字段 | 值` form); arrows select, Enter edits (inline line editor for text, option list for enums), `Esc` steps one level back, and every accepted edit is written to config.json immediately
- **Terminal look** — filled title bar, cyan headers, dim rules, right-aligned numbers, and state colouring (green = as intended, yellow = look here, grey = idle — e.g. `on-demand` yellow, `HIDDEN` green); CJK text is aligned by **display width**. Colours only apply in a real terminal: piped or logged output degrades to plain text with no ANSI escapes
- **On-demand items (no logon start)** — `am add --no-autostart` registers an item without starting it: it still shows in `am list` and in the hotkey picker, but the logon pass never starts it and never hides its windows; launch it with Enter in the picker or `am start <name>`
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
am add "C:\path\app.exe" --no-autostart    # register only, no logon start (launch on demand)
am add                           # interactive (Q&A, incl. logon auto-start)
am list                          # all items + live state + AT LOGON column (APPS / SCRIPTS sections)
am update                        # editable table of every item (arrows + Enter)
am update MyApp                  # edit just that item
am start MyApp                   # start it if needed, otherwise bring its main window back
am hotkey                        # set the global hotkey (interactive capture, two confirmations)
am hotkey clear                  # remove the hotkey
am remove MyApp                  # remove entry
am start                         # one-shot: start + hide, then exit
am run                           # start the resident engine (via the logon task: parent = Task Scheduler, so it is not killed with the shell/job that called it)
am stop                          # stop the engine and hold the keep-alive task off until the next am run / logon
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
│   ├── shared\am_shared.cs    # shared core: config model, P/Invoke, DoPass, Launchers, Hotkey
│   ├── cli\am_cli.cs          # CLI entry point
│   ├── engine\am_engine.cs    # engine entry (silent start + hotkey listener)
│   └── ui\am_picker.cs        # app picker window (global hotkey, compiled into the engine)
├── tests\
│   ├── picker_test.cs         # picker acceptance test (A render / B z-order / C drag / D logic)
│   ├── tray_test.cs           # native-hide / tray-preserve acceptance test (N1 hide / N2 picker restore)
│   └── window_probe.cs        # picker window interactive probe (manual drag/resize/keyboard, non-asserting)
├── scripts\
│   ├── install_am.ps1         # register the logon task + the 5-minute keep-alive task
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
am add <exe|script> [--name n] [--title t] [--time duration] [--launcher id] [--no-autostart]
```

| Param | Required | Description |
|---|---|---|
| `<exe\|script>` | yes | target path (.exe or script) |
| `--name` | no | entry name (defaults to the file name) |
| `--title` | no | window title keyword (empty = hide all windows of the process) |
| `--time` | no | monitor duration: `30s` / `30` (<1000 = seconds) / `5000ms`; default 30s; ignored for scripts |
| `--launcher` | no | launcher id (built-in id or learned id); auto-matched when omitted for scripts |
| `--no-autostart` | no | register only (an "on-demand" item): never started at logon and its windows are never hidden by the logon pass; launch it from the picker (Enter) or with `am start <name>` |

Bare `am add` with no arguments → interactive Q&A mode.

## On-demand items (no logon start)

Some programs belong in am's single entry point (`am list` and the hotkey picker) without being started silently at every logon. Mark them **on-demand** with `--no-autostart` when adding, or later by switching the 登录自启 column to 按需 inside `am update`:

```bash
am add "C:\path\app.exe" --no-autostart   # no logon start right away
am update MyApp                           # open the table and set 登录自启 to 按需
am update                                 # or show that column for every item at once
```

| Behaviour | Silent logon start (default) | On-demand (`autostart: false`) |
|---|---|---|
| Started by the engine at logon | yes | **no** |
| Windows hidden during the logon monitor | yes (`--time`) | **no** (am leaves its windows alone) |
| Present in `am list` | yes (AT LOGON=silent) | yes (AT LOGON=on-demand) |
| Present in the hotkey picker | yes (state stopped / running) | yes (state on-demand) |
| Enter in the picker | start + restore main window | start + restore main window |
| `am start` (one-shot pass) | start + hide | untouched |
| `am start <name>` | start that item + restore its main window | start that item + restore its main window |

The two kinds differ only in "is it started and hidden automatically at logon". An on-demand item starts only when explicitly asked for (Enter in the picker, or `am start <name>`), so am never touches its windows — open it yourself and it just shows.

Note: re-running `am add` for the same target resets it to silent logon start (same handling as `enabled`); keep it on-demand by passing `--no-autostart`, or by switching the column back in `am update`. Items in an older `config.json` without the `autostart` field are treated as silent logon start.

## Table-style editing (am update)

`am update` works in two levels: **pick an item, then edit that item's fields** — you only ever face one item's config at a time, and edits are saved immediately.

```bash
am update              # list every item → pick one to open its field form
am update MyApp        # open that item's field form directly
```

**Level 1 · item list** (one row per app/script; columns `名称 | 类型 | 登录自启 | 启用 | 监控时长 | 目标`)

| Key | Action |
|---|---|
| `↑` `↓` | pick an item (wraps at the edges) |
| `Enter` | open its field form |
| `q` / `Esc` | quit |

**Level 2 · field form** (one row per field: `字段 | 值`)

| Key | Action |
|---|---|
| `↑` `↓` | pick a field (wraps) |
| `Enter` | edit it: **text fields** open an inline line editor (`Enter` saves · `Esc` cancels · `←→` moves the caret · `Backspace`/`Delete`); **enum fields** open an option list (`←→`/`↑↓` pick · `Enter` applies · `Esc` cancels) |
| `Esc` | back to the item list (with `am update <name>` there is no list, so it quits) |
| `q` | quit right away |

**`Esc` always steps exactly one level up** — option list → field form → item list → quit — so nothing traps you. Read-only fields (类型/宿主/启动参数/进程名) say so when you press Enter, and validation failures (duplicate name, missing path, …) keep you in place with a red reason line.

| Field | Type | Rules |
|---|---|---|
| 名称 | text | not empty, no duplicate name |
| 类型 | read-only | app / script (decided by the target extension; use `am add` to change kind) |
| 应用地址 / 脚本地址 | text | target must exist; app and script items cannot be swapped; changing it re-derives the process name (app) or re-resolves the launcher (script) |
| 窗口标题 | text | empty = all windows of that process (app items only) |
| 监控时长 | text | `30s` / `30` / `5000ms` / `0` (app items only) |
| 启动器 | choice | the 5 built-ins + learned ids; picking one rewrites host path/args/process name (script items) |
| 宿主 / 启动参数 | read-only | decided by the launcher (use `am launchers learn` to change) |
| 登录自启 | choice | `静默启动` / `按需` (the `autostart` field) |
| 启用 | choice | `是` / `否` (the `enabled` field) |
| 进程名 | read-only | the process name being tracked |

Every accepted edit is **written to `config.json` immediately** (one line in `am.log`), and the status line below the form shows `已保存：…`. It still takes effect at the next logon, or right away with `am stop && am run`.
When the output is redirected (pipe/file) the command does not go interactive: it prints the item overview plus every field of every item, which keeps it scriptable — colours drop out there automatically, so the output is plain text that can go straight into a log.

## Global hotkey (app picker)

A graphical "find me anytime" entry for every managed item: while the engine is resident, pressing the global hotkey (none by default, e.g. `Ctrl+0`) pops a semi-transparent app picker — arrows move the selection one row at a time (crossing into the next section at an edge), Enter opens the app's **main window** (restored from the tray or shown again; started first if not running), Esc or pressing the hotkey again closes it.

```bash
am hotkey            # interactive capture: two Enter-gated rounds, written only when both agree
am hotkey clear      # remove the hotkey (listener goes off at next engine start)
```

Behavior details:

- **Capture**: no keyboard hooks — the engine side uses the standard `RegisterHotKey`; the CLI side polls global keyboard state via `GetAsyncKeyState`. Bare letters/digits are rejected (a global hotkey must carry a modifier or be an F-key); two independent captures must agree before the config is written, guarding against typos
- **Never blocks**: the picker opens topmost, then TOPMOST is released ~1.5s after opening so ordinary apps can cover it; the window can be dragged by its caption and resized from the corner (self-drawn move/resize, no native NC drag loop)
- **Main window only**: opening an item restores exactly one window (the app's main one) and never touches its render/tray/IME helper windows; after you close it the app's own minimize-to-tray logic takes over and the tray icon stays
- **Effective from**: the next engine start (`am stop && am run`); while the engine is not running the hotkey does nothing
- **A dead hotkey is usually a dead engine**: `am list` ends with `engine: not running`, and `am.log` shows nothing after the last `hotkey: listening …` line — that is exactly the "engine is not resident" state, cured by a single `am run`; after an external kill (Task Manager / force-kill) the `AppManagerKeepAlive` task fills in within 5 minutes, **skipping the logon pass** so it neither relaunches apps you closed nor hides windows you are using. If the engine's listener thread ever exits on its own it logs `hotkey: listener ended unexpectedly; restarting` and rebuilds the listener in place (no engine restart needed)
- **Modifiers are recorded as the system sees them**: capture reads the OS key state, so a key remapper (e.g. `C:\Scripts\WinRemap.ahk` doing `LAlt::LWin`) makes a held Alt capture as `Win+…` — that is the remapped key, not an am deviation; a remapped-away modifier is also a poor choice for a hotkey
- The picker lists the current `config.json`; script items show up as usual (launch-only, no hiding)

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
      "enabled": true,
      "autostart": true
    }
  ],
  "extLaunchers": [
    { "ext": ".lua", "id": "luajit", "host": "C:\\Tools\\luajit.exe", "args": "{script}", "proc": "luajit" }
  ],
  "hotkey": "Ctrl+0"
}
```

| Field | Description |
|---|---|
| `name` | entry name (used by `am start <name>` / `am update <name>` / `am remove <name>`) |
| `exe` | target path (.exe or script) |
| `processName` | process to track (for scripts: the host process name) |
| `windowTitle` | window title keyword; empty = hide all windows |
| `silentWindowMs` | monitor duration in ms; 0 = default 30000 (30s); scripts always 0 |
| `launcher` | launcher id (built-in id / learned id; empty for exe type) |
| `hostExe` | host exe path (for custom launchers; filled in automatically for built-ins) |
| `hostArgs` | args template (`{script}` = target path) |
| `script` | true = script item (launch only, no hiding) |
| `enabled` | whether the item is active |
| `autostart` | start silently at logon; `false` = on-demand item (registered only: never started, never hidden). A `config.json` written before this field existed defaults to `true`, i.e. the previous behaviour |
| `extLaunchers[]` | learned extension→host mapping table |

## Monitor timing

`--time` is the **entire monitoring window** (default 30s, app items only). The engine starts any item
that is not already running, then for T seconds it polls every 250ms and hides any visible window.
There is **no** distinction between "already running" vs "cold-started" — the window may pop up
at any moment during T and gets caught on the next tick.
On-demand items (`autostart: false`) are not part of this pass: they are neither started nor hidden
at logon, and start only when explicitly asked (Enter in the picker / `am start <name>`).

Hiding is two-tiered: the visible **main window** first goes through the app's own "minimize to tray"
(`SC_MINIMIZE`), which keeps the tray icon of tray-capable apps; an app that ends up on the taskbar
(no tray support) is then fully hidden via `SW_HIDE` on the next tick.
Only the app's own main window is touched (unowned, non-tool, non-zero-size) — render/tray/IME
helper windows are left alone.

```
logon → start-if-needed → for T seconds: hide every 250ms (SC_MINIMIZE → SW_HIDE fallback) → standby
```

Accepted `--time` formats: `30s` = 30s; `30` (<1000) = 30s; `30000` (>=1000) = 30000ms.
If a window surfaces late (e.g. a slow cold start), just set a larger T for that item.

## License

MIT
