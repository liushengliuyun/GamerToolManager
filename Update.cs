namespace GamerToolManager;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ---------------- 自动更新引擎:GitHub Release 检查 / 多源下载 / 静默安装 ----------------
internal static class Updater
{
    public const string Repo = "liushengliuyun/GamerToolManager";
    public const string ReleasePage = "https://github.com/" + Repo + "/releases/latest";
    public const string SetupAsset = "GamerToolManager-Setup.exe";

    /// <summary>当前程序版本(程序集版本前三段,形如 1.6.0)。</summary>
    public static Version Current => typeof(Updater).Assembly.GetName().Version;

    // API 走令牌客户端(限速 5000/h);令牌失效(401)自动降级匿名;下载一律匿名 —— 令牌绝不发给第三方镜像
    static readonly HttpClient http = CreateClient(null, auth: true);
    static readonly HttpClient httpAnon = CreateClient(null, auth: false);
    static readonly HttpClient httpDlAuth = CreateClient(null, auth: true, dl: true);
    static readonly HttpClient httpDl = CreateClient(null, auth: false, dl: true);
    static HttpClient httpProxy;
    static HttpClient httpProxyAnon;
    static string proxyKey;
    static string proxyKeyAnon;

    static HttpClient CreateClient(string proxy, bool auth, bool dl = false)
    {
        var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!dl) h.AutomaticDecompression = DecompressionMethods.None; // 下载流不解压:避免 Content-Length(压缩)≠ 流长(解压)导致大小校验错乱
        if (proxy != null) { h.Proxy = new WebProxy(proxy); h.UseProxy = true; }
        // dl 客户端 10 分钟超时:镜像限速(~250KB/s)时 56MB 需 ~4 分钟;UI 有取消按钮兜底
        var c = new HttpClient(h) { Timeout = dl ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(proxy == null ? 20 : 35) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GamerToolManager/" + Current.ToString(3));
        // dl=true:资产流客户端(Accept 必须 octet-stream,否则资产端点返回 JSON 元数据而非二进制)
        c.DefaultRequestHeaders.Accept.ParseAdd(dl ? "application/octet-stream" : "application/vnd.github+json, application/json");
        // 令牌仅用于 api.github.com 限速提升;失效(401)由 GetJsonAsync 降级匿名兜底
        if (auth && !string.IsNullOrWhiteSpace(Collector.UploadToken))
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Collector.UploadToken);
        return c;
    }

    static HttpClient httpProxyDl;
    static string proxyKeyDl;

    static HttpClient Px(string proxy, bool auth, bool dl = false)
    {
        if (dl)
        {
            if (httpProxyDl == null || proxyKeyDl != proxy)
            {
                httpProxyDl?.Dispose();
                httpProxyDl = CreateClient(proxy, false, dl: true);
                proxyKeyDl = proxy;
            }
            return httpProxyDl;
        }
        if (auth)
        {
            if (httpProxy == null || proxyKey != proxy)
            {
                httpProxy?.Dispose();
                httpProxy = CreateClient(proxy, auth: true);
                proxyKey = proxy;
            }
            return httpProxy;
        }
        if (httpProxyAnon == null || proxyKeyAnon != proxy)
        {
            httpProxyAnon?.Dispose();
            httpProxyAnon = CreateClient(proxy, auth: false);
            proxyKeyAnon = proxy;
        }
        return httpProxyAnon;
    }

    /// <summary>查 API JSON:令牌直连 → 匿名直连 → 令牌代理 → 匿名代理,逐级降级;任何 401 都不致命。</summary>
    static async Task<string> GetJsonAsync(string url, string proxy, CancellationToken ct)
    {
        bool hasProxy = !string.IsNullOrWhiteSpace(proxy);
        var attempts = new List<(HttpClient cli, string via)>
        {
            (http, "直连"),
            (httpAnon, "直连(匿名)"),
        };
        if (hasProxy)
        {
            attempts.Add((Px(proxy, true), "代理"));
            attempts.Add((Px(proxy, false), "代理(匿名)"));
        }
        Exception last = null;
        foreach (var (cli, via) in attempts)
        {
            try { return await cli.GetStringAsync(url, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        throw new HttpRequestException("无法访问: " + url, last);
    }
    /// <summary>版本比较:兼容 v/V 前缀与 1~3 段数字;解析不了的按更低处理 —— 宁可多提示一次,不误报已是最新。</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] pa = ParseParts(a), pb = ParseParts(b);
        if (pa == null && pb == null) return 0;
        if (pa == null) return -1;
        if (pb == null) return 1;
        for (int i = 0; i < 3; i++)
        {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    static int[] ParseParts(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim().TrimStart('v', 'V');
        var parts = v.Split('.');
        if (parts.Length == 0 || parts.Length > 3) return null;
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int x) || x < 0) return null;
            n[i] = x;
        }
        return n;
    }

    public class ReleaseInfo
    {
        public string Tag, Name, Notes, PublishedAt;
        public long AssetId;
        public string AssetUrl;   // 安装包 browser_download_url
        public string AssetName;
        public long AssetSize;
        public string Version => Tag?.TrimStart('v');
        public bool IsNewer => CompareVersions(Version, Current.ToString(3)) > 0;
    }

    /// <summary>查询 GitHub 最新 Release(直连优先,失败走代理)。</summary>
    public static async Task<ReleaseInfo> CheckAsync(string proxy, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{Repo}/releases/latest";
        string json = await GetJsonAsync(url, proxy, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rel = new ReleaseInfo
        {
            Tag = root.GetProperty("tag_name").GetString(),
            Name = root.TryGetProperty("name", out var n) ? n.GetString() : null,
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null,
            PublishedAt = root.TryGetProperty("published_at", out var p) ? p.GetString() : null,
        };

        // 找安装包附件:精确名优先,其次任意 .exe
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string an = a.GetProperty("name").GetString();
                bool exe = an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (an.Equals(SetupAsset, StringComparison.OrdinalIgnoreCase)) { Pick(a); break; }
                if (exe && rel.AssetUrl == null) Pick(a);
            }
        }
        void Pick(JsonElement a)
        {
            rel.AssetUrl = a.GetProperty("browser_download_url").GetString();
            rel.AssetName = a.GetProperty("name").GetString();
            rel.AssetSize = a.GetProperty("size").GetInt64();
            rel.AssetId = a.GetProperty("id").GetInt64();
        }
        if (string.IsNullOrEmpty(rel.AssetUrl))
            throw new InvalidOperationException("最新 Release 没有 .exe 安装包附件,请到发布页手动下载。");

        return rel;
    }

    /// <summary>
    /// 多源接力下载安装包到临时目录:API 资产流(api.github.com 国内直连可达) → ghproxy.net 镜像 →
    /// github 直连(挂 VPN 时) → 本地代理。逐源流式落盘 + 大小/PE 校验,失败自动换下一个源。
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo rel, string proxy,
        Action<string> status, Action<long, long> progress, CancellationToken ct)
    {
        string dest = Path.Combine(Path.GetTempPath(), "GamerToolManager-Setup-" + rel.Version + ".exe");
        string apiAsset = $"https://api.github.com/repos/{Repo}/releases/assets/{rel.AssetId}";
        bool hasProxy = !string.IsNullOrWhiteSpace(proxy);
        Exception last = null;

        // 分段并发优先：单连接到 GitHub 资产 CDN 常被限速，6 线程 Range 分块通常有数倍提升；
        // 任一段失败 → 删除半成品回退到下面的多源接力
        if (rel.AssetSize > 1_000_000)
        {
            status?.Invoke($"正在下载(6 线程分段)…({Math.Round(rel.AssetSize / 1048576.0)} MB)");
            try
            {
                await SegmentedDownloadAsync(rel, dest, progress, ct);
                var fi = new FileInfo(dest);
                if (fi.Length == rel.AssetSize && IsPe(dest)) return dest;
                status?.Invoke("⚠ 分段下载校验未过，改用多源接力…");
            }
            catch (OperationCanceledException) { TryDelete(dest); throw; }
            catch (Exception ex) { status?.Invoke("⚠ 分段下载不可用(" + ex.Message + ")，改用多源接力…"); }
        }

        var attempts = new List<(string label, HttpClient cli, string url, bool octet)>
        {
            ("API 资产流", httpDlAuth, apiAsset, true),
            ("API 资产流(匿名)", httpDl, apiAsset, true),      // 令牌失效或公开构建无令牌
            ("ghproxy.net 镜像", httpAnon, "https://ghproxy.net/" + rel.AssetUrl, false),
            ("github 直连", httpAnon, rel.AssetUrl, false),     // 挂 VPN 时可用
        };
        if (hasProxy) attempts.Add(("本地代理", Px(proxy, false, dl: true), rel.AssetUrl, false));

        foreach (var (label, cli, surl, octet) in attempts)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"正在下载(源:{label})…(" + Math.Round(rel.AssetSize / 1048576.0) + " MB)");
            try
            {
                Task<HttpResponseMessage> task;
                if (octet)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, surl);
                    req.Headers.Accept.ParseAdd("application/octet-stream");
                    task = cli.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                else
                {
                    task = cli.GetAsync(surl, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                using var resp = await task;
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? rel.AssetSize;
                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920];
                long done = 0;
                int read;
                while ((read = await net.ReadAsync(buf, ct)) > 0)
                {
                    await file.WriteAsync(buf.AsMemory(0, read), ct); // 只写实际读到的字节,整 buf 会写入脏尾
                    done += read;
                    progress?.Invoke(done, total);
                }
            }
            catch (OperationCanceledException) { TryDelete(dest); throw; }
            catch (Exception ex) { last = ex; TryDelete(dest); continue; }

            // 校验:大小一致 + PE 头(MZ) —— 镜像吐错内容时能兜住(实测 gh-proxy.com 曾吐 197MB 错误文件)
            var fi = new FileInfo(dest);
            if (rel.AssetSize > 0 && fi.Length != rel.AssetSize)
            {
                string why = $"源 {label} 大小校验失败({fi.Length:#,0} ≠ {rel.AssetSize:#,0} 字节),换源…";
                status?.Invoke("⚠ " + why);
                last = new IOException(why);
                TryDelete(dest);
                continue;
            }
            if (!IsPe(dest))
            {
                string why = $"源 {label} 校验失败(内容不是安装包),换源…";
                status?.Invoke("⚠ " + why);
                last = new IOException(why);
                TryDelete(dest);
                continue;
            }
            return dest;
        }
        throw new InvalidOperationException("所有下载源均失败,请检查网络或稍后再试。", last);
    }

    /// <summary>
    /// 6 线程 Range 分段下载:预分配目标文件,每段独立流式写入自己的区间(互不重叠,FileShare.Write 安全);
    /// 进度按聚合字节数回调(每 200ms 汇报一次,避免跨线程改 UI)。任一段两次尝试都失败即抛出,由调用方回退。
    /// </summary>
    static async Task SegmentedDownloadAsync(ReleaseInfo rel, string dest, Action<long, long> progress, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{Repo}/releases/assets/{rel.AssetId}";

        // Range 支持探测:期望 206 Partial Content
        using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
        {
            probe.Headers.Accept.ParseAdd("application/octet-stream");
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await httpDlAuth.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode != 206) throw new IOException("源不支持分段(非 206)");
        }

        using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            fs.SetLength(rel.AssetSize);                       // 预分配,段任务各自定位写自己的区间

        const int Segs = 6;
        long segSize = rel.AssetSize / Segs;
        var segDone = new long[Segs];
        var segs = Enumerable.Range(0, Segs).Select(i => Task.Run(async () =>
        {
            long start = i * segSize;
            long to = (i == Segs - 1) ? rel.AssetSize - 1 : (i + 1) * segSize - 1;
            for (int attempt = 0; ; attempt++)
            {
                long pos = start;
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.ParseAdd("application/octet-stream");
                    req.Headers.Range = new RangeHeaderValue(pos, to);
                    using var resp = await httpDlAuth.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    if ((int)resp.StatusCode != 206) throw new IOException("非 206 响应");
                    await using var net = await resp.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(dest, FileMode.Open, FileAccess.Write, FileShare.Write, 81920, useAsync: true);
                    file.Seek(pos, SeekOrigin.Begin);
                    var buf = new byte[81920];
                    int read;
                    while ((read = await net.ReadAsync(buf, ct)) > 0)
                    {
                        await file.WriteAsync(buf.AsMemory(0, read), ct);
                        pos += read;
                        segDone[i] = pos - start;
                    }
                    if (pos != to + 1) throw new IOException("段不完整");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (attempt < 1)
                {
                    segDone[i] = 0;   // 整段重试,进度回退本段
                }
            }
        })).ToArray();

        // 进度泵:每 200ms 汇报各段聚合值
        var all = Task.WhenAll(segs);
        while (true)
        {
            var fin = await Task.WhenAny(all, Task.Delay(200, ct));
            if (fin == all) break;
            progress?.Invoke(segDone.Sum(), rel.AssetSize);
        }
        await all;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>PE 头校验:前两字节是 "MZ"。</summary>
    static bool IsPe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return fs.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
        }
        catch { return false; }
    }

    /// <summary>启动安装包静默升级:一次 UAC 授权,装完由安装器自动重启程序(installer.iss [Run] 配合)。</summary>
    public static void LaunchInstaller(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS",
            UseShellExecute = true, // 走 requireAdministrator 清单,触发 UAC
        });
    }

    /// <summary>发布说明摘要:取前 N 行可读文本,供启动弹窗用。</summary>
    public static string NotesExcerpt(string md, int lines)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var keep = new List<string>();
        foreach (var raw in md.Split('\n'))
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("|--") || t.StartsWith("| --")) continue;   // 表格分隔
            if (t.StartsWith("![") || t.StartsWith("<img")) continue;    // 图片
            t = Regex.Replace(t, @"^#{1,6}\s*", "");                     // 标题井号
            t = Regex.Replace(t, @"!?\[([^\]]*)\]\([^)]*\)", "$1");      // 链接 → 文本
            t = t.Replace("**", "").Replace("`", "");                    // 粗体/代码标记
            if (t.Length > 0) keep.Add(t);
            if (keep.Count >= lines) break;
        }
        return string.Join("  ", keep);
    }
}

