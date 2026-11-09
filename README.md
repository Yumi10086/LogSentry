# LogSentry · Windows 日志一把梭分析

便携式 Windows 日志分析小工具：勾选多个检测项 → 一键顺序执行 → 在同一个窗口里比对
「攻击失败痕迹」和「攻击成功痕迹」（例如 4625 刷屏 + 紧随的 4624 成功登录 + 4720 建号 + 7045 装服务，
就是一条完整的爆破成功链）。

底层调用微软 `LogParser.exe`。**不用 Python、不用 Node、不用装任何运行时**：
单文件 exe 拷到任意 Win10 / Win11 机器双击即可（系统自带 .NET Framework 4.8），
UAC 自动提权。Win7 SP1 / Win8 需要系统里已有 .NET Framework 4.5+（用了 `async/await`）。

界面三段：顶部 LogParser 路径（`浏览…`/`测试`）、中部时间筛选条 + 可滚动检测项、
底部通栏操作条（`▶ 开始分析（F5）` / 停止 / 清空结果 / 导出结果）与结果区。
时间筛选走 SQL 的 `TimeGenerated` 条件（`-b`/`-e` 这类参数 LogParser 2.2 没有），
勾上后本次所有查询自动带 `TO_TIMESTAMP(...)` 区间，不动 `queries.txt`。

![界面](docs/screenshots/ui-2-after.png)

## 交付物

| 文件 | 说明 |
| --- | --- |
| `dist/LogSentry_Portable.exe` | **单文件便携版**（7-Zip SFX，1134394 字节 / 1107.8 KB，解压后在临时目录下建 `LogSentry\` 文件夹再自动启动；SHA256 `E65A8A3E…7D3B`） |
| `dist/LogSentry.exe` | 主程序本体，138240 字节，版本 1.0.0.0（SHA256 `84B075D3…49C3`），需与 `LogParser.exe`、`queries.txt` 同目录 |
| `dist/LogParser.exe` | 微软 Log Parser 2.2（1159920 字节），查询引擎 |
| `dist/queries.txt` | 26 个检测项的配置，改这个就能增减检测项，**不用改代码、不用重编译** |
| `dist/使用说明.txt` | 面向使用者的手册（19514 字节）：界面（含左栏三页与「手动命令」）、时间筛选（含秒级与「此刻」）、判读方法、导出三种格式、配置扩展示例、常见问题 |

辅助目录（不参与打包）：

| 路径 | 说明 |
| --- | --- |
| `docs/screenshots/` | 界面截图（执行前 / 执行后 / 最大化 / 参数页） |
| `docs/evidence/` | 验收证据：自检输出、界面冒烟输出、UAC manifest 静态校验、历史崩溃日志 |
| `sfx-config.txt` | SFX 配置正文模板（UTF-8，中文标题写在这里，脚本读入时不会被 ANSI 解码弄乱） |

## 仓库结构

```text
LogSentry/
├─ src/                     源码（C# 5，.NET Framework 4.x，单个 csproj 即可用 csc 直编）
│  ├─ app.manifest          requireAdministrator（双击即 UAC 提权）
│  ├─ Program.cs / EntryPoint.cs   启动入口、命令行分发（--selftest / --smoke）
│  ├─ MainForm.cs           全部界面与执行引擎（左栏三页 + 结果区 + 导出）
│  ├─ Engine.cs             LogParser 封装：参数拼装、编码、超时、输出清理
│  ├─ Exporters.cs          CSV / XLSX 生成（手写 OOXML + ZipArchive）
│  ├─ Config.cs / BuiltinQueries.cs   queries.txt 解析 + 26 个内置检测项
│  ├─ SelfTest.cs           --selftest 的 12 组断言
│  └─ UiSmokeTest.cs        --smoke 的界面/导出/手动命令自动化冒烟
├─ build.ps1                csc 编译 → dist/LogSentry.exe + _selftest.exe + _smoke.exe
├─ pack-sfx.ps1             7-Zip SFX 三段拼接 → dist/LogSentry_Portable.exe
├─ sfx-config.txt           SFX 配置正文（UTF-8，中文标题放这里）
├─ dist/                    交付产物（见上表）
├─ docs/
│  ├─ screenshots/          界面截图
│  └─ evidence/             自检/冒烟/导出的实测输出
└─ queries.txt              （dist/ 内副本）检测项配置，改它就能增减检测项
```

重新打包前需要 7-Zip 的 `7z.sfx`：本仓库不收录二进制，把它放到 `.tools/sfx/7z.sfx`（来自 7-Zip 安装包的 `7z.sfx`，或用 `7zS.sfx`），再运行 `powershell -File .\pack-sfx.ps1`。
## 技术选型

- **C# WinForms / .NET Framework 4.x**（系统自带，Win10/11 是 4.8，Win7 SP1 可用 4.0+）。
- 编译只用系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（C# 5 语法），
  不需要 Visual Studio、不需要 Roslyn、不需要 Reference Assemblies。
  所以 `$"..."` 这类 C# 6 语法一律不用，字符串拼接用 `+`。
- `src/app.manifest` 写死 `<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>`，
  双击即弹 UAC——读 Security 日志必须有管理员权限。
- 执行用 `Process` + `async/await` + `RedirectStandardOutput`，`CreateNoWindow = true`
  （不弹黑框），`StandardOutputEncoding = Encoding.GetEncoding(936)`（LogParser 按 OEM 代码页输出，
  简体中文系统是 CP936，用 UTF-8 会乱码）。

## 构建

```powershell
# 编译 dist\LogSentry.exe + dist\_selftest.exe + dist\_smoke.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 再打单文件便携版（输出 dist\LogSentry_Portable.exe）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -PackSfx
```

`build.ps1` 会自动找 `csc.exe`（优先 Framework64，回退 32 位）、优先用
`Reference Assemblies\...\v4.8` 否则回退 Framework 目录里的 GAC 引用。
输出全部是 ASCII 提示，避免中文在 `powershell -File` 下被当 token 解析。

`pack-sfx.ps1` 单独调用也可以：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-sfx.ps1
```

