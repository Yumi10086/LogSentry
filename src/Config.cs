using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace LogSentry
{
    /// <summary>一条检测项：完全由 queries.txt 配置驱动，GUI 不硬编码任何项。</summary>
    public sealed class QueryItem
    {
        public string Id = "";
        public string Label = "";
        public string Group = "通用";
        public string Query = "";
        public string Note = "";
        public bool DefaultOn;
        public int Line;
        /// <summary>是否在标题里附带事件号（手动命令这种没有配置来源的项关掉，免得从 SQL 里误抓数字）。</summary>
        public bool ShowEventIds = true;

        /// <summary>来源："builtin" = 出厂 queries.txt；"user" = config\user-queries.txt。</summary>
        public string Source = "builtin";

        /// <summary>true 表示这一项不参与执行（内置项被用户在「自定义检测项」页签里停用）。</summary>
        public bool Disabled;

        /// <summary>
        /// 标题行显示名，含日志源与事件号，便于结果区定位。
        /// 日志源不再写在配置里，而是从 query 的 FROM 子句推断（见 LogSource.FromQuery）；
        /// 认不出来（没有 FROM、FROM 后面是文件路径、手动命令）就只显示事件号。
        /// </summary>
        public string DisplayLabel
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Label);
                string source = _sniffed ? _sniffedSource : SniffLogSource();
                string tag = source;
                if (ShowEventIds && !string.IsNullOrEmpty(EventIdList)) tag += " · " + EventIdList;
                if (!string.IsNullOrEmpty(tag)) sb.Append("  [").Append(tag).Append(']');
                return sb.ToString();
            }
        }

        private bool _sniffed;
        private string _sniffedSource = "";

        /// <summary>推断日志源并缓存（Query 是公开字段，改完后调 InvalidateLogSource 让缓存失效）。</summary>
        private string SniffLogSource()
        {
            string source = LogSource.FromQuery(Query);
            _sniffedSource = source;
            _sniffed = true;
            return source;
        }

        /// <summary>Query 被改写后调用，下次取 DisplayLabel 时重新推断。</summary>
        public void InvalidateLogSource()
        {
            _sniffed = false;
            _sniffedSource = "";
        }

        /// <summary>从查询语句中提取 WHERE EventID = x 的事件号，仅用于展示。</summary>
        public string EventIdList
        {
            get
            {
                if (string.IsNullOrEmpty(Query)) return "";
                List<string> ids = new List<string>();
                int i = 0;
                while (i < Query.Length)
                {
                    int p = Query.IndexOf("EventID", i, StringComparison.OrdinalIgnoreCase);
                    if (p < 0) break;
                    int j = p + 7;
                    // 跳过空白
                    while (j < Query.Length && char.IsWhiteSpace(Query[j])) j++;
                    // 可选比较符
                    bool range = false;
                    if (j < Query.Length && (Query[j] == '<' || Query[j] == '>' || Query[j] == '!' || Query[j] == '='))
                    {
                        if (Query[j] == '<' || Query[j] == '>') range = true;
                        while (j < Query.Length && (Query[j] == '=' || Query[j] == '<' || Query[j] == '>')) j++;
                        while (j < Query.Length && char.IsWhiteSpace(Query[j])) j++;
                    }
                    bool inList = j < Query.Length && Query[j] == '(';
                    if (inList) { j++; range = true; }
                    int start = j;
                    while (j < Query.Length && (char.IsDigit(Query[j]) || Query[j] == ',' || char.IsWhiteSpace(Query[j]))) j++;
                    string nums = Query.Substring(start, j - start).Trim();
                    if (nums.Length == 0) { i = p + 7; continue; }
                    if (range) ids.Add(nums.Replace(" ", ""));
                    else ids.Add(nums);
                    i = j;
                }
                return string.Join(",", ids.ToArray());
            }
        }

        public override string ToString() { return DisplayLabel; }
    }

    /// <summary>配置加载结果：成功项 + 失败行的错误信息（用于界面底部提示）。</summary>
    public sealed class ConfigLoadResult
    {
        public List<QueryItem> Items = new List<QueryItem>();
        public List<string> Errors = new List<string>();
        public string SourcePath = "";
    }

    /// <summary>
    /// queries.txt 解析器。
    /// 语法：每个条目以 [xx] 或 --- 或 === 起头；键值形如 "key: value"；
    /// 以 # 或 ; 开头的整行为注释；空行忽略。
    /// </summary>
    public static class ConfigLoader
    {
        /// <summary>读文件并解析；文件不存在会在 Errors 里留一条（出厂 queries.txt 用）。</summary>
        public static ConfigLoadResult Load(string path)
        {
            ConfigLoadResult result = new ConfigLoadResult();
            result.SourcePath = path;
            if (!System.IO.File.Exists(path))
            {
                result.Errors.Add("配置文件不存在：" + path);
                return result;
            }
            return ParseText(ReadTextAuto(path), path);
        }

        /// <summary>
        /// 读「可有可无」的配置文件：不存在就返回空结果、不报错。
        /// config\user-queries.txt 是用户自己攒出来的，没有它属于正常状态。
        /// </summary>
        public static ConfigLoadResult LoadOptional(string path)
        {
            ConfigLoadResult result = new ConfigLoadResult();
            result.SourcePath = path;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return result;
            return ParseText(ReadTextAuto(path), path);
        }

        /// <summary>
        /// 合并「出厂配置 + 用户配置」：
        ///  1) 同 Id 时用户项的字段覆盖出厂项（label/query/group/note 非空才算改写），
        ///     用户侧写 disabled: true 就是「停用这个内置项」；
        ///  2) 用户文件里独有的 Id 追加到末尾，Source = "user"；
        ///  3) 结果里保留 Disabled 项，界面负责灰显，执行时跳过。
        /// </summary>
        public static ConfigLoadResult LoadMerged(string builtinPath, string userPath)
        {
            ConfigLoadResult builtin = Load(builtinPath);
            ConfigLoadResult user = LoadOptional(userPath);

            ConfigLoadResult merged = new ConfigLoadResult();
            merged.SourcePath = builtinPath;

            Dictionary<string, QueryItem> userById = new Dictionary<string, QueryItem>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < user.Items.Count; i++)
            {
                QueryItem u = user.Items[i];
                u.Source = "user";
                if (!userById.ContainsKey(u.Id)) userById[u.Id] = u;
            }

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < builtin.Items.Count; i++)
            {
                QueryItem b = builtin.Items[i];
                b.Source = "builtin";
                QueryItem u;
                if (userById.TryGetValue(b.Id, out u))
                {
                    if (u.Disabled) b.Disabled = true;
                    if (u.Label.Length > 0) b.Label = u.Label;
                    if (u.Query.Length > 0) b.Query = u.Query;
                    if (u.Group.Length > 0) b.Group = u.Group;
                    if (u.Note.Length > 0) b.Note = u.Note;
                    b.InvalidateLogSource();
                }
                used.Add(b.Id);
                merged.Items.Add(b);
            }

            for (int i = 0; i < user.Items.Count; i++)
            {
                QueryItem u = user.Items[i];
                if (used.Contains(u.Id)) continue;   // 已在上面并进出厂项
                merged.Items.Add(u);
                used.Add(u.Id);
            }

            merged.Errors.AddRange(builtin.Errors);
            merged.Errors.AddRange(user.Errors);
            return merged;
        }

        /// <summary>「文本 → 检测项列表」的解析本体，自检与合并都复用它。</summary>
        public static ConfigLoadResult ParseText(string text, string sourcePath)
        {
            ConfigLoadResult result = new ConfigLoadResult();
            result.SourcePath = sourcePath;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            QueryItem cur = null;
            for (int n = 0; n < lines.Length; n++)
            {
                string raw = lines[n];
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == ';') continue;

                // 条目分隔符：[id] / [xxx] / --- / ===
                if ((line[0] == '[' && line[line.Length - 1] == ']')
                    || line.StartsWith("---", StringComparison.Ordinal)
                    || line.StartsWith("===", StringComparison.Ordinal))
                {
                    cur = new QueryItem();
                    cur.Line = n + 1;
                    if (line[0] == '[') cur.Id = line.Substring(1, line.Length - 2).Trim();
                    result.Items.Add(cur);
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    result.Errors.Add("第 " + (n + 1) + " 行无法解析：" + Clip(line));
                    continue;
                }

                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string val = line.Substring(colon + 1).Trim();

                if (key == "id")
                {
                    if (cur == null) { cur = new QueryItem(); cur.Line = n + 1; result.Items.Add(cur); }
                    cur.Id = val;
                    continue;
                }
                if (cur == null) { cur = new QueryItem(); cur.Line = n + 1; result.Items.Add(cur); }

                switch (key)
                {
                    case "label": cur.Label = val; break;
                    case "group": cur.Group = val; break;
                    case "note": cur.Note = val; break;
                    // log/logname 是旧格式的写法，现在日志源由 query 的 FROM 子句自动推断，
                    // 这里不再读取，遇到的旧行静默忽略（走 default 分支）。
                    case "query": cur.Query = val; break;
                    case "default":
                        bool on;
                        if (bool.TryParse(val, out on)) cur.DefaultOn = on;
                        else cur.DefaultOn = (val == "1" || val == "yes" || val == "on" || val == "是");
                        break;
                    // disabled: true 只在用户配置里出现，表示「停用这个 id 的项」。
                    case "disabled":
                        bool off;
                        if (bool.TryParse(val, out off)) cur.Disabled = off;
                        else cur.Disabled = (val == "1" || val == "yes" || val == "on" || val == "是");
                        break;
                    default: break; // 未知键静默忽略，方便写备注
                }
            }

            // 校验
            List<QueryItem> ok = new List<QueryItem>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Items.Count; i++)
            {
                QueryItem it = result.Items[i];
                string err = null;
                // 停用标记允许只写 [id] + disabled: true，字段留空也算合法
                if (it.Disabled && it.Id.Length > 0 && it.Query.Length == 0)
                {
                    ids.Add(it.Id);
                    ok.Add(it);
                    continue;
                }
                if (it.Query.Length == 0) err = "缺少 query";
                else if (it.Label.Length == 0) err = "缺少 label";
                else if (it.Id.Length == 0) err = "缺少 id";
                else if (ids.Contains(it.Id)) err = "id 重复：" + it.Id;
                if (err != null)
                {
                    result.Errors.Add("第 " + it.Line + " 行检测项无效（" + err + "）：" + Clip(it.Label + " " + it.Query));
                    continue;
                }
                ids.Add(it.Id);
                ok.Add(it);
            }
            result.Items = ok;
            return result;
        }

        /// <summary>自动识别 UTF-8/UTF-16/GBK，兼容记事本另存的各类编码。</summary>
        public static string ReadTextAuto(string path)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            // 无 BOM：先按 UTF-8 严格解码，失败则回退 GBK
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return GetGbk().GetString(bytes);
            }
        }

        private static Encoding _gbk;
        public static Encoding GetGbk()
        {
            if (_gbk == null)
            {
                try { _gbk = Encoding.GetEncoding(936); }
                catch (Exception) { _gbk = Encoding.Default; }
            }
            return _gbk;
        }

        private static string Clip(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length <= 80 ? s : s.Substring(0, 80) + "…";
        }
    }
}
