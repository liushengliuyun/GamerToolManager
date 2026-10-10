using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GamerToolManager;

// ---------------- 主题配色 ----------------
internal static class Theme
{
    public static readonly Color Back   = Color.FromArgb(24, 26, 34);
    public static readonly Color Panel  = Color.FromArgb(31, 34, 44);
    public static readonly Color Input  = Color.FromArgb(40, 44, 56);
    public static readonly Color Hover  = Color.FromArgb(48, 53, 66);
    public static readonly Color Active = Color.FromArgb(55, 61, 76);
    public static readonly Color Border = Color.FromArgb(56, 61, 76);
    public static readonly Color Text   = Color.FromArgb(230, 233, 240);
    public static readonly Color Sub    = Color.FromArgb(150, 158, 176);
    public static readonly Color Accent = Color.FromArgb(80, 130, 245);
    public static readonly Color Green  = Color.FromArgb(74, 196, 124);
    public static readonly Color Red    = Color.FromArgb(229, 90, 82);
    public static readonly Color Yellow = Color.FromArgb(228, 180, 80);
    public static readonly Color ToolC  = Color.FromArgb(130, 145, 168);
}

// ---------------- 设置 ----------------
public class Settings
{
    public string GamerToolDir { get; set; } = @"C:\GamerTool";
    public string SteamDir { get; set; } = "";          // 留空 = 注册表自动检测
    public string StPluginDir { get; set; } = "";       // 留空 = <steam>\config\stplug-in
    public string DepotCacheDir { get; set; } = "";     // 留空 = <steam>\config\depotcache
    public string AppIdsDb { get; set; } = "";          // 留空 = C:\Program Files\SteamTools\appids.db
    public string M2LVariant { get; set; } = "normal"; // normal | nolock
    public string NameProxy { get; set; } = "http://127.0.0.1:7892";
    public bool CleanDepotCache { get; set; } = true;   // 还原时同时清理 depotcache 清单
    public bool CleanAppIdsDb { get; set; } = true;     // 还原时同时清理 SteamTools appids.db 登记
    public bool PassProxyToTool { get; set; } = false;  // (保留兼容)
    public bool AutoFetchNames { get; set; } = true;    // 启动时后台抓取缺失游戏名

    // ----- 窗口布局记忆 -----
    public int WinW { get; set; } = 0;      // 0 = 未记忆，用默认
    public int WinH { get; set; } = 0;
    public bool WinMax { get; set; }
    public int SplitTop { get; set; } = 0;  // 列表 | 详情 分割位置
    public int SplitMain { get; set; } = 0; // 主区 | 日志 分割位置
    public int RegSort { get; set; } = 0;   // 游戏排序: 0=默认 1=注册时间最新在上 2=最旧在上

    public string NormalExe => ResolveTool("Manifest2Lua.exe");
    public string NolockExe => ResolveTool("Manifest2Lua-nolock.exe");
    public string SelectedExe => M2LVariant == "nolock" ? NolockExe : NormalExe;

    /// <summary>工具解析：优先 exe 旁边（安装包自带），其次 GamerTool 目录，最后返回 exe 旁路径（用于报错提示）。</summary>
    private string ResolveTool(string name)
    {
        string besideApp = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(besideApp)) return besideApp;
        string inRoot = Path.Combine(GamerToolDir, name);
        if (File.Exists(inRoot)) return inRoot;
        return besideApp;
    }
}

// ---------------- 持久化的游戏备注记录 ----------------
public class GameRecord
{
    public long AppId { get; set; }
    public string Name { get; set; }         // 用户自定义名（优先显示）
    public string FetchedName { get; set; }  // 联网抓取缓存
    public DateTime? DeployedAt { get; set; } // 注册（部署）时间
}

// ---------------- 运行时游戏条目 ----------------
public class GameEntry
{
    public long AppId;
    public string Folder;                // [appid] 文件夹（可能不存在）
    public bool FolderExists;
    public bool SourceLuaExists;
    public string DeployedLuaPath;       // stplug-in\<appid>.lua（未部署为 null）
    public List<long> Depots = new();    // lua 中所有 addappid 目标（含 appid 自身）
    public List<string> LocalManifests = new();
    public string CustomName;
    public string FetchedName;
    public DateTime? RegTime;   // 注册（部署）时间

    public bool Deployed => !string.IsNullOrEmpty(DeployedLuaPath);
    /// <summary>文件夹存在但没有生成解锁 Lua —— 无法注册（拉取失败或游戏不在清单仓库）。</summary>
    public bool Empty => FolderExists && !SourceLuaExists;
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomName) ? CustomName :
        !string.IsNullOrWhiteSpace(FetchedName) ? FetchedName :
        "（未命名 " + AppId + "）";
}

