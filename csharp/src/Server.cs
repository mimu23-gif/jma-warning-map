using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using JmaMap.Tools;

namespace JmaMap
{
    // ローカル専用のHTTPサーバ。GAS版の doGet / google.script.run に対応する。
    // localhost 固定でバインドするため、URL ACL 登録も管理者権限もファイアウォール許可も要らない。
    public class Server
    {
        readonly Settings cfg;
        readonly GeoStore store;
        readonly object indexGate = new object();
        readonly object pointsGate = new object();

        HttpListener listener;
        Thread acceptThread;
        volatile bool running;

        GeoIndex index;
        PointsData points;
        DateTime pointsStamp = DateTime.MinValue;

        // 簡略化したジオメトリのキャッシュ。キーは「ファイル|geometryの位置|許容誤差」。
        // 間引き後は元の数%まで小さくなるので、全ズーム段を載せてもメモリは知れている。
        readonly Dictionary<string, string> simplifiedCache = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly object simplifyGate = new object();

        // フィーチャごとの外接矩形。ビューポート絞り込みの判定に使う。
        readonly Dictionary<string, double[]> bboxCache = new Dictionary<string, double[]>(StringComparer.Ordinal);
        readonly object bboxGate = new object();

        public int Port;
        public string BaseUrl = "";
        public Action<string> Log;

        public Server(Settings cfg)
        {
            this.cfg = cfg;
            this.store = new GeoStore(cfg.GeoCacheFiles);
        }

        void Trace(string msg)
        {
            Action<string> log = Log;
            if (log != null) log(msg);
        }

        /*** 起動・停止 ***/

        // 設定ポートから順に空きを探す。掴めたポートで BaseUrl を組み立てる。
        public bool Start()
        {
            for (int i = 0; i < cfg.PortTries; i++)
            {
                int port = cfg.Port + i;
                var l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                try
                {
                    l.Start();
                }
                catch (Exception ex)
                {
                    Trace("ポート " + port.ToString(CultureInfo.InvariantCulture) + " は使用できません: " + ex.Message);
                    continue;
                }

                listener = l;
                Port = port;
                BaseUrl = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                running = true;
                acceptThread = new Thread(AcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Start();
                Trace("待ち受け開始: " + BaseUrl);
                return true;
            }
            return false;
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); }
            catch (Exception) { }
        }

