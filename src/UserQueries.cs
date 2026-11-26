using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LogSentry
{
    /// <summary>
    /// 用户自定义检测项的持久化：&lt;exe&gt;\config\user-queries.txt。
    ///
    /// 为什么单独一个文件：
    ///  · queries.txt 是出厂配置，升级/重装会整份覆盖，用户项写进去就丢；
    ///  · 两份文件按 id 合并（ConfigLoader.LoadMerged），用户侧写同 id + disabled: true
    ///    就是「停用这个内置项」，不需要再维护一张禁用清单；
    ///  · 格式与 queries.txt 完全一致，方便用户直接拿记事本改。
    /// </summary>
    public static class UserQueries
    {
        public const string FolderName = "config";
        public const string FileName = "user-queries.txt";

        /// <summary>程序所在目录下的用户配置文件完整路径。</summary>
        public static string PathFor(string exeDir)
        {
            if (string.IsNullOrEmpty(exeDir)) exeDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(Path.Combine(exeDir, FolderName), FileName);
        }

        /// <summary>写回用户配置文件（UTF-8 带 BOM，和 queries.txt 保持一致）。</summary>
        public static void Save(string path, List<QueryItem> items)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("路径为空", "path");
            StringBuilder sb = new StringBuilder();
            sb.Append("# LogSentry 用户自定义检测项\r\n");
            sb.Append("# 由程序「自定义检测项」页签生成，也可以直接用记事本改；格式与 queries.txt 相同。\r\n");
            sb.Append("# 这里的项会追加在出厂检测项之后；写一个与内置项同 id 的段落并加 disabled: true 就是停用它。\r\n");
            sb.Append("# 删掉本文件 = 全部恢复出厂设置。\r\n");

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++) AppendItem(sb, items[i]);
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void AppendItem(StringBuilder sb, QueryItem it)
        {
            if (it == null) return;
            string id = it.Id;
            if (string.IsNullOrEmpty(id)) return;
            sb.Append("\r\n[").Append(OneLine(id)).Append("]\r\n");
            sb.Append("label: ").Append(OneLine(it.Label)).Append("\r\n");
            sb.Append("group: ").Append(OneLine(it.Group)).Append("\r\n");
            sb.Append("query: ").Append(OneLine(it.Query)).Append("\r\n");
            if (!string.IsNullOrEmpty(it.Note)) sb.Append("note: ").Append(OneLine(it.Note)).Append("\r\n");
            sb.Append("default: ").Append(it.DefaultOn ? "true" : "false").Append("\r\n");
            if (it.Disabled) sb.Append("disabled: true\r\n");
        }

        /// <summary>
        /// 配置语法是「一行一个键」，所以多行内容必须压成单行。
        /// 换行替换成空格（不是删除），这样粘贴多行 SQL 后不会把两个词粘在一起。
        /// </summary>
        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>生成一个不与现有 id 冲突的自定义 id（user_1、user_2……）。</summary>
        public static string NewId(List<QueryItem> existing)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    if (existing[i] != null) used.Add(existing[i].Id);
                }
            }
            for (int n = 1; n < 100000; n++)
            {
                string id = "user_" + n.ToString();
                if (!used.Contains(id)) return id;
            }
            return "user_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
