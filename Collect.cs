using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GamerToolManager;

// ---------------- 清单收集引擎：扫描本地清单集 + 上传到 GitHub 收集仓库 ----------------
// 收集仓库按 “一个 AppID 一个分支” 组织（与公共清单仓库同构），走 Git Data API 提交：
// 只增不删、绝不 force，SHA 去重（未变化的文件不重新上传）。
internal static class Collector
{
    internal const string Owner = "liushengliuyun";
    internal const string Repo = "GamerToolManifests";
    internal const string RepoSlug = Owner + "/" + Repo;

    /// <summary>上传令牌来自 gitignored 的 Secrets.cs（仅发布构建存在）；空 = 上传禁用。</summary>
    internal static string UploadToken =>
#if HAS_UPLOAD_TOKEN
        Secrets.UploadToken;
#else
        ""; // 无 Secrets.cs（公开源码构建）：上传禁用，扫描不受影响
#endif

    /// <summary>识别出的一个清单集（一个 AppID 的本地文件夹或根目录散装文件）。</summary>
    public class CollectSet
    {
        public long AppId;
        public bool Recognized;         // 是否识别出 AppID（未识别不可上传）
        public string Dir;              // 所在目录（散装集合 = 扫描根目录）
        public string DisplayName;
        public string Source = "";      // "Steam 提取" = 内置提取器产出（含 .steam-extract 标记）
        public string Note = "";        // 附加说明（无法识别 / 重复 / 名称冲突）
        public List<string> Manifests = new();
        public string VdfLocal;         // Key.vdf / config.vdf 本地路径（可空）
        public bool HasLua;             // 本地已有 <appid>.lua
        public long TotalBytes;
    }

    public class UploadResult
    {
        public bool Uploaded;       // 实际产生了提交
        public bool Skipped;        // 远端已最新，未提交
        public string CommitSha;    // 短 SHA（跳过为 null）
        public int UploadedFiles;
    }

    // ---------- HTTP（上传专用：长超时 + Bearer；直连失败走代理） ----------
    static readonly HttpClient hcDirect = CreateClient(null);
    static HttpClient hcProxy;
    static string hcProxyKey;

