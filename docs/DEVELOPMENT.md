# 开发与验证记录

本文件收录面向维护者的内部记录：LogParser 2.2 的实测坑、验收对照表、历史反馈逐条处理、
自检/冒烟的内部断言细节、复检命令。对外使用文档见 [README.md](../README.md)，
终端用户手册见 `dist/使用说明.txt`。

---

## 1. LogParser 2.2 的坑（实测记录）

执行引擎的每一处特殊处理都对应下面某一行，动 `Engine.cs` 前先读这张表。

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

事件模板的 `Strings` 下标强依赖模板（如 7045 是 `服务名|映像路径|服务类型|启动类型|账户`）。
`queries.txt` 里保留了一条「原始 Strings 导出」，先跑它确认下标再写 `EXTRACT_TOKEN`。

## 2. 界面框架层的坑（.NET Framework WinForms）

| 现象 | 结论 |
| --- | --- |
| `SplitContainer` 改 `SplitterDistance` 后 `TabPage` 不跟着缩（`Panel1=430` 而 `TabPage=908`） | .NET Framework 的 `TabControl` 只在切页时更新 `TabPage.Bounds`。`SyncTabPageBounds()` 在 `_tabs.Resize` / `Shown` 里把每个页面的 `Bounds` 强制设为 `_tabs.DisplayRectangle`，否则页面右侧内容被标签区裁掉 |
| `TableLayoutPanel` 里的 `ComboBox` 默认 `AutoSize=true` 会按最长条目撑宽 | 在 `Dock=Fill` 的表格单元格里这会直接把容器顶宽（实测 826~888px，左栏出横向滚动条）；`AutoSize=false` + `Dock=Fill` + `DropDownWidth` 才是正解 |
| `_smoke.exe` 测出「20 多秒」，而 LogParser 本身只花 0.4s | 冒烟框架原先用手写 `Application.DoEvents()` + `Thread.Sleep` 泵消息，`await` 之后的续体掉到线程池（实测 `线程=5 跨线程=True 同步上下文=null`），从非 UI 线程碰 `RichTextBox` 每次要等约 1 秒，白等出 18 秒假耗时。改成 `Application.Run(f)` + `Shown → BeginInvoke → async void BodyAsync` 后 `Run.Wall` 立刻变成 0.5s |
| 大输出时逐行 `AppendText` + 逐行 `ScrollToCaret` 极慢 | `WordWrap=false` + `ScrollBars=Both` 下每次写入都要重算横向滚动范围（实测单次 200ms 级）。改法：按样式攒进 `StringBuilder`，节拍刷一次，滚动每拍最多一次 |

## 3. 验收对照（原始 Brief 九项）

| Brief 验收项 | 证据 |
| --- | --- |
| 1. 双击自动 UAC | manifest `requireAdministrator`；非管理员进程 `Process.Start(UseShellExecute=false)` 报「请求的操作需要提升」→ [uac-manifest-check.txt](evidence/uac-manifest-check.txt) |
| 2. 可勾选多项 | 界面 26 个复选框，`_smoke.exe` 实测勾选 5 项跑通 → [smoke-output.txt](evidence/smoke-output.txt) |
| 3. 按顺序执行多个查询 | `[*] 1/2 …` `[*] 2/2 …` 顺序输出 → [smoke-head.txt](evidence/smoke-head.txt) |
| 4. 执行期间不冻结 | 冒烟过程中泵消息并采样状态栏，`UI stayed alive = True` |
| 5. 中文不乱码 | `OutputEncoding = CP936`，结果区中文正常 → [ui-2-after.png](screenshots/ui-2-after.png) |
| 6. 单项失败有标记且不影响后续 | `[!] 查询失败：标签` + 原因 + SQL，后一项照常执行；自检第 6/7 项覆盖 |
| 7. 拷到别的机器即用 | 只依赖 .NET Framework + 同目录 `LogParser.exe`；打包版是自解压单文件，解包后 5 个文件哈希与 `dist` 一致 |
| 8. 加检测项只改配置 | `queries.txt` 动态生成界面，`MainForm` 无任何事件号硬编码；自检 [1b] 保证文件与内置默认值同步 |
| 9. 可识别 PsExec / 爆破 / 建号 / 清日志 | 内置 4624/4625/4720/4728/7045/4698/1102/Defender 等 26 项 |

