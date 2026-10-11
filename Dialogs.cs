using System.Diagnostics;
using SteamKit2.Authentication;
using static GamerToolManager.Collector;

namespace GamerToolManager;

// ---------------- 通用输入框 ----------------
internal static class InputDialog
{
    public static string Show(IWin32Window owner, string title, string label, string defaultValue)
    {
        string result = null;

        // 纯手工计算布局：各部件实测高度 → 窗口高 → 绝对定位。
        // 不用 Form.AutoSize / Dock 组合（高 DPI 下被反复证明不可靠：按钮/输入框被裁）。
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            Padding = new Padding(10, 8, 10, 0),
            ForeColor = Theme.Sub,
            Font = new Font("Microsoft YaHei UI", 9.5f),
        };
        int lblH = lbl.GetPreferredSize(new Size(400, 0)).Height;

        var txt = new TextBox
        {
            Font = new Font("Microsoft YaHei UI", 11f),
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        int txtH = txt.PreferredHeight;

        const int W = 420, BtnW = 88, BtnH = 30, Pad = 10, Gap = 10;
        int txtY = lblH + 2;
        int btnY = txtY + txtH + Gap;
        using var f = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(W, btnY + BtnH + Pad),
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = new Font("Microsoft YaHei UI", 9.5f),
        };
        lbl.SetBounds(0, 0, W, lblH);
        txt.SetBounds(Pad, txtY, W - Pad * 2, txtH);

        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        btnOk.SetBounds(W - Pad - BtnW, btnY, BtnW, BtnH);
        btnCancel.SetBounds(W - Pad - BtnW * 2 - 8, btnY, BtnW, BtnH);
        btnOk.FlatAppearance.BorderSize = 0; btnOk.FlatStyle = FlatStyle.Flat; btnOk.BackColor = Theme.Accent; btnOk.ForeColor = Color.White;
        btnCancel.FlatAppearance.BorderSize = 0; btnCancel.FlatStyle = FlatStyle.Flat; btnCancel.BackColor = Theme.Input; btnCancel.ForeColor = Theme.Text;

        f.Controls.Add(lbl);
        f.Controls.Add(txt);
        f.Controls.Add(btnOk);
        f.Controls.Add(btnCancel);
        f.AcceptButton = btnOk; f.CancelButton = btnCancel;
        if (f.ShowDialog(owner) == DialogResult.OK) result = txt.Text;
        return result;
    }
}

// ---------------- 拉取新游戏 ----------------
internal class PullDialog : Form
{
    private readonly Store store;
    private readonly TextBox txt = new();
    private readonly RadioButton rbN = new() { Text = "Manifest2Lua（标准）", Checked = true };
    private readonly RadioButton rbL = new() { Text = "Manifest2Lua-nolock（防文件锁定）" };
    private readonly Button btnStart = new() { Text = "开始拉取" };
    private readonly Button btnCancelRun = new() { Text = "取消运行", Enabled = false };
    private readonly RichTextBox log = new();
    private readonly Label lblState = new();
    private readonly CheckBox chkProxy = new() { Text = "使用代理环境变量", ForeColor = Theme.Sub };

    public Action OnFinished;

    private CancellationTokenSource cts;

