using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using LogSentry;

namespace LogSentry
{
    /// <summary>控制台测试入口：csc /define:SELFTEST 编译后用于无 UI 验证引擎。</summary>
    internal static class SelfTest
    {
        private static int _fail;

        public static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== LogSentry 自检 ===");
            Console.WriteLine("OutputEncoding.CodePage = " + LogParserRunner.OutputEncoding.CodePage);

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string lp = args.Length > 0 ? args[0] : System.IO.Path.Combine(exeDir, "LogParser.exe");
            Console.WriteLine("LogParser = " + lp + "  exists=" + System.IO.File.Exists(lp));

            // 1) 配置解析
            string cfgPath = System.IO.Path.Combine(exeDir, "queries.txt");
            if (!System.IO.File.Exists(cfgPath))
                cfgPath = System.IO.Path.Combine(exeDir, "..", "dist", "queries.txt");
            ConfigLoadResult cfg = ConfigLoader.Load(cfgPath);
            Console.WriteLine("[1] 配置 = " + cfgPath);
            Console.WriteLine("[1] 配置项 = " + cfg.Items.Count + "  警告 = " + cfg.Errors.Count);
            for (int i = 0; i < cfg.Errors.Count; i++) Console.WriteLine("    ! " + cfg.Errors[i]);
            Check("配置项数量 >= 8", cfg.Items.Count >= 8);
            Check("每条都有 Id/Label/Query", AllItemsValid(cfg.Items));
            Check("每条 Id 唯一", UniqueIds(cfg.Items));

            // 1b) 随程序分发的查询表不能和内置默认值跑偏：两者不一致，用户删掉 queries.txt
            //     之后重新生成的检测项就会和说明书里写的不一样。
            string normFile = NormText(System.IO.File.ReadAllText(cfgPath));
            string normBuiltin = NormText(BuiltinQueries.Text);
            bool sameText = normFile == normBuiltin;
            Console.WriteLine("[1b] queries.txt " + normFile.Length + " 字符 / BuiltinQueries " + normBuiltin.Length
                + " 字符 / 一致=" + sameText);
            Check("queries.txt 与内置默认配置一致", sameText);

            // 2) 参数构造
            LogParserRunner.Options opt = new LogParserRunner.Options();
            opt.LogParserPath = lp;
            opt.UseLocalTime = true;
            opt.ExtraArguments = "-q:on";
            string argv = LogParserRunner.BuildArguments(opt, "SELECT 1 FROM System WHERE \"Name\"='x'");
            Console.WriteLine("[2] Arguments = " + argv);
            Check("参数含 -i:EVT", argv.Contains("-i:EVT"));
            Check("引号转义", argv.Contains("\\\""));

            // 3) 逐项执行（System 日志，普通权限可读）
            int ok = 0, fail = 0;
            LogParserRunner runner = new LogParserRunner();
            for (int i = 0; i < cfg.Items.Count; i++)
            {
                QueryItem it = cfg.Items[i];
                System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
                Task<QueryResult> t = runner.RunAsync(opt, it, cts.Token);
                t.Wait(60000);
                QueryResult r = t.Result;
                string first = FirstLine(r.Ok ? r.Output : r.Error);
                Console.WriteLine("[3] " + (r.Ok ? "OK  " : "FAIL") + "  " + it.Id.PadRight(22)
                    + " " + r.Elapsed.TotalSeconds.ToString("0.00") + "s  " + first);
                if (r.Ok) ok++; else fail++;
            }
            Console.WriteLine("[3] 汇总 ok=" + ok + " fail=" + fail);

            // 4) 输出清洗
            string raw = "A B\r\n--- ---\r\n1 2\r\n\r\nStatistics:\r\n-----------\r\nElements processed: 5\r\nElements output: 1\r\n\r\n";
            string cleaned;
            LogParserRunner.CleanStandardOutput(raw, out cleaned);
            Console.WriteLine("[4] cleaned = <" + cleaned.Replace("\r\n", "\\n").Replace("\n", "\\n") + ">");
            Check("统计块被裁剪", cleaned.IndexOf("Statistics") < 0);
            Check("正文保留", cleaned.IndexOf("1 2") >= 0);
            Check("ProcessedCount", LogParserRunner.ParseProcessedCount(raw) == 5);