## 4. 迭代记录（每轮反馈 → 处理 → 证据）

| 反馈 | 处理 | 证据 |
| --- | --- | --- |
| 顶部 LogParser 路径在最大化时重叠、看不到自选路径入口 | 顶部改 3 列 `TableLayoutPanel`（标签/输入框填充/`浏览…`+`测试`），输入框随窗口拉伸，按钮永不重叠 | [ui-3-maximized.png](screenshots/ui-3-maximized.png) |
| 找不到「开始」按钮 | 按钮条从右栏挪到窗口底部通栏（`▶ 开始分析（F5）` 200×38 主色按钮 + 停止 / 清空结果 / 导出结果 + 右侧「当前已选 N 个检测项」），任何尺寸都固定在底部 | [ui-1-before.png](screenshots/ui-1-before.png) |
| 要时间筛选 | 顶部新增筛选条：`按时间段筛选` + 快捷（1h/24h/3d/7d/30d/自定义）+ 从/到；`InjectTimeFilter` 把 `TimeGenerated >= / <= TO_TIMESTAMP(...)` 插进 WHERE（跳过字符串字面量、插在 GROUP/ORDER 前） | 自检 `[9] 全量=204 行 / 2026-10-20..10-31=25 行`；冒烟 `--hours 48` 下 7045 命中 4 行 → [smoke-output.txt](evidence/smoke-output.txt) |
| 左栏「检测项 + 全选/全不选/默认项」压字、时间框只显示到「时」 | 左栏 header 改 `FlowLayoutPanel`（`WrapContents=true`、`AutoSize`），宽度不够自动折行；时间框与快捷下拉宽度改为按 `TextRenderer.MeasureText` 实测（`2099-12-31 23:59:59` 全宽 + 34），高 DPI 下不再裁字；新增「此刻」按钮 | 冒烟 `Fit.Picker width=160 need=154 ok=True`、`Fit.Combo width=128 need=108 ok=True`、`Fit.Header children=5 overlaps=0 ok=True` |
| 导出要 xlsx / csv | 「导出结果」弹三选一：文本 txt / CSV（每项一个文件 + `00_概述.csv`）/ Excel xlsx（一个工作簿，每项一个工作表，首行冻结、列宽自适应）；两种结构化格式都用 `-o:CSV` 重新查询后写盘，不在 NAT 文本上切列 | 自检 `[10] CSV 行数=205 列数=6`；冒烟 `--export-xlsx` 产出 4049 B 工作簿、`--export-csv` 产出 2 个文件；LibreOffice 无提示打开并转出三张表（`概述` / `服务安装 (7045)` / `系统日志清除 (104)`） |
| 时间想精确到分秒 | 日期框（`yyyy-MM-dd`）+ 「时」00~23 下拉 + 「分」00~59 下拉，分列成三段；「此刻」一键设为最近 1 小时；上界补到该分钟第 59 秒（选「到 15:45」包含 `15:45:30` 的记录） | 冒烟 `Fit.Combo "15"/"46" width=66 need=52 ok=True`；`时间范围 : … <= TO_TIMESTAMP('2026-11-05 15:46:59', …)` |
| 下拉里五种输出格式只有 NAT/CSV 能用 | 根因是 `-rtp:-1` 只有 NAT 认，换了格式就 `invalid parameter "rtp"`；`BuildArguments` 现在对非 NAT 自动剥离。另外补了两处只影响新格式的解码：XML 按 UTF-16 读、在 `</ROOT>` 截断；W3C 的 stdout 会死锁，改用 `INTO` 落临时文件再读回。命中行数按格式分别统计 | 自检 `[11]` 五种格式各 `ok=True 行数=2`；冒烟 `--format CSV/XML/W3C/TSV` 各 `命中 204 行` → [ui-format-xml.png](screenshots/ui-format-xml.png) |
| XML 格式下没命中时打出乱码汉字，而不是「(该检测项在当前日志中没有匹配记录)」 | 没命中行时 LogParser 不写 XML，stdout 只剩 ANSI Statistics 块，按 UTF-16 解码成乱码；`CleanStandardOutput` 现在找不到 `</ROOT>` 就判空并写占位，空 stdout 也补占位。五种格式行为统一 | 自检 `[11]` 新增 10 条「空结果 + 无乱码」断言全 PASS；冒烟 `--format XML` 输出占位文本 → [ui-xml-empty.png](evidence/ui-xml-empty.png) |
| 导出的 CSV 目录里会少项（查询失败/无命中的项被静默跳过） | 原规则「只有表头的不落盘」会把失败项和空结果项一起丢掉；改成只跳过完全空表，失败项写 `[!] 查询失败` + 原因、空结果项写占位文本。同时补上「目标目录不存在就建」 | 冒烟 `--export-csv` 现在落 6 个文件（`00_概述` + 5 项，含两项权限失败的 `[!] 查询失败`） |
| 检测项不够用，想临时手写 LogParser 命令查 | 左栏新增第三页「手动命令」：只写 SQL / 整条命令行 / 参数+SQL 三种写法都认，Ctrl+Enter 执行；命令行里的 `-i:` `-o:` 覆盖「参数与超时」页（`EffectiveFormat` 决定解码）；「套用上方的时间筛选」控制是否注入 `TimeGenerated` 条件；最近 10 条命令存 `%LOCALAPPDATA%\LogSentry\manual-history.txt` | 自检 `[12]` 八条断言全 PASS（含真实跑 7045 `ok=True 行数=3`）；冒烟 `--manual '<命令行>'` → `ManualParse = 输入格式 EVT · 输出格式 NAT · 参数 -i:EVT -o:NAT -rtp:-1` |
| 手动命令页的历史下拉把左栏顶出横向滚动条、命令框右侧被裁 | ①`_cboHistory` 默认 `AutoSize=true` 会撑到 826~888px，改成 `AutoSize=false` + `Dock=Fill` + `DropDownWidth=520`；②`TabControl` 不跟 `SplitContainer` 收缩，新增 `SyncTabPageBounds()` | 冒烟 `Fit.Manual pageW=402 maxRight=118 overflow=0 txtH=198 ok=True`；父链 `ComboBox=382 → TableLayoutPanel=382 → Panel=382 → TabPage=402 → TabControl=410 → Panel=430` |
| 感觉比之前慢了很多 | ①结果区渲染从「逐行 `AppendText` + 每行 `ScrollToCaret`」改成「按样式攒进 `StringBuilder`，节拍统一写一次」；②更要紧的是自测框架用 `DoEvents` 泵消息，`await` 续体掉到线程池，跨线程写 RichTextBox 每次等 1 秒，之前的「20 多秒」是量具错 | `Run.Wall = 0.5s`、`Perf.AppendMs = 8 ms / 17 次`（旧记录 23.17s）；3000 行重负载 `Run.Wall = 1.2s` → [smoke-heavy-output.txt](evidence/smoke-heavy-output.txt) |
| 开始分析后右栏一片白，要等一会才有内容 | 以前为省重绘在开跑时把结果区 `Visible=false`，整轮结束才显示。现在边跑边写：点完按钮先写「(正在执行…) 正在启动 LogParser」，每项写完就落地并滚到底；样式切换时把插入点移到本行行首（零长度选区）再刷，既不会反白旧行 | 冒烟 `Run.Wall = 0.5s`、最慢一次写入 `18 ms`；3000 行重负载单次 420359 字符只花 29ms → [smoke-output.txt](evidence/smoke-output.txt) |

