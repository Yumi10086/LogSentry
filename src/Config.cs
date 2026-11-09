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
        public string LogName = "Security";
        public string Query = "";
        public string Note = "";
        public bool DefaultOn;
        public int Line;
        /// <summary>是否在标题里附带事件号（手动命令这种没有配置来源的项关掉，免得从 SQL 里误抓数字）。</summary>
        public bool ShowEventIds = true;

        /// <summary>标题行显示名，含日志源与事件号，便于结果区定位。</summary>
        public string DisplayLabel
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Label);
                string tag = LogName;
                if (ShowEventIds && !string.IsNullOrEmpty(EventIdList)) tag += " · " + EventIdList;
                if (!string.IsNullOrEmpty(tag)) sb.Append("  [").Append(tag).Append(']');
                return sb.ToString();
            }
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
        public static ConfigLoadResult Load(string path)
        {
            ConfigLoadResult result = new ConfigLoadResult();
            result.SourcePath = path;
            if (!System.IO.File.Exists(path))
            {
                result.Errors.Add("配置文件不存在：" + path);
                return result;
            }

            string text = ReadTextAuto(path);
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
                    case "log":
                    case "logname": cur.LogName = val; break;
                    case "query": cur.Query = val; break;
                    case "default":
                        bool on;
                        if (bool.TryParse(val, out on)) cur.DefaultOn = on;
                        else cur.DefaultOn = (val == "1" || val == "yes" || val == "on" || val == "是");
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
