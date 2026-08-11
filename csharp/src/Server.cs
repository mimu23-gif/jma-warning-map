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

        void ServeWarnings(HttpListenerContext ctx)
        {
            HashSet<string> levels = null;
            HashSet<string> phenomena = null;
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
                    levels = ToSet(Json.Arr(Json.Get(args, "levels")));
                    phenomena = ToSet(Json.Arr(Json.Get(args, "phenomena")));

                    object z = Json.Get(args, "zoom");
                    if (z != null)
                        double.TryParse(Json.Str(z), NumberStyles.Float, CultureInfo.InvariantCulture, out zoom);
                }
            }

            // ズーム未指定なら最も粗い段（全国表示相当）を使う
            double tolerance = (zoom >= 0) ? cfg.ToleranceForZoom(zoom) : cfg.ToleranceForZoom(0);

            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.SendChunked = true;
            using (var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
            {
                WriteWarnings(w, levels, phenomena, tolerance);
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
            WriteWarnings(w, levels, phenomena, 0);
        }

        /// <summary>
        /// 現況警報をFeatureCollectionとして書き出す。
        /// tolerance &gt; 0 なら、その許容誤差（度）でジオメトリを間引いてから送る。
        /// </summary>
        public void WriteWarnings(TextWriter w, HashSet<string> levels, HashSet<string> phenomena, double tolerance)
        {
            GeoIndex idx = GetIndex();
            List<WarnItem> items = Jma.GetActiveWarnings();
            Dictionary<string, string> class20Parent = Jma.Class20Parent();

            var byFile = new Dictionary<string, List<Pending>>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string[]>();

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

                // コード → フィーチャ の索引を作る（7桁一致を優先し、外れたら6桁で拾う）
                var map7 = new Dictionary<string, FeatureRef>(StringComparer.Ordinal);
                var map6 = new Dictionary<string, FeatureRef>(StringComparer.Ordinal);
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

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    Pending p = kv.Value[i];
                    FeatureRef ft;
                    if (!map7.TryGetValue(p.Key, out ft))
                    {
                        if (!map6.TryGetValue(Head6(p.Key), out ft)) ft = null;
                    }
                    if (ft == null)
                    {
                        unresolved.Add(new string[] { p.Item.RegionCode, "FEATURE_NOT_FOUND" });
                        continue;
                    }

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
            w.Write("],\"updatedAt\":");
            w.Write(Json.Quote(updatedAt));
            w.Write('}');
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
    }
}
