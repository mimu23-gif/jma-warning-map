using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JmaMap.Tools
{
    // 表示用にポリゴンの頂点を間引く（Douglas-Peucker）。
    //
    // 保存する境界データは気象庁の原データそのまま（高精度）にしておき、
    // 地図に送るぶんだけ軽くするための処理。許容誤差は「度」で指定する
    // （緯度1度 ≒ 111km なので 1e-4 度 ≒ 11m）。
    public static class Simplify
    {
        /// <summary>GeoJSONのgeometryテキストを簡略化して返す。頂点数の増減を outBefore/outAfter で返す。</summary>
        public static string Geometry(string geometryJson, double tolerance, ref long before, ref long after)
        {
            // 線のデータ（津波予報区など）はリングではないので、閉じ直さずに間引く
            List<double[]> paths = ParseLines(geometryJson);
            if (paths != null)
            {
                var keptPaths = new List<double[]>(paths.Count);
                for (int i = 0; i < paths.Count; i++)
                {
                    before += paths[i].Length / 2;
                    double[] simplified = Line(paths[i], tolerance);
                    if (simplified == null) continue;
                    after += simplified.Length / 2;
                    keptPaths.Add(simplified);
                }
                var lsb = new StringBuilder(1024);
                CoordWriter.AppendLineGeometry(lsb, keptPaths);
                return lsb.ToString();
            }

            List<List<double[]>> polygons = ParsePolygons(geometryJson);
            if (polygons == null) return geometryJson;   // null geometry などはそのまま

            var outPolys = new List<List<double[]>>(polygons.Count);
            for (int p = 0; p < polygons.Count; p++)
            {
                var rings = polygons[p];
                var keptRings = new List<double[]>(rings.Count);
                for (int r = 0; r < rings.Count; r++)
                {
                    before += rings[r].Length / 2;
                    double[] simplified = Ring(rings[r], tolerance);
                    if (simplified == null) continue;     // 潰れたリング（面積が小さすぎる穴など）は捨てる
                    after += simplified.Length / 2;
                    keptRings.Add(simplified);
                }
                // 外周が消えたポリゴンは丸ごと捨てる
                if (keptRings.Count > 0) outPolys.Add(keptRings);
            }

            var sb = new StringBuilder(1024);
            CoordWriter.AppendGeometry(sb, outPolys);
            return sb.ToString();
        }

        /// <summary>
        /// 開いた折れ線を簡略化する。リングと違って閉じ直さず、2点あれば線として成立するので
        /// 潰して消すこともしない（海岸線が虫食いになるのを避ける）。
        /// </summary>
        public static double[] Line(double[] path, double tolerance)
        {
            int n = path.Length / 2;
            if (n <= 2) return path;

            var keep = new bool[n];
            keep[0] = true;
            keep[n - 1] = true;
            DouglasPeucker(path, 0, n - 1, tolerance, keep);

            int kept = 0;
            for (int i = 0; i < n; i++) if (keep[i]) kept++;

            var outPath = new double[kept * 2];
            int w = 0;
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                outPath[w * 2] = path[i * 2];
                outPath[w * 2 + 1] = path[i * 2 + 1];
                w++;
            }
            return outPath;
        }

        /// <summary>
        /// 閉じたリングを簡略化する。最後の点は最初の点と同じなので、開いた折れ線として
        /// 処理してから閉じ直す。3点未満になったリングは null（＝消す）。
        /// </summary>
        public static double[] Ring(double[] ring, double tolerance)
        {
            int n = ring.Length / 2;
            if (n < 4) return ring;    // これ以上減らせない

            bool closed = (ring[0] == ring[(n - 1) * 2] && ring[1] == ring[(n - 1) * 2 + 1]);
            int last = closed ? n - 1 : n;   // 開いた折れ線としての点数

            var keep = new bool[last];
            keep[0] = true;
            keep[last - 1] = true;
            DouglasPeucker(ring, 0, last - 1, tolerance, keep);

            int kept = 0;
            for (int i = 0; i < last; i++) if (keep[i]) kept++;

            // 閉じたリングとして成立するには最低3頂点（+閉じ点）必要
            if (closed && kept < 3) return null;
            if (!closed && kept < 2) return null;

            int outLen = closed ? kept + 1 : kept;
            var result = new double[outLen * 2];
            int k = 0;
            for (int i = 0; i < last; i++)
            {
                if (!keep[i]) continue;
                result[k * 2] = ring[i * 2];
                result[k * 2 + 1] = ring[i * 2 + 1];
                k++;
            }
            if (closed)
            {
                result[k * 2] = result[0];
                result[k * 2 + 1] = result[1];
            }
            return result;
        }

        // 再帰ではなくスタックで回す（長いリングでのスタック溢れを避けるため）
        static void DouglasPeucker(double[] pts, int first, int lastIndex, double tolerance, bool[] keep)
        {
            var stack = new Stack<int[]>();
            stack.Push(new int[] { first, lastIndex });

            while (stack.Count > 0)
            {
                int[] range = stack.Pop();
                int a = range[0], b = range[1];
                if (b <= a + 1) continue;

                double ax = pts[a * 2], ay = pts[a * 2 + 1];
                double bx = pts[b * 2], by = pts[b * 2 + 1];
                double dx = bx - ax, dy = by - ay;
                double lenSq = dx * dx + dy * dy;

                double maxDist = -1;
                int maxIdx = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double px = pts[i * 2], py = pts[i * 2 + 1];
                    double dist;
                    if (lenSq <= 0)
                    {
                        double ex = px - ax, ey = py - ay;
                        dist = Math.Sqrt(ex * ex + ey * ey);
                    }
                    else
                    {
                        // 線分ABへの垂線距離（線分外なら端点までの距離）
                        double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
                        if (t < 0) t = 0; else if (t > 1) t = 1;
                        double cx = ax + t * dx, cy = ay + t * dy;
                        double ex = px - cx, ey = py - cy;
                        dist = Math.Sqrt(ex * ex + ey * ey);
                    }
                    if (dist > maxDist) { maxDist = dist; maxIdx = i; }
                }

                if (maxDist > tolerance && maxIdx > 0)
                {
                    keep[maxIdx] = true;
                    stack.Push(new int[] { a, maxIdx });
                    stack.Push(new int[] { maxIdx, b });
                }
            }
        }

        /*** geometry テキストの読み取り ***/

        /// <summary>
        /// Polygon / MultiPolygon の coordinates を double[] のリングへ読み込む。
        /// Json.ParseValue は全要素をobjectへボクシングして数百万点では重すぎるため、
        /// 数値を直接doubleへ読む専用の走査を使う。
        /// </summary>
        public static List<List<double[]>> ParsePolygons(string s)
        {
            int i = 0;
            Json.SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '{') return null;

            string type = null;
            int coordStart = -1;
            i++;
            while (true)
            {
                Json.SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') break;

                string key = Json.ParseString(s, ref i);
                Json.SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                Json.SkipWs(s, ref i);

                if (key == "type") type = Json.ParseString(s, ref i);
                else if (key == "coordinates") { coordStart = i; Json.SkipValue(s, ref i); }
                else Json.SkipValue(s, ref i);
            }

            if (coordStart < 0 || type == null) return null;

            int p = coordStart;
            if (type == "Polygon")
            {
                var one = ReadRings(s, ref p);
                var list = new List<List<double[]>>(1);
                if (one != null && one.Count > 0) list.Add(one);
                return list;
            }
            if (type == "MultiPolygon")
            {
                var polys = new List<List<double[]>>();
                SkipTo(s, ref p, '[');
                p++;
                while (true)
                {
                    SkipWsLocal(s, ref p);
                    if (p >= s.Length) break;
                    if (s[p] == ']') { p++; break; }
                    if (s[p] == ',') { p++; continue; }
                    var rings = ReadRings(s, ref p);
                    if (rings != null && rings.Count > 0) polys.Add(rings);
                }
                return polys;
            }
            return null;   // Point/LineString 等はここでは扱わない（ParseLines が受け持つ）
        }

        /// <summary>
        /// LineString / MultiLineString の coordinates をパスの配列へ読み込む。
        /// 線でなければ null を返し、呼び出し側はポリゴンとして処理する。
        /// </summary>
        public static List<double[]> ParseLines(string s)
        {
            int i = 0;
            Json.SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '{') return null;

            string type = null;
            int coordStart = -1;
            i++;
            while (true)
            {
                Json.SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') break;

                string key = Json.ParseString(s, ref i);
                Json.SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                Json.SkipWs(s, ref i);

                if (key == "type") type = Json.ParseString(s, ref i);
                else if (key == "coordinates") { coordStart = i; Json.SkipValue(s, ref i); }
                else Json.SkipValue(s, ref i);
            }

            if (coordStart < 0 || type == null) return null;

            int p = coordStart;
            if (type == "LineString")
            {
                // 座標の並びはリング1本と同じ形
                var one = ReadRing(s, ref p);
                var list = new List<double[]>(1);
                if (one != null) list.Add(one);
                return list;
            }
            if (type == "MultiLineString")
            {
                // Polygon の rings と同じ形
                return ReadRings(s, ref p);
            }
            return null;
        }

        static List<double[]> ReadRings(string s, ref int p)
        {
            SkipTo(s, ref p, '[');
            p++;
            var rings = new List<double[]>();
            while (true)
            {
                SkipWsLocal(s, ref p);
                if (p >= s.Length) break;
                if (s[p] == ']') { p++; break; }
                if (s[p] == ',') { p++; continue; }
                double[] ring = ReadRing(s, ref p);
                if (ring != null) rings.Add(ring);
            }
            return rings;
        }

        static double[] ReadRing(string s, ref int p)
        {
            SkipTo(s, ref p, '[');
            p++;
            var buf = new List<double>(512);
            while (true)
            {
                SkipWsLocal(s, ref p);
                if (p >= s.Length) break;
                if (s[p] == ']') { p++; break; }
                if (s[p] == ',') { p++; continue; }
                if (s[p] != '[') { p++; continue; }

                p++;                       // 座標の '['
                buf.Add(ReadNumber(s, ref p));
                SkipWsLocal(s, ref p);
                if (p < s.Length && s[p] == ',') p++;
                buf.Add(ReadNumber(s, ref p));
                SkipWsLocal(s, ref p);
                while (p < s.Length && s[p] != ']') p++;   // 3要素目（標高）があれば読み飛ばす
                if (p < s.Length) p++;
            }
            if (buf.Count < 4) return null;
            return buf.ToArray();
        }

        static double ReadNumber(string s, ref int p)
        {
            SkipWsLocal(s, ref p);
            int start = p;
            while (p < s.Length)
            {
                char c = s[p];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') p++;
                else break;
            }
            double d;
            double.TryParse(s.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return d;
        }

        static void SkipTo(string s, ref int p, char c)
        {
            while (p < s.Length && s[p] != c) p++;
        }

        static void SkipWsLocal(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') p++;
                else break;
            }
        }
    }
}
