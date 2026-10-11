namespace GamerToolManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 隐藏自检模式：GamerToolManager.exe --selftest，无窗口，输出到 stdout
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        // 原生拉取实测：--pulltest <appid>，产物放临时目录，stdout 输出
        if (args.Length > 1 && args[0].Equals("--pulltest", StringComparison.OrdinalIgnoreCase))
        {
            var store = new Store();
            store.Load();
            long appid = long.Parse(args[1]);
            string dest = Path.Combine(Path.GetTempPath(), "gtm_pulltest");
            Directory.CreateDirectory(dest);
            Console.WriteLine("== pulltest appid=" + appid + " dest=" + dest);
            var res = Store.NativePuller.PullAsync(appid, null, dest, store.ProxyEff,
                (t, _) => Console.WriteLine("  " + t), CancellationToken.None).GetAwaiter().GetResult();
            if (res == null)
            {
                Console.WriteLine("PULLTEST FAILED: no repo branch");
                return 1;
            }
            Console.WriteLine("dir: " + res.Dir);
            Console.WriteLine("lua: " + (res.LuaPath ?? "(none)"));
            Console.WriteLine("files: " + res.Files + " failed: " + res.FailedFiles + " repo: " + res.Repo);
            if (res.LuaPath != null && File.Exists(res.LuaPath))
                foreach (var line in File.ReadAllLines(res.LuaPath)) Console.WriteLine("  | " + line);
            Console.WriteLine(res.FailedFiles == 0 && res.LuaPath != null ? "PULLTEST OK" : "PULLTEST FAILED");
            return res.FailedFiles == 0 && res.LuaPath != null ? 0 : 1;
        }

        // 清单收集上传实测：--collecttest <目录>，扫描并把识别出的集合上传到收集仓库
        if (args.Length > 1 && args[0].Equals("--collecttest", StringComparison.OrdinalIgnoreCase))
        {
            var store = new Store();
            store.Load();
            string dir = args[1];
            var sets = Collector.ScanCollectionDir(dir).Where(s => s.Recognized).ToList();
            Console.WriteLine($"== collecttest dir={dir} sets={sets.Count} token={(Collector.UploadToken == "" ? "(无)" : "(已配置)")}");
            if (sets.Count == 0) { Console.WriteLine("COLLECTTEST FAILED: no recognized set"); return 1; }
            try { Collector.EnsureRepoAsync(store.ProxyEff).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine("COLLECTTEST FAILED: " + ex.Message); return 1; }
            Console.WriteLine("repo check OK");
            int ok = 0;
            foreach (var s in sets)
            {
                try
                {
                    var r = Collector.UploadSetAsync(store, s, t => Console.WriteLine("  " + t), CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine(r.Skipped ? $"  [{s.AppId}] 已是最新（跳过）" : $"  [{s.AppId}] 已上传 {r.UploadedFiles} 个文件 commit={r.CommitSha}");
                    ok++;
                }
                catch (Exception ex) { Console.WriteLine($"  [{s.AppId}] FAILED: {ex.Message}"); }
            }
            Console.WriteLine(ok == sets.Count ? "COLLECTTEST OK" : "COLLECTTEST FAILED");
            return ok == sets.Count ? 0 : 1;
        }

        // 布局实测模式：打开拉取对话框 0.8 秒，输出所有控件坐标
        if (args.Length > 0 && args[0].Equals("--layouttest", StringComparison.OrdinalIgnoreCase))
        {
            var store = new Store();
            store.Load();
            ApplicationConfiguration.Initialize();
            var d = new PullDialog(store, "1020790", false);
            d.Shown += async (_, _) =>
            {
                await Task.Delay(800);
                void Dump(Control c, int depth)
                {
                    Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + "  " + c.Bounds);
                    foreach (Control k in c.Controls) Dump(k, depth + 1);
                }
                Dump(d, 0);
                d.Close();
            };
            Application.Run(d);

            var s = new SettingsDialog(store);
            s.Shown += async (_, _) =>
            {
                await Task.Delay(800);
                void Dump2(Control c, int depth)
                {
                    Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + "  " + c.Bounds);
                    foreach (Control k in c.Controls) Dump2(k, depth + 1);
                }
                Dump2(s, 0);
                s.Close();
            };
            Application.Run(s);

            var m = new MainForm();
            m.Shown += async (_, _) =>
            {
                await Task.Delay(1200);
                void Dump3(Control c, int depth)
                {
                    Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + "  " + c.Bounds);
                    foreach (Control k in c.Controls) Dump3(k, depth + 1);
                }
                Dump3(m, 0);
                m.Close();
            };
            Application.Run(m);

            var cd = new CollectorDialog(store);
            cd.Shown += async (_, _) =>
            {
                await Task.Delay(800);
                void Dump4(Control c, int depth)
                {
                    Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + "  " + c.Bounds);
                    foreach (Control k in c.Controls) Dump4(k, depth + 1);
                }
                Dump4(cd, 0);
                cd.Close();
            };
            Application.Run(cd);

            var od = new OrphanDialog(store, new List<long> { 404010, 404012, 730 });
            od.Shown += async (_, _) =>
            {
                await Task.Delay(2500);
                void Dump5(Control c, int depth)
                {
                    Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + "  " + c.Bounds);
                    foreach (Control k in c.Controls) Dump5(k, depth + 1);
                }
                Dump5(od, 0);
                od.Close();
            };
            Application.Run(od);

            var pi = new ManifestPack.PackInfo { PackPath = "layouttest.gtm" };
            pi.Games.Add(new ManifestPack.PackGame { AppId = 460900, Name = "测试游戏", FileCount = 3, ManifestCount = 1, HasLua = true, HasKeyVdf = true, TotalBytes = 123456 });
            var ipd = new ImportPackDialog(store, pi);
            ipd.Shown += async (_, _) =>
            {
                await Task.Delay(1200);
                void Dump6(Control c, int depth)
                {
                    Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + "  " + c.Bounds);
                    foreach (Control k in c.Controls) Dump6(k, depth + 1);
                }
                Dump6(ipd, 0);
                ipd.Close();
            };
            Application.Run(ipd);
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { ShowError(e.ExceptionObject as Exception ?? new Exception("未知错误")); } catch { }
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show("发生未处理的错误：\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                "GamerTool 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}

