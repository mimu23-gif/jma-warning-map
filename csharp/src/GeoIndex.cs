using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JmaMap
{
    public class IndexEntry
    {
        public string File;   // GeoJSONの絶対パス
        public string Raw;    // そのファイル内で実際に一致させるべき原コード
    }

    // 地域コード → GeoJSONファイル の対応表。
    // GAS版の region-index.json（Drive ファイルIDを持つ）に相当するが、
    // ローカルではファイルパスがそのまま識別子になるのでID解決の段が要らない。
    public class GeoIndex
    {
        public Dictionary<string, IndexEntry> Raw = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        public Dictionary<string, IndexEntry> Norm6 = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        public int FileCount;
        public int FeatureCount;
        public string Signature = "";

        // 数字だけを取り出し、6桁/7桁のときだけ返す（Code.js の normalizeCodeString_ と同じ）
        public static string NormalizeCode(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c >= '0' && c <= '9') sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length == 6 || s.Length == 7) return s;
            return "";
        }

        // Code.js の findIndexEntryForCode_ と同じ解決規則：
        // 7桁は原コード厳密一致 → 外れたら（strictでなければ）先頭6桁で代表フィーチャへ。
        public IndexEntry Find(string code, bool strict)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (code.Length == 7)
            {
                IndexEntry hit;
                if (Raw.TryGetValue(code, out hit)) return hit;
                if (strict) return null;
                IndexEntry n;
                if (Norm6.TryGetValue(code.Substring(0, 6), out n)) return n;
                return null;
            }
            IndexEntry n6;
            if (Norm6.TryGetValue(code, out n6)) return n6;
            return null;
        }

        /*** 構築 ***/

        public static List<string> ListGeoFiles(List<string> folders)
        {
            var files = new List<string>();
            for (int i = 0; i < folders.Count; i++)
            {
                string dir = folders[i];
                if (!Directory.Exists(dir)) continue;
                string[] found = Directory.GetFiles(dir, "*.geojson", SearchOption.TopDirectoryOnly);
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                files.AddRange(found);
            }
            return files;
        }

        // ファイル数・総バイト数・最終更新時刻・全パスのハッシュから署名を作る。
        // キャッシュには絶対パスが入るため、データフォルダを移動したときも必ず無効化されるよう
        // パスそのものを署名に含める（数・サイズ・更新時刻は移動しても変わらない）。
        public static string ComputeSignature(List<string> files)
        {
            long total = 0;
            long maxTicks = 0;
            ulong pathHash = 14695981039346656037UL;   // FNV-1a 64bit
            for (int i = 0; i < files.Count; i++)
            {
                var fi = new FileInfo(files[i]);
                total += fi.Length;
                long t = fi.LastWriteTimeUtc.Ticks;
                if (t > maxTicks) maxTicks = t;

                string p = files[i];
                for (int k = 0; k < p.Length; k++)
                {
                    pathHash ^= char.ToLowerInvariant(p[k]);
                    pathHash *= 1099511628211UL;
                }
                pathHash ^= (ulong)'|';
                pathHash *= 1099511628211UL;
            }
            return files.Count.ToString(CultureInfo.InvariantCulture) + "|"
                 + total.ToString(CultureInfo.InvariantCulture) + "|"
                 + maxTicks.ToString(CultureInfo.InvariantCulture) + "|"
                 + pathHash.ToString("x16", CultureInfo.InvariantCulture);
        }

        public static GeoIndex Build(List<string> files, Action<string> log)
        {
            var idx = new GeoIndex();
            idx.Signature = ComputeSignature(files);

            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                List<FeatureRef> feats;
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    feats = GeoJson.Scan(text);
                }
                catch (Exception ex)
                {
                    if (log != null) log("索引: 読み込み失敗 " + Path.GetFileName(path) + " : " + ex.Message);
                    continue;
                }

                idx.FileCount++;
                for (int k = 0; k < feats.Count; k++)
                {
                    string code = feats[k].Code;
                    if (string.IsNullOrEmpty(code)) continue;
                    idx.FeatureCount++;

                    // 原コードは先勝ちでそのまま登録（"10" のような短縮コードも保持される）
                    if (!idx.Raw.ContainsKey(code))
                        idx.Raw[code] = NewEntry(path, code);

                    string norm = NormalizeCode(code);
                    if (norm.Length == 0) continue;
                    string key6 = (norm.Length == 7) ? norm.Substring(0, 6) : norm;
                    if (!idx.Norm6.ContainsKey(key6))
                        idx.Norm6[key6] = NewEntry(path, code);
                }

                if (log != null && (i % 40 == 39 || i == files.Count - 1))
                    log("索引構築: " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + files.Count.ToString(CultureInfo.InvariantCulture) + " ファイル");
            }
            return idx;
        }

        static IndexEntry NewEntry(string path, string raw)
        {
            var e = new IndexEntry();
            e.File = path;
            e.Raw = raw;
            return e;
        }

        /*** キャッシュ（起動のたびに192MBを読み直さないため） ***/

        public void Save(string path)
        {
            var files = new List<string>();
            var fileIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.Append("{\"version\":\"1\",\"signature\":");
            Json.AppendString(sb, Signature);
            sb.Append(",\"fileCount\":").Append(FileCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"featureCount\":").Append(FeatureCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"raw\":");
            AppendMap(sb, Raw, files, fileIds);
            sb.Append(",\"norm6\":");
            AppendMap(sb, Norm6, files, fileIds);
            sb.Append(",\"files\":[");
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Json.AppendString(sb, files[i]);
            }
            sb.Append("]}");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static void AppendMap(StringBuilder sb, Dictionary<string, IndexEntry> map, List<string> files, Dictionary<string, int> fileIds)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in map)
            {
                int id;
                if (!fileIds.TryGetValue(kv.Value.File, out id))
                {
                    id = files.Count;
                    files.Add(kv.Value.File);
                    fileIds[kv.Value.File] = id;
                }
                if (!first) sb.Append(',');
                first = false;
                Json.AppendString(sb, kv.Key);
                sb.Append(":[").Append(id.ToString(CultureInfo.InvariantCulture)).Append(',');
                Json.AppendString(sb, kv.Value.Raw);
                sb.Append(']');
            }
            sb.Append('}');
        }

        // 署名が一致しなければ null を返す（＝データが変わったので作り直し）
        public static GeoIndex Load(string path, string expectedSignature)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.Obj(Json.Parse(text));
                if (root == null) return null;
                if (Json.GetStr(root, "signature") != expectedSignature) return null;

                var fileList = Json.Arr(Json.Get(root, "files"));
                if (fileList == null) return null;
                var files = new string[fileList.Count];
                for (int i = 0; i < fileList.Count; i++) files[i] = Json.Str(fileList[i]);

                var idx = new GeoIndex();
                idx.Signature = expectedSignature;
                idx.FileCount = ReadInt(root, "fileCount", files.Length);
                idx.FeatureCount = ReadInt(root, "featureCount", 0);
                ReadMap(Json.Obj(Json.Get(root, "raw")), files, idx.Raw);
                ReadMap(Json.Obj(Json.Get(root, "norm6")), files, idx.Norm6);
                if (idx.Raw.Count == 0 && idx.Norm6.Count == 0) return null;
                return idx;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int ReadInt(Dictionary<string, object> root, string key, int fallback)
        {
            object v = Json.Get(root, key);
            if (v == null) return fallback;
            double d;
            if (double.TryParse(Json.Str(v), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (int)d;
            return fallback;
        }

        static void ReadMap(Dictionary<string, object> src, string[] files, Dictionary<string, IndexEntry> dst)
        {
            if (src == null) return;
            foreach (var kv in src)
            {
                var pair = Json.Arr(kv.Value);
                if (pair == null || pair.Count < 2) continue;
                int id = (int)Convert.ToDouble(Json.Str(pair[0]), CultureInfo.InvariantCulture);
                if (id < 0 || id >= files.Length) continue;
                dst[kv.Key] = NewEntry(files[id], Json.Str(pair[1]));
            }
        }
    }

    // GeoJSON本文のLRUキャッシュ。GAS版の getGeoJsonByFileIdCached_（5分キャッシュ）に相当するが、
    // CacheServiceの95KB制限が無いので本文をそのまま持てる。上限ファイル数でメモリを抑える。
    public class GeoFile
    {
        public string Text;
        public List<FeatureRef> Features;
        public DateTime LastUsed;
    }

    public class GeoStore
    {
        readonly Dictionary<string, GeoFile> cache = new Dictionary<string, GeoFile>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        readonly int maxFiles;

        public GeoStore(int maxFiles)
        {
            this.maxFiles = (maxFiles > 0) ? maxFiles : 8;
        }

        public GeoFile Get(string path)
        {
            lock (gate)
            {
                GeoFile hit;
                if (cache.TryGetValue(path, out hit))
                {
                    hit.LastUsed = DateTime.UtcNow;
                    return hit;
                }
            }

            var gf = new GeoFile();
            gf.Text = File.ReadAllText(path, Encoding.UTF8);
            gf.Features = GeoJson.Scan(gf.Text);
            gf.LastUsed = DateTime.UtcNow;

            lock (gate)
            {
                cache[path] = gf;
                while (cache.Count > maxFiles)
                {
                    string oldest = null;
                    DateTime oldestAt = DateTime.MaxValue;
                    foreach (var kv in cache)
                    {
                        if (kv.Value.LastUsed < oldestAt) { oldestAt = kv.Value.LastUsed; oldest = kv.Key; }
                    }
                    if (oldest == null) break;
                    cache.Remove(oldest);
                }
            }
            return gf;
        }
    }
}
