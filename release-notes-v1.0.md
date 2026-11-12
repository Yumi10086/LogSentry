## LogSentry v1.0 — Windows 日志一把梭分析

便携式 Windows 日志痕迹分析工具。勾选多个检测项 → 一键顺序执行 → 同一窗口对比
「攻击失败痕迹」和「攻击成功痕迹」，快速判断爆破是否得手、账户是否被创建、服务是否被安装。

底层调用微软 Log Parser 2.2，**不需要 Python / Node / 任何第三方运行时**，
Win10 / Win11 自带 .NET Framework 4.8，双击即用，自动 UAC 提权。

### 下载

| 文件 | 说明 | 大小 | SHA256 |
| --- | --- | --- | --- |
| `LogSentry_Portable.exe` | **推荐**：单文件便携版（7-Zip SFX = `7z.sfx` + 配置 + `payload.7z`，含 .NET Framework 4 离线安装包；解压器是 7-Zip 原生模块，不依赖任何 .NET） | 51478755 B | `12197F80283527C982D981960DEF063D0EB67A295BBEB2091D964A9F30FE8D91` |
| `LogSentry_v1.0.zip` | 绿色文件夹版全套（含 `.NET Framework 4` 离线安装包），解压后用 `LogSentry.exe` | 51473613 B | `2DBC6EF23DEFF8629074BD1E9C34F1FC429EFA20637D9BECAC37F073B19BF67D` |

单文件 SHA256 校验：

```powershell
Get-FileHash .\LogSentry_Portable.exe -Algorithm SHA256
```

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

1. 下载 `LogSentry_Portable.exe`，双击 → 在 7-Zip 的「Extract to:」里选目录（建议 `D:\LogSentry`）→ `Extract`
2. 进入刚解出来的 `LogSentry\` 文件夹，双击 `LogSentry.exe` → UAC 确认 → 界面打开
   （若弹「需要 .NET Framework v4.0.30319」，先双击同目录的 `dotNetFx40_Full_x86_x64.exe`，装完重启一次）
3. 左栏勾选检测项，例如同时勾上「登录失败 (4625)」与「登录成功 (4624)」
4. 点 `▶ 开始分析（F5）`，结果区按项分隔显示，末尾给出成功 / 失败 / 命中汇总
5. 需要留档点「导出结果」，选 xlsx 或 csv

完整说明见仓库内 `README.md` 与 `使用说明.txt`；实测坑位与验证记录见 `docs/DEVELOPMENT.md`。

### 声明

本仓库仅包含自研代码与微软官方免费分发的 Log Parser 2.2 原始二进制（未做修改）。
`LogParser.exe` / `LogParser.chm` 版权归 Microsoft Corporation 所有，按其随附许可使用。
