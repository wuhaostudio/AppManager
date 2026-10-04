# AppManager

<p align="center">
  <img src="assets/logo.svg" alt="AppManager logo" width="120"><br>
  <strong>让 Windows 应用在登录时<em>完全静默</em>启动——没有托盘图标、没有可见窗口、没有控制台弹窗。<br>
  只有后台进程，需要时一行命令找回窗口。</strong>
</p>

![GitHub stars](https://img.shields.io/github/stars/wuhaostudio/AppManager?style=flat-square)
![GitHub forks](https://img.shields.io/github/forks/wuhaostudio/AppManager?style=flat-square)
![GitHub issues](https://img.shields.io/github/issues/wuhaostudio/AppManager?style=flat-square)
![Language C#](https://img.shields.io/badge/language-C%23-68267B?style=flat-square)
![Platform Windows](https://img.shields.io/badge/platform-Windows-0078D6?style=flat-square)
![Runtime .NET Framework 4.x](https://img.shields.io/badge/runtime-.NET%20Framework%204.x-512BD4?style=flat-square)
![License MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)

> 📄 **语言 | Languages**: [中文](README.md) · [English](docs/README.en.md)

---

## 工作原理

两个用系统自带 `csc.exe` 编译的小型 .NET 可执行文件——无需 Visual Studio、无项目文件、无额外依赖：

| 可执行文件 | 编译目标 | 角色 |
|---|---|---|
| `am.exe` | `/target:exe`（控制台） | CLI：查程序、管理配置、控制引擎 |
| `am-engine.exe` | `/target:winexe`（无窗口） | 引擎：登录时静默拉起应用、轮询藏窗、待机 |

一个**计划任务**（`scripts\install_am.ps1` 注册）在登录时以无窗口方式拉起引擎。
CLI 与引擎**没有 IPC**，只通过 `config.json`、pid 文件和 OS 进程表协作。

```
  Task Planner ──logon──▶ am-engine.exe (no window) ◀──hide/show──▶ your apps
                              │
                              │ spawn / kill (pid file)
                              │
                           am.exe (CLI)
```

## 特性

- **静默启动** — 每个条目先按需启动，再隐藏其窗口（按进程名匹配，可选按标题子串过滤）
- **每条目监控时长** — 每个 app 条目一个统一监控时长（默认 30s）；`--time` 支持 `15s`、`30`、`5000ms` 写法；脚本项不做轮询藏窗，无默认监控时间
- **持续监控** — 登录启动后，`--time` 窗口期内每 250ms 持续藏窗，窗口晚弹也能追上
- **全局热键选单** — 一条 `am hotkey` 设全局快捷键（如 `Ctrl+0`），随时弹出半透明应用选单：方向键选中、Enter 打开、Esc 关闭；开窗 1.5s 后自动让位（释放 TOPMOST），平时不挡路
- **一键找回** — `am show <name>` 恢复被隐藏窗口
- **程序发现** — `am scan [keyword]` 读注册表 Uninstall 键，自动推断 exe
- **交互式添加** — 不带参数 `am add` 进入逐步问答模式
- **脚本启动器** — 管理 `.ahk/.ps1/.bat/.vbs/.py/.lua/...` 脚本，启动器自动匹配 + 学习机制
- **零占用** — 无托盘、无窗口、无 IPC；引擎监控期结束后空闲待机
- **单实例引擎** — 命名互斥锁保证，重启安全

## 快速开始

前提：Windows + .NET Framework 4.x（自带 `csc.exe`）

### 1. 编译

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出 `am.exe` 和 `am-engine.exe` 到 `%LOCALAPPDATA%\AppManager\`。

### 2. 安装

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install_am.ps1
```

注册每用户 AtLogon 无窗口任务，并清理废弃任务。

### 3. 管理程序

```bash
am scan Office                    # 查已装程序
am add "C:\path\app.exe"         # 最简添加（默认 30s、藏全部窗口）
am add "C:\path\app.exe" --name MyApp --title 主窗口 --time 30s
am add                           # 交互式（逐问答）
am list                          # 查看全部条目 + 运行状态（APPS / SCRIPTS 分区）
am hotkey                        # 设置全局热键（交互捕获，两次确认）
am hotkey clear                  # 清除热键
am show MyApp                    # 恢复窗口
am remove MyApp                  # 移除条目
am start                         # 一次性拉起+藏窗后退出
am run                           # 派生常驻引擎
am stop                          # 停引擎
```

新条目下次登录生效，或立即 `am stop && am run`。

### 4. 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall_am.ps1
```

## 项目结构

```
C:\project\AppManager\              ← 开发/源码（本目录）
├── src\
│   ├── shared\am_shared.cs    # 共享核心：配置模型、P/Invoke、DoPass、Launchers、Hotkey
│   ├── cli\am_cli.cs          # CLI 入口
│   ├── engine\am_engine.cs    # 引擎入口（静默启动 + 热键监听）
│   └── ui\am_picker.cs        # 应用选单窗口（全局热键弹出，编译进引擎）
├── tests\
│   └── picker_test.cs         # 选单最终验收测试（A 渲染 / B 层级 / C 拖拽 / D 逻辑）
├── scripts\
│   ├── install_am.ps1         # 注册登录任务
│   └── uninstall_am.ps1       # 卸载
├── docs\README.en.md           # 英文 README
├── build.ps1                   # 编译 → 部署到 AppData
└── README.md                   # 本文件（中文主版）

%LOCALAPPDATA%\AppManager\          ← 部署/运行
├── am.exe                # CLI
├── am-engine.exe         # 引擎（无窗口）
├── config.json            # 配置
├── am.log                # 日志
└── am-engine.pid         # PID
```

源码删除后已编译 exe 照常运行。

## 脚本与启动器

am 不止能管 `.exe`，还能管**脚本**——`.ahk / .ps1 / .psm1 / .bat / .cmd / .vbs / .js / .py` 等。
脚本本身不是可执行文件，必须交给一个**宿主/解释器（launcher）** 去跑。

### 内置启动器（自动匹配，无需学习）

| 脚本 | launcher 名称 | 实际启动命令 |
|---|---|---|
| `*.ahk` | autohotkey | `AutoHotkey64.exe <script>` |
| `*.ps1` `*.psm1` | powershell | `powershell -NoProfile -ExecutionPolicy Bypass -File <script>` |
| `*.bat` `*.cmd` | cmd | `cmd /d /c <script>` |
| `*.vbs` `*.js` `*.jse` | wscript | `wscript <script>`（无窗口） |
| `*.py` `*.pyw` | python | `python -B <script>` |

### 学习机制（非内置扩展）

对于内置没覆盖的脚本（如 `.lua`、`.pl`），先用 `am launchers learn` 定义一次映射，之后**同后缀自动匹配**，不用再写 `--launcher`：

```bash
# 学习一条映射（定义 .lua → luajit）
am launchers learn .lua luajit "C:\Tools\luajit.exe"

# 现在加 .lua 不用再写 --launcher 了（自动命中学习表）
am add "C:\Scripts\x.lua"

# 查看当前学习表
am launchers list

# 不再需要，删除映射
am launchers forget .lua
```

### 解析优先级

`am add xxx.<ext>` 不带 `--launcher` 时按以下顺序找宿主：

1. **学习表**（`config.json` 的 `extLaunchers`）
2. **内置 5 种**（`.ahk/.ps1/.bat/.vbs/.py`）
3. 都没有 → **报错拒绝写入**，提示先 learn 或装解释器

### 使用示例

```bash
# 内置（自动匹配，不用写 --launcher）
am add "C:\Scripts\WinRemap.ahk"             # 自动 autohotkey
am add "C:\Scripts\setup.ps1"                # 自动 powershell

# 显式指定（覆盖自动）
am add "C:\Scripts\WinRemap.ahk" --launcher autohotkey

# 学习后自动匹配
am launchers learn .lua luajit "C:\Tools\luajit.exe"
am add "C:\Scripts\game.lua"                 # 自动 luajit
```

### 脚本类条目行为

- **只启动、不藏窗口**：宿主进程（cmd/powershell/AutoHotkey64）常与其它工具共享，强行藏窗危险且无意义（AHK 热键代理本来也没主窗口）
- `am list` 的 SCRIPTS 段显示宿主进程状态（RUNNING / not-started），无监控时长列
- `--time`（监控时长）对脚本项不适用

要同时保留"既启动又藏窗"，就用普通 `.exe` 条目（app 型）。

## am add 参数

```
am add <exe|script> [--name n] [--title t] [--time 时长] [--launcher 名称]
```

| 参数 | 必填 | 说明 |
|---|---|---|
| `<exe\|script>` | 是 | 目标路径（.exe 或脚本） |
| `--name` | 否 | 管理项名称（默认取文件名） |
| `--title` | 否 | 窗口标题关键词（空 = 藏该进程全部窗口） |
| `--time` | 否 | 监控时长：`30s` / `30`（<1000=秒）/ `5000ms`；默认 30s；脚本项忽略 |
| `--launcher` | 否 | 启动器名称（内置 id 或已学习的 id）；脚本省略时自动匹配 |

不带任何参数直接 `am add` → 交互问答模式。

## 全局热键（应用选单）

给所有管理项一个"随时找回"的图形入口：引擎常驻时按一次全局热键（默认未设置，如 `Ctrl+0`）就弹出半透明应用选单——方向键移动选中项，`Enter` 打开该应用的窗口（未运行则先启动），`Esc` 或再按一次热键关闭。

```bash
am hotkey            # 交互捕获：按两次 Enter 分段确认，两轮输入一致才写入
am hotkey clear      # 清除热键（引擎下次启动后监听关闭）
```

行为细节：

- **捕获方式**：无键盘钩子，引擎侧是标准 `RegisterHotKey`；CLI 侧用 `GetAsyncKeyState` 轮询全局键盘状态。裸字母/数字键被拒绝（全局热键必须带修饰键或为 F 键），两次独立捕获必须一致才写入配置，防误触
- **不挡路**：选单开窗时置顶，~1.5 秒后自动释放 TOPMOST，普通应用可随时盖过它；可拖标题栏移动、拖边角缩放
- **生效时机**：下次引擎启动时加载（`am stop && am run`）；引擎未运行时热键不生效
- 选单列表来自当前 `config.json`，脚本项照常显示（只启动不藏窗）

## 配置（config.json）

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
  ],
  "hotkey": "Ctrl+0"
}
```

| 字段 | 说明 |
|---|---|
| `name` | 管理项名称（`am show <name>` / `am remove <name>` 用） |
| `exe` | 目标路径（.exe 或脚本） |
| `processName` | 跟踪的进程名（脚本 = 宿主进程名） |
| `windowTitle` | 窗口标题关键词；空 = 藏全部窗口 |
| `silentWindowMs` | 监控时长（毫秒）；0 = 默认 30000（30s）；脚本项恒为 0 |
| `launcher` | 启动器名称（内置 id / 学习 id；exe 型为空） |
| `hostExe` | 宿主 exe 路径（自定义启动器用；内置由引擎自动填） |
| `hostArgs` | 启动参数模板（`{script}` = 目标路径） |
| `script` | true = 脚本项（只启动不藏窗） |
| `enabled` | 是否启用 |
| `extLaunchers[]` | 学习映射表（ext → 宿主） |
| `hotkey` | 全局热键（如 `"Ctrl+0"`；空 = 关闭），由 `am hotkey` 维护 |

## 监控时序

`--time` 是**整个监控窗口**（默认 30s）。引擎先启动未运行的条目，随后 T 秒内每 250ms 轮询并隐藏可见窗口（仅 app 条目）。
不区分"已运行"与"冷启动"——窗口在 T 内任何时刻弹出都会被下一轮捕获。

```
登录 → 按需启动 → T 秒内：每 250ms 藏窗 → 待机
```

`--time` 接受的格式：`30s` = 30s；`30`（<1000）= 30s；`30000`（>=1000）= 30000ms。
窗口弹出偏晚（如冷启动慢），把该条目的 T 调大即可。

## 许可

MIT
