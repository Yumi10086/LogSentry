## LogSentry v1.0 — Windows 日志一把梭分析

便携式 Windows 日志痕迹分析工具。勾选多个检测项 → 一键顺序执行 → 同一窗口对比
「攻击失败痕迹」和「攻击成功痕迹」，快速判断爆破是否得手、账户是否被创建、服务是否被安装。

底层调用微软 Log Parser 2.2，**不需要 Python / Node / 任何第三方运行时**，
Win10 / Win11 自带 .NET Framework 4.8，双击即用，自动 UAC 提权。

### 下载

| 文件 | 说明 | 大小 | SHA256 |
| --- | --- | --- | --- |
| `LogSentry_v1.0.zip` | **推荐**：绿色包，5 文件（`LogSentry.exe` / `LogParser.exe` / `queries.txt` / `LogParser.chm` / `使用说明.txt`），解压即用，机器上已有 .NET Framework 4.5+ 就够 | 1,089,732 B | `2256219CAADDE327984BD05FBEAE107D7CA7F39F46A91E4DA48F7F96748792A6` |
| `LogSentry_v1.0_full.zip` | 完整包，6 文件，多带一个 `.NET Framework 4.8` 离线安装包 `NDP48-x86-x64-AllOS-ENU.exe`（Win7 未打补丁 / 无 .NET 现场用） | 122,370,814 B | `8349729B17B46ADC1D6BAD3EFF5C5E28D3C0AC735ED5DC52470C07003C4F0655` |

SHA256 校验：

```powershell
Get-FileHash .\LogSentry_v1.0.zip      -Algorithm SHA256
Get-FileHash .\LogSentry_v1.0_full.zip -Algorithm SHA256
```

不再提供单文件 exe：116.7 MB 超过 GitHub 100 MB 上限，且自解压包只是弹个「Extract to:」对话框，省不下任何步骤。

### 内置检测项（26 项，可自由勾选）

- **登录与爆破**：登录成功 (4624)、登录失败 (4625)、显式凭据登录 (4648)、特殊登录 (4672)
- **账户变更**：新建账户 (4720)、启用账户 (4722)、组成员变更 (4728 / 4732 / 4756)、账户锁定 (4740)
- **横向与持久化**：服务安装 (7045，PsExec 典型痕迹)、计划任务创建 (4698)、驱动加载、Sysmon 进程创建 (EventID 1)
- **日志与防护**：日志清除 (Security 1102 / System 104)、Defender 状态变更、审计策略修改
- **原始 Strings 导出**：用于自己核对事件模板字段下标，再写 `EXTRACT_TOKEN`

### 本次发布包含

- 时间筛选精确到分秒，含「最近 1h / 24h / 3d / 7d / 30d / 自定义」与「此刻」快选
- 手动命令页：直接手写 SQL 或整条 LogParser 命令行，Ctrl+Enter 执行，最近 10 条自动留存
- 导出三种格式：文本 txt / CSV（每项一个文件 + 概述）/ Excel xlsx（每项一个工作表）
- 五种输出格式全部可用：NAT / CSV / TSV / XML / W3C（XML 与 W3C 的编码、死锁问题已处理）
- 结果区流式渲染，执行期间界面不冻结，每项完成即落地并滚动到底
- 配置驱动：新增检测项只改 `queries.txt`，不改代码、不用重编译

### 使用说明

1. 下载 `LogSentry_v1.0.zip`（或 `_full.zip`），右键 →「全部解压缩」（解压交给 Windows 资源管理器，不需要任何运行库）
2. 进入刚解出来的 `LogSentry\` 文件夹，双击 `LogSentry.exe` → UAC 确认 → 界面打开
   （若弹「需要 .NET Framework 4.5 或更高」，先双击同目录的 `NDP48-x86-x64-AllOS-ENU.exe`（.NET Framework 4.8），装完重启一次。只装 4.0 不够：主程序用了 async/await 与 ZipArchive，会报 `TypeLoadException: Could not load type 'System.Runtime.CompilerServices.IAsyncStateMachine'`。Win7 可能要先打 SHA-2 补丁 KB4474419 / KB4490628）
3. 左栏勾选检测项，例如同时勾上「登录失败 (4625)」与「登录成功 (4624)」
4. 点 `▶ 开始分析（F5）`，结果区按项分隔显示，末尾给出成功 / 失败 / 命中汇总
5. 需要留档点「导出结果」，选 xlsx 或 csv

完整说明见仓库内 `README.md` 与 `使用说明.txt`；实测坑位与验证记录见 `docs/DEVELOPMENT.md`。

### 声明

本仓库仅包含自研代码与微软官方免费分发的 Log Parser 2.2 原始二进制（未做修改）。
`LogParser.exe` / `LogParser.chm` 版权归 Microsoft Corporation 所有，按其随附许可使用。
