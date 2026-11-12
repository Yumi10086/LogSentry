# LogSentry · Windows 日志一把梭分析

便携式 Windows 日志分析小工具：勾选多个检测项 → 一键顺序执行 → 在同一个窗口里比对
「攻击失败痕迹」和「攻击成功痕迹」。例如 4625 刷屏 + 紧随其后的 4624 成功登录 + 4720 新建账户 +
7045 安装服务，就是一条完整的爆破成功链。

底层调用微软 Log Parser 2.2（`LogParser.exe`）。**不需要 Python、Node 或任何第三方运行时**：
单文件 exe 拷到任意 Win10 / Win11 机器双击即可（系统自带 .NET Framework 4.8），自动 UAC 提权。

> 单文件包是 **7-Zip SFX**：解压器是 7-Zip 自带的原生模块，解压这一步不依赖任何 .NET。
> 双击后是 7-Zip 自己的「Extract to:」对话框 —— 选目录、Extract，然后在里面双击
> `LogSentry\LogSentry.exe`。包内还带 `NDP48-x86-x64-AllOS-ENU.exe`（.NET Framework 4.8
> 离线安装包，115.7 MB），机器没装运行库时自己双击装上、重启再启动主程序。
> 主程序要求 **.NET Framework 4.5+**（源码用了 async/await 与 ZipArchive，都是 4.5 才有的）。

![界面](docs/screenshots/ui-2-after.png)

## 功能

- **26 个内置检测项**，覆盖登录爆破、账户变更、服务安装（PsExec 痕迹）、计划任务、日志清除、
  Defender 状态、Sysmon 进程创建等常见痕迹；可任意多选，按顺序执行。
- **时间筛选**：顶部筛选条支持最近 1h / 24h / 3d / 7d / 30d 与自定义区间，可精确到分秒；
  走 SQL 的 `TimeGenerated` 条件，不改 `queries.txt`。
- **手动命令**：左栏「手动命令」页可直接手写 SQL 或整条 LogParser 命令行，Ctrl+Enter 执行，
  历史命令自动留存。
- **导出**：文本 txt / CSV（每项一个文件 + 概述）/ Excel xlsx（每项一个工作表），
  CSV 与 xlsx 走 `-o:CSV` 重新查询生成，不在 NAT 文本上切列，中文不会串位。
- **配置驱动**：界面完全由 `queries.txt` 生成，新增检测项只改配置，不用改代码、不用重编译。

## 快速开始

