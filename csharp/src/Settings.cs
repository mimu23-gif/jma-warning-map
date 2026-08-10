using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JmaMap
{
    // settings.json（実行ファイルと同じ場所）。GAS版の Script Properties に相当する。
    public class Settings
    {
        public int Port = 8787;
        public int PortTries = 10;
        public string Browser = "default";       // "default"（既定ブラウザ） / "app"（Edgeアプリモード）
        public string WebDir = "web";
        public string DataDir = "..";
        public string PointsCsvPath = "../points.csv";
        public string IndexCachePath = "region-index-local.json";
        public int GeoCacheFiles = 8;
        public List<string> GeoFolders = new List<string>();

        public string ExeDir = "";

        public static Settings Load(string path, string exeDir)
        {
            var s = new Settings();
            s.ExeDir = exeDir;
            s.GeoFolders.Add("1saibun");
            s.GeoFolders.Add("hukenyohoukutou");
            s.GeoFolders.Add("sikutyousonnwomatometatiikitou");
            s.GeoFolders.Add("sityousontou");

            if (!File.Exists(path)) return s;

            try
            {
                var root = Json.Obj(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (root == null) return s;

                s.Port = GetInt(root, "port", s.Port);
                s.PortTries = GetInt(root, "portTries", s.PortTries);
                s.GeoCacheFiles = GetInt(root, "geoCacheFiles", s.GeoCacheFiles);

                string b = Json.GetStr(root, "browser").Trim();
                if (b.Length > 0) s.Browser = b;
                string w = Json.GetStr(root, "webDir").Trim();
                if (w.Length > 0) s.WebDir = w;
                string d = Json.GetStr(root, "dataDir").Trim();
                if (d.Length > 0) s.DataDir = d;
                string p = Json.GetStr(root, "pointsCsv").Trim();
                if (p.Length > 0) s.PointsCsvPath = p;
                string ic = Json.GetStr(root, "indexCache").Trim();
                if (ic.Length > 0) s.IndexCachePath = ic;

                var folders = Json.Arr(Json.Get(root, "geoFolders"));
                if (folders != null && folders.Count > 0)
                {
                    s.GeoFolders.Clear();
                    for (int i = 0; i < folders.Count; i++)
                    {
                        string f = Json.Str(folders[i]).Trim();
                        if (f.Length > 0) s.GeoFolders.Add(f);
                    }
                }
            }
            catch (Exception)
            {
                // 壊れた設定ファイルで起動不能にはしない（既定値で動かす）
            }
            return s;
        }

        static int GetInt(Dictionary<string, object> root, string key, int fallback)
        {
            object v = Json.Get(root, key);
            if (v == null) return fallback;
            double d;
            if (double.TryParse(Json.Str(v), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (int)d;
            return fallback;
        }

        // 相対パスは実行ファイルの場所を基準に解決する
        public string Resolve(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return ExeDir;
            if (Path.IsPathRooted(relative)) return Path.GetFullPath(relative);
            return Path.GetFullPath(Path.Combine(ExeDir, relative));
        }

        public List<string> ResolvedGeoFolders()
        {
            string baseDir = Resolve(DataDir);
            var list = new List<string>();
            for (int i = 0; i < GeoFolders.Count; i++)
            {
                string f = GeoFolders[i];
                list.Add(Path.IsPathRooted(f) ? Path.GetFullPath(f) : Path.GetFullPath(Path.Combine(baseDir, f)));
            }
            return list;
        }
    }
}
