using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JmaMap
{
    // 標準機能だけで動く軽量JSONパーサ／ライタ（C# 5 構文）。
    //
    // 境界GeoJSONは1ファイル最大6MB・座標が数十万点あるため、JavaScriptSerializer のように
    // 全要素を object へボクシングすると数倍のメモリを食う。そこで「値を読み飛ばす」SkipValue を
    // 公開し、必要な properties だけを実体化して geometry は原文のまま扱えるようにしてある。
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            return ParseValue(text, ref i);
        }

        public static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        public static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: 予期しない終端です");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        public static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') throw new FormatException("JSON: オブジェクトのキーが不正です");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                d[key] = ParseValue(s, ref i);
            }
            return d;
        }

        public static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                list.Add(ParseValue(s, ref i));
            }
            return list;
        }

        public static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // 開き '"'
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; break; }
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) break;
                    char e = s[i];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'u')
                    {
                        if (i + 4 < s.Length)
                        {
                            string hex = s.Substring(i + 1, 4);
                            int code;
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                    }
                    else sb.Append(e); // " \ / はそのまま
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // 中身を捨てる前提で文字列を読み飛ばす（エスケープを考慮）
        public static void SkipString(string s, ref int i)
        {
            i++; // 開き '"'
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\') { i += 2; continue; }
                i++;
                if (c == '"') return;
            }
        }

        // 任意の値を実体化せずに読み飛ばす。座標配列を捨てるための要。
        public static void SkipValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return;
            char c = s[i];
            if (c == '"') { SkipString(s, ref i); return; }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (ch == '"') { SkipString(s, ref i); continue; }
                    if (ch == '{' || ch == '[') { depth++; i++; continue; }
                    if (ch == '}' || ch == ']')
                    {
                        depth--;
                        i++;
                        if (depth <= 0) return;
                        continue;
                    }
                    i++;
                }
                return;
            }
            // 数値 / true / false / null
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == ',' || ch == '}' || ch == ']' || ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') break;
                i++;
            }
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            double d;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return d;
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("JSON: 不正なリテラルです");
            i += literal.Length;
        }

        /*** 取り出しヘルパ ***/

        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> Arr(object o)
        {
            return o as List<object>;
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (d.TryGetValue(key, out v)) return v;
            return null;
        }

        // 文字列/数値のどちらで来ても文字列として取り出す（地域コードは両方の形で現れる）
        public static string Str(object o)
        {
            if (o == null) return "";
            if (o is string) return (string)o;
            if (o is double)
            {
                double d = (double)o;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                    return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            if (o is bool) return ((bool)o) ? "true" : "false";
            return o.ToString();
        }

        public static string GetStr(Dictionary<string, object> d, string key)
        {
            return Str(Get(d, key));
        }

        /*** 書き出し ***/

        public static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            AppendEscaped(sb, s);
            sb.Append('"');
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder();
            AppendString(sb, s);
            return sb.ToString();
        }

        static void AppendEscaped(StringBuilder sb, string s)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
        }
    }
}