## 5. 自检 `_selftest.exe` 的 12 组断言

1. 配置解析（26 项 / 0 警告 / id 唯一）
2. `queries.txt` 与 `BuiltinQueries` 文本一致（防两者跑偏）
3. 参数构造与引号转义
4. 逐项真实执行
5. Statistics 块裁剪与 `Elements processed` 解析
6. 空结果不算失败
7. 语法错误必须判失败
8. 权限不足的中文提示
9. 时间筛选：条件生成、WHERE 追加 / 补 WHERE / GROUP BY 前插入 / 字符串字面量里的 `ORDER` 不误判，以及同一查询加区间后命中数从 204 降到 25 的真实对比
10. CSV/XLSX 生成与回读
11. 五种输出格式与空结果占位
12. 手动命令命令行解析与真实执行

`_smoke.exe` 会 `new MainForm()` → `Application.Run` → 勾选指定 id → 跑完整队列（真实消息循环里
检测界面是否卡死）→ `PrintWindow` 截图 → 写 `smoke.log` / `ui-results.txt`。
之所以用 `PrintWindow`：`DrawToBitmap` 画不出 `RichTextBox` 的内容，`CopyFromScreen` 会被窗口位置和遮挡干扰。
可选参数：`--hours N`（打开「最近 N 小时」筛选，走与用户点界面相同的控件）、`--max`（最大化截图）、
`--tab2`（切到「参数与超时」页）、`--format X`（切换输出格式）、`--export-xlsx / --export-csv`（导出并校验）、
`--manual '<命令行>'`（走「手动命令」页执行）。

