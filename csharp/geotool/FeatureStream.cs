using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JmaMap.Tools
{
    // 巨大なGeoJSON（全国版は数百MB）を丸ごとメモリに載せずに1フィーチャずつ取り出す読み取り器。
    // 文字列リテラル内の括弧・エスケープを正しく無視しながら、対応する括弧までを原文のまま切り出す。
    public class FeatureStream : IDisposable
    {
        readonly TextReader reader;
        readonly char[] buf = new char[1 << 16];
        int len;
        int pos;
        bool insideFeatures;

        public FeatureStream(string path)
        {
            reader = new StreamReader(path, Encoding.UTF8, true, 1 << 16);
        }

        public void Dispose()
        {
            reader.Dispose();
        }

        bool Fill()
        {
            len = reader.Read(buf, 0, buf.Length);
            pos = 0;
            return len > 0;
        }

        int Peek()
        {
            if (pos >= len && !Fill()) return -1;
            return buf[pos];
        }

        int Read()
        {
            if (pos >= len && !Fill()) return -1;
            return buf[pos++];
        }

        void SkipWs()
        {
            while (true)
            {
                int c = Peek();
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { pos++; continue; }
                return;
            }
        }

        /// <summary>トップレベルのオブジェクトを走査し、features 配列の直前まで進む。</summary>
        public bool MoveToFeatures()
        {
            SkipWs();
            if (Read() != '{') throw new FormatException("GeoJSONのトップレベルがオブジェクトではありません");

            while (true)
            {
                SkipWs();
                int c = Peek();
                if (c < 0) return false;
                if (c == ',') { pos++; continue; }
                if (c == '}') { pos++; return false; }
                if (c != '"') throw new FormatException("GeoJSONのキーが不正です");

                string key = ReadStringLiteral();
                SkipWs();
                if (Peek() == ':') pos++;
                SkipWs();

                if (key == "features")
                {
                    if (Peek() != '[') throw new FormatException("features が配列ではありません");
                    pos++;
                    insideFeatures = true;
                    return true;
                }
                SkipValue();
            }
        }

        /// <summary>次のフィーチャを原文のまま返す。終端なら null。</summary>
        public string NextFeature()
        {
            if (!insideFeatures) return null;
            while (true)
            {
                SkipWs();
                int c = Peek();
                if (c < 0) return null;
                if (c == ',') { pos++; continue; }
                if (c == ']') { pos++; insideFeatures = false; return null; }
                if (c != '{') { SkipValue(); continue; }
                return ReadBalanced();
            }
        }

        string ReadStringLiteral()
        {
            var sb = new StringBuilder();
            pos++; // 開き '"'
            bool esc = false;
            while (true)
            {
                int c = Read();
                if (c < 0) break;
                char ch = (char)c;
                if (esc) { sb.Append(ch); esc = false; continue; }
                if (ch == '\\') { esc = true; continue; }
                if (ch == '"') break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>任意の値を読み飛ばす（文字列・配列・オブジェクト・数値）。</summary>
        void SkipValue()
        {
            SkipWs();
            int c = Peek();
            if (c < 0) return;
            if (c == '"') { ReadStringLiteral(); return; }
            if (c == '{' || c == '[') { ReadBalanced(); return; }
            while (true)
            {
                int ch = Peek();
                if (ch < 0 || ch == ',' || ch == '}' || ch == ']' ||
                    ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') return;
                pos++;
            }
        }

        /// <summary>現在位置の '{' または '[' から対応する閉じ括弧までを原文で返す。</summary>
        string ReadBalanced()
        {
            var sb = new StringBuilder(4096);
            int depth = 0;
            bool inStr = false;
            bool esc = false;
            while (true)
            {
                int c = Read();
                if (c < 0) throw new FormatException("JSONが途中で終わっています");
                char ch = (char)c;
                sb.Append(ch);

                if (inStr)
                {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0) return sb.ToString();
                }
            }
        }
    }

    // 都道府県別GeoJSONの書き出し。既存データ（geopandas → OGR GeoJSONドライバ）の
    // 書式に合わせてある: BOM無しUTF-8・LF改行・1フィーチャ1行・区切りは ",\n"。
    public class GeoJsonWriter : IDisposable
    {
        readonly StreamWriter w;
        bool first = true;
        public int Count;

        public GeoJsonWriter(string path, string layerName)
        {
            w = new StreamWriter(path, false, new UTF8Encoding(false));
            w.NewLine = "\n";
            w.Write("{\n");
            w.Write("\"type\": \"FeatureCollection\",\n");
            w.Write("\"name\": \"");
            w.Write(layerName);
            w.Write("\",\n");
            w.Write("\"crs\": { \"type\": \"name\", \"properties\": { \"name\": \"urn:ogc:def:crs:OGC:1.3:CRS84\" } },\n");
            w.Write("\"features\": [\n");
        }

        public void WriteFeature(string rawFeature)
        {
            if (!first) w.Write(",\n");
            first = false;
            w.Write(rawFeature);
            Count++;
        }

        public void Dispose()
        {
            w.Write("\n]\n}\n");
            w.Flush();
            w.Dispose();
        }
    }
}
