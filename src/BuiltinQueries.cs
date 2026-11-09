using System;
using System.Text;

namespace LogSentry
{
    /// <summary>
    /// 内嵌默认配置（首次运行时若同目录没有 queries.txt 会自动落盘，之后以文件为准）。
    /// 字段索引基于 Server 2008R2 ~ Windows 11 的简体中文事件模板实测；
    /// 换语言或换模型时，可先用“原始 Strings 导出”项核对索引再改本文件。
    /// </summary>
    internal static class BuiltinQueries
    {
        public const string Text = @"# =====================================================================
# LogSentry 检测项配置（配置驱动：改这里即改界面，不用改程序）
# ---------------------------------------------------------------------
# 语法：
#   [id]            一个检测项的开始，id 全局唯一，仅用于标识
#   label:  xx      复选框显示文字（必填）
#   group:  xx      分组标题，同 group 的项会归到一个小节（默认：通用）
#   log:    xx     日志源：Security / System / Application / 自定义
#   query:  xx     LogParser SQL（必填，整行一直到行尾）
#   note:   xx      可选备注，显示在复选框右侧
#   default: true   是否默认勾选
#
#   以 # 或 ; 开头的行是注释；空行忽略。
#   query 中可写 %PARAM%，执行时被界面“自定义参数”填写的值替换。
#   文件编码 UTF-8 / UTF-16 / GBK 均可自动识别。
#
# 注意：EXTRACT_TOKEN(Strings, N, '|') 的 N 强依赖事件模板顺序。
#       想核对某事件号的字段排布，勾选最后一项“原始 Strings 导出”即可原样看到。
# =====================================================================

[logon_success]
label: 登录成功 (4624)
group: 一、登录与认证
log: Security
default: true
note: 用于对照爆破是否成功
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 5, '|') AS 目标用户, EXTRACT_TOKEN(Strings, 8, '|') AS 登录类型, EXTRACT_TOKEN(Strings, 18, '|') AS 来源IP, EXTRACT_TOKEN(Strings, 9, '|') AS 认证包 FROM Security WHERE EventID = 4624

[logon_fail]
label: 登录失败 (4625)
group: 一、登录与认证
log: Security
default: true
note: 爆破 / 口令喷洒
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 5, '|') AS 目标用户, EXTRACT_TOKEN(Strings, 7, '|') AS 状态码, EXTRACT_TOKEN(Strings, 8, '|') AS 子状态码, EXTRACT_TOKEN(Strings, 10, '|') AS 登录类型, EXTRACT_TOKEN(Strings, 19, '|') AS 来源IP, EXTRACT_TOKEN(Strings, 20, '|') AS 来源工作站 FROM Security WHERE EventID = 4625

[logon_explicit]
label: 显式凭据登录 (4648)
group: 一、登录与认证
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 1, '|') AS 用户, EXTRACT_TOKEN(Strings, 5, '|') AS 目标用户, EXTRACT_TOKEN(Strings, 8, '|') AS 目标主机, EXTRACT_TOKEN(Strings, 10, '|') AS 目标服务器 FROM Security WHERE EventID = 4648

[logon_network_cleartext]
label: 明文凭据登录 (4624 类型8)
group: 一、登录与认证
log: Security
note: 局域网嗅探风险
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 5, '|') AS 目标用户, EXTRACT_TOKEN(Strings, 18, '|') AS 来源IP, EXTRACT_TOKEN(Strings, 19, '|') AS 来源工作站 FROM Security WHERE EventID = 4624 AND EXTRACT_TOKEN(Strings, 8, '|') = '8'

[session_disconnect]
label: 会话断开 (4779)
group: 一、登录与认证
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 1, '|') AS 账户, EXTRACT_TOKEN(Strings, 2, '|') AS 会话名, EXTRACT_TOKEN(Strings, 4, '|') AS 客户端名, EXTRACT_TOKEN(Strings, 5, '|') AS 客户端地址 FROM Security WHERE EventID = 4779

[account_created]
label: 新建账户 (4720)
group: 二、账户与权限
log: Security
default: true
note: 后门账户
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 新账户, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者, EXTRACT_TOKEN(Strings, 1, '|') AS 所在域 FROM Security WHERE EventID = 4720

[account_enabled]
label: 账户启用 (4722)
group: 二、账户与权限
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 目标账户, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者 FROM Security WHERE EventID = 4722

[account_disabled]
label: 账户禁用 (4725)
group: 二、账户与权限
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 目标账户, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者 FROM Security WHERE EventID = 4725

[account_deleted]
label: 删除账户 (4726)
group: 二、账户与权限
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 目标账户, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者 FROM Security WHERE EventID = 4726

[account_added_to_group]
label: 加入用户组 (4728)
group: 二、账户与权限
log: Security
note: 观察是否进了管理员组
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 成员, EXTRACT_TOKEN(Strings, 2, '|') AS 组名, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者 FROM Security WHERE EventID = 4728

[group_added_to_group]
label: 组嵌套提权 (4732)
group: 二、账户与权限
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 成员, EXTRACT_TOKEN(Strings, 2, '|') AS 组名, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者 FROM Security WHERE EventID = 4732

[password_reset]
label: 密码重置 (4724)
group: 二、账户与权限
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 目标账户, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者 FROM Security WHERE EventID = 4724

