# StartManager

<p align="center">
  <img src="assets/logo.svg" alt="StartManager logo" width="120"><br>
  <strong>让 Windows 应用在登录时<em>完全静默</em>启动——没有可见窗口、没有控制台弹窗。<br>
  只做一件事：按你的配置在登录时拉起应用并藏好窗口；引擎跑完就退出。</strong>
</p>

![GitHub stars](https://img.shields.io/github/stars/wuhaostudio/StartManager?style=flat-square)
![GitHub forks](https://img.shields.io/github/forks/wuhaostudio/StartManager?style=flat-square)
![GitHub issues](https://img.shields.io/github/issues/wuhaostudio/StartManager?style=flat-square)
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
| `sm.exe` | `/target:exe`（控制台） | 配置 CLI（命令 `sm`）：添加、修改、删除、查看登录启动项 |
| `sm-engine.exe` | `/target:winexe`（无窗口） | 登录时执行一次静默 pass；跑完即退出 |

一个**计划任务**（`scripts\install_sm.ps1` 注册）：`StartManager`（用户登录时以无窗口方式运行 `sm-engine.exe`）。

CLI 与引擎**没有 IPC**，只通过部署目录里的 `config.json` 协作。引擎不常驻、不监听热键、不托盘、不提供任何 UI：

```
  登录 ──▶ StartManager 任务 ──▶ sm-engine.exe（无窗口）
                                      │
                                      │ 启动未运行的条目 + 轮询藏窗
                                      ▼
                                  配置里的应用
                                      │
                                   跑完退出
```

## 特性

- **登录静默启动** — 每个启用的条目：未运行就先启动，然后隐藏它的窗口
- **监控时长** — 每个条目一个监控窗口（默认 30s）；期间每 250ms 持续藏窗，窗口晚弹出也能追上
- **只动真实窗口** — 只处理应用自己的主窗口（无 owner、非 TOOLWINDOW、非零尺寸）；不碰渲染宿主、托盘宿主、IME 等内部窗口
- **原生藏窗，保留托盘图标** — 藏窗走应用自己的"最小化到托盘"路径（`SC_MINIMIZE`）；缩进任务栏的窗口由 `SW_HIDE` 兜底
- **按需禁用** — 条目可设为"停用"，停用项不启动也不藏窗，但配置保留
- **脚本启动器（内置 + 学习）** — 内置 `.ahk/.ps1/.bat/.vbs/.py` 等宿主解析；`sm launchers learn` 教会新后缀（如 `.lua → luajit`）后，`sm add xxx.lua` 自动匹配宿主；脚本只启动、不藏窗
- **启动参数** — `.exe` 条目可带命令行参数；脚本的宿主/参数模板由启动器决定，可在 `sm update` 里切换启动器
- **只做配置** — 无常驻引擎、无热键、无弹窗、无窗口找回、无进程 guard、无 keep-alive、无自动重拉
- **零依赖** — 无第三方库、无 UI 框架；一个后台进程只在登录时短暂运行

## 快速开始

前提：Windows + .NET Framework 4.x（自带 `csc.exe`）

### 1. 编译

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出 `sm.exe` 和 `sm-engine.exe` 到 `%LOCALAPPDATA%\StartManager\`。

### 2. 安装

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install_sm.ps1
```

注册登录任务 `StartManager`，并把部署目录加入当前用户 PATH（新开终端后可直接用 `sm`）。旧版本的 `AppManager*` / `StartManagerKeepAlive` 任务会被清理。

### 3. 配置

```bash
sm add "C:\path\app.exe"                       # 添加/更新一个启动项（默认 30s）
sm add "C:\path\app.exe" --name MyApp --title 主窗口 --time 30s
sm add "C:\Scripts\WinRemap.ahk" --name WinRemap   # 脚本自动匹配 autohotkey
sm launchers learn .lua luajit "C:\Tools\luajit.exe"  # 教一个新后缀
sm add "C:\Scripts\game.lua" --name GameLua        # 自动匹配学习表
sm list                                        # 查看配置
sm update                                      # 交互式编辑全部条目
sm update MyApp                                # 只编辑这一条
sm remove MyApp                                # 删除条目
```

改动在**下次登录**生效；想立即验证可手动运行任务：

```powershell
schtasks /run /tn StartManager
```

### 4. 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall_sm.ps1
```

停止正在运行的 pass、注销任务、删除 `%LOCALAPPDATA%\StartManager\`，并从当前用户 PATH 移除该目录。

## 命令

| 命令 | 作用 |
|---|---|
| `sm add <exe\|script> [--name n] [--args s] [--title t] [--time 时长] [--launcher L]` | 添加条目；同名条目原地更新；脚本自动匹配启动器 |
| `sm launchers [list\|learn\|forget]` | 查看内置/学习表；教新后缀；删除学习映射 |
| `sm update [name]` | 交互式配置编辑器（条目列表 → 字段表） |
| `sm remove <name>` | 删除条目 |
| `sm list` | 查看全部配置 |
| `sm help` | 帮助 |

没有 `sm start` / `sm run` / `sm stop` / `sm scan` / `sm hotkey`：StartManager 只负责登录时的那一次静默启动。

`--time` 接受 `30s`、`30`（<1000 视为秒）、`5000ms`；`0` 表示使用默认 30s。`--time`/`--title`/`--args` 只对 `.exe` 条目生效；脚本的宿主参数由启动器决定。

## 脚本与启动器

`.ahk / .ps1 / .bat / .vbs / .py` 等脚本不是可执行文件，必须交给**宿主解释器（launcher）**。StartManager 内置了常见映射，也支持学习新后缀。

### 内置启动器（自动匹配）

| 脚本 | launcher | 实际启动命令 |
|---|---|---|
| `*.ahk` | autohotkey | `AutoHotkey64.exe <script>` |
| `*.ps1` `*.psm1` | powershell | `powershell -NoProfile -ExecutionPolicy Bypass -File <script>` |
| `*.bat` `*.cmd` | cmd | `cmd /d /c <script>` |
| `*.vbs` `*.js` `*.jse` | wscript | `wscript <script>`（无窗口） |
| `*.py` `*.pyw` | python | `python -B <script>` |

### 学习机制（非内置后缀）

```bash
# 教一条映射：.lua → luajit
sm launchers learn .lua luajit "C:\Tools\luajit.exe"

# 之后同后缀自动匹配，不用再写 --launcher
sm add "C:\Scripts\game.lua" --name GameLua

# 查看内置 + 学习表
sm launchers list

# 删除学习映射
sm launchers forget .lua
```

`sm launchers learn` 的参数：`<ext> <id> <host> [args模板] [proc]`；`args模板` 里用 `{script}` 表示脚本路径，`proc` 表示要跟踪的宿主进程名（默认等于 id）。

### 解析优先级

`sm add <script>` 不带 `--launcher` 时：

1. **显式 `--launcher <id>`**（最高）
2. **学习表**（`config.json` 的 `extLaunchers`）
3. **内置 5 种**
4. 都没有 → 报错拒绝写入，提示先 `learn` 或指定 `--launcher`

### 脚本条目行为

- **只启动、不藏窗**：宿主进程常与其它工具共享（AutoHotkey / powershell / cmd），强行藏窗危险且无意义
- 脚本条目没有监控时长；`sm list` 的 MONITOR 显示 `-`
- 可在 `sm update` 里切换启动器（宿主/参数/进程名会自动重写）
- 修改脚本地址时会按当前启动器（或后缀）重新解析宿主

## 配置（config.json）

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

| 字段 | 说明 |
|---|---|
| `name` | 条目名称（`sm remove` / `sm update` 用） |
| `exe` | 应用：要启动的 `.exe`；脚本：脚本文件路径 |
| `args` | 应用条目的命令行参数；脚本条目为空（宿主参数在 `hostArgs`） |
| `processName` | 跟踪的进程名；应用由 `exe` 文件名推导，脚本由启动器的 `proc` 决定，`sm update` 中只读 |
| `windowTitle` | 应用条目的窗口标题关键词；空 = 该进程全部真实窗口；脚本不使用 |
| `silentWindowMs` | 应用条目的监控时长（毫秒）；0 = 默认 30000（30s）；脚本不使用 |
| `enabled` | 是否启用；停用项不启动也不藏窗 |
| `script` | true = 脚本条目（只启动、不藏窗） |
| `launcher` | 脚本使用的启动器 id（内置或学习 id） |
| `hostExe` / `hostArgs` | 由启动器解析出的宿主 exe 与参数模板（`{script}` = 脚本路径）；`sm update` 中只读 |
| `extLaunchers[]` | 学习映射表：`ext → id / host / args / proc` |

窗口状态、进程 guard、pid、stop 标记等文件在新版本中**不再产生**；升级时可以直接删除旧状态文件。

## 监控时序

`--time` 是**整个监控窗口**（默认 30s）。引擎先启动所有未运行的启用条目，随后 T 秒内每 250ms 轮询并隐藏可见窗口（脚本条目不参与藏窗）：

```
登录 → 启动未运行条目 → T 秒内每 250ms 藏应用窗口（SC_MINIMIZE → SW_HIDE 兜底）→ 引擎退出
```

不区分"已运行"与"冷启动"：窗口在 T 内任何时刻弹出都会被下一轮捕获。窗口弹出偏晚（冷启动慢）时，把该条目的 `--time` 调大即可。

## 项目结构

```
C:\project\StartManager\            ← 开发/源码（仓库根目录；产品名 StartManager）
├── src\
│   ├── shared\sm_shared.cs         # 配置模型、启动器解析/学习、P/Invoke、DoPass
│   ├── cli\sm_cli.cs               # CLI 入口（add / update / remove / list / launchers）
│   ├── cli\sm_update.cs            # sm update 两级表格编辑器
│   ├── cli\sm_style.cs             # 配色 + 中文宽度对齐的表格渲染
│   └── engine\sm_engine.cs         # 登录 pass（跑完退出）
├── tests\
│   ├── update_test.cs              # 配置编辑器 + 启动器解析测试（92 项）
│   └── build_update.cmd            # 测试编译脚本
├── scripts\
│   ├── install_sm.ps1              # 注册登录任务 + 更新 PATH
│   └── uninstall_sm.ps1            # 注销任务 + 删除部署目录 + 移除 PATH
├── docs\README.en.md               # 英文 README
├── build.ps1                       # 编译 → 部署到 %LOCALAPPDATA%\StartManager
└── README.md                       # 本文件（中文主版）

%LOCALAPPDATA%\StartManager\        ← 部署/运行
├── sm.exe                 # CLI
├── sm-engine.exe          # 登录 pass（无窗口）
├── config.json            # 配置
└── sm.log                 # 日志
```

源码删除后已编译 exe 照常运行。

## 从旧版升级

旧版（`am` / AppManager）包含热键选单、常驻引擎、窗口找回、进程 guard、keep-alive 等能力；这些已全部移除。升级时：

1. 运行新的 `scripts\install_sm.ps1`：会注销旧 `AppManager` / `AppManagerKeepAlive` / `StartManagerKeepAlive` 任务；
2. 重新登记启动项：旧配置里的 `autostart` / `hotkey` 字段不再使用；`script` / `launcher` / `hostExe` / `hostArgs` 现在重新受支持，但推荐直接重加脚本（`sm add "C:\Scripts\WinRemap.ahk" --name WinRemap`），让内置/学习表自动解析宿主；
3. 旧状态文件（`sm-engine.pid`、`sm-engine.stop.txt`、`sm-winstate.json`、`sm-guard.json`）可以删除；
4. 旧部署目录 `%LOCALAPPDATA%\AppManager\` 不再使用。

## 许可

MIT