            // 5) Unknown EventID 空结果不算失败
            QueryItem empty = new QueryItem();
            empty.Id = "empty"; empty.Label = "空结果"; empty.LogName = "System";
            empty.Query = "SELECT TimeGenerated FROM System WHERE EventID = 999999";
            System.Threading.CancellationTokenSource c2 = new System.Threading.CancellationTokenSource();
            Task<QueryResult> t2 = runner.RunAsync(opt, empty, c2.Token);
            t2.Wait(60000);
            Console.WriteLine("[5] 空结果 ok=" + t2.Result.Ok + " output=<" + t2.Result.Output.Replace("\r\n", "\\n") + ">");
            Check("空结果被视为成功", t2.Result.Ok);

            // 6) 语法错误必须被判失败
            QueryItem bad = new QueryItem();
            bad.Id = "bad"; bad.Label = "坏查询"; bad.LogName = "System";
            bad.Query = "SELECT NoSuchField FROM System";
            System.Threading.CancellationTokenSource c3 = new System.Threading.CancellationTokenSource();
            Task<QueryResult> t3 = runner.RunAsync(opt, bad, c3.Token);
            t3.Wait(60000);
            Console.WriteLine("[6] 坏查询 ok=" + t3.Result.Ok + " err=" + FirstLine(t3.Result.Error));
            Check("语法错误被判失败", !t3.Result.Ok);
            Check("错误信息含关键字", t3.Result.Error.IndexOf("Syntax", StringComparison.OrdinalIgnoreCase) >= 0);

            // 7) 权限不足的 Security 日志：应报失败且不崩
            QueryItem sec = new QueryItem();
            sec.Id = "sec"; sec.Label = "安全日志"; sec.LogName = "Security";
            sec.Query = "SELECT TOP 1 TimeGenerated FROM Security";
            System.Threading.CancellationTokenSource c4 = new System.Threading.CancellationTokenSource();
            Task<QueryResult> t4 = runner.RunAsync(opt, sec, c4.Token);
            t4.Wait(60000);
            Console.WriteLine("[7] Security ok=" + t4.Result.Ok + " msg=" + FirstLine(t4.Result.Ok ? t4.Result.Output : t4.Result.Error));

            // 8) 空白压缩：用真实的 7045 输出验证（手工构造样例容易把空格数写错，测不出真问题）
            QueryItem col = new QueryItem();
            col.Id = "col_probe"; col.Label = "列宽探测"; col.LogName = "System";
            col.Query = "SELECT TOP 5 TimeGenerated AS 时间, EXTRACT_TOKEN(Strings,0,'|') AS 服务名, "
                + "EXTRACT_TOKEN(Strings,1,'|') AS 映像路径, EXTRACT_TOKEN(Strings,3,'|') AS 启动类型, "
                + "EXTRACT_TOKEN(Strings,4,'|') AS 运行账户 FROM System WHERE EventID = 7045";
            // 注意：-q:on 会把表头和 --- 分列线一起吞掉，所以这里用和界面默认一致的 -rtp:-1
            LogParserRunner.Options optCols = new LogParserRunner.Options();
            optCols.LogParserPath = lp;
            optCols.UseLocalTime = true;
            optCols.ExtraArguments = "-rtp:-1";
            System.Threading.CancellationTokenSource c5 = new System.Threading.CancellationTokenSource();
            Task<QueryResult> t5 = runner.RunAsync(optCols, col, c5.Token);
            t5.Wait(60000);
            string body8 = t5.Result.Ok ? t5.Result.Output : "";
            string prettyB = LogParserRunner.CollapsePadding(body8);
            string[] rawLines = body8.Replace("\r\n", "\n").Split('\n');
            string[] prettyLines = prettyB.Replace("\r\n", "\n").Split('\n');

            Console.WriteLine("[8] 原始行数=" + rawLines.Length + "  原始字节=" + body8.Length + "  压缩后字节=" + prettyB.Length);
            int shown = 0;
            for (int i = 0; i < prettyLines.Length && shown < 3; i++)
                if (prettyLines[i].Trim().Length > 0) { Console.WriteLine("    | " + prettyLines[i]); shown++; }

