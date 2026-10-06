# AppManager

<p align="center">
  <img src="assets/logo.svg" alt="AppManager logo" width="120"><br>
  <strong>让 Windows 应用在登录时<em>完全静默</em>启动——没有可见窗口、没有控制台弹窗。<br>
  只有后台进程；带托盘的应用保留托盘图标，需要时一行命令或一次全局热键找回窗口。</strong>
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

两个**计划任务**（`scripts\install_am.ps1` 注册）都以无窗口方式运行引擎：
`AppManager`（登录时拉起）与 `AppManagerKeepAlive`（每 5 分钟兜底：引擎不在了就补位，热键不会因为引擎被外部杀掉而长期失效）。
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
- **全局热键选单** — 一条 `am hotkey` 设全局快捷键（如 `Ctrl+0`），随时弹出半透明应用选单：方向键一次走一行（不跳项，到分区末尾再按会跨到下一个分区）、Enter 打开、Esc 关闭；开窗 1.5s 后自动让位（释放 TOPMOST），平时不挡路
- **只动主窗口** — 打开与隐藏都只针对应用自己的主窗口：不会把 Electron/Chromium 的渲染宿主、托盘宿主、IME 等内部窗口一起弹出来，也不会去最小化它们
- **原生藏窗，保留托盘图标** — 藏窗走应用自己的"最小化到托盘"路径（`SC_MINIMIZE`），带托盘的应用（如 IM 类）图标照常保留；不支持托盘、缩进任务栏的，再由 `SW_HIDE` 兜底全隐；用户点 × 关窗本身就是应用自己的"最小化到托盘"（进程不退出、图标保留）
- **一键找回 / 按需启动** — `am start <name>`：没运行就启动，已在运行就把它的**主窗口**找回来（与选单里按 Enter 同一套逻辑）
- **程序发现** — `am scan [keyword]` 读注册表 Uninstall 键，自动推断 exe
- **交互式添加** — 不带参数 `am add` 进入逐步问答模式（含"是否登录静默启动"一问）
- **表格化配置编辑** — `am update [name]` 两级操作：先在"一行一条目"的列表里选一个应用/脚本，再编辑它的字段（`字段 | 值` 表）；方向键选择、Enter 编辑（文本字段＝行内编辑，枚举字段＝选项列表），`Esc` 逐级返回，改动即时写入 config.json
- **终端观感** — 标题栏蓝底白字、表头青色、分隔线暗灰，数字列右对齐；状态按语义着色（绿＝正常 / 黄＝需注意 / 灰＝闲置，如 `on-demand` 黄、`HIDDEN` 绿）；中文按**显示宽度**对齐不乱列。颜色只在真实终端生效——管道/日志输出自动退化为纯文本，不掺 ANSI 转义
- **按需条目（不随登录启动）** — `am add --no-autostart` 只登记不自启：照样出现在 `am list` 与热键选单里，但登录时既不启动它、也不藏它的窗口；要用时从选单按 Enter 或 `am start <name>` 拉起
- **脚本启动器** — 管理 `.ahk/.ps1/.bat/.vbs/.py/.lua/...` 脚本，启动器自动匹配 + 学习机制
- **零占用** — 无引擎窗口、无 IPC；引擎监控期结束后空闲待机；藏窗只藏窗口，不杀进程
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
am add "C:\path\app.exe" --no-autostart    # 只登记不自启（按需从选单/命令拉起）
am add                           # 交互式（逐问答，含是否登录自启）
am list                          # 查看全部条目 + 运行状态 + AT LOGON 列（APPS / SCRIPTS 分区）
am update                        # 表格化编辑全部条目（方向键 + Enter）
am update MyApp                  # 只编辑这一条
am start MyApp                   # 没运行就启动；在运行就找回它的主窗口
am hotkey                        # 设置全局热键（交互捕获，两次确认）
am hotkey clear                  # 清除热键
am remove MyApp                  # 移除条目
am start                         # 一次性拉起+藏窗后退出
am run                           # 启动常驻引擎（经登录任务启动：父进程=任务计划程序，不会被调用它的终端/作业连带杀掉）
am stop                          # 停引擎，并让看护任务在下次 am run / 登录前不再拉起它
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
│   ├── picker_test.cs         # 选单最终验收测试（A 渲染 / B 层级 / C 拖拽 / D 逻辑）
│   └── tray_test.cs           # 原生藏窗/托盘保留验收测试（N1 藏窗 / N2 选单恢复）
├── scripts\
│   ├── install_am.ps1         # 注册登录任务 + 看护任务（每 5 分钟兜底拉起引擎）
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
am add <exe|script> [--name n] [--title t] [--time 时长] [--launcher 名称] [--no-autostart]
```

| 参数 | 必填 | 说明 |
|---|---|---|
| `<exe\|script>` | 是 | 目标路径（.exe 或脚本） |
| `--name` | 否 | 管理项名称（默认取文件名） |
| `--title` | 否 | 窗口标题关键词（空 = 藏该进程全部窗口） |
| `--time` | 否 | 监控时长：`30s` / `30`（<1000=秒）/ `5000ms`；默认 30s；脚本项忽略 |
| `--launcher` | 否 | 启动器名称（内置 id 或已学习的 id）；脚本省略时自动匹配 |
| `--no-autostart` | 否 | 只登记不自启（"按需条目"）：登录时不启动、登录监控期也不藏它的窗口；要用时从选单 Enter 或 `am start <name>` 拉起 |

不带任何参数直接 `am add` → 交互问答模式。

## 按需条目（不随登录启动）

有些程序你想纳入 am 的统一入口（`am list` 看得到、热键选单里选得到），但不希望它跟着登录静默跑起来。添加时用 `--no-autostart`，或事后在 `am update` 表格里把「登录自启」列改成「按需」，就把它标成了**按需条目**：

```bash
am add "C:\path\app.exe" --no-autostart   # 添加时就不自启
am update MyApp                           # 打开表格，把「登录自启」改成「按需」
am update                                 # 或一次看到全部条目的登录自启列
```

| 行为 | 登录静默启动（默认） | 按需条目（`autostart: false`） |
|---|---|---|
| 登录时被引擎启动 | 是 | **否** |
| 登录监控期藏窗 | 是（`--time`） | **否**（am 不碰它的窗口） |
| 在 `am list` 中 | 是（AT LOGON=silent） | 是（AT LOGON=on-demand） |
| 在热键选单中 | 是（状态 stopped / running） | 是（状态 on-demand） |
| 选单里按 Enter | 启动 + 恢复主窗口 | 启动 + 恢复主窗口 |
| `am start`（一次性 pass） | 启动 + 藏窗 | 不动它 |
| `am start <name>` | 启动该条 + 恢复主窗口 | 启动该条 + 恢复主窗口 |

两种条目只差"登录时是否被自动拉起并藏窗"。按需条目永远只在被明确要求时才启动（选单 Enter 或 `am start <name>`），因此它的窗口不受 am 干预——你自己开它就正常显示。

注意：重新 `am add` 同一个目标会把它恢复成登录自启（与 `enabled` 的处理一致）；要保留按需，带上 `--no-autostart`，或事后用 `am update` 把该列改回「按需」。老 `config.json` 没有 `autostart` 字段的条目一律按登录自启处理。

## 表格化配置编辑（am update）

`am update` 分两级：**先选条目，再改该条目的字段**，每次只面对一项配置；改完即时保存。

```bash
am update              # 列出全部条目 → 选一条进入它的字段表
am update MyApp        # 直接进入 MyApp 的字段表
```

**第 1 级 · 条目列表**（一行一个应用/脚本；列：`名称 | 类型 | 登录自启 | 启用 | 监控时长 | 目标`）

| 按键 | 作用 |
|---|---|
| `↑` `↓` | 选择条目（到边界回绕） |
| `Enter` | 进入该条目的字段表 |
| `q` / `Esc` | 退出 |

**第 2 级 · 字段表**（一行一个字段：`字段 | 值`）

| 按键 | 作用 |
|---|---|
| `↑` `↓` | 选择字段（到边界回绕） |
| `Enter` | 编辑该字段：**文本字段**进入行内编辑（`Enter` 保存 · `Esc` 取消 · `←→` 移光标 · `Backspace`/`Delete` 删字符）；**枚举字段**弹出选项列表（`←→`/`↑↓` 选 · `Enter` 应用 · `Esc` 取消） |
| `Esc` | 返回条目列表（`am update <name>` 没有列表可回时＝退出） |
| `q` | 直接退出 |

**`Esc` 永远只退一级**：选项列表 → 字段表 → 条目列表 → 退出，不会卡在某一格里；只读字段（类型/宿主/启动参数/进程名）按 Enter 会明确提示"只读"，校验失败（重名、路径不存在…）会留在原地并给出红色原因。

| 字段 | 类型 | 约束 |
|---|---|---|
| 名称 | 文本 | 不能为空、不能与其他条目重名 |
| 类型 | 只读 | 应用 / 脚本（由目标后缀决定；要换类型请用 `am add`） |
| 应用地址 / 脚本地址 | 文本 | 目标必须存在；app 与脚本不能互换；改地址会自动重算进程名（app）或重新解析启动器（脚本） |
| 窗口标题 | 文本 | 空 = 该进程全部窗口（仅 app 条目） |
| 监控时长 | 文本 | `30s` / `30` / `5000ms` / `0`（仅 app 条目） |
| 启动器 | 选项 | 内置 5 种 + 已学习的 id；选中后自动写入宿主/参数/进程名（仅脚本条目） |
| 宿主 / 启动参数 | 只读 | 由启动器决定（要改就 `am launchers learn`） |
| 登录自启 | 选项 | `静默启动` / `按需`（即 `autostart`） |
| 启用 | 选项 | `是` / `否`（即 `enabled`） |
| 进程名 | 只读 | 跟踪用的进程名 |

每次被接受的修改**立即写入 `config.json`**（并在 `am.log` 留一行），表格下方显示「已保存：…」。生效时机仍是下次登录，或立即 `am stop && am run`。
输出被重定向时（管道/文件）不进入交互：打印条目总览表 + 每条目的完整字段清单，方便脚本化查看——此时配色自动失效，输出是纯文本，可以直接进日志。

## 全局热键（应用选单）

给所有管理项一个"随时找回"的图形入口：引擎常驻时按一次全局热键（默认未设置，如 `Ctrl+0`）就弹出半透明应用选单——方向键逐项移动（一次一行；到分区末尾再按会跨到下一个分区），`Enter` 打开该应用的**主窗口**（最小化到托盘的还原、隐藏的显示出来；未运行则先启动），`Esc` 或再按一次热键关闭。

```bash
am hotkey            # 交互捕获：按两次 Enter 分段确认，两轮输入一致才写入
am hotkey clear      # 清除热键（引擎下次启动后监听关闭）
```

行为细节：

- **捕获方式**：无键盘钩子，引擎侧是标准 `RegisterHotKey`；CLI 侧用 `GetAsyncKeyState` 轮询全局键盘状态。裸字母/数字键被拒绝（全局热键必须带修饰键或为 F 键），两次独立捕获必须一致才写入配置，防误触
- **不挡路**：选单开窗时置顶，~1.5 秒后自动释放 TOPMOST，普通应用可随时盖过它；可拖标题栏移动、拖边角缩放（自绘拖动/缩放，无系统原生拖动循环）
- **只碰主窗口**：打开条目只恢复该应用的**一个主窗口**并置前，不碰它的渲染/托盘/IME 等内部窗口；关窗后由应用自己的"最小化到托盘"逻辑接管，托盘图标不受影响
- **生效时机**：下次引擎启动时加载（`am stop && am run`）；引擎未运行时热键不生效
- **热键哑了先看引擎**：`am list` 末行会写 `engine: not running`，而 `am.log` 停在最后一次 `hotkey: listening …` 之后不再有新行 —— 这就是"引擎没在跑"，`am run` 一条命令即可恢复；若引擎是被外部杀掉的（任务管理器 / 强杀），`AppManagerKeepAlive` 看护任务会在 5 分钟内自动补位（**跳过启动 pass**：不会重新拉起你手动关掉的应用，也不会藏掉正在用的窗口）。引擎内部的监听线程若异常退出，会自行记一行 `hotkey: listener ended unexpectedly; restarting` 并重建监听（不必重启引擎）
- **修饰键按系统实际状态记录**：捕获读的是系统看到的按键。若系统里有按键重映射（例如 `C:\Scripts\WinRemap.ahk` 的 `LAlt::LWin`），按住 Alt 会被捕获成 `Win+…` —— 这是重映射后的真实按键，不是 am 的偏差；被重映射掉的修饰键也不要拿来做热键
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

| 字段 | 说明 |
|---|---|
| `name` | 管理项名称（`am start <name>` / `am update <name>` / `am remove <name>` 用） |
| `exe` | 目标路径（.exe 或脚本） |
| `processName` | 跟踪的进程名（脚本 = 宿主进程名） |
| `windowTitle` | 窗口标题关键词；空 = 藏全部窗口 |
| `silentWindowMs` | 监控时长（毫秒）；0 = 默认 30000（30s）；脚本项恒为 0 |
| `launcher` | 启动器名称（内置 id / 学习 id；exe 型为空） |
| `hostExe` | 宿主 exe 路径（自定义启动器用；内置由引擎自动填） |
| `hostArgs` | 启动参数模板（`{script}` = 目标路径） |
| `script` | true = 脚本项（只启动不藏窗） |
| `enabled` | 是否启用 |
| `autostart` | 是否随登录静默启动；`false` = 按需条目（只登记：不启动、也不藏窗）。老 config.json 没有这个字段时默认 `true`（与从前行为一致） |
| `extLaunchers[]` | 学习映射表（ext → 宿主） |
| `hotkey` | 全局热键（如 `"Ctrl+0"`；空 = 关闭），由 `am hotkey` 维护 |

## 监控时序

`--time` 是**整个监控窗口**（默认 30s）。引擎先启动未运行的条目，随后 T 秒内每 250ms 轮询并隐藏可见窗口（仅 app 条目）。
不区分"已运行"与"冷启动"——窗口在 T 内任何时刻弹出都会被下一轮捕获。
按需条目（`autostart: false`）不进这个 pass：登录时既不启动也不藏窗，只在被明确要求时（选单 Enter / `am start <name>`）启动。

藏窗分两级：可见的**主窗口**先走应用原生"最小化到托盘"（`SC_MINIMIZE`，保留托盘图标）；
若应用不支持托盘、缩进了任务栏，则下一轮由 `SW_HIDE` 兜底全隐。
只处理应用自己的主窗口（无 owner、非 TOOLWINDOW、非零尺寸的那一个），不会去动渲染宿主 / 托盘宿主 / IME 这些内部窗口。

```
登录 → 按需启动 → T 秒内：每 250ms 藏窗（SC_MINIMIZE → SW_HIDE 兜底）→ 待机
```

`--time` 接受的格式：`30s` = 30s；`30`（<1000）= 30s；`30000`（>=1000）= 30000ms。
窗口弹出偏晚（如冷启动慢），把该条目的 T 调大即可。

## 许可

MIT
