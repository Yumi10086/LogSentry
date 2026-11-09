using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LogSentry
{
    /// <summary>导出用的一张表：表名 + 行（第一行当表头）。</summary>
    internal sealed class ExportSheet
    {
        public string Name = "Sheet1";
        public List<string[]> Rows = new List<string[]>();

        public int ColumnCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i] != null && Rows[i].Length > n) n = Rows[i].Length;
                }
                return n;
            }
        }
    }

    /// <summary>
    /// CSV / XLSX 导出器。不依赖任何第三方库：xlsx 是手写的 OOXML 包（ZIP + 若干 xml），
    /// 单元格一律写成 inlineStr，避免 sharedStrings 的索引维护，也不怕时间/IP 被 Excel 自动转换。
    /// </summary>
    internal static class Exporters
    {
        // ---------------------------------------------------------------- CSV

        /// <summary>写 CSV（UTF-8 带 BOM，Excel 打开中文不乱码）。</summary>
        public static void WriteCsv(string path, ExportSheet sheet)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sheet.Rows.Count; i++) AppendCsvRow(sb, sheet.Rows[i]);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void AppendCsvRow(StringBuilder sb, string[] cells)
        {
            if (cells == null) { sb.Append("\r\n"); return; }
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(EscapeCsv(cells[i]));
            }
            sb.Append("\r\n");
        }

        private static string EscapeCsv(string v)
        {
            if (v == null) return "";
            bool need = false;
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == ',' || c == '"' || c == '\r' || c == '\n') { need = true; break; }
            }
            if (!need) return v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>按 RFC4180 解析 CSV 文本（支持引号内的逗号 / 换行 / 双引号转义）。</summary>
        public static List<string[]> ParseCsv(string text)
        {
            List<string[]> rows = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return rows;

            List<string> cells = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inQuotes = false;
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i += 2; continue; }
                        inQuotes = false; i++; continue;
                    }
                    cell.Append(c); i++; continue;
                }

                if (c == '"') { inQuotes = true; i++; continue; }
                if (c == ',') { cells.Add(cell.ToString()); cell.Length = 0; i++; continue; }
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    cells.Add(cell.ToString()); cell.Length = 0;
                    rows.Add(cells.ToArray()); cells.Clear(); i++; continue;
                }
                if (c == '\n')
                {
                    cells.Add(cell.ToString()); cell.Length = 0;
                    rows.Add(cells.ToArray()); cells.Clear(); i++; continue;
                }
                cell.Append(c); i++;
            }

            if (cell.Length > 0 || cells.Count > 0)
            {
                cells.Add(cell.ToString());
                rows.Add(cells.ToArray());
            }

            // 丢掉完全空白的行（LogParser 的 CSV 会在末尾留空行）
            List<string[]> clean = new List<string[]>(rows.Count);
            for (int r = 0; r < rows.Count; r++)
            {
                string[] row = rows[r];
                bool blank = true;
                for (int k = 0; k < row.Length; k++)
                {
                    if (!string.IsNullOrEmpty(row[k])) { blank = false; break; }
                }
                if (!blank) clean.Add(row);
            }
            return clean;
        }

        // ---------------------------------------------------------------- XLSX

        private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>写 xlsx（一个工作簿，每张表一个 sheet）。</summary>
        public static void WriteXlsx(string path, List<ExportSheet> sheets)
        {
            if (sheets == null || sheets.Count == 0) throw new ArgumentException("没有可导出的数据");

            if (File.Exists(path)) File.Delete(path);

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddEntry(zip, "[Content_Types].xml", BuildContentTypes(sheets.Count));
                AddEntry(zip, "_rels/.rels", BuildRootRels());
                AddEntry(zip, "xl/workbook.xml", BuildWorkbook(sheets));
                AddEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(sheets.Count));
                AddEntry(zip, "xl/styles.xml", BuildStyles());
                for (int i = 0; i < sheets.Count; i++)
                {
                    AddEntry(zip, "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml",
                             BuildSheet(sheets[i]));
                }
            }
        }

        private static void AddEntry(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = e.Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private static string BuildContentTypes(int sheetCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
            {
                sb.Append("<Override PartName=\"/xl/worksheets/sheet")
                  .Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            }
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRels()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            sb.Append("<Relationship Id=\"rId1\" Type=\"").Append(NsRel).Append("/officeDocument\" Target=\"xl/workbook.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildWorkbook(List<ExportSheet> sheets)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"").Append(NsMain).Append("\" xmlns:r=\"").Append(NsRel).Append("\"><sheets>");
            List<string> used = new List<string>();
            for (int i = 0; i < sheets.Count; i++)
            {
                string name = UniqueSheetName(used, sheets[i].Name);
                used.Add(name);
                sb.Append("<sheet name=\"").Append(EscapeXml(name)).Append("\" sheetId=\"")
                  .Append((i + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" r:id=\"rId").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\"/>");
            }
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string BuildWorkbookRels(int sheetCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            for (int i = 1; i <= sheetCount; i++)
            {
                string n = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("<Relationship Id=\"rId").Append(n).Append("\" Type=\"").Append(NsRel)
                  .Append("/worksheet\" Target=\"worksheets/sheet").Append(n).Append(".xml\"/>");
            }
            sb.Append("<Relationship Id=\"rId").Append((sheetCount + 1).ToString(CultureInfo.InvariantCulture))
              .Append("\" Type=\"").Append(NsRel).Append("/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        /// <summary>两个单元格样式：0=正文，1=表头加粗。</summary>
        private static string BuildStyles()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<fonts count=\"2\">");
            sb.Append("<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
            sb.Append("<font><b/><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
            sb.Append("</fonts>");
            sb.Append("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"gray125\"/></fill></fills>");
            sb.Append("<borders count=\"1\"><border/></borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append("<cellXfs count=\"2\">");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>");
            sb.Append("</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        private static string BuildSheet(ExportSheet sheet)
        {
            int cols = Math.Max(1, sheet.ColumnCount);

            StringBuilder sb = new StringBuilder(64 * 1024);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");

            // 冻结首行，滚动时表头始终可见
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("</sheetView></sheetViews>");

            // 列宽：按内容估算，截断在 12..60 之间
            sb.Append("<cols>");
            for (int c = 0; c < cols; c++)
            {
                int w = 10;
                for (int r = 0; r < sheet.Rows.Count; r++)
                {
                    string[] row = sheet.Rows[r];
                    if (row == null || c >= row.Length || row[c] == null) continue;
                    int len = TextWidth(row[c]);
                    if (len > w) w = len;
                }
                if (w < 12) w = 12;
                if (w > 60) w = 60;
                sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1)
                  .Append("\" width=\"").Append(w.ToString(CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");

            sb.Append("<sheetData>");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                string[] row = sheet.Rows[r];
                int rn = r + 1;
                sb.Append("<row r=\"").Append(rn.ToString(CultureInfo.InvariantCulture)).Append("\">");
                if (row != null)
                {
                    for (int c = 0; c < row.Length; c++)
                    {
                        string v = row[c];
                        if (v == null || v.Length == 0) continue;
                        sb.Append("<c r=\"").Append(ColumnName(c)).Append(rn.ToString(CultureInfo.InvariantCulture))
                          .Append("\" t=\"inlineStr\"");
                        if (r == 0) sb.Append(" s=\"1\"");
                        sb.Append("><is><t xml:space=\"preserve\">").Append(EscapeXml(v)).Append("</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        /// <summary>粗略估算列宽：中日韩字符按 2 计。</summary>
        private static int TextWidth(string s)
        {
            int w = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                w += (c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF)
                     || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF)
                     || (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60)
                     || (c >= 0xFFE0 && c <= 0xFFE6))) ? 2 : 1;
            }
            return w;
        }

        private static string ColumnName(int index)
        {
            StringBuilder sb = new StringBuilder();
            int n = index;
            while (true)
            {
                sb.Insert(0, (char)('A' + (n % 26)));
                n = n / 26 - 1;
                if (n < 0) break;
            }
            return sb.ToString();
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // XML 1.0 不允许的控制字符直接丢弃，否则 Excel 打不开
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string UniqueSheetName(List<string> used, string raw)
        {
            string name = (raw ?? "Sheet").Trim();
            char[] bad = { '\\', '/', '?', '*', '[', ']', ':' };
            for (int i = 0; i < bad.Length; i++) name = name.Replace(bad[i], '_');
            if (name.Length == 0) name = "Sheet";
            if (name.Length > 28) name = name.Substring(0, 28);

            string candidate = name;
            int k = 2;
            while (ContainsIgnoreCase(used, candidate))
            {
                candidate = name + "_" + k.ToString(CultureInfo.InvariantCulture);
                k++;
            }
            return candidate;
        }

        private static bool ContainsIgnoreCase(List<string> list, string v)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- LogParser 输出辅助

        /// <summary>
        /// 从 LogParser 的 CSV 原始输出里切出干净的表：丢掉尾巴的 Statistics 统计块和
        /// "已处理元素" 之类的中文统计行，再按 RFC4180 解析。
        /// </summary>
        public static List<string[]> RowsFromLogParserCsv(string raw)
        {
            string text = raw ?? "";
            string[] markers = { "Statistics:", "统计:" };
            foreach (string m in markers)
            {
                int idx = text.LastIndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) { text = text.Substring(0, idx); break; }
            }
            List<string[]> rows = ParseCsv(text);

            // 尾部可能残留空的/单列的统计行
            while (rows.Count > 0 && rows[rows.Count - 1].Length <= 1) rows.RemoveAt(rows.Count - 1);
            return rows;
        }
    }
}