    static HttpClient CreateClient(string proxy)
    {
        var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (proxy != null) { h.Proxy = new WebProxy(proxy); h.UseProxy = true; }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(300) }; // 大清单 blob POST 需要长超时
        // GitHub API 要求 User-Agent，缺失会 403（同 Core.cs NativePuller）
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GamerToolManager/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, application/json");
        if (!string.IsNullOrWhiteSpace(UploadToken))
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", UploadToken);
        return c;
    }

    static HttpClient Px(string proxy)
    {
        // 地址可能经 ProxyEff 漂移切换，按地址缓存
        if (hcProxy == null || hcProxyKey != proxy)
        {
            hcProxy = CreateClient(proxy);
            hcProxyKey = proxy;
        }
        return hcProxy;
    }

    static async Task<HttpResponseMessage> SendSmart(HttpRequestMessage msg, string proxy)
    {
        try { return await hcDirect.SendAsync(msg); }
        catch
        {
            if (string.IsNullOrWhiteSpace(proxy)) throw;
            // HttpRequestMessage 不能复用，工厂重建一份
            using var retry = await CloneRequest(msg);
            return await Px(proxy).SendAsync(retry);
        }
    }

    static async Task<HttpRequestMessage> CloneRequest(HttpRequestMessage src)
    {
        var copy = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version };
        if (src.Content != null)
        {
            var bytes = await src.Content.ReadAsByteArrayAsync();
            copy.Content = new ByteArrayContent(bytes);
            copy.Content.Headers.ContentType = src.Content.Headers.ContentType;
        }
        return copy;
    }

    // ---------- API 响应封装 ----------
    internal sealed class ApiException : Exception
    {
        public readonly int Status;
        public ApiException(int status, string message) : base(message) { Status = status; }
    }

    static async Task<JsonElement> SendJson(string method, string url, object body, string proxy)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        using var resp = await SendSmart(msg, proxy);
        string text = null;
        try { text = await resp.Content.ReadAsStringAsync(); } catch { }

        if ((int)resp.StatusCode >= 400)
        {
            // 限速优先判定，其次权限，最后透出 GitHub 的 message
            if ((int)resp.StatusCode == 403)
            {
                var remain = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rv) ? rv.FirstOrDefault() : null;
                if (remain == "0" || (text != null && text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(403, "已达 GitHub API 速率限制（5000 次/小时），请稍后再传。");
                if (text != null && (text.Contains("Resource not accessible", StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(403, $"令牌缺少 {Repo} 的 Contents 读写权限（检查 PAT 的仓库范围与权限）。");
            }
            string gm = "?";
            try
            {
                using var doc = JsonDocument.Parse(text ?? "{}");
                if (doc.RootElement.TryGetProperty("message", out var m)) gm = m.GetString() ?? "?";
            }
            catch { }
            throw new ApiException((int)resp.StatusCode, $"GitHub {(int)resp.StatusCode}: {gm}");
        }
        if (string.IsNullOrWhiteSpace(text)) return default;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ---------- 批前检 ----------
    /// <summary>收集仓库现有的 AppID 分支集合（分页拉全）。一键收集用它实现「仓库已有的跳过」。</summary>
    public static async Task<HashSet<long>> GetBranchesAsync(string proxy)
    {
        var set = new HashSet<long>();
        for (int page = 1; ; page++)
        {
            var arr = await SendJson("GET", $"https://api.github.com/repos/{RepoSlug}/branches?per_page=100&page={page}", null, proxy);
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;
            foreach (var b in arr.EnumerateArray())
                if (b.TryGetProperty("name", out var n) && long.TryParse(n.GetString(), out var id))
                    set.Add(id);
            if (arr.GetArrayLength() < 100) break;
        }
        return set;
    }

    /// <summary>分支最后一次提交时间（UTC）；分支不存在返回 null，网络/限流错误抛 ApiException。</summary>
    public static async Task<DateTime?> GetBranchTipDateAsync(long appId, string proxy)
    {
        try
        {
            var c = await SendJson("GET", $"https://api.github.com/repos/{RepoSlug}/commits/{appId}", null, proxy);
            string date = null;
            if (c.TryGetProperty("commit", out var cm) &&
                cm.TryGetProperty("committer", out var co) &&
                co.TryGetProperty("date", out var d))
                date = d.GetString();
            if (date != null && DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
                return t;
            return null;
        }
        catch (ApiException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>上传前一次性校验令牌与仓库可达；空仓库自动补初始化提交（GitHub 无法在空仓库上建 Git 树/孤儿分支，409）。</summary>
    public static async Task EnsureRepoAsync(string proxy)
    {
        try
        {
            await SendJson("GET", $"https://api.github.com/repos/{RepoSlug}", null, proxy);

            var branches = await SendJson("GET", $"https://api.github.com/repos/{RepoSlug}/branches?per_page=1", null, proxy);
            if (branches.ValueKind == JsonValueKind.Array && branches.GetArrayLength() == 0)
            {
                var readme = "# GamerToolManifests\n\nGamerTool 管理器的社区清单收集库：一个 AppID 一个分支，内含 manifest + Key.vdf + 元数据。\n由 GamerTool 管理器在上传时自动初始化。\n";
                await SendJson("PUT", $"https://api.github.com/repos/{RepoSlug}/contents/README.md",
                    new { message = "init: GamerTool 收集库初始化", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(readme)) }, proxy);
            }
        }
        catch (ApiException ex)
        {
            throw new ApiException(ex.Status, ex.Status switch
            {
                401 => "上传令牌无效或已被吊销（检查 Secrets.cs / 到 GitHub 重新生成 PAT）。",
                403 => ex.Message,
                404 => $"收集仓库 {RepoSlug} 不存在（先按 README 用 gh 创建），或令牌无权访问它。",
                _ => ex.Message,
            });
        }
    }

    // ---------- 扫描 ----------
    static readonly Regex RxFolder = new(@"^\[(\d+)\]", RegexOptions.IgnoreCase);
    static readonly Regex RxLua = new(@"^(\d+)\.lua$", RegexOptions.IgnoreCase);
    static readonly Regex RxJson = new(@"^(\d+)\.json$", RegexOptions.IgnoreCase);

    /// <summary>
    /// 扫描收集目录下的清单集：[AppID]xxx 子文件夹（可带游戏名后缀）、含 *.manifest / &lt;AppID&gt;.lua 的其他文件夹、
    /// 以及散落在根目录的文件（构成一个伪集合）。绝不从 manifest 文件名推断 AppID（那是 depotId）。
    /// </summary>
    public static List<CollectSet> ScanCollectionDir(string root, Store store = null)
    {
        var list = new List<CollectSet>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return list;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(dir);
                var m = RxFolder.Match(name);
                if (m.Success)
                {
                    AddSet(list, MakeSet(dir, long.Parse(m.Groups[1].Value), name[(m.Groups[1].Length + 2)..], store));
                    continue;
                }
                // 无 [appid] 前缀：像清单集才收（含 *.manifest 或 <appid>.lua/.json）
                var files = SafeFiles(dir);
                bool looksLikeSet = files.Any(f => f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                                 || files.Any(f => RxLua.IsMatch(Path.GetFileName(f)));
                if (!looksLikeSet) continue;
                long? id = InferAppId(files);
                var set = MakeSet(dir, id ?? 0, name, store);
                if (id == null)
                {
                    set.Recognized = false;
                    set.Note = "无法识别 AppID（需要 [AppID] 文件夹名或 <AppID>.lua）；重命名为 [AppID] 即可识别";
                }
                AddSet(list, set);
            }

            // 根目录散装文件构成一个伪集合
            var loose = SafeFiles(root).Where(f =>
                f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Equals("Key.vdf", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Equals("config.vdf", StringComparison.OrdinalIgnoreCase) ||
                RxLua.IsMatch(Path.GetFileName(f))).ToList();
            if (loose.Count > 0)
            {
                long? id = InferAppId(loose);
                var set = MakeSet(root, id ?? 0, "（根目录散装文件）", store);
                if (id == null)
                {
                    set.Recognized = false;
                    set.Note = "无法识别 AppID（需要 <AppID>.lua）";
                }
                AddSet(list, set);
            }
        }
        catch (Exception) { /* 目录不可读时返回已收集部分 */ }

        list.Sort((a, b) =>
        {
            if (a.AppId != b.AppId) return a.AppId.CompareTo(b.AppId);
            return string.CompareOrdinal(a.Dir, b.Dir);
        });
        return list;
    }

    static List<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir).ToList(); } catch { return new List<string>(); }
    }

    /// <summary>从文件名推断 AppID：&lt;appid&gt;.lua → &lt;appid&gt;.json。Key.vdf 不含 AppID，manifest 文件名是 depotId。</summary>
    static long? InferAppId(List<string> files)
    {
        foreach (var f in files)
        {
            var m = RxLua.Match(Path.GetFileName(f));
            if (m.Success) return long.Parse(m.Groups[1].Value);
        }
        foreach (var f in files)
        {
            var m = RxJson.Match(Path.GetFileName(f));
            if (m.Success) return long.Parse(m.Groups[1].Value);
        }
        return null;
    }

    internal static CollectSet MakeSet(string dir, long appId, string displayName, Store store = null)
    {
        var set = new CollectSet { Dir = dir, AppId = appId, Recognized = appId > 0, DisplayName = displayName };
        foreach (var f in SafeFiles(dir))
        {
            string name = Path.GetFileName(f);
            if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                set.Manifests.Add(f);
            else if (name.Equals("Key.vdf", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("config.vdf", StringComparison.OrdinalIgnoreCase))
                set.VdfLocal = f;
            else if (appId > 0 && name.Equals(appId + ".lua", StringComparison.OrdinalIgnoreCase))
                set.HasLua = true;
        }
        // 名称兜底：文件夹没带游戏名时用 games.json 的记录
        if (appId > 0 && string.IsNullOrWhiteSpace(set.DisplayName))
        {
            string n = null;
            if (store?.Records != null && store.Records.TryGetValue(appId, out var r))
                n = r.FetchedName ?? r.Name;
            if (!string.IsNullOrWhiteSpace(n)) set.DisplayName = n;
        }
        if (File.Exists(System.IO.Path.Combine(dir, ".steam-extract")))
            set.Source = "Steam 提取";
        set.TotalBytes = set.Manifests.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        if (set.VdfLocal != null) set.TotalBytes += new FileInfo(set.VdfLocal).Length;
        return set;
    }

    static void AddSet(List<CollectSet> list, CollectSet set)
    {
        // 同 AppID 重复：保留第一个，后者禁选
        if (set.Recognized)
        {
            var dup = list.FirstOrDefault(s => s.Recognized && s.AppId == set.AppId);
            if (dup != null)
            {
                set.Note = $"与 [ {set.AppId} ] 的另一目录重复：" + dup.Dir;
                set.Recognized = false;
            }
        }
        list.Add(set);
    }

    // ---------- 上传 ----------
    /// <summary>上传一个清单集到收集仓库的 <appid> 分支。冲突不覆盖，网络/服务端错误抛 ApiException。</summary>
    public static async Task<UploadResult> UploadSetAsync(Store store, CollectSet set, Action<string> log, CancellationToken ct)
    {
        var r = new UploadResult();
        string proxy = store.ProxyEff;
        long appid = set.AppId;

        // 1) 分支现状
        string tip = null, tipTree = null;
        try
        {
            var br = await SendJson("GET", $"https://api.github.com/repos/{RepoSlug}/branches/{appid}", null, proxy);
            tip = br.GetProperty("commit").GetProperty("sha").GetString();
            tipTree = br.GetProperty("commit").GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString();
            log($"分支 {appid} 已存在（{tip?[..8]}），只提交差异。");
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            log($"分支 {appid} 不存在，将新建。");
        }
        ct.ThrowIfCancellationRequested();

        // 2) 远端文件 sha 映射（跳过未变化文件用）
        var remote = new Dictionary<string, string>();
        if (tipTree != null)
        {
            var tree = await SendJson("GET", $"https://api.github.com/repos/{RepoSlug}/git/trees/{tipTree}", null, proxy);
            if (tree.TryGetProperty("tree", out var items))
                foreach (var it in items.EnumerateArray())
                {
                    if (it.TryGetProperty("type", out var ty) && ty.GetString() == "blob" &&
                        it.TryGetProperty("path", out var p) && it.TryGetProperty("sha", out var sh))
                        remote[p.GetString()] = sh.GetString();
                }
        }

        // 3) 组装上传清单（本地路径 → 远端路径）
        var uploads = new List<(string localPath, string remotePath)>();
        foreach (var f in set.Manifests) uploads.Add((f, Path.GetFileName(f)));
        if (set.VdfLocal != null)
            uploads.Add((set.VdfLocal, "Key.vdf")); // config.vdf 一律以 Key.vdf 入库

        // <appid>.json 元数据（远端唯一真源，始终生成）
        string name = null, schName = null;
        if (store.Records.TryGetValue(appid, out var rec))
        {
            name = rec.FetchedName ?? rec.Name;
            schName = rec.FetchedName;
        }
        name ??= (string.IsNullOrWhiteSpace(set.DisplayName) || set.DisplayName.StartsWith("（") ? "" : set.DisplayName);
        var metaJson = JsonSerializer.Serialize(new { appid, name = name ?? "", type = "Game", schinese_name = schName ?? name ?? "" });
        uploads.Add((null, appid + ".json"));
        string tempJsonPath = Path.Combine(Path.GetTempPath(), "gtm_upload_" + appid + ".json");
        await File.WriteAllTextAsync(tempJsonPath, metaJson, ct);

        // <appid>.lua：本地缺则按 Key.vdf + manifest 生成（与拉取同一逻辑）
        string luaLocal = Path.Combine(set.Dir, appid + ".lua");
        bool luaExists = File.Exists(luaLocal);
        if (!luaExists)
        {
            string vdfText = set.VdfLocal != null ? await File.ReadAllTextAsync(set.VdfLocal, ct) : null;
            var luaText = Store.NativePuller.BuildLuaText(appid, Store.NativePuller.ParseKeyVdf(vdfText), set.Manifests);
            await File.WriteAllTextAsync(luaLocal, luaText, ct);
            log("本地缺 <appid>.lua，已按 Key.vdf + 清单生成。");
        }
        uploads.Add((luaLocal, appid + ".lua"));

        // 4) 逐文件对比 blob SHA，只传有差异的
        int uploadedFiles = 0;
        var treeItems = new List<object>();
        foreach (var (localPath, remotePath) in uploads)
        {
            ct.ThrowIfCancellationRequested();
            string path = localPath ?? tempJsonPath;
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            string localSha = BlobSha(bytes);
            if (remote.TryGetValue(remotePath, out var rs) && rs == localSha) continue;

            log($"上传 {remotePath}（{bytes.Length / 1024.0:F0} KB）…");
            var blob = await SendJson("POST", $"https://api.github.com/repos/{RepoSlug}/git/blobs",
                new { content = Convert.ToBase64String(bytes), encoding = "base64" }, proxy);
            treeItems.Add(new
            {
                path = remotePath,
                mode = "100644",
                type = "blob",
                sha = blob.GetProperty("sha").GetString(),
            });
            uploadedFiles++;
        }
        try { File.Delete(tempJsonPath); } catch { }
        // lua 若是刚生成的，保留在本地（这样部署/收集都能直接用）

        if (treeItems.Count == 0)
        {
            r.Skipped = true;
            log("远端已是最新，跳过提交。");
            return r;
        }

        // 5) 树 → 提交 → 移动分支（force=false，绝不覆盖他人提交）
        object treeBody = tipTree == null
            ? (object)new { tree = treeItems }
            : new { base_tree = tipTree, tree = treeItems };
        var treeResp = await SendJson("POST", $"https://api.github.com/repos/{RepoSlug}/git/trees", treeBody, proxy);
        string newTree = treeResp.GetProperty("sha").GetString();

        string message = $"GamerTool 收集: {appid} {(string.IsNullOrWhiteSpace(name) ? "" : name)}（{uploadedFiles} 个文件）";
        var commitBody = tip == null
            ? (object)new { message, tree = newTree }
            : new { message, tree = newTree, parents = new[] { tip } };
        var commit = await SendJson("POST", $"https://api.github.com/repos/{RepoSlug}/git/commits", commitBody, proxy);
        string commitSha = commit.GetProperty("sha").GetString();

        ct.ThrowIfCancellationRequested();
        if (tip != null)
        {
            await SendJson("PATCH", $"https://api.github.com/repos/{RepoSlug}/git/refs/heads/{appid}",
                new { sha = commitSha, force = false }, proxy);
        }
        else
        {
            await SendJson("POST", $"https://api.github.com/repos/{RepoSlug}/git/refs",
                new { @ref = $"refs/heads/{appid}", sha = commitSha }, proxy);
        }

        r.Uploaded = true;
        r.UploadedFiles = uploadedFiles;
        r.CommitSha = commitSha[..8];
        return r;
    }

    /// <summary>git blob sha1 = SHA1("blob " + 长度 + "\\0" + 内容)。</summary>
    internal static string BlobSha(byte[] bytes)
    {
        string header = "blob " + bytes.Length + "\0";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        byte[] all = new byte[headerBytes.Length + bytes.Length];
        Buffer.BlockCopy(headerBytes, 0, all, 0, headerBytes.Length);
        Buffer.BlockCopy(bytes, 0, all, headerBytes.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(all)).ToLowerInvariant();
    }
}
