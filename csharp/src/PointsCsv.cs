using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JmaMap
{
    public class PointItem
    {
        public string Name;
        public string Type1;
        public string Type2;
        public string Type3;
        public string Address;
        public double Lat;
        public double Lng;
    }

    public class PointsData
    {
        public List<PointItem> Items = new List<PointItem>();
        public List<string> Types1 = new List<string>();
        public List<string> Types2 = new List<string>();
        public List<string> Types3 = new List<string>();
        public string Label1 = "種別1";
        public string Label2 = "種別2";
        public string Label3 = "種別3";
    }

    // POI（任意地点）を CSV から読む。GAS版の Points.js（スプレッドシート）に対応し、
    // ヘッダーの別名解決と「ヘッダー文字列をそのままフィルタ見出しに使う」挙動を引き継ぐ。
    public static class PointsCsv
    {
        static readonly string[] AliasName = { "name", "名称", "地点名", "地点名称" };
        static readonly string[] AliasType1 = { "type1", "種別1", "category1", "type", "種別" };
        static readonly string[] AliasType2 = { "type2", "種別2", "category2" };
        static readonly string[] AliasType3 = { "type3", "種別3", "category3" };
        static readonly string[] AliasAddress = { "address", "住所" };
        static readonly string[] AliasLat = { "lat", "latitude", "緯度" };
        static readonly string[] AliasLng = { "lng", "lon", "long", "longitude", "経度" };

        public static PointsData Load(string path)
        {
            var data = new PointsData();
            if (!File.Exists(path)) return data;

            List<string[]> rows = ParseCsv(ReadTextAuto(path));
            if (rows.Count < 2) return data;

            string[] rawHeader = rows[0];
            var header = new string[rawHeader.Length];
            for (int i = 0; i < rawHeader.Length; i++)
                header[i] = (rawHeader[i] == null) ? "" : rawHeader[i].Trim().ToLowerInvariant();

            int iName = ColIndex(header, AliasName);
            int iType1 = ColIndex(header, AliasType1);
            int iType2 = ColIndex(header, AliasType2);
            int iType3 = ColIndex(header, AliasType3);
            int iAddr = ColIndex(header, AliasAddress);
            int iLat = ColIndex(header, AliasLat);
            int iLng = ColIndex(header, AliasLng);

            if (iType1 >= 0 && rawHeader[iType1].Trim().Length > 0) data.Label1 = rawHeader[iType1].Trim();
            if (iType2 >= 0 && rawHeader[iType2].Trim().Length > 0) data.Label2 = rawHeader[iType2].Trim();
            if (iType3 >= 0 && rawHeader[iType3].Trim().Length > 0) data.Label3 = rawHeader[iType3].Trim();

            var set1 = new List<string>();
            var set2 = new List<string>();
            var set3 = new List<string>();

            for (int r = 1; r < rows.Count; r++)
            {
                string[] row = rows[r];
                if (IsBlank(row)) continue;

                double lat, lng;
                if (!TryNum(Cell(row, iLat), out lat)) continue;   // 座標が無い行は捨てる
                if (!TryNum(Cell(row, iLng), out lng)) continue;

                var p = new PointItem();
                p.Name = Cell(row, iName);
                if (p.Name.Length == 0) p.Name = "(無題)";
                p.Type1 = Cell(row, iType1);
                p.Type2 = Cell(row, iType2);
                p.Type3 = Cell(row, iType3);
                p.Address = Cell(row, iAddr);
                p.Lat = lat;
                p.Lng = lng;
                data.Items.Add(p);

                // 未設定（空文字）も選択肢として残す。UI側で「(未設定)」として表示される。
                if (!set1.Contains(p.Type1)) set1.Add(p.Type1);
                if (!set2.Contains(p.Type2)) set2.Add(p.Type2);
                if (!set3.Contains(p.Type3)) set3.Add(p.Type3);
            }

            set1.Sort(StringComparer.Ordinal);
            set2.Sort(StringComparer.Ordinal);
            set3.Sort(StringComparer.Ordinal);
            data.Types1 = set1;
            data.Types2 = set2;
            data.Types3 = set3;
            return data;
        }

        public static string ToJson(PointsData d)
        {
            var sb = new StringBuilder();
            sb.Append("{\"items\":[");
            for (int i = 0; i < d.Items.Count; i++)
            {
                PointItem p = d.Items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":");
                Json.AppendString(sb, p.Name);
                sb.Append(",\"type1\":");
                Json.AppendString(sb, p.Type1);
                sb.Append(",\"type2\":");
                Json.AppendString(sb, p.Type2);
                sb.Append(",\"type3\":");
                Json.AppendString(sb, p.Type3);
                sb.Append(",\"address\":");
                Json.AppendString(sb, p.Address);
                sb.Append(",\"lat\":").Append(p.Lat.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(",\"lng\":").Append(p.Lng.ToString("R", CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("],\"types1\":");
            AppendArray(sb, d.Types1);
            sb.Append(",\"types2\":");
            AppendArray(sb, d.Types2);
            sb.Append(",\"types3\":");
            AppendArray(sb, d.Types3);
            sb.Append(",\"labels\":{\"type1\":");
            Json.AppendString(sb, d.Label1);
            sb.Append(",\"type2\":");
            Json.AppendString(sb, d.Label2);
            sb.Append(",\"type3\":");
            Json.AppendString(sb, d.Label3);
            sb.Append("}}");
            return sb.ToString();
        }

        static void AppendArray(StringBuilder sb, List<string> list)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Json.AppendString(sb, list[i]);
            }
            sb.Append(']');
        }

        /*** CSV ***/

        // BOM付きUTF-8 / BOM無しUTF-8 / CP932（Excelの既定）のいずれでも読めるようにする
        public static string ReadTextAuto(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(932).GetString(bytes);
            }
        }

        // 引用符・埋め込み改行・"" によるエスケープに対応した最小限のCSVリーダ
        public static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                    continue;
                }

                if (c == '"') { inQuotes = true; continue; }
                if (c == ',') { row.Add(field.ToString()); field.Length = 0; continue; }
                if (c == '\r') continue;
                if (c == '\n')
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                    rows.Add(row.ToArray());
                    row.Clear();
                    continue;
                }
                field.Append(c);
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row.ToArray());
            }
            return rows;
        }

        static int ColIndex(string[] header, string[] aliases)
        {
            for (int a = 0; a < aliases.Length; a++)
            {
                for (int i = 0; i < header.Length; i++)
                {
                    if (string.Equals(header[i], aliases[a], StringComparison.Ordinal)) return i;
                }
            }
            return -1;
        }

        static string Cell(string[] row, int i)
        {
            if (i < 0 || i >= row.Length || row[i] == null) return "";
            return row[i].Trim();
        }

        static bool IsBlank(string[] row)
        {
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i] != null && row[i].Trim().Length > 0) return false;
            }
            return true;
        }

        static bool TryNum(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