它把 `LogSentry.exe / LogParser.exe / LogParser.chm / queries.txt / 使用说明.txt` 装进
`archive.7z`，再用 `7z.sfx + config.txt + archive.7z` 三段字节拼接成单文件。
`config.txt` 必须是 **UTF-8 无 BOM**（脚本会显式检查前 3 字节不是 `EF BB BF`），
否则 SFX 会把整段配置当注释吞掉、解压后不启动。

## 自检

```powershell
cd dist
.\_selftest.exe                          # 命令行全链路自检
.\_smoke.exe --smoke . service_install,psExec_service   # 无人值守跑一遍真实界面
```

`_selftest.exe` 覆盖 12 组检查：配置解析（26 项 / 0 警告 / id 唯一）、
`queries.txt` 与 `BuiltinQueries` 文本一致（防两者跑偏）、参数构造与引号转义、
逐项真实执行、Statistics 块裁剪与 `Elements processed` 解析、空结果不算失败、
语法错误必须判失败、权限不足的中文提示、空白压缩（行数不变 + 逐行字符不丢）、
时间筛选（条件生成、WHERE 追加 / 补 WHERE / GROUP BY 前插入 / 字符串字面量里的
`ORDER` 不误判，以及同一查询加区间后命中数从 204 降到 25 的真实对比）、
CSV/XLSX 生成与回读（`[10]`）、五种输出格式与空结果占位（`[11]`）、
手动命令命令行解析与真实执行（`[12]`）。

`_smoke.exe` 会 `new MainForm()` → `Show()` → 勾选指定 id → 跑完整队列（期间泵消息、
检测界面是否卡死）→ `PrintWindow` 截图 → 写 `smoke.log` / `ui-results.txt`。
之所以用 `PrintWindow`：`DrawToBitmap` 画不出 `RichTextBox` 的内容，
`CopyFromScreen` 会被窗口位置和遮挡干扰。可选参数：`--hours N`（打开「最近 N 小时」时间筛选，
走的是和用户点界面完全相同的控件）、`--max`（额外截一张最大化尺寸的图，验证不重叠）、
`--tab2`（切到「参数与超时」页截图）、`--format X`（切换输出格式）、
`--export-xlsx / --export-csv`（导出并校验）、`--manual '<命令行>'`（走「手动命令」页执行，
并打印解析回显与 `Fit.Manual` 布局断言）。

```powershell
.\_smoke.exe --smoke . service_install,psExec_service --hours 24 --max
```

