using System;
using System.Collections.Generic;
using System.Globalization;

namespace JmaMap
{
    /*** 地震（震源・震度） ***/

    public class QuakeCityInt
    {
        public string Code;     // 市区町村コード（7桁）
        public string Shindo;   // "1".."4" / "5-" / "5+" / "6-" / "6+" / "7"
    }

    public class QuakeItem
    {
        public string Eid = "";          // 地震識別子（同じ地震の続報をまとめる鍵）
        public string Title = "";
        public string ReportedAt = "";   // 発表時刻
        public string OriginTime = "";   // 発震時刻
        public string Hypocenter = "";
        public string Magnitude = "";
        public string MaxInt = "";
        public bool HasCoord;
        public double Lat;
        public double Lon;
        public double DepthKm;
        public List<QuakeCityInt> Cities = new List<QuakeCityInt>();
    }

    /*** 台風 ***/

    public class TyphoonPoint
    {
        public string Part = "";         // 実況 / 予報　１２時間後 …
        public int AdvancedHours;
        public string ValidTime = "";
        public double Lat;
        public double Lon;
        public double CircleRadiusM;     // 予報円の半径（実況は0）
        public string Category = "";
        public string Pressure = "";
        public string WindSustained = "";
        public string WindGust = "";
        public string Course = "";
        public string Speed = "";
        public string Location = "";
    }

    public class TyphoonItem
    {
        public string Id = "";           // TC2618
        public string Number = "";       // 2616（"a" 等の非数値もあり得る）
        public string NameJp = "";
        public string NameEn = "";
        public string Category = "";
        public string Issue = "";
        public List<double[]> TrackPre = new List<double[]>();       // [lat, lon]
        public List<double[]> TrackTyphoon = new List<double[]>();
        public bool HasGale;
        public double GaleLat;
        public double GaleLon;
        public double GaleRadiusM;       // 暴風警戒域
        public List<TyphoonPoint> Points = new List<TyphoonPoint>();
    }

    // 警報フィード以外の「いま発生している災害」を読む。
    // 出典: 気象庁ホームページ（https://www.jma.go.jp/bosai/ ）
    public static class Disaster
    {
        const string QuakeListUrl = "https://www.jma.go.jp/bosai/quake/data/list.json";
        const string TyphoonTargetUrl = "https://www.jma.go.jp/bosai/typhoon/data/targetTc.json";
        const string TyphoonDataBase = "https://www.jma.go.jp/bosai/typhoon/data/";

        static readonly object gate = new object();

        static string quakeCache;
        static DateTime quakeCacheAt = DateTime.MinValue;
        static readonly TimeSpan QuakeTtl = TimeSpan.FromSeconds(60);

        static List<TyphoonItem> typhoonCache;
        static DateTime typhoonCacheAt = DateTime.MinValue;
        static readonly TimeSpan TyphoonTtl = TimeSpan.FromMinutes(10);

        /*** 地震 ***/

        static string FetchQuakeFeed()
        {
            lock (gate)
            {
                if (quakeCache != null && DateTime.UtcNow - quakeCacheAt < QuakeTtl) return quakeCache;
            }
            string text = Jma.FetchText(QuakeListUrl);
            lock (gate)
            {
                quakeCache = text;
                quakeCacheAt = DateTime.UtcNow;
            }
            return text;
        }

        /// <summary>
        /// 直近 hours 時間の地震を新しい順に返す。市区町村別の震度を持つ報だけを対象にし、
        /// 同じ地震の続報は最新の1件へまとめる（list.json は新しい順に並んでいる）。
        /// </summary>
        public static List<QuakeItem> GetRecentQuakes(double hours, int max)
        {
            var result = new List<QuakeItem>();
            List<object> list;
            try
            {
                list = Json.Arr(Json.Parse(FetchQuakeFeed()));
            }
            catch (Exception)
            {
                return result;
            }
            if (list == null) return result;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < list.Count && result.Count < max; i++)
            {
                var e = Json.Obj(list[i]);
                if (e == null) continue;

                // 市区町村ごとの震度を持たない報（遠地地震・津波関連など）は地図に塗れない
                var ints = Json.Arr(Json.Get(e, "int"));
                if (ints == null || ints.Count == 0) continue;

                string eid = Json.GetStr(e, "eid").Trim();
                if (eid.Length == 0) continue;
                if (seen.Contains(eid)) continue;       // 同じ地震の古い続報

                string rdt = Json.GetStr(e, "rdt").Trim();
                if (hours > 0 && rdt.Length > 0)
                {
                    DateTimeOffset t;
                    if (DateTimeOffset.TryParse(rdt, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out t))
                    {
                        if ((now - t).TotalHours > hours) break;   // 以降はさらに古い
                    }
                }
                seen.Add(eid);

                var q = new QuakeItem();
                q.Eid = eid;
                q.Title = Json.GetStr(e, "ttl");
                q.ReportedAt = rdt;
                q.OriginTime = Json.GetStr(e, "at");
                q.Hypocenter = Json.GetStr(e, "anm");
                q.Magnitude = Json.GetStr(e, "mag");
                q.MaxInt = Json.GetStr(e, "maxi");
                ParseIso6709(Json.GetStr(e, "cod"), q);

                for (int p = 0; p < ints.Count; p++)
                {
                    var pref = Json.Obj(ints[p]);
                    var cities = Json.Arr(Json.Get(pref, "city"));
                    if (cities == null) continue;
                    for (int c = 0; c < cities.Count; c++)
                    {
                        var city = Json.Obj(cities[c]);
                        string code = Json.GetStr(city, "code").Trim();
                        string shindo = Json.GetStr(city, "maxi").Trim();
                        if (code.Length == 0 || shindo.Length == 0) continue;
                        var ci = new QuakeCityInt();
                        ci.Code = code;
                        ci.Shindo = shindo;
                        q.Cities.Add(ci);
                    }
                }
                if (q.Cities.Count == 0) continue;
                result.Add(q);
            }
            return result;
        }