/// <summary>无界面自检：核心逻辑（扫描 / GBK / SQLite / Steam 检测）不依赖窗口即可验证。</summary>
internal static class SelfTest
{
    public static int Run()
    {
        int fail = 0;
        Action<string, bool> check = (name, ok) =>
        {
            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name);
            if (!ok) fail++;
        };

        try
        {
            var store = new Store();
            store.Load();
            var entries = store.Scan();
            check("扫描: 游戏条目 " + entries.Count + " 个（文件夹 " +
                  entries.Count(e => e.FolderExists) + " / 已部署 " + entries.Count(e => e.Deployed) + "）", entries.Count > 0);

            check("Steam 目录: " + store.SteamDirEff, Directory.Exists(store.SteamDirEff));
            check("stplug-in: " + store.StPluginEff, Directory.Exists(store.StPluginEff));
            check("depotcache: " + store.DepotCacheEff, Directory.Exists(store.DepotCacheEff));
            check("SteamTools appids.db: " + store.AppIdsDbEff, File.Exists(store.AppIdsDbEff));
            check("清单工具: " + store.S.NormalExe, File.Exists(store.S.NormalExe));

            // 拖拽链接解析
            bool linkOk = Store.ExtractAppId("https://store.steampowered.com/app/1020790/NARUTO_X_BORUTO/") == 1020790
                       && Store.ExtractAppId("https://store.steampowered.com.cn/app/730/COUNTER_STRIKE/") == 730
                       && Store.ExtractAppId("https://steamcommunity.com/app/2772820") == 2772820
                       && Store.ExtractAppId("steam://store/570") == 570
                       && Store.ExtractAppId("  1244090 ") == 1244090
                       && Store.ExtractAppId("随便一段没有数字的文字") == null
                       && Store.ExtractAppIdFromPath(@"C:\GamerTool\[1020790]") == 1020790
                       && Store.ExtractAppIdFromPath(@"C:\x\1020791_2896417252875612038.manifest") == 1020791;
            check("拖拽链接/路径解析 AppID", linkOk);

            // GBK 解码
            byte[] gbk = { 0xC7, 0xEB, 0xCA, 0xE4, 0xC8, 0xEB }; // “请输入”
            string s = Native.decode_gbk(gbk);
            string codes = string.Join(",", s.Select(c => ((int)c).ToString("X4")));
            check("GBK 解码: " + s + "  (U+" + codes + ")", s == "请输入");

            // 清单收集引擎自检（不依赖网络）
            byte[] blob = System.Text.Encoding.UTF8.GetBytes("hello gamer tool\n");
            string sha = Collector.BlobSha(blob);
            check("BlobSha 向量: " + sha[..12] + "…", sha == "dc5e0f7783288b3ecded5f7f4d2aa364d1b930e2");

            string lua = Store.NativePuller.BuildLuaText(460900,
                new List<(long depot, string key)> { (460901, new string('a', 64)) },
                new[] { "460901_2896417252875612038.manifest" });
            check("BuildLuaText 生成解锁脚本",
                  lua.StartsWith("addappid(460900)") && lua.Contains("setManifestid(460901,\"2896417252875612038\",0)"));

            PackTest.Run(check);

            string scanRoot = Path.Combine(Path.GetTempPath(), "gtm_scan_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(scanRoot, "[460900]SampleGame"));
            File.WriteAllText(Path.Combine(scanRoot, "[460900]SampleGame", "460900.lua"), "addappid(460900)");
            Directory.CreateDirectory(Path.Combine(scanRoot, "nolabel"));
            File.WriteAllText(Path.Combine(scanRoot, "nolabel", "460901.lua"), "lua");
            Directory.CreateDirectory(Path.Combine(scanRoot, "unknown"));
            File.WriteAllText(Path.Combine(scanRoot, "unknown", "123_456.manifest"), "bin");
            var sets = Collector.ScanCollectionDir(scanRoot);
            check("扫描: 识别 3 个集合（2 可上传 + 1 无法识别）",
                  sets.Count == 3 &&
                  sets.Count(x => x.Recognized) == 2 &&
                  sets.Any(x => x.AppId == 460900 && x.Recognized) &&
                  sets.Any(x => x.AppId == 460901 && x.Recognized) &&
                  sets.Any(x => !x.Recognized && x.Note.Contains("无法识别")));
            try { Directory.Delete(scanRoot, true); } catch { }

            // SQLite（在临时副本上测试，不动真实数据库）
            string tmp = Path.Combine(Path.GetTempPath(), "gtm_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");
            File.Copy(store.AppIdsDbEff, tmp, true);
            var ids0 = MiniSql.ListAppIds(tmp);
            bool listed = ids0 != null && ids0.Count > 0;
            check("SQLite 读取 appids.db 副本: " + (ids0 == null ? -1 : ids0.Count) + " 行", listed);
            if (listed)
            {
                long victim = ids0.First();
                int del = MiniSql.DeleteAppIds(tmp, new[] { victim });
                var ids1 = MiniSql.ListAppIds(tmp);
                check("SQLite 删除一行 (" + victim + "): 影响 " + del + " 行",
                      del == 1 && ids1.Count == ids0.Count - 1);
            }
            try { File.Delete(tmp); } catch { }

            Console.WriteLine(fail == 0 ? "SELFTEST OK" : "SELFTEST FAILED");
            return fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAIL] 异常: " + ex);
            return 1;
        }
    }
}
