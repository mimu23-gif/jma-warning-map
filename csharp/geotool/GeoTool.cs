using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JmaMap.Tools
{
    // 境界データ変換ツール（コンソール）。Windows標準のcsc.exeでビルドできる範囲だけで書いてある。
    //
    //   GeoTool.exe convert     --in <ZIP|SHP> --out <出力フォルダ>      ZIP/SHPから都道府県別GeoJSONまで一気に
    //   GeoTool.exe shp2geojson --in <ZIP|SHP> --out <全国版.geojson>    シェープファイル→全国版GeoJSON
    //   GeoTool.exe split       --in <全国版>  --out <出力フォルダ>      全国版→都道府県別に分割
    //   GeoTool.exe merge       --in <フォルダ> --out <全国版.geojson>   都道府県別→全国版（検証用）
    //
    // tools/build_geojson_folders.py（geopandas版）と同じ成果物を、追加インストール無しで作る。
    static class GeoTool
    {
        // 都道府県コード（先頭2桁）-> ファイル名用の英語名（Python版の PREF_NAMES と同一）
        static readonly Dictionary<string, string> PrefNames = BuildPrefNames();

        // フォルダ名から既定のファイル名サフィックスを引く（Python版の TARGETS と同一）
        static readonly Dictionary<string, string> FolderSuffix = BuildFolderSuffix();

        static int Main(string[] args)
        {
            var tempDirs = new List<string>();
            try
            {
                if (args.Length == 0) { Usage(); return 1; }
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "split") return CmdSplit(args);
                if (cmd == "merge") return CmdMerge(args);
                if (cmd == "shp2geojson") return CmdShp2GeoJson(args, tempDirs);
                if (cmd == "convert") return CmdConvert(args, tempDirs);
                if (cmd == "simplify") return CmdSimplify(args);
                Console.Error.WriteLine("不明なコマンド: " + args[0]);
                Usage();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("エラー: " + ex.Message);
                return 1;
            }
            finally
            {
                for (int i = 0; i < tempDirs.Count; i++)
                {
                    try { Directory.Delete(tempDirs[i], true); }
                    catch (Exception) { }
                }
            }
        }

        static void Usage()
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  GeoTool.exe convert     --in <ZIP|SHP> --out <出力フォルダ> [--suffix area] [--encoding cp932] [--layer 名前の一部]");
            Console.WriteLine("  GeoTool.exe shp2geojson --in <ZIP|SHP> --out <全国版.geojson> [--encoding cp932] [--layer 名前の一部]");
            Console.WriteLine("  GeoTool.exe split       --in <全国版.geojson> --out <出力フォルダ> [--suffix area]");
            Console.WriteLine("  GeoTool.exe merge       --in <都道府県別フォルダ> --out <全国版.geojson>");
            Console.WriteLine("  GeoTool.exe simplify    --in <フォルダ|ファイル> --out <同> --tolerance 0.001");
            Console.WriteLine();
            Console.WriteLine("  convert     : ZIP/シェープファイルから都道府県別GeoJSONまで一気に作る");
            Console.WriteLine("  shp2geojson : シェープファイルを全国版GeoJSON 1ファイルへ変換する");
            Console.WriteLine("  split       : 全国版を地域コード先頭2桁で都道府県別へ分割する");
            Console.WriteLine("  merge       : 都道府県別を1つに連結する（検証用）");
            Console.WriteLine("  simplify    : 表示用に頂点を間引く（Douglas-Peucker）。--tolerance は度で指定（1e-4≒11m）");
            Console.WriteLine();
            Console.WriteLine("  座標変換は行わない。気象庁の予報区等GISはJGD2011の地理座標で、WGS84と同等のため");
            Console.WriteLine("  そのままEPSG:4326として出力する。投影座標系(PROJCS)の入力は受け付けない。");
        }

        /*** 都道府県別の振り分け（split と convert で共有） ***/

        class PrefRouter : IDisposable
        {
            readonly string outDir;
            readonly string suffix;
            readonly Dictionary<string, GeoJsonWriter> writers = new Dictionary<string, GeoJsonWriter>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> SkippedPref = new Dictionary<string, int>(StringComparer.Ordinal);
            public int Written;
            public int NoCode;
            public int Injected;

            public PrefRouter(string outDir, string suffix)
            {
                this.outDir = outDir;
                this.suffix = suffix;
                Directory.CreateDirectory(outDir);
            }

            public int FileCount { get { return writers.Count; } }

            public void Add(string rawFeature)
            {
                List<FeatureRef> refs = GeoJson.Scan(rawFeature);
                if (refs.Count == 0 || string.IsNullOrEmpty(refs[0].Code)) { NoCode++; return; }

                FeatureRef fr = refs[0];
                string digits = OnlyDigits(fr.Code);
                if (digits.Length < 2) { NoCode++; return; }

                string pref2 = digits.Substring(0, 2);
                string romaji;
                if (!PrefNames.TryGetValue(pref2, out romaji))
                {
                    // 全国・地方など都道府県に属さないコードは出力しない（Python版と同じ挙動）
                    int n;
                    SkippedPref.TryGetValue(pref2, out n);
                    SkippedPref[pref2] = n + 1;
                    return;
                }

                string layer = pref2 + "_" + romaji + "_" + suffix;
                GeoJsonWriter w;
                if (!writers.TryGetValue(layer, out w))
                {
                    w = new GeoJsonWriter(Path.Combine(outDir, layer + ".geojson"), layer);
                    writers[layer] = w;
                }

                string outRaw = EnsureRegionCode(rawFeature, fr, fr.Code, pref2);
                if (!object.ReferenceEquals(outRaw, rawFeature)) Injected++;
                w.WriteFeature(outRaw);
                Written++;
            }

            public void Report(int read)
            {
                Console.WriteLine("読み込み " + read.ToString(CultureInfo.InvariantCulture)
                    + " / 書き出し " + Written.ToString(CultureInfo.InvariantCulture)
                    + " フィーチャ、" + FileCount.ToString(CultureInfo.InvariantCulture) + " ファイル");
                if (Injected > 0)
                    Console.WriteLine("regioncode を補完: " + Injected.ToString(CultureInfo.InvariantCulture) + " 件");
                if (NoCode > 0)
                    Console.WriteLine("コードを取得できずスキップ: " + NoCode.ToString(CultureInfo.InvariantCulture) + " 件");
                foreach (var kv in SkippedPref)
                    Console.WriteLine("都道府県外のコードをスキップ: 先頭2桁=" + kv.Key + " " + kv.Value.ToString(CultureInfo.InvariantCulture) + " 件");
            }

            public void Dispose()
            {
                foreach (var kv in writers) kv.Value.Dispose();
                writers.Clear();
            }
        }

        static string ResolveSuffix(string suffix, string outDir)
        {
            if (!string.IsNullOrEmpty(suffix)) return suffix;
            string folder = new DirectoryInfo(outDir.TrimEnd('\\', '/')).Name;
            string s;
            if (!FolderSuffix.TryGetValue(folder, out s))
                throw new ArgumentException("--suffix を指定してください（出力フォルダ名 '" + folder + "' から既定値を決められません）");
            Console.WriteLine("サフィックス: " + s + "（出力フォルダ名から決定）");
            return s;
        }

        /*** split ***/

        static int CmdSplit(string[] args)
        {
            string input = GetArg(args, "--in");
            string outDir = GetArg(args, "--out");
            if (input == null || outDir == null) { Usage(); return 1; }
            if (!File.Exists(input)) throw new FileNotFoundException("入力がありません: " + input);

            string suffix = ResolveSuffix(GetArg(args, "--suffix"), outDir);
            int read = 0;
            using (var router = new PrefRouter(outDir, suffix))
            {
                using (var stream = new FeatureStream(input))
                {
                    if (!stream.MoveToFeatures())
                        throw new FormatException("features 配列が見つかりません: " + input);
                    string raw;
                    while ((raw = stream.NextFeature()) != null)
                    {
                        read++;
                        router.Add(raw);
                    }
                }
                Console.WriteLine("入力: " + input);
                Console.WriteLine("出力: " + Path.GetFullPath(outDir));
                router.Report(read);
            }
            return 0;
        }

        /*** shp2geojson ***/

        static int CmdShp2GeoJson(string[] args, List<string> tempDirs)
        {
            string input = GetArg(args, "--in");
            string output = GetArg(args, "--out");
            if (input == null || output == null) { Usage(); return 1; }

            string shp = ShpConvert.ResolveShpPath(input, GetArg(args, "--layer"), tempDirs);
            Console.WriteLine("シェープファイル: " + shp);
            Console.WriteLine("座標系: " + PrjCheck.Verify(shp));

            Encoding enc = ParseEncoding(GetArg(args, "--encoding"));
            int count;
            using (var w = new GeoJsonWriter(output, Path.GetFileNameWithoutExtension(output)))
            {
                var writer = w;
                count = ShpConvert.Each(shp, enc, delegate(string feature, Dictionary<string, string> props)
                {
                    writer.WriteFeature(feature);
                });
            }
            Console.WriteLine("変換: " + count.ToString(CultureInfo.InvariantCulture) + " フィーチャ -> " + Path.GetFullPath(output));
            return 0;
        }

        /*** convert（ZIP/SHP → 都道府県別まで一気に） ***/

        static int CmdConvert(string[] args, List<string> tempDirs)
        {
            string input = GetArg(args, "--in");
            string outDir = GetArg(args, "--out");
            if (input == null || outDir == null) { Usage(); return 1; }

            string shp = ShpConvert.ResolveShpPath(input, GetArg(args, "--layer"), tempDirs);
            Console.WriteLine("シェープファイル: " + shp);
            Console.WriteLine("座標系: " + PrjCheck.Verify(shp));

            string suffix = ResolveSuffix(GetArg(args, "--suffix"), outDir);
            Encoding enc = ParseEncoding(GetArg(args, "--encoding"));

            int read = 0;
            using (var router = new PrefRouter(outDir, suffix))
            {
                var r = router;
                read = ShpConvert.Each(shp, enc, delegate(string feature, Dictionary<string, string> props)
                {
                    r.Add(feature);
                });
                Console.WriteLine("出力: " + Path.GetFullPath(outDir));
                router.Report(read);
            }
            return 0;
        }

        static Encoding ParseEncoding(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;   // null = 自動判定
            if (string.Equals(name, "cp932", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "shift_jis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "sjis", StringComparison.OrdinalIgnoreCase))
                return Encoding.GetEncoding(932);
            if (string.Equals(name, "utf-8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "utf8", StringComparison.OrdinalIgnoreCase))
                return new UTF8Encoding(false);
            return Encoding.GetEncoding(name);
        }

        /// <summary>
        /// properties に regioncode / PrefCode が無ければ補って返す（GASの admin_buildRegionIndex と
        /// C#版の索引は regioncode を見る）。既にあるフィーチャは原文をそのまま返す。
        /// </summary>
        static string EnsureRegionCode(string raw, FeatureRef fr, string code, string pref2)
        {
            if (fr.PropsEnd <= fr.PropsStart) return raw;

            int idx = fr.PropsStart;
            Dictionary<string, object> props = Json.ParseObject(raw, ref idx);
            bool hasRegion = props.ContainsKey("regioncode");
            bool hasPref = props.ContainsKey("PrefCode");
            if (hasRegion && hasPref) return raw;

            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in props)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(' ');
                Json.AppendString(sb, kv.Key);
                sb.Append(": ");
                AppendValue(sb, kv.Value);
            }
            if (!hasPref)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(" \"PrefCode\": ");
                Json.AppendString(sb, pref2);
            }
            if (!hasRegion)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(" \"regioncode\": ");
                Json.AppendString(sb, code);
            }
            sb.Append(" }");

            return raw.Substring(0, fr.PropsStart) + sb.ToString() + raw.Substring(fr.PropsEnd);
        }

        static void AppendValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Json.AppendString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                    sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            Json.AppendString(sb, v.ToString());
        }

        /*** simplify（表示用の間引き） ***/

        static int CmdSimplify(string[] args)
        {
            string input = GetArg(args, "--in");
            string output = GetArg(args, "--out");
            string tolText = GetArg(args, "--tolerance");
            if (input == null || output == null || tolText == null)
            {
                Console.Error.WriteLine("--in / --out / --tolerance が必要です");
                Usage();
                return 1;
            }
            double tolerance;
            if (!double.TryParse(tolText, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance) || tolerance <= 0)
                throw new ArgumentException("--tolerance は正の数（度）で指定してください: " + tolText);

            var files = new List<string>();
            bool isDir = Directory.Exists(input);
            if (isDir)
            {
                files.AddRange(Directory.GetFiles(input, "*.geojson", SearchOption.TopDirectoryOnly));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                Directory.CreateDirectory(output);
            }
            else
            {
                if (!File.Exists(input)) throw new FileNotFoundException("入力がありません: " + input);
                files.Add(input);
                string parent = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            }

            long before = 0, after = 0, features = 0, dropped = 0;
            long bytesIn = 0, bytesOut = 0;

            for (int f = 0; f < files.Count; f++)
            {
                string src = files[f];
                string dst = isDir ? Path.Combine(output, Path.GetFileName(src)) : output;
                bytesIn += new FileInfo(src).Length;

                using (var stream = new FeatureStream(src))
                using (var w = new GeoJsonWriter(dst, Path.GetFileNameWithoutExtension(dst)))
                {
                    if (!stream.MoveToFeatures()) continue;
                    string raw;
                    while ((raw = stream.NextFeature()) != null)
                    {
                        features++;
                        List<FeatureRef> refs = GeoJson.Scan(raw);
                        if (refs.Count == 0 || !GeoJson.HasGeometry(refs[0])) { w.WriteFeature(raw); continue; }

                        FeatureRef fr = refs[0];
                        string geom = raw.Substring(fr.GeomStart, fr.GeomEnd - fr.GeomStart);
                        string simplified = Simplify.Geometry(geom, tolerance, ref before, ref after);
                        if (simplified == "null") { dropped++; }
                        w.WriteFeature(raw.Substring(0, fr.GeomStart) + simplified + raw.Substring(fr.GeomEnd));
                    }
                }
                bytesOut += new FileInfo(dst).Length;
            }

            double reduction = (before > 0) ? (100.0 * (1.0 - (double)after / before)) : 0;
            Console.WriteLine("許容誤差: " + tolerance.ToString("R", CultureInfo.InvariantCulture)
                + " 度（緯度換算で約 " + (tolerance * 111320).ToString("N0", CultureInfo.InvariantCulture) + " m）");
            Console.WriteLine("対象: " + files.Count.ToString(CultureInfo.InvariantCulture) + " ファイル / "
                + features.ToString(CultureInfo.InvariantCulture) + " フィーチャ");
            Console.WriteLine("頂点: " + before.ToString("N0", CultureInfo.InvariantCulture) + " -> "
                + after.ToString("N0", CultureInfo.InvariantCulture)
                + "  削減 " + reduction.ToString("N2", CultureInfo.InvariantCulture) + " %");
            Console.WriteLine("サイズ: " + (bytesIn / 1048576.0).ToString("N1", CultureInfo.InvariantCulture) + " MB -> "
                + (bytesOut / 1048576.0).ToString("N1", CultureInfo.InvariantCulture) + " MB");
            if (dropped > 0)
                Console.WriteLine("ジオメトリが消えたフィーチャ: " + dropped.ToString(CultureInfo.InvariantCulture) + " 件（許容誤差が大きすぎます）");
            return 0;
        }

        /*** merge（検証・再分割用） ***/

        static int CmdMerge(string[] args)
        {
            string inDir = GetArg(args, "--in");
            string output = GetArg(args, "--out");
            if (inDir == null || output == null) { Usage(); return 1; }
            if (!Directory.Exists(inDir)) throw new DirectoryNotFoundException("入力フォルダがありません: " + inDir);

            string[] files = Directory.GetFiles(inDir, "*.geojson", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            int total = 0;
            using (var w = new GeoJsonWriter(output, Path.GetFileNameWithoutExtension(output)))
            {
                for (int i = 0; i < files.Length; i++)
                {
                    using (var stream = new FeatureStream(files[i]))
                    {
                        if (!stream.MoveToFeatures()) continue;
                        string raw;
                        while ((raw = stream.NextFeature()) != null)
                        {
                            w.WriteFeature(raw);
                            total++;
                        }
                    }
                }
            }
            Console.WriteLine("連結: " + files.Length.ToString(CultureInfo.InvariantCulture) + " ファイル / "
                + total.ToString(CultureInfo.InvariantCulture) + " フィーチャ -> " + Path.GetFullPath(output));
            return 0;
        }

        /*** 雑用 ***/

        static string GetArg(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        static string OnlyDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') sb.Append(c);
            }
            return sb.ToString();
        }

        static Dictionary<string, string> BuildPrefNames()
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] names = {
                "01","hokkaido", "02","aomori", "03","iwate", "04","miyagi", "05","akita",
                "06","yamagata", "07","fukushima", "08","ibaraki", "09","tochigi", "10","gunma",
                "11","saitama", "12","chiba", "13","tokyo", "14","kanagawa", "15","niigata",
                "16","toyama", "17","ishikawa", "18","fukui", "19","yamanashi", "20","nagano",
                "21","gifu", "22","shizuoka", "23","aichi", "24","mie", "25","shiga",
                "26","kyoto", "27","osaka", "28","hyogo", "29","nara", "30","wakayama",
                "31","tottori", "32","shimane", "33","okayama", "34","hiroshima", "35","yamaguchi",
                "36","tokushima", "37","kagawa", "38","ehime", "39","kochi", "40","fukuoka",
                "41","saga", "42","nagasaki", "43","kumamoto", "44","oita", "45","miyazaki",
                "46","kagoshima", "47","okinawa"
            };
            for (int i = 0; i + 1 < names.Length; i += 2) m[names[i]] = names[i + 1];
            return m;
        }

        static Dictionary<string, string> BuildFolderSuffix()
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            m["1saibun"] = "area";
            m["hukenyohoukutou"] = "forecast";
            m["sikutyousonnwomatometatiikitou"] = "region";
            m["sityousontou"] = "region";
            return m;
        }
    }
}