        void AcceptLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch (Exception)
                {
                    if (!running) return;
                    continue;
                }
                ThreadPool.QueueUserWorkItem(HandleAsync, ctx);
            }
        }

        void HandleAsync(object state)
        {
            var ctx = (HttpListenerContext)state;
            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                Trace("要求処理でエラー: " + ex.Message);
                try
                {
                    ctx.Response.StatusCode = 500;
                    WriteText(ctx, "application/json; charset=utf-8",
                        "{\"error\":" + Json.Quote(ex.Message) + "}");
                }
                catch (Exception) { }
            }
            finally
            {
                try { ctx.Response.Close(); }
                catch (Exception) { }
            }
        }

        /*** ルーティング ***/

        void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (path == "/" || path == "/index.html" || path == "/map.html")
            {
                ServeStatic(ctx, "map.html", "text/html; charset=utf-8");
                return;
            }
            if (path == "/leaflet.js")
            {
                ServeStatic(ctx, "leaflet.js", "application/javascript; charset=utf-8");
                return;
            }
            if (path == "/leaflet.css")
            {
                ServeStatic(ctx, "leaflet.css", "text/css; charset=utf-8");
                return;
            }
            if (path == "/api/points")
            {
                WriteText(ctx, "application/json; charset=utf-8", PointsCsv.ToJson(GetPoints()));
                return;
            }
            if (path == "/api/warnings")
            {
                ServeWarnings(ctx);
                return;
            }
            if (path == "/api/status")
            {
                WriteText(ctx, "application/json; charset=utf-8", StatusJson());
                return;
            }
            if (path == "/api/quake")
            {
                ServeQuakeList(ctx);
                return;
            }
            if (path == "/api/quake/intensity")
            {
                ServeQuakeIntensity(ctx);
                return;
            }
            if (path == "/api/typhoon")
            {
                ServeTyphoon(ctx);
                return;
            }
            if (path == "/api/volcano")
            {
                ServeVolcano(ctx);
                return;
            }
            if (path == "/api/flood")
            {
                ServeFlood(ctx);
                return;
            }
            if (path == "/api/tsunami")
            {
                ServeTsunami(ctx);
                return;
            }
            ctx.Response.StatusCode = 404;
            WriteText(ctx, "text/plain; charset=utf-8", "404 Not Found");
        }

        // 配信対象は呼び出し側が渡すファイル名だけ（外から任意パスを指定させない）
        void ServeStatic(HttpListenerContext ctx, string fileName, string contentType)
        {
            string full = Path.Combine(cfg.Resolve(cfg.WebDir), fileName);
            if (!File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                WriteText(ctx, "text/plain; charset=utf-8", "ファイルがありません: " + fileName);
                return;
            }
            byte[] bytes = File.ReadAllBytes(full);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        static void WriteText(HttpListenerContext ctx, string contentType, string body)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        string StatusJson()
        {
            GeoIndex idx;
            lock (indexGate) { idx = index; }
            var sb = new StringBuilder();
            sb.Append("{\"port\":").Append(Port.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"indexBuilt\":").Append(idx != null ? "true" : "false");

            // クライアントはこの表を見て「ズームが変わったら取り直すか」を判断する
            sb.Append(",\"zoomTiers\":[");
            for (int i = 0; i < cfg.ZoomTolerances.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(cfg.ZoomTolerances[i][0].ToString("R", CultureInfo.InvariantCulture));
                sb.Append(',').Append(cfg.ZoomTolerances[i][1].ToString("R", CultureInfo.InvariantCulture)).Append(']');
            }
            sb.Append(']');
            if (idx != null)
            {
                sb.Append(",\"files\":").Append(idx.FileCount.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"raw\":").Append(idx.Raw.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"norm6\":").Append(idx.Norm6.Count.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return sb.ToString();
        }

        /*** POI ***/

        public PointsData GetPoints()
        {
            string path = cfg.Resolve(cfg.PointsCsvPath);
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (pointsGate)
            {
                if (points != null && stamp == pointsStamp) return points;
            }
            PointsData loaded = PointsCsv.Load(path);
            lock (pointsGate)
            {
                points = loaded;
                pointsStamp = stamp;
            }
            return loaded;
        }

        /*** 索引 ***/

        public GeoIndex GetIndex()
        {
            lock (indexGate)
            {
                if (index != null) return index;

                List<string> files = GeoIndex.ListGeoFiles(cfg.ResolvedGeoFolders());
                if (files.Count == 0)
                    throw new InvalidOperationException("境界GeoJSONが見つかりません。settings.json の dataDir / geoFolders を確認してください。");

                string signature = GeoIndex.ComputeSignature(files);
                string cachePath = cfg.Resolve(cfg.IndexCachePath);

                GeoIndex loaded = GeoIndex.Load(cachePath, signature);
                if (loaded != null)
                {
                    Trace("索引キャッシュを読み込み: " + cachePath);
                    index = loaded;
                    return index;
                }

                Trace("索引を構築します（" + files.Count.ToString(CultureInfo.InvariantCulture) + " ファイル）…");
                GeoIndex built = GeoIndex.Build(files, Trace);
                try
                {
                    built.Save(cachePath);
                    Trace("索引キャッシュを保存: " + cachePath);
                }
                catch (Exception ex)
                {
                    Trace("索引キャッシュの保存に失敗（動作には影響しません）: " + ex.Message);
                }
                index = built;
                return index;
            }
        }

        public void RebuildIndex()
        {
            lock (indexGate) { index = null; }
            try
            {
                string cachePath = cfg.Resolve(cfg.IndexCachePath);
                if (File.Exists(cachePath)) File.Delete(cachePath);
            }
            catch (Exception) { }
            GetIndex();
        }

        /*** 警報 → FeatureCollection ***/

        class Pending
        {
            public string Key;        // このファイル内で一致させるコード
            public WarnItem Item;
        }

        /// <summary>
        /// GeoJSONファイル1つぶんの「コード → フィーチャ」索引。
        /// 7桁一致を優先し、外れたら先頭6桁で代表フィーチャを拾う（Code.js と同じ規則）。
        /// </summary>
        class CodeMap
        {
            readonly Dictionary<string, FeatureRef> map7 = new Dictionary<string, FeatureRef>(StringComparer.Ordinal);
            readonly Dictionary<string, FeatureRef> map6 = new Dictionary<string, FeatureRef>(StringComparer.Ordinal);

            public CodeMap(GeoFile gf)
            {
                for (int f = 0; f < gf.Features.Count; f++)
                {
                    FeatureRef fr = gf.Features[f];
                    if (!GeoJson.HasGeometry(fr)) continue;
                    string propCode = GeoIndex.NormalizeCode(fr.Code);
                    if (propCode.Length == 7)
                    {
                        if (!map7.ContainsKey(propCode)) map7[propCode] = fr;
                        string h6 = propCode.Substring(0, 6);
                        if (!map6.ContainsKey(h6)) map6[h6] = fr;
                    }
                    else if (propCode.Length == 6)
                    {
                        if (!map6.ContainsKey(propCode)) map6[propCode] = fr;
                    }
                }
            }

            public FeatureRef Find(string key)
            {
                FeatureRef ft;
                if (map7.TryGetValue(key, out ft)) return ft;
                if (map6.TryGetValue(Head6(key), out ft)) return ft;
                return null;
            }
        }

        void ServeWarnings(HttpListenerContext ctx)
        {
            HashSet<string> levels = null;
            HashSet<string> phenomena = null;
            double zoom = -1;
            double[] view = null;

            if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = sr.ReadToEnd();
                }
                if (body != null && body.Trim().Length > 0)
                {
                    var args = Json.Obj(Json.Parse(body));
                    levels = ToSet(Json.Arr(Json.Get(args, "levels")));
                    phenomena = ToSet(Json.Arr(Json.Get(args, "phenomena")));

                    object z = Json.Get(args, "zoom");
                    if (z != null)
                        double.TryParse(Json.Str(z), NumberStyles.Float, CultureInfo.InvariantCulture, out zoom);

                    view = BBoxFromJson(Json.Arr(Json.Get(args, "bbox")));
                }
            }

            // ズーム未指定なら最も粗い段（全国表示相当）を使う
            double tolerance = (zoom >= 0) ? cfg.ToleranceForZoom(zoom) : cfg.ToleranceForZoom(0);

            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.SendChunked = true;
            using (var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
            {
                WriteWarnings(w, levels, phenomena, tolerance, view);
            }
        }

        static HashSet<string> ToSet(List<object> list)
        {
            if (list == null || list.Count == 0) return null;   // 空＝絞り込みなし（GAS版と同じ）
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++) set.Add(Json.Str(list[i]));
            return set;
        }

        static string Head6(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return (s.Length >= 6) ? s.Substring(0, 6) : s;
        }

        public void WriteWarnings(TextWriter w, HashSet<string> levels, HashSet<string> phenomena)
        {
            WriteWarnings(w, levels, phenomena, 0, null);
        }

        public void WriteWarnings(TextWriter w, HashSet<string> levels, HashSet<string> phenomena, double tolerance)
        {
            WriteWarnings(w, levels, phenomena, tolerance, null);
        }

        /// <summary>
        /// 現況警報をFeatureCollectionとして書き出す。
        /// tolerance &gt; 0 なら、その許容誤差（度）でジオメトリを間引いてから送る。
        /// view（[西,南,東,北]）を渡すと、そこに重ならないフィーチャは送らない。
        /// </summary>
        public void WriteWarnings(TextWriter w, HashSet<string> levels, HashSet<string> phenomena,
                                  double tolerance, double[] view)
        {
            GeoIndex idx = GetIndex();
            List<WarnItem> items = Jma.GetActiveWarnings();
            Dictionary<string, string> class20Parent = Jma.Class20Parent();

            var byFile = new Dictionary<string, List<Pending>>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string[]>();

            // いま全国で発表されている現象コードとレベルを、絞り込みをかける前に集めておく。
            // 画面のフィルタはこれを使って「発表されていない種別」を隠す。
            var availableCodes = new List<string>();
            var availableLevels = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                WarnItem it = items[i];
                if (!availableLevels.Contains(it.Level)) availableLevels.Add(it.Level);
                if (it.Codes == null) continue;
                for (int c = 0; c < it.Codes.Count; c++)
                {
                    if (!availableCodes.Contains(it.Codes[c])) availableCodes.Add(it.Codes[c]);
                }
            }
            availableCodes.Sort(StringComparer.Ordinal);

            for (int i = 0; i < items.Count; i++)
            {
                WarnItem it = items[i];
                if (levels != null && !levels.Contains(it.Level)) continue;
                if (phenomena != null && !AnyIn(it.Codes, phenomena)) continue;

                IndexEntry pick = idx.Find(it.RegionCode, false);
                if (pick == null && class20Parent != null)
                {
                    // 政令市の区分割など、GeoJSON側がそこまで細分化されていないコードは親へ寄せる
                    string parent;
                    if (class20Parent.TryGetValue(it.RegionCode, out parent)) pick = idx.Find(parent, false);
                }
                if (pick == null)
                {
                    unresolved.Add(new string[] { it.RegionCode, "INDEX_NOT_FOUND" });
                    continue;
                }

                List<Pending> list;
                if (!byFile.TryGetValue(pick.File, out list))
                {
                    list = new List<Pending>();
                    byFile[pick.File] = list;
                }
                var p = new Pending();
                string norm = GeoIndex.NormalizeCode(pick.Raw);
                p.Key = (norm.Length > 0) ? norm : pick.Raw;
                p.Item = it;
                list.Add(p);
            }

            string updatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            w.Write("{\"type\":\"FeatureCollection\",\"features\":[");

            bool first = true;
            foreach (var kv in byFile)
            {
                GeoFile gf;
                try
                {
                    gf = store.Get(kv.Key);
                }
                catch (Exception ex)
                {
                    Trace("GeoJSON読み込み失敗 " + Path.GetFileName(kv.Key) + ": " + ex.Message);
                    for (int i = 0; i < kv.Value.Count; i++)
                        unresolved.Add(new string[] { kv.Value[i].Item.RegionCode, "GEOJSON_LOAD_FAILED" });
                    continue;
                }

                var codeMap = new CodeMap(gf);

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    Pending p = kv.Value[i];
                    FeatureRef ft = codeMap.Find(p.Key);
                    if (ft == null)
                    {
                        unresolved.Add(new string[] { p.Item.RegionCode, "FEATURE_NOT_FOUND" });
                        continue;
                    }
                    // 画面外のエリアは送らない（未解決には数えない。表示範囲の都合で省いただけなので）
                    if (OutsideView(kv.Key, gf, ft, view)) continue;

                    if (!first) w.Write(',');
                    first = false;
                    WriteFeature(w, GetGeometryJson(kv.Key, gf, ft, tolerance), ft, p.Item, updatedAt);
                }
            }

            w.Write("],\"unresolved\":[");
            for (int i = 0; i < unresolved.Count; i++)
            {
                if (i > 0) w.Write(',');
                w.Write("{\"code\":");
                w.Write(Json.Quote(unresolved[i][0]));
                w.Write(",\"reason\":");
                w.Write(Json.Quote(unresolved[i][1]));
                w.Write('}');
            }
            w.Write("],\"available\":{\"levels\":");
            WriteStrArray(w, availableLevels);
            w.Write(",\"codes\":");
            WriteStrArray(w, availableCodes);
            w.Write("},\"updatedAt\":");
            w.Write(Json.Quote(updatedAt));
            w.Write('}');
        }

        /*** ビューポート絞り込み ***/

        /// <summary>
        /// フィーチャの外接矩形を返す（[minLon, minLat, maxLon, maxLat]）。
        /// geometry の中の数値は座標しか無いので、2つずつ組にして走査するだけで求まる。
        /// 一度計算したらファイル内の位置をキーにキャッシュする。
        /// </summary>
        double[] FeatureBBox(string filePath, GeoFile gf, FeatureRef ft)
        {
            string key = filePath + "|" + ft.GeomStart.ToString(CultureInfo.InvariantCulture);
            lock (bboxGate)
            {
                double[] hit;
                if (bboxCache.TryGetValue(key, out hit)) return hit;
            }

            double minLon = double.MaxValue, minLat = double.MaxValue;
            double maxLon = double.MinValue, maxLat = double.MinValue;

            string s = gf.Text;
            int i = ft.GeomStart;
            int end = ft.GeomEnd;
            bool haveLon = false;
            double lon = 0;

            while (i < end)
            {
                char c = s[i];
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int start = i;
                    i++;
                    while (i < end)
                    {
                        char d = s[i];
                        if ((d >= '0' && d <= '9') || d == '.' || d == 'e' || d == 'E' || d == '+' || d == '-') i++;
                        else break;
                    }
                    double v;
                    if (double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v))
                    {
                        if (!haveLon) { lon = v; haveLon = true; }
                        else
                        {
                            if (lon < minLon) minLon = lon;
                            if (lon > maxLon) maxLon = lon;
                            if (v < minLat) minLat = v;
                            if (v > maxLat) maxLat = v;
                            haveLon = false;
                        }
                    }
                    continue;
                }
                // "type": "MultiPolygon" のような文字列は中身を見ない
                if (c == '"') { Json.SkipString(s, ref i); continue; }
                i++;
            }

            double[] box = (minLon <= maxLon)
                ? new double[] { minLon, minLat, maxLon, maxLat }
                : null;

            lock (bboxGate)
            {
                bboxCache[key] = box;
            }
            return box;
        }

        // bbox が指定されていて、フィーチャがそこに全く重ならなければ送らない
        bool OutsideView(string filePath, GeoFile gf, FeatureRef ft, double[] view)
        {
            if (view == null) return false;
            double[] b = FeatureBBox(filePath, gf, ft);
            if (b == null) return false;      // 範囲が取れないものは落とさない
            if (b[2] < view[0] || b[0] > view[2]) return true;
            if (b[3] < view[1] || b[1] > view[3]) return true;
            return false;
        }

        // "west,south,east,north" を読む。日付変更線をまたぐ指定は絞り込まない。
        static double[] ParseBBox(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string[] parts = raw.Split(',');
            if (parts.Length < 4) return null;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;
            }
            if (v[0] > v[2]) return null;
            if (v[1] > v[3]) return null;
            return v;
        }

        static double[] BBoxFromJson(List<object> arr)
        {
            if (arr == null || arr.Count < 4) return null;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(Json.Str(arr[i]), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;
            }
            if (v[0] > v[2] || v[1] > v[3]) return null;
            return v;
        }

        /// <summary>
        /// 送信するジオメトリを返す。tolerance が 0 なら原文そのまま、そうでなければ
        /// 間引いた結果をキャッシュから返す（同じズーム段の2回目以降は再計算しない）。
        /// </summary>
        string GetGeometryJson(string filePath, GeoFile gf, FeatureRef ft, double tolerance)
        {
            string raw = gf.Text.Substring(ft.GeomStart, ft.GeomEnd - ft.GeomStart);
            if (tolerance <= 0) return raw;

            string key = filePath + "|" + ft.GeomStart.ToString(CultureInfo.InvariantCulture)
                       + "|" + tolerance.ToString("R", CultureInfo.InvariantCulture);
            lock (simplifyGate)
            {
                string hit;
                if (simplifiedCache.TryGetValue(key, out hit)) return hit;
            }

            long before = 0, after = 0;
            string simplified;
            try
            {
                simplified = Simplify.Geometry(raw, tolerance, ref before, ref after);
            }
            catch (Exception ex)
            {
                Trace("簡略化に失敗（原文を送ります）: " + ex.Message);
                return raw;
            }

            lock (simplifyGate)
            {
                simplifiedCache[key] = simplified;
            }
            return simplified;
        }

        static void WriteFeature(TextWriter w, string geometryJson, FeatureRef ft, WarnItem item, string updatedAt)
        {
            w.Write("{\"type\":\"Feature\",\"properties\":{\"code\":");
            w.Write(Json.Quote(item.RegionCode));
            w.Write(",\"name\":");
            w.Write(Json.Quote(ft.Name));
            w.Write(",\"level\":");
            w.Write(Json.Quote(item.Level));
            w.Write(",\"codes\":");
            WriteStrArray(w, item.Codes);
            w.Write(",\"kinds\":");
            WriteStrArray(w, item.Kinds);
            w.Write(",\"updatedAt\":");
            w.Write(Json.Quote(updatedAt));
            w.Write("},\"geometry\":");
            w.Write(geometryJson);
            w.Write('}');
        }

        static void WriteStrArray(TextWriter w, List<string> list)
        {
            w.Write('[');
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) w.Write(',');
                    w.Write(Json.Quote(list[i]));
                }
            }
            w.Write(']');
        }

        static bool AnyIn(List<string> codes, HashSet<string> filter)
        {
            if (codes == null) return false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (filter.Contains(codes[i])) return true;
            }
            return false;
        }

        /*** 災害情報（地震・台風）***/

        /// <summary>
        /// 地域コードを1つ以上の索引エントリへ解決する。
        /// 7桁一致 → 6桁 → 政令市の親コード → 気象警報用に細分された区域（市全体のコードで来る情報向け）。
        /// </summary>
        static List<IndexEntry> ResolveEntries(GeoIndex idx, Dictionary<string, string> class20Parent, string code)
        {
            var list = new List<IndexEntry>(1);
            IndexEntry pick = idx.Find(code, false);
            if (pick == null && class20Parent != null)
            {
                string parent;
                if (class20Parent.TryGetValue(code, out parent)) pick = idx.Find(parent, false);
            }
            if (pick != null) { list.Add(pick); return list; }
            return idx.FindSubdivisions(code);
        }

        // 震度の強さ順。同じポリゴンに複数の市区町村が寄ったときは強いほうを残す。
        static int ShindoRank(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            switch (s.Trim())
            {
                case "1": return 1;
                case "2": return 2;
                case "3": return 3;
                case "4": return 4;
                case "5-": return 5;
                case "5+": return 6;
                case "6-": return 7;
                case "6+": return 8;
                case "7": return 9;
                default: return 0;
            }
        }

        static double QueryNum(HttpListenerContext ctx, string name, double fallback)
        {
            string raw = ctx.Request.QueryString[name];
            if (string.IsNullOrEmpty(raw)) return fallback;
            double v;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        void ServeQuakeList(HttpListenerContext ctx)
        {
            double hours = QueryNum(ctx, "hours", 24);
            int max = (int)QueryNum(ctx, "max", 20);
            List<QuakeItem> items = Disaster.GetRecentQuakes(hours, max);

            var sb = new StringBuilder();
            sb.Append("{\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                QuakeItem q = items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"eid\":").Append(Json.Quote(q.Eid));
                sb.Append(",\"title\":").Append(Json.Quote(q.Title));
                sb.Append(",\"reportedAt\":").Append(Json.Quote(q.ReportedAt));
                sb.Append(",\"originTime\":").Append(Json.Quote(q.OriginTime));
                sb.Append(",\"hypocenter\":").Append(Json.Quote(q.Hypocenter));
                sb.Append(",\"magnitude\":").Append(Json.Quote(q.Magnitude));
                sb.Append(",\"maxInt\":").Append(Json.Quote(q.MaxInt));
                sb.Append(",\"cities\":").Append(q.Cities.Count.ToString(CultureInfo.InvariantCulture));
                if (q.HasCoord)
                {
                    sb.Append(",\"lat\":").Append(q.Lat.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"lon\":").Append(q.Lon.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"depthKm\":").Append(q.DepthKm.ToString("R", CultureInfo.InvariantCulture));
                }
                sb.Append('}');
            }
            sb.Append("]}");
            WriteText(ctx, "application/json; charset=utf-8", sb.ToString());
        }

        void ServeQuakeIntensity(HttpListenerContext ctx)
        {
            string eid = "";
            double zoom = -1;

            if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = sr.ReadToEnd();
                }
                if (body != null && body.Trim().Length > 0)
                {
                    var args = Json.Obj(Json.Parse(body));
                    eid = Json.GetStr(args, "eid").Trim();
                    object z = Json.Get(args, "zoom");
                    if (z != null)
                        double.TryParse(Json.Str(z), NumberStyles.Float, CultureInfo.InvariantCulture, out zoom);
                }
            }
            else
            {
                eid = ctx.Request.QueryString["eid"];
                if (eid == null) eid = "";
                zoom = QueryNum(ctx, "zoom", -1);
            }

            // 一覧に出していない古い地震を指定されても拾えるよう、範囲は広めに取る
            List<QuakeItem> items = Disaster.GetRecentQuakes(72, 100);
            QuakeItem q = (eid.Length > 0) ? Disaster.FindQuake(items, eid)
                                           : (items.Count > 0 ? items[0] : null);

            double tolerance = (zoom >= 0) ? cfg.ToleranceForZoom(zoom) : cfg.ToleranceForZoom(0);
            double[] view = ParseBBox(ctx.Request.QueryString["bbox"]);

            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.SendChunked = true;
            using (var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
            {
                WriteQuakeIntensity(w, q, tolerance, view);
            }
        }

        /// <summary>
        /// 市区町村別の震度を、警報と同じ境界データのポリゴンに載せて書き出す。
        /// コードの解決規則（7桁一致 → 6桁 → 政令市の親コード）は警報と共通。
        /// </summary>
        public void WriteQuakeIntensity(TextWriter w, QuakeItem q, double tolerance)
        {
            WriteQuakeIntensity(w, q, tolerance, null);
        }

        public void WriteQuakeIntensity(TextWriter w, QuakeItem q, double tolerance, double[] view)
        {
            if (q == null)
            {
                w.Write("{\"type\":\"FeatureCollection\",\"features\":[],\"unresolved\":[],\"quake\":null}");
                return;
            }

            // 市区町村ごとの震度を、他の災害情報と同じ「コード一覧を塗る」形に落とす。
            // 同じポリゴンに複数の市区町村が寄ったときは、強い震度が残る（Rank比較）。
            var items = new List<PaintItem>(q.Cities.Count);
            for (int i = 0; i < q.Cities.Count; i++)
            {
                QuakeCityInt ci = q.Cities[i];
                var p = new PaintItem();
                p.Code = ci.Code;
                p.Group = "quake";
                p.Rank = ShindoRank(ci.Shindo);
                p.PropsJson = ",\"shindo\":" + Json.Quote(ci.Shindo);
                items.Add(p);
            }

            var unresolved = new List<string[]>();
            w.Write("{\"type\":\"FeatureCollection\",\"features\":[");
            WritePaintedFeatures(w, items, tolerance, unresolved, view);
            w.Write("],\"unresolved\":");
            WriteUnresolved(w, unresolved);
            w.Write(",\"quake\":{\"eid\":");
            w.Write(Json.Quote(q.Eid));
            w.Write(",\"title\":");
            w.Write(Json.Quote(q.Title));
            w.Write(",\"originTime\":");
            w.Write(Json.Quote(q.OriginTime));
            w.Write(",\"hypocenter\":");
            w.Write(Json.Quote(q.Hypocenter));
            w.Write(",\"magnitude\":");
            w.Write(Json.Quote(q.Magnitude));
            w.Write(",\"maxInt\":");
            w.Write(Json.Quote(q.MaxInt));
            if (q.HasCoord)
            {
                w.Write(",\"lat\":");
                w.Write(q.Lat.ToString("R", CultureInfo.InvariantCulture));
                w.Write(",\"lon\":");
                w.Write(q.Lon.ToString("R", CultureInfo.InvariantCulture));
                w.Write(",\"depthKm\":");
                w.Write(q.DepthKm.ToString("R", CultureInfo.InvariantCulture));
            }
            w.Write("}}");
        }

        void ServeTyphoon(HttpListenerContext ctx)
        {
            List<TyphoonItem> items = Disaster.GetTyphoons();
            var sb = new StringBuilder();
            sb.Append("{\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                TyphoonItem t = items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(Json.Quote(t.Id));
                sb.Append(",\"number\":").Append(Json.Quote(t.Number));
                sb.Append(",\"nameJp\":").Append(Json.Quote(t.NameJp));
                sb.Append(",\"nameEn\":").Append(Json.Quote(t.NameEn));
                sb.Append(",\"category\":").Append(Json.Quote(t.Category));
                sb.Append(",\"issue\":").Append(Json.Quote(t.Issue));
                sb.Append(",\"trackPre\":");
                AppendTrack(sb, t.TrackPre);
                sb.Append(",\"trackTyphoon\":");
                AppendTrack(sb, t.TrackTyphoon);
                if (t.HasGale)
                {
                    sb.Append(",\"gale\":{\"lat\":").Append(t.GaleLat.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"lon\":").Append(t.GaleLon.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"radius\":").Append(t.GaleRadiusM.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append('}');
                }
                sb.Append(",\"points\":[");
                for (int k = 0; k < t.Points.Count; k++)
                {
                    TyphoonPoint p = t.Points[k];
                    if (k > 0) sb.Append(',');
                    sb.Append("{\"part\":").Append(Json.Quote(p.Part));
                    sb.Append(",\"advancedHours\":").Append(p.AdvancedHours.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"validTime\":").Append(Json.Quote(p.ValidTime));
                    sb.Append(",\"lat\":").Append(p.Lat.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"lon\":").Append(p.Lon.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"radius\":").Append(p.CircleRadiusM.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(",\"category\":").Append(Json.Quote(p.Category));
                    sb.Append(",\"pressure\":").Append(Json.Quote(p.Pressure));
                    sb.Append(",\"wind\":").Append(Json.Quote(p.WindSustained));
                    sb.Append(",\"gust\":").Append(Json.Quote(p.WindGust));
                    sb.Append(",\"course\":").Append(Json.Quote(p.Course));
                    sb.Append(",\"speed\":").Append(Json.Quote(p.Speed));
                    sb.Append(",\"location\":").Append(Json.Quote(p.Location));
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            WriteText(ctx, "application/json; charset=utf-8", sb.ToString());
        }

        static void AppendTrack(StringBuilder sb, List<double[]> track)
        {
            sb.Append('[');
            if (track != null)
            {
                for (int i = 0; i < track.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(track[i][0].ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(',').Append(track[i][1].ToString("R", CultureInfo.InvariantCulture)).Append(']');
                }
            }
            sb.Append(']');
        }

        /*** 噴火警報・降灰予報・指定河川洪水予報 ***/

        // 「地域コードの一覧をポリゴンに塗る」だけの共通処理。
        // 塗る対象がどの情報かは PropsJson（properties へ足す JSON 断片）で区別する。
        class PaintItem
        {
            public string Code;        // フィード側のコード（表示用）
            public string Group;       // 重なりを判定する単位。同じGroup同士だけを突き合わせる
            public int Rank;           // 同じポリゴン・同じGroupで重なったとき、大きいほうを残す
            public string PropsJson;   // 例: ",\"kind\":\"volcano\",\"volcano\":\"桜島\""
        }

        class PaintResolved
        {
            public string Key;
            public string Code;
            public int Rank;
            public string PropsJson;
        }

        /// <summary>
        /// コード一覧を境界ポリゴンへ解決して FeatureCollection の features 部分を書き出す。
        /// 解決規則（7桁一致 → 6桁 → 政令市の親コード）は警報・震度と共通。
        /// </summary>
        void WritePaintedFeatures(TextWriter w, List<PaintItem> items, double tolerance, List<string[]> unresolved)
        {
            WritePaintedFeatures(w, items, tolerance, unresolved, null);
        }

        void WritePaintedFeatures(TextWriter w, List<PaintItem> items, double tolerance,
                                  List<string[]> unresolved, double[] view)
        {
            GeoIndex idx = GetIndex();
            Dictionary<string, string> class20Parent = Jma.Class20Parent();

            var byFile = new Dictionary<string, List<PaintResolved>>(StringComparer.OrdinalIgnoreCase);
            var best = new Dictionary<string, PaintResolved>(StringComparer.Ordinal);

            for (int i = 0; i < items.Count; i++)
            {
                PaintItem it = items[i];
                if (it.Code == null || it.Code.Length == 0) continue;

                List<IndexEntry> picks = ResolveEntries(idx, class20Parent, it.Code);
                if (picks.Count == 0)
                {
                    unresolved.Add(new string[] { it.Code, "INDEX_NOT_FOUND" });
                    continue;
                }

                for (int e = 0; e < picks.Count; e++)
                {
                    IndexEntry pick = picks[e];
                    string norm = GeoIndex.NormalizeCode(pick.Raw);
                    string key = (norm.Length > 0) ? norm : pick.Raw;
                    string dedup = pick.File + "|" + key + "|" + it.Group;

                    PaintResolved exist;
                    if (best.TryGetValue(dedup, out exist))
                    {
                        // 同じ図形に重なったら強いほうを残す（政令市の区が親ポリゴンへ寄る場合など）
                        if (it.Rank > exist.Rank)
                        {
                            exist.Rank = it.Rank;
                            exist.Code = it.Code;
                            exist.PropsJson = it.PropsJson;
                        }
                        continue;
                    }

                    var r = new PaintResolved();
                    r.Key = key;
                    r.Code = it.Code;
                    r.Rank = it.Rank;
                    r.PropsJson = it.PropsJson;
                    best[dedup] = r;

                    List<PaintResolved> list;
                    if (!byFile.TryGetValue(pick.File, out list))
                    {
                        list = new List<PaintResolved>();
                        byFile[pick.File] = list;
                    }
                    list.Add(r);
                }
            }

            bool first = true;
            foreach (var kv in byFile)
            {
                GeoFile gf;
                try
                {
                    gf = store.Get(kv.Key);
                }
                catch (Exception ex)
                {
                    Trace("GeoJSON読み込み失敗 " + Path.GetFileName(kv.Key) + ": " + ex.Message);
                    for (int i = 0; i < kv.Value.Count; i++)
                        unresolved.Add(new string[] { kv.Value[i].Code, "GEOJSON_LOAD_FAILED" });
                    continue;
                }

                var codeMap = new CodeMap(gf);

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    PaintResolved p = kv.Value[i];
                    FeatureRef ft = codeMap.Find(p.Key);
                    if (ft == null)
                    {
                        unresolved.Add(new string[] { p.Code, "FEATURE_NOT_FOUND" });
                        continue;
                    }
                    if (OutsideView(kv.Key, gf, ft, view)) continue;

                    if (!first) w.Write(',');
                    first = false;
                    w.Write("{\"type\":\"Feature\",\"properties\":{\"code\":");
                    w.Write(Json.Quote(p.Code));
                    w.Write(",\"name\":");
                    w.Write(Json.Quote(ft.Name));
                    w.Write(p.PropsJson);
                    w.Write("},\"geometry\":");
                    w.Write(GetGeometryJson(kv.Key, gf, ft, tolerance));
                    w.Write('}');
                }
            }
        }

        static void WriteUnresolved(TextWriter w, List<string[]> unresolved)
        {
            w.Write("[");
            for (int i = 0; i < unresolved.Count; i++)
            {
                if (i > 0) w.Write(',');
                w.Write("{\"code\":");
                w.Write(Json.Quote(unresolved[i][0]));
                w.Write(",\"reason\":");
                w.Write(Json.Quote(unresolved[i][1]));
                w.Write('}');
            }
            w.Write("]");
        }

        void ServeVolcano(HttpListenerContext ctx)
        {
            double zoom = QueryNum(ctx, "zoom", -1);
            double tolerance = (zoom >= 0) ? cfg.ToleranceForZoom(zoom) : cfg.ToleranceForZoom(0);

            List<VolcanoWarn> warns = Hazards.GetVolcanoWarnings();
            List<AshFall> ashes = Hazards.GetAshFalls(40);

            var items = new List<PaintItem>();
            for (int i = 0; i < warns.Count; i++)
            {
                VolcanoWarn v = warns[i];
                string props = ",\"kind\":\"volcano\",\"volcano\":" + Json.Quote(v.VolcanoName)
                             + ",\"warnName\":" + Json.Quote(v.KindName)
                             + ",\"warnCode\":" + Json.Quote(v.KindCode)
                             + ",\"levelName\":" + Json.Quote(v.LevelName);
                for (int m = 0; m < v.Municipalities.Count; m++)
                {
                    var p = new PaintItem();
                    p.Code = v.Municipalities[m];
                    // 火山ごとに別のフィーチャとして残す（同じ市町村に複数の火山が効くことがある）
                    p.Group = props;
                    p.Rank = Hazards.VolcanoRank(v.KindCode);
                    p.PropsJson = props;
                    items.Add(p);
                }
            }
            for (int i = 0; i < ashes.Count; i++)
            {
                AshFall a = ashes[i];
                string ashProps = ",\"kind\":\"ash\",\"volcano\":" + Json.Quote(a.VolcanoName);
                for (int m = 0; m < a.Ash.Count; m++)
                {
                    var p = new PaintItem();
                    p.Code = a.Ash[m];
                    p.Group = ashProps;
                    p.Rank = 1;
                    p.PropsJson = ashProps;
                    items.Add(p);
                }
                string stoneProps = ",\"kind\":\"stone\",\"volcano\":" + Json.Quote(a.VolcanoName);
                for (int m = 0; m < a.Stone.Count; m++)
                {
                    var p = new PaintItem();
                    p.Code = a.Stone[m];
                    p.Group = stoneProps;
                    p.Rank = 2;
                    p.PropsJson = stoneProps;
                    items.Add(p);
                }
            }

            var unresolved = new List<string[]>();
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.SendChunked = true;
            using (var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
            {
                w.Write("{\"type\":\"FeatureCollection\",\"features\":[");
                WritePaintedFeatures(w, items, tolerance, unresolved, ParseBBox(ctx.Request.QueryString["bbox"]));
                w.Write("],\"unresolved\":");
                WriteUnresolved(w, unresolved);

                w.Write(",\"warnings\":[");
                for (int i = 0; i < warns.Count; i++)
                {
                    VolcanoWarn v = warns[i];
                    if (i > 0) w.Write(',');
                    w.Write("{\"volcano\":");
                    w.Write(Json.Quote(v.VolcanoName));
                    w.Write(",\"volcanoCode\":");
                    w.Write(Json.Quote(v.VolcanoCode));
                    w.Write(",\"warnName\":");
                    w.Write(Json.Quote(v.KindName));
                    w.Write(",\"warnCode\":");
                    w.Write(Json.Quote(v.KindCode));
                    w.Write(",\"levelName\":");
                    w.Write(Json.Quote(v.LevelName));
                    w.Write(",\"reportedAt\":");
                    w.Write(Json.Quote(v.ReportedAt));
                    w.Write(",\"areas\":");
                    WriteStrArray(w, v.Municipalities);
                    w.Write('}');
                }
                w.Write("],\"ash\":[");
                for (int i = 0; i < ashes.Count; i++)
                {
                    AshFall a = ashes[i];
                    if (i > 0) w.Write(',');
                    w.Write("{\"volcano\":");
                    w.Write(Json.Quote(a.VolcanoName));
                    w.Write(",\"reportedAt\":");
                    w.Write(Json.Quote(a.ReportedAt));
                    w.Write(",\"headline\":");
                    w.Write(Json.Quote(a.Headline));
                    w.Write(",\"ashAreas\":");
                    w.Write(a.Ash.Count.ToString(CultureInfo.InvariantCulture));
                    w.Write(",\"stoneAreas\":");
                    w.Write(a.Stone.Count.ToString(CultureInfo.InvariantCulture));
                    if (a.HasCoord)
                    {
                        w.Write(",\"lat\":");
                        w.Write(a.Lat.ToString("R", CultureInfo.InvariantCulture));
                        w.Write(",\"lon\":");
                        w.Write(a.Lon.ToString("R", CultureInfo.InvariantCulture));
                    }
                    w.Write('}');
                }
                w.Write("]}");
            }
        }

        void ServeFlood(HttpListenerContext ctx)
        {
            double zoom = QueryNum(ctx, "zoom", -1);
            double tolerance = (zoom >= 0) ? cfg.ToleranceForZoom(zoom) : cfg.ToleranceForZoom(0);

            List<FloodWarn> floods = Hazards.GetFloodWarnings(60);

            // 同じ府県に複数の河川が出ていることがあるので、最も高いレベルで塗る。
            // 解除済みの河川は一覧には残すが塗らない。
            var items = new List<PaintItem>();
            for (int i = 0; i < floods.Count; i++)
            {
                FloodWarn f = floods[i];
                if (f.Cleared) continue;
                string props = ",\"kind\":\"flood\",\"level\":" + f.Level.ToString(CultureInfo.InvariantCulture)
                             + ",\"warnName\":" + Json.Quote(f.KindName);
                for (int p = 0; p < f.PrefCodes.Count; p++)
                {
                    var it = new PaintItem();
                    it.Code = f.PrefCodes[p];
                    it.Group = props;
                    it.Rank = f.Level;
                    it.PropsJson = props;
                    items.Add(it);
                }
            }

            var unresolved = new List<string[]>();
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.SendChunked = true;
            using (var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
            {
                w.Write("{\"type\":\"FeatureCollection\",\"features\":[");
                WritePaintedFeatures(w, items, tolerance, unresolved, ParseBBox(ctx.Request.QueryString["bbox"]));
                w.Write("],\"unresolved\":");
                WriteUnresolved(w, unresolved);

                w.Write(",\"rivers\":[");
                for (int i = 0; i < floods.Count; i++)
                {
                    FloodWarn f = floods[i];
                    if (i > 0) w.Write(',');
                    w.Write("{\"river\":");
                    w.Write(Json.Quote(f.RiverName));
                    w.Write(",\"riverCode\":");
                    w.Write(Json.Quote(f.RiverCode));
                    w.Write(",\"level\":");
                    w.Write(f.Level.ToString(CultureInfo.InvariantCulture));
                    w.Write(",\"warnName\":");
                    w.Write(Json.Quote(f.KindName));
                    w.Write(",\"title\":");
                    w.Write(Json.Quote(f.Title));
                    w.Write(",\"headline\":");
                    w.Write(Json.Quote(f.Headline));
                    w.Write(",\"reportedAt\":");
                    w.Write(Json.Quote(f.ReportedAt));
                    w.Write(",\"cleared\":");
                    w.Write(f.Cleared ? "true" : "false");
                    w.Write(",\"prefs\":");
                    WriteStrArray(w, f.PrefNames);
                    w.Write(",\"sections\":");
                    WriteStrArray(w, f.Sections);
                    w.Write('}');
                }
                w.Write("]}");
            }
        }

        /// <summary>
        /// 津波予報区データの自己診断。発表が無いときでも「フィードのコードで海岸線を引けるか」
        /// 「線データを間引けるか」を確かめられるようにしてある。
        /// </summary>
        public string CheckTsunamiData(List<string> probeCodes, double tolerance)
        {
            string geoPath = cfg.Resolve(cfg.TsunamiGeoJson);
            if (!File.Exists(geoPath)) return "GeoJSONがありません: " + geoPath;

            GeoFile gf;
            try { gf = store.Get(geoPath); }
            catch (Exception ex) { return "読み込み失敗: " + ex.Message; }

            var byCode = new Dictionary<string, FeatureRef>(StringComparer.Ordinal);
            for (int i = 0; i < gf.Features.Count; i++)
            {
                FeatureRef fr = gf.Features[i];
                if (!GeoJson.HasGeometry(fr)) continue;
                string c = (fr.Code == null) ? "" : fr.Code.Trim();
                if (c.Length > 0 && !byCode.ContainsKey(c)) byCode[c] = fr;
            }

            var sb = new StringBuilder();
            sb.Append("区域 ").Append(byCode.Count.ToString(CultureInfo.InvariantCulture)).Append(" 件収録");
            if (probeCodes != null)
            {
                for (int i = 0; i < probeCodes.Count; i++)
                {
                    string code = probeCodes[i];
                    FeatureRef ft;
                    sb.Append(" / ").Append(code);
                    if (!byCode.TryGetValue(code, out ft)) { sb.Append("→未収録"); continue; }
                    string geom = GetGeometryJson(geoPath, gf, ft, tolerance);
                    string type = geom.IndexOf("MultiLineString", StringComparison.Ordinal) >= 0 ? "MultiLineString"
                                : geom.IndexOf("LineString", StringComparison.Ordinal) >= 0 ? "LineString" : "?";
                    sb.Append("→OK ").Append(ft.Name).Append(' ').Append(type)
                      .Append(' ').Append((geom.Length / 1024).ToString(CultureInfo.InvariantCulture)).Append("KB");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 津波警報・注意報。区域は3桁の津波予報区コードで、6/7桁前提の索引には載らないため、
        /// 専用の海岸線GeoJSON（線データ）を直接引く。
        /// </summary>
        void ServeTsunami(HttpListenerContext ctx)
        {
            double zoom = QueryNum(ctx, "zoom", -1);
            double tolerance = (zoom >= 0) ? cfg.ToleranceForZoom(zoom) : cfg.ToleranceForZoom(0);

            double[] view = ParseBBox(ctx.Request.QueryString["bbox"]);
            TsunamiReport rep = Hazards.GetTsunami();
            string geoPath = cfg.Resolve(cfg.TsunamiGeoJson);

            GeoFile gf = null;
            string loadError = null;
            if (File.Exists(geoPath))
            {
                try { gf = store.Get(geoPath); }
                catch (Exception ex) { loadError = ex.Message; }
            }
            else loadError = "津波予報区のGeoJSONがありません: " + geoPath;

            var byCode = new Dictionary<string, FeatureRef>(StringComparer.Ordinal);
            if (gf != null)
            {
                for (int i = 0; i < gf.Features.Count; i++)
                {
                    FeatureRef fr = gf.Features[i];
                    if (!GeoJson.HasGeometry(fr)) continue;
                    string c = (fr.Code == null) ? "" : fr.Code.Trim();
                    if (c.Length > 0 && !byCode.ContainsKey(c)) byCode[c] = fr;
                }
            }

            var unresolved = new List<string[]>();
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.SendChunked = true;
            using (var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
            {
                w.Write("{\"type\":\"FeatureCollection\",\"features\":[");
                bool first = true;
                for (int i = 0; i < rep.Areas.Count; i++)
                {
                    TsunamiArea a = rep.Areas[i];
                    int rank = Hazards.TsunamiRank(a.KindName);
                    if (rank <= 0) continue;              // 解除・津波なしは描かない

                    FeatureRef ft;
                    if (gf == null || !byCode.TryGetValue(a.Code, out ft))
                    {
                        unresolved.Add(new string[] { a.Code, gf == null ? "GEOJSON_MISSING" : "AREA_NOT_FOUND" });
                        continue;
                    }
                    if (OutsideView(geoPath, gf, ft, view)) continue;

                    if (!first) w.Write(',');
                    first = false;
                    w.Write("{\"type\":\"Feature\",\"properties\":{\"code\":");
                    w.Write(Json.Quote(a.Code));
                    w.Write(",\"name\":");
                    w.Write(Json.Quote(a.Name.Length > 0 ? a.Name : ft.Name));
                    w.Write(",\"kind\":\"tsunami\",\"warnName\":");
                    w.Write(Json.Quote(a.KindName));
                    w.Write(",\"rank\":");
                    w.Write(rank.ToString(CultureInfo.InvariantCulture));
                    w.Write(",\"maxHeight\":");
                    w.Write(Json.Quote(a.MaxHeight));
                    w.Write(",\"firstHeight\":");
                    w.Write(Json.Quote(a.FirstHeight));
                    w.Write("},\"geometry\":");
                    w.Write(GetGeometryJson(geoPath, gf, ft, tolerance));
                    w.Write('}');
                }
                w.Write("],\"unresolved\":");
                WriteUnresolved(w, unresolved);

                w.Write(",\"report\":{\"eventId\":");
                w.Write(Json.Quote(rep.EventId));
                w.Write(",\"title\":");
                w.Write(Json.Quote(rep.Title));
                w.Write(",\"reportedAt\":");
                w.Write(Json.Quote(rep.ReportedAt));
                w.Write(",\"hypocenter\":");
                w.Write(Json.Quote(rep.Hypocenter));
                w.Write(",\"magnitude\":");
                w.Write(Json.Quote(rep.Magnitude));
                w.Write(",\"cleared\":");
                w.Write(rep.Cleared ? "true" : "false");
                w.Write(",\"areas\":[");
                for (int i = 0; i < rep.Areas.Count; i++)
                {
                    TsunamiArea a = rep.Areas[i];
                    if (i > 0) w.Write(',');
                    w.Write("{\"code\":");
                    w.Write(Json.Quote(a.Code));
                    w.Write(",\"name\":");
                    w.Write(Json.Quote(a.Name));
                    w.Write(",\"warnName\":");
                    w.Write(Json.Quote(a.KindName));
                    w.Write(",\"rank\":");
                    w.Write(Hazards.TsunamiRank(a.KindName).ToString(CultureInfo.InvariantCulture));
                    w.Write(",\"maxHeight\":");
                    w.Write(Json.Quote(a.MaxHeight));
                    w.Write('}');
                }
                w.Write("]}");
                if (loadError != null)
                {
                    w.Write(",\"dataError\":");
                    w.Write(Json.Quote(loadError));
                }
                w.Write('}');
            }
        }
    }
}
