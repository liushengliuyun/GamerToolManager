using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GamerToolManager;

// ---------------- 清单包（.gtm）：本地清单 ↔ 单个文件，导出即分享，导入即部署 ----------------
// 格式 = ZIP：根放 gtmpack.json 元数据（format/version/games），每个游戏一个 <AppID>/ 子目录，
// 内容与收集上传同构（*.manifest + Key.vdf/config.vdf + <AppID>.lua）。
// 导出时本地缺 <AppID>.lua 的，按 Key.vdf + manifest 用同一逻辑生成，保证导入方拿到即可部署。
internal static class ManifestPack
{
    internal const string Ext = ".gtm";
    internal const string FormatTag = "GamerTool-Manifest-Pack";
    internal const int FormatVersion = 1;
    internal const string MetaName = "gtmpack.json";

    /// <summary>清单包内的一个游戏（导出填充来源信息；导入预览填充包内信息）。</summary>
    public class PackGame
    {
        public long AppId;
        public string Name = "";
        public int FileCount;       // 包内该游戏的文件数
        public int ManifestCount;   // 其中 *.manifest 数
        public bool HasLua;         // 包内带 <AppID>.lua（导出生成的也算）
        public bool HasKeyVdf;
        public long TotalBytes;     // 解压后该游戏内容大小
    }

    public class PackInfo
    {
        public string PackPath;
        public string ExportedAt = "";
        public string Tool = "";
        public List<PackGame> Games = new();
        public long PackBytes;      // 包文件自身大小
        public long TotalBytes;     // 解压后内容总大小
    }

    public class ExportResult { public int Games; public int Files; public int SkippedEmpty; public long Bytes; }
    public class ImportResult { public int Imported; public int Skipped; public int Files; public long Bytes; }

    /// <summary>导出来源：一个游戏的清单文件夹。</summary>
    public class ExportSource { public long AppId; public string Name; public string Dir; }

    // ================= 导出 =================
    /// <summary>把若干清单文件夹打成一个 .gtm 清单包。文件夹里挑不出任何可导出文件时跳过并计数。</summary>
    public static ExportResult Export(IList<ExportSource> sources, string packPath, Action<string> log, CancellationToken ct)
    {
        var r = new ExportResult();
        using (var fs = new FileStream(packPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var games = new List<object>();
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                var files = SetFiles(src.Dir, src.AppId);
                if (files.Count == 0)
                {
                    r.SkippedEmpty++;
                    log?.Invoke($"⚠ 跳过 [{src.AppId}] {src.Name}: 文件夹里没有清单 / Key.vdf / Lua 可导出。");
                    continue;
                }

                string prefix = src.AppId + "/";
                string luaName = src.AppId + ".lua";
                var g = new PackGame { AppId = src.AppId, Name = (src.Name ?? "").Trim() };
                string vdfText = null;
                bool hasLua = false;

                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(f);
                    byte[] data = File.ReadAllBytes(f);
                    if (name.Equals("Key.vdf", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("config.vdf", StringComparison.OrdinalIgnoreCase))
                        vdfText = Encoding.UTF8.GetString(data);
                    WriteEntry(zip, prefix + name, data);
                    g.FileCount++;
                    g.TotalBytes += data.Length;
                    if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) g.ManifestCount++;
                    else if (name.Equals(luaName, StringComparison.OrdinalIgnoreCase)) hasLua = true;
                }

                // 缺 <AppID>.lua：按 Key.vdf + manifest 生成进包（与拉取/收集同一逻辑），导入方拿到即可部署
                if (!hasLua)
                {
                    var depots = Store.NativePuller.ParseKeyVdf(vdfText);
                    byte[] lua = Encoding.UTF8.GetBytes(
                        Store.NativePuller.BuildLuaText(src.AppId, depots, files.Where(f => Path.GetFileName(f).EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))));
                    WriteEntry(zip, prefix + luaName, lua);
                    g.FileCount++;
                    g.TotalBytes += lua.Length;
                }

                games.Add(new { appId = g.AppId, name = g.Name });
                r.Games++;
                r.Files += g.FileCount;
                r.Bytes += g.TotalBytes;
                log?.Invoke($"✓ [{src.AppId}] {g.Name}: {g.FileCount} 个文件（{FmtBytes(g.TotalBytes)}）入包。");
            }