1. 到 [Releases](https://github.com/Yumi10086/LogSentry/releases) 下载 `LogSentry_Portable.exe`（仓库里不存这个 116.7 MB 的成品，GitHub 单文件上限 100 MB），双击 → 7-Zip 的「Extract to:」对话框里选目录
   （建议 `D:\LogSentry`）→ `Extract` → 进入 `D:\LogSentry\LogSentry\` 双击 `LogSentry.exe`
   → UAC 确认 → 界面打开。
   （那个文件夹里就是完整一套文件：`LogParser.exe`、配置、说明书，以及
   `NDP48-x86-x64-AllOS-ENU.exe`。目标机没装 .NET Framework 4.5+ 时，主程序会先弹
   「初始化错误」——双击这个安装包装好、重启一次即可。解压阶段不检测、不代跑。）
2. 左栏「按分类勾选」里挑检测项，例如同时勾上「登录失败」与「登录成功」。
3. 点 `▶ 开始分析（F5）`，结果区按项分隔显示，末尾给出成功 / 失败 / 命中汇总。
4. 需要留档就点「导出结果」，选 xlsx 或 csv。

只用 `dist/LogSentry.exe` 也可以：把它和 `LogParser.exe`、`queries.txt` 放在同一目录再双击。

## 交付物

| 文件 | 说明 |
| --- | --- |
| `LogSentry_Portable.exe`（[Releases](https://github.com/Yumi10086/LogSentry/releases) 附件） | **单文件便携版**（7-Zip SFX：`7z.sfx` + 配置 + `payload.7z`，含 `.NET Framework 4.8` 离线安装包；双击后手动选目录解压，再双击里面的 `LogSentry.exe`） |
| `dist/LogSentry.exe` | 主程序本体，版本 1.0.0.0，需与 `LogParser.exe`、`queries.txt` 同目录 |
| `dist/LogParser.exe` | 微软 Log Parser 2.2，查询引擎 |
| `dist/LogParser.chm` | Log Parser 官方帮助文档 |
| `dist/queries.txt` | 检测项配置，改它就能增减检测项 |
| `dist/使用说明.txt` | 面向使用者的手册：界面、时间筛选、判读方法、导出、配置扩展示例、常见问题 |
| `NDP48-x86-x64-AllOS-ENU.exe`（仓库根目录） | .NET Framework 4.8 离线安装包（微软官方，未修改，121,346,568 B），打包时会被塞进便携包的 `LogSentry\` 里；缺运行库的机器自己双击装。不入库（`.gitignore`） |

## 仓库结构

```text
LogSentry/
├─ src/                     源码（C# 5 / .NET Framework 4.x，用系统自带 csc 直编）
│  ├─ app.manifest          requireAdministrator（双击即 UAC 提权）
│  ├─ Program.cs / EntryPoint.cs   启动入口、命令行分发（--selftest / --smoke）
│  ├─ MainForm.cs           全部界面与执行流程（左栏三页 + 结果区 + 导出）
│  ├─ Engine.cs             LogParser 封装：参数拼装、编码、超时、输出清理
│  ├─ Exporters.cs          CSV / XLSX 生成（手写 OOXML + ZipArchive）
│  ├─ Config.cs / BuiltinQueries.cs   queries.txt 解析 + 内置检测项
│  ├─ SelfTest.cs           --selftest 的 12 组断言
│  └─ UiSmokeTest.cs        --smoke 的界面/导出/手动命令自动化冒烟
├─ build.ps1                csc 编译 → dist/LogSentry.exe + _selftest.exe + _smoke.exe
├─ pack-sfx.ps1             7za 打 .pkg/payload.7z + 拼 7z.sfx/config/archive → dist/LogSentry_Portable.exe
├─ sfx-config.txt           7-zip SFX 配置正文（UTF-8 无 BOM，打包时加首尾标记）
├─ dist/                    交付产物（见上表）
├─ docs/
│  ├─ screenshots/          界面截图
│  ├─ evidence/             自检 / 冒烟 / 导出的实测输出
│  └─ DEVELOPMENT.md        开发与验证记录（LogParser 实测坑、迭代记录、复检命令）
└─ queries.txt              （dist/ 内副本）检测项配置
```

## 技术选型

- **C# WinForms / .NET Framework 4.x**：系统自带，Win10/11 是 4.8，无需额外运行库。
- 编译只用系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（C# 5 语法），
  不需要 Visual Studio、Roslyn 或 Reference Assemblies。
- **单文件包 = 7-Zip SFX**：`.tools\sfx\7z.sfx`（官方 GUI 解压模块，原生 PE）+ UTF-8 无 BOM 的
  `config.txt` + `.pkg\payload.7z` 三段字节拼接。解压器是原生的，不依赖任何 .NET。
  官方模块**不解析** `;!@Install@!` 段里的 `RunProgram / Directory / GUIMode`（实测：双击只弹
  「Extract to:」对话框），所以没有自动启动；带安装脚本能力的是第三方 `7zSD.sfx`，
  官方 extra 包里不提供。`sfx-config.txt` 里这几行仍然保留，将来换 `7zSD.sfx` 不用改一行。
- `src/app.manifest` 写死 `requireAdministrator`：读 Security 日志必须有管理员权限。
- 执行用 `Process` + `async/await` + `RedirectStandardOutput`，`CreateNoWindow = true` 不弹黑框，
  输出按 OEM 代码页（简体中文为 CP936）解码，中文不乱码。

## 构建

```powershell
# 编译 dist\LogSentry.exe + dist\_selftest.exe + dist\_smoke.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 再打单文件便携版（输出 dist\LogSentry_Portable.exe，里面带 .NET 4.8 离线安装包；这个文件不入库，发版时传 Releases 附件）
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-sfx.ps1

# 换压缩级别 / 换 SFX 模块
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-sfx.ps1 -CompressionLevel 9
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-sfx.ps1 -SfxModule .tools\sfx\7z.sfx
```

`build.ps1` 自动寻找 `csc.exe`（优先 Framework64，回退 32 位），优先使用
`Reference Assemblies\...\v4.8`，否则回退 Framework 目录里的 GAC 引用。

`pack-sfx.ps1` 用 `.tools\extra\7za.exe`（仓库自带，不用装 7-Zip）把
`.pkg\stage\LogSentry\` 打成 `.pkg\payload.7z`（`-mx=5 -mhc=on`），再按
**`7z.sfx` + `config.txt` + `payload.7z`** 顺序拼成 `dist\LogSentry_Portable.exe`。
`config.txt` 由 `sfx-config.txt` 读入（显式 UTF-8，并校验没有 BOM），加
`;!@Install@!UTF-8!` / `;!@InstallEnd@!` 首尾标记。

> 为什么不继续用自写的托管外壳：它必须挑一个 CLR——编成 CLR 4 时，缺 .NET 4 的机器连解压都
> 进不去；编成 CLR 2.0 时，Win10/11 默认没开 .NET 3.5 又起不来。7-Zip SFX 是原生 PE，
> 两头都不受影响，代价只是"要自己点一下 Extract"。

## 自检

```powershell
cd dist
.\_selftest.exe                                            # 命令行全链路自检
.\_smoke.exe --smoke . service_install,logon_success       # 无人值守跑一遍真实界面
```

`_selftest.exe` 全绿时最后一行是 `=== 全部通过 ===`；`_smoke.exe` 会真实开窗、勾选、执行、
截图并写出 `ui-results.txt`，最后一行 `RESULT = OK`。完整断言清单、参数说明与复检命令见
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。

非管理员会话下 Security 类检测项会报权限不足，这是预期行为，`_smoke.exe` 会汇报成
`RESULT = OK(部分项按预期失败)`。

## 架构

`MainForm` 不认任何具体事件号，界面完全按 `queries.txt` 生成。新增检测项 = 往 `queries.txt`
加一段 `[id] / label / log / query`，重启即见：

```ini
[logon_fail]
label: 登录失败 (4625)
log: Security
query: SELECT TimeGenerated, EXTRACT_TOKEN(Strings, 5, '|') AS TargetUser, ...
```

执行全程 `await`，每项之间刷新状态栏与计数，执行期禁用「开始分析」，「停止」随时可用。
结果渲染按样式攒进缓冲区、节拍统一写入，长输出列表也不拖慢界面。

## 文档

- `dist/使用说明.txt` —— 面向使用者
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) —— 开发与验证记录（LogParser 实测坑、迭代记录、复检命令、源码维护注意）
- `dist/LogParser.chm` —— Log Parser 官方帮助

## 许可与声明

本仓库仅包含自研代码与微软官方免费分发工具 Log Parser 2.2 的原始二进制，未做修改。
`LogParser.exe` / `LogParser.chm` 版权归 Microsoft Corporation 所有，按其随附许可使用。
其余代码可用于任何用途，不提供任何担保。
