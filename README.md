# StarRailAutoBattle 启动器

一个在 Windows 本机运行的桌面小工具（简体中文、浅色界面）：

- 可选：点击“启动”时用系统默认浏览器打开一个视频网址；
- 启动游戏：像在资源管理器里双击一样，打开你选的 `.exe` 或 `.lnk` 快捷方式；
- 只识别已运行游戏：不启动任何程序，找到已经由官方启动器打开的游戏窗口，尝试还原并切到前台。

**交付形式：** 便携版单文件 `StarRailAutoBattle.exe`（自包含 .NET 运行时，用户电脑不需要另外安装 .NET，不需要安装程序）。

## 技术方案

| 项 | 选择 |
| --- | --- |
| 语言 / 框架 | C# 13 / .NET 10，WPF 桌面界面 |
| 启动程序 | Win32 `ShellExecuteEx`（默认动作，与资源管理器双击相同；不使用 `runas`，不拼接命令行） |
| 读取快捷方式 | Windows Shell `IShellLink`（只读，不调用 `Resolve`，不修改 .lnk） |
| 识别窗口 | `QueryFullProcessImageName` + `EnumWindows`，按“进程文件名 + 完整程序路径”匹配，从不只凭窗口标题 |
| 置前 | `ShowWindowAsync(SW_RESTORE)` + `SetForegroundWindow`，然后用 `GetForegroundWindow` 核实结果；不使用模拟按键等绕过系统限制的手段 |
| 权限 | 应用清单为 `asInvoker`，从不自行请求管理员权限 |

## 开发环境与命令