            var meta = new
            {
                format = FormatTag,
                version = FormatVersion,
                tool = "GamerToolManager/" + typeof(ManifestPack).Assembly.GetName().Version.ToString(3),
                exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                games,
            };
            WriteEntry(zip, MetaName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta)));
        }
        return r;
    }

    /// <summary>可导出的文件集（与收集上传同构）：*.manifest + Key.vdf/config.vdf + &lt;AppID&gt;.lua。其余（.steam-extract 等）一律不带。</summary>
    static List<string> SetFiles(string dir, long appId)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                string n = Path.GetFileName(f);
                bool isManifest = n.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
                bool isVdf = n.Equals("Key.vdf", StringComparison.OrdinalIgnoreCase) ||
                             n.Equals("config.vdf", StringComparison.OrdinalIgnoreCase);
                bool isLua = n.Equals(appId + ".lua", StringComparison.OrdinalIgnoreCase);
                if (isManifest || isVdf || isLua) list.Add(f);
            }
        }
        catch { }
        return list;
    }

    // ================= 预览（不解压） =================
    /// <summary>读包不解压：从 zip 中央目录与元数据取游戏列表与大小，供导入预览。非 GamerTool 清单包时抛人话异常。</summary>
    public static PackInfo Inspect(string packPath)
    {
        var pi = new PackInfo { PackPath = packPath, PackBytes = new FileInfo(packPath).Length };
        using var zip = OpenPack(packPath);
        var (exportedAt, tool, games) = ReadMeta(zip);

        pi.ExportedAt = exportedAt ?? "";
        pi.Tool = tool ?? "";
        foreach (var (appId, name) in games)
        {
            var g = new PackGame { AppId = appId, Name = name };
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(appId + "/", StringComparison.OrdinalIgnoreCase)) continue;
                string n = Path.GetFileName(e.FullName);
                if (n.Length == 0) continue;
                g.FileCount++;
                g.TotalBytes += e.Length;
                if (n.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) g.ManifestCount++;
                else if (n.Equals(appId + ".lua", StringComparison.OrdinalIgnoreCase)) g.HasLua = true;
                else if (n.Equals("Key.vdf", StringComparison.OrdinalIgnoreCase) ||
                         n.Equals("config.vdf", StringComparison.OrdinalIgnoreCase)) g.HasKeyVdf = true;
            }
            pi.Games.Add(g);
            pi.TotalBytes += g.TotalBytes;
        }
        return pi;
    }

    // ================= 导入 =================
    /// <summary>
    /// 把清单包落盘为 GamerTool 目录下的 [AppID] 文件夹。
    /// 同 AppID 已有文件夹时：overwrite=false 跳过；true 写入原文件夹（只更新包内文件，不删本地多余文件）。
    /// </summary>
    public static ImportResult Import(string packPath, string rootDir, bool overwrite, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(rootDir);
        using var zip = OpenPack(packPath);
        var (_, _, games) = ReadMeta(zip);
        var r = new ImportResult();

        foreach (var (appId, name) in games)
        {
            ct.ThrowIfCancellationRequested();
            var entries = zip.Entries
                .Where(e => e.FullName.StartsWith(appId + "/", StringComparison.OrdinalIgnoreCase))
                .Where(e => Path.GetFileName(e.FullName).Length > 0)
                .ToList();
            if (entries.Count == 0)
            {
                log?.Invoke($"⚠ 包内 [{appId}] 没有文件，跳过。");
                continue;
            }

            string dest = FindExistingDir(rootDir, appId);
            bool exists = dest != null;
            if (exists && !overwrite)
            {
                r.Skipped++;
                log?.Invoke($"  [{appId}] {name}: 本地已有「{Path.GetFileName(dest)}」，跳过（预览里勾选「覆盖」可更新）。");
                continue;
            }
            if (!exists) dest = Path.Combine(rootDir, "[" + appId + "]" + SafeDirName(name));
            Directory.CreateDirectory(dest);

            int files = 0;
            long bytes = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                string fname = Path.GetFileName(e.FullName);
                if (fname.Contains('/'))
                {
                    log?.Invoke($"⚠ 跳过包内嵌套路径 {e.FullName}（清单集是平铺结构）。");
                    continue;
                }
                File.WriteAllBytes(Path.Combine(dest, fname), ReadEntry(e));
                files++;
                bytes += e.Length;
            }
            r.Imported++;
            r.Files += files;
            r.Bytes += bytes;
            log?.Invoke($"✓ [{appId}] {name}: {files} 个文件 → {Path.GetFileName(dest)}" + (exists ? "（覆盖更新，多余文件保留）" : ""));
        }
        return r;
    }

    /// <summary>root 下已有 [appId]* 文件夹时返回其路径（同 AppID 不建重复文件夹）。</summary>
    static string FindExistingDir(string root, long appId)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => Regex.IsMatch(Path.GetFileName(d), @"^\[" + appId + @"\]"));
        }
        catch { return null; }
    }

    // ---------- 内部 ----------
    /// <summary>打开清单包 zip；损坏/非 zip 一律转成人话异常（UI 直接展示给用户）。</summary>
    static ZipArchive OpenPack(string path)
    {
        FileStream fs = null;
        try
        {
            fs = File.OpenRead(path);
            return new ZipArchive(fs, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            fs?.Dispose();
            throw new InvalidOperationException("不是有效的 GamerTool 清单包（zip 结构损坏或文件不完整）。");
        }
        catch
        {
            fs?.Dispose();
            throw;
        }
    }

    /// <summary>校验并读取包元数据；非 GamerTool 清单包 / 版本过新 / 元数据损坏时抛人话异常。</summary>
    static (string exportedAt, string tool, List<(long appId, string name)> games) ReadMeta(ZipArchive zip)
    {
        var e = zip.Entries.FirstOrDefault(x => x.FullName.Equals(MetaName, StringComparison.OrdinalIgnoreCase));
        if (e == null)
            throw new InvalidOperationException("不是 GamerTool 清单包文件（缺少包元数据 gtmpack.json）。");

        JsonDocument doc;
        try
        {
            using var s = e.Open();
            doc = JsonDocument.Parse(s);
        }
        catch
        {
            throw new InvalidOperationException("清单包元数据损坏（gtmpack.json 无法解析），文件可能不完整或被改动。");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out var ft) || ft.GetString() != FormatTag)
                throw new InvalidOperationException("不是 GamerTool 清单包文件（格式标记不符）。");
            if (!root.TryGetProperty("version", out var ver) ||
                ver.ValueKind != JsonValueKind.Number || ver.GetInt32() > FormatVersion)
                throw new InvalidOperationException("清单包版本过新，请升级 GamerTool 管理器后再导入。");

            var games = new List<(long, string)>();
            if (root.TryGetProperty("games", out var gs) && gs.ValueKind == JsonValueKind.Array)
            {
                foreach (var gj in gs.EnumerateArray())
                {
                    if (gj.ValueKind != JsonValueKind.Object ||
                        !gj.TryGetProperty("appId", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                    string nm = gj.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
                    games.Add((idEl.GetInt64(), nm));
                }
            }
            return (root.TryGetProperty("exportedAt", out var ea) ? ea.GetString() : null,
                    root.TryGetProperty("tool", out var tl) ? tl.GetString() : null,
                    games);
        }
    }

    static void WriteEntry(ZipArchive zip, string entryName, byte[] data)
    {
        var e = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    static byte[] ReadEntry(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>包内游戏名 → 安全文件夹名（[AppID] 后缀部分）。</summary>
    static string SafeDirName(string name)
    {
        string n = (name ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Length > 60 ? n[..60] : n;
    }

    /// <summary>人话字节数：1.50 GB / 320.4 MB / 512 KB / 96 B。</summary>
    public static string FmtBytes(long b) =>
        b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("F2") + " GB" :
        b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("F1") + " MB" :
        b >= 1L << 10 ? (b / 1024.0).ToString("F0") + " KB" :
        b + " B";
}
