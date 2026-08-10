using System;
using System.Collections.Generic;
using System.Text;

namespace JmaMap
{
    // GeoJSONの1フィーチャを指す参照。geometry は文字列化せず、原文中の範囲だけを覚えておき、
    // レスポンス生成時にそのまま流し込む。座標を double へ起こさないので速く、メモリも増えない。
    public class FeatureRef
    {
        public string Code;      // properties から取り出した原コード（6桁/7桁など）
        public string Name;      // 区域名
        public int GeomStart;    // geometry 値の開始位置（'{'）
        public int GeomEnd;      // geometry 値の終端（次の文字の位置）
    }

    public static class GeoJson
    {
        // Code.js の featureCodeEquals_ / iterFeatureCodes_ と同じ優先順
        static readonly string[] CodeKeys = { "regioncode", "code", "Code", "AREA_CODE" };
        static readonly string[] NameKeys = { "name", "NAM", "NAM_JP" };

        // FeatureCollection（または単一Feature）を走査してフィーチャ参照の一覧を返す
        public static List<FeatureRef> Scan(string text)
        {
            var list = new List<FeatureRef>();
            int i = 0;
            Json.SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '{') return list;

            int objStart = i;
            i++;
            string topType = "";
            bool sawFeatures = false;
            while (true)
            {
                Json.SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') break;

                string key = Json.ParseString(text, ref i);
                Json.SkipWs(text, ref i);
                if (i < text.Length && text[i] == ':') i++;

                if (key == "features")
                {
                    sawFeatures = true;
                    ScanFeatureArray(text, ref i, list);
                }
                else if (key == "type")
                {
                    topType = Json.Str(Json.ParseValue(text, ref i));
                }
                else
                {
                    Json.SkipValue(text, ref i);
                }
            }

            // 単一 Feature 形式のファイルにも対応する
            if (!sawFeatures && topType == "Feature")
            {
                int j = objStart;
                FeatureRef single = ScanFeatureObject(text, ref j);
                if (single != null) list.Add(single);
            }
            return list;
        }

        static void ScanFeatureArray(string text, ref int i, List<FeatureRef> list)
        {
            Json.SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '[') { Json.SkipValue(text, ref i); return; }
            i++;
            while (true)
            {
                Json.SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == ']') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '{') { Json.SkipValue(text, ref i); continue; }
                FeatureRef fr = ScanFeatureObject(text, ref i);
                if (fr != null) list.Add(fr);
            }
        }

        static FeatureRef ScanFeatureObject(string text, ref int i)
        {
            var fr = new FeatureRef();
            fr.Code = "";
            fr.Name = "";
            i++; // '{'
            while (true)
            {
                Json.SkipWs(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') { i++; break; }
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') { Json.SkipValue(text, ref i); continue; }

                string key = Json.ParseString(text, ref i);
                Json.SkipWs(text, ref i);
                if (i < text.Length && text[i] == ':') i++;

                if (key == "properties")
                {
                    Json.SkipWs(text, ref i);
                    if (i < text.Length && text[i] == '{')
                    {
                        Dictionary<string, object> props = Json.ParseObject(text, ref i);
                        fr.Code = FirstOf(props, CodeKeys);
                        fr.Name = FirstOf(props, NameKeys);
                    }
                    else Json.SkipValue(text, ref i);
                }
                else if (key == "geometry")
                {
                    Json.SkipWs(text, ref i);
                    fr.GeomStart = i;
                    Json.SkipValue(text, ref i);
                    fr.GeomEnd = i;
                }
                else
                {
                    Json.SkipValue(text, ref i);
                }
            }
            return fr;
        }

        static string FirstOf(Dictionary<string, object> props, string[] keys)
        {
            for (int k = 0; k < keys.Length; k++)
            {
                object v = Json.Get(props, keys[k]);
                if (v == null) continue;
                string s = Json.Str(v).Trim();
                if (s.Length > 0) return s;
            }
            return "";
        }

        public static bool HasGeometry(FeatureRef fr)
        {
            return fr != null && fr.GeomEnd > fr.GeomStart;
        }
    }
}
