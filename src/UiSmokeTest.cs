using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LogSentry
{
    /// <summary>
    /// 无人值守端到端冒烟测试：
    ///   建窗体 → 截图 → 勾选指定检测项 → 点“开始分析” → 等结果 → 再截图 → 写报告退出。
    /// 用途：在没有管理员权限、也没有人点击的环境里，证明「GUI 不冻结 + 引擎 + 中文输出 + 失败标记」
    /// 整条链路确实能跑通。
    ///
    /// 调用：LogSentry.exe --smoke [输出目录] [id1,id2,...]
    ///   不给 id 时使用配置里的默认勾选项。
    /// 退出码：0 = 窗体与执行都成功；1 = 有异常；2 = 执行阶段有项失败（非管理员读 Security 的预期失败）。
    /// </summary>
    internal static class UiSmokeTest
    {
        public static int Run(string[] args)
        {
            string outDir = args.Length > 1 && args[1].Length > 0 ? args[1] : AppDomain.CurrentDomain.BaseDirectory;
            string idList = args.Length > 2 ? args[2] : "";
            string manualCmd = "";
            string fromStr = "";
            string toStr = "";
            int hours = 0;
            bool maximized = false;
            bool optionsTab = false;
            string exportXlsx = "";
            string exportCsvDir = "";
            string formatName = "";
            for (int a = 3; a < args.Length - 1; a++)
            {
                if (args[a] == "--hours") int.TryParse(args[a + 1], out hours);
                if (args[a] == "--from") fromStr = args[a + 1];
                if (args[a] == "--to") toStr = args[a + 1];
            }
            for (int a = 1; a < args.Length - 1; a++)
            {
                if (args[a] == "--export-xlsx") exportXlsx = args[a + 1];
                if (args[a] == "--export-csv") exportCsvDir = args[a + 1];
                if (args[a] == "--format") formatName = args[a + 1];
                if (args[a] == "--manual") manualCmd = args[a + 1];
            }
            for (int a = 1; a < args.Length; a++)
            {
                if (args[a] == "--max") maximized = true;
                if (args[a] == "--tab2") optionsTab = true;
            }
            StringBuilder sb = new StringBuilder();
            int code = 0;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 用真正的消息循环跑，和用户双击 exe 完全一致：
                // Application.Run 会装上 WindowsFormsSynchronizationContext，await 之后的
                // 续体回到 UI 线程。手写 DoEvents 泵不会装上下文，续体会掉到线程池，
                // 那时碰 RichTextBox 每次要等近 1 秒（实测），测出来的耗时毫无参考价值。
                MainForm f = new MainForm();
                int[] codeBox = new int[] { 0 };
                f.Shown += delegate
                {
                    f.BeginInvoke((MethodInvoker)delegate
                    {
                        BodyAsync(f, sb, outDir, idList, manualCmd, fromStr, toStr, hours,
                            maximized, optionsTab, exportXlsx, exportCsvDir, formatName, codeBox);
                    });
                };
                Application.Run(f);
                code = codeBox[0];
            }
            catch (Exception ex)
            {
                code = 1;
                sb.AppendLine("RESULT           = FAIL");
                sb.AppendLine(ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
            }

            try { File.WriteAllText(Path.Combine(outDir, "smoke.log"), sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            Console.OutputEncoding = Encoding.UTF8;
            Console.Write(sb.ToString());
            return code;
        }

        /// <summary>
        /// 冒烟主体：跑在真实消息循环里，等待全部用 await（不手写 DoEvents 泵），
        /// 所以测出来的 Run.Wall / Perf.* 就是用户双击 exe 时的真实数字。
        /// </summary>
        private static async void BodyAsync(MainForm f, StringBuilder sb, string outDir, string idList, string manualCmd,
            string fromStr, string toStr, int hours, bool maximized, bool optionsTab,
            string exportXlsx, string exportCsvDir, string formatName, int[] codeBox)
        {
            int code = 0;
            try
            {
                sb.AppendLine("=== LogSentry 自动化冒烟 ===");
                sb.AppendLine("SyncContext      = " + (System.Threading.SynchronizationContext.Current == null
                    ? "null" : System.Threading.SynchronizationContext.Current.GetType().Name));
                sb.AppendLine("Form.Text        = " + f.Text);
                sb.AppendLine("ClientSize       = " + f.ClientSize.Width + "x" + f.ClientSize.Height);
                sb.AppendLine("CheckboxCount    = " + CountChecks(f));
                sb.AppendLine("Elevated         = " + IsElevated());
                sb.AppendLine("OutputEncoding   = CP" + LogParserRunner.OutputEncoding.CodePage);
                DumpLayout(f, sb);
                DumpTextFit(f, sb);
                Snap(f, Path.Combine(outDir, "ui-1-before.png"), sb);

                if (maximized)
                {
                    f.WindowState = FormWindowState.Maximized;
                    await Task.Delay(600);
                    DumpLayout(f, sb);
                    Snap(f, Path.Combine(outDir, "ui-3-maximized.png"), sb);
                }

                if (optionsTab)
                {
                    f.ShowOptionsTab();
                    await Task.Delay(300);
                    Snap(f, Path.Combine(outDir, "ui-4-options.png"), sb);
                    f.ShowPresetTab();
                }

                AutomationRun run = new AutomationRun();
                if (idList.Length > 0)
                {
                    string[] ids = idList.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fromStr.Length > 0 && toStr.Length > 0)
                    {
                        f.SetTimeFilterExact(DateTime.Parse(fromStr), DateTime.Parse(toStr));
                        sb.AppendLine("TimeFilter       = " + fromStr + " ~ " + toStr);
                    }
                    else
                        f.SelectByIds(ids);
                }

                if (hours > 0)
                {
                    f.SetTimeFilter(hours);
                    sb.AppendLine("TimeFilter       = 最近 " + hours + " 小时");
                }

                if (formatName.Length > 0)
                {
                    f.SetOutputFormat(formatName);
                    sb.AppendLine("OutputFormat     = " + f.OutputFormatName);
                }

                bool uiAlive = true;
                DateTime tStart = DateTime.Now;
                if (manualCmd.Length > 0)
                {
                    // 手动命令通路：切到「手动命令」页 → 填命令 → 直接执行
                    f.ShowManualTab();
                    f.ManualText = manualCmd;
                    sb.AppendLine("ManualTab        = 已切换到「手动命令」页");
                    sb.AppendLine("ManualInput      = " + manualCmd);
                    sb.AppendLine("ManualParse      = " + f.ManualParsePreview.Replace("\r\n", " / "));
                    Snap(f, Path.Combine(outDir, "ui-5-manual.png"), sb);
                    await f.RunManualAsync(run);
                }
                else
                {
                    await f.RunSelectionAsync(run);
                }

                sb.AppendLine("UI stayed alive  = " + (uiAlive && run.Finished));
                sb.AppendLine("Run.Wall         = 执行到完成 " + (DateTime.Now - tStart).TotalSeconds.ToString("0.0") + "s");
                sb.AppendLine("Perf.RunOneMs    = LogParser 调用累计 " + f.PerfRunOneMs.ToString("0") + " ms");
                sb.AppendLine("Perf.PrepMs      = 结果预处理（数行/压空格）累计 " + f.PerfPrepMs.ToString("0") + " ms");
                sb.AppendLine("Perf.AppendMs    = 结果区渲染累计 " + f.PerfAppendMs.ToString("0") + " ms / " + f.PerfAppendCalls + " 次");
                sb.AppendLine("Perf.AppendSplit = 定位/颜色 " + f.PerfFontMs.ToString("0") + " ms，AppendText " + f.PerfTextMs.ToString("0") + " ms，ScrollToCaret " + f.PerfScrollMs.ToString("0") + " ms");
                sb.AppendLine("Perf.MaxFlush    = 最慢一次写入 " + f.PerfMaxFlushMs.ToString("0") + " ms / " + f.PerfMaxFlushChars + " 字符；累计写入 " + f.PerfTotalChars + " 字符");
                sb.AppendLine("Perf.FlushLog    = " + f.PerfFlushLog);
                sb.AppendLine("Result.Ok        = " + run.OkCount);
                sb.AppendLine("Result.Fail      = " + run.FailCount);
                sb.AppendLine("Result.Hit       = " + run.HitCount);
                sb.AppendLine("StatusBar        = " + StatusText(f));
                Snap(f, Path.Combine(outDir, "ui-2-after.png"), sb);

                // 导出通路：--export-xlsx <file> / --export-csv <dir>（不弹任何对话框）
                if (exportXlsx.Length > 0)
                {
                    await f.ExportForTestAsync(false, exportXlsx);
                    bool made = File.Exists(exportXlsx);
                    sb.AppendLine("Export.Xlsx      = " + exportXlsx + " (" + (made ? new FileInfo(exportXlsx).Length + " bytes" : "MISSING") + ")");
                    sb.AppendLine("Export.Xlsx.Sum  = " + f.ExportSummary);
                    if (!made) code = 1;
                }
                if (exportCsvDir.Length > 0)
                {
                    await f.ExportForTestAsync(true, exportCsvDir);
                    string[] csvs = Directory.Exists(exportCsvDir)
                        ? Directory.GetFiles(exportCsvDir, "*.csv") : new string[0];
                    sb.AppendLine("Export.CsvDir    = " + exportCsvDir + " (" + csvs.Length + " files)");
                    sb.AppendLine("Export.Csv.Sum   = " + f.ExportSummary);
                    for (int i = 0; i < Math.Min(csvs.Length, 3); i++)
                        sb.AppendLine("  + " + Path.GetFileName(csvs[i]) + " (" + new FileInfo(csvs[i]).Length + " bytes)");
                    if (csvs.Length == 0) code = 1;
                }

                string txt = Path.Combine(outDir, "ui-results.txt");
                File.WriteAllText(txt, run.OutputText, new UTF8Encoding(true));
                sb.AppendLine("ResultsFile      = " + txt + " (" + new FileInfo(txt).Length + " bytes)");
                sb.AppendLine("FirstLines:");
                string[] lines = run.OutputText.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length && i < 12; i++) sb.AppendLine("  | " + lines[i]);

                bool chineseOk = run.OutputText.IndexOf("完成：成功", StringComparison.Ordinal) >= 0;
                sb.AppendLine("ChineseOK        = " + chineseOk);
                if (!chineseOk && code == 0) code = 1;

                if (run.FailCount > 0 && code == 0) code = 2;
                sb.AppendLine("RESULT           = " + (code == 0 ? "OK" : (code == 2 ? "OK(部分项按预期失败)" : "FAIL")));
                DumpStatusItems(f, sb);
            }
            catch (Exception ex)
            {
                code = 1;
                sb.AppendLine("RESULT           = FAIL");
                sb.AppendLine(ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
            }
            finally
            {
                codeBox[0] = code;
                try { f.Close(); } catch (Exception) { }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        /// <summary>
        /// 截图：DrawToBitmap 画不出 RichTextBox 的内容，CopyFromScreen 又受窗口位置/遮挡影响，
        /// 所以优先用 PrintWindow(PW_RENDERFULLCONTENT) 让窗口自己把内容画到内存 DC 上。
        /// </summary>
        private static void Snap(Form f, string path, StringBuilder sb)
        {
            string how = "none";
            try
            {
                f.BringToFront();
                Application.DoEvents();
                System.Threading.Thread.Sleep(200);
                Application.DoEvents();

                bool ok = false;
                if (f.IsHandleCreated)
                {
                    using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            try { ok = PrintWindow(f.Handle, hdc, PW_RENDERFULLCONTENT); }
                            finally { g.ReleaseHdc(hdc); }
                        }
                        if (ok) { bmp.Save(path, ImageFormat.Png); how = "printwindow"; }
                    }
                }

                if (!ok)
                {
                    using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(path, ImageFormat.Png);
                    }
                    how = "drawtobitmap";
                }
                sb.AppendLine("Screenshot       = " + path + " (" + new FileInfo(path).Length
                    + " bytes, " + how + ")");
            }
            catch (Exception ex) { sb.AppendLine("Screenshot FAILED: " + ex.Message); }
        }

        private static void DumpLayout(Form f, StringBuilder sb)
        {
            foreach (Control c in AllControls(f))
            {
                SplitContainer sc = c as SplitContainer;
                if (sc == null) continue;
                sb.AppendLine("Splitter         = Distance=" + sc.SplitterDistance
                    + " Width=" + sc.Width + " Panel1=" + sc.Panel1.Width + " Panel2=" + sc.Panel2.Width
                    + " Panel1Min=" + sc.Panel1MinSize + " Panel2Min=" + sc.Panel2MinSize);
                sb.AppendLine("Panel1Preferred  = " + sc.Panel1.PreferredSize.Width);
                sb.AppendLine("Panel2Preferred  = " + sc.Panel2.PreferredSize.Width);
            }
        }

        /// <summary>
        /// 文字是否放得下：按控件当前字体实测所需宽度，和控件实际宽度比较。
        /// 高 DPI 下这一项最容易出问题（写死宽度的 DateTimePicker / ComboBox 会把"秒"裁掉）。
        /// </summary>
        private static void DumpTextFit(Form f, StringBuilder sb)
        {
            foreach (Control c in AllControls(f))
            {
                DateTimePicker dp = c as DateTimePicker;
                if (dp != null)
                {
                    int need = TextRenderer.MeasureText(dp.Text, dp.Font).Width + 28;
                    sb.AppendLine("Fit.Picker       = \"" + dp.Text + "\" width=" + dp.Width
                        + " need=" + need + " ok=" + (dp.Width >= need));
                    continue;
                }

                ComboBox cb = c as ComboBox;
                if (cb != null && cb.DropDownStyle == ComboBoxStyle.DropDownList)
                {
                    int need = 0;
                    for (int i = 0; i < cb.Items.Count; i++)
                    {
                        int w = TextRenderer.MeasureText(cb.Items[i].ToString(), cb.Font).Width + 30;
                        if (w > need) need = w;
                    }
                    // 命令历史下拉是故意截断到 58 字符显示的（完整命令在 DropDownWidth=520
                    // 的展开列表里看），闭合状态显示省略号属正常，不按「文字被裁」算失败。
                    bool relaxed = (cb.Name == "cboHistory");
                    sb.AppendLine("Fit.Combo        = \"" + cb.Text + "\" width=" + cb.Width
                        + " need=" + need + " ok=" + (relaxed || cb.Width >= need)
                        + (relaxed ? " (历史命令按设计截断)" : ""));
                }
            }

            // 左栏标题与三个按钮必须在同一行时不重叠（重叠说明又是绝对坐标惹的）
            foreach (Control c in AllControls(f))
            {
                FlowLayoutPanel fl = c as FlowLayoutPanel;
                if (fl == null || fl.Name != "leftHeader") continue;
                Rectangle[] r = new Rectangle[fl.Controls.Count];
                for (int i = 0; i < fl.Controls.Count; i++) r[i] = fl.Controls[i].Bounds;
                int overlaps = 0;
                for (int i = 0; i < r.Length; i++)
                {
                    for (int j = i + 1; j < r.Length; j++)
                    {
                        if (r[i].IntersectsWith(r[j])) overlaps++;
                    }
                }
                sb.AppendLine("Fit.Header       = children=" + fl.Controls.Count + " height=" + fl.Height
                    + " overlaps=" + overlaps + " ok=" + (overlaps == 0));
            }

            // 手动命令页：所有控件必须落在页宽内（否则横向滚动条），且命令框要有实际高度
            Control mp = (f is MainForm) ? ((MainForm)f).ManualPage : null;
            if (mp != null)
            {
                int pageW = mp.ClientSize.Width;
                int maxRight = 0;
                int overflow = 0;
                Control box = null;
                foreach (Control c in AllControls(mp))
                {
                    if (c == mp) continue;
                    // 子控件 Left 是相对各自父容器的，换算到页坐标系才准
                    Point at = mp.PointToClient(c.PointToScreen(Point.Empty));
                    int right = at.X + c.Width;
                    if (right > maxRight) maxRight = right;
                    if (right > pageW) overflow++;
                    if (c.Name == "txtManual") box = c;
                }
                sb.AppendLine("Fit.Manual       = pageW=" + pageW + " maxRight=" + maxRight
                    + " overflow=" + overflow
                    + " txtH=" + (box == null ? -1 : box.Height)
                    + " ok=" + (overflow == 0 && box != null && box.Height >= 80));

                // 诊断：把 cboHistory 的父链宽度打出来，定位是谁把左栏撑宽的
                foreach (Control c in AllControls(mp))
                {
                    if (c.Name != "cboHistory") continue;
                    Control p = c;
                    string chain = "";
                    while (p != null)
                    {
                        chain += p.GetType().Name + "(" + (string.IsNullOrEmpty(p.Name) ? "-" : p.Name) + ")="
                            + p.Width + "/" + p.ClientSize.Width + " ";
                        p = p.Parent;
                    }
                    sb.AppendLine("Fit.ManualDiag   = cboHistory.Width=" + c.Width + " chain: " + chain);
                }
            }
        }


        private static void DumpStatusItems(Form f, StringBuilder sb)
        {
            foreach (Control c in AllControls(f))
            {
                StatusStrip ss = c as StatusStrip;
                if (ss == null) continue;
                for (int i = 0; i < ss.Items.Count; i++)
                    sb.AppendLine("StatusItem[" + i + "]    = \"" + ss.Items[i].Text + "\"");
            }
        }

        private static string StatusText(Form f)
        {
            foreach (Control c in AllControls(f))
            {
                StatusStrip ss = c as StatusStrip;
                if (ss == null || ss.Items.Count == 0) continue;
                string joined = "";
                for (int i = 0; i < ss.Items.Count; i++)
                {
                    string t = (ss.Items[i].Text ?? "").Trim();
                    if (t.Length == 0) continue;
                    if (joined.Length > 0) joined += " | ";
                    joined += t;
                }
                return joined;
            }
            return "(no status strip)";
        }

        private static System.Collections.Generic.IEnumerable<Control> AllControls(Control root)
        {
            yield return root;
            for (int i = 0; i < root.Controls.Count; i++)
                foreach (Control c in AllControls(root.Controls[i])) yield return c;
        }

        private static int CountChecks(Control root)
        {
            int n = root is CheckBox ? 1 : 0;
            for (int i = 0; i < root.Controls.Count; i++) n += CountChecks(root.Controls[i]);
            return n;
        }

        private static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }
    }
}