## 6. 复检命令

```powershell
cd dist
.\_selftest.exe                     # 期望最后一行：=== 全部通过 ===
.\_smoke.exe --smoke . logon_success,logon_fail,service_install   # 期望：RESULT = OK
.\_smoke.exe --smoke . service_install --hours 24 --max           # 时间筛选 + 最大化截图
.\_smoke.exe --smoke . service_install --export-xlsx out.xlsx --export-csv <目录>
.\_smoke.exe --smoke . service_install --format XML               # NAT/CSV/TSV/XML/W3C
.\_smoke.exe --smoke . system_log_cleared --format XML            # 空结果 + XML 占位
.\_smoke.exe --smoke . "" --max --manual '<命令行>'               # 手动命令页
```

非管理员会话下 Security 类检测项必然报权限不足，那是预期行为，不是故障——
`_smoke.exe` 会把它汇报成 `RESULT = OK(部分项按预期失败)`。

## 7. 打包

`pack-sfx.ps1` 走「7-Zip SFX」：

1. 把 `LogSentry.exe / LogParser.exe / LogParser.chm / queries.txt / 使用说明.txt` 以及
   `dotNetFx40_Full_x86_x64.exe` 放进 `.pkg/stage/LogSentry/`（一个 `LogSentry\` 子目录，
   解压后就是一个文件夹，不会把 50 MB 的文件散到目标目录根上）。
2. 用仓库自带的 `.tools/extra/7za.exe` 打包：`7za a -t7z -mx=5 -mhc=on .pkg\payload.7z LogSentry`
   （installer 本身已是 cab 压缩，`-mx=9` 只省 2–3 MB 却慢很多）。
3. `config.txt` = `;!@Install@!UTF-8!` + `sfx-config.txt` 正文（过滤 `;` 注释行）+
   `;!@InstallEnd@!`，**UTF-8 无 BOM**（脚本里有 BOM 校验，有 BOM 直接 throw）。
4. 三段字节拼接：`7z.sfx` + `config.txt` + `payload.7z` → `dist/LogSentry_Portable.exe`。

产物结构（`dist/LogSentry_Portable.exe`，51,478,763 B 时核对过）：
前 214,528 B 与 `.tools/sfx/7z.sfx` 逐字节相同；偏移 214,528 起是 `;!@Install@!UTF-8!` 开头的
155 B 配置；紧接着 `37 7A BC AF 27 1C`（7z 魔数）。

**行为（实测，别期待自动启动）**：双击后弹的是 7-Zip 自己的对话框
`7-Zip self-extracting archive / Extract to: <路径> [Extract][Cancel]`，用户选目录、点 Extract，
再自己去 `LogSentry\` 里双击 `LogSentry.exe`。官方 GUI 模块不解析配置段，`GUIMode / Directory /
RunProgram` 全被忽略（实测：旧产物双击后停在 Extract 对话框，轮询 10 秒既没有 `7zS*.tmp\LogSentry\`
被创建，也没有主程序进程被拉起）。

**包内不管 .NET 4**：`dotNetFx40_Full_x86_x64.exe` 就是载荷里的一个普通文件，和 `使用说明.txt`
一样被解出来；缺运行库时用户自己双击安装。不检测、不弹询问框、不代跑安装程序。

### 2026-11-12 的三次改道（按顺序，别再绕回去）

1. **自写托管外壳（CLR 4）**：`src/Launcher.cs` 用 `v4.0.30319\csc.exe` 编译 —— 目标机缺 .NET 4 时
   mscoree 在进 `Main()` 之前就弹「初始化错误」，包里的 50 MB 安装包永远解不出来，形成死锁。
2. **自写托管外壳（CLR 2.0）**：改成 C# 2.0 + 手写 zip 解压（`DeflateStream`），Win7 的 3.5.1 能跑，
   Win10/11 却默认没开 3.5 —— 于是换成"原生壳"的讨论。
3. **最终回到 7-Zip SFX**（用户决定）：原生 PE、任何 Windows 都能解压，代价是手动点一次 Extract。
   自写外壳与 `pack-portable.ps1` 已删除（`git show 99c4853:src/Launcher.cs` 可找回）。

> 判断 CLR 目标别用 `IMAGE_COR20_HEADER.MajorRuntimeVersion`：.NET 2.0 和 4.x 程序集这个值都是 2
> （Minor 都是 5）。要看 `ImageRuntimeVersion` / mscorlib 的 AssemblyRef 版本，且只能用
> Windows PowerShell 5.1 的 `ReflectionOnlyLoadFrom`（PS 7 的 .NET 8 加载不了 2.0 程序集）。

### 为什么没有自动启动（实测结论，别再试官方模块）

- 官方 `7z.sfx / 7zS.sfx / 7zCon.sfx` 都是**纯解压模块**：二进制里扫不到 `RunProgram` /
  `InstallPath` / `Directory` 任何配置键（ASCII 与 UTF-16 都扫过）。仓库里 `.tools/sfx/7z.sfx` 与
  `7zS.sfx` 的 MD5 都是 `E48CBBC210990C29F21D0EC949A8B5E6` —— 就是同一个 GUI 模块。
- 拼包实测：`7zS.sfx` + `InstallPath` + `RunProgram="cmd.exe /c echo X > %TEMP%\sfx_main.txt"`
  → `exit = 0`，标记文件与目标目录都不存在；`7z.sfx` 用 `Directory=".\out\"` 同样没有
  `out_marker.txt`；`7zCon.sfx` 只认命令行 `-o`。即：**解压完就退出，不会启动主程序**。