// ---------------- 更新对话框:版本信息 + 更新说明 + 下载进度 + 一键安装 ----------------
internal class UpdateDialog : Form
{
    readonly Store store;
    Updater.ReleaseInfo rel;
    string setupPath;
    bool downloading;
    CancellationTokenSource cts;

    readonly Label lblVer = new() { AutoSize = true, ForeColor = Theme.Text };
    readonly Label lblTitle = new() { AutoSize = true, ForeColor = Theme.Sub };
    readonly Label lblStatus = new() { AutoSize = true, ForeColor = Theme.Sub, MaximumSize = new Size(300, 0) };
    readonly ProgressBar bar = new() { Width = 260, Height = 16, Visible = false };
    readonly RichTextBox rtb = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,   // 缺此设置会以默认小尺寸叠在最上层，盖住顶部信息并留白
        BackColor = Theme.Back,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        DetectUrls = false,
        WordWrap = true,
        Font = new Font("Microsoft YaHei UI", 9f),
    };
    readonly Button btnAction = new() { Text = "立即下载并安装", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0) };
    readonly Button btnPage = new() { Text = "打开发布页", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0) };
    readonly Button btnClose = new() { Text = "关闭", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0) };

    public UpdateDialog(Store store, Updater.ReleaseInfo info)
    {
        this.store = store;
        rel = info;
        Text = "软件更新 — GamerTool 管理器";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 520);
        MinimizeBox = false;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        FormClosing += (_, _) => cts?.Cancel();

        // 按钮统一走全应用扁平样式（否则是系统灰按钮且高度不一）
        void StyleBtn(Button b, bool primary = false)
        {
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(0, 32);
            b.Padding = new Padding(10, 0, 10, 0);
            UiHelper.Style(b, primary);
        }
        StyleBtn(btnAction, true);
        StyleBtn(btnPage);
        StyleBtn(btnClose);

        // ---------- 顶部信息(FlowLayoutPanel 自适应,DPI 安全) ----------
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Panel,
            Padding = new Padding(12, 10, 12, 4),
        };

        lblVer.Text = $"当前版本 v{Updater.Current.ToString(3)}    最新版本 v{rel.Version}" +
                      (rel.IsNewer ? "" : "(已是最新)");
        lblTitle.Text = (string.IsNullOrEmpty(rel.Name) ? "" : rel.Name + "  ·  ") +
                        PubDate(rel.PublishedAt) + "  ·  安装包 " + ManifestPack.FmtBytes(rel.AssetSize);
        top.Controls.Add(lblVer);
        top.Controls.Add(lblTitle);

        // ---------- 更新说明(中间填充) ----------
        rtb.Text = BuildNotes(rel.Notes);

        // ---------- 底部:状态/进度 + 按钮 ----------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Panel };
        var flowL = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 0, 6),
        };
        flowL.Controls.Add(lblStatus);
        flowL.Controls.Add(bar);
        var flowR = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 16, 12, 16),
        };
        flowR.Controls.Add(btnClose);   // RightToLeft:先加在最右
        flowR.Controls.Add(btnPage);
        flowR.Controls.Add(btnAction);
        bottom.Controls.Add(flowL);
        bottom.Controls.Add(flowR);

        Controls.Add(rtb);
        Controls.Add(bottom);
        Controls.Add(top);

        // ---------- 交互 ----------
        btnAction.Click += async (_, _) => await OnActionAsync();
        btnPage.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = Updater.ReleasePage, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "打开发布页失败:" + ex.Message, "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        btnClose.Click += (_, _) => Close();
    }

    /// <summary>主操作按钮:空闲=开始下载 / 下载中=取消 / 已就绪=安装并重启。</summary>
    async Task OnActionAsync()
    {
        if (downloading) { cts?.Cancel(); return; }          // 下载中 → 作为「取消」
        if (setupPath != null) { Install(); return; }        // 已就绪 → 安装

        downloading = true;
        cts = new CancellationTokenSource();
        btnAction.Text = "取消下载";
        bar.Value = 0;
        bar.Visible = true;
        try
        {
            string proxy = store.ProxyEff;
            setupPath = await Updater.DownloadAsync(rel, proxy,
                s => lblStatus.Text = s,
                (done, total) =>
                {
                    lblStatus.Text = $"正在下载… {ManifestPack.FmtBytes(done)} / {ManifestPack.FmtBytes(total)}";
                    if (total > 0) bar.Value = (int)Math.Clamp(done * 100 / Math.Max(1, total), 0, 100);
                },
                cts.Token);
            downloading = false;
            btnAction.Text = "立即安装";
            lblStatus.Text = "✓ 安装包已下载并校验通过，安装时程序将自动重启";
            bar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            downloading = false;
            setupPath = null;
            btnAction.Text = "重试下载";
            lblStatus.Text = "已取消下载。";
            bar.Visible = false;
        }
        catch (Exception ex)
        {
            downloading = false;
            setupPath = null;
            btnAction.Text = "重试下载";
            lblStatus.Text = "✗ 下载失败:" + ex.Message;
            bar.Visible = false;
        }
        finally
        {
            cts?.Dispose();
            cts = null;
        }
    }

    /// <summary>启动安装包并退出程序;安装完成后由安装器自动重启新版本。</summary>
    void Install()
    {
        if (MessageBox.Show(this,
                "安装包已下载并校验通过,是否立即安装?\n\n" +
                "点「是」将关闭本程序并启动安装(弹出 UAC 时请点「是」),安装完成后程序会自动重新打开。",
                "安装更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;
        try
        {
            Updater.LaunchInstaller(setupPath);
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动安装包失败:" + ex.Message, "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>发布说明轻量净化:去掉图片/表格分隔/链接括号,保留可读文本。</summary>
    static string BuildNotes(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "(无更新说明)";
        var sb = new StringBuilder();
        foreach (var raw in md.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) { sb.AppendLine(); continue; }
            if (line.StartsWith("|--") || line.StartsWith("| --")) continue;      // 表格分隔行
            if (line.StartsWith("![") || line.StartsWith("<img")) continue;       // 图片行
            line = Regex.Replace(line, @"^#{1,6}\s*", "");                        // 标题井号
            line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1");         // 链接 → 文本
            line = line.Replace("**", "").Replace("`", "");                        // 粗体/行内代码标记
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>ISO 时间戳 → yyyy-MM-dd(解析不了返回空串)。</summary>
    static string PubDate(string iso)
    {
        return DateTime.TryParse(iso, out var d) ? d.ToLocalTime().ToString("yyyy-MM-dd") : "";
    }

    /// <summary>启动更新对话框。</summary>
    public static void ShowUpdate(Form owner, Store store, Updater.ReleaseInfo rel)
    {
        using var d = new UpdateDialog(store, rel);
        d.ShowDialog(owner);
    }
}
