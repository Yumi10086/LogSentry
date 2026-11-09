using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LogSentry
{
    /// <summary>单条查询的执行结果。</summary>
    public sealed class QueryResult
    {
        public QueryItem Item;
        public bool Ok;
        public string Output = "";
        public string RawOutput = "";
        public string Error = "";
        public long ElementCount = -1;
        public TimeSpan Elapsed;
        public bool TimedOut;
    }

    /// <summary>
    /// LogParser 调用引擎。
    /// 要点：CreateNoWindow 隐藏黑框；异步读取标准输出+错误输出（不阻塞 UI）；
    /// 中文按 OEM 代码页（简中系统为 GBK/936）解码；超时可取消（杀进程树）。
    /// </summary>
    public sealed class LogParserRunner
    {
        private const string ParamToken = "%PARAM%";
        private const int MaxRedirectOutputChars = 8 * 1024 * 1024;

        private static readonly Regex StatsRegex = new Regex(
            @"^\s*Elements\s+(processed|output)\s*:\s*(-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex StatsCjkRegex = new Regex(
            @"^\s*已处理元素\s*:\s*(-?\d+)|^\s*已输出元素\s*:\s*(-?\d+)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public sealed class Options
        {
            public string LogParserPath;
            public string OutputFormat = "NAT";
            public bool UseLocalTime = true;
            public string ExtraArguments = "";
            public int TimeoutSeconds = 300;
            /// <summary>用户填写的自定义参数，用于把配置里的 %PARAM% 替换进去。</summary>
            public string UserParam = "";
            /// <summary>时间筛选下界（含）。null = 不限。</summary>
            public DateTime? TimeFrom;
            /// <summary>时间筛选上界（含）。null = 不限。</summary>
            public DateTime? TimeTo;
        }

        // ---------------------------------------------------------------- 编码

        private static Encoding _outputEncoding;
        /// <summary>
        /// LogParser 控制台输出使用 OEM 代码页（简中系统 = 936/GBK）。
        /// 若设置错误会直接乱码，所以这里按“脚本显示语言”与系统默认 OEM 代码页推断，
        /// 并用 Encoding.Default 兜底，绝不抛异常终止分析。
        /// </summary>
        public static Encoding OutputEncoding
        {
            get
            {
                if (_outputEncoding == null)
                {
                    List<int> candidates = new List<int>();
                    candidates.Add(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                    candidates.Add(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
                    candidates.Add(936);
                    candidates.Add(Encoding.Default.CodePage);
                    foreach (int cp in candidates)
                    {
                        try
                        {
                            _outputEncoding = Encoding.GetEncoding(cp);
                            break;
                        }
                        catch (Exception) { }
                    }
                    if (_outputEncoding == null) _outputEncoding = Encoding.Default;
                }
                return _outputEncoding;
            }
        }

        public static void OverrideEncoding(Encoding enc)
        {
            if (enc != null) _outputEncoding = enc;
        }

        // ---------------------------------------------------------------- 命令行

        /// <summary>
        /// 拼接参数。使用 Arguments 字符串（非 ArgumentList），确保 .NET Framework 4.x 下可用。
        /// 附加参数里若自带 -i: / -o:，以它为准（手动命令窗口要能跑 EVTX/文本/其它输入格式），
        /// 并且把这两个参数提到固定位置 —— 否则命令行里会出现两个 -o:，LogParser 用哪个不好预期。
        /// </summary>
        public static string BuildArguments(Options opt, string query)
        {
            string fmt = NormalizeFormat(opt.OutputFormat);
            string input = "EVT";
            string extra = (opt.ExtraArguments ?? "").Trim();
            string rest;
            string v = ExtractOptionValue(extra, "-i:", out rest);
            if (v.Length > 0) { input = v; extra = rest; }
            v = ExtractOptionValue(extra, "-o:", out rest);
            if (v.Length > 0) { fmt = NormalizeFormat(v); extra = rest; }
            // -rtp 是 NAT 专用参数：-o:CSV|TSV|XML 带着它会直接
            // `Error: invalid parameter "rtp"`（exit 87），所以在非 NAT 格式下自动剥掉。
            if (!IsNat(fmt)) extra = StripNatOnlyArguments(extra);
            StringBuilder sb = new StringBuilder();
            sb.Append("-i:").Append(input);
            sb.Append(" -o:").Append(fmt);
            if (extra.Length > 0) sb.Append(' ').Append(extra);
            sb.Append(" \"").Append(EscapeForDoubleQuotes(query ?? "")).Append('"');
            return sb.ToString();
        }

        /// <summary>生效的输出格式：附加参数里的 -o: 优先于下拉框（手动命令会用到）。</summary>
        public static string EffectiveFormat(Options opt)
        {
            if (opt == null) return "NAT";
            string fmt = NormalizeFormat(opt.OutputFormat);
            string rest;
            string v = ExtractOptionValue((opt.ExtraArguments ?? "").Trim(), "-o:", out rest);
            return v.Length > 0 ? NormalizeFormat(v) : fmt;
        }

        /// <summary>
        /// 从参数串里摘出第一段以 prefix 开头的词，返回它的值（去掉包裹的引号，如 `-i:"C:\a b.evtx"` → `C:\a b.evtx`），
        /// remaining 为剩下的参数。引号内的空格不参与切分，带空格的路径不会被切断。
        /// </summary>
        public static string ExtractOptionValue(string extra, string prefix, out string remaining)
        {
            remaining = extra ?? "";
            if (remaining.Length == 0) return "";
            List<string> toks = TokenizeArguments(remaining);
            StringBuilder keep = new StringBuilder();
            string found = null;
            for (int i = 0; i < toks.Count; i++)
            {
                string t = toks[i];
                if (found == null && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    found = t.Substring(prefix.Length).Trim().Trim('"');
                    continue;
                }
                if (keep.Length > 0) keep.Append(' ');
                keep.Append(t);
            }
            remaining = keep.ToString();
            return found ?? "";
        }

        /// <summary>按空白切分参数串，但双引号内的内容（含空格）算一个词。</summary>
        public static List<string> TokenizeArguments(string s)
        {
            List<string> toks = new List<string>();
            if (string.IsNullOrEmpty(s)) return toks;
            StringBuilder cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    cur.Append(c);
                    continue;
                }
                if (!inQuote && (c == ' ' || c == '\t' || c == '\r' || c == '\n'))
                {
                    if (cur.Length > 0) { toks.Add(cur.ToString()); cur.Length = 0; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) toks.Add(cur.ToString());
            return toks;
        }

        /// <summary>
        /// 手动命令窗口的输入解析，三种写法都吃：
        ///   1) 只写 SQL：SELECT ... FROM Security WHERE EventID = 4624
        ///   2) 整条命令行：LogParser.exe -i:EVT -o:NAT "SELECT ... FROM ..."
        ///   3) 参数 + SQL：-i:EVTX -o:CSV "SELECT ..."
        /// 返回 false 表示里面找不到 SELECT，调用方应当提示用户。
        /// </summary>
        public static bool SplitCommandLine(string text, out string extraArgs, out string query)
        {
            extraArgs = "";
            query = "";
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.Length == 0) return false;

            // 去掉开头的可执行文件名（LogParser.exe / 带路径 / 被引号包着）
            while (true)
            {
                string rest;
                string first = FirstToken(s, out rest);
                if (first.Length == 0) break;
                string bare = first.Trim('"').Trim();
                if (!bare.EndsWith("logparser.exe", StringComparison.OrdinalIgnoreCase)) break;
                s = rest.Trim();
                if (s.Length == 0) break;
            }
            if (s.Length == 0) return false;

            int sel = FindTopLevelKeyword(s, "SELECT");
            if (sel < 0) return false;

            extraArgs = s.Substring(0, sel).Trim().Trim('"').Trim();
            string q = s.Substring(sel).Trim();
            // 整条命令行一般把 SQL 用一对双引号包起来，去掉这对包裹引号
            if (q.Length > 1 && q[0] == '"' && q[q.Length - 1] == '"')
                q = q.Substring(1, q.Length - 2).Trim();
            else if (q.Length > 0 && q[q.Length - 1] == '"')
                q = q.Substring(0, q.Length - 1).TrimEnd();
            if (q.Length == 0) return false;
            query = q;
            return true;
        }

        /// <summary>取第一个词（双引号内的空格不算分隔），rest 为剩余部分。</summary>
        private static string FirstToken(string s, out string rest)
        {
            rest = "";
            if (string.IsNullOrEmpty(s)) return "";
            int i = 0;
            bool inQuote = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { inQuote = !inQuote; i++; continue; }
                if (!inQuote && (c == ' ' || c == '\t' || c == '\r' || c == '\n')) break;
                i++;
            }
            string tok = s.Substring(0, i);
            rest = i < s.Length ? s.Substring(i).Trim() : "";
            return tok;
        }

        /// <summary>输出格式规范化：空值当 NAT，统一转大写。</summary>
        public static string NormalizeFormat(string fmt)
        {
            if (string.IsNullOrEmpty(fmt)) return "NAT";
            string s = fmt.Trim().ToUpperInvariant();
            return s.Length == 0 ? "NAT" : s;
        }

        public static bool IsNat(string fmt)
        {
            return string.Equals(NormalizeFormat(fmt), "NAT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// LogParser 的 -o:XML 输出是 UTF-16LE（带 FF FE BOM，声明 encoding="ISO-10646-UCS-2"），
        /// 其余格式是本地代码页（CP936）。用错编码读出来就是 `? x m l` 那种一个字符一个空格的乱码。
        /// </summary>
        public static Encoding EncodingForFormat(string fmt)
        {
            return string.Equals(NormalizeFormat(fmt), "XML", StringComparison.OrdinalIgnoreCase)
                ? Encoding.Unicode
                : OutputEncoding;
        }

        /// <summary>
        /// 去掉只对 NAT 有效的参数。实测 `-o:CSV -rtp:-1` 直接报
        /// `Error: invalid parameter "rtp"`（exit 87），所以导出换格式时必须先剥掉 -rtp。
        /// </summary>
        public static string StripNatOnlyArguments(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return "";
            string[] parts = extra.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("-rtp", StringComparison.OrdinalIgnoreCase)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        /// <summary>导出（CSV/XML/TSV 等）时用的附加参数：剥掉 NAT 专用参数，并关掉尾部统计块。</summary>
        public static string BuildExportExtraArguments(string extra, bool keepStatistics)        {
            string s = StripNatOnlyArguments(extra);
            if (!keepStatistics)
            {
                bool hasStats = s.IndexOf("-stats:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasStats)
                {
                    if (s.Length > 0) s += " ";
                    s += "-stats:off";
                }
            }
            return s;
        }

        /// <summary>查询已在双引号内：内部双引号转义为 \\"，反斜杠加倍。</summary>
        private static string EscapeForDoubleQuotes(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n') { sb.Append(' '); continue; }
                if (c == '\\') { sb.Append("\\\\"); continue; }
                if (c == '"') { sb.Append("\\\""); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static string ApplyParam(string query, string param)
        {
            if (string.IsNullOrEmpty(query)) return "";
            if (query.IndexOf(ParamToken, StringComparison.OrdinalIgnoreCase) < 0) return query;
            return query.Replace(ParamToken, param ?? "");
        }

        public static bool NeedsParam(QueryItem item)
        {
            return item != null && item.Query != null
                && item.Query.IndexOf(ParamToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------- W3C 落地

        /// <summary>
        /// 在 SELECT 和顶层 FROM 之间插入 `INTO '文件'`。
        /// 为什么要这么绕：-o:W3C 直接写 stdout 会卡住（进程不退出也不产出行），
        /// 但同样的查询加上 INTO 落地就秒回，所以 W3C 走临时文件再读回来。
        /// 查询里已经有 INTO 时原样返回。
        /// </summary>
        public static string InsertIntoClause(string query, string filePath)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(filePath)) return null;
            int fromIdx = FindTopLevelKeyword(query, "FROM");
            if (fromIdx < 0) return null;
            int intoIdx = FindTopLevelKeyword(query, "INTO");
            if (intoIdx >= 0 && intoIdx < fromIdx) return query; // 已经指定了输出文件
            string quoted = "'" + filePath.Replace("'", "''") + "'";
            return query.Substring(0, fromIdx) + "INTO " + quoted + " " + query.Substring(fromIdx);
        }

        /// <summary>找顶层关键字（跳过单引号字符串里的内容），返回起始下标；找不到返回 -1。</summary>
        public static int FindTopLevelKeyword(string sql, string keyword)
        {
            if (string.IsNullOrEmpty(sql) || string.IsNullOrEmpty(keyword)) return -1;
            bool inQuote = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (c == '\'')
                {
                    if (inQuote && i + 1 < sql.Length && sql[i + 1] == '\'') { i++; continue; }
                    inQuote = !inQuote;
                    continue;
                }
                if (inQuote) continue;
                if (i + keyword.Length > sql.Length) break;
                if (string.Compare(sql, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                bool leftOk = i == 0 || !IsIdentChar(sql[i - 1]);
                int after = i + keyword.Length;
                bool rightOk = after >= sql.Length || !IsIdentChar(sql[after]);
                if (leftOk && rightOk) return i;
            }
            return -1;
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';
        }

        // ---------------------------------------------------------------- 时间筛选

        /// <summary>TO_TIMESTAMP 的格式串，和界面上 DateTimePicker 的显示格式一致。</summary>
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>
        /// 生成 TimeGenerated 的过滤条件。LogParser 的 TimeGenerated 是 T（timestamp）类型，
        /// 直接和字符串字面量比较也能自动转换，但显式套 TO_TIMESTAMP 更稳（不受区域设置影响）。
        /// </summary>
        public static string BuildTimeCondition(DateTime? from, DateTime? to)
        {
            StringBuilder sb = new StringBuilder();
            if (from.HasValue)
            {
                sb.Append("TimeGenerated >= TO_TIMESTAMP('")
                  .Append(from.Value.ToString(TimeFormat))
                  .Append("', '").Append(TimeFormat).Append("')");
            }
            if (to.HasValue)
            {
                if (sb.Length > 0) sb.Append(" AND ");
                sb.Append("TimeGenerated <= TO_TIMESTAMP('")
                  .Append(to.Value.ToString(TimeFormat))
                  .Append("', '").Append(TimeFormat).Append("')");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把时间条件插进查询的 WHERE 里（没有 WHERE 就补一个），
        /// 插在 GROUP BY / ORDER BY / HAVING 之前，保证语法成立。
        /// 扫描时跳过单引号字符串，避免把 WHERE 写在 LIKE '%WHERE%' 里被误判。
        /// </summary>
        public static string InjectTimeFilter(string query, DateTime? from, DateTime? to)
        {
            string cond = BuildTimeCondition(from, to);
            if (cond.Length == 0) return query ?? "";
            if (string.IsNullOrEmpty(query)) return query ?? "";

            int len = query.Length;
            int whereEnd = -1;   // WHERE 关键字结束位置
            int tail = len;      // 第一个顶层 GROUP / ORDER / HAVING 的起始位置

            for (int i = 0; i < len; i++)
            {
                char c = query[i];
                if (c == '\'')
                {
                    // 跨过字符串字面量，'' 是转义的单引号
                    i++;
                    while (i < len)
                    {
                        if (query[i] == '\'')
                        {
                            if (i + 1 < len && query[i + 1] == '\'') { i += 2; continue; }
                            break;
                        }
                        i++;
                    }
                    continue;
                }
                if (!char.IsLetter(c)) continue;

                int start = i;
                while (i < len && (char.IsLetterOrDigit(query[i]) || query[i] == '_')) i++;
                string word = query.Substring(start, i - start).ToUpperInvariant();

                if (word == "WHERE") whereEnd = i;
                else if (word == "GROUP" || word == "ORDER" || word == "HAVING")
                {
                    if (whereEnd >= 0 && tail == len) tail = start;
                }
                i--;
            }

            string head = query.Substring(0, tail).TrimEnd();
            string back = query.Substring(tail);
            string injected = head + (whereEnd >= 0 ? " AND " : " WHERE ") + cond;
            if (back.Length > 0) injected += " " + back.TrimStart();
            return injected;
        }

        /// <summary>%PARAM% 替换 + 时间筛选，得到真正要交给 LogParser 的语句。</summary>
        public static string EffectiveQuery(Options opt, string rawQuery)
        {
            string q = ApplyParam(rawQuery, opt == null ? null : opt.UserParam);
            if (q == null || q.Trim().Length == 0) q = rawQuery ?? "";
            if (opt == null) return q;
            return InjectTimeFilter(q, opt.TimeFrom, opt.TimeTo);
        }

        // ---------------------------------------------------------------- 执行

        /// <summary>异步执行一条查询。永不抛异常，失败信息写进 QueryResult。</summary>
        public async Task<QueryResult> RunAsync(Options opt, QueryItem item, CancellationToken token)
        {
            QueryResult r = new QueryResult();
            r.Item = item;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                if (opt == null || string.IsNullOrEmpty(opt.LogParserPath) || !File.Exists(opt.LogParserPath))
                {
                    r.Error = "找不到 LogParser.exe：" + (opt == null ? "(未设置)" : opt.LogParserPath);
                    return r;
                }

                string query = EffectiveQuery(opt, item.Query);
                string fmtName = EffectiveFormat(opt);

                // -o:W3C 写 stdout 会卡死（实测 204 行时进程空转不退，写文件却秒回），
                // 所以 W3C 改成 SELECT ... INTO 'tmp.log' 落地再读回来。
                string w3cTempFile = null;
                if (fmtName == "W3C")
                {
                    w3cTempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "LogSentry_w3c_" + Guid.NewGuid().ToString("N") + ".log");
                    string withInto = InsertIntoClause(query, w3cTempFile);
                    if (withInto == null)
                    {
                        r.Error = "查询里找不到顶层 FROM，无法为 W3C 输出指定落地文件";
                        return r;
                    }
                    query = withInto;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = opt.LogParserPath;
                psi.Arguments = BuildArguments(opt, query);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true; // 阻止 LogParser 交互式等待输入
                psi.WorkingDirectory = Path.GetDirectoryName(opt.LogParserPath);
                // XML 输出是 UTF-16LE，其余是 CP936；用错编码读出来就是乱码
                Encoding outEnc = EncodingForFormat(fmtName);
                psi.StandardOutputEncoding = outEnc;
                psi.StandardErrorEncoding = outEnc;

                Process proc = new Process();
                proc.StartInfo = psi;

                if (!proc.Start())
                {
                    r.Error = "无法启动 LogParser.exe（被安全软件拦截或路径无权限）";
                    return r;
                }

                try { proc.StandardInput.Close(); } catch (Exception) { }

                Task<string> outTask = ReadAllAsync(proc.StandardOutput, MaxRedirectOutputChars);
                Task<string> errTask = ReadAllAsync(proc.StandardError, 1024 * 1024);

                TimeSpan timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds <= 0 ? 300 : opt.TimeoutSeconds);
                Task waitTask = Task.Factory.StartNew(delegate
                {
                    if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        try { proc.Kill(); } catch (Exception) { }
                        throw new TimeoutException("查询超时（" + (int)timeout.TotalSeconds + " 秒）已终止");
                    }
                });

                Task completed = await Task.WhenAny(waitTask, Task.Delay(System.Threading.Timeout.Infinite, token));
                if (completed != waitTask)
                {
                    // 用户点击停止
                    try { proc.Kill(); } catch (Exception) { }
                    r.TimedOut = true;
                    r.Error = "已取消";
                    await SafeString(outTask);
                    sw.Stop();
                    r.Elapsed = sw.Elapsed;
                    return r;
                }

                await waitTask; // 传播超时异常

                string stdout = await outTask;
                string stderr = await errTask;
                sw.Stop();
                r.Elapsed = sw.Elapsed;
                r.RawOutput = stdout ?? "";

                if (w3cTempFile != null)
                {
                    // W3C 的正文在落地文件里，stdout 只剩统计块
                    try
                    {
                        if (File.Exists(w3cTempFile))
                            r.RawOutput = File.ReadAllText(w3cTempFile, OutputEncoding) + "\r\n" + r.RawOutput;
                    }
                    catch (Exception) { }
                    try { File.Delete(w3cTempFile); } catch (Exception) { }
                    w3cTempFile = null;
                }

                int exitCode = -1;
                try { exitCode = proc.ExitCode; } catch (Exception) { }

                string errText = (stderr ?? "").Trim();
                bool failed = exitCode != 0 || LooksLikeFailure(r.RawOutput)
                    || (errText.Length > 0 && errText.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0);

                if (failed)
                {
                    CleanStandardOutput(r.RawOutput, out r.Output, fmtName);
                    r.Error = HumanizeError(BuildErrorDetail(r.Output, errText, exitCode));
                    r.Ok = false;
                }
                else
                {
                    CleanStandardOutput(r.RawOutput, out r.Output, fmtName);
                    r.Ok = true;
                    r.Error = "";
                }
            }
            catch (TimeoutException te)
            {
                sw.Stop();
                r.Ok = false;
                r.TimedOut = true;
                r.Elapsed = sw.Elapsed;
                r.Error = te.Message;
            }
            catch (Exception ex)
            {
                sw.Stop();
                r.Ok = false;
                r.Elapsed = sw.Elapsed;
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        private static async Task<string> SafeString(Task<string> t)
        {
            try { return await t; } catch (Exception) { return ""; }
        }

        private static async Task<string> ReadAllAsync(StreamReader reader, int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            char[] buf = new char[8192];
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                if (sb.Length < maxChars) sb.Append(buf, 0, Math.Min(n, maxChars - sb.Length));
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 输出清洗

        /// <summary>去掉 LogParser 的 Statistics 尾部统计块，只保留表格正文。</summary>
        public static void CleanStandardOutput(string raw, out string cleaned)
        {
            CleanStandardOutput(raw, out cleaned, null);
        }

        /// <summary>
        /// 按输出格式清洗。-o:XML 是 UTF-16 文档，尾部的 Statistics 块却是 ANSI 写的，
        /// 按 UTF-16 读出来是一串乱码汉字，所以 XML 改成在 &lt;/ROOT&gt; 处截断。
        /// </summary>
        public static void CleanStandardOutput(string raw, out string cleaned, string format)
        {
            cleaned = raw ?? "";
            if (cleaned.Length == 0)
            {
                // 空 stdout 和"只剩统计块"是一回事：都算没命中
                cleaned = "(该检测项在当前日志中没有匹配记录)";
                return;
            }

            if (string.Equals(NormalizeFormat(format), "XML", StringComparison.OrdinalIgnoreCase))
            {
                if (cleaned.Length > 0 && cleaned[0] == '\uFEFF') cleaned = cleaned.Substring(1);
                int end = cleaned.LastIndexOf("</ROOT>", StringComparison.OrdinalIgnoreCase);
                if (end > 0)
                {
                    cleaned = cleaned.Substring(0, end + "</ROOT>".Length).TrimEnd();
                    return;
                }
                // 没有命中行时 LogParser 根本不写 XML，stdout 里只剩 ANSI 的 Statistics 块。
                // 这段按 UTF-16 解码会变成一串乱码汉字（埭瑯鐚欏…），必须先丢掉再判空。
                cleaned = "";
                if (IsEmptyResult(cleaned)) cleaned = "(该检测项在当前日志中没有匹配记录)";
                return;
            }

            string[] markers = { "Statistics:", "统计:" };
            foreach (string m in markers)
            {
                int idx = cleaned.LastIndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    // 往前吞掉前置空行
                    int start = idx;
                    while (start > 0 && (cleaned[start - 1] == '\n' || cleaned[start - 1] == '\r' || cleaned[start - 1] == ' '))
                        start--;
                    cleaned = cleaned.Substring(0, start);
                    break;
                }
            }
            cleaned = cleaned.TrimEnd();
            if (IsEmptyResult(cleaned)) cleaned = "(该检测项在当前日志中没有匹配记录)";
        }

        /// <summary>
        /// 把 NAT 输出里用来对齐的连续空格（≥2 个）压成两个空格。
        /// 为什么不按"列起点"硬切：LogParser 的列宽是按字符数算的，中文表头/中文值会让
        /// 宽度算少一格，于是个别长字段（比如 7045 的映像路径）会溢出到下一列里；
        /// 这时按固定列位切就会把路径从中间劈开（实测切出过 C:\WINDOWS\s ystem32\...）。
        /// 压缩空白不改变任何"词"，既保住内容又把整行收窄回可读宽度。
        /// </summary>
        public static string CollapsePadding(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            StringBuilder sb = new StringBuilder(body.Length);
            int spaces = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == ' ')
                {
                    spaces++;
                    continue;
                }
                if (c == '\t')
                {
                    spaces += 4;
                    continue;
                }
                if (spaces > 0)
                {
                    if (c == '\n' || c == '\r') { } // 行尾空白直接丢掉
                    else if (spaces >= 2) sb.Append("  ");
                    else sb.Append(' ');
                    spaces = 0;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>LogParser 无匹配行时 stdout 为空或只有空白。</summary>
        public static bool IsEmptyResult(string cleaned)
        {
            return string.IsNullOrEmpty(cleaned) || cleaned.Trim().Length == 0;
        }

        /// <summary>把 LogParser 的英文/系统错误翻译成一眼能懂的中文处置建议。</summary>
        public static string HumanizeError(string error)
        {
            if (string.IsNullOrEmpty(error)) return error;
            string e = error;
            if (e.IndexOf("客户端没有所需的特权", StringComparison.Ordinal) >= 0
                || e.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0)
            {
                return "权限不足：读取 Security 日志需要管理员权限。请右键“以管理员身份运行”，"
                    + "或确认 app.manifest 的 requireAdministrator 生效。原始信息：" + e;
            }
            if (e.IndexOf("unknown field", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "查询语句字段名写错（配置 queries.txt 里的列名不存在）。原始信息：" + e;
            }
            if (e.IndexOf("invalid parameter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "LogParser 参数无效（检查“参数与超时”页的附加参数）。原始信息：" + e;
            }
            if (e.IndexOf("Cannot open", StringComparison.OrdinalIgnoreCase) >= 0
                && e.IndexOf("event log", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "打不开该事件日志（服务未启用或日志名错误）。原始信息：" + e;
            }
            return e;
        }

        public static long ParseElementCount(string cleaned)
        {
            // 因为处于清理后文本，统计块已被裁掉，无法再取；保留接口给需要时用原始文本
            return -1;
        }

        private static bool LooksLikeFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            string head = output.TrimStart();
            return head.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("错误", StringComparison.Ordinal);
        }

        private static string ExtractBodyOnly(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            return output.Trim();
        }

        private static string BuildErrorDetail(string stdout, string stderr, int exitCode)
        {
            StringBuilder sb = new StringBuilder();
            string s = (stdout ?? "").Trim();
            if (s.Length > 0) sb.Append(s);
            string e = (stderr ?? "").Trim();
            if (e.Length > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(e);
            }
            if (sb.Length == 0) sb.Append("LogParser 退出码 ").Append(exitCode);
            string text = sb.ToString().Replace("\r\n", " ").Replace("\n", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text;
        }

        /// <summary>从 LogParser 原始输出中解析统计信息（原始文本含 Statistics 块）。</summary>
        public static long ParseProcessedCount(string rawOut)
        {
            if (string.IsNullOrEmpty(rawOut)) return -1;
            Match m = StatsRegex.Match(rawOut);
            if (m.Success)
            {
                long v;
                if (long.TryParse(m.Groups[2].Value, out v)) return v;
            }
            Match c = StatsCjkRegex.Match(rawOut);
            if (c.Success)
            {
                string g = c.Groups[1].Success ? c.Groups[1].Value : c.Groups[2].Value;
                long v;
                if (long.TryParse(g, out v)) return v;
            }
            return -1;
        }

        // ---------------------------------------------------------------- 自检

        public static bool TestLogParser(string path, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                message = "文件不存在";
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = path;
                psi.Arguments = "-h -i:EVT";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = OutputEncoding;
                psi.StandardErrorEncoding = OutputEncoding;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        message = "自检超时";
                        return false;
                    }
                    if (o.IndexOf("EVT", StringComparison.OrdinalIgnoreCase) >= 0
                        || o.IndexOf("Fields", StringComparison.OrdinalIgnoreCase) >= 0
                        || o.IndexOf("EventLog", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message = "LogParser 可用";
                        return true;
                    }
                    message = "输出异常：" + (o.Length > 120 ? o.Substring(0, 120) : o);
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = "无法执行：" + ex.Message;
                return false;
            }
        }
    }
}