    public PullDialog(Store store, string prefill = null, bool autoStart = false)
    {
        this.store = store;
        Text = "拉取新游戏 — Manifest2Lua";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 520);
        MinimumSize = new Size(560, 400);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        // 拖拽链接进来 → 直接填入 AppID
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            try
            {
                string t = e.Data.GetData(DataFormats.Text) as string
                           ?? e.Data.GetData(DataFormats.StringFormat) as string;
                var id = Store.ExtractAppId(t);
                if (id == null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files is { Length: > 0 }) id = Store.ExtractAppIdFromPath(files[0]);
                }
                if (id != null) txt.Text = id.ToString();
            }
            catch { }
        };

        var top = new Panel { Dock = DockStyle.Top, Height = 158, BackColor = Theme.Panel };
        var lbl = new Label { Text = "AppID 或游戏名：", Location = new Point(14, 8), AutoSize = true, ForeColor = Theme.Sub };
        txt.Location = new Point(14, 38);
        txt.Font = new Font("Microsoft YaHei UI", 11.5f);
        txt.BackColor = Theme.Input; txt.ForeColor = Theme.Text; txt.BorderStyle = BorderStyle.FixedSingle;

        rbN.Location = new Point(14, 82);
        rbL.Location = new Point(14, 82);
        rbN.AutoSize = rbL.AutoSize = chkProxy.AutoSize = true;
        rbN.BackColor = rbL.BackColor = Theme.Panel;
        rbN.ForeColor = rbL.ForeColor = Theme.Text;
        chkProxy.Checked = store.S.PassProxyToTool;

        StyleBtn(btnStart, primary: true);
        StyleBtn(btnCancelRun);
        btnStart.Location = new Point(300, 118);
        btnCancelRun.Location = new Point(416, 118);

        top.Controls.AddRange(new Control[] { lbl, txt, rbN, rbL, chkProxy, btnStart, btnCancelRun });
        void Relayout()
        {
            txt.Width = top.Width - 28;
            // 单选钮按 AutoSize 实际宽度动态排队，避免互相遮挡
            rbN.Left = 14;
            rbL.Left = rbN.Right + 14;
            chkProxy.Location = new Point(14, 120);
            btnCancelRun.Location = new Point(top.Width - 14 - 108, 118);
            btnStart.Location = new Point(top.Width - 14 - 224, 118);
        }
        top.Resize += (_, _) => Relayout();
        Relayout();

        lblState.Dock = DockStyle.Bottom;
        lblState.Height = 26;
        lblState.ForeColor = Theme.Sub;
        lblState.Text = " 工具会自动从 GitHub 下载清单并生成解锁 Lua，产物在 GamerTool 目录的 [AppID] 文件夹。";
        lblState.BackColor = Theme.Panel;

        log.Dock = DockStyle.Fill;
        log.BackColor = Color.FromArgb(18, 20, 26);
        log.ForeColor = Theme.Text;
        log.Font = new Font("Microsoft YaHei UI", 9f);
        log.ReadOnly = true;
        log.BorderStyle = BorderStyle.None;

        Controls.Add(log);
        Controls.Add(lblState);
        Controls.Add(top);

        btnStart.Click += async (_, _) => await StartAsync();
        btnCancelRun.Click += (_, _) => cts?.Cancel();
        txt.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await StartAsync(); } };
        FormClosing += (_, e) => { if (cts != null && !cts.IsCancellationRequested) cts.Cancel(); };

        if (store.S.M2LVariant == "nolock") { rbL.Checked = true; rbN.Checked = false; }
        if (!string.IsNullOrEmpty(prefill)) txt.Text = prefill;
        if (autoStart)
            Shown += async (_, _) => { if (txt.Text.Trim().Length > 0) await StartAsync(); };
    }

    private static void StyleBtn(Button b, bool primary = false)
    {
        b.AutoSize = false; b.Width = 108; b.Height = 30;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.FlatAppearance.MouseDownBackColor = Theme.Active;
        b.BackColor = primary ? Theme.Accent : Theme.Input;
        b.ForeColor = primary ? Color.White : Theme.Text;
    }

    private void AppendLog(string line, Color c)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = c;
            log.AppendText(line + "\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }
        catch { }
    }

    private void AppendToolLine(string line) =>
        BeginInvoke(() => AppendLog("  " + line, Theme.ToolC));

    private async Task StartAsync()
    {
        string input = txt.Text.Trim();
        if (input.Length == 0)
        {
            MessageBox.Show(this, "请输入 AppID 或游戏名。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string exe = rbL.Checked ? store.S.NolockExe : store.S.NormalExe;
        if (!File.Exists(exe))
        {
            MessageBox.Show(this, "找不到工具：\n" + exe, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        btnStart.Enabled = false; btnCancelRun.Enabled = true; txt.Enabled = false;
        rbN.Enabled = rbL.Enabled = false;
        lblState.Text = " 运行中…（正在下载清单 / 生成 Lua，网络较慢时请耐心等待）";
        lblState.ForeColor = Theme.Yellow;
        AppendLog("> " + Path.GetFileName(exe) + "   输入: " + input, Theme.Accent);

        var before = FolderIds();
        cts = new CancellationTokenSource();

        Dictionary<string, string> env = null;
        if (chkProxy.Checked && !string.IsNullOrWhiteSpace(store.S.NameProxy))
            env = new Dictionary<string, string>
            {
                ["HTTP_PROXY"] = store.S.NameProxy,
                ["HTTPS_PROXY"] = store.S.NameProxy,
                ["http_proxy"] = store.S.NameProxy,
                ["https_proxy"] = store.S.NameProxy,
            };

        int code = -1;
        try
        {
            code = await Store.RunToolAsync(exe, store.S.GamerToolDir, input, env, AppendToolLine, cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("✕ 已手动取消。", Theme.Yellow);
        }
        catch (Exception ex)
        {
            AppendLog("✕ 启动失败: " + ex.Message, Theme.Red);
        }

        var after = FolderIds();
        var fresh = after.Except(before).ToList();
        if (fresh.Count > 0)
            AppendLog("✓ 拉取完成，生成清单文件夹: " + string.Join(", ", fresh.Select(id => "[" + id + "]")), Theme.Green);
        else if (code == 0)
            AppendLog("✓ 工具执行完成（未出现新文件夹 —— 可能该游戏已存在）。", Theme.Green);
        else
            AppendLog("工具退出（退出码 " + code + "）。", Theme.Yellow);

        lblState.Text = " 完成。回到列表选中游戏，点【部署 / 加入 SteamTools 管理】按钮即可解锁。";
        lblState.ForeColor = Theme.Green;
        btnStart.Enabled = true; btnCancelRun.Enabled = false; txt.Enabled = true;
        rbN.Enabled = rbL.Enabled = true;
        OnFinished?.Invoke();
    }

    private HashSet<long> FolderIds()
    {
        var set = new HashSet<long>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(store.S.GamerToolDir))
            {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(d), @"^\[(\d+)\]$");
                if (m.Success) set.Add(long.Parse(m.Groups[1].Value));
            }
        }
        catch { }
        return set;
    }
}

// ---------------- 设置 ----------------
internal class SettingsDialog : Form
{
    private readonly Store store;
    private readonly TextBox txtRoot = new();
    private readonly TextBox txtSteam = new();
    private readonly TextBox txtProxy = new();
    private readonly CheckBox chkDepot = new() { Text = "还原时同时清理 depotcache 清单文件", Checked = true };
    private readonly CheckBox chkDb = new() { Text = "还原时同时清理 SteamTools 登记 (appids.db)", Checked = true };
    private readonly CheckBox chkAutoFetch = new() { Text = "启动时后台联网抓取缺失游戏名", Checked = true };
    private readonly Label lblPreview = new();

    public SettingsDialog(Store store)
    {
        this.store = store;
        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(720, 0); // 给字段表一个真实宽度，百分比列才有意义
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 10f);

        // 根表格：单列，行高随内容自适应（DPI 安全）
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 10),
            Margin = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 三组字段：标签独占一行，输入框 + 浏览按钮一行（输入框获得整行宽度）
        var fields = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0; // 本地行号（TableLayoutPanel.RowCount 初始为 -1，不能直接用）
        void AddRow(string caption, Control input, Button browse = null)
        {
            var lbl = new Label { Text = caption, AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 10, 0, 2) };
            fields.Controls.Add(lbl, 0, row);
            fields.Controls.Add(input, 0, row + 1);
            input.Margin = new Padding(0, 0, 8, 4);
            input.Dock = DockStyle.Fill;
            if (browse != null)
            {
                browse.Margin = new Padding(0, 0, 0, 4);
                fields.Controls.Add(browse, 1, row + 1);
            }
            row += 2;
        }

        StyleBox(txtRoot); StyleBox(txtSteam); StyleBox(txtProxy);

        var browseRoot = MkBrowse();
        var browseSteam = MkBrowse();
        browseRoot.Click += (_, _) => { Browse(txtRoot, "选择 GamerTool 目录"); };
        browseSteam.Click += (_, _) => { Browse(txtSteam, "选择 Steam 目录（一般留空自动检测）"); };

        AddRow("GamerTool 目录（拉取产物保存到这里）：", txtRoot, browseRoot);
        AddRow("Steam 目录（留空自动检测）：", txtSteam, browseSteam);
        AddRow("游戏名抓取代理：", txtProxy);
        root.Controls.Add(fields);

        // 行为组（内部流式，随字体自适应）
        var grp = new GroupBox { Text = "行为", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.Sub, Margin = new Padding(0, 12, 0, 0) };
        var grpFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(4, 0, 4, 0) };
        foreach (var chk in new[] { chkDepot, chkDb, chkAutoFetch })
        {
            chk.AutoSize = true;
            chk.Margin = new Padding(0, 3, 0, 3);
            grpFlow.Controls.Add(chk);
        }
        grp.Controls.Add(grpFlow);
        root.Controls.Add(grp);

        var lblPreview = new Label { AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 10, 0, 0) };
        root.Controls.Add(lblPreview);

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var btnOk = new Button { Text = "保存", Width = 100, Height = 32 };
        var btnNo = new Button { Text = "取消", Width = 100, Height = 32, Margin = new Padding(0, 0, 8, 0) };
        UiHelper.Style(btnOk, true); UiHelper.Style(btnNo);
        flow.Controls.Add(btnNo); flow.Controls.Add(btnOk);
        root.Controls.Add(flow);
        Controls.Add(root);

        txtRoot.Text = store.S.GamerToolDir;
        txtSteam.Text = store.S.SteamDir;
        txtProxy.Text = store.S.NameProxy;
        chkDepot.Checked = store.S.CleanDepotCache;
        chkDb.Checked = store.S.CleanAppIdsDb;
        chkAutoFetch.Checked = store.S.AutoFetchNames;

        void UpdatePreview()
        {
            string steam = txtSteam.Text.Trim();
            if (steam == "") steam = store.SteamDirEff;
            string stp = steam == "" ? "?" : Path.Combine(steam, "config", "stplug-in");
            lblPreview.Text = "stplug-in: " + stp + "\ndepotcache: " + (steam == "" ? "?" : Path.Combine(steam, "config", "depotcache")) +
                              "\nSteamTools: " + (Store.SteamToolsInstalled ? "✓ 已安装 (" + Store.SteamToolsExe + ")" : "✗ 未安装（主界面【SteamTools】按钮可安装）");
        }
        txtSteam.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        btnOk.Click += (_, _) =>
        {
            store.S.GamerToolDir = txtRoot.Text.Trim();
            store.S.SteamDir = txtSteam.Text.Trim();
            store.S.NameProxy = txtProxy.Text.Trim();
            store.S.CleanDepotCache = chkDepot.Checked;
            store.S.CleanAppIdsDb = chkDb.Checked;
            store.S.AutoFetchNames = chkAutoFetch.Checked;
            store.SaveSettings();
            DialogResult = DialogResult.OK;
            Close();
        };
        btnNo.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
    }

    private static void StyleBox(TextBox t)
    {
        t.BorderStyle = BorderStyle.FixedSingle;
        t.BackColor = Theme.Input;
        t.ForeColor = Theme.Text;
    }

    private Button MkBrowse()
    {
        var b = new Button { Text = "…", FlatStyle = FlatStyle.Flat, AutoSize = true, MinimumSize = new Size(56, 30) };
        b.FlatAppearance.BorderSize = 1; b.FlatAppearance.BorderColor = Theme.Border;
        b.BackColor = Theme.Input; b.ForeColor = Theme.Text;
        return b;
    }

    private void Browse(TextBox target, string title)
    {
        using var d = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        if (Directory.Exists(target.Text)) d.SelectedPath = target.Text;
        if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
    }
}