[kerberos_rc4]
label: Kerberos RC4 请求 (4768)
group: 二、账户与权限
log: Security
note: 弱加密，可能是 Kerberoasting
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 账户, EXTRACT_TOKEN(Strings, 3, '|') AS 服务名, EXTRACT_TOKEN(Strings, 5, '|') AS IP, EXTRACT_TOKEN(Strings, 6, '|') AS 加密类型 FROM Security WHERE EventID = 4768

[service_install]
label: 服务安装 (7045)
group: 三、持久化与横向
log: System
default: true
note: PsExec / 后门服务典型痕迹
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 服务名, EXTRACT_TOKEN(Strings, 1, '|') AS 映像路径, EXTRACT_TOKEN(Strings, 2, '|') AS 启动类型, EXTRACT_TOKEN(Strings, 3, '|') AS 服务类型, EXTRACT_TOKEN(Strings, 4, '|') AS 运行账户 FROM System WHERE EventID = 7045

[service_start]
label: 服务启动异常 (7000/7009)
group: 三、持久化与横向
log: System
query: SELECT TimeGenerated AS 时间, EventID AS 事件ID, EXTRACT_TOKEN(Strings, 0, '|') AS 服务名, EXTRACT_TOKEN(Strings, 1, '|') AS 详情 FROM System WHERE EventID = 7000 OR EventID = 7009

[task_created]
label: 计划任务创建 (4698)
group: 三、持久化与横向
log: Security
default: true
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 任务名, EXTRACT_TOKEN(Strings, 1, '|') AS 任务内容, EXTRACT_TOKEN(Strings, 6, '|') AS 操作者 FROM Security WHERE EventID = 4698

[task_updated]
label: 计划任务修改 (4702)
group: 三、持久化与横向
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 任务名, EXTRACT_TOKEN(Strings, 6, '|') AS 操作者 FROM Security WHERE EventID = 4702

[task_deleted]
label: 计划任务删除 (4699)
group: 三、持久化与横向
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 任务名, EXTRACT_TOKEN(Strings, 6, '|') AS 操作者 FROM Security WHERE EventID = 4699

[psExec_service]
label: PsExec 服务命名痕迹
group: 三、持久化与横向
log: System
note: 默认服务名 PSEXESVC
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 服务名, EXTRACT_TOKEN(Strings, 1, '|') AS 映像路径 FROM System WHERE EventID = 7045 AND EXTRACT_TOKEN(Strings, 0, '|') LIKE '%PSEXESVC%'

[share_access]
label: 网络共享访问 (5140/5145)
group: 三、持久化与横向
log: Security
note: 横向移动拷贝样本
query: SELECT TimeGenerated AS 时间, EventID AS 事件ID, EXTRACT_TOKEN(Strings, 1, '|') AS 账户, EXTRACT_TOKEN(Strings, 3, '|') AS 共享名, EXTRACT_TOKEN(Strings, 6, '|') AS 来源IP, EXTRACT_TOKEN(Strings, 7, '|') AS 相对路径 FROM Security WHERE EventID = 5140 OR EventID = 5145

[dcsync]
label: 目录复制请求 (4662)
group: 三、持久化与横向
log: Security
note: DCSync 复制域凭据
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 1, '|') AS 账户, EXTRACT_TOKEN(Strings, 2, '|') AS 对象, EXTRACT_TOKEN(Strings, 6, '|') AS 访问属性 FROM Security WHERE EventID = 4662

[log_cleared]
label: 日志清除 (1102)
group: 四、痕迹清理与破坏
log: Security
default: true
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 清除者, EXTRACT_TOKEN(Strings, 1, '|') AS 计算机 FROM Security WHERE EventID = 1102

[system_log_cleared]
label: 系统日志清除 (104)
group: 四、痕迹清理与破坏
log: System
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 0, '|') AS 日志名, EXTRACT_TOKEN(Strings, 1, '|') AS 用户, EXTRACT_TOKEN(Strings, 2, '|') AS 计算机 FROM System WHERE EventID = 104

[audit_policy_changed]
label: 审计策略变更 (4719)
group: 四、痕迹清理与破坏
log: Security
query: SELECT TimeGenerated AS 时间, EXTRACT_TOKEN(Strings, 4, '|') AS 操作者, EXTRACT_TOKEN(Strings, 6, '|') AS 详情 FROM Security WHERE EventID = 4719

[defender_disabled]
label:  Defender 关闭/篡改
group: 四、痕迹清理与破坏
log: System
query: SELECT TimeGenerated AS 时间, EventID AS 事件ID, SourceName AS 来源, Message AS 详情 FROM System WHERE (SourceName LIKE '%Defender%' OR SourceName LIKE '%Windows Defender%') AND (EventID = 5001 OR EventID = 5004 OR EventID = 5007 OR EventID = 5010 OR EventID = 5012)

[process_creation_raw]
label: 原始 Strings 导出（核对字段索引）
group: 五、字段核查工具
log: System
note: 想加新检测项时先用它看字段顺序
query: SELECT TOP 200 TimeGenerated AS 时间, EventID AS 事件ID, SourceName AS 来源, Strings AS 原始字段, Message AS 消息 FROM System WHERE EventID <> 0
";
    }
}
