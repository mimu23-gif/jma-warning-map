using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace JmaMap
{
    /*** 噴火警報 ***/

    public class VolcanoWarn
    {
        public string EventId = "";
        public string VolcanoName = "";
        public string VolcanoCode = "";
        public string KindName = "";      // 火口周辺警報 / 噴火警報（居住地域）など
        public string KindCode = "";      // 01 / 02 / 03 …
        public string LevelName = "";     // レベル２（火口周辺規制）など、対象火山側の表現
        public string ReportedAt = "";
        public List<string> Municipalities = new List<string>();   // 7桁の市町村コード
    }

    /*** 降灰予報 ***/

    public class AshFall
    {
        public string VolcanoName = "";
        public string VolcanoCode = "";
        public string ReportedAt = "";
        public string Headline = "";
        public bool HasCoord;
        public double Lat;
        public double Lon;
        public List<string> Ash = new List<string>();     // 降灰(70)が予想される市町村
        public List<string> Stone = new List<string>();   // 小さな噴石の落下(75)が予想される市町村
    }

    /*** 指定河川洪水予報 ***/

    public class FloodWarn
    {
        public string EventId = "";        // 予報区域コード（河川区間）
        public string RiverName = "";
        public string RiverCode = "";
        public string KindName = "";       // レベル４氾濫危険警報 など
        public string KindCode = "";       // 40 / 30 / 20 …
        public int Level;                  // 2〜5。取れなければ0
        public string ReportedAt = "";
        public string Title = "";
        public string Headline = "";
        public bool Cleared;               // 最新報が解除だった（＝現在は発表なし）
        public List<string> PrefCodes = new List<string>();   // 府県予報区コード（地図の塗り分けに使う）
        public List<string> PrefNames = new List<string>();
        public List<string> Sections = new List<string>();    // 「右岸：〜から〜まで」の区間説明
    }

    /*** 津波警報・注意報 ***/

    public class TsunamiArea
    {
        public string Code = "";        // 津波予報区コード（3桁）
        public string Name = "";
        public string KindName = "";    // 大津波警報 / 津波警報 / 津波注意報 / 津波予報
        public string KindCode = "";
        public string MaxHeight = "";
        public string FirstHeight = "";
    }

    public class TsunamiReport
    {
        public string EventId = "";
        public string Title = "";
        public string ReportedAt = "";
        public string Hypocenter = "";
        public string Magnitude = "";
        public bool Cleared;            // 最新報が「解除」「津波なし」だけ
        public List<TsunamiArea> Areas = new List<TsunamiArea>();
    }

    // 噴火警報・降灰予報・指定河川洪水予報。
    // いずれも「地上の人と建築物に影響する」情報のうち、同梱の境界データと結合できるもの。
    // 出典: 気象庁ホームページ（https://www.jma.go.jp/bosai/ ・https://www.data.jma.go.jp/developer/xml/ ）
    public static class Hazards
    {
        const string VolcanoWarningUrl = "https://www.jma.go.jp/bosai/volcano/data/warning.json";

        static readonly object gate = new object();

        static List<VolcanoWarn> volcanoCache;
        static DateTime volcanoCacheAt = DateTime.MinValue;
        static List<AshFall> ashCache;
        static DateTime ashCacheAt = DateTime.MinValue;
        static List<FloodWarn> floodCache;
        static DateTime floodCacheAt = DateTime.MinValue;
        static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);

        /*** 噴火警報 ***/

        /// <summary>
        /// 発表中の噴火警報を返す。対象市町村は7桁コードで来るため、警報・震度と同じ索引で解決できる。
        /// </summary>
        public static List<VolcanoWarn> GetVolcanoWarnings()
        {
            lock (gate)
            {
                if (volcanoCache != null && DateTime.UtcNow - volcanoCacheAt < Ttl) return volcanoCache;
            }

            var result = new List<VolcanoWarn>();
            try
            {
                var list = Json.Arr(Json.Parse(Jma.FetchText(VolcanoWarningUrl)));
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = Json.Obj(list[i]);
                        if (r == null) continue;

                        var w = new VolcanoWarn();
                        w.EventId = Json.GetStr(r, "eventId");
                        w.ReportedAt = Json.GetStr(r, "reportDatetime");

                        var infos = Json.Arr(Json.Get(r, "volcanoInfos"));
                        if (infos == null) continue;

                        for (int k = 0; k < infos.Count; k++)
                        {
                            var info = Json.Obj(infos[k]);
                            string type = Json.GetStr(info, "type");
                            var items = Json.Arr(Json.Get(info, "items"));
                            if (items == null) continue;

                            for (int t = 0; t < items.Count; t++)
                            {
                                var item = Json.Obj(items[t]);
                                var areas = Json.Arr(Json.Get(item, "areas"));

                                if (type.IndexOf("対象火山", StringComparison.Ordinal) >= 0)
                                {
                                    // 火山名と、レベル表現（「レベル２（火口周辺規制）」等）はこちらに入る
                                    w.LevelName = Json.GetStr(item, "name");
                                    if (areas != null && areas.Count > 0)
                                    {
                                        var a = Json.Obj(areas[0]);
                                        w.VolcanoName = Json.GetStr(a, "name");
                                        w.VolcanoCode = Json.GetStr(a, "code");
                                    }
                                }
                                else if (string.CompareOrdinal(type, "噴火警報・予報（対象市町村等）") == 0)
                                {
                                    w.KindName = Json.GetStr(item, "name");
                                    w.KindCode = Json.GetStr(item, "code");
                                    if (areas == null) continue;
                                    for (int a = 0; a < areas.Count; a++)
                                    {
                                        string code = Json.GetStr(Json.Obj(areas[a]), "code").Trim();
                                        if (code.Length > 0 && !w.Municipalities.Contains(code))
                                            w.Municipalities.Add(code);
                                    }
                                }
                            }
                        }

                        if (w.Municipalities.Count > 0) result.Add(w);
                    }
                }
            }
            catch (Exception)
            {
                // 火山が取れなくても他の表示は続ける
            }

            lock (gate)
            {
                volcanoCache = result;
                volcanoCacheAt = DateTime.UtcNow;
            }
            return result;
        }

        // 噴火警報の重み。居住地域 ＞ 火口周辺 ＞ 周辺海域
        public static int VolcanoRank(string kindCode)
        {
            switch ((kindCode == null) ? "" : kindCode.Trim())
            {
                case "01": return 3;   // 噴火警報（居住地域）＝特別警報相当
                case "02": return 2;   // 火口周辺警報
                case "03": return 1;   // 噴火警報（周辺海域）
                default: return 0;
            }
        }

        /*** 降灰予報 ***/

        /// <summary>
        /// 降灰予報（定時・速報・詳細）から、降灰と小さな噴石が予想される市町村を集める。
        /// 同じ火山の古い報は捨て、火山ごとに最新の1件だけを残す。
        /// </summary>
        public static List<AshFall> GetAshFalls(int maxDocs)
        {
            lock (gate)
            {
                if (ashCache != null && DateTime.UtcNow - ashCacheAt < Ttl) return ashCache;
            }

            var result = new List<AshFall>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                List<FeedEntry> entries = XmlFeed.Entries(XmlFeed.EqVol, null, 200);
                int fetched = 0;
                for (int i = 0; i < entries.Count && fetched < maxDocs; i++)
                {
                    if (entries[i].Title.IndexOf("降灰予報", StringComparison.Ordinal) < 0) continue;
                    XmlDocument doc = XmlFeed.Document(entries[i].Link);
                    fetched++;
                    if (doc == null) continue;

                    AshFall a = ParseAshFall(doc);
                    if (a == null || a.VolcanoCode.Length == 0) continue;
                    if (seen.Contains(a.VolcanoCode)) continue;   // 同じ火山の古い報
                    seen.Add(a.VolcanoCode);
                    if (a.Ash.Count > 0 || a.Stone.Count > 0) result.Add(a);
                }
            }
            catch (Exception) { }

            lock (gate)
            {
                ashCache = result;
                ashCacheAt = DateTime.UtcNow;
            }
            return result;
        }

        static AshFall ParseAshFall(XmlDocument doc)
        {
            var a = new AshFall();
            a.ReportedAt = XmlFeed.InnerText(doc, "ReportDateTime");
            a.Headline = XmlFeed.InnerText(doc, "Headline/Text");

            XmlNodeList infos = XmlFeed.PickAll(doc, "VolcanoInfo");
            if (infos == null) return a;

            for (int i = 0; i < infos.Count; i++)
            {
                XmlNode info = infos[i];
                string type = XmlFeed.NodeAttr(info, "type");

                if (type.IndexOf("対象火山", StringComparison.Ordinal) >= 0)
                {
                    XmlNode area = XmlFeed.Pick(info, "Areas/Area");
                    if (area != null)
                    {
                        a.VolcanoName = XmlFeed.InnerText(area, "Name");
                        a.VolcanoCode = XmlFeed.InnerText(area, "Code");
                        double lat, lon;
                        if (XmlFeed.ParseCoordinate(XmlFeed.InnerText(area, "Coordinate"), out lat, out lon))
                        {
                            a.HasCoord = true;
                            a.Lat = lat;
                            a.Lon = lon;
                        }
                    }
                    continue;
                }

                if (type.IndexOf("対象市町村", StringComparison.Ordinal) < 0) continue;

                XmlNodeList items = XmlFeed.PickAll(info, "Item");
                if (items == null) continue;
                for (int k = 0; k < items.Count; k++)
                {
                    string kindCode = XmlFeed.InnerText(items[k], "Kind/Code");
                    XmlNodeList areas = XmlFeed.PickAll(items[k], "Areas/Area");
                    if (areas == null) continue;
                    for (int m = 0; m < areas.Count; m++)
                    {
                        string code = XmlFeed.InnerText(areas[m], "Code");
                        if (code.Length == 0) continue;
                        if (string.CompareOrdinal(kindCode, "75") == 0)
                        {
                            if (!a.Stone.Contains(code)) a.Stone.Add(code);
                        }
                        else
                        {
                            if (!a.Ash.Contains(code)) a.Ash.Add(code);
                        }
                    }
                }
            }
            return a;
        }

        /*** 指定河川洪水予報 ***/

        /// <summary>
        /// 指定河川洪水予報を河川ごとに最新1件へまとめて返す。解除済みの河川も
        /// Cleared=true を立てて残す（「さっきまで出ていた」ことを画面に出せるようにする）。
        /// 電文には市町村コードが無く、地図に結合できるのは府県予報区コードだけ。
        /// </summary>
        public static List<FloodWarn> GetFloodWarnings(int maxDocs)
        {
            lock (gate)
            {
                if (floodCache != null && DateTime.UtcNow - floodCacheAt < Ttl) return floodCache;
            }

            var result = new List<FloodWarn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                List<FeedEntry> entries = XmlFeed.Entries(XmlFeed.Extra, "指定河川洪水予報", maxDocs);
                for (int i = 0; i < entries.Count; i++)
                {
                    XmlDocument doc = XmlFeed.Document(entries[i].Link);
                    if (doc == null) continue;

                    FloodWarn f = ParseFlood(doc);
                    if (f == null) continue;

                    string key = (f.RiverCode.Length > 0) ? f.RiverCode : f.EventId;
                    if (key.Length == 0 || seen.Contains(key)) continue;   // 同じ河川の古い報
                    seen.Add(key);

                    f.Cleared = IsCleared(f);
                    result.Add(f);
                }
            }
            catch (Exception) { }

            lock (gate)
            {
                floodCache = result;
                floodCacheAt = DateTime.UtcNow;
            }
            return result;
        }

        static bool IsCleared(FloodWarn f)
        {
            if (f.Headline.IndexOf("解除", StringComparison.Ordinal) >= 0) return true;
            if (f.KindName.IndexOf("解除", StringComparison.Ordinal) >= 0) return true;
            if (f.Title.IndexOf("解除", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static FloodWarn ParseFlood(XmlDocument doc)
        {
            var f = new FloodWarn();
            f.EventId = XmlFeed.InnerText(doc, "EventID");
            f.ReportedAt = XmlFeed.InnerText(doc, "ReportDateTime");
            f.Title = XmlFeed.InnerText(doc, "Head/Title");
            f.Headline = XmlFeed.InnerText(doc, "Headline/Text");

            XmlNodeList infos = XmlFeed.PickAll(doc, "Headline/Information");
            if (infos != null)
            {
                for (int i = 0; i < infos.Count; i++)
                {
                    XmlNode info = infos[i];

                    if (f.KindName.Length == 0)
                    {
                        f.KindName = XmlFeed.InnerText(info, "Item/Kind/Name");
                        f.KindCode = XmlFeed.InnerText(info, "Item/Kind/Code");
                    }

                    // Information/@type は「指定河川洪水予報（府県予報区等）」のように
                    // どれも文字列「河川」を含んでしまう。区別は Areas/@codeType で行う。
                    XmlNode areasNode = XmlFeed.Pick(info, "Item/Areas");
                    XmlNodeList areas = XmlFeed.PickAll(info, "Item/Areas/Area");
                    if (areasNode == null || areas == null) continue;
                    string codeType = XmlFeed.NodeAttr(areasNode, "codeType");

                    if (string.CompareOrdinal(codeType, "河川") == 0)
                    {
                        if (areas.Count > 0)
                        {
                            f.RiverName = XmlFeed.InnerText(areas[0], "Name");
                            f.RiverCode = XmlFeed.InnerText(areas[0], "Code");
                        }
                    }
                    else if (codeType.IndexOf("府県予報区", StringComparison.Ordinal) >= 0)
                    {
                        for (int m = 0; m < areas.Count; m++)
                        {
                            string code = XmlFeed.InnerText(areas[m], "Code");
                            string name = XmlFeed.InnerText(areas[m], "Name");
                            if (code.Length > 0 && !f.PrefCodes.Contains(code))
                            {
                                f.PrefCodes.Add(code);
                                f.PrefNames.Add(name);
                            }
                        }
                    }
                    else if (f.EventId.Length == 0)
                    {
                        // 予報区域（河川区間）。河川名が別途取れなければこちらで代用する。
                        if (areas.Count > 0) f.EventId = XmlFeed.InnerText(areas[0], "Code");
                    }
                }
            }

            if (f.RiverName.Length == 0)
            {
                // Head/Title は「善福寺川レベル４氾濫危険警報」の形。レベル以降を落として河川名にする。
                string t = f.Title;
                int at = t.IndexOf("レベル", StringComparison.Ordinal);
                f.RiverName = (at > 0) ? t.Substring(0, at) : t;
            }
            f.Level = LevelFromKind(f.KindName, f.KindCode);

            // 「右岸：〜から〜まで」の区間説明。地図に線は引けないので文章で補う。
            XmlNodeList sections = XmlFeed.PickAll(doc, "ChargeSection");
            if (sections != null)
            {
                for (int i = 0; i < sections.Count; i++)
                {
                    string s = sections[i].InnerText.Trim();
                    if (s.Length > 0 && !f.Sections.Contains(s)) f.Sections.Add(s);
                }
            }
            return f;
        }

        /*** 津波警報・注意報 ***/

        const string TsunamiListUrl = "https://www.jma.go.jp/bosai/tsunami/data/list.json";
        const string TsunamiDataBase = "https://www.jma.go.jp/bosai/tsunami/data/";

        static TsunamiReport tsunamiCache;
        static DateTime tsunamiCacheAt = DateTime.MinValue;

        /// <summary>
        /// 最新の津波警報・注意報を返す。区域は3桁の津波予報区コードで来るため、
        /// 市町村や府県予報区ではなく専用の海岸線データと突き合わせる。
        /// </summary>
        public static TsunamiReport GetTsunami()
        {
            lock (gate)
            {
                if (tsunamiCache != null && DateTime.UtcNow - tsunamiCacheAt < Ttl) return tsunamiCache;
            }

            TsunamiReport report = null;
            try
            {
                var list = Json.Arr(Json.Parse(Jma.FetchText(TsunamiListUrl)));
                if (list != null)
                {
                    for (int i = 0; i < list.Count && report == null; i++)
                    {
                        var e = Json.Obj(list[i]);
                        if (e == null) continue;

                        // 区域別の内容を持つのは津波警報・注意報・予報の本体（VTSE41 など）
                        string file = Json.GetStr(e, "json").Trim();
                        if (file.Length == 0) continue;

                        TsunamiReport r = ParseTsunami(Jma.FetchText(TsunamiDataBase + file));
                        if (r == null || r.Areas.Count == 0) continue;

                        r.EventId = Json.GetStr(e, "eid");
                        r.Title = Json.GetStr(e, "ttl");
                        r.ReportedAt = Json.GetStr(e, "rdt");
                        r.Hypocenter = Json.GetStr(e, "anm");
                        r.Magnitude = Json.GetStr(e, "mag");
                        report = r;
                    }
                }
            }
            catch (Exception) { }

            if (report == null) report = new TsunamiReport();

            lock (gate)
            {
                tsunamiCache = report;
                tsunamiCacheAt = DateTime.UtcNow;
            }
            return report;
        }

        static TsunamiReport ParseTsunami(string text)
        {
            var root = Json.Obj(Json.Parse(text));
            if (root == null) return null;

            var r = new TsunamiReport();
            var body = Json.Obj(Json.Get(root, "Body"));
            var tsunami = Json.Obj(Json.Get(body, "Tsunami"));
            var forecast = Json.Obj(Json.Get(tsunami, "Forecast"));
            if (forecast == null) return null;

            // Item は1件のときオブジェクト、複数のとき配列で来る
            object itemNode = Json.Get(forecast, "Item");
            var items = Json.Arr(itemNode);
            if (items == null)
            {
                var single = Json.Obj(itemNode);
                if (single == null) return r;
                items = new List<object>();
                items.Add(single);
            }

            bool anyActive = false;
            for (int i = 0; i < items.Count; i++)
            {
                var item = Json.Obj(items[i]);
                var area = Json.Obj(Json.Get(item, "Area"));
                if (area == null) continue;

                var a = new TsunamiArea();
                a.Code = Json.GetStr(area, "Code").Trim();
                a.Name = Json.GetStr(area, "Name");
                if (a.Code.Length == 0) continue;

                var category = Json.Obj(Json.Get(item, "Category"));
                var kind = Json.Obj(Json.Get(category, "Kind"));
                a.KindName = Json.GetStr(kind, "Name");
                a.KindCode = Json.GetStr(kind, "Code");

                var first = Json.Obj(Json.Get(item, "FirstHeight"));
                a.FirstHeight = Json.GetStr(first, "Condition");
                var max = Json.Obj(Json.Get(item, "MaxHeight"));
                a.MaxHeight = Json.GetStr(max, "TsunamiHeight");

                if (TsunamiRank(a.KindName) > 0) anyActive = true;
                r.Areas.Add(a);
            }
            r.Cleared = !anyActive;
            return r;
        }

        /// <summary>大津波警報 ＞ 津波警報 ＞ 津波注意報。解除・津波なしは0。</summary>
        public static int TsunamiRank(string kindName)
        {
            string n = (kindName == null) ? "" : kindName;
            if (n.IndexOf("解除", StringComparison.Ordinal) >= 0) return 0;
            if (n.IndexOf("大津波警報", StringComparison.Ordinal) >= 0) return 3;
            if (n.IndexOf("津波警報", StringComparison.Ordinal) >= 0) return 2;
            if (n.IndexOf("津波注意報", StringComparison.Ordinal) >= 0) return 1;
            return 0;   // 津波予報（若干の海面変動）・津波なし
        }

        // 「レベル４氾濫危険警報」→ 4。名前から取れなければコード（40→4）で補う。
        public static int LevelFromKind(string name, string code)
        {
            string n = (name == null) ? "" : name;
            int at = n.IndexOf("レベル", StringComparison.Ordinal);
            if (at >= 0 && at + 3 < n.Length)
            {
                char c = n[at + 3];
                int v = ZenkakuDigit(c);
                if (v > 0) return v;
            }
            int num;
            if (int.TryParse((code == null) ? "" : code.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out num) && num >= 10) return num / 10;
            return 0;
        }

        static int ZenkakuDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= '０' && c <= '９') return c - '０';
            return 0;
        }
    }
}