// ---------------- 孤儿登记项清理 ----------------
internal class OrphanDialog : Form
{
    private readonly Store store;
    private readonly List<long> ids;
    private readonly CheckedListBox list = new();

    public OrphanDialog(Store store, List<long> ids)
    {
        this.store = store;
        this.ids = ids;
        Text = "SteamTools 孤儿登记项 — appids.db";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 520);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 66,
            Text = "以下 AppID 登记在 SteamTools 的 appids.db 中，但没有任何解锁 Lua 覆盖它们，\n" +
                   "通常是 Lua 已被删除后的残留。勾选要清理的项（默认全选），点击清理。\n" +
                   "注意：如果这些是你仍需要的功能（如悬浮窗登记的解密 Key），请不要清理。",
            ForeColor = Theme.Sub,
            BackColor = Theme.Panel,
            Padding = new Padding(10, 8, 10, 0),
        };

        list.Dock = DockStyle.Fill;
        list.BackColor = Color.FromArgb(18, 20, 26);
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.None;
        list.CheckOnClick = true;

        var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel };
        var btnClean = new Button { Text = "清理勾选项", Width = 110, Height = 30 };
        var btnAll = new Button { Text = "全选/全不选", Width = 100, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        var btnClose = new Button { Text = "关闭", Width = 88, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        UiHelper.Style(btnClean, true); UiHelper.Style(btnAll); UiHelper.Style(btnClose);
        flow.Controls.Add(btnClose); flow.Controls.Add(btnAll); flow.Controls.Add(btnClean);
        flow.Padding = new Padding(0, 8, 12, 8);

        Controls.Add(list);
        Controls.Add(lbl);
        Controls.Add(flow);

        foreach (long id in ids)
        {
            int i = list.Items.Add(id.ToString(), true);
        }

        btnAll.Click += (_, _) =>
        {
            bool anyUnchecked = false;
            for (int i = 0; i < list.Items.Count; i++) if (!list.GetItemChecked(i)) anyUnchecked = true;
            for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, anyUnchecked);
        };

        btnClean.Click += (_, _) =>
        {
            var targets = new List<long>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i)) targets.Add(long.Parse((string)list.Items[i]));
            if (targets.Count == 0) return;
            if (MessageBox.Show(this, "将从 appids.db 删除 " + targets.Count + " 行登记，确定？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            int n = MiniSql.DeleteAppIds(store.AppIdsDbEff, targets);
            MessageBox.Show(this, n >= 0 ? "已删除 " + n + " 行。" : "删除失败（数据库被占用或无权限）。",
                n >= 0 ? "完成" : "错误", MessageBoxButtons.OK,
                n >= 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            if (n > 0)
            {
                foreach (var t in targets)
                    for (int i = 0; i < list.Items.Count; i++)
                        if ((string)list.Items[i] == t.ToString()) { list.Items.RemoveAt(i); break; }
            }
        };

        btnClose.Click += (_, _) => Close();
    }
}