- 带安装脚本能力的是第三方改造版 `7zSD.sfx`（sFxSetup），官方 `7z2501-extra.7z` 里只有
  `7za.exe / 7za.dll / 7zxa.dll / Far/*`，没有它。
- 若哪天要自动启动：把 `7z.sfx` 换成 `7zSD.sfx` 即可，`sfx-config.txt` 里的
  `GUIMode / Directory / RunProgram` 一行都不用改。

### 载荷校验

`Expand-Archive` / 任意 zip 工具解包 `.pkg/payload.zip` 后，逐文件 SHA256 与 `dist/` 对比应为 MATCH。
当前正式包：`dist/LogSentry_Portable.exe` 51468733 B，
SHA256 `A2794F3630E4531E15C387FB19AC21861121BCF6D8F2B3D36C4D166D59D61972`。

## 8. 源码维护注意

- 编译器是系统自带的 `csc.exe`（C# 5）：`$"..."`、`nameof`、`?.`、插值字符串一律不可用。
- `MainForm.cs` 体量大且是全代码构建 UI，改结构体（`try/finally`、`for` 块）时按内容一次性整块替换，
  不要用 `IndexOf` + 逐条 `RemoveAt` —— 会命中同名行把别的块删掉，产生 `CS1518/CS1031/CS1524`。
- 每次改完立刻 `.\build.ps1` + `.\dist\_selftest.exe`，再跑一次 `_smoke.exe --hours 240`。
