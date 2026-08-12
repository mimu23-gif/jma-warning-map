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

        // 津波予報区は3桁コードの線データで、6/7桁前提の索引には載らない。
        // 単独ファイルとして持ち、コードは原文のまま突き合わせる。
        public string TsunamiGeoJson = "../data/boundaries/tsunami/tsunami_area.geojson";

        // ズーム別の簡略化の許容誤差（度）。maxZoom 以下ならその tolerance を使う。
        // 遠景ほど大きく間引き、近景は同梱データそのまま（tolerance = 0）を送る。
        public List<double[]> ZoomTolerances = new List<double[]>();   // [maxZoom, tolerance]

        public string ExeDir = "";

        public static Settings Load(string path, string exeDir)
        {
            var s = new Settings();
            s.ExeDir = exeDir;
            s.GeoFolders.Add("1saibun");
            s.GeoFolders.Add("hukenyohoukutou");
            s.GeoFolders.Add("sikutyousonnwomatometatiikitou");
            s.GeoFolders.Add("sityousontou");
            s.SetDefaultZoomTolerances();

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
                string tg = Json.GetStr(root, "tsunamiGeoJson").Trim();
                if (tg.Length > 0) s.TsunamiGeoJson = tg;

                var zoom = Json.Arr(Json.Get(root, "zoomTolerances"));
                if (zoom != null && zoom.Count > 0)
                {
                    s.ZoomTolerances.Clear();
                    for (int i = 0; i < zoom.Count; i++)
                    {
                        var pair = Json.Arr(zoom[i]);
                        if (pair == null || pair.Count < 2) continue;
                        double mz, tol;
                        if (!double.TryParse(Json.Str(pair[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out mz)) continue;
                        if (!double.TryParse(Json.Str(pair[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out tol)) continue;
                        s.ZoomTolerances.Add(new double[] { mz, tol });
                    }
                    if (s.ZoomTolerances.Count == 0) s.SetDefaultZoomTolerances();
                    s.ZoomTolerances.Sort(new Comparison<double[]>(CompareByMaxZoom));
                }

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

        /// <summary>
        /// 既定のズーム別許容誤差。各段は「その縮尺で1ピクセル前後の誤差」を目安にしてある
        /// （Web Mercatorの1ピクセルは緯度45度・ズームzで約 110km / 2^z）。
        /// </summary>
        public void SetDefaultZoomTolerances()
        {
            ZoomTolerances.Clear();
            ZoomTolerances.Add(new double[] { 5, 0.02 });     // 全国表示: 約2.2km
            ZoomTolerances.Add(new double[] { 7, 0.008 });    // 地方表示: 約890m
            ZoomTolerances.Add(new double[] { 9, 0.003 });    // 県表示  : 約330m
            ZoomTolerances.Add(new double[] { 11, 0.001 });   // 市郡表示: 約110m
            ZoomTolerances.Add(new double[] { 13, 0.0005 });  // 市街表示: 約56m
            ZoomTolerances.Add(new double[] { 99, 0 });       // それ以上: 同梱データそのまま(約22m)
        }

        static int CompareByMaxZoom(double[] x, double[] y)
        {
            return x[0].CompareTo(y[0]);
        }

        /// <summary>ズームに対応する許容誤差（度）。0 なら簡略化しない。</summary>
        public double ToleranceForZoom(double zoom)
        {
            for (int i = 0; i < ZoomTolerances.Count; i++)
            {
                if (zoom <= ZoomTolerances[i][0]) return ZoomTolerances[i][1];
            }
            return 0;
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