需要：Windows 10/11 x64，[.NET SDK 10.0.100 或更新的 10.0 版本](https://dotnet.microsoft.com/download)（见 `global.json`）。不需要 Node.js。

```powershell
dotnet restore StarRailAutoBattle.slnx
dotnet build StarRailAutoBattle.slnx
dotnet test StarRailAutoBattle.slnx          # 自动化测试
dotnet run --project src/GameLauncher         # 开发运行
```

生成发布用的 exe（先跑测试，再打包到 `dist\StarRailAutoBattle.exe`）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

等价的单条命令：`dotnet publish src/GameLauncher -c Release -r win-x64 -p:DebugType=none -o dist`

`bin/`、`obj/`、`dist/` 等构建产物已在 `.gitignore` 中忽略。

## 使用

1. **视频网址（可选）**：填完整的 `http://` 或 `https://` 网址；留空则只处理游戏。格式不对时会在输入框下提示，且**不会执行任何操作**。
2. **游戏入口**：手动输入路径或点“浏览文件…”。支持真实 `.exe` 和 `.lnk` 快捷方式。下方会显示文件的**实际类型**（按文件内容判断，不按显示名），以及快捷方式的目标、参数、起始位置和程序要求的权限。
   - 资源管理器默认隐藏 `.lnk` 扩展名：显示为 `XXX.exe` 的快捷方式，真实文件名是 `XXX.exe.lnk`。如果手动输入了不带 `.lnk` 的名称，应用会提示。
   - 从资源管理器“复制文件地址”粘贴的带引号路径会自动去掉引号。
3. **运行模式**：选“启动游戏”或“只识别已运行游戏”，点“启动”。
4. **识别窗口用的游戏程序（可选）**：入口是官方启动器时，在这里选游戏本体 `.exe`，用于识别游戏窗口；留空则使用入口实际指向的程序。

### 点击“启动”后的实际顺序

1. 校验网址和路径。任何一项不合格：只显示错误，**不打开网页、不启动程序**。
2. 若填写了网址：请求默认浏览器打开。结果单独显示在状态区的“视频”一行（✓ / ✗ / –），**与游戏结果互不影响**。
3. 处理游戏：
   - **启动游戏**：`ShellExecuteEx` 打开入口。若程序清单要求管理员权限，Windows 会弹出 UAC 确认，由你决定；选“否”会显示“已取消启动”。程序若在 3 秒内退出，会显示退出码。随后最多等待 90 秒检测游戏窗口，找到后尝试切到前台（可点“停止等待窗口”）。
   - **只识别已运行游戏**：不创建任何进程。找到窗口就还原并请求置前；没找到时提示“未检测到已运行的游戏（…），请先通过官方启动器启动，再返回本应用重试。”
4. 状态区显示结果；“诊断详情”里有操作阶段和 Windows 错误码（只含文件名，不含完整路径）。

视频先于游戏处理，是为了让游戏窗口最后出现在前面。

### 窗口识别规则

- 进程文件名必须与目标程序相同；能读取进程完整路径时，路径也必须一致（同名但不同位置的程序不算）。
- 只考虑可见、无所有者、非工具窗口的顶层窗口（最小化的也算）。
- Windows 不允许读取进程路径时，**不会自动切换**，而是列出候选窗口请你确认。
- 置前后会核实前台窗口；Windows 拒绝时如实提示，请手动点击任务栏中的游戏图标，或点“切换到游戏窗口”重试（由你点击按钮时，系统通常允许切换）。

## 关于星穹铁道

在开发机上核查（只读）得到的事实：

- 测试快捷方式 `StarRail_for_test.exe` 的真实文件名是 `StarRail_for_test.exe.lnk`，指向游戏本体 `StarRail.exe`（miHoYo 签名有效），无参数，起始位置为游戏目录。
- `StarRail.exe` 的嵌入清单声明 `requestedExecutionLevel level="requireAdministrator"`。
- 旧版用 Node.js `child_process.spawn`（底层 `CreateProcess`）启动。`CreateProcess` 无法启动要求管理员权限的程序，返回 `ERROR_ELEVATION_REQUIRED (740)`，而 libuv 把它映射为 `EACCES`（errno `-4092`）。在同一台电脑上用另一个声明 `requireAdministrator` 的系统程序复现了完全相同的 `spawn … EACCES -4092`。

因此，旧版报错**最可能**的原因是 `spawn` 这条路径不能触发 UAC，而不是文件读取权限或安全软件拦截。本应用改用 `ShellExecuteEx`，由 Windows 按正常流程弹出 UAC。

**Windows 实机验证结果（开发者本机）：** 选择星铁快捷方式和直接选择 `StarRail.exe` 都能打开游戏；先用官方启动器启动游戏后，“只识别已运行游戏”模式可以把游戏窗口切到前台。若在其他电脑上游戏要求必须经官方启动器启动：入口选官方启动器（或其快捷方式），“识别窗口用的游戏程序”选 `StarRail.exe`；或者先用官方启动器开游戏，再用“只识别已运行游戏”模式。本应用不会绕过启动器、反作弊或 UAC。

另外：官方启动器和游戏通常以管理员身份运行，而本应用以普通权限运行。在某些系统上 Windows 可能因此不允许本应用还原最小化的游戏窗口或把它切到前台；应用会如实提示，请手动点击任务栏。

## 本机数据

- 偏好（上次的入口路径、识别用程序路径、模式）：`%LOCALAPPDATA%\StarRailAutoBattle\settings.json`。**视频网址不保存。**
- 诊断日志：`%LOCALAPPDATA%\StarRailAutoBattle\logs\launcher.log`（只记录文件名、错误码和阶段，不记录完整路径、快捷方式参数内容或网址；超过 512 KB 自动轮换）。
- 不联网（打开视频网址由默认浏览器完成），不上传任何路径，不需要任何 API Key。

## Windows 人工验收清单

在打包后的 `dist\StarRailAutoBattle.exe` 上逐项检查（自动化测试**不会**真的启动程序）：

| # | 操作 | 预期 |
| --- | --- | --- |
| 1 | 双击 `StarRailAutoBattle.exe` | 窗口正常打开，不出现 UAC 提示；无需安装 .NET |
| 2 | 入口选普通 `.exe`（如 `C:\Windows\System32\notepad.exe`），模式“启动游戏”，网址留空 | 视频行显示“未填写”；记事本打开；显示进程 ID；随后能识别窗口并切到前台 |
| 3 | 给记事本建一个桌面快捷方式，在属性里加参数（如一个 .txt 路径）和起始位置，选这个 `.lnk` | 类型显示“Windows 快捷方式（.lnk）”，目标/参数/起始位置与属性一致；启动后按参数打开该文件 |
| 4 | 网址填 `https://example.com` 再执行第 2 项 | 默认浏览器打开网页；视频行 ✓；游戏结果单独显示 |
| 5 | 网址填 `example.com` 或 `ftp://x` | 提示格式错误；不打开网页，也不启动程序 |
| 6 | 入口填不存在的路径 / 一个文件夹 / 一个 `.txt` / 相对路径 | 分别显示中文错误；点“启动”不执行任何操作 |
| 7 | 入口填 `…\StarRail_for_test.exe`（不带 .lnk） | 提示同目录存在 `StarRail_for_test.exe.lnk`、扩展名被隐藏 |
| 8 | 入口选星铁快捷方式或 `StarRail.exe`，“启动游戏” | 出现 UAC；选“否”→“已取消启动”；选“是”→游戏正常打开（开发者本机已验证） |
| 9 | 入口选官方启动器（或其快捷方式），识别用程序选 `StarRail.exe`，“启动游戏” | 启动器打开；在启动器里点“开始游戏”后，90 秒内识别到游戏窗口；超时会给出下一步提示 |
| 10 | 用官方启动器开好星铁，切到别的窗口，回到本应用选“只识别已运行游戏”，点“启动” | 切到游戏窗口；若 Windows 拒绝，显示“未能切到前台”并出现“切换到游戏窗口”按钮 |
| 11 | 同上但先把游戏最小化 | 窗口被还原并置前；若因权限差异无法还原，会如实提示手动点击任务栏 |
| 12 | 星铁没有运行时，用“只识别已运行游戏” | 显示“未检测到已运行的游戏（StarRail.exe），请先通过官方启动器启动，再返回本应用重试。” |
| 13 | 关闭并重新打开应用 | 入口路径、识别用程序和模式被记住；视频网址不保留 |

## 项目结构

```
StarRailAutoBattle.slnx
global.json                       .NET SDK 版本
scripts/publish.ps1               测试 + 打包为 dist\StarRailAutoBattle.exe
src/GameLauncher.Core/            与界面无关的逻辑（可测试）
  EntryInspector.cs               路径校验、按内容判断真实类型、解析快捷方式
  ExecutableInspector.cs          PE 架构、清单权限级别
  ShortcutReader.cs               IShellLink 只读解析
  ShellLauncher.cs                ShellExecuteEx 启动 + 提前退出检测
  LaunchErrorMapper.cs            Win32 错误码 → 分类与中文提示
  WindowMatcher.cs                窗口匹配规则（纯逻辑）
  WindowService.cs                枚举进程/窗口、还原与置前
  AppStorage.cs                   偏好与诊断日志
  Native/NativeMethods.cs         Win32 / COM 声明
src/GameLauncher/                 WPF 界面（MainWindow、确认窗口对话框、app.manifest）
tests/GameLauncher.Tests/         xUnit 测试
```

## 已知限制

- 星铁的启动与置前已在开发者本机验证；其他电脑的系统设置、启动器版本不同，结果可能不同。
- 如果游戏的反作弊阻止普通程序查询其进程路径，应用会改为请你确认窗口，不会自动切换。
- 快捷方式没有普通文件目标（如应用商店或安装程序创建的特殊快捷方式）时，仍可启动，但识别窗口需另外指定游戏程序。
- 只做了 x64 版本。
