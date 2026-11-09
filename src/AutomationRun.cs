using System.Collections.Generic;

namespace LogSentry
{
    /// <summary>
    /// 自动化运行参数（--smoke 模式使用）：把勾选项与结果回传给调用方，
    /// 让程序在没有人工点击的环境里也能跑完整条链路并自证。
    /// </summary>
    internal sealed class AutomationRun
    {
        /// <summary>要执行的检测项 id；为空则使用配置里的默认勾选项。</summary>
        public List<string> Ids = new List<string>();

        /// <summary>执行是否已经结束。</summary>
        public bool Finished;

        public int OkCount;
        public int FailCount;
        public int HitCount;
        public string OutputText = "";
    }
}