## 架构

```
src/
  EntryPoint.cs      程序入口；--selftest / --smoke 分支在这里分流
  Program.cs         单实例 Mutex（Global\LogSentry.SingleInstance.7F1C0A80）、
                     非管理员时 Process.Start(Verb=runas) 自提权、顶层异常写 LogSentry_error.log
  MainForm.cs        全代码构建 UI（无 Designer）：动态复选框、异步执行队列、结果渲染；
                     左栏三页（按分类勾选 / 参数与超时 / 手动命令）、SyncTabPageBounds、
                     RunSelectionAsync / RunAsyncCore / RunManualAsync、导出三选一
  Config.cs          queries.txt 解析（[id] / key: value / # 注释），UTF-8 严格解码失败回退 GBK；`ShowEventIds`
  Engine.cs          LogParserRunner：BuildArguments、RunAsync（异步读流 + 超时 Kill）、
                     CleanStandardOutput、ParseProcessedCount、HumanizeError、CollapsePadding、
                     BuildTimeCondition / InjectTimeFilter / EffectiveQuery（时间筛选）、
                     StripNatOnlyArguments / BuildExportExtraArguments（导出用参数清洗）
                     NormalizeFormat / IsNat / EncodingForFormat（XML 走 UTF-16）
                     InsertIntoClause / FindTopLevelKeyword（W3C 落地临时文件）
                     SplitCommandLine / ExtractOptionValue / TokenizeArguments / EffectiveFormat（手动命令）
  Exporters.cs       ExportSheet + WriteCsv / ParseCsv / WriteXlsx（手写 OOXML，ZipArchive）/
                     RowsFromLogParserCsv —— 导出 CSV 与 Excel，不依赖 Excel/EPPlus
  BuiltinQueries.cs  内嵌 26 个检测项，首启动时落成 queries.txt
  AutomationRun.cs   无人值守执行的参数/结果载体
  SelfTest.cs        命令行自检
  UiSmokeTest.cs     界面冒烟 + 截图
  AssemblyInfo.cs    版本信息（1.0.0.0），让 exe 属性里能看出这是什么
  app.manifest       requireAdministrator + Win7~Win11 supportedOS + dpiAware
```

**配置驱动**：`MainForm` 不认任何具体事件号，界面完全按 `queries.txt` 生成。
新增检测项 = 往 `queries.txt` 加一段 `[id] / label / log / query`，重启即见。

**不冻结**：`RunSelectionAsync` 全程 `await`，每项之间 `SetStatus` + `UpdateCountLabel`，
`_btnStart` 在执行期禁用、`_btnStop` 可用（取消后当前项跑完即中断）。

## 导出

「导出结果」弹三选一：

| 格式 | 产物 | 说明 |
| --- | --- | --- |
| 文本 txt | 单个 `.txt` | 与界面完全一致的纯文本，UTF-8 带 BOM |
| CSV | 每项一个 `.csv` + `00_概述.csv` | Excel 双击即开；概述表记导出时间 / LogParser 路径 / 时间范围 / 附加参数 |
| Excel xlsx | 单个 `.xlsx` | 每项一个工作表，`inlineStr` 单元格、首行冻结、列宽按内容自适应 |

结构化导出**不在 NAT 文本上切列**——NAT 按字符数对齐，中文会把后面的列挤错位。做法是用
`-o:CSV` 重新查一遍拿规范 CSV（`Exporters.RowsFromLogParserCsv`），再写 CSV 或手写 OOXML
（`Exporters.WriteXlsx`，只用 `System.IO.Compression`，不依赖 Excel / EPPlus）。
失败项也会写一行 `[!] 查询失败` + 原因，不会被静默丢掉。

## LogParser 的坑（实测记录）

