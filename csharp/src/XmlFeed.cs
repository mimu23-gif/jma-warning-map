using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace JmaMap
{
    public class FeedEntry
    {
        public string Title = "";
        public string Updated = "";
        public string Content = "";
        public string Link = "";
    }

    // 気象庁「防災情報XML」のAtomフィードを読む。
    // bosai配下のJSONが用意されていない情報（指定河川洪水予報・降灰予報など）はこちらから取る。
    // 出典: 気象庁ホームページ（https://www.data.jma.go.jp/developer/xml/ ）
    public static class XmlFeed
    {
        public const string Extra = "https://www.data.jma.go.jp/developer/xml/feed/extra.xml";
        public const string EqVol = "https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml";

        static readonly object gate = new object();

        static readonly Dictionary<string, string> feedCache = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, DateTime> feedCacheAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        static readonly TimeSpan FeedTtl = TimeSpan.FromSeconds(60);

        // 個別電文のURLは発表時刻を含むので中身が変わらない。取得したら captured しておく。
        static readonly Dictionary<string, XmlDocument> docCache = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        static readonly List<string> docOrder = new List<string>();
        const int DocCacheMax = 300;

        /// <summary>指定フィードのエントリを新しい順に返す（title で絞り込める）。</summary>
        public static List<FeedEntry> Entries(string feedUrl, string titleFilter, int max)
        {
            var result = new List<FeedEntry>();
            string text;
            lock (gate)
            {
                DateTime at;
                if (feedCache.TryGetValue(feedUrl, out text)
                    && feedCacheAt.TryGetValue(feedUrl, out at)
                    && DateTime.UtcNow - at < FeedTtl)
                {
                    // キャッシュ有効
                }
                else text = null;
            }

            if (text == null)
            {
                try { text = Jma.FetchText(feedUrl); }
                catch (Exception) { return result; }
                lock (gate)
                {
                    feedCache[feedUrl] = text;
                    feedCacheAt[feedUrl] = DateTime.UtcNow;
                }
            }

            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;     // 外部エンティティを読みに行かせない
            try { doc.LoadXml(text); }
            catch (Exception) { return result; }

            XmlNodeList entries = doc.SelectNodes("//*[local-name()='entry']");
            if (entries == null) return result;

            for (int i = 0; i < entries.Count && result.Count < max; i++)
            {
                XmlNode e = entries[i];
                string title = InnerText(e, "title");
                if (titleFilter != null && titleFilter.Length > 0
                    && string.CompareOrdinal(title, titleFilter) != 0) continue;

                var fe = new FeedEntry();
                fe.Title = title;
                fe.Updated = InnerText(e, "updated");
                fe.Content = InnerText(e, "content");
                fe.Link = Attr(e, "link", "href");
                if (fe.Link.Length == 0) fe.Link = InnerText(e, "id");
                result.Add(fe);
            }
            return result;
        }

        /// <summary>個別電文を取得する。同じURLは2度取りに行かない。</summary>
        public static XmlDocument Document(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (gate)
            {
                XmlDocument hit;
                if (docCache.TryGetValue(url, out hit)) return hit;
            }

            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            try { doc.LoadXml(Jma.FetchText(url)); }
            catch (Exception) { return null; }

            lock (gate)
            {
                if (!docCache.ContainsKey(url))
                {
                    docCache[url] = doc;
                    docOrder.Add(url);
                    while (docOrder.Count > DocCacheMax)
                    {
                        docCache.Remove(docOrder[0]);
                        docOrder.RemoveAt(0);
                    }
                }
            }
            return doc;
        }

        /*** XPathヘルパ（気象庁XMLは名前空間付きなので local-name() で引く）***/

        public static XmlNode Pick(XmlNode scope, string localPath)
        {
            if (scope == null) return null;
            return scope.SelectSingleNode(ToLocalXPath(localPath));
        }

        public static XmlNodeList PickAll(XmlNode scope, string localPath)
        {
            if (scope == null) return null;
            return scope.SelectNodes(ToLocalXPath(localPath));
        }

        // "Head/Headline/Text" → ".//*[local-name()='Head']/*[local-name()='Headline']/..."
        static string ToLocalXPath(string localPath)
        {
            string[] parts = localPath.Split('/');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                sb.Append(i == 0 ? ".//" : "/");
                sb.Append("*[local-name()='").Append(parts[i]).Append("']");
            }
            return sb.ToString();
        }

        public static string InnerText(XmlNode scope, string localName)
        {
            XmlNode n = Pick(scope, localName);
            return (n != null) ? n.InnerText.Trim() : "";
        }

        public static string Attr(XmlNode scope, string localName, string attrName)
        {
            XmlNode n = Pick(scope, localName);
            if (n == null || n.Attributes == null) return "";
            XmlAttribute a = n.Attributes[attrName];
            return (a != null) ? a.Value : "";
        }

        public static string NodeAttr(XmlNode node, string attrName)
        {
            if (node == null || node.Attributes == null) return "";
            XmlAttribute a = node.Attributes[attrName];
            return (a != null) ? a.Value : "";
        }

        /// <summary>
        /// 気象庁が使う ISO6709 の2形式を解く。
        /// 度分表記（+3026.60+13013.03+657/ ＝ 北緯30度26.60分）と、
        /// 十進度表記（+32.7+130.7-10000/）の両方が現れる。
        /// </summary>
        public static bool ParseCoordinate(string cod, out double lat, out double lon)
        {
            lat = 0; lon = 0;
            if (string.IsNullOrEmpty(cod)) return false;

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
                        CultureInfo.InvariantCulture, out v)) nums.Add(v);
            }
            if (nums.Count < 2) return false;

            lat = ToDegrees(nums[0], 90);
            lon = ToDegrees(nums[1], 180);
            return true;
        }

        // 絶対値が取り得る範囲を超えていたら度分表記とみなす（緯度なら90、経度なら180）
        static double ToDegrees(double v, double limit)
        {
            if (Math.Abs(v) <= limit) return v;
            double sign = (v < 0) ? -1 : 1;
            double a = Math.Abs(v);
            double deg = Math.Floor(a / 100.0);
            double min = a - deg * 100.0;
            return sign * (deg + min / 60.0);
        }
    }
}