// ---------------- 原生 API ----------------
internal static class Native
{
    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] src, int srcLen, [Out] char[] dst, int dstLen);

    /// <summary>CP936 (GBK) 解码 —— 不依赖 System.Text.Encoding.CodePages。</summary>
    public static string decode_gbk(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";
        int n = MultiByteToWideChar(936, 0, bytes, bytes.Length, null, 0);
        if (n <= 0) return Encoding.Latin1.GetString(bytes);
        var dst = new char[n];
        MultiByteToWideChar(936, 0, bytes, bytes.Length, dst, n);
        return new string(dst);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOERRORUI = 0x400;

    /// <summary>删除到回收站（可撤销）。</summary>
    public static bool RecycleDelete(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0",
            fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI),
        };
        return SHFileOperation(ref op) == 0;
    }
}

// ---------------- 极简 SQLite（winsqlite3.dll，Windows 自带） ----------------
internal static class MiniSql
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x1;
    private const int SQLITE_OPEN_READWRITE = 0x2;

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport("winsqlite3.dll")]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_changes(IntPtr db);

    [DllImport("winsqlite3.dll")]
    private static extern long sqlite3_column_int64(IntPtr stmt, int col);

    private static byte[] Utf8z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private static string Err(IntPtr db) => Marshal.PtrToStringAnsi(sqlite3_errmsg(db)) ?? "?";

    /// <summary>读取 appids.db 全部 appid；失败返回 null。</summary>
    public static HashSet<long> ListAppIds(string dbPath)
    {
        var result = new HashSet<long>();
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8z(dbPath), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK) return null;
            sqlite3_busy_timeout(db, 3000);
            if (sqlite3_prepare_v2(db, Utf8z("SELECT appid FROM Appinfo;"), -1, out stmt, IntPtr.Zero) != SQLITE_OK)
                return null; // 无表 / 损坏
            while (sqlite3_step(stmt) == SQLITE_ROW) result.Add(sqlite3_column_int64(stmt, 0));
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }

    /// <summary>删除指定 appid 行，返回删除行数；失败返回 -1。</summary>
    public static int DeleteAppIds(string dbPath, IEnumerable<long> ids)
    {
        int removed = 0;
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8z(dbPath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
                return -1;
            sqlite3_busy_timeout(db, 3000);
            byte[] begin = Utf8z("BEGIN;"), commit = Utf8z("COMMIT;");
            sqlite3_prepare_v2(db, begin, -1, out stmt, IntPtr.Zero);
            sqlite3_step(stmt); sqlite3_finalize(stmt); stmt = IntPtr.Zero;

            byte[] sql = Utf8z("DELETE FROM Appinfo WHERE appid = ?;");
            foreach (long id in ids)
            {
                if (sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != SQLITE_OK)
                { stmt = IntPtr.Zero; continue; }
                sqlite3_bind_int64(stmt, 1, id);
                sqlite3_step(stmt);
                removed += sqlite3_changes(db);
                sqlite3_finalize(stmt); stmt = IntPtr.Zero;
            }

            sqlite3_prepare_v2(db, commit, -1, out stmt, IntPtr.Zero);
            sqlite3_step(stmt); sqlite3_finalize(stmt); stmt = IntPtr.Zero;
            return removed;
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }

    /// <summary>把 appid 登记进 SteamTools appids.db（type=1，INSERT OR IGNORE），返回新插入行数；失败 -1。</summary>
    public static int RegisterAppIds(string dbPath, IEnumerable<long> ids)
    {
        int inserted = 0;
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8z(dbPath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
                return -1;
            sqlite3_busy_timeout(db, 3000);
            byte[] begin = Utf8z("BEGIN;"), commit = Utf8z("COMMIT;");
            sqlite3_prepare_v2(db, begin, -1, out stmt, IntPtr.Zero);
            sqlite3_step(stmt); sqlite3_finalize(stmt); stmt = IntPtr.Zero;

            byte[] sql = Utf8z("INSERT OR IGNORE INTO Appinfo (appid, type) VALUES (?, 1);");
            foreach (long id in ids)
            {
                if (sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != SQLITE_OK)
                { stmt = IntPtr.Zero; continue; }
                sqlite3_bind_int64(stmt, 1, id);
                sqlite3_step(stmt);
                inserted += sqlite3_changes(db);
                sqlite3_finalize(stmt); stmt = IntPtr.Zero;
            }

            sqlite3_prepare_v2(db, commit, -1, out stmt, IntPtr.Zero);
            sqlite3_step(stmt); sqlite3_finalize(stmt); stmt = IntPtr.Zero;
            return inserted;
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }
}

// ---------------- 数据与操作中枢 ----------------
public class Store
{
    public Settings S = new();
    public Dictionary<long, GameRecord> Records = new();

    private static readonly Regex RxFolder = new(@"^\[(\d+)\]"); // 允许 [appid]游戏名 形式的后缀
    private static readonly Regex RxLua = new(@"^(\d+)\.lua$", RegexOptions.IgnoreCase);
    private static readonly Regex RxManifest = new(@"^(\d+)_.+\.manifest$", RegexOptions.IgnoreCase);
    private static readonly Regex RxAddAppId = new(@"addappid\s*\(\s*(\d+)", RegexOptions.IgnoreCase);

    private string detectedSteam;

    public static string AppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamerToolManager");
    private static string SettingsPath => Path.Combine(AppDir, "settings.json");
    private static string GamesPath => Path.Combine(AppDir, "games.json");

    /// <summary>GamerTool 根目录下现有 [appid] 文件夹集合。</summary>
    public static HashSet<long> FolderIds(string root)
    {
        var set = new HashSet<long>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                var m = RxFolder.Match(Path.GetFileName(d));
                if (m.Success) set.Add(long.Parse(m.Groups[1].Value));
            }
        }
        catch { }
        return set;
    }

    // ---------- 加载 / 保存 ----------
    public void Load()
    {
        try { Directory.CreateDirectory(AppDir); } catch { }
        try
        {
            if (File.Exists(SettingsPath))
                S = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch { }
        try
        {
            if (File.Exists(GamesPath))
            {
                var list = JsonSerializer.Deserialize<List<GameRecord>>(File.ReadAllText(GamesPath)) ?? new();
                Records = list.Where(r => r != null).ToDictionary(r => r.AppId);
            }
        }
        catch { Records = new Dictionary<long, GameRecord>(); }
    }

    public void SaveSettings()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public void SaveRecords()
    {
        var list = Records.Values.OrderBy(r => r.AppId).ToList();
        try { File.WriteAllText(GamesPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    // ---------- 路径解析 ----------
    public string SteamDirEff
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(S.SteamDir) && Directory.Exists(S.SteamDir)) return S.SteamDir;
            if (detectedSteam == null)
            {
                detectedSteam = "";
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        var v = k?.GetValue("SteamPath") as string;
                        if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v)) detectedSteam = Path.GetFullPath(v);
                    }
                    if (detectedSteam == "")
                    {
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                        {
                            var v = k?.GetValue("InstallPath") as string;
                            if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v)) detectedSteam = Path.GetFullPath(v);
                        }
                    }
                }
                catch { }
            }
            return detectedSteam;
        }
    }

    public string StPluginEff =>
        !string.IsNullOrWhiteSpace(S.StPluginDir) ? S.StPluginDir :
        SteamDirEff == "" ? "" : Path.Combine(SteamDirEff, "config", "stplug-in");

    public string DepotCacheEff
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(S.DepotCacheDir)) return S.DepotCacheDir;
            if (SteamDirEff == "") return "";
            string cfg = Path.Combine(SteamDirEff, "config", "depotcache");
            string root = Path.Combine(SteamDirEff, "depotcache");
            if (Directory.Exists(cfg)) return cfg;
            if (Directory.Exists(root)) return root;
            return cfg;
        }
    }

    public string AppIdsDbEff =>
        !string.IsNullOrWhiteSpace(S.AppIdsDb) && File.Exists(S.AppIdsDb) ? S.AppIdsDb :
        File.Exists(@"C:\Program Files\SteamTools\appids.db") ? @"C:\Program Files\SteamTools\appids.db" : "";

    // ---------- 扫描 ----------
    public List<GameEntry> Scan()
    {
        var map = new Dictionary<long, GameEntry>();

        GameEntry Get(long id)
        {
            if (!map.TryGetValue(id, out var e))
            {
                e = new GameEntry { AppId = id };
                map[id] = e;
            }
            return e;
        }

        // 1) GamerTool 目录下的 [appid] 文件夹
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(S.GamerToolDir))
            {
                var m = RxFolder.Match(Path.GetFileName(dir));
                if (!m.Success) continue;
                var e = Get(long.Parse(m.Groups[1].Value));
                e.Folder = dir;
                e.FolderExists = true;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    var ml = RxLua.Match(name);
                    if (ml.Success && long.Parse(ml.Groups[1].Value) == e.AppId)
                    {
                        e.SourceLuaExists = true;
                        AddDepots(e, f);
                    }
                    else if (RxManifest.IsMatch(name))
                    {
                        e.LocalManifests.Add(f);
                        var mm = RxManifest.Match(name);
                        AddDepot(e, long.Parse(mm.Groups[1].Value));
                    }
                }
            }
        }
        catch { }

        // 2) stplug-in 中已部署的 <appid>.lua
        try
        {
            foreach (var f in Directory.EnumerateFiles(StPluginEff, "*.lua"))
            {
                var m = RxLua.Match(Path.GetFileName(f));
                if (!m.Success) continue; // Steamtools.lua 等运行组件，跳过
                var e = Get(long.Parse(m.Groups[1].Value));
                e.DeployedLuaPath = f;
                AddDepots(e, f);
            }
        }
        catch { }

        // 3) 合并备注名 / 注册时间（无记录的已部署游戏用 Lua 文件时间回填）
        bool dirty = false;
        foreach (var e in map.Values)
        {
            if (!Records.TryGetValue(e.AppId, out var r))
            {
                r = new GameRecord { AppId = e.AppId };
                Records[e.AppId] = r;
                dirty = true;
            }
            e.CustomName = r.Name;
            e.FetchedName = r.FetchedName;
            if (e.Deployed)
            {
                if (r.DeployedAt == null)
                {
                    try { r.DeployedAt = File.GetLastWriteTime(e.DeployedLuaPath); dirty = true; } catch { }
                }
                e.RegTime = r.DeployedAt;
            }
        }
        if (dirty) SaveRecords();

        return map.Values
            .OrderByDescending(e => e.Deployed)
            .ThenBy(e => e.AppId)
            .ToList();
    }

    private static void AddDepots(GameEntry e, string luaPath)
    {
        try
        {
            foreach (Match m in RxAddAppId.Matches(File.ReadAllText(luaPath)))
                AddDepot(e, long.Parse(m.Groups[1].Value));
        }
        catch { }
    }

    private static void AddDepot(GameEntry e, long id)
    {
        if (!e.Depots.Contains(id)) e.Depots.Add(id);
    }

    /// <summary>该游戏在 depotcache 中占用的清单文件。</summary>
    public List<string> DepotCacheFiles(GameEntry e)
    {
        var list = new List<string>();
        try
        {
            foreach (long d in e.Depots)
                list.AddRange(Directory.GetFiles(DepotCacheEff, d + "_*.manifest"));
        }
        catch { }
        return list;
    }

    /// <summary>该游戏在 appids.db 中登记的行。</summary>
    public List<long> RegisteredDbIds(GameEntry e)
    {
        var ids = MiniSql.ListAppIds(AppIdsDbEff);
        if (ids == null) return new List<long>();
        return e.Depots.Where(ids.Contains).ToList();
    }

    // ---------- 部署 / 还原 ----------
    public class DeployResult
    {
        public int Files;      // 复制的 lua + 清单数
        public int DbRows;     // 新登记进 appids.db 的行
        public int DbErrors;
    }

    /// <summary>部署 = 复制 Lua 到 stplug-in + 清单到 depotcache + addappid 全部 id 登记 appids.db（加入 SteamTools 管理）。</summary>
    public DeployResult Deploy(GameEntry e)
    {
        if (!e.SourceLuaExists || string.IsNullOrEmpty(e.Folder))
            throw new InvalidOperationException("游戏文件夹中没有生成解锁 Lua，请先拉取。");
        string st = StPluginEff;
        if (string.IsNullOrEmpty(st)) throw new InvalidOperationException("未找到 Steam 目录。");
        Directory.CreateDirectory(st);
        Directory.CreateDirectory(DepotCacheEff);

        File.Copy(Path.Combine(e.Folder, e.AppId + ".lua"), Path.Combine(st, e.AppId + ".lua"), true);
        foreach (var m in e.LocalManifests)
            File.Copy(m, Path.Combine(DepotCacheEff, Path.GetFileName(m)), true);

        // 记录注册时间
        if (!Records.TryGetValue(e.AppId, out var rec))
        {
            rec = new GameRecord { AppId = e.AppId };
            Records[e.AppId] = rec;
        }
        rec.DeployedAt = DateTime.Now;
        SaveRecords();

        var r = new DeployResult { Files = 1 + e.LocalManifests.Count };
        if (AppIdsDbEff != "")
        {
            int n = MiniSql.RegisterAppIds(AppIdsDbEff, e.Depots);
            if (n >= 0) r.DbRows = n; else r.DbErrors++;
        }
        return r;
    }

    public class RestoreResult
    {
        public int LuaDeleted;
        public int ManifestsDeleted;
        public int DbRowsDeleted;
        public int DbErrors;
    }

    public RestoreResult Restore(GameEntry e, bool cleanDepot, bool cleanDb)
    {
        var r = new RestoreResult();

        // 1) stplug-in 里的解锁 lua
        string lua = e.DeployedLuaPath ?? Path.Combine(StPluginEff, e.AppId + ".lua");
        if (File.Exists(lua))
        {
            File.Delete(lua);
            r.LuaDeleted++;
        }

        // 2) depotcache 清单
        if (cleanDepot)
        {
            foreach (var f in DepotCacheFiles(e))
            {
                try { File.Delete(f); r.ManifestsDeleted++; } catch { }
            }
        }

        // 3) SteamTools appids.db 登记
        if (cleanDb && AppIdsDbEff != "")
        {
            var ids = RegisteredDbIds(e);
            if (ids.Count > 0)
            {
                int n = MiniSql.DeleteAppIds(AppIdsDbEff, ids);
                if (n >= 0) r.DbRowsDeleted = n; else r.DbErrors++;
            }
        }
        return r;
    }

    /// <summary>appids.db 中存在、但没有任何 stplug-in lua 覆盖的登记项（孤儿）。</summary>
    public List<long> OrphanDbIds()
    {
        var ids = MiniSql.ListAppIds(AppIdsDbEff);
        if (ids == null) return new List<long>();
        var covered = new HashSet<long>();
        foreach (var e in Scan())
            foreach (long d in e.Depots) covered.Add(d);
        return ids.Where(i => !covered.Contains(i)).OrderBy(i => i).ToList();
    }

    // ---------- 原生拉取引擎（不再依赖交互式控制台工具） ----------
    /// <summary>manifest 仓库按 “一个 AppID 一个分支” 组织；jsdelivr 系镜像支持 repo@sha 路径。端点与格式均实测。</summary>
    public static class NativePuller
    {
        static readonly string[] Repos =
        {
            "Auiowu/ManifestAutoUpdate",
            "steamtools-games/ManifestHub3",
            "steamtoolsapp/ManifestHub",
            "tymolu233/ManifestAutoUpdate",
            "SteamAutoCracks/ManifestHub",
            "ikun0014/ManifestHub",
        };
        static readonly string[] Mirrors =
        {
            "https://cdn.jsdelivr.net/gh",
            "https://fastly.jsdelivr.net/gh",
            "https://gcore.jsdelivr.net/gh",
            "https://cdn.jsdmirror.com/gh",
        };

        static readonly HttpClient Http = CreateClient(null);
        static HttpClient httpProxy;

        static HttpClient CreateClient(string proxy)
        {
            var h = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (proxy != null) { h.Proxy = new WebProxy(proxy); h.UseProxy = true; }
            var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(proxy == null ? 25 : 35) };
            // GitHub API 要求 User-Agent，缺失会 403
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GamerToolManager/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, application/json");
            return c;
        }

        static HttpClient Px(string proxy) => httpProxy ??= CreateClient(proxy);

        /// <summary>直连优先，失败走代理。</summary>
        static async Task<string> GetStringSmart(string url, string proxy)
        {
            try { return await Http.GetStringAsync(url); } catch { }
            if (!string.IsNullOrWhiteSpace(proxy))
                return await Px(proxy).GetStringAsync(url);
            throw new HttpRequestException("无法访问: " + url);
        }

        static async Task<(byte[] data, string via)> GetFromMirror(string repo, string sha, string path, string proxy)
        {
            foreach (var m in Mirrors)
            {
                try
                {
                    var b = await Http.GetByteArrayAsync($"{m}/{repo}@{sha}/{path}");
                    if (b != null && b.Length > 0) return (b, new Uri(m).Host);
                }
                catch { }
            }
            // 终极兜底：raw.githubusercontent 走代理
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                var b = await Px(proxy).GetByteArrayAsync($"https://raw.githubusercontent.com/{repo}/{sha}/{path}");
                return (b, "raw.gh(proxy)");
            }
            throw new HttpRequestException("所有镜像均失败: " + path);
        }

        /// <summary>带文件锁重试的写入：Steam 运行时可能占用 manifest，写入失败自动等待重试（等价于原 nolock 逻辑）。</summary>
        internal static async Task WriteWithRetryAsync(string path, byte[] data, Action<string> log = null, int tries = 4)
        {
            for (int i = 1; ; i++)
            {
                try
                {
                    await File.WriteAllBytesAsync(path, data);
                    return;
                }
                catch (IOException) when (i < tries)
                {
                    log?.Invoke($"⚠ {Path.GetFileName(path)} 被占用（Steam 可能正在使用），重试 {i}/{tries - 1}…");
                    await Task.Delay(500 * i);
                }
            }
        }

        public class Result
        {
            public string Dir;
            public string LuaPath;
            public int Files;
            public List<long> Depots = new();
            public string Repo;
            public int FailedFiles;
        }

        /// <summary>解析 Key.vdf 的 depot → DecryptionKey。</summary>
        public static List<(long depot, string key)> ParseKeyVdf(string vdfText)
        {
            var list = new List<(long, string)>();
            if (string.IsNullOrEmpty(vdfText)) return list;
            var rx = new Regex(@"""(\d+)""\s*\{[^{}]*?""DecryptionKey""\s+""([0-9a-fA-F]{16,128})""", RegexOptions.Singleline);
            foreach (Match m in rx.Matches(vdfText))
                list.Add((long.Parse(m.Groups[1].Value), m.Groups[2].Value));
            return list;
        }

        public static async Task<Result> PullAsync(long appid, string gameName, string rootDir, string proxy,
            Action<string, LogKind> log, CancellationToken ct, string existingDir = null, bool force = false)
        {
            string dirName = "[" + appid + "]" + (gameName ?? "");
            foreach (char c in Path.GetInvalidFileNameChars()) dirName = dirName.Replace(c, '_');
            string dir = existingDir ?? Path.Combine(rootDir, dirName);

            foreach (var repo in Repos)
            {
                ct.ThrowIfCancellationRequested();
                log($"搜索仓库 {repo}（分支 {appid}）…", LogKind.Tool);

                // 文件发现：优先 data.jsdelivr（绕开 GitHub API 匿名限流），失败回退 api.github（带 UA）
                string fileRef = appid.ToString();
                string via;
                List<string> paths;
                try
                {
                    var jsd = await GetStringSmart($"https://data.jsdelivr.com/v1/packages/gh/{repo}@{appid}", proxy);
                    using var doc = JsonDocument.Parse(jsd);
                    paths = doc.RootElement.GetProperty("files").EnumerateArray()
                        .Where(f => f.TryGetProperty("type", out var ty) && ty.GetString() == "file")
                        .Select(f => f.GetProperty("name").GetString())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    via = "data.jsdelivr";
                }
                catch
                {
                    try
                    {
                        var brJson = await GetStringSmart($"https://api.github.com/repos/{repo}/branches/{appid}", proxy);
                        string sha = null, treeUrl = null;
                        using (var doc = JsonDocument.Parse(brJson))
                        {
                            var commit = doc.RootElement.GetProperty("commit");
                            sha = commit.GetProperty("sha").GetString();
                            treeUrl = commit.GetProperty("commit").GetProperty("tree").GetProperty("url").GetString();
                        }
                        if (sha == null || treeUrl == null)
                        {
                            log($"该仓库没有 {appid} 分支，换下一个。", LogKind.Warn);
                            continue;
                        }
                        var treeJson = await GetStringSmart(treeUrl, proxy);
                        using var tdoc = JsonDocument.Parse(treeJson);
                        paths = tdoc.RootElement.GetProperty("tree").EnumerateArray()
                            .Where(it => it.TryGetProperty("type", out var ty) && ty.GetString() == "blob")
                            .Select(it => it.GetProperty("path").GetString())
                            .Where(p => !string.IsNullOrEmpty(p))
                            .ToList();
                        fileRef = sha;
                        via = "api.github";
                    }
                    catch (Exception ex)
                    {
                        log($"⚠ 访问 {repo} 失败: {ex.Message}", LogKind.Warn);
                        continue;
                    }
                }
                log("文件列表 (" + via + "): " + paths.Count + " 项", LogKind.Tool);

                var vdfPath = paths.FirstOrDefault(p =>
                    p.Equals("Key.vdf", StringComparison.OrdinalIgnoreCase) || p.Equals("config.vdf", StringComparison.OrdinalIgnoreCase));
                var manifests = paths.Where(p => p.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)).ToList();
                if (vdfPath == null && manifests.Count == 0)
                {
                    log("分支为空，换下一个仓库。", LogKind.Warn);
                    continue;
                }

                Directory.CreateDirectory(dir);
                var result = new Result { Dir = dir, Repo = repo };
                string vdfText = null;

                // Key.vdf（存在即跳过）
                string vdfLocal = Path.Combine(dir, "Key.vdf");
                if (vdfPath != null)
                {
                    if (File.Exists(vdfLocal) && !force)
                    {
                        vdfText = File.ReadAllText(vdfLocal);
                        log("✓ Key.vdf 已存在，跳过下载", LogKind.Tool);
                        result.Files++;
                    }
                    else
                    {
                        var (data, mvia) = await GetFromMirror(repo, fileRef, vdfPath, proxy);
                        await WriteWithRetryAsync(vdfLocal, data, t => log(t, LogKind.Tool));
                        vdfText = System.Text.Encoding.UTF8.GetString(data);
                        log($"✓ Key.vdf ({data.Length}B) ← {mvia}", LogKind.Ok);
                        result.Files++;
                    }
                }

                // 清单并行下载
                int done = 0, failed = 0;
                var tasks = manifests.Select(async mp =>
                {
                    string local = Path.Combine(dir, mp);
                    if (File.Exists(local) && !force)
                    {
                        log($"✓ {mp} 已存在，跳过", LogKind.Tool);
                        Interlocked.Increment(ref done);
                        return;
                    }
                    try
                    {
                        var (data, mvia) = await GetFromMirror(repo, fileRef, mp, proxy);
                        await WriteWithRetryAsync(local, data, t => log(t, LogKind.Tool));
                        log($"✓ {mp} ({data.Length / 1024}KB) ← {mvia}", LogKind.Ok);
                        Interlocked.Increment(ref done);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        log($"✕ {mp}: {ex.Message}", LogKind.Err);
                    }
                }).ToList();
                await Task.WhenAll(tasks);
                result.FailedFiles = failed;
                result.Files += done;

                var depots = ParseKeyVdf(vdfText);
                result.Depots = depots.Select(d => d.depot).ToList();

                // 生成解锁 Lua
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"addappid({appid})");
                foreach (var (depot, key) in depots)
                    sb.AppendLine($"addappid({depot},1,\"{key}\")");
                var rxMan = new Regex(@"^(\d+)_(\d+)\.manifest$", RegexOptions.IgnoreCase);
                foreach (var mp in manifests)
                {
                    var m = rxMan.Match(Path.GetFileName(mp));
                    if (m.Success)
                        sb.AppendLine($"setManifestid({m.Groups[1].Value},\"{m.Groups[2].Value}\",0)");
                }
                string luaPath = Path.Combine(dir, appid + ".lua");
                await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);
                result.LuaPath = luaPath;
                result.Files++;
                return result;
            }
            return null;
        }

        /// <summary>Steam 官方 suggest 接口搜索游戏（直连→代理），返回 (appid, 名称) 列表。</summary>
        public static async Task<List<(long id, string name)>> SearchGamesAsync(string term, string proxy)
        {
            string url = "https://store.steampowered.com/search/suggest?term=" + Uri.EscapeDataString(term) +
                         "&f=games&cc=CN&l=schinese&use_store_query=1&use_search_spellcheck=1&search_creators_and_tags=1";
            string html = null;
            try { html = await Http.GetStringAsync(url); } catch { }
            if (string.IsNullOrEmpty(html) && !string.IsNullOrWhiteSpace(proxy))
                html = await Px(proxy).GetStringAsync(url);
            var list = new List<(long, string)>();
            if (string.IsNullOrEmpty(html)) return list;
            var rx = new Regex(@"data-ds-appid=""(\d+)""[\s\S]*?match_name"">\s*([^<]*)", RegexOptions.Compiled);
            var seen = new HashSet<long>();
            foreach (Match m in rx.Matches(html))
            {
                long id = long.Parse(m.Groups[1].Value);
                if (seen.Add(id))
                {
                    string n = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                    list.Add((id, n));
                }
            }
            return list;
        }
    }

    // ---------- 拖拽链接 / 路径 → AppID ----------
    private static readonly Regex RxSteamApp = new(@"steam(?:powered|community|tools)?\.(?:com|com\.cn|net)/(?:app|apps)/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex RxSteamUri = new(@"steam://(?:store|run|advertise)/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex RxBareId = new(@"^\s*(\d{4,8})\s*$");

    /// <summary>从拖入文本（Steam 商店链接 / steam:// / 纯数字）解析 AppID。</summary>
    public static long? ExtractAppId(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = RxSteamApp.Match(s);
        if (!m.Success) m = RxSteamUri.Match(s);
        if (m.Success) return long.Parse(m.Groups[1].Value);
        m = RxBareId.Match(s.Trim());
        if (m.Success) return long.Parse(m.Groups[1].Value);
        return null;
    }

    /// <summary>从拖入的文件/文件夹路径解析 AppID（[appid] 文件夹、&lt;appid&gt;.lua、&lt;depot&gt;_*.manifest）。</summary>
    public static long? ExtractAppIdFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var m = RxFolder.Match(name);
        if (m.Success) return long.Parse(m.Groups[1].Value);
        m = RxLua.Match(name);
        if (m.Success) return long.Parse(m.Groups[1].Value);
        m = RxManifest.Match(name);
        if (m.Success) return long.Parse(m.Groups[1].Value);
        return null;
    }

    // ---------- SteamTools 检测 / 内置安装包 ----------
    public static string SteamToolsExe
    {
        get
        {
            string pf = @"C:\Program Files\SteamTools\Steamtools.exe";
            if (File.Exists(pf)) return pf;
            string lad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Programs", "SteamTools", "Steamtools.exe");
            if (File.Exists(lad)) return lad;
            return "";
        }
    }

    public static bool SteamToolsInstalled => SteamToolsExe != "";

    /// <summary>随安装包附带的 SteamTools 安装程序（{app}\bundled\st-setup-*.exe）。</summary>
    public static string BundledSteamToolsSetup()
    {
        try
        {
            var d = Path.Combine(AppContext.BaseDirectory, "bundled");
            if (Directory.Exists(d))
                return Directory.GetFiles(d, "st-setup-*.exe")
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        catch { }
        return null;
    }

    // ---------- Steam 进程 ----------
    public static bool SteamRunning => Process.GetProcessesByName("steam").Any();

    public static async Task<bool> ShutdownSteamAsync(string steamDir)
    {
        string exe = Path.Combine(steamDir, "steam.exe");
        if (!File.Exists(exe)) return !SteamRunning;
        try { Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false }); } catch { }
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            if (!SteamRunning) break;
        }
        // 等所有子进程(steamwebhelper 等)真正退出，避免“半退出”状态下重启导致解锁不生效
        bool fullyExited = false;
        for (int i = 0; i < 20; i++)
        {
            if (!Process.GetProcessesByName("steam").Any() &&
                !Process.GetProcessesByName("steamwebhelper").Any())
            {
                fullyExited = true;
                break;
            }
            await Task.Delay(500);
        }
        if (fullyExited) await Task.Delay(1500); // 缓冲：释放文件句柄与单实例状态
        return fullyExited;
    }

    public static void KillSteam()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("steam"))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch { }
    }

    public static void StartSteam(string steamDir)
    {
        string exe = Path.Combine(steamDir, "steam.exe");
        if (File.Exists(exe))
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); } catch { }
    }

    /// <summary>
    /// 清理 Steam 授权查询缓存（appcache/packageinfo、contracts）。
    /// Steam 会缓存"无授权"的查询结果，导致重启后轮流破解失败；清掉强制下次启动重新查询，SteamTools 必然应答。
    /// 文件会被 Steam 自动重建，删除安全。
    /// </summary>
    public static int CleanSteamLicenseCache(string steamDir)
    {
        int removed = 0;
        try
        {
            string ac = Path.Combine(steamDir, "appcache");
            if (!Directory.Exists(ac)) return 0;
            foreach (var f in Directory.GetFiles(ac, "*.vdf"))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n.StartsWith("packageinfo") || n.StartsWith("contracts"))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
            }
        }
        catch { }
        return removed;
    }

    // ---------- SteamTools 进程 ----------
    public static bool SteamToolsRunning => Process.GetProcessesByName("Steamtools").Any();

    /// <summary>温和退出 SteamTools（关窗口→强杀兜底），确保下次以干净状态先于 Steam 启动。</summary>
    public static async Task KillSteamToolsAsync()
    {
        try { foreach (var p in Process.GetProcessesByName("Steamtools")) try { p.CloseMainWindow(); } catch { } } catch { }
        await Task.Delay(1500);
        try { foreach (var p in Process.GetProcessesByName("Steamtools")) try { p.Kill(); } catch { } } catch { }
        await Task.Delay(500);
    }

    public static void StartSteamTools(string exe)
    {
        if (File.Exists(exe))
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { }
    }

    // ---------- 游戏名抓取 ----------
    private static readonly HttpClient HcDirect = new(new HttpClientHandler
    { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(6) };

    private static HttpClient hcProxy;

    private static HttpClient ProxyClient(string proxy)
    {
        if (hcProxy == null)
            hcProxy = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy(proxy),
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All,
            }) { Timeout = TimeSpan.FromSeconds(8) };
        return hcProxy;
    }

    public static async Task<string> FetchGameNameAsync(long appid, string proxy)
    {
        // 注意: filters=name 会返回 data:[] 空数组（Steam API 的坑），必须用 filters=basic
        string url = $"https://store.steampowered.com/api/appdetails?appids={appid}&filters=basic&l=schinese";
        string key = appid.ToString();
        // 先直连，再走代理
        foreach (bool useProxy in new[] { false, true })
        {
            if (useProxy && string.IsNullOrWhiteSpace(proxy)) break;
            try
            {
                var hc = useProxy ? ProxyClient(proxy) : HcDirect;
                using var resp = await hc.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(key, out var app) &&
                    app.ValueKind == JsonValueKind.Object &&
                    app.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                    app.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("name", out var nm))
                {
                    var name = nm.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch { }
        }
        return null;
    }

    // ---------- 运行 Manifest2Lua ----------
    /// <summary>运行交互式清单工具：stdin 喂入输入行 + 换行（喂过结尾“按任意键”），GBK 输出逐行回调。</summary>
    public static async Task<int> RunToolAsync(string exe, string workDir, string input,
        Dictionary<string, string> env, Action<string> onLine, CancellationToken ct)
    {
        try { Directory.CreateDirectory(workDir); } catch { }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + exe);

        _ = Task.Run(async () =>
        {
            try
            {
                await p.StandardInput.WriteAsync(input + "\n\n\n");
                await p.StandardInput.FlushAsync();
            }
            catch { }
        });

        var t1 = Pump(p.StandardOutput.BaseStream, onLine);
        var t2 = Pump(p.StandardError.BaseStream, onLine);

        try
        {
            while (!p.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
        }

        await Task.WhenAll(t1, t2).ConfigureAwait(false);
        return p.ExitCode;
    }

    private static async Task Pump(Stream s, Action<string> onLine)
    {
        var buf = new byte[8192];
        var acc = new List<byte>(256);
        while (true)
        {
            int n;
            try { n = await s.ReadAsync(buf.AsMemory(0, buf.Length)); } catch { break; }
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == 0x0A)
                {
                    byte[] line = acc.ToArray();
                    acc.Clear();
                    if (line.Length > 0) onLine(Native.decode_gbk(line));
                }
                else if (b != 0x0D)
                {
                    acc.Add(b);
                }
            }
        }
        if (acc.Count > 0)
        {
            byte[] line = acc.ToArray();
            onLine(Native.decode_gbk(line));
        }
    }
}