| 现象 | 结论 |
| --- | --- |
| 输出中文乱码 | 输出编码是 OEM 代码页 CP936，不是 UTF-8 |
| `-full` 报 `invalid parameter`（exit 87） | LogParser 2.2 没有 `-full`；要全文用 `-i:EVT -fullText`（输入选项） |
| NAT 只显示 10 行 | `-o:NAT` 默认 `-rtp:10`，必须 `-rtp:-1` |
| `-q:on` 之后没有表头、没有 Statistics | `-q:on` 会吞掉表头行、`--- ---` 分列线**和**尾部统计块；想要状态栏的「累计处理」就别开它 |
| `-o` 给两次报 `specified more than once`（exit 13） | 输出格式只能指定一次 |
| 没有 `-b` / `-e` 时间参数 | LogParser 2.2 全局参数只有 `-i/-o/-q/-e/-iw/-stats/-saveDefaults/-queryInfo/-c/-multiSite`；时间过滤只能写进 SQL 的 `TimeGenerated` 条件（用 `TO_TIMESTAMP('...','yyyy-MM-dd HH:mm:ss')`） |
| 语法错误 | stderr/stdout 出现 `Error: SELECT clause: Syntax Error: unknown field 'X'`，exit 非 0 |
| 空结果 | stdout 为空、exit 0 → 视为成功但标注「无匹配记录」 |
| 非管理员读 Security | `Error opening event log "Security": 客户端没有所需的特权。` → 翻译成中文处置建议 |
| 列对齐 | NAT 用空格补齐，且列宽按**字符数**算，中文值会溢出到下一列；因此不做硬切列，只把 ≥2 个连续空格压成 2 个（`CollapsePadding`），保证不切坏路径 |
| `-o:CSV` 加 `-rtp:-1` 报 `Error: invalid parameter "rtp"`（exit 87） | `-rtp` 是 NAT 专用参数；导出 CSV 前必须剥掉（`StripNatOnlyArguments`），并可用 `-stats:off` 关掉尾部统计块 |
| `-o:TSV` / `-o:XML` 带 `-rtp:-1` 同样 exit 87 | 同上：`BuildArguments` 现在只要输出格式不是 NAT 就自动剥掉 `-rtp`，界面里留着 `-rtp:-1` 也不会报错 |
| `-o:XML` 读出来是 ` ? x m l …` 一个字符夹一个空格 | 那是 UTF-16LE（带 `FF FE` BOM，声明 `encoding="ISO-10646-UCS-2"`）；`EncodingForFormat` 对 XML 返回 `Encoding.Unicode` |
| `-o:XML` 尾部冒出一串乱码汉字 | XML 正文是 UTF-16，但 Statistics 块仍按 ANSI 写；按 UTF-16 解码后找不到 `Statistics:`，所以 XML 改成在 `</ROOT>` 处截断 |
| `-o:XML` 一项都没命中时，结果显示成 `埭瑯鐚欏幓…` 这种乱码汉字 | 没命中行时 LogParser **根本不写 XML**，stdout 里只剩 ANSI 的 Statistics 块（实测 114 字节，首字节 `13,10,83,116,…` = `\r\nStatistics:`），按 UTF-16 解码就是乱码。`CleanStandardOutput` 找不到 `</ROOT>` 时直接判空并写占位文本；空 stdout（`-q:on` 时）也同样写占位 |
| `-o:W3C` 写 stdout 会挂住（204 行时进程不退、空转、CPU 0.1%） | W3C 的 stdout 模式有死锁；同一条查询加 `INTO 'C:\Temp\x.log'` 落地就秒回。`Engine` 对 W3C 自动插 `INTO` 写临时文件再读回来（`InsertIntoClause`），实测 204 行 0.42 秒 |
| `-o:CSV` 输出的表头带引号、字段含逗号 | 走完整 RFC4180 解析（`ParseCsv`），不要按逗号 `Split`；统计块用 `Statistics:` / `统计:` 截断 |
| `SplitContainer` 改 `SplitterDistance` 后 `TabPage` 不跟着缩（`Panel1=430` 而 `TabPage=908`） | .NET Framework 的 `TabControl` 只在切页时更新 `TabPage.Bounds`。`SyncTabPageBounds()` 在 `_tabs.Resize` / `Shown` 里把每个页面的 `Bounds` 强制设为 `_tabs.DisplayRectangle`，否则页面右侧内容被标签区裁掉 |
| `TableLayoutPanel` 里的 `ComboBox` 默认 `AutoSize=true` 会按最长条目撑宽 | 在 `Dock=Fill` 的表格单元格里这会直接把容器顶宽（实测 826~888px，左栏出横向滚动条）；`AutoSize=false` + `Dock=Fill` + `DropDownWidth` 才是正解 |
| `_smoke.exe` 测出「20 多秒」，而 LogParser 本身只花 0.4s | 冒烟框架原先用手写 `Application.DoEvents()` + `Thread.Sleep` 泵消息，`await` 之后的续体掉到线程池（实测 `线程=5 跨线程=True 同步上下文=null`），从非 UI 线程碰 `RichTextBox` 每次要等约 1 秒，白等出 18 秒假耗时。改成 `Application.Run(f)` + `Shown → BeginInvoke → async void BodyAsync` 后 `Run.Wall` 立刻变成 0.5s |

