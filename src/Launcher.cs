using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace LogSentryLauncher
{
    /// <summary>
    /// LogSentry 便携版引导器（自解压外壳）。
    ///
    /// 为什么不用 7-Zip SFX：官方 7z.sfx / 7zS.sfx / 7zCon.sfx 都是纯解压模块，不认
    /// ;!@Install@! 配置段里的 RunProgram / Directory / InstallPath，解压完直接退出
    /// （实测 exit=0 但标记文件从未生成）。带安装脚本能力的是第三方改造版 7zSD.sfx，
    /// 官方 extra 包里没有。所以这里自己写外壳：
    ///
    /// 文件布局（pack-portable.ps1 拼装）：
    ///   [本 exe 的 PE 内容] + "LSPAYLOAD!" + [zip 载荷]
    ///
    /// zip 载荷用 .NET 自带 ZipArchive 读，目标机不需要任何外部解压工具。
    /// 引导器自身不提权（asInvoker），UAC 由解压出来的 LogSentry.exe 自己弹。
    /// </summary>
    internal static class Launcher
    {
        private const string AppTitle = "LogSentry 便携版";
        private const string PayloadMarker = "LSPAYLOAD!";
        private const string MainExeName = "LogSentry.exe";

        [STAThread]
        private static int Main(string[] args)
        {
            bool keep = false;
            bool silent = false;
            foreach (string a in args)
            {
                if (string.Compare(a, "--keep", true) == 0) keep = true;
                else if (string.Compare(a, "--silent", true) == 0) silent = true;
            }

            string workDir = null;
            try
            {
                byte[] payload = ExtractPayload(Application.ExecutablePath);
                if (payload == null || payload.Length == 0)
                {
                    MessageBox.Show(
                        "便携包损坏：没有找到内部载荷。\r\n\r\n请重新下载 LogSentry_Portable.exe。",
                        AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }

                workDir = CreateWorkDir(Application.ExecutablePath, keep);
                UnzipTo(payload, workDir);

                string target = Path.Combine(workDir, MainExeName);
                if (!File.Exists(target))
                {
                    MessageBox.Show(
                        "便携包内容不完整：解压目录里找不到 " + MainExeName + "。\r\n\r\n" + workDir,
                        AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 3;
                }

                bool started = Launch(target, workDir);
                if (!started && !silent)
                {
                    MessageBox.Show(
                        "已解压到：\r\n" + workDir + "\r\n\r\n但没有启动成功，请手动双击该目录下的 " + MainExeName + "。",
                        AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return started ? 0 : 4;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "便携版启动失败：\r\n\r\n" + ex.Message + (workDir == null ? "" : "\r\n\r\n解压目录：" + workDir),
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                if (workDir != null && !keep) ScheduleCleanup(workDir);
            }
        }

        /// <summary>从自身文件尾部读取出 zip 载荷（标记之后到文件末尾）。</summary>
        private static byte[] ExtractPayload(string selfPath)
        {
            using (FileStream fs = new FileStream(selfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int markerLen = PayloadMarker.Length;          // 纯 ASCII，字节数等于字符数
                byte[] marker = Encoding.ASCII.GetBytes(PayloadMarker);
                const int chunk = 64 * 1024;
                long searchFrom = fs.Length;                    // 已扫描到的位置上限

                while (searchFrom > 0)
                {
                    long blockStart = searchFrom - chunk;
                    if (blockStart < 0) blockStart = 0;
                    int blockLen = (int)(searchFrom - blockStart);
                    byte[] buf = new byte[blockLen];
                    fs.Position = blockStart;
                    int read = 0;
                    while (read < blockLen)
                    {
                        int n = fs.Read(buf, read, blockLen - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    for (int i = read - markerLen; i >= 0; i--)
                    {
                        if (buf[i] != marker[0]) continue;
                        bool hit = true;
                        for (int k = 1; k < markerLen; k++)
                        {
                            if (buf[i + k] != marker[k]) { hit = false; break; }
                        }
                        if (!hit) continue;
                        return ReadFrom(fs, blockStart + i + markerLen);
                    }

                    if (blockStart == 0) break;
                    // 相邻块留出一段重叠，保证跨块的标记也能被看到
                    searchFrom = blockStart + markerLen;
                }
            }
            return null;
        }

        private static byte[] ReadFrom(FileStream fs, long offset)
        {
            long len = fs.Length - offset;
            if (len <= 0) return null;
            byte[] data = new byte[len];
            fs.Position = offset;
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == data.Length) return data;
            byte[] trimmed = new byte[read];
            Buffer.BlockCopy(data, 0, trimmed, 0, read);
            return trimmed;
        }

        /// <summary>解压目录：默认 %TEMP%\LogSentry_&lt;pid&gt;_&lt;ticks&gt;，--keep 时落到 exe 旁边。</summary>
        private static string CreateWorkDir(string selfPath, bool keep)
        {
            if (keep)
            {
                string side = Path.Combine(Path.GetDirectoryName(selfPath), "LogSentry_unpacked");
                try
                {
                    if (Directory.Exists(side)) Directory.Delete(side, true);
                    Directory.CreateDirectory(side);
                    return side;
                }
                catch (Exception) { }
            }
            string dir = Path.Combine(
                Path.GetTempPath(),
                "LogSentry_" + Process.GetCurrentProcess().Id + "_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>把 zip 载荷展开到目标目录（保留目录结构）。</summary>
        private static void UnzipTo(byte[] zipBytes, string destDir)
        {
            using (MemoryStream ms = new MemoryStream(zipBytes, false))
            using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.Combine(destDir, name);
                    if (entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(full);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(full);
                    if (parent != null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    using (Stream src = entry.Open())
                    using (FileStream dst = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buf = new byte[64 * 1024];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                    }
                }
            }
        }

        /// <summary>启动 LogSentry.exe；用户拒绝 UAC 时返回 false。</summary>
        private static bool Launch(string target, string workDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = target;
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = true;
            try
            {
                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return false;   // 用户在 UAC 点了「否」
                throw;
            }
        }

        /// <summary>退出后删除解压目录：起一个隐藏 cmd 等几秒再删，规避占用。</summary>
        private static void ScheduleCleanup(string workDir)
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(sysDir, "cmd.exe");
                psi.Arguments = "/c ping -n 4 127.0.0.1 >nul & rd /s /q \"" + workDir + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception) { }
        }
    }
}
