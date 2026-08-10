using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace JmaMap
{
    public class WarnItem
    {
        public string RegionCode;
        public string Level;              // tokubetsu / kiken / keihou / chuui
        public List<string> Codes;        // R06 現象コード（2桁）
        public List<string> Kinds;        // 現象名
    }

    // 気象庁の公開フィードを読む。
    // 出典: 気象庁ホームページ（https://www.jma.go.jp/bosai/ ）
    public static class Jma
    {
        // 全国の現況警報・注意報を集約した単一フィード。気象庁の公開地図自身もこれを参照している。
        // 都道府県別の warning/{code}.json は発表元で更新が止まっているため使わない。
        const string WarningMapUrl = "https://www.jma.go.jp/bosai/warning/data/r8/map.json";
        const string AreaConstUrl = "https://www.jma.go.jp/bosai/common/const/area.json";

        static readonly object gate = new object();

        static Jma()
        {
            // csc.exe で直接ビルドすると app.config も TargetFrameworkAttribute も無いため、
            // 実行時は互換モード扱いになり SecurityProtocol が TLS 1.0 のままになる。
            // 気象庁のサーバは TLS 1.2 以上しか受け付けないので明示的に指定する。
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            catch (NotSupportedException) { }
        }

        static string warnCache;
        static DateTime warnCacheAt = DateTime.MinValue;
        static readonly TimeSpan WarnTtl = TimeSpan.FromSeconds(60);

        static Dictionary<string, List<string>> class10Children;
        static Dictionary<string, string> class20Parent;
        static DateTime areaCacheAt = DateTime.MinValue;
        static readonly TimeSpan AreaTtl = TimeSpan.FromHours(6);

        public static string FetchText(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "JmaWarningMap/1.0 (local desktop viewer)";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (Stream st = res.GetResponseStream())
            using (var sr = new StreamReader(st, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        static void EnsureAreaConst()
        {
            lock (gate)
            {
                if (class10Children != null && DateTime.UtcNow - areaCacheAt < AreaTtl) return;
            }

            var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var root = Json.Obj(Json.Parse(FetchText(AreaConstUrl)));
                var c10 = Json.Obj(Json.Get(root, "class10s"));
                if (c10 != null)
                {
                    foreach (var kv in c10)
                    {
                        var node = Json.Obj(kv.Value);
                        var ch = Json.Arr(Json.Get(node, "children"));
                        if (ch == null || ch.Count == 0) continue;
                        var list = new List<string>(ch.Count);
                        for (int i = 0; i < ch.Count; i++)
                        {
                            string s = Json.Str(ch[i]).Trim();
                            if (s.Length > 0) list.Add(s);
                        }
                        if (list.Count > 0) children[kv.Key] = list;
                    }
                }
                var c20 = Json.Obj(Json.Get(root, "class20s"));
                if (c20 != null)
                {
                    foreach (var kv in c20)
                    {
                        var node = Json.Obj(kv.Value);
                        string p = Json.GetStr(node, "parent").Trim();
                        if (p.Length > 0) parents[kv.Key] = p;
                    }
                }
            }
            catch (Exception)
            {
                // area.json が取れなくても、コードをそのまま使えば大半は解決できるので続行する
            }

            lock (gate)
            {
                class10Children = children;
                class20Parent = parents;
                areaCacheAt = DateTime.UtcNow;
            }
        }

        public static Dictionary<string, List<string>> Class10Children()
        {
            EnsureAreaConst();
            lock (gate) { return class10Children; }
        }

        public static Dictionary<string, string> Class20Parent()
        {
            EnsureAreaConst();
            lock (gate) { return class20Parent; }
        }

        static string FetchWarningFeed()
        {
            lock (gate)
            {
                if (warnCache != null && DateTime.UtcNow - warnCacheAt < WarnTtl) return warnCache;
            }
            string text = FetchText(WarningMapUrl);
            lock (gate)
            {
                warnCache = text;
                warnCacheAt = DateTime.UtcNow;
            }
            return text;
        }

        // 地域コードごとに、いま有効な現象コードを集約する（Code.js の getActiveWarnings と同じ規則）。
        // フィード自体が現象スレッドごとに最新1件だけを持つため、時系列の上書き判定は不要。
        public static List<WarnItem> GetActiveWarnings()
        {
            var reports = Json.Arr(Json.Parse(FetchWarningFeed()));
            var byArea = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (reports == null) return new List<WarnItem>();

            Dictionary<string, List<string>> class10s = Class10Children();
            string[] itemKeys = { "class10Items", "class20Items" };

            for (int r = 0; r < reports.Count; r++)
            {
                var w = Json.Obj(Json.Get(Json.Obj(reports[r]), "warning"));
                if (w == null) continue;

                for (int ki = 0; ki < itemKeys.Length; ki++)
                {
                    var items = Json.Arr(Json.Get(w, itemKeys[ki]));
                    if (items == null) continue;

                    for (int a = 0; a < items.Count; a++)
                    {
                        var it = Json.Obj(items[a]);
                        string regionCode = Json.GetStr(it, "areaCode").Trim();
                        if (regionCode.Length == 0) continue;

                        // 支庁内の「地方」単位（class10s）で届くことがあり、こちらのGeoJSONは
                        // その1段下（class15s）までしか持たない。children で実体のあるコードへ展開する。
                        List<string> targets;
                        if (class10s == null || !class10s.TryGetValue(regionCode, out targets) || targets == null || targets.Count == 0)
                        {
                            targets = new List<string>(1);
                            targets.Add(regionCode);
                        }

                        var kinds = Json.Arr(Json.Get(it, "kinds"));
                        if (kinds == null) continue;

                        for (int k = 0; k < kinds.Count; k++)
                        {
                            var kind = Json.Obj(kinds[k]);
                            if (Json.GetStr(kind, "status").Trim() == "解除") continue;
                            string code = NormalizeR06(Json.GetStr(kind, "code"));
                            if (code.Length == 0) continue;

                            for (int t = 0; t < targets.Count; t++)
                            {
                                HashSet<string> set;
                                if (!byArea.TryGetValue(targets[t], out set))
                                {
                                    set = new HashSet<string>(StringComparer.Ordinal);
                                    byArea[targets[t]] = set;
                                }
                                set.Add(code);
                            }
                        }
                    }
                }
            }

            var result = new List<WarnItem>(byArea.Count);
            foreach (var kv in byArea)
            {
                var codes = new List<string>(kv.Value);
                codes.Sort(StringComparer.Ordinal);

                var kindNames = new List<string>();
                for (int i = 0; i < codes.Count; i++)
                {
                    string name = CodeToKind(codes[i]);
                    if (!kindNames.Contains(name)) kindNames.Add(name);
                }

                var item = new WarnItem();
                item.RegionCode = kv.Key;
                item.Codes = codes;
                item.Kinds = kindNames;
                item.Level = HighestLevel(codes);
                result.Add(item);
            }
            return result;
        }

        // R06 現象コードを2桁ゼロ埋めへ（例: 3 -> "03"）
        public static string NormalizeR06(string v)
        {
            if (v == null) return "";
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c >= '0' && c <= '9') sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length == 0) return "";
            if (s.Length >= 2) return s.Substring(s.Length - 2);
            return "0" + s;
        }

        static readonly string[] LevelTokubetsu = { "32", "33", "35", "36", "37", "38", "39" };
        static readonly string[] LevelKiken = { "43", "48", "49" };
        static readonly string[] LevelKeihou = { "02", "03", "04", "05", "06", "07", "08", "09" };

        // 特別 ＞ 危険 ＞ 警報 ＞ 注意
        public static string HighestLevel(List<string> codes)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (codes != null)
            {
                for (int i = 0; i < codes.Count; i++) set.Add(codes[i].Trim());
            }
            for (int i = 0; i < LevelTokubetsu.Length; i++) if (set.Contains(LevelTokubetsu[i])) return "tokubetsu";
            for (int i = 0; i < LevelKiken.Length; i++) if (set.Contains(LevelKiken[i])) return "kiken";
            for (int i = 0; i < LevelKeihou.Length; i++) if (set.Contains(LevelKeihou[i])) return "keihou";
            return "chuui";
        }

        static readonly Dictionary<string, string> KindNames = BuildKindNames();

        static Dictionary<string, string> BuildKindNames()
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            m["00"] = "解除";
            m["02"] = "暴風雪"; m["13"] = "風雪"; m["32"] = "暴風雪特別";
            m["03"] = "大雨"; m["10"] = "大雨"; m["33"] = "大雨特別"; m["43"] = "大雨危険度";
            m["04"] = "洪水"; m["18"] = "洪水";
            m["05"] = "暴風"; m["15"] = "強風"; m["35"] = "暴風特別";
            m["06"] = "大雪"; m["12"] = "大雪"; m["36"] = "大雪特別";
            m["07"] = "波浪"; m["16"] = "波浪"; m["37"] = "波浪特別";
            m["08"] = "高潮"; m["19"] = "高潮"; m["38"] = "高潮特別"; m["48"] = "高潮危険度";
            m["09"] = "土砂災害"; m["29"] = "土砂災害"; m["39"] = "土砂特別"; m["49"] = "土砂危険度";
            m["14"] = "雷";
            m["17"] = "融雪";
            m["20"] = "濃霧";
            m["21"] = "乾燥";
            m["22"] = "なだれ";
            m["23"] = "低温";
            m["24"] = "霜";
            m["25"] = "着氷";
            m["26"] = "着雪";
            m["27"] = "その他";
            return m;
        }

        public static string CodeToKind(string code)
        {
            string key = NormalizeR06(code);
            string name;
            if (KindNames.TryGetValue(key, out name)) return name;
            return "その他";
        }
    }
}
