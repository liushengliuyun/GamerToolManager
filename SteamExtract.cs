using System.Text;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Authentication;

namespace GamerToolManager;

// ---------------- Steam 凭据存储 ----------------
// %APPDATA%\GamerToolManager\steam.refresh：两行文本 —— 第 1 行账号名，第 2 行 refresh token。
// 只存 refresh token（绝不存密码）；删除该文件即退出登录。明文落盘为 v1 权衡，README 有风险说明。
internal static class SteamAuthStore
{
    public sealed class AuthInfo
    {
        public string Account;
        public string RefreshToken;
    }

    static string Path => System.IO.Path.Combine(Store.AppDir, "steam.refresh");

    public static AuthInfo Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var lines = File.ReadAllLines(Path);
            if (lines.Length < 2 || string.IsNullOrWhiteSpace(lines[0]) || string.IsNullOrWhiteSpace(lines[1])) return null;
            return new AuthInfo { Account = lines[0].Trim(), RefreshToken = lines[1].Trim() };
        }
        catch { return null; }
    }

    public static void Save(string account, string refreshToken)
    {
        try
        {
            Directory.CreateDirectory(Store.AppDir);
            File.WriteAllLines(Path, new[] { account, refreshToken });
        }
        catch { }
    }

    public static void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { }
    }

    /// <summary>
    /// 从 Steam 客户端的 loginusers.vdf 读取最近登录的账号名，用于登录预填。
    /// 该文件是明文且只含账号名（不含任何凭据）；凭据部分客户端是加密存储的，我们不读取。
    /// 优先级：AutoLogin=1（客户端当前自动登录账号）→ MostRecent=1（旧字段）→ Timestamp 最新。
    /// </summary>
    public static string DetectLastSteamAccount(string steamDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(steamDir)) return null;
            string path = System.IO.Path.Combine(steamDir, "config", "loginusers.vdf");
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            string best = null;
            long bestTs = -1;
            foreach (Match b in Regex.Matches(text, "\"7656\\d+\"\\s*\\{([^}]*)\\}"))
            {
                string body = b.Groups[1].Value;
                string name = Regex.Match(body, "\"AccountName\"\\s*\"([^\"]*)\"").Groups[1].Value;
                if (string.IsNullOrEmpty(name)) continue;
                if (Regex.IsMatch(body, "\"AutoLogin\"\\s*\"1\"") ||
                    Regex.IsMatch(body, "\"MostRecent\"\\s*\"1\""))
                    return name;
                long.TryParse(Regex.Match(body, "\"Timestamp\"\\s*\"(\\d+)\"").Groups[1].Value, out long ts);
                if (best == null || ts > bestTs) { best = name; bestTs = ts; }
            }
            return best;
        }
        catch { return null; }
    }
}

// ---------------- Steam 清单提取器（SteamKit2 3.4.0） ----------------
/// <summary>
/// 登录自己的 Steam 账号，从已拥有游戏提取 manifest + depot 密钥，输出到与拉取一致的
/// &lt;GamerToolDir&gt;\[AppID] 目录结构，供部署 / 清单收集上传直接消费。
/// 按需连接，应用关闭时 Disconnect()。
/// </summary>
internal sealed class SteamExtractor : IDisposable
{
    public static SteamExtractor Default { get; } = new();

    SteamClient steamClient;
    CallbackManager manager;
    SteamUser steamUser;
    SteamApps steamApps;
    SteamContent steamContent;
    SteamAuthentication steamAuth;
    SteamKit2.CDN.Client cdnClient;

    Thread pump;
    volatile bool pumpRunning;
    volatile bool connected;

    SteamAuthStore.AuthInfo savedAuth;
    volatile bool licensesReceived;
    readonly List<uint> packageIds = new();
    HashSet<uint> ownedApps;

    public string Account { get; private set; }
    public bool IsLoggedOn { get; private set; }

    /// <summary>登录/登出状态变化（UI 线程外触发，UI 自行 BeginInvoke）。</summary>
    public event Action StateChanged;

    SteamExtractor() { }

