using System;

namespace LogSentry
{
    /// <summary>
    /// 从查询语句的 FROM 子句推断日志源名（Security / System / Application …），仅用于展示标签。
    /// 单独成一个文件是为了让 Config.cs 不依赖 Engine.cs（Engine 还要依赖 Config，互相引用会成环）。
    /// </summary>
    public static class LogSource
    {
        /// <summary>
        /// 取顶层 FROM 后面的那一项，返回 "Security" 这类短名。
        /// 识别不出来（没有 FROM、指向文件路径、用了宏或变量）时返回空串，调用方据此不显示标签。
        /// </summary>
        public static string FromQuery(string query)
        {
            int fromIdx = FindTopLevelKeyword(query, "FROM");
            if (fromIdx < 0) return "";

            string rest = query.Substring(fromIdx + 4);
            // 顶层 FROM 后面可能直接跟 WHERE/GROUP/ORDER，说明没有来源，放弃
            if (StartsWithKeyword(rest.TrimStart(), "WHERE")
                || StartsWithKeyword(rest.TrimStart(), "GROUP")
                || StartsWithKeyword(rest.TrimStart(), "ORDER")) return "";

            string token = ReadToken(rest);
            if (token.Length == 0) return "";

            string bare = StripQuotes(token);
            // 文件路径（C:\…、\\server\…、*.evtx、.csv 之类）不是日志源名
            if (bare.IndexOf(':') >= 0 || bare.IndexOf('\\') >= 0 || bare.IndexOf('/') >= 0) return "";
            if (bare.IndexOf('.') >= 0) return "";

            string simple = LastSegment(bare);
            if (simple.Length == 0) return "";

            // 前三个是 Windows 自带通道，给中文名更好读；其余原样返回
            string lower = simple.ToLowerInvariant();
            if (lower == "security") return "Security";
            if (lower == "system") return "System";
            if (lower == "application") return "Application";
            return simple;
        }

        /// <summary>SQL 里跳过单引号字符串找顶层关键字，返回起始下标；找不到返回 -1。</summary>
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

        private static bool StartsWithKeyword(string s, string keyword)
        {
            if (s.Length < keyword.Length) return false;
            if (string.Compare(s, 0, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            return s.Length == keyword.Length || !IsIdentChar(s[keyword.Length]);
        }

        /// <summary>读 FROM 后面第一个 token：带引号时读到配对引号，否则读到空白或括号。</summary>
        private static string ReadToken(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return "";

            if (s[i] == '\'')
            {
                int j = i + 1;
                while (j < s.Length)
                {
                    if (s[j] == '\'')
                    {
                        if (j + 1 < s.Length && s[j + 1] == '\'') { j += 2; continue; }
                        return s.Substring(i, j - i + 1);
                    }
                    j++;
                }
                return s.Substring(i); // 引号没闭合，交给上层当怪东西处理
            }

            int k = i;
            while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] != '(' && s[k] != ')') k++;
            return s.Substring(i, k - i);
        }

        private static string StripQuotes(string token)
        {
            string t = token.Trim();
            if (t.Length >= 2 && t[0] == '\'' && t[t.Length - 1] == '\'')
                return t.Substring(1, t.Length - 2).Replace("''", "'").Trim();
            return t;
        }

        /// <summary>取最后一段：既接受 "Security" 这样的裸名，也接受 LogParser 的 "Security;Security" 与 "机器名\日志名"。</summary>
        private static string LastSegment(string name)
        {
            string s = name.Trim();
            int semi = s.LastIndexOf(';');
            if (semi >= 0) s = s.Substring(semi + 1);
            int slash = s.LastIndexOf('\\');
            if (slash >= 0) s = s.Substring(slash + 1);
            int bracket = s.IndexOf('(');
            if (bracket > 0) s = s.Substring(0, bracket);
            return s.Trim();
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';
        }
    }
}