// ---------------- 使用教程查看器 ----------------
internal class TutorialDialog : Form
{
    public TutorialDialog(string path)
    {
        Text = "使用教程";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 640);
        BackColor = Color.FromArgb(18, 20, 26);
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        MinimizeBox = false;

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(18, 20, 26),
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 9.75f),
            WordWrap = false,
            DetectUrls = false,
        };
        try
        {
            rtb.Text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            rtb.Text = "教程文件读取失败: " + ex.Message + "\n\n请直接用记事本打开程序目录下的《使用教程.txt》。";
        }

        var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel, Padding = new Padding(0, 7, 12, 7) };
        var btnClose = new Button { Text = "关闭", Width = 88, Height = 30 };
        var btnNote = new Button { Text = "在记事本中打开", Width = 130, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        UiHelper.Style(btnClose); UiHelper.Style(btnNote);
        btnClose.Click += (_, _) => Close();
        btnNote.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "打开记事本失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        flow.Controls.Add(btnClose); flow.Controls.Add(btnNote);

        Controls.Add(rtb);
        Controls.Add(flow);
    }
}

// ---------------- 游戏选择器（本地过滤：AppID / 粘贴或拖入商店链接） ----------------
internal class GameChooser : Form
{
    public long PickId;
    public string PickName;
    private readonly List<(long id, string name)> games;
    private List<(long id, string name)> filtered = new();
    private readonly ListBox list = new();
    private readonly TextBox txtFilter = new();
    private readonly Label lblCaption;
    private readonly string baseCaption;

    public GameChooser(List<(long id, string name)> games, string caption = null)
    {
        this.games = games;
        baseCaption = caption ?? " 搜索到多个匹配，请选择：";
        Text = "选择游戏";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(520, 620);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        // 顶部：说明一行 + 过滤框一行（各自独立，互不挤压）
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Panel,
            Padding = new Padding(8, 8, 8, 4),
        };
        lblCaption = new Label
        {
            Text = baseCaption,
            AutoSize = true,
            ForeColor = Theme.Sub,
            Margin = new Padding(0, 2, 0, 2),
        };
        txtFilter.Width = 320;
        txtFilter.PlaceholderText = "AppID / 粘贴商店链接，或直接拖入";
        txtFilter.BackColor = Theme.Input; txtFilter.ForeColor = Theme.Text; txtFilter.BorderStyle = BorderStyle.FixedSingle;
        txtFilter.Margin = new Padding(0, 4, 0, 2);
        txtFilter.TextChanged += (_, _) => ApplyFilter();
        top.Controls.Add(lblCaption);
        top.Controls.Add(txtFilter);