事件模板的 `Strings` 下标强依赖模板（如 7045 是 `服务名|映像路径|服务类型|启动类型|账户`）。
`queries.txt` 里保留了一条「原始 Strings 导出」，先跑它确认下标再写 `EXTRACT_TOKEN`。

## 验收对照

| Brief 验收项 | 证据 |
| --- | --- |
| 1. 双击自动 UAC | manifest `requireAdministrator`；非管理员进程 `Process.Start(UseShellExecute=false)` 报「请求的操作需要提升」→ [uac-manifest-check.txt](docs/evidence/uac-manifest-check.txt) |
| 2. 可勾选多项 | 界面 26 个复选框，`_smoke.exe` 实测勾选 5 项跑通 → [smoke-output.txt](docs/evidence/smoke-output.txt) |
| 3. 按顺序执行多个查询 | `[*] 1/5 …` `[*] 5/5 …` 顺序输出 → [ui-results.txt](docs/evidence/ui-results.txt) |
| 4. 执行期间不冻结 | 冒烟过程中泵消息并采样状态栏，`UI stayed alive = True` |
| 5. 中文不乱码 | `OutputEncoding = CP936`，结果区中文正常 → [ui-after.png](docs/screenshots/ui-2-after.png) |
| 6. 单项失败有标记且不影响后续 | `[!] 查询失败：标签` + 原因 + SQL，后一项照常执行；自检第 6/7 项覆盖 |
| 7. 拷到别的机器即用 | 只依赖 .NET Framework + 同目录 `LogParser.exe`；打包版是自解压单文件，解包后 5 个文件哈希与 `dist` 一致 |
| 8. 加检测项只改配置 | `queries.txt` 动态生成界面，`MainForm` 无任何事件号硬编码；自检 [1b] 保证文件与内置默认值同步 |
| 9. 可识别 PsExec / 爆破 / 建号 / 清日志 | 内置 4624/4625/4720/4728/7045/4698/1102/Defender 等 26 项 |

追加需求（用户反馈后的修正）：

