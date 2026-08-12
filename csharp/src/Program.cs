using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace JmaMap
{
    // 起動・常駐・ブラウザ起動を受け持つ。
    // 画面はブラウザ側にあるので、このプロセス自体はトレイアイコンだけを持つ。
    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        const string MutexName = "Local\\JmaWarningMapSingleInstance";
        const string PortFileName = "runtime-port.txt";

        static Settings cfg;
        static string exeDir = "";
        static string logPath = "";
        static bool toConsole;
        static Server server;
        static NotifyIcon tray;

        [STAThread]
        static void Main(string[] args)
        {
            exeDir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(exeDir, "jmamap.log");
            cfg = Settings.Load(Path.Combine(exeDir, "settings.json"), exeDir);

            if (HasFlag(args, "--console"))
            {
                toConsole = AttachConsole(-1);
            }

            if (HasFlag(args, "--selftest"))
            {
                RunSelfTest(args);
                return;
            }

            bool createdNew;
            var mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 二重起動：既に動いているインスタンスのURLを開くだけにする
                string existing = ReadPortUrl();
                if (HasFlag(args, "--noopen")) Log("二重起動を検知しました（--noopen のため何も開きません）: " + existing);
                else if (existing != null) OpenBrowser(existing, cfg.Browser);
                else MessageBox.Show("すでに起動しています。", "気象庁警報マップ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                server = new Server(cfg);
                server.Log = Log;
                if (!server.Start())
                {
                    MessageBox.Show("ポートを確保できませんでした（" + cfg.Port.ToString(CultureInfo.InvariantCulture)
                        + " から " + cfg.PortTries.ToString(CultureInfo.InvariantCulture) + " 個を試行）。\n"
                        + "settings.json の port を変更してください。",
                        "気象庁警報マップ", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                WritePortFile(server.Port);
                CreateTray();
                // --noopen: 動作確認用。サーバだけ立ててブラウザは開かない
                if (!HasFlag(args, "--noopen")) OpenBrowser(server.BaseUrl, cfg.Browser);
                else Log("--noopen が指定されたためブラウザは開きません: " + server.BaseUrl);

                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                Log("致命的なエラー: " + ex.ToString());
                MessageBox.Show("起動に失敗しました:\n" + ex.Message, "気象庁警報マップ",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Shutdown();
                GC.KeepAlive(mutex);
                mutex.Close();
            }
        }

        /*** トレイ ***/

        static void CreateTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("ブラウザで開く", null, delegate(object s, EventArgs e)
            {
                OpenBrowser(server.BaseUrl, cfg.Browser);
            });
            menu.Items.Add("索引を再構築", null, delegate(object s, EventArgs e)
            {
                Balloon("索引の再構築を開始しました…");
                var t = new Thread(delegate()
                {
                    try
                    {
                        server.RebuildIndex();
                        Balloon("索引の再構築が完了しました。");
                    }
                    catch (Exception ex)
                    {
                        Log("索引の再構築に失敗: " + ex.Message);
                        Balloon("索引の再構築に失敗しました: " + ex.Message);
                    }
                });
                t.IsBackground = true;
                t.Start();
            });
            menu.Items.Add("ログを開く", null, delegate(object s, EventArgs e)
            {
                try { if (File.Exists(logPath)) Process.Start(logPath); }
                catch (Exception ex) { Log("ログを開けません: " + ex.Message); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了", null, delegate(object s, EventArgs e)
            {
                Application.Exit();
            });

            tray = new NotifyIcon();
            // アイコンはWindows標準のものを借りる（.ico ファイルを同梱しないで済む）
            tray.Icon = SystemIcons.Information;
            tray.Text = "気象庁警報マップ (localhost:" + server.Port.ToString(CultureInfo.InvariantCulture) + ")";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate(object s, EventArgs e)
            {
                OpenBrowser(server.BaseUrl, cfg.Browser);
            };
        }

        static void Balloon(string text)
        {
            NotifyIcon n = tray;
            if (n == null) return;
            try
            {
                n.BalloonTipTitle = "気象庁警報マップ";
                n.BalloonTipText = text;
                n.ShowBalloonTip(4000);
            }
            catch (Exception) { }
        }

        static void Shutdown()
        {
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } }
            catch (Exception) { }
            try { if (server != null) server.Stop(); }
            catch (Exception) { }
            try
            {
                string pf = Path.Combine(exeDir, PortFileName);
                if (File.Exists(pf)) File.Delete(pf);
            }
            catch (Exception) { }
        }

        /*** ブラウザ ***/

        static void OpenBrowser(string url, string mode)
        {
            if (string.Equals(mode, "app", StringComparison.OrdinalIgnoreCase))
            {
                string edge = FindEdge();
                if (edge != null)
                {
                    try
                    {
                        Process.Start(edge, "--app=" + url + " --window-size=1440,900");
                        return;
                    }
                    catch (Exception ex) { Log("Edgeアプリモードの起動に失敗: " + ex.Message); }
                }
                else Log("Edgeが見つからないため既定ブラウザで開きます。");
            }

            // 既定ブラウザ（.NET Framework では UseShellExecute が既定 true なのでURLを直接渡せる）
            try
            {
                Process.Start(url);
                return;
            }
            catch (Exception ex) { Log("既定ブラウザの起動に失敗: " + ex.Message); }

            string fallback = FindEdge();
            if (fallback != null)
            {
                try
                {
                    Process.Start(fallback, url);
                    return;
                }
                catch (Exception ex) { Log("Edgeでの起動に失敗: " + ex.Message); }
            }

            MessageBox.Show("ブラウザを自動で開けませんでした。次のURLを手動で開いてください:\n" + url,
                "気象庁警報マップ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static string FindEdge()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "Microsoft\\Edge\\Application\\msedge.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Microsoft\\Edge\\Application\\msedge.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i])) return candidates[i];
            }
            return null;
        }

        /*** ポートファイル（二重起動時に既存インスタンスのURLを知るため） ***/

        static void WritePortFile(int port)
        {
            try
            {
                File.WriteAllText(Path.Combine(exeDir, PortFileName),
                    port.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("ポートファイルを書けません: " + ex.Message); }
        }

        static string ReadPortUrl()
        {
            try
            {
                string pf = Path.Combine(exeDir, PortFileName);
                if (!File.Exists(pf)) return null;
                string s = File.ReadAllText(pf, Encoding.UTF8).Trim();
                int port;
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) return null;
                return "http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            }
            catch (Exception) { return null; }
        }

        /*** 自己診断（GUIを出さずに実データで一通り動かす） ***/

        static void RunSelfTest(string[] args)
        {
            string outPath = Path.Combine(exeDir, "selftest.txt");
            var report = new StringBuilder();
            Action<string> say = delegate(string line)
            {
                report.AppendLine(line);
                if (toConsole) Console.WriteLine(line);
            };

            try
            {
                var srv = new Server(cfg);
                srv.Log = say;

                say("== 自己診断 ==");
                say("データフォルダ: " + cfg.Resolve(cfg.DataDir));

                var sw = Stopwatch.StartNew();
                GeoIndex idx = srv.GetIndex();
                sw.Stop();
                say("索引: files=" + idx.FileCount.ToString(CultureInfo.InvariantCulture)
                    + " features=" + idx.FeatureCount.ToString(CultureInfo.InvariantCulture)
                    + " raw=" + idx.Raw.Count.ToString(CultureInfo.InvariantCulture)
                    + " norm6=" + idx.Norm6.Count.ToString(CultureInfo.InvariantCulture)
                    + " (" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");

                sw = Stopwatch.StartNew();
                PointsData pts = srv.GetPoints();
                sw.Stop();
                say("地点: items=" + pts.Items.Count.ToString(CultureInfo.InvariantCulture)
                    + " types1=[" + string.Join(", ", pts.Types1.ToArray()) + "]"
                    + " (" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");

                sw = Stopwatch.StartNew();
                System.Collections.Generic.List<WarnItem> items = Jma.GetActiveWarnings();
                sw.Stop();
                say("警報: 地域数=" + items.Count.ToString(CultureInfo.InvariantCulture)
                    + " (" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < items.Count && i < 5; i++)
                {
                    say("  " + items[i].RegionCode + " " + items[i].Level
                        + " [" + string.Join(",", items[i].Codes.ToArray()) + "] "
                        + string.Join("・", items[i].Kinds.ToArray()));
                }

                sw = Stopwatch.StartNew();
                var writer = new StringWriter();
                srv.WriteWarnings(writer, null, null);
                sw.Stop();
                string json = writer.ToString();
                say("応答: " + (json.Length / 1024).ToString(CultureInfo.InvariantCulture) + " KB, "
                    + CountOccurrences(json, "\"type\":\"Feature\"").ToString(CultureInfo.InvariantCulture) + " features, "
                    + "未解決 " + CountOccurrences(json, "\"reason\":").ToString(CultureInfo.InvariantCulture) + " 件 ("
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");

                int head = Math.Min(json.Length, 400);
                say("先頭: " + json.Substring(0, head));

                // 併記する災害情報（警報とは別レイヤー）
                sw = Stopwatch.StartNew();
                System.Collections.Generic.List<QuakeItem> quakes = Disaster.GetRecentQuakes(24, 20);
                sw.Stop();
                say("地震: 直近24時間で " + quakes.Count.ToString(CultureInfo.InvariantCulture) + " 件 ("
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < quakes.Count && i < 3; i++)
                {
                    QuakeItem q = quakes[i];
                    say("  " + q.OriginTime + " " + q.Hypocenter + " M" + q.Magnitude
                        + " 最大震度" + q.MaxInt + " 市区町村" + q.Cities.Count.ToString(CultureInfo.InvariantCulture) + "件");
                }
                if (quakes.Count > 0)
                {
                    sw = Stopwatch.StartNew();
                    var qw = new StringWriter();
                    srv.WriteQuakeIntensity(qw, quakes[0], 0);
                    sw.Stop();
                    string qjson = qw.ToString();
                    say("震度の応答: " + (qjson.Length / 1024).ToString(CultureInfo.InvariantCulture) + " KB, "
                        + CountOccurrences(qjson, "\"type\":\"Feature\"").ToString(CultureInfo.InvariantCulture) + " features, "
                        + "未解決 " + CountOccurrences(qjson, "\"reason\":").ToString(CultureInfo.InvariantCulture) + " 件 ("
                        + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                }

                sw = Stopwatch.StartNew();
                System.Collections.Generic.List<TyphoonItem> typhoons = Disaster.GetTyphoons();
                sw.Stop();
                say("台風: " + typhoons.Count.ToString(CultureInfo.InvariantCulture) + " 個 ("
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < typhoons.Count; i++)
                {
                    TyphoonItem t = typhoons[i];
                    say("  " + t.Id + " " + t.Category + " " + t.NameJp
                        + " 実績" + t.TrackTyphoon.Count.ToString(CultureInfo.InvariantCulture) + "点"
                        + " 予報" + t.Points.Count.ToString(CultureInfo.InvariantCulture) + "点"
                        + (t.HasGale ? " 暴風警戒域あり" : ""));
                }
                sw = Stopwatch.StartNew();
                System.Collections.Generic.List<VolcanoWarn> volcanoes = Hazards.GetVolcanoWarnings();
                sw.Stop();
                say("噴火警報: " + volcanoes.Count.ToString(CultureInfo.InvariantCulture) + " 件 ("
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < volcanoes.Count && i < 4; i++)
                {
                    VolcanoWarn v = volcanoes[i];
                    say("  " + v.VolcanoName + " " + v.KindName + "(" + v.KindCode + ") "
                        + v.LevelName + " 市町村" + v.Municipalities.Count.ToString(CultureInfo.InvariantCulture) + "件");
                }

                sw = Stopwatch.StartNew();
                System.Collections.Generic.List<AshFall> ashes = Hazards.GetAshFalls(40);
                sw.Stop();
                say("降灰予報: " + ashes.Count.ToString(CultureInfo.InvariantCulture) + " 火山 ("
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < ashes.Count && i < 4; i++)
                {
                    AshFall a = ashes[i];
                    say("  " + a.VolcanoName + " 降灰" + a.Ash.Count.ToString(CultureInfo.InvariantCulture)
                        + "市町村 / 噴石" + a.Stone.Count.ToString(CultureInfo.InvariantCulture) + "市町村");
                }

                sw = Stopwatch.StartNew();
                System.Collections.Generic.List<FloodWarn> floods = Hazards.GetFloodWarnings(60);
                sw.Stop();
                int active = 0;
                for (int i = 0; i < floods.Count; i++) { if (!floods[i].Cleared) active++; }
                say("指定河川洪水予報: 発表中 " + active.ToString(CultureInfo.InvariantCulture)
                    + " 河川 / 直近の報がある河川 " + floods.Count.ToString(CultureInfo.InvariantCulture) + " ("
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < floods.Count && i < 6; i++)
                {
                    FloodWarn f = floods[i];
                    // 洪水は府県予報区コードでしか塗れないので、その解決可否をここで確かめておく
                    var resolved = new System.Collections.Generic.List<string>();
                    for (int p = 0; p < f.PrefCodes.Count; p++)
                    {
                        IndexEntry hit = idx.Find(f.PrefCodes[p], false);
                        resolved.Add(f.PrefCodes[p] + (hit != null ? "→OK" : "→未解決"));
                    }
                    say("  " + (f.Cleared ? "[解除] " : "[発表中] ") + f.RiverName
                        + " レベル" + f.Level.ToString(CultureInfo.InvariantCulture)
                        + " " + f.KindName + " 府県=" + string.Join(",", f.PrefNames.ToArray())
                        + " 塗り先=" + string.Join(",", resolved.ToArray())
                        + " 区間" + f.Sections.Count.ToString(CultureInfo.InvariantCulture) + "件");
                }

                sw = Stopwatch.StartNew();
                TsunamiReport ts = Hazards.GetTsunami();
                sw.Stop();
                int tsActive = 0;
                for (int i = 0; i < ts.Areas.Count; i++) { if (Hazards.TsunamiRank(ts.Areas[i].KindName) > 0) tsActive++; }
                say("津波: " + (ts.Cleared ? "発表なし" : "発表中")
                    + " / 区域" + ts.Areas.Count.ToString(CultureInfo.InvariantCulture)
                    + "（うち警報・注意報 " + tsActive.ToString(CultureInfo.InvariantCulture) + "）"
                    + " 最新報=" + ts.Title + " " + ts.ReportedAt
                    + " (" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
                for (int i = 0; i < ts.Areas.Count && i < 4; i++)
                {
                    TsunamiArea a = ts.Areas[i];
                    say("  " + a.Code + " " + a.Name + " " + a.KindName);
                }
                // 発表が無いときでも、海岸線データを引けること自体は確かめておく
                var probe = new System.Collections.Generic.List<string>();
                for (int i = 0; i < ts.Areas.Count && probe.Count < 2; i++) probe.Add(ts.Areas[i].Code);
                if (probe.Count == 0) { probe.Add("100"); probe.Add("712"); }
                say("  津波予報区データ: " + srv.CheckTsunamiData(probe, 0.003));

                say("== 正常終了 ==");
            }
            catch (Exception ex)
            {
                say("!! 失敗: " + ex.ToString());
            }

            try { File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(true)); }
            catch (Exception) { }
        }

        static int CountOccurrences(string haystack, string needle)
        {
            int n = 0;
            int i = 0;
            while (true)
            {
                int k = haystack.IndexOf(needle, i, StringComparison.Ordinal);
                if (k < 0) break;
                n++;
                i = k + needle.Length;
            }
            return n;
        }

        /*** 雑用 ***/

        static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static readonly object logGate = new object();

        static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            if (toConsole)
            {
                try { Console.WriteLine(line); } catch (Exception) { }
            }
            try
            {
                lock (logGate)
                {
                    File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }
    }
}