        public static QuakeItem FindQuake(List<QuakeItem> items, string eid)
        {
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Eid, eid, StringComparison.Ordinal)) return items[i];
            }
            return null;
        }

        // "+32.7+130.7-10000/" → 緯度 +32.7 / 経度 +130.7 / 深さ -10000m
        // 符号が区切りを兼ねる ISO6709 の短縮形。深さは省略されることがある。
        public static void ParseIso6709(string cod, QuakeItem q)
        {
            if (q == null || string.IsNullOrEmpty(cod)) return;
            var nums = new List<double>();
            int i = 0;
            while (i < cod.Length)
            {
                char c = cod[i];
                if (c != '+' && c != '-') { i++; continue; }
                int start = i;
                i++;
                while (i < cod.Length)
                {
                    char d = cod[i];
                    if ((d >= '0' && d <= '9') || d == '.') i++;
                    else break;
                }
                double v;
                if (double.TryParse(cod.Substring(start, i - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out v))
                {
                    nums.Add(v);
                }
            }
            if (nums.Count < 2) return;
            q.Lat = nums[0];
            q.Lon = nums[1];
            q.HasCoord = true;
            if (nums.Count >= 3) q.DepthKm = Math.Abs(nums[2]) / 1000.0;
        }

        /*** 台風 ***/

        /// <summary>
        /// 発生中の台風・熱帯低気圧を、実績経路・予報円・暴風警戒域つきで返す。
        /// targetTc.json が対象の一覧、TC別の forecast.json が座標、specifications.json が諸元。
        /// </summary>
        public static List<TyphoonItem> GetTyphoons()
        {
            lock (gate)
            {
                if (typhoonCache != null && DateTime.UtcNow - typhoonCacheAt < TyphoonTtl)
                    return typhoonCache;
            }

            var items = new List<TyphoonItem>();
            try
            {
                var targets = Json.Arr(Json.Parse(Jma.FetchText(TyphoonTargetUrl)));
                if (targets != null)
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        var t = Json.Obj(targets[i]);
                        string id = Json.GetStr(t, "tropicalCyclone").Trim();
                        if (id.Length == 0) continue;

                        var item = new TyphoonItem();
                        item.Id = id;
                        item.Number = Json.GetStr(t, "typhoonNumber");
                        item.Category = Json.GetStr(t, "category");
                        item.Issue = Json.GetStr(t, "issue");

                        try { LoadForecast(item); }
                        catch (Exception) { }
                        try { LoadSpecifications(item); }
                        catch (Exception) { }

                        // 座標が1つも取れなかったものは地図に出せない
                        if (item.Points.Count > 0 || item.TrackTyphoon.Count > 0) items.Add(item);
                    }
                }
            }
            catch (Exception)
            {
                // 台風が取れなくても地震・警報の表示は続ける
            }

            lock (gate)
            {
                typhoonCache = items;
                typhoonCacheAt = DateTime.UtcNow;
            }
            return items;
        }

        static void LoadForecast(TyphoonItem item)
        {
            var parts = Json.Arr(Json.Parse(Jma.FetchText(TyphoonDataBase + item.Id + "/forecast.json")));
            if (parts == null) return;

            for (int i = 0; i < parts.Count; i++)
            {
                var p = Json.Obj(parts[i]);
                if (p == null) continue;
                string partName = PartName(Json.Get(p, "part"));

                if (partName == "title")
                {
                    var nm = Json.Obj(Json.Get(p, "name"));
                    if (nm != null)
                    {
                        item.NameJp = Json.GetStr(nm, "jp");
                        item.NameEn = Json.GetStr(nm, "en");
                    }
                    if (item.Number.Length == 0) item.Number = Json.GetStr(p, "typhoonNumber");
                    continue;
                }

                var track = Json.Obj(Json.Get(p, "track"));
                if (track != null)
                {
                    AppendTrack(item.TrackPre, Json.Arr(Json.Get(track, "preTyphoon")));
                    AppendTrack(item.TrackTyphoon, Json.Arr(Json.Get(track, "typhoon")));
                }

                var gale = Json.Obj(Json.Get(p, "galeWarningArea"));
                if (gale != null)
                {
                    double[] gc = LatLon(Json.Arr(Json.Get(gale, "center")));
                    double gr = Num(Json.Get(gale, "radius"));
                    if (gc != null && gr > 0)
                    {
                        item.HasGale = true;
                        item.GaleLat = gc[0];
                        item.GaleLon = gc[1];
                        item.GaleRadiusM = gr;
                    }
                }

                double[] center = LatLon(Json.Arr(Json.Get(p, "center")));
                if (center == null) continue;

                var pt = new TyphoonPoint();
                pt.Part = partName;
                pt.AdvancedHours = (int)Num(Json.Get(p, "advancedHours"));
                pt.Lat = center[0];
                pt.Lon = center[1];

                var vt = Json.Obj(Json.Get(p, "validtime"));
                if (vt != null) pt.ValidTime = Json.GetStr(vt, "JST");

                var circle = Json.Obj(Json.Get(p, "probabilityCircle"));
                if (circle != null) pt.CircleRadiusM = Num(Json.Get(circle, "radius"));

                item.Points.Add(pt);
            }
        }

        // 諸元（気圧・最大風速・進行方向など）を、時刻の一致する予報点へ足す
        static void LoadSpecifications(TyphoonItem item)
        {
            var parts = Json.Arr(Json.Parse(Jma.FetchText(TyphoonDataBase + item.Id + "/specifications.json")));
            if (parts == null) return;

            for (int i = 0; i < parts.Count; i++)
            {
                var p = Json.Obj(parts[i]);
                if (p == null) continue;
                string partName = PartName(Json.Get(p, "part"));

                if (partName == "title")
                {
                    var nm = Json.Obj(Json.Get(p, "name"));
                    if (nm != null && item.NameJp.Length == 0)
                    {
                        item.NameJp = Json.GetStr(nm, "jp");
                        item.NameEn = Json.GetStr(nm, "en");
                    }
                    var cat = Json.Obj(Json.Get(p, "category"));
                    if (cat != null) item.Category = Json.GetStr(cat, "jp");
                    continue;
                }

                int hours = (int)Num(Json.Get(p, "advancedHours"));
                TyphoonPoint pt = null;
                for (int k = 0; k < item.Points.Count; k++)
                {
                    if (item.Points[k].AdvancedHours == hours) { pt = item.Points[k]; break; }
                }
                if (pt == null) continue;

                var cat2 = Json.Obj(Json.Get(p, "category"));
                if (cat2 != null) pt.Category = Json.GetStr(cat2, "jp");
                pt.Pressure = Json.GetStr(p, "pressure");
                pt.Location = Json.GetStr(p, "location");
                pt.Course = Json.GetStr(p, "course");

                var sp = Json.Obj(Json.Get(p, "speed"));
                if (sp != null) pt.Speed = Json.GetStr(sp, "km/h");

                var mw = Json.Obj(Json.Get(p, "maximumWind"));
                if (mw != null)
                {
                    var sus = Json.Obj(Json.Get(mw, "sustained"));
                    if (sus != null) pt.WindSustained = Json.GetStr(sus, "m/s");
                    var gust = Json.Obj(Json.Get(mw, "gust"));
                    if (gust != null) pt.WindGust = Json.GetStr(gust, "m/s");
                }
            }
        }

        // part は "title"（文字列）と {"jp":"実況","en":"Analysis"}（オブジェクト）の両方で来る
        static string PartName(object part)
        {
            if (part == null) return "";
            var o = Json.Obj(part);
            if (o != null) return Json.GetStr(o, "jp");
            return Json.Str(part);
        }

        static void AppendTrack(List<double[]> dest, List<object> src)
        {
            if (dest == null || src == null) return;
            for (int i = 0; i < src.Count; i++)
            {
                double[] ll = LatLon(Json.Arr(src[i]));
                if (ll != null) dest.Add(ll);
            }
        }

        static double[] LatLon(List<object> pair)
        {
            if (pair == null || pair.Count < 2) return null;
            return new double[] { Num(pair[0]), Num(pair[1]) };
        }

        static double Num(object o)
        {
            if (o == null) return 0;
            if (o is double) return (double)o;
            double d;
            if (double.TryParse(Json.Str(o), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }
    }
}