            Check("确实压缩了空白", prettyB.Length < body8.Length);
            Check("行数不变", rawLines.Length == prettyLines.Length);

            int broken = 0;
            for (int i = 0; i < rawLines.Length; i++)
            {
                if (rawLines[i].Trim().Length == 0) continue;
                // 每个"词"都必须原样留在同一行里：去掉所有空格后逐字比较
                if (StripSpaces(rawLines[i]) != StripSpaces(prettyLines[i])) broken++;
            }
            Check("没有切丢字符", broken == 0);
            if (broken > 0) Console.WriteLine("    ! 有 " + broken + " 行的字符与原文不一致");

            // 压缩后不该再有 3 个以上连续空格
            int longRuns = 0;
            for (int i = 0; i < prettyLines.Length; i++)
                if (prettyLines[i].IndexOf("   ", StringComparison.Ordinal) >= 0) longRuns++;
            Check("没有残留的多余空格", longRuns == 0);
            if (longRuns > 0) Console.WriteLine("    ! 有 " + longRuns + " 行仍含 3 个连续空格");

            // 9) 时间筛选：语法注入 + 真实查询条数必须真的下降
            string cond = LogParserRunner.BuildTimeCondition(
                new DateTime(2026, 10, 1, 0, 0, 0), new DateTime(2026, 10, 31, 23, 59, 59));
            Console.WriteLine("[9] cond = " + cond);
            Check("时间条件含 TO_TIMESTAMP", cond.IndexOf("TO_TIMESTAMP", StringComparison.Ordinal) >= 0);
            Check("时间条件含 >= 与 <=", cond.IndexOf(" >= ") >= 0 && cond.IndexOf(" <= ") >= 0);

            string inj = LogParserRunner.InjectTimeFilter(
                "SELECT EventID FROM System WHERE EventID = 7045", null, null);
            Check("无时间条件时原样返回", inj == "SELECT EventID FROM System WHERE EventID = 7045");

            string inj2 = LogParserRunner.InjectTimeFilter(
                "SELECT EventID FROM System WHERE EventID = 7045",
                new DateTime(2026, 10, 1), null);
            Console.WriteLine("    | " + inj2);
            Check("有 WHERE 时用 AND 追加", inj2.IndexOf("EventID = 7045 AND TimeGenerated >=", StringComparison.Ordinal) >= 0);

            string inj3 = LogParserRunner.InjectTimeFilter(
                "SELECT EventID FROM System", new DateTime(2026, 10, 1), null);
            Check("无 WHERE 时补 WHERE", inj3.IndexOf(" FROM System WHERE TimeGenerated >=", StringComparison.Ordinal) >= 0);

            string inj4 = LogParserRunner.InjectTimeFilter(
                "SELECT EventID FROM System WHERE EventID = 7045 GROUP BY EventID",
                new DateTime(2026, 10, 1), null);
            Console.WriteLine("    | " + inj4);
            Check("GROUP BY 前插入", inj4.IndexOf("AND TimeGenerated >=", StringComparison.Ordinal) > 0
                && inj4.IndexOf("GROUP BY", StringComparison.Ordinal) > inj4.IndexOf("TimeGenerated", StringComparison.Ordinal));

            string inj5 = LogParserRunner.InjectTimeFilter(
                "SELECT EventID FROM System WHERE Strings LIKE '%ORDER%'",
                new DateTime(2026, 10, 1), null);
            Check("字符串字面量里的 ORDER 不被误判",
                inj5.IndexOf("LIKE '%ORDER%' AND TimeGenerated", StringComparison.Ordinal) > 0);

            // 真实执行：同一查询加不加时间条件，命中条数应该不同
            QueryItem tm = new QueryItem();
            tm.Id = "time_probe"; tm.Label = "时间探测"; tm.LogName = "System";
            tm.Query = "SELECT TimeGenerated FROM System WHERE EventID = 7045";

            QueryItem tm2 = new QueryItem();
            tm2.Id = "time_probe2"; tm2.Label = "时间探测(过滤)"; tm2.LogName = "System";
            tm2.Query = tm.Query;

            LogParserRunner.Options optAll = new LogParserRunner.Options();
            optAll.LogParserPath = lp;
            optAll.ExtraArguments = "-rtp:-1";

