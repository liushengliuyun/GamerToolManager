using System.Text;

namespace GamerToolManager;

// ---------------- 自检：清单包（.gtm）导出/导入引擎往返（无网络、无窗口） ----------------
internal static class PackTest
{
    public static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "gtm_pack_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // 游戏A：完整（lua + Key.vdf + 2 个 manifest）
        string pa = Path.Combine(root, "[460900]SampleGame");
        Directory.CreateDirectory(pa);
        File.WriteAllText(Path.Combine(pa, "460900.lua"), "addappid(460900)");
        File.WriteAllText(Path.Combine(pa, "Key.vdf"),
            "\"460901\"\n{\n\t\"DecryptionKey\"\t\t\"" + new string('a', 64) + "\"\n}\n");
        File.WriteAllText(Path.Combine(pa, "123_456.manifest"), "bin1");
        File.WriteAllText(Path.Combine(pa, "710_abc.manifest"), "bin2");

        // 游戏B：缺 lua（导出时应按 Key.vdf + manifest 自动生成）
        string pb = Path.Combine(root, "[460901]NoLua");
        Directory.CreateDirectory(pb);
        File.WriteAllText(Path.Combine(pb, "Key.vdf"),
            "\"460902\"\n{\n\t\"DecryptionKey\"\t\t\"" + new string('b', 64) + "\"\n}\n");
        File.WriteAllText(Path.Combine(pb, "460902_2896417252875612038.manifest"), "bin3");

        // 空文件夹（导出时应跳过）
        Directory.CreateDirectory(Path.Combine(root, "[460902]Empty"));

        string packPath = Path.Combine(root, "testpack.gtm");
        var export = ManifestPack.Export(new List<ManifestPack.ExportSource>
        {
            new() { AppId = 460900, Name = "SampleGame", Dir = pa },
            new() { AppId = 460901, Name = "NoLua", Dir = pb },
            new() { AppId = 460902, Name = "Empty", Dir = Path.Combine(root, "[460902]Empty") },
        }, packPath, null, CancellationToken.None);
        check($"清单包导出: {export.Games} 个游戏 {export.Files} 个文件，空文件夹跳过 {export.SkippedEmpty}",
              export.Games == 2 && export.Files == 7 && export.SkippedEmpty == 1 && File.Exists(packPath));

        var info = ManifestPack.Inspect(packPath);
        var ga = info.Games.FirstOrDefault(x => x.AppId == 460900);
        var gb = info.Games.FirstOrDefault(x => x.AppId == 460901);
        check("清单包元数据: " + string.Join(", ", info.Games.Select(g => g.AppId + "(" + g.FileCount + " 文件)")),
              info.Games.Count == 2 &&
              ga != null && ga.FileCount == 4 && ga.ManifestCount == 2 && ga.HasLua && ga.HasKeyVdf &&
              gb != null && gb.FileCount == 3 && gb.ManifestCount == 1 && gb.HasLua);

        // 覆盖导入：先放一个旧文件夹（同 AppID 不同后缀），覆盖时必须写进它而不是另建
        string importRoot = Path.Combine(root, "import");
        Directory.CreateDirectory(importRoot);
        string oldDir = Path.Combine(importRoot, "[460900]Old");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "old.txt"), "keep");

        var imp1 = ManifestPack.Import(packPath, importRoot, overwrite: true, null, CancellationToken.None);
        check($"覆盖导入: {imp1.Imported} 个游戏写入 {imp1.Files} 个文件（旧文件夹多余文件保留）",
              imp1.Imported == 2 && imp1.Files == 7 &&
              File.Exists(Path.Combine(oldDir, "460900.lua")) &&
              File.Exists(Path.Combine(oldDir, "Key.vdf")) &&
              File.Exists(Path.Combine(oldDir, "old.txt")) &&
              File.ReadAllText(Path.Combine(oldDir, "123_456.manifest")) == "bin1");

        // 默认导入：已存在一律跳过
        var imp2 = ManifestPack.Import(packPath, importRoot, overwrite: false, null, CancellationToken.None);
        check("默认导入: 已存在全部跳过", imp2.Imported == 0 && imp2.Skipped == 2);

        // 缺 Lua 的游戏在导入侧拿到可部署的解锁 Lua
        string luaB = File.ReadAllText(Path.Combine(importRoot, "[460901]NoLua", "460901.lua"));
        check("缺 Lua 自动生成: addappid + 密钥 + setManifestid",
              luaB.StartsWith("addappid(460901)") &&
              luaB.Contains("addappid(460902,1,\"" + new string('b', 64) + "\")") &&
              luaB.Contains("setManifestid(460902,\"2896417252875612038\",0)"));

        // 非清单包文件拒绝并给出人话
        string bad = Path.Combine(root, "bad.gtm");
        File.WriteAllText(bad, "not a zip at all");
        bool rejected = false;
        try { ManifestPack.Inspect(bad); }
        catch (InvalidOperationException) { rejected = true; }
        check("非清单包文件被拒绝", rejected);

        try { Directory.Delete(root, true); } catch { }
    }
}