| 反馈 | 处理 | 证据 |
| --- | --- | --- |
| 顶部 LogParser 路径在最大化时重叠、看不到自选路径入口 | 顶部改 3 列 `TableLayoutPanel`（标签/输入框填充/`浏览…`+`测试`），输入框随窗口拉伸，按钮永不重叠 | [ui-maximized.png](docs/screenshots/ui-3-maximized.png) |
| 找不到「开始」按钮 | 按钮条从右栏挪到窗口底部通栏（`▶ 开始分析（F5）` 200×38 主色按钮 + 停止 / 清空结果 / 导出结果 + 右侧「当前已选 N 个检测项」），任何尺寸都固定在底部 | [ui-before.png](docs/screenshots/ui-1-before.png) |
| 要时间筛选 | 顶部新增筛选条：`按时间段筛选` + 快捷（1h/24h/3d/7d/30d/自定义）+ 从/到；`InjectTimeFilter` 把 `TimeGenerated >= / <= TO_TIMESTAMP(...)` 插进 WHERE（跳过字符串字面量、插在 GROUP/ORDER 前） | 自检 `[9] 全量=204 行 / 2026-10-20..10-31=25 行`；冒烟 `--hours 48` 下 7045 命中 4 行 → [smoke-output.txt](docs/evidence/smoke-output.txt) |
| 左栏「检测项 + 全选/全不选/默认项」压字、时间框只显示到「时」 | 左栏 header 改 `FlowLayoutPanel`（`WrapContents=true`、`AutoSize`），宽度不够自动折行；时间框与快捷下拉宽度改为按 `TextRenderer.MeasureText` 实测（`2099-12-31 23:59:59` 全宽 + 34），高 DPI 下不再裁字；新增「此刻」按钮 | 冒烟 `Fit.Picker width=160 need=154 ok=True`、`Fit.Combo width=128 need=108 ok=True`、`Fit.Header children=5 overlaps=0 ok=True` → [smoke-output.txt](docs/evidence/smoke-output.txt) |
| 导出要 xlsx / csv | 「导出结果」弹三选一：文本 txt / CSV（每项一个文件 + `00_概述.csv`）/ Excel xlsx（一个工作簿，每项一个工作表，首行冻结、列宽自适应）；两种结构化格式都用 `-o:CSV` 重新查询后写盘，不在 NAT 文本上切列 | 自检 `[10] CSV 行数=205 列数=6`；冒烟 `--export-xlsx` 产出 4049 B 工作簿、`--export-csv` 产出 2 个文件；LibreOffice 无提示打开并转出三张表（`概述` / `服务安装 (7045)` / `系统日志清除 (104)`） |
| 时间想精确到分秒 | 日期框（`yyyy-MM-dd`）+ 「时」00~23 下拉 + 「分」00~59 下拉，分列成三段，第一次上手也一眼看得出能调；「此刻」一键设为最近 1 小时；上界补到该分钟第 59 秒（选「到 15:45」包含 `15:45:30` 的记录） | 冒烟 `Fit.Combo "15"/"46" width=66 need=52 ok=True`；`时间范围 : … <= TO_TIMESTAMP('2026-11-05 15:46:59', …)` → [smoke-output.txt](docs/evidence/smoke-output.txt) |
| 下拉里五种输出格式只有 NAT/CSV 能用 | 根因是 `-rtp:-1` 只有 NAT 认，换了格式就 `invalid parameter "rtp"`；`BuildArguments` 现在对非 NAT 自动剥离。另外补了两处只影响新格式的解码：XML 按 UTF-16 读、在 `</ROOT>` 截断；W3C 的 stdout 会死锁，改用 `INTO` 落临时文件再读回。命中行数按格式分别统计 | 自检 `[11]` 五种格式各 `ok=True 行数=2`；冒烟 `--format CSV/XML/W3C/TSV` 各 `命中 204 行` → [smoke-output.txt](docs/evidence/smoke-output.txt)、截图 [ui-format-xml.png](docs/screenshots/ui-format-xml.png) |
| XML 格式下没命中时打出乱码汉字，而不是「(该检测项在当前日志中没有匹配记录)」 | 没命中行时 LogParser 不写 XML，stdout 只剩 ANSI Statistics 块，按 UTF-16 解码成乱码；`CleanStandardOutput` 现在找不到 `</ROOT>` 就判空并写占位，空 stdout 也补占位。五种格式行为统一 | 自检 `[11]` 新增 10 条「空结果 + 无乱码」断言全 PASS；冒烟 `--smoke . system_log_cleared --format XML` 输出占位文本 → [smoke-output.txt](docs/evidence/smoke-output.txt)、[ui-xml-empty.png](docs/evidence/ui-xml-empty.png) |
| 导出的 CSV 目录里会少项（查询失败/无命中的项被静默跳过） | 原规则「只有表头的不落盘」会把失败项和空结果项一起丢掉；改成只跳过完全空表，失败项写 `[!] 查询失败` + 原因、空结果项写占位文本，导出的目录和界面上的项数就一致了。同时补上「目标目录不存在就建」 | 冒烟 `--export-csv` 现在落 6 个文件（`00_概述` + 5 项，含两项权限失败的 `[!] 查询失败`） |
| 检测项不够用，想临时手写 LogParser 命令查 | 左栏新增第三页「手动命令」：只写 SQL / 整条命令行 / 参数+SQL 三种写法都认，Ctrl+Enter 执行；命令行里的 `-i:` `-o:` 覆盖「参数与超时」页（`EffectiveFormat` 决定解码，`-o:XML` 不会再按 GBK 读成乱码）；「套用上方的时间筛选」控制是否注入 `TimeGenerated` 条件；最近 10 条命令存 `%LOCALAPPDATA%\LogSentry\manual-history.txt`，一行一条可点选回填 | 自检 `[12]` 八条断言全 PASS（含真实跑 7045 `ok=True 行数=3`）；冒烟 `--manual '<命令行>'` → `ManualParse = 输入格式 EVT · 输出格式 NAT · 参数 -i:EVT -o:NAT -rtp:-1`、`Result.Ok=1` |
| 手动命令页的历史下拉把左栏顶出横向滚动条、命令框右侧被裁 | 两处根因：①`_cboHistory` 用默认 `AutoSize=true` 会按最长条目撑到 826~888px，改成 `AutoSize=false` + `Dock=Fill` + `DropDownWidth=520`，闭合状态按设计截断到 58 字符；②`.NET Framework` 的 `TabControl` 不跟着 `SplitContainer` 收缩（`Panel1` 从 936 收到 430 后 TabPage 还停在 908），新增 `SyncTabPageBounds()` 在 `_tabs.Resize` 与 `Shown` 里把页面尺寸同步回 `DisplayRectangle` | 冒烟 `Fit.Manual pageW=402 maxRight=118 overflow=0 txtH=198 ok=True`；父链 `ComboBox(cboHistory)=382 → TableLayoutPanel=382 → Panel=382 → TabPage=402 → TabControl=410 → Panel=430`（修前是 888/908/410 三档错位） |
| 感觉比之前慢了很多 | 两处一起改：①结果区渲染从「逐行 `AppendText` + 每行 `ScrollToCaret`」改成「按样式攒进 `StringBuilder`，120ms 节拍统一写一次，滚动每拍最多一次」，`ScrollToCaret` 由逐行累计 10.8s 降到整轮 2ms；②更要紧的是自测框架用 `DoEvents` 泵消息，`await` 续体掉到线程池，跨线程写 RichTextBox 每次等 1 秒，之前记录的「20 多秒」是量具错，不是程序慢。`_smoke.exe` 现在跑在 `Application.Run` 的真实消息循环里 | 冒烟 `service_install,logon_success` → `Run.Wall = 0.5s`、`Perf.AppendMs = 8 ms / 17 次`、结果文件 `总耗时：0.48s`（旧记录 23.17s）；3000 行重负载 → `Run.Wall = 1.2s`、`Perf.AppendMs = 39 ms`、单次 420591 字符只花 5ms → [smoke-output.txt](docs/evidence/smoke-output.txt)、[smoke-heavy-output.txt](docs/evidence/smoke-heavy-output.txt) |
| 开始分析后右栏一片白，要等一会才有内容 | 以前为了省重绘在开跑时把结果区 `Visible=false`，整轮结束才显示——用户看到的就是白屏，而且末项结果必须等全部跑完。现在改成边跑边写：结果区始终可见，点完按钮立刻先写一行「(正在执行…) 正在启动 LogParser」，随后每项写完就落地并滚到底。样式切换时把插入点移到本行行首（零长度选区）再刷，既不会反白旧行，也不会因为要「清选区」而再去改可见性 | 冒烟 `service_install,logon_success` → `Run.Wall = 0.5s`、渲染 `31 ms / 18 次`、最慢一次写入 `18 ms`；3000 行重负载 → `Run.Wall = 1.1s`、渲染 `49 ms`、单次 420359 字符只花 29ms；`ui-2-after.png` 里第一行就是「(正在执行…)」随后是分隔标题 → [smoke-output.txt](docs/evidence/smoke-output.txt) |

一键复检：

```powershell
cd dist
.\_selftest.exe                     # 期望最后一行：=== 全部通过 ===
.\_smoke.exe --smoke . logon_success,logon_fail,service_install   # 期望：RESULT = OK
.\_smoke.exe --smoke . service_install --hours 24 --max           # 时间筛选 + 最大化截图
.\_smoke.exe --smoke . service_install --export-xlsx out.xlsx --export-csv <目录>  # 导出 CSV/XLSX 并校验
.\_smoke.exe --smoke . service_install --format XML               # 换输出格式（NAT/CSV/TSV/XML/W3C）
.\_smoke.exe --smoke . system_log_cleared --format XML            # 空结果 + XML：应显示「(该检测项在当前日志中没有匹配记录)」
.\_smoke.exe --smoke . "" --max --manual '<命令行>'               # 手动命令页：解析回显 + 执行 + 截图
```

非管理员会话下 Security 类检测项必然报权限不足，那是预期行为，不是故障——
`_smoke.exe` 会把它汇报成 `RESULT = OK(部分项按预期失败)`。