            LogParserRunner.Options optWin = new LogParserRunner.Options();
            optWin.LogParserPath = lp;
            optWin.ExtraArguments = "-rtp:-1";
            optWin.TimeFrom = new DateTime(2026, 10, 20);
            optWin.TimeTo = new DateTime(2026, 10, 31, 23, 59, 59);

            System.Threading.CancellationTokenSource c6 = new System.Threading.CancellationTokenSource();
            Task<QueryResult> t6a = runner.RunAsync(optAll, tm, c6.Token);
            t6a.Wait(90000);
            Task<QueryResult> t6b = runner.RunAsync(optWin, tm2, c6.Token);
            t6b.Wait(90000);
            int rowsAll = CountRows(t6a.Result.Output);
            int rowsWin = CountRows(t6b.Result.Output);
            Console.WriteLine("[9] 全量=" + rowsAll + " 行  2026-10-20..10-31=" + rowsWin + " 行  (ok=" + t6a.Result.Ok + "/" + t6b.Result.Ok + ")");
            Check("两次查询都成功", t6a.Result.Ok && t6b.Result.Ok);
            Check("时间筛选确实减少了命中", rowsAll > 0 && rowsWin > 0 && rowsWin < rowsAll);

            // ---------------------------------------------------------- [10] 导出（CSV / XLSX）
            Console.WriteLine("[10] 导出 CSV / XLSX");
            string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "logsentry_selftest_" + DateTime.Now.ToString("HHmmss"));
            System.IO.Directory.CreateDirectory(tmpDir);

            // CSV 解析：引号内的逗号 / 换行 / 双引号转义都要还原
            List<string[]> parsed = Exporters.ParseCsv("a,b\r\n\"x,1\",\"he said \"\"hi\"\"\"\r\n");
            Check("CSV 解析行数", parsed.Count == 2);
            Check("CSV 逗号在引号内不切分", parsed.Count == 2 && parsed[1].Length == 2 && parsed[1][0] == "x,1");
            Check("CSV 双引号转义还原", parsed.Count == 2 && parsed[1][1] == "he said \"hi\"");

            // 真实查询一次 CSV 输出，验证统计块被切掉、行列对齐
            LogParserRunner.Options optCsv = new LogParserRunner.Options();
            optCsv.LogParserPath = optAll.LogParserPath;
            optCsv.OutputFormat = "CSV";
            // -o:CSV + -rtp 会报 invalid parameter "rtp"，必须先剥掉（这是实测出来的坑）
            Check("-rtp 被剥离（CSV 不认该参数）",
                LogParserRunner.StripNatOnlyArguments("-rtp:-1 -stats:on") == "-stats:on");
            Check("导出参数自动补 -stats:off",
                LogParserRunner.BuildExportExtraArguments("-rtp:-1", false) == "-stats:off");
            optCsv.ExtraArguments = LogParserRunner.BuildExportExtraArguments(optAll.ExtraArguments, false);
            optCsv.TimeoutSeconds = 180;
            System.Threading.CancellationTokenSource c10 = new System.Threading.CancellationTokenSource();
            QueryItem svc = null;
            for (int i = 0; i < cfg.Items.Count; i++) { if (cfg.Items[i].Id == "service_install") { svc = cfg.Items[i]; break; } }
            if (svc != null)
            {
                Task<QueryResult> t10 = runner.RunAsync(optCsv, svc, c10.Token);
                t10.Wait(120000);
                List<string[]> logRows = Exporters.RowsFromLogParserCsv(t10.Result.RawOutput);
                int cols = logRows.Count > 0 ? logRows[0].Length : 0;
                Console.WriteLine("[10] CSV 行数=" + logRows.Count + " 列数=" + cols);
                Check("CSV 查询成功", t10.Result.Ok);
                Check("CSV 至少有一行表头 + 数据", logRows.Count >= 2);
                Check("CSV 表头 6 列（7045 配置）", cols == 6);
                Check("CSV 尾部统计块已切除", logRows.Count > 0
                    && logRows[logRows.Count - 1][0].IndexOf("Statistics", StringComparison.OrdinalIgnoreCase) < 0);
            }

