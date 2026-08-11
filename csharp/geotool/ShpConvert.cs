using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace JmaMap.Tools
{
    // シェープファイル1レコード → GeoJSONフィーチャ文字列。
    // 書式は既存データ（OGR GeoJSONドライバ）に合わせている: 座標は %.15f の末尾ゼロ落とし。
    public static class ShpConvert
    {
        static readonly string[] WantedExtensions = { ".shp", ".dbf", ".shx", ".prj", ".cpg" };

        /// <summary>ZIPなら一時フォルダへ展開し、対象の .shp のパスを返す。</summary>
        public static string ResolveShpPath(string input, string layerHint, List<string> tempDirs)
        {
            if (Directory.Exists(input))
                return PickShp(Directory.GetFiles(input, "*.shp", SearchOption.AllDirectories), layerHint, input);

            if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return ExtractZip(input, layerHint, tempDirs);

            return input;
        }

        /// <summary>
        /// ZIPから必要な拡張子だけを取り出す。
        /// 気象庁配布ZIPのエントリ名は日本語で、古いものはCP932のまま（UTF-8フラグ無し）なので
        /// 環境によって文字化けする。名前で判断せず、拡張子だけ見て "layer.shp" 等の固定名で
        /// 展開することで、エントリ名の文字コードに一切依存しないようにしている。
        /// </summary>
        static string ExtractZip(string zipPath, string layerHint, List<string> tempDirs)
        {
            string dir = Path.Combine(Path.GetTempPath(), "geotool_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            tempDirs.Add(dir);

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                // 同じベース名（=1つのシェープファイル一式）ごとにまとめる
                var groups = new List<string>();
                var byGroup = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.Ordinal);

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // フォルダ
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (Array.IndexOf(WantedExtensions, ext) < 0) continue;

                    string key = entry.FullName.Substring(0, entry.FullName.Length - ext.Length);
                    List<ZipArchiveEntry> list;
                    if (!byGroup.TryGetValue(key, out list))
                    {
                        list = new List<ZipArchiveEntry>();
                        byGroup[key] = list;
                        groups.Add(key);
                    }
                    list.Add(entry);
                }

                if (groups.Count == 0) throw new FileNotFoundException("ZIPにシェープファイルが入っていません: " + zipPath);

                int index = 0;
                if (groups.Count > 1)
                {
                    index = SelectGroup(groups, layerHint, zipPath);
                }

                string layerDir = Path.Combine(dir, "layer");
                Directory.CreateDirectory(layerDir);
                string shpPath = null;
                foreach (ZipArchiveEntry entry in byGroup[groups[index]])
                {
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    string dest = Path.Combine(layerDir, "layer" + ext);
                    entry.ExtractToFile(dest, true);
                    if (ext == ".shp") shpPath = dest;
                }
                if (shpPath == null) throw new FileNotFoundException(".shp がZIPに見つかりません: " + zipPath);
                return shpPath;
            }
        }

        static int SelectGroup(List<string> groups, string layerHint, string zipPath)
        {
            if (!string.IsNullOrEmpty(layerHint))
            {
                int n;
                if (int.TryParse(layerHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                    && n >= 1 && n <= groups.Count) return n - 1;

                for (int i = 0; i < groups.Count; i++)
                {
                    if (groups[i].IndexOf(layerHint, StringComparison.OrdinalIgnoreCase) >= 0) return i;
                }
            }
            var sb = new StringBuilder("ZIPに複数のシェープファイルがあります。--layer で番号か名前の一部を指定してください:\n");
            for (int i = 0; i < groups.Count; i++)
                sb.Append("  ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(groups[i]).Append('\n');
            throw new ArgumentException(sb.ToString());
        }

        static string PickShp(string[] candidates, string layerHint, string source)
        {
            if (candidates.Length == 0) throw new FileNotFoundException(".shp が見つかりません: " + source);
            if (candidates.Length == 1) return candidates[0];

            if (!string.IsNullOrEmpty(layerHint))
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (Path.GetFileName(candidates[i]).IndexOf(layerHint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return candidates[i];
                }
            }
            var sb = new StringBuilder(".shp が複数あります。--layer で指定してください:\n");
            for (int i = 0; i < candidates.Length; i++) sb.Append("  ").Append(Path.GetFileName(candidates[i])).Append('\n');
            throw new ArgumentException(sb.ToString());
        }

        /// <summary>
        /// シェープファイルを1レコードずつ読み、GeoJSONフィーチャ文字列にして渡す。
        /// </summary>
        public static int Each(string shpPath, Encoding dbfEncoding, Action<string, Dictionary<string, string>> onFeature)
        {
            string dbfPath = Path.ChangeExtension(shpPath, ".dbf");
            if (!File.Exists(dbfPath)) throw new FileNotFoundException("属性ファイル(.dbf)がありません: " + dbfPath);

            Encoding enc = dbfEncoding;
            if (enc == null) enc = DbfReader.DetectEncoding(dbfPath);

            int count = 0;
            using (var shp = new ShpReader(shpPath))
            using (var dbf = new DbfReader(dbfPath, enc))
            {
                while (true)
                {
                    ShpRecord rec = shp.Next();
                    if (rec == null) break;
                    string[] values = dbf.ReadRecord();
                    if (values == null) values = new string[dbf.Fields.Count];

                    var props = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < dbf.Fields.Count && i < values.Length; i++)
                        props[dbf.Fields[i].Name] = values[i];

                    onFeature(BuildFeature(rec, dbf.Fields, values), props);
                    count++;
                }
            }
            return count;
        }

        static string BuildFeature(ShpRecord rec, List<DbfField> fields, string[] values)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{ \"type\": \"Feature\", \"properties\": { ");
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Json.AppendString(sb, fields[i].Name);
                sb.Append(": ");
                AppendAttr(sb, fields[i], (i < values.Length) ? values[i] : null);
            }
            sb.Append(" }, \"geometry\": ");
            AppendGeometry(sb, rec);
            sb.Append(" }");
            return sb.ToString();
        }

        static void AppendAttr(StringBuilder sb, DbfField f, string raw)
        {
            string v = (raw == null) ? "" : raw.Trim();
            if (f.Type == 'N' || f.Type == 'F')
            {
                if (v.Length == 0) { sb.Append("null"); return; }
                double d;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { Json.AppendString(sb, v); return; }
                if (f.Decimals == 0 && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (f.Type == 'L')
            {
                if (v.Length == 0) { sb.Append("null"); return; }
                char c = char.ToUpperInvariant(v[0]);
                if (c == 'T' || c == 'Y') { sb.Append("true"); return; }
                if (c == 'F' || c == 'N') { sb.Append("false"); return; }
                sb.Append("null");
                return;
            }
            Json.AppendString(sb, v);
        }

        static void AppendGeometry(StringBuilder sb, ShpRecord rec)
        {
            if (rec.IsNull || rec.Rings == null || rec.Rings.Count == 0) { sb.Append("null"); return; }
            CoordWriter.AppendGeometry(sb, GroupRings(rec.Rings));
        }

        /// <summary>
        /// シェープファイルはリングを平坦に並べるだけなので、外周と穴を自分で判別する。
        /// ESRI仕様では外周が時計回り、穴が反時計回り。符号付き面積の符号で見分け、
        /// 外周が現れるたびに新しいポリゴンを開始し、穴は直前の外周にぶら下げる。
        /// </summary>
        static List<List<double[]>> GroupRings(List<double[]> rings)
        {
            var polygons = new List<List<double[]>>();
            for (int i = 0; i < rings.Count; i++)
            {
                bool isHole = SignedArea(rings[i]) > 0;   // 反時計回り = 穴
                if (!isHole || polygons.Count == 0)
                {
                    var poly = new List<double[]>();
                    poly.Add(rings[i]);
                    polygons.Add(poly);
                }
                else
                {
                    polygons[polygons.Count - 1].Add(rings[i]);
                }
            }
            return polygons;
        }

        /// <summary>符号付き面積。正なら反時計回り、負なら時計回り。</summary>
        static double SignedArea(double[] ring)
        {
            double sum = 0;
            int n = ring.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                sum += ring[i * 2] * ring[j * 2 + 1] - ring[j * 2] * ring[i * 2 + 1];
            }
            return sum / 2.0;
        }

    }
}
