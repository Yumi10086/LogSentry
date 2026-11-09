using System;

namespace LogSentry
{
    internal static class EntryPoint
    {
        [STAThread]
        public static int Main(string[] args)
        {
#if SELFTEST
            return SelfTest.Run(args);
#else
            // --smoke：无人值守 UI 冒烟测试（构建窗体→截图→退出），CI/自动验证用
            if (args != null && args.Length > 0 && args[0] == "--smoke")
                return UiSmokeTest.Run(args);

            Program.MainCore();
            return 0;
#endif
        }
    }
}