            // XLSX：写一个两表工作簿，再用 ZipArchive 读回校验结构
            List<ExportSheet> sheets = new List<ExportSheet>();
            ExportSheet s1 = new ExportSheet();
            s1.Name = "登录成功 (4624)";
            s1.Rows.Add(new string[] { "时间", "目标用户", "来源IP" });
            s1.Rows.Add(new string[] { "2026-11-05 15:00:00", "administrator", "10.0.0.5" });
            s1.Rows.Add(new string[] { "2026-11-05 15:00:01", "含 & < > \" 特殊字符", "127.0.0.1" });
            sheets.Add(s1);
            ExportSheet s2 = new ExportSheet();
            s2.Name = "服务安装 (7045)";
            s2.Rows.Add(new string[] { "时间", "服务名" });
            s2.Rows.Add(new string[] { "2026-11-05 15:00:02", "PSEXESVC" });
            sheets.Add(s2);

            string xlsxPath = System.IO.Path.Combine(tmpDir, "export.xlsx");
            Exporters.WriteXlsx(xlsxPath, sheets);
            Check("XLSX 文件已生成", System.IO.File.Exists(xlsxPath));
            bool zipOk = false, hasSheet1 = false, hasSheet2 = false, hasWorkbook = false, xmlEscaped = false, cjkSheetName = false;
            if (System.IO.File.Exists(xlsxPath))
            {
                using (System.IO.Compression.ZipArchive zip =
                    System.IO.Compression.ZipFile.OpenRead(xlsxPath))
                {
                    zipOk = zip.Entries.Count >= 6;
                    foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries)
                    {
                        if (e.FullName == "xl/worksheets/sheet1.xml") hasSheet1 = true;
                        if (e.FullName == "xl/worksheets/sheet2.xml") hasSheet2 = true;
                        if (e.FullName == "xl/workbook.xml")
                        {
                            hasWorkbook = true;
                            using (System.IO.Stream st = e.Open())
                            using (System.IO.StreamReader sr = new System.IO.StreamReader(st, new System.Text.UTF8Encoding(false)))
                            {
                                string wb = sr.ReadToEnd();
                                cjkSheetName = wb.IndexOf("登录成功 (4624)", StringComparison.Ordinal) >= 0;
                            }
                        }
                        if (e.FullName == "xl/worksheets/sheet1.xml")
                        {
                            using (System.IO.Stream st = e.Open())
                            using (System.IO.StreamReader sr = new System.IO.StreamReader(st, new System.Text.UTF8Encoding(false)))
                            {
                                string sx = sr.ReadToEnd();
                                xmlEscaped = sx.IndexOf("含 &amp; &lt; &gt; &quot; 特殊字符", StringComparison.Ordinal) >= 0;
                            }
                        }
                    }
                }
            }
            Check("XLSX 是合法 ZIP 且含全部部件", zipOk && hasWorkbook && hasSheet1 && hasSheet2);
            Check("XLSX 工作表名支持中文", cjkSheetName);
            Check("XLSX 单元格已做 XML 转义", xmlEscaped);

            string csvPath = System.IO.Path.Combine(tmpDir, "export.csv");
            Exporters.WriteCsv(csvPath, s1);
            string csvBack = System.IO.File.ReadAllText(csvPath, new System.Text.UTF8Encoding(true));
            Check("CSV 导出带 BOM（Excel 中文不乱码）",
                System.IO.File.ReadAllBytes(csvPath).Length > 3
                && System.IO.File.ReadAllBytes(csvPath)[0] == 0xEF
                && System.IO.File.ReadAllBytes(csvPath)[1] == 0xBB
                && System.IO.File.ReadAllBytes(csvPath)[2] == 0xBF);
            Check("CSV 导出后能原样解析回来",
                Exporters.ParseCsv(csvBack).Count == 3
                && Exporters.ParseCsv(csvBack)[2][1].IndexOf("含 & < >", StringComparison.Ordinal) == 0);

            try { System.IO.Directory.Delete(tmpDir, true); } catch (Exception) { }

            // ---------------------------------------------------------- [11] 五种输出格式
            Console.WriteLine("[11] 五种输出格式（NAT / CSV / TSV / XML / W3C）");
            string fmtQuery = "SELECT TOP 2 TimeGenerated, EXTRACT_TOKEN(Strings,0,'|') AS ServiceName"
                + " FROM System WHERE EventID = 7045";
            QueryItem fmtItem = new QueryItem();
            fmtItem.Id = "fmt_probe";
            fmtItem.Label = "格式自检";
            fmtItem.LogName = "System";
            fmtItem.Query = fmtQuery;
            string[] formats = { "NAT", "CSV", "TSV", "XML", "W3C" };
            LogParserRunner fmtRunner = new LogParserRunner();
            for (int fi = 0; fi < formats.Length; fi++)
            {
                LogParserRunner.Options fopt = new LogParserRunner.Options();
                fopt.LogParserPath = lp;
                fopt.OutputFormat = formats[fi];
                fopt.ExtraArguments = "-rtp:-1"; // NAT 专用；其余格式必须被自动剥掉
                fopt.TimeoutSeconds = 300;
                int rows11;
                QueryResult fr;
                using (System.Threading.CancellationTokenSource c11 = new System.Threading.CancellationTokenSource())
                {
                    fr = fmtRunner.RunAsync(fopt, fmtItem, c11.Token).Result;
                }
                rows11 = CountRowsAny(fr.Output, formats[fi]);
                Console.WriteLine("     -o:" + formats[fi] + "  ok=" + fr.Ok + "  行数=" + rows11
                    + "  长度=" + (fr.Output == null ? 0 : fr.Output.Length)
                    + (fr.Ok ? "" : "  错误=" + FirstLine(fr.Error)));
                Check("输出格式 " + formats[fi] + " 可执行（-rtp 自动剥离）", fr.Ok);
                Check("输出格式 " + formats[fi] + " 拿到 2 行", rows11 == 2);
                Check("输出格式 " + formats[fi] + " 无替换符乱码", (fr.Output ?? "").IndexOf('\uFFFD') < 0);
                if (formats[fi] == "XML")
                    Check("XML 里没有 ANSI 统计块残渣",
                        (fr.Output ?? "").IndexOf("Statistics", StringComparison.OrdinalIgnoreCase) < 0);
            }

            // 空结果：每种格式都必须落到同一句占位文本上（XML 特别容易翻车：
            // 没有命中行时 LogParser 不写 XML，stdout 只剩 ANSI 的 Statistics 块，
            // 按 UTF-16 解码就成了"埭瑯鐚欏…"这种乱码汉字）
            QueryItem emptyItem = new QueryItem();
            emptyItem.Id = "empty_probe";
            emptyItem.Label = "空结果自检";
            emptyItem.LogName = "System";
            emptyItem.Query = "SELECT TimeGenerated FROM System WHERE EventID = 9999";
            for (int fi = 0; fi < formats.Length; fi++)
            {
                LogParserRunner.Options eopt = new LogParserRunner.Options();
                eopt.LogParserPath = lp;
                eopt.OutputFormat = formats[fi];
                eopt.ExtraArguments = "-rtp:-1";
                eopt.TimeoutSeconds = 300;
                QueryResult er;
                using (System.Threading.CancellationTokenSource ce = new System.Threading.CancellationTokenSource())
                {
                    er = fmtRunner.RunAsync(eopt, emptyItem, ce.Token).Result;
                }
                bool placeholder = (er.Output ?? "").IndexOf("没有匹配记录", StringComparison.Ordinal) >= 0;
                Console.WriteLine("     空结果 -o:" + formats[fi] + "  ok=" + er.Ok + "  输出=<" + FirstLine(er.Output) + ">");
                Check("空结果 " + formats[fi] + " 显示占位文本", er.Ok && placeholder);
                Check("空结果 " + formats[fi] + " 没有乱码", (er.Output ?? "").IndexOf('\uFFFD') < 0
                    && (er.Output ?? "").IndexOf("Statistics", StringComparison.OrdinalIgnoreCase) < 0);
            }

            LogParserRunner.Options optNat = new LogParserRunner.Options();
            optNat.LogParserPath = lp;
            optNat.OutputFormat = "NAT";
            optNat.ExtraArguments = "-rtp:-1";
            Check("NAT 保留 -rtp", LogParserRunner.BuildArguments(optNat, fmtQuery).IndexOf("-rtp:-1", StringComparison.Ordinal) > 0);
            LogParserRunner.Options optXml = new LogParserRunner.Options();
            optXml.LogParserPath = lp;
            optXml.OutputFormat = "XML";
            optXml.ExtraArguments = "-rtp:-1";
            Check("非 NAT 时 -rtp 被剥离", LogParserRunner.BuildArguments(optXml, fmtQuery).IndexOf("-rtp", StringComparison.OrdinalIgnoreCase) < 0);

            // ---------------------------------------------------------- [12] 手动命令（命令行解析 + 真跑一条）
            Console.WriteLine("[12] 手动命令窗口（命令行解析与执行）");
            string mArgs, mSql;
            Check("只写 SQL 也能解析",
                LogParserRunner.SplitCommandLine("SELECT TimeGenerated FROM System WHERE EventID = 7045", out mArgs, out mSql)
                && mArgs.Length == 0 && mSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("     只写 SQL → 参数=<" + mArgs + "> SQL=<" + FirstLine(mSql) + ">");

            // 整条命令行（用户直接从别处粘贴的情形），可执行文件名与包裹引号都要去掉
            string fullCmd = "LogParser.exe -i:EVT -o:NAT -rtp:-1 \"SELECT TimeGenerated, EventID FROM System WHERE EventID = 7045\"";
            Check("整条命令行能拆出参数与 SQL",
                LogParserRunner.SplitCommandLine(fullCmd, out mArgs, out mSql)
                && mArgs.IndexOf("-rtp:-1", StringComparison.Ordinal) >= 0
                && mSql.IndexOf("LogParser.exe", StringComparison.OrdinalIgnoreCase) < 0);
            Console.WriteLine("     整条命令行 → 参数=<" + mArgs + "> SQL=<" + FirstLine(mSql) + ">");

            // 带空格的引号路径不能被切成两段（此阶段 -i: 还留在参数字符串里，由 BuildArguments 提取）
            string pArgs, pSql;
            LogParserRunner.SplitCommandLine("-i:\"C:\\Program Files\\x\\a.evtx\" -o:CSV SELECT TimeGenerated FROM a_evtx "
                + "WHERE EventID = 4624", out pArgs, out pSql);
            Check("引号里带空格的 -i: 路径不被切断",
                pArgs.IndexOf("\"C:\\Program Files\\x\\a.evtx\"", StringComparison.Ordinal) >= 0
                && pArgs.IndexOf("-o:CSV", StringComparison.OrdinalIgnoreCase) >= 0);
            string pathLeft;
            string pathVal = LogParserRunner.ExtractOptionValue(pArgs, "-i:", out pathLeft);
            Check("引号路径能被 ExtractOptionValue 取出",
                pathVal.IndexOf("a.evtx", StringComparison.OrdinalIgnoreCase) >= 0
                && pathVal.IndexOf("Program Files", StringComparison.Ordinal) >= 0);
            Console.WriteLine("     引号路径 → 取出=<" + pathVal + "> 剩余=<" + pathLeft + ">");

            // -i: / -o: 从附加参数里提出来后要提到固定位置，不能留两个 -o:
            LogParserRunner.Options mopt = new LogParserRunner.Options();
            mopt.LogParserPath = lp;
            mopt.OutputFormat = "NAT";
            mopt.ExtraArguments = "-o:CSV -st:off";
            string mBuilt = LogParserRunner.BuildArguments(mopt, "SELECT TimeGenerated FROM System");
            Check("命令行里的 -o: 覆盖下拉框且只有一个", mBuilt.StartsWith("-i:EVT -o:CSV ", StringComparison.Ordinal)
                && mBuilt.IndexOf("-o:NAT", StringComparison.Ordinal) < 0);
            Check("EffectiveFormat 取附加参数里的 -o:", LogParserRunner.EffectiveFormat(mopt) == "CSV");
            Console.WriteLine("     拼装结果 → " + mBuilt);

            LogParserRunner.Options mopt2 = new LogParserRunner.Options();
            mopt2.LogParserPath = lp;
            mopt2.OutputFormat = "NAT";
            mopt2.ExtraArguments = "-i:EVTX -rtp:-1";
            string mBuilt2 = LogParserRunner.BuildArguments(mopt2, "SELECT TimeGenerated FROM System");
            Check("命令行里的 -i: 覆盖默认 EVT", mBuilt2.StartsWith("-i:EVTX -o:NAT", StringComparison.Ordinal));
            Console.WriteLine("     输入格式覆盖 → " + mBuilt2);

            // 真跑一条：手动命令最终走的就是 RunAsync，这里验证 SQL 能出结果
            QueryItem manItem = new QueryItem();
            manItem.Id = "manual";
            manItem.Label = "手动命令自检";
            manItem.LogName = "(手动输入)";
            manItem.Query = "SELECT TOP 3 TimeGenerated, EventID, EXTRACT_TOKEN(Strings,0,'|') AS 服务名"
                + " FROM System WHERE EventID = 7045";
            LogParserRunner.Options manOpt = new LogParserRunner.Options();
            manOpt.LogParserPath = lp;
            manOpt.OutputFormat = "NAT";
            manOpt.ExtraArguments = "-rtp:-1";
            manOpt.TimeoutSeconds = 300;
            QueryResult mr;
            using (System.Threading.CancellationTokenSource cm = new System.Threading.CancellationTokenSource())
            {
                mr = fmtRunner.RunAsync(manOpt, manItem, cm.Token).Result;
            }
            int manRows = CountRowsAny(mr.Output, "NAT");
            Console.WriteLine("     手动命令执行 ok=" + mr.Ok + " 行数=" + manRows + " 耗时="
                + mr.Elapsed.TotalSeconds.ToString("0.00") + "s" + (mr.Ok ? "" : " 错误=" + FirstLine(mr.Error)));
            Check("手动命令能跑出结果", mr.Ok && manRows >= 1);
            Check("手动命令结果无乱码", (mr.Output ?? "").IndexOf('\uFFFD') < 0);

            Console.WriteLine(_fail == 0 ? "=== 全部通过 ===" : "=== 存在 " + _fail + " 项失败 ===");
            return _fail;
        }

        /// <summary>按格式数行：NAT 跳表头与分列线，W3C 跳 # 注释，XML 数 &lt;ROW&gt;，CSV/TSV 跳表头。</summary>
        private static int CountRowsAny(string body, string format)
        {
            if (string.IsNullOrEmpty(body)) return 0;
            string fmt = LogParserRunner.NormalizeFormat(format);
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            if (fmt == "XML")
            {
                int nx = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("<ROW", StringComparison.OrdinalIgnoreCase) && t.EndsWith(">", StringComparison.Ordinal)) nx++;
                }
                return nx;
            }
            if (fmt == "W3C")
            {
                int nw = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    nw++;
                }
                return nw;
            }
            return CountRows(body);
        }

        /// <summary>数 NAT 输出的数据行：跳过表头、--- 分列线和空行。</summary>
        private static int CountRows(string body)
        {
            if (string.IsNullOrEmpty(body)) return 0;
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            int n = 0;
            bool header = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("---", StringComparison.Ordinal)) { header = false; continue; }
                if (header) { header = false; continue; }
                n++;
            }
            return n;
        }

        private static bool AllItemsValid(List<QueryItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                QueryItem it = items[i];
                if (string.IsNullOrEmpty(it.Id) || string.IsNullOrEmpty(it.Label) || string.IsNullOrEmpty(it.Query))
                    return false;
                if (it.Query.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) != 0) return false;
            }
            return true;
        }

        private static bool UniqueIds(List<QueryItem> items)
        {
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i++)
            {
                if (seen.ContainsKey(items[i].Id)) { Console.WriteLine("    ! 重复 id: " + items[i].Id); return false; }
                seen[items[i].Id] = true;
            }
            return true;
        }

        private static string NormText(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);
            return s.TrimEnd();
        }

        private static string StripSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (s[i] != ' ' && s[i] != '\t') sb.Append(s[i]);
            return sb.ToString();
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            s = s.Replace("\r\n", "\n").Split('\n')[0].Trim();
            return s.Length > 110 ? s.Substring(0, 110) : s;
        }

        private static void Check(string name, bool cond)
        {
            Console.WriteLine("    " + (cond ? "PASS " : "FAIL ") + name);
            if (!cond) _fail++;
        }
    }
}