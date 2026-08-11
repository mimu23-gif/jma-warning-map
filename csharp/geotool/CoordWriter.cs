using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace JmaMap.Tools
{
    // 座標・ジオメトリの書き出し（OGR GeoJSONドライバと同じ書式）。
    // shp2geojson と simplify の両方から使う。
    public static class CoordWriter
    {
        const int CoordDecimals = 15;
        static readonly BigInteger Scale = BigInteger.Pow(10, CoordDecimals);

        /// <summary>ポリゴン群を GeoJSON の geometry オブジェクトとして書く。</summary>
        public static void AppendGeometry(StringBuilder sb, List<List<double[]>> polygons)
        {
            if (polygons == null || polygons.Count == 0) { sb.Append("null"); return; }

            if (polygons.Count == 1)
            {
                sb.Append("{ \"type\": \"Polygon\", \"coordinates\": ");
                AppendPolygon(sb, polygons[0]);
                sb.Append(" }");
                return;
            }
            sb.Append("{ \"type\": \"MultiPolygon\", \"coordinates\": [ ");
            for (int i = 0; i < polygons.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendPolygon(sb, polygons[i]);
            }
            sb.Append(" ] }");
        }

        public static void AppendPolygon(StringBuilder sb, List<double[]> rings)
        {
            sb.Append("[ ");
            for (int i = 0; i < rings.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendRing(sb, rings[i]);
            }
            sb.Append(" ]");
        }

        public static void AppendRing(StringBuilder sb, double[] ring)
        {
            sb.Append("[ ");
            int n = ring.Length / 2;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("[ ");
                AppendCoord(sb, ring[i * 2]);
                sb.Append(", ");
                AppendCoord(sb, ring[i * 2 + 1]);
                sb.Append(" ]");
            }
            sb.Append(" ]");
        }

        // Cの "%.15f" 相当。.NET Framework の ToString("F15") は有効数字15桁で頭打ちになり
        // 精度が落ちるため、doubleの厳密な10進展開から BigInteger で正しく丸める。
        //
        // 補足: GDALは "999999..." のような並びを見つけると短い表記を選ぶことがあり、その場合だけ
        // 文字列としての見た目が変わる（例: 144.972053060999997 と 144.972053061）。
        // どちらも読み戻せば同一のdoubleなので数値としては等価。
        public static void AppendCoord(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }

            long bits = BitConverter.DoubleToInt64Bits(d);
            bool negative = bits < 0;
            int exponent = (int)((bits >> 52) & 0x7FF);
            long mantissa = bits & 0xFFFFFFFFFFFFFL;
            if (exponent == 0) exponent = -1074;                 // 非正規化数
            else { mantissa |= 1L << 52; exponent -= 1075; }

            BigInteger num = mantissa * Scale;
            BigInteger den = BigInteger.One;
            if (exponent > 0) num <<= exponent;
            else den <<= -exponent;

            BigInteger rem;
            BigInteger q = BigInteger.DivRem(num, den, out rem);
            BigInteger twice = rem * 2;
            int cmp = twice.CompareTo(den);
            if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += BigInteger.One;

            string digits = q.ToString(CultureInfo.InvariantCulture);
            if (digits.Length <= CoordDecimals) digits = digits.PadLeft(CoordDecimals + 1, '0');

            int split = digits.Length - CoordDecimals;
            string intPart = digits.Substring(0, split);
            string fracPart = digits.Substring(split).TrimEnd('0');

            if (negative && q.Sign != 0) sb.Append('-');
            sb.Append(intPart);
            if (fracPart.Length > 0)
            {
                sb.Append('.');
                sb.Append(fracPart);
            }
        }
    }
}