    // ---------- 泵线程 ----------
    void EnsureClient()
    {
        if (steamClient != null) return;
        steamClient = new SteamClient();
        manager = new CallbackManager(steamClient);
        steamUser = steamClient.GetHandler<SteamUser>();
        steamApps = steamClient.GetHandler<SteamApps>();
        steamContent = steamClient.GetHandler<SteamContent>();
        steamAuth = steamClient.Authentication;
        cdnClient = new SteamKit2.CDN.Client(steamClient);

        manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            connected = false;
            if (IsLoggedOn)
            {
                IsLoggedOn = false;
                licensesReceived = false;
                ownedApps = null;
                StateChanged?.Invoke();
            }
        });
        // 授权列表常驻订阅：Steam 只在登录后主动推送一次，错过订阅时机就再也收不到 ——
        // 必须在连接前订好并缓存（GetOwnedAppsAsync 曾因此等待超时）
        manager.Subscribe<SteamApps.LicenseListCallback>(cb =>
        {
            if (cb.LicenseList is not { Count: > 0 }) return;
            packageIds.Clear();
            packageIds.AddRange(cb.LicenseList.Select(l => l.PackageID));
            licensesReceived = true;
        });
        StartPump();
    }

    void StartPump()
    {
        if (pump is { IsAlive: true }) return;
        pumpRunning = true;
        pump = new Thread(PumpLoop) { IsBackground = true };
        pump.Start();
    }

    void PumpLoop()
    {
        while (pumpRunning)
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
    }

    /// <summary>等待某个回调（带超时与取消），过期未到抛 TimeoutException。</summary>
    async Task<T> WaitFor<T>(Func<T, bool> match, int timeoutSec, CancellationToken ct) where T : CallbackMsg
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable sub = null;
        sub = manager.Subscribe<T>(cb =>
        {
            if (match != null && !match(cb)) return;
            tcs.TrySetResult(cb);
        });
        try
        {
            var done = tcs.Task;
            var delay = Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct);
            var winner = await Task.WhenAny(done, delay).ConfigureAwait(false);
            if (winner != done)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"等待 Steam 回应超时（{timeoutSec}s）");
            }
            return await done.ConfigureAwait(false);
        }
        finally
        {
            sub?.Dispose();
        }
    }

    // ---------- 连接 / 登录 ----------
    async Task ConnectAsync(CancellationToken ct)
    {
        EnsureClient();
        if (connected) return;
        steamClient.Connect();
        await WaitFor<SteamClient.ConnectedCallback>(_ => true, 20, ct).ConfigureAwait(false);
        connected = true;
    }

    /// <summary>确保已登录：有保存的凭据就静默续登，否则抛 NeedsLoginException 由 UI 引导登录。</summary>
    public async Task EnsureLoggedOnAsync(CancellationToken ct, Action<string, LogKind> log = null)
    {
        if (IsLoggedOn) return;
        savedAuth = SteamAuthStore.Load();
        if (savedAuth == null) throw new NeedsLoginException("尚未登录 Steam。");
        await ConnectAsync(ct).ConfigureAwait(false);
        await TokenLogOnAsync(ct, log).ConfigureAwait(false);
    }

    /// <summary>交互式登录（密码 + Steam Guard），成功后凭据落盘供下次静默续登。</summary>
    public async Task<string> InteractiveLogOnAsync(string username, string password, IAuthenticator uiAuth,
        CancellationToken ct, Action<string, LogKind> log = null)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("请输入 Steam 账号与密码。");

        savedAuth = null;
        await ConnectAsync(ct).ConfigureAwait(false);
        log?.Invoke("正在验证凭据（如触发 Steam Guard 请按提示输入验证码）…", LogKind.Info);

        var session = await steamAuth.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = true,          // 换取可长期使用的 refresh token
            Authenticator = uiAuth,              // 邮件码 / 手机令牌码 / 手机确认 经 UI 弹窗收集
        }).ConfigureAwait(false);

        var poll = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
        savedAuth = new SteamAuthStore.AuthInfo
        {
            Account = string.IsNullOrWhiteSpace(poll.AccountName) ? username : poll.AccountName,
            RefreshToken = poll.RefreshToken,
        };
        SteamAuthStore.Save(savedAuth.Account, savedAuth.RefreshToken);
        log?.Invoke("✓ 验证通过，已保存本机登录凭据（refresh token，不含密码）。", LogKind.Ok);

        await TokenLogOnAsync(ct, log).ConfigureAwait(false);
        return savedAuth.Account;
    }

    /// <summary>用 refresh token 静默续登；令牌失效时清除存储并抛 NeedsLoginException。</summary>
    async Task TokenLogOnAsync(CancellationToken ct, Action<string, LogKind> log)
    {
        var details = new SteamUser.LogOnDetails
        {
            Username = savedAuth.Account,
            AccessToken = savedAuth.RefreshToken,
            ShouldRememberPassword = true, // 与 IsPersistentSession 换取的 token 配套
        };
        steamUser.LogOn(details);
        var cb = await WaitFor<SteamUser.LoggedOnCallback>(_ => true, 30, ct).ConfigureAwait(false);

        if (cb.Result == EResult.OK)
        {
            IsLoggedOn = true;
            Account = savedAuth.Account;
            StateChanged?.Invoke();
            log?.Invoke($"✓ 已登录 Steam: {Account}", LogKind.Ok);
            return;
        }

        // 令牌类失败 → 清凭据要求重新登录；限速单独提示
        SteamAuthStore.Delete();
        savedAuth = null;
        if (cb.Result == EResult.AccountLoginDeniedThrottle)
            throw new InvalidOperationException("登录尝试过于频繁，请几分钟后再试。");
        if (cb.Result != EResult.InvalidPassword)
            log?.Invoke($"⚠ Steam 返回 {cb.Result}，已清除本机凭据，需要重新登录。", LogKind.Warn);
        IsLoggedOn = false;
        StateChanged?.Invoke();
        throw new NeedsLoginException("保存的登录已过期，请重新登录。");
    }

    /// <summary>切换账号：清凭据 + 断开，回到未登录状态（UI 随后弹出登录面板）。</summary>
    public void Logout()
    {
        SteamAuthStore.Delete();
        savedAuth = null;
        IsLoggedOn = false;
        Account = null;
        ownedApps = null;
        licensesReceived = false;
        StateChanged?.Invoke();
        if (connected)
        {
            try { steamClient.Disconnect(); } catch { }
            connected = false;
        }
    }

    public void Disconnect()
    {
        pumpRunning = false;
        if (connected)
        {
            try { steamClient?.Disconnect(); } catch { }
            connected = false;
        }
    }

    public void Dispose() => Disconnect();

    // ---------- 已拥有游戏 ----------
    /// <summary>已拥有 AppID 集合（懒加载：LicenseListCallback → PICS 包信息 → appids 并集）。</summary>
    public async Task<HashSet<uint>> GetOwnedAppsAsync(CancellationToken ct, Action<string, LogKind> log = null)
    {
        if (ownedApps != null) return ownedApps;
        await EnsureLoggedOnAsync(ct, log).ConfigureAwait(false);

        if (!licensesReceived)
        {
            // 常驻订阅通常已缓存；这里只处理"刚登录、列表还在路上"的情况
            log?.Invoke("等待 Steam 授权列表…", LogKind.Tool);
            await WaitFor<SteamApps.LicenseListCallback>(
                c => c.LicenseList is { Count: > 0 }, 30, ct);
        }

        log?.Invoke("正在获取已拥有游戏列表（PICS 包信息）…", LogKind.Tool);
        var requests = packageIds.Select(id => new SteamApps.PICSRequest(id)).ToList();
        var rs = await steamApps.PICSGetProductInfo(null, requests, false);
        if (rs.Failed) throw new InvalidOperationException("获取已拥有游戏列表失败（Steam 网络异常），请重试。");

        var set = new HashSet<uint>();
        foreach (var res in rs.Results)
            foreach (var pkg in res.Packages.Values)
                foreach (var child in pkg.KeyValues["appids"].Children)
                    if (uint.TryParse(child.Value, out var id)) set.Add(id);
        ownedApps = set;
        log?.Invoke($"✓ 已拥有 {set.Count} 个游戏。", LogKind.Ok);
        return set;
    }

    // ---------- 提取 ----------
    public sealed class ExtractResult
    {
        public long AppId;
        public string Dir;
        public string Name;
        public int Files;
        public List<uint> FailedDepots = new();
    }

    /// <summary>从 Steam 提取一个已拥有游戏的清单（manifest + Key.vdf + 解锁 Lua），输出 [AppID] 目录。</summary>
    public async Task<ExtractResult> ExtractAsync(long appid, string rootDir, CancellationToken ct,
        Action<string, LogKind> log = null)
    {
        await EnsureLoggedOnAsync(ct, log).ConfigureAwait(false);

        // 1) 拥有权预检（集合未加载时先加载；仍不在集合里 → 明确拒绝）
        var owned = await GetOwnedAppsAsync(ct, log).ConfigureAwait(false);
        if (!owned.Contains((uint)appid))
            throw new InvalidOperationException($"当前账号未拥有游戏 {appid}（清单受 Steam 授权保护，无法提取）。");

        // 2) 应用信息（token → product info）
        log?.Invoke($"获取 {appid} 的应用信息…", LogKind.Tool);
        var tokens = await steamApps.PICSGetAccessTokens((uint)appid, null);
        tokens.AppTokens.TryGetValue((uint)appid, out var accessToken);

        var appRs = await steamApps.PICSGetProductInfo(
            new SteamApps.PICSRequest((uint)appid, accessToken), null, false);
        if (appRs.Failed) throw new InvalidOperationException("获取应用信息失败（Steam 网络异常），请重试。");

        SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo = null;
        foreach (var res in appRs.Results)
            if (res.Apps.TryGetValue((uint)appid, out var info)) { appInfo = info; break; }
        if (appInfo == null)
            throw new InvalidOperationException("Steam 上找不到该 AppID 的信息（未发售或已下架）。");

        var kv = appInfo.KeyValues;
        string name = kv["common"]["name"].AsString();
        if (string.IsNullOrWhiteSpace(name)) name = appid.ToString();

        // 3) 解析 depot 清单（跳过共享 depot 与无 public 清单的空 depot）
        var depots = new List<(uint depotId, ulong gid)>();
        foreach (var d in kv["depots"].Children)
        {
            if (d.Name == null || !uint.TryParse(d.Name, out var depotId)) continue;         // 跳过 branches 等非数字键
            if (!string.IsNullOrEmpty(d["depotfromapp"].AsString()))                          // 共享 depot（属别的 app）
            {
                log?.Invoke($"  depot {d.Name} 为共享内容（depotfromapp），v1 跳过。", LogKind.Warn);
                continue;
            }
            string gid = d["manifests"]["public"]["gid"].AsString();
            if (string.IsNullOrEmpty(gid) || !ulong.TryParse(gid, out var manifestGid))
                continue;                                                                     // 空 Depot / 无公开清单
            depots.Add((depotId, manifestGid));
        }
        if (depots.Count == 0)
            throw new InvalidOperationException("该应用没有可提取的内容 depot。");
        log?.Invoke($"共 {depots.Count} 个 depot 需要提取。", LogKind.Info);

        // 4) 目标目录（与拉取产物同构）
        string dirName = "[" + appid + "]" + name;
        foreach (char c in Path.GetInvalidFileNameChars()) dirName = dirName.Replace(c, '_');
        string dir = Path.Combine(rootDir, dirName);
        Directory.CreateDirectory(dir);

        // 5) 逐 depot：密钥 → CDN 下载 manifest
        var servers = (await steamContent.GetServersForSteamPipe(null, null).ConfigureAwait(false))
            .Where(s => !string.IsNullOrEmpty(s.Host))
            .OrderByDescending(s => s.Protocol == SteamKit2.CDN.Server.ConnectionProtocol.HTTPS)
            .ThenByDescending(s => s.Type == "CDN")
            .ToList();
        if (servers.Count == 0) throw new InvalidOperationException("无法获取 Steam CDN 服务器列表。");

        var keys = new List<(long depot, string key)>();
        var manifestPaths = new List<string>();
        var failed = new List<uint>();

        foreach (var (depotId, gid) in depots)
        {
            ct.ThrowIfCancellationRequested();
            string localPath = Path.Combine(dir, depotId + "_" + gid + ".manifest");

            byte[] depotKey = null;
            try
            {
                // 密钥每次都要取（Key.vdf 需要）；只有清单文件本身可跳过下载
                var keyCb = await steamApps.GetDepotDecryptionKey(depotId, (uint)appid);
                if (keyCb.Result != EResult.OK || keyCb.DepotKey == null)
                    throw new InvalidOperationException($"Steam 返回 {keyCb.Result}（未拥有该 depot 或未发售）");
                depotKey = keyCb.DepotKey;

                if (File.Exists(localPath))
                {
                    log?.Invoke($"✓ {depotId}_{gid}.manifest 已存在，跳过下载", LogKind.Tool);
                }
                else
                {
                    ulong code = await steamContent.GetManifestRequestCode(depotId, (uint)appid, gid).ConfigureAwait(false);
                    Exception lastEx = null;
                    for (int i = 0; i < Math.Min(3, servers.Count); i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var server = servers[i];
                        try
                        {
                            var auth = await steamContent.GetCDNAuthToken((uint)appid, depotId, server.Host).ConfigureAwait(false);
                            var manifest = await cdnClient.DownloadManifestAsync(depotId, gid, code,
                                server, depotKey, cdnAuthToken: auth.Token).ConfigureAwait(false);
                            manifest.SaveToFile(localPath);
                            log?.Invoke($"✓ {depotId}_{gid}.manifest（{new FileInfo(localPath).Length / 1024.0:F0} KB）← {server.Host}", LogKind.Ok);
                            lastEx = null;
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            log?.Invoke($"  ⚠ {server.Host} 失败: {ex.Message}", LogKind.Warn);
                        }
                    }
                    if (lastEx != null) throw new InvalidOperationException("所有 CDN 服务器均失败: " + lastEx.Message);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed.Add(depotId);
                log?.Invoke($"✕ depot {depotId}: {ex.Message}", LogKind.Err);
                continue;
            }

            keys.Add((depotId, BytesHex(depotKey)));
            manifestPaths.Add(localPath);
        }

        // 6) Key.vdf（ParseKeyVdf 兼容形状；只有拿到的密钥才会写入）
        string vdfPath = Path.Combine(dir, "Key.vdf");
        var sb = new StringBuilder();
        sb.AppendLine("\"depots\"");
        sb.AppendLine("{");
        foreach (var (depot, key) in keys)
        {
            sb.AppendLine($"    \"{depot}\"");
            sb.AppendLine("    {");
            sb.AppendLine($"        \"DecryptionKey\" \"{key}\"");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        await File.WriteAllTextAsync(vdfPath, sb.ToString(), ct);

        // 7) 解锁 Lua 最后写（文件夹里没有 lua 的状态 = 未完成，不会被当成可部署）
        string luaText = Store.NativePuller.BuildLuaText(appid, keys, manifestPaths);
        string luaPath = Path.Combine(dir, appid + ".lua");
        await File.WriteAllTextAsync(luaPath, luaText, ct);

        if (failed.Count > 0)
            log?.Invoke($"⚠ 提取完成但有 {failed.Count} 个 depot 失败: {string.Join(", ", failed)}", LogKind.Warn);
        else
            log?.Invoke($"✓ 提取完成: {dir}（{manifestPaths.Count} 个清单）", LogKind.Ok);

        return new ExtractResult
        {
            AppId = appid,
            Dir = dir,
            Name = name,
            Files = manifestPaths.Count + 2,
            FailedDepots = failed,
        };
    }

    static string BytesHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>需要用户登录（无凭据或凭据已失效）。</summary>
internal sealed class NeedsLoginException : Exception
{
    public NeedsLoginException(string message) : base(message) { }
}
