using System.Diagnostics;

namespace GamerToolManager;

// ---------------- 通用输入框 ----------------
internal static class InputDialog
{
    public static string Show(IWin32Window owner, string title, string label, string defaultValue)
    {
        string result = null;
        using var f = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(420, 132),
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = new Font("Microsoft YaHei UI", 9.5f),
        };
        var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 10, 10, 0), ForeColor = Theme.Sub };
        var txt = new TextBox
        {
            Text = defaultValue,
            Dock = DockStyle.Top,
            Font = new Font("Microsoft YaHei UI", 11f),
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(10, 0, 10, 0),
        };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel };
        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 88, Height = 30 };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88, Height = 30, Margin = new Padding(0, 7, 8, 0) };
        btnOk.FlatAppearance.BorderSize = 0; btnOk.FlatStyle = FlatStyle.Flat; btnOk.BackColor = Theme.Accent; btnOk.ForeColor = Color.White;
        btnCancel.FlatAppearance.BorderSize = 0; btnCancel.FlatStyle = FlatStyle.Flat; btnCancel.BackColor = Theme.Input; btnCancel.ForeColor = Theme.Text;
        flow.Controls.Add(btnCancel); flow.Controls.Add(btnOk);
        f.Controls.Add(flow); f.Controls.Add(txt); f.Controls.Add(lbl);
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
            AppendLog("✓ 拉取完成，生成游戏文件夹: " + string.Join(", ", fresh.Select(id => "[" + id + "]")), Theme.Green);
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

// ---------------- 游戏选择器（搜索到多个匹配时） ----------------
internal class GameChooser : Form
{
    public long PickId;
    public string PickName;
    private readonly ListBox list = new();

    public GameChooser(List<(long id, string name)> games)
    {
        Text = "选择游戏";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(480, 380);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Text = " 搜索到多个匹配，请选择：",
            ForeColor = Theme.Sub,
            BackColor = Theme.Panel,
            Padding = new Padding(8, 8, 0, 0),
        };
        list.Dock = DockStyle.Fill;
        list.BackColor = Color.FromArgb(18, 20, 26);
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.None;
        foreach (var g in games)
            list.Items.Add(g.id + "  —  " + g.name);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) { Accept(); } };

        var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel, Padding = new Padding(0, 7, 12, 7) };
        var btnOk = new Button { Text = "确定", Width = 88, Height = 28 };
        var btnNo = new Button { Text = "取消", Width = 88, Height = 28, Margin = new Padding(0, 0, 8, 0) };
        UiHelper.Style(btnOk, true); UiHelper.Style(btnNo);
        btnOk.Click += (_, _) => Accept();
        btnNo.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        flow.Controls.Add(btnNo); flow.Controls.Add(btnOk);

        Controls.Add(list);
        Controls.Add(lbl);
        Controls.Add(flow);

        void Accept()
        {
            if (list.SelectedIndex < 0) return;
            var g = games[list.SelectedIndex];
            PickId = g.id;
            PickName = g.name;
            DialogResult = DialogResult.OK;
            Close();
        }
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
