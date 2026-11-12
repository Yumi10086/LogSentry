using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LogSentry
{
    /// <summary>程序启动引导：异常兜底、单实例、自动提权、启动主窗体。</summary>
    internal static class Program
    {
        private const string AppTitle = "LogSentry · Windows 日志一把梭分析";
        private const string MutexName = "Global\\LogSentry.SingleInstance.7F1C0A80";

        public static void MainCore()
        {
            // 老机器（Win7 未打补丁 / Win8.1 精简版）可能没有 .NET Framework 4.x，
            // 那样 CLR 根本不会加载到这一行，直接弹「初始化错误」。所以真正的前置检查
            // 在打包版里由 SFX 的 InstallPath 脚本做（见 pack-sfx.ps1 / 7zSD.sfx 的自解压流程），
            // 这里只做兜底：当 CLR 能起来但版本低于 4.0 时给出安装指引。
            if (!RuntimeIsUsable())
            {
                MessageBox.Show(
                    "当前系统的 .NET Framework 版本过低，LogSentry 需要 .NET Framework 4.0 或更高版本。\r\n\r\n"
                    + "请先安装 .NET Framework 4.x 后再运行本程序；\r\n"
                    + "便携版（LogSentry_Portable.exe）会自动检测并安装随包的 .NET Framework 4。",
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { ShowFatal(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) ShowFatal(ex);
            };

            bool createdNew;
            Mutex mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                ActivateExistingInstance();
                return;
            }

            if (!IsElevated())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    Process.Start(psi);
                    return;
                }
                catch (Win32Exception)
                {
                    // 用户在 UAC 拒绝：继续以普通权限运行，界面右下角会提示
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch (Exception) { }
            }
        }

        private static void ActivateExistingInstance()
        {
            try
            {
                Process cur = Process.GetCurrentProcess();
                Process[] all = Process.GetProcessesByName(cur.ProcessName);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Id != cur.Id && all[i].MainWindowHandle != IntPtr.Zero)
                    {
                        SetForegroundWindow(all[i].MainWindowHandle);
                        break;
                    }
                }
            }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>CLR 能起来说明至少 4.x 的某版本，这里再确认关键程序集可用。</summary>
        private static bool RuntimeIsUsable()
        {
            try
            {
                Version v = Environment.Version;   // 4.x 时为 4.0.30319.x
                if (v.Major < 4) return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p =
                    new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ShowFatal(Exception ex)
        {
            try
            {
                string text = "程序发生未处理的异常：\r\n\r\n" + ex.GetType().Name + ": " + ex.Message
                    + "\r\n\r\n" + ex.StackTrace;
                try
                {
                    string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LogSentry_error.log");
                    File.AppendAllText(logFile, DateTime.Now + "\r\n" + text + "\r\n\r\n", Encoding.UTF8);
                }
                catch (Exception) { }
                MessageBox.Show(text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }
    }
}