        list.Dock = DockStyle.Fill;
        list.BackColor = Color.FromArgb(18, 20, 26);
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.None;
        list.DoubleClick += (_, _) => Accept();

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Theme.Panel,
            Padding = new Padding(0, 6, 12, 6),
        };
        var btnOk = new Button { Text = "确定", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Padding = new Padding(8, 0, 8, 0) };
        var btnNo = new Button { Text = "取消", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 8, 0) };
        UiHelper.Style(btnOk, true); UiHelper.Style(btnNo);
        btnOk.Click += (_, _) => Accept();
        btnNo.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        flow.Controls.Add(btnNo); flow.Controls.Add(btnOk);

        Controls.Add(list);
        Controls.Add(top);
        Controls.Add(flow);

        // 拖入商店链接 / [appid] 文件夹 → 提取 AppID 过滤
        AllowDrop = true;
        list.AllowDrop = true;
        txtFilter.AllowDrop = true;
        DragEnter += DragEntry;
        list.DragEnter += DragEntry;
        txtFilter.DragEnter += DragEntry;
        DragDrop += DropEntry;
        list.DragDrop += DropEntry;
        txtFilter.DragDrop += DropEntry;

        ApplyFilter();
        lblCaption.Text = baseCaption + $"（共 {filtered.Count} 项）";
    }

    void DragEntry(object _, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effect = DragDropEffects.Copy;
    }

    void DropEntry(object _, DragEventArgs e)
    {
        try
        {
            long? id = null;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files is { Length: > 0 }) id = Store.ExtractAppIdFromPath(files[0]);
            }
            if (id == null)
            {
                string t = e.Data.GetData(DataFormats.Text) as string
                           ?? e.Data.GetData(DataFormats.StringFormat) as string;
                id = Store.ExtractAppId(t);
            }
            if (id != null)
            {
                txtFilter.Text = id.ToString();   // 触发 ApplyFilter
                Activate();
            }
        }
        catch { }
    }

    private void ApplyFilter()
    {
        string q = txtFilter.Text.Trim();
        if (q.Length == 0)
        {
            filtered = games;
            lblCaption.Text = baseCaption + $"（共 {filtered.Count} 项）";
            lblCaption.ForeColor = Theme.Sub;
        }
        else
        {
            long? linkId = Store.ExtractAppId(q);   // 支持商店链接 / steam:// / 纯 AppID
            filtered = linkId != null
                ? games.Where(g => g.id == linkId.Value).ToList()
                : games.Where(g =>
                    g.id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (g.name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            if (linkId != null && filtered.Count == 0)
            {
                lblCaption.Text = $" {linkId} 不在你拥有的列表里（家庭共享/未入库的游戏无法提取）";
                lblCaption.ForeColor = Theme.Red;
            }
            else
            {
                lblCaption.Text = baseCaption + $"（匹配 {filtered.Count} 项）";
                lblCaption.ForeColor = Theme.Sub;
            }
        }
        Render();
    }

    private void Render()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var g in filtered)
            list.Items.Add(g.id + "  —  " + g.name);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.EndUpdate();
    }

    private void Accept()
    {
        if (list.SelectedIndex < 0 || list.SelectedIndex >= filtered.Count) return;
        PickId = filtered[list.SelectedIndex].id;
        PickName = filtered[list.SelectedIndex].name;
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ---------------- 按钮样式小助手 ----------------
internal static class UiHelper
{
    public static void Style(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.FlatAppearance.MouseDownBackColor = Theme.Active;
        b.BackColor = primary ? Theme.Accent : Theme.Input;
        b.ForeColor = primary ? Color.White : Theme.Text;
        b.Cursor = Cursors.Hand;
    }
}

// ---------------- 清单收集：扫描本地清单集 → 上传收集仓库；登录 Steam → 提取已拥有游戏清单 ----------------
internal class CollectorDialog : Form
{
    private readonly Store store;
    private readonly SteamExtractor extractor = SteamExtractor.Default;

    private readonly TextBox txtDir = new();
    private readonly DataGridView grid = new();
    private readonly RichTextBox log = new();
    private readonly Label lblTokenHint = new();
    private readonly Label lblSteam = new();
    private readonly Button btnScan = new() { Text = "扫描" };
    private readonly Button btnUpload = new() { Text = "上传选中" };
    private readonly Button btnStop = new() { Text = "停止", Enabled = false };
    private readonly Button btnExtract = new() { Text = "从 Steam 提取" };
    private readonly Button btnOwned = new() { Text = "从已拥有列表选" };
    private readonly Button btnSteam = new() { Text = "登录 Steam" };

    // Steam 登录行（未登录且需要登录时显示；不用 GroupBox，避免 AutoSize 裁切）
    private readonly FlowLayoutPanel rowLogin = new();
    private readonly TextBox txtUser = new();
    private readonly TextBox txtPass = new();
    private readonly Button btnDoLogin = new() { Text = "登录" };

    private CancellationTokenSource cts;
    private bool busy;

    /// <summary>提取完成后回调主窗体（刷新列表 + 记日志）。</summary>
    public Action<long, string> OnExtracted;
    /// <summary>上传批次结束后回调主窗体（成功数, 失败/冲突数）。</summary>
    public Action<int, int> OnUploadDone;

    public CollectorDialog(Store store)
    {
        this.store = store;
        Text = "清单收集 — 扫描 / 上传 / Steam 提取";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 680);
        MinimumSize = new Size(780, 560);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        // 恢复记忆的窗口大小
        if (store.S.CollectWinW >= MinimumSize.Width && store.S.CollectWinH >= MinimumSize.Height)
            Size = new Size(store.S.CollectWinW, store.S.CollectWinH);

        BuildUi();

        txtDir.Text = store.S.CollectDirEff;
        UpdateTokenHint();
        UpdateSteamUi();
        extractor.StateChanged += OnSteamStateChanged;
        // 打开即自动扫描上次目录，免去手点
        Shown += (_, _) => ScanDir();
        FormClosing += (_, _) =>
        {
            store.S.CollectWinW = Width;
            store.S.CollectWinH = Height;
            store.SaveSettings();
            cts?.Cancel();
            extractor.StateChanged -= OnSteamStateChanged;
        };
    }

    void BuildUi()
    {
        // 本地助手：按钮全部 AutoSize（同主窗体 ToolBtn 模式），任何 DPI 缩放都不裁字
        void AutoSizeBtn(Button b, bool primary = false)
        {
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(0, 32);
            b.Padding = new Padding(8, 0, 8, 0);
            UiHelper.Style(b, primary);
        }

        // ---------- 顶部：目录行 + Steam 行（FlowLayoutPanel 自适应高度） ----------
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Panel,
            Padding = new Padding(10, 8, 10, 2),
        };
        var row1 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        var row2 = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 0) };

        var lblDir = new Label { Text = "收集目录:", AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 10, 8, 0) };
        txtDir.Width = 340;
        txtDir.BackColor = Theme.Input; txtDir.ForeColor = Theme.Text; txtDir.BorderStyle = BorderStyle.FixedSingle;
        txtDir.Margin = new Padding(0, 6, 8, 0);
        var btnBrowse = new Button { Text = "浏览" };
        AutoSizeBtn(btnBrowse);
        btnBrowse.Margin = new Padding(0, 3, 8, 0);
        btnBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "选择清单收集目录" };
            if (Directory.Exists(txtDir.Text)) d.SelectedPath = txtDir.Text;
            if (d.ShowDialog(this) == DialogResult.OK) { txtDir.Text = d.SelectedPath; PersistDir(); ScanDir(); }
        };
        AutoSizeBtn(btnScan, true);
        btnScan.Margin = new Padding(0, 3, 10, 0);
        btnScan.Click += (_, _) => ScanDir();
        row1.Controls.AddRange(new Control[] { lblDir, txtDir, btnBrowse, btnScan });

        lblSteam.AutoSize = true;
        lblSteam.ForeColor = Theme.Sub;
        lblSteam.Margin = new Padding(0, 10, 10, 0);
        AutoSizeBtn(btnSteam);
        btnSteam.Margin = new Padding(0, 3, 8, 0);
        btnSteam.Click += (_, _) => ToggleSteamPanel();
        AutoSizeBtn(btnExtract, true);
        btnExtract.Margin = new Padding(0, 3, 8, 0);
        btnExtract.Click += async (_, _) => await ExtractAsync();
        AutoSizeBtn(btnOwned);
        btnOwned.Margin = new Padding(0, 3, 0, 0);
        btnOwned.Click += async (_, _) => await ChooseOwnedAsync();
        row2.Controls.AddRange(new Control[] { lblSteam, btnSteam, btnExtract, btnOwned });

        // Steam 登录行（未登录且点「登录 Steam」/需要登录时展开）
        rowLogin.FlowDirection = FlowDirection.LeftToRight;
        rowLogin.WrapContents = false;
        rowLogin.AutoSize = true;
        rowLogin.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        rowLogin.Margin = new Padding(14, 2, 0, 0);
        rowLogin.Visible = false;
        var lblLoginTag = new Label { Text = "登录:", AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 10, 8, 0) };
        txtUser.Width = 180;
        txtUser.PlaceholderText = "账号";
        StyleBox(txtUser);
        txtUser.Margin = new Padding(0, 6, 8, 0);
        txtPass.Width = 180;
        txtPass.PlaceholderText = "密码";
        txtPass.PasswordChar = '●';
        StyleBox(txtPass);
        txtPass.Margin = new Padding(0, 6, 8, 0);
        AutoSizeBtn(btnDoLogin, true);
        btnDoLogin.Margin = new Padding(0, 3, 8, 0);
        btnDoLogin.Click += async (_, _) => await LoginAsync();
        var lblLoginNote = new Label { Text = "凭据只存本机，密码不留存", AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 10, 0, 0) };
        rowLogin.Controls.AddRange(new Control[] { lblLoginTag, txtUser, txtPass, btnDoLogin, lblLoginNote });

        // 令牌提示独立成行，避免挤在目录行右侧溢出
        lblTokenHint.AutoSize = true;
        lblTokenHint.ForeColor = Theme.Sub;
        lblTokenHint.Margin = new Padding(14, 4, 0, 0);

        top.Controls.Add(row1);
        top.Controls.Add(row2);
        top.Controls.Add(lblTokenHint);
        top.Controls.Add(rowLogin);

        // ---------- 网格 ----------
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Theme.Back;
        grid.BorderStyle = BorderStyle.None;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowHeadersVisible = false;
        grid.EnableHeadersVisualStyles = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.GridColor = Theme.Border;
        grid.RowTemplate.Height = 30;
        grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        // 复选框点击即时提交，保证外部读取单元格值可靠
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        // 点行上任意位置都切换勾选（复选框格交给内置切换，其余列手动翻转）
        grid.CellMouseClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex == 0) return;
            if (grid.Rows[e.RowIndex].Tag is not CollectSet s || !s.Recognized) return;
            if (grid.Rows[e.RowIndex].Cells[0] is DataGridViewCheckBoxCell { ReadOnly: false } cell)
                cell.Value = !(cell.Value is true);
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Sub,
            SelectionBackColor = Theme.Panel,
            SelectionForeColor = Theme.Sub,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Back,
            ForeColor = Theme.Text,
            SelectionBackColor = Color.FromArgb(43, 58, 90),
            SelectionForeColor = Theme.Text,
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "cCheck", HeaderText = "选", Width = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        grid.Columns.Add("cId", "AppID");
        grid.Columns.Add("cName", "名称");
        grid.Columns.Add("cSrc", "来源");
        grid.Columns.Add("cCount", "清单数");
        grid.Columns.Add("cVdf", "Key.vdf");
        grid.Columns.Add("cSize", "大小");
        grid.Columns.Add("cState", "状态");
        grid.Columns[1].FillWeight = 70;
        grid.Columns[2].FillWeight = 150;
        grid.Columns[3].FillWeight = 65;
        grid.Columns[4].FillWeight = 50;
        grid.Columns[5].FillWeight = 50;
        grid.Columns[6].FillWeight = 55;
        grid.Columns[7].FillWeight = 120;
        // 勾选标记用绿色平铺样式，深底上醒目
        if (grid.Columns[0] is DataGridViewCheckBoxColumn chk)
        {
            chk.FlatStyle = FlatStyle.Flat;
            chk.DefaultCellStyle.ForeColor = Theme.Green;
            chk.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        // 勾中的行整行加蓝色底色，选中状态一目了然
        grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            row.DefaultCellStyle.BackColor = row.Cells[0].Value is true ? Color.FromArgb(32, 48, 76) : Theme.Back;
        };

        // ---------- 底部按钮行 + 日志（AutoSize，任何 DPI 不裁字） ----------
        var btnAll = new Button { Text = "全选/全不选" };
        AutoSizeBtn(btnAll);
        var btnClose = new Button { Text = "关闭" };
        AutoSizeBtn(btnClose);
        var bottomFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 6, 12, 6),
        };
        AutoSizeBtn(btnUpload, true);
        AutoSizeBtn(btnStop);
        btnUpload.Margin = btnAll.Margin = new Padding(0, 0, 8, 0);
        btnStop.Margin = btnClose.Margin = new Padding(0, 0, 8, 0);
        btnUpload.Click += async (_, _) => await UploadSelectedAsync();
        btnAll.Click += (_, _) => ToggleAll();
        btnStop.Click += (_, _) => cts?.Cancel();
        btnClose.Click += (_, _) => Close();
        bottomFlow.Controls.Add(btnClose);
        bottomFlow.Controls.Add(btnStop);
        bottomFlow.Controls.Add(btnAll);
        bottomFlow.Controls.Add(btnUpload);

        log.Dock = DockStyle.Bottom;
        log.Height = 150;
        log.BackColor = Color.FromArgb(18, 20, 26);
        log.ForeColor = Theme.Text;
        log.Font = new Font("Microsoft YaHei UI", 9f);
        log.ReadOnly = true;
        log.BorderStyle = BorderStyle.None;

        Controls.Add(grid);
        Controls.Add(log);
        Controls.Add(bottomFlow);
        Controls.Add(top);
    }

    static void StyleBox(TextBox t)
    {
        t.BorderStyle = BorderStyle.FixedSingle;
        t.BackColor = Theme.Input;
        t.ForeColor = Theme.Text;
    }

    void UpdateTokenHint()
    {
        if (Collector.UploadToken != "")
        {
            lblTokenHint.Text = "✓ 上传令牌已内置";
            lblTokenHint.ForeColor = Theme.Green;
        }
        else
        {
            lblTokenHint.Text = "⚠ 未内置上传令牌，上传不可用 —— 见 Secrets.cs.example";
            lblTokenHint.ForeColor = Theme.Yellow;
        }
    }

    void UpdateSteamUi()
    {
        bool logged = extractor.IsLoggedOn;
        lblSteam.Text = logged ? "Steam: 已登录 " + extractor.Account : "Steam: 未登录";
        lblSteam.ForeColor = logged ? Theme.Green : Theme.Sub;
        btnSteam.Text = logged ? "切换账号" : "登录 Steam";
        rowLogin.Visible = !logged && rowLogin.Tag as string == "open";
        if (string.IsNullOrWhiteSpace(txtUser.Text))
            txtUser.Text = SteamAuthStore.Load()?.Account
                           ?? SteamAuthStore.DetectLastSteamAccount(store.SteamDirEff)
                           ?? "";
        txtPass.Clear();
    }

    void ToggleSteamPanel()
    {
        if (extractor.IsLoggedOn)
        {
            if (MessageBox.Show(this, "退出当前 Steam 登录并清除本机保存的凭据？（refresh token 将被删除）",
                    "切换账号", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            extractor.Logout();
            rowLogin.Tag = "open";
            UpdateSteamUi();
            AppendLog("已退出 Steam 登录。", Theme.Yellow);
            return;
        }
        rowLogin.Tag = rowLogin.Visible ? null : "open";
        UpdateSteamUi();
    }

    void OnSteamStateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => UpdateSteamUi()); } catch { }
    }

    // ---------- 扫描 ----------
    void PersistDir()
    {
        store.S.CollectDir = txtDir.Text.Trim();
        store.SaveSettings();
    }

    void ScanDir()
    {
        string dir = txtDir.Text.Trim();
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            MessageBox.Show(this, "目录不存在:\n" + dir, "清单收集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        PersistDir();
        var sets = Collector.ScanCollectionDir(dir);
        grid.Rows.Clear();
        foreach (var s in sets)
        {
            int i = grid.Rows.Add(
                false, // 默认不勾选，勾谁传谁
                s.Recognized ? s.AppId.ToString() : "—",
                s.DisplayName,
                string.IsNullOrEmpty(s.Source) ? "—" : s.Source,
                s.Manifests.Count.ToString(),
                s.VdfLocal != null ? "✓" : "—",
                s.TotalBytes >= 1024 * 1024 ? (s.TotalBytes / 1024.0 / 1024).ToString("F1") + " MB" : (s.TotalBytes / 1024.0).ToString("F0") + " KB",
                s.Recognized ? "待上传" : s.Note);
            grid.Rows[i].Tag = s;
            grid.Rows[i].Cells[3].Style.ForeColor = string.IsNullOrEmpty(s.Source) ? Theme.Sub : Theme.Green;
            if (!s.Recognized)
            {
                grid.Rows[i].Cells[0].Value = false;
                grid.Rows[i].Cells[0].ReadOnly = true;
                grid.Rows[i].Cells[7].Style.ForeColor = Theme.Sub;
            }
        }
        int ok = sets.Count(s => s.Recognized);
        int bad = sets.Count - ok;
        AppendLog($"扫描完成: 发现 {sets.Count} 个清单集（{ok} 个可上传" + (bad > 0 ? $"，{bad} 个无法识别 AppID" : "") + "）。", Theme.Accent);
        if (sets.Count == 0)
            AppendLog("该目录下没有找到清单集。支持 [AppID] 文件夹 / 散装 *.manifest + Key.vdf / 含 <AppID>.lua 的文件夹。", Theme.Sub);
    }

    void ToggleAll()
    {
        bool anyUnchecked = false;
        foreach (DataGridViewRow r in grid.Rows)
            if (r.Tag is CollectSet s && s.Recognized && (r.Cells[0] as DataGridViewCheckBoxCell)?.Value is not true)
                anyUnchecked = true;
        foreach (DataGridViewRow r in grid.Rows)
            if (r.Tag is CollectSet s && s.Recognized)
                r.Cells[0].Value = anyUnchecked;
    }

    void AppendLog(string line, Color c)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = c;
            log.AppendText(line + "\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }
        catch { }
    }

    Action<string, LogKind> ExtractLog() => (t, k) =>
    {
        var c = k switch
        {
            LogKind.Ok => Theme.Green,
            LogKind.Warn => Theme.Yellow,
            LogKind.Err => Theme.Red,
            LogKind.Tool => Theme.ToolC,
            _ => Theme.Text,
        };
        try { BeginInvoke(() => AppendLog("  " + t, c)); } catch { }
    };

    void SetBusy(bool on)
    {
        busy = on;
        btnScan.Enabled = btnUpload.Enabled = btnExtract.Enabled = btnOwned.Enabled =
        btnSteam.Enabled = btnDoLogin.Enabled = txtUser.Enabled = txtPass.Enabled = txtDir.Enabled = !on;
        btnStop.Enabled = on;
    }

    void SetRowState(DataGridViewRow row, string state, Color color)
    {
        row.Cells[7].Value = state;
        row.Cells[7].Style.ForeColor = color;
    }

    // ---------- 上传 ----------
    async Task UploadSelectedAsync()
    {
        if (busy) return;
        var targets = grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Tag is CollectSet s && s.Recognized && (r.Cells[0] as DataGridViewCheckBoxCell)?.Value is true)
            .ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "请先扫描并勾选要上传的清单集。", "清单收集", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (Collector.UploadToken == "")
        {
            MessageBox.Show(this,
                "当前构建没有内置上传令牌。\n\n维护者按 Secrets.cs.example 配好令牌重新编译后，上传即可用（普通用户无感）。",
                "上传", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"将把 {targets.Count} 个清单集上传到 {Collector.RepoSlug}。\n已有分支只提交差异，不删除远端文件。\n\n继续？",
                "上传清单", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        SetBusy(true);
        cts = new CancellationTokenSource();
        int ok = 0, conflict = 0, fail = 0;
        try
        {
            AppendLog($"== 开始上传 {targets.Count} 个清单集 → {Collector.RepoSlug}", Theme.Accent);
            try
            {
                await Collector.EnsureRepoAsync(store.ProxyEff);
                AppendLog("✓ 令牌与仓库校验通过。", Theme.Green);
            }
            catch (Exception ex)
            {
                AppendLog("✕ " + ex.Message, Theme.Red);
                return; // abort batch
            }

            foreach (var row in targets)
            {
                var s = (CollectSet)row.Tag;
                cts.Token.ThrowIfCancellationRequested();
                SetRowState(row, "上传中…", Theme.Yellow);
                try
                {
                    var r = await Collector.UploadSetAsync(store, s, t => AppendLog("  " + t, Theme.ToolC), cts.Token);
                    if (r.Skipped) { SetRowState(row, "已是最新（跳过）", Theme.Sub); }
                    else
                    {
                        SetRowState(row, $"已上传 ✓（{r.CommitSha}）", Theme.Green);
                        ok++;
                    }
                }
                catch (Collector.ApiException aex) when (aex.Status == 401 || aex.Status == 403)
                {
                    SetRowState(row, "失败: " + aex.Message, Theme.Red);
                    AppendLog("✕ " + aex.Message + " 中止整批上传。", Theme.Red);
                    break;
                }
                catch (Collector.ApiException aex) when (aex.Status == 422)
                {
                    SetRowState(row, "冲突（远端已被他人更新）", Theme.Yellow);
                    conflict++;
                    AppendLog($"  ⚠ {s.AppId}: 分支在期间被他人更新，跳过（重新扫描后可再传）。", Theme.Yellow);
                }
                catch (Exception ex)
                {
                    fail++;
                    SetRowState(row, "失败: " + ex.Message, Theme.Red);
                    AppendLog($"  ✕ {s.AppId}: {ex.Message}", Theme.Red);
                }
            }
            AppendLog($"== 上传结束: 成功 {ok}，冲突 {conflict}，失败 {fail}。", ok + conflict + fail > 0 ? Theme.Accent : Theme.Sub);
        }
        catch (OperationCanceledException)
        {
            AppendLog("已手动取消。", Theme.Yellow);
        }
        finally
        {
            SetBusy(false);
            OnUploadDone?.Invoke(ok, conflict + fail);
        }
    }

    // ---------- Steam 登录 ----------
    async Task LoginAsync()
    {
        if (busy) return;
        string user = txtUser.Text.Trim(), pass = txtPass.Text;
        if (user.Length == 0 || pass.Length == 0)
        {
            MessageBox.Show(this, "请输入账号与密码。", "Steam 登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true);
        cts = new CancellationTokenSource();
        try
        {
            await extractor.InteractiveLogOnAsync(user, pass, new UiAuthenticator(this), cts.Token, ExtractLog());
            AppendLog("✓ Steam 登录完成。", Theme.Green);
        }
        catch (OperationCanceledException) { AppendLog("已取消登录。", Theme.Yellow); }
        catch (NeedsLoginException) { /* 凭据失效已清除，面板保持展开 */ }
        catch (Exception ex) { AppendLog("✕ 登录失败: " + ex.Message, Theme.Red); }
        finally { SetBusy(false); txtPass.Clear(); }
    }

    // ---------- 提取 ----------
    async Task ExtractAsync()
    {
        if (busy) return;
        string input = InputDialog.Show(this, "从 Steam 提取清单",
            "输入 AppID / 游戏名 / Steam 商店链接（仅限当前账号已拥有的游戏）:", "");
        if (string.IsNullOrWhiteSpace(input)) return;

        long appid;
        var linkId = Store.ExtractAppId(input);
        if (linkId != null) appid = linkId.Value;
        else if (long.TryParse(input.Trim(), out var n) && input.Trim().Length >= 4) appid = n;
        else
        {
            // 游戏名搜索（与主窗体拉取一致的 UX）
            AppendLog("搜索 Steam 商店: " + input, Theme.ToolC);
            List<(long id, string name)> matches;
            try { matches = await Store.NativePuller.SearchGamesAsync(input, store.ProxyEff); }
            catch (Exception ex) { AppendLog("✕ 搜索失败: " + ex.Message, Theme.Red); return; }
            if (matches.Count == 0) { AppendLog("✕ 未搜索到匹配的游戏（可尝试英文名或直接输 AppID）。", Theme.Yellow); return; }
            if (matches.Count == 1) { appid = matches[0].id; }
            else
            {
                using var chooser = new GameChooser(matches.Take(15).ToList());
                if (chooser.ShowDialog(this) != DialogResult.OK) return;
                appid = chooser.PickId;
            }
        }

        await RunExtractAsync(appid);
    }

    /// <summary>提取的共用执行段（输入解析后与已拥有列表选中后共用）。</summary>
    async Task RunExtractAsync(long appid)
    {
        cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            AppendLog($"== 从 Steam 提取 {appid} …", Theme.Accent);
            var r = await extractor.ExtractAsync(appid, store.S.GamerToolDir, cts.Token, ExtractLog());
            AfterExtract(r);
        }
        catch (NeedsLoginException)
        {
            rowLogin.Tag = "open";
            UpdateSteamUi();
            AppendLog("⚠ 需要先登录 Steam（在上方输入账号密码）。", Theme.Yellow);
        }
        catch (OperationCanceledException) { AppendLog("已手动取消提取。", Theme.Yellow); }
        catch (Exception ex)
        {
            AppendLog("✕ 提取失败: " + ex.Message, Theme.Red);
            var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(frame)) AppendLog("  at " + frame, Theme.ToolC);
        }
        finally { SetBusy(false); }
    }

    void AfterExtract(SteamExtractor.ExtractResult r)
    {
        AppendLog($"✓ 提取完成: {r.Dir}", Theme.Green);
        // 提取产物在 GamerTool 目录；收集目录相同则自动重扫，否则提示
        if (string.Equals(store.S.CollectDirEff, store.S.GamerToolDir, StringComparison.OrdinalIgnoreCase))
        {
            txtDir.Text = store.S.CollectDirEff;
            ScanDir();
        }
        else
        {
            AppendLog("提示: 提取产物在 GamerTool 目录，把收集目录切到那里扫描即可上传。", Theme.Sub);
        }
        OnExtracted?.Invoke(r.AppId, r.Dir);
    }

    // ---------- 已拥有列表 ----------
    async Task ChooseOwnedAsync()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            var owned = await extractor.GetOwnedAppsAsync(CancellationToken.None, ExtractLog());
            // 名称按需获取：列表默认显示 AppID，输游戏名按回车联网搜索后自动记住
            var games = owned.OrderBy(id => id)
                .Select(id => ((long)id, store.Records.TryGetValue(id, out var r) ? (r.Name ?? r.FetchedName) ?? "(未登记名)" : "(未登记名)"))
                .ToList();
            if (games.Count == 0) { AppendLog("已拥有列表为空。", Theme.Yellow); return; }
            using var chooser = new GameChooser(games, "选择要提取的游戏（粘贴/拖入商店链接，或输 AppID）：");
            if (chooser.ShowDialog(this) == DialogResult.OK)
            {
                // 搜到的名字记进 games.json，下次本地过滤直接命中
                if (!store.Records.TryGetValue(chooser.PickId, out var rec))
                    store.Records[chooser.PickId] = rec = new GameRecord { AppId = chooser.PickId };
                if (!string.IsNullOrEmpty(chooser.PickName) && chooser.PickName != "(未登记名)")
                    rec.FetchedName ??= chooser.PickName;
                store.SaveRecords();
                await RunExtractAsync(chooser.PickId);
            }
        }
        catch (NeedsLoginException)
        {
            rowLogin.Tag = "open";
            UpdateSteamUi();
            AppendLog("⚠ 需要先登录 Steam（在上方输入账号密码）。", Theme.Yellow);
        }
        catch (Exception ex) { AppendLog("✕ 获取已拥有列表失败: " + ex.Message, Theme.Red); }
        finally { SetBusy(false); }
    }

}

// ---------------- Steam Guard UI 适配器：SteamKit 在任意线程回调，这里转到 UI 线程弹窗 ----------------
internal class UiAuthenticator : IAuthenticator
{
    private readonly Form owner;
    public UiAuthenticator(Form owner) { this.owner = owner; }

    Task<T> OnUi<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        OnUi(() => InputDialog.Show(owner, "Steam 令牌",
            "输入手机 Steam 令牌的动态验证码" + (previousCodeWasIncorrect ? "（上次验证码错误，请重输）" : "") + ":", ""));

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        OnUi(() => InputDialog.Show(owner, "Steam Guard",
            $"输入发送到 {email} 的邮件验证码" + (previousCodeWasIncorrect ? "（上次验证码错误，请重输）" : "") + ":", ""));

    public Task<bool> AcceptDeviceConfirmationAsync() =>
        OnUi(() => MessageBox.Show(owner,
            "请在手机 Steam 上确认本次登录，完成后点“确定”。",
            "手机确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK);
}
