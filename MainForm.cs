using System.Diagnostics;

namespace GamerToolManager;

public class MainForm : Form
{
    private readonly Store store = new();
    private List<GameEntry> entries = new();
    private bool building;
    private bool fetchingNames;
    private int regSort; // 0=默认(已部署优先) 1=注册时间最新在上 2=最旧在上（启动时从设置恢复）

    // 控件
    private DataGridView grid = new();
    private SplitContainer scTop = new();
    private SplitContainer scMain = new();
    private readonly TextBox txtPull = new();
    private Button btnPullGo;
    private int activePulls;
    private readonly HashSet<long> busyApps = new();
    private readonly TextBox txtSearch = new();
    private readonly RichTextBox rtbLog = new();
    private readonly ToolStripStatusLabel lblStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Panel pnlDetails = new();
    private readonly Label lblBig = new();
    private readonly Label lblInfo = new();
    private readonly Label lblHint = new();
    private Button btnOpenFolder, btnDeploy, btnRestore, btnLocate, btnDelLua, btnDelFolder, btnRename, btnFetch, btnRePull, btnUploadCollect;

    public MainForm()
    {
        store.Load(); // 必须在最前：窗口尺寸恢复依赖已加载的设置
        regSort = Math.Clamp(store.S.RegSort, 0, 2);
        Text = "GamerTool 管理器 v" + typeof(MainForm).Assembly.GetName().Version.ToString(3);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1140, 720);
        Font = new Font("Microsoft YaHei UI", 10f);
        // 恢复记忆的窗口大小（没有记忆或小于最小值时用默认）
        if (store.S.WinW >= MinimumSize.Width && store.S.WinH >= MinimumSize.Height)
            Size = new Size(store.S.WinW, store.S.WinH);
        else
            Size = new Size(1320, 810);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        DoubleBuffered = true;

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BuildUi();
        RefreshAll();

        // 代理探活/漂移自愈通知写进运行日志
        Store.ProxyNotice += msg => Log(msg,
            msg.StartsWith("⚠") ? LogKind.Warn : msg.StartsWith("✓") ? LogKind.Ok : LogKind.Info);

        Load += (_, _) =>
        {
            // 控件有实际尺寸后再设分割位置：先恢复最大化，再按当前宽度收敛记忆值
            try
            {
                if (store.S.WinMax)
                    WindowState = FormWindowState.Maximized;

                int defTop = Math.Max(scTop.Panel1MinSize, scTop.Width - (int)(400 * DeviceDpi / 96f));
                if (store.S.SplitTop > 0)
                    scTop.SplitterDistance = Math.Clamp(store.S.SplitTop, 150, Math.Max(300, scTop.Width - 150));
                else
                    scTop.SplitterDistance = defTop;

                int defMain = Math.Max(scMain.Panel1MinSize, scMain.Height - 175);
                if (store.S.SplitMain > 0)
                    scMain.SplitterDistance = Math.Clamp(store.S.SplitMain, 80, Math.Max(120, scMain.Height - 80));
                else
                    scMain.SplitterDistance = defMain;
            }
            catch { }

            if (store.S.AutoFetchNames)
                _ = FetchMissingNamesAsync(silent: true);
            // 首次运行引导：未装 SteamTools 且自带了安装包 → 主动提示
            if (!Store.SteamToolsInstalled && Store.BundledSteamToolsSetup() != null)
            {
                if (MessageBox.Show(this,
                        "未检测到 SteamTools（解锁依赖它保持运行）。\n\n是否现在运行程序自带的 SteamTools 安装包？",
                        "安装 SteamTools", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    RunSteamToolsInstaller();
                }
            }
        };

        // 关闭时记忆布局：窗口宽高、最大化的判定、两个分割位置
        FormClosing += (_, e) =>
        {
            SteamExtractor.Default.Disconnect();
            try
            {
                if (WindowState == FormWindowState.Normal)
                {
                    store.S.WinW = Width;
                    store.S.WinH = Height;
                }
                store.S.WinMax = WindowState == FormWindowState.Maximized;
                store.S.SplitTop = scTop.SplitterDistance;
                store.S.SplitMain = scMain.SplitterDistance;
                store.SaveSettings();
            }
            catch { }
        };
    }

    // ================= 界面搭建 =================
    private void BuildUi()
    {
        // ---------- 顶部工具栏（流式布局，自动排布不重叠） ----------
        var top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Panel };

        var title = new Label
        {
            Text = " GamerTool 管理器",
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 16, 0),
        };

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel,
            Padding = new Padding(0, 16, 0, 0),
        };
        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel,
            Padding = new Padding(0, 16, 12, 0),
        };

        Button ToolBtn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 34), Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0) };
            UiHelper.Style(b, false);
            b.Click += onClick;
            leftFlow.Controls.Add(b);
            return b;
        }

        ToolBtn("重启 Steam", async (_, _) => await PromptSteamRestartAsync("手动重启 Steam。"));
        ToolBtn("刷新", (_, _) => RefreshAll());
        ToolBtn("SteamTools", (_, _) => SteamToolsButton());
                ToolBtn("使用教程", (_, _) => ShowTutorial());
        ToolBtn("清单收集", (_, _) => ShowCollector());
        ToolBtn("设置", (_, _) => ShowSettings());

        var moreBtn = new Button
        {
            Text = "更多 ▾",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 34),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 14, 0),
        };
        UiHelper.Style(moreBtn, false);
        moreBtn.Click += (s, _) =>
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Theme.Panel, ForeColor = Theme.Text };
            var i0 = new ToolStripMenuItem("还原全部 —— 把所有已部署游戏一键还原成未破解") { ForeColor = Theme.Yellow, BackColor = Theme.Panel };
            i0.Click += async (s3, e3) => await RestoreAllAsync();
            var i1 = new ToolStripMenuItem("抓取全部名字 —— 给没名字的游戏联网补中文名") { ForeColor = Theme.Text, BackColor = Theme.Panel };
            i1.Click += async (s2, e2) => await FetchMissingNamesAsync(silent: false);
            var i2 = new ToolStripMenuItem("孤儿登记 —— 清理 appids.db 中的残留登记") { ForeColor = Theme.Text, BackColor = Theme.Panel };
            i2.Click += (_, _) => ShowOrphans();
            menu.Items.Add(i0);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(i1);
            menu.Items.Add(i2);
            menu.Show((Control)s, new Point(0, ((Control)s).Height));
        };

        var search = new Label { Text = "搜索:", AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 8, 8, 0) };
        txtSearch.BackColor = Theme.Input; txtSearch.ForeColor = Theme.Text;
        txtSearch.BorderStyle = BorderStyle.FixedSingle;
        txtSearch.Width = 190;
        txtSearch.Margin = new Padding(0, 5, 0, 0);
        rightFlow.Controls.Add(moreBtn);
        rightFlow.Controls.Add(search);
        rightFlow.Controls.Add(txtSearch);
        txtSearch.TextChanged += (_, _) => RebuildGrid();

        top.Controls.Add(rightFlow);
        top.Controls.Add(leftFlow);
        top.Controls.Add(title);

        // ---------- 拉取栏（流式布局，输入框固定宽度，任何缩放下按钮都完整可见） ----------
        var pullBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Panel };
        var pullFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(14, 15, 0, 0),
        };
        var lblPull = new Label { Text = "拉取新游戏:", AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(0, 8, 10, 0) };
        txtPull.Font = new Font("Microsoft YaHei UI", 10.5f);
        txtPull.BackColor = Theme.Input; txtPull.ForeColor = Theme.Text; txtPull.BorderStyle = BorderStyle.FixedSingle;
        txtPull.Width = 420; // 固定宽度，不随窗口伸缩
        txtPull.Margin = new Padding(0, 0, 10, 0);
        txtPull.PlaceholderText = "AppID / 游戏名 / 粘贴或拖入 Steam 链接";
        btnPullGo = new Button { Text = "开始拉取", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 32), Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 0, 0) };
        UiHelper.Style(btnPullGo, true);
        btnPullGo.Click += async (_, _) => await PullFromMainAsync();
        txtPull.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await PullFromMainAsync(); }
        };
        pullFlow.Controls.Add(lblPull);
        pullFlow.Controls.Add(txtPull);
        pullFlow.Controls.Add(btnPullGo);
        pullBar.Controls.Add(pullFlow);

        // ---------- 主区 ----------
        scTop = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            BackColor = Theme.Back,
        };
        grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Back,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            GridColor = Theme.Border,
        };
        grid.RowTemplate.Height = 32;
        grid.ColumnHeadersHeight = 36;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Sub,
            SelectionBackColor = Theme.Panel,
            SelectionForeColor = Theme.Sub,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Padding = new Padding(4, 0, 0, 0),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Back,
            ForeColor = Theme.Text,
            SelectionBackColor = Color.FromArgb(43, 58, 90),
            SelectionForeColor = Theme.Text,
            Padding = new Padding(4, 0, 0, 0),
        };
        grid.Columns.Add("cName", "游戏名");
        grid.Columns.Add("cId", "AppID");
        grid.Columns.Add("cState", "Steam 注册");
        grid.Columns.Add("cReg", "注册时间");
        grid.Columns.Add("cFolder", "本地文件夹");
        grid.Columns.Add("cMani", "清单");
        grid.Columns[0].FillWeight = 200;
        grid.Columns[1].FillWeight = 75;
        grid.Columns[2].FillWeight = 88;
        grid.Columns[3].FillWeight = 100;
        grid.Columns[4].FillWeight = 80;
        grid.Columns[5].FillWeight = 50;
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (grid.Columns[e.ColumnIndex].Name == "cReg")
            {
                regSort = (regSort + 1) % 3; // 默认 → 最新在上 → 最旧在上
                store.S.RegSort = regSort;
                store.SaveSettings();
                RefreshAll();
            }
        };
        grid.SelectionChanged += (_, _) => { if (!building) UpdateDetails(); };
        grid.CellMouseDoubleClick += (s, e) =>
        {
            if (e.RowIndex < 0) return;
            if (grid.Rows[e.RowIndex].Tag is not GameEntry g) return;
            try
            {
                if (e.ColumnIndex == 1) // AppID 列 → 复制 AppID
                {
                    Clipboard.SetText(g.AppId.ToString());
                    Log("✓ 已复制 AppID: " + g.AppId, LogKind.Ok);
                }
                else // 其他列 → 复制游戏名
                {
                    Clipboard.SetText(g.DisplayName);
                    Log("✓ 已复制游戏名: " + g.DisplayName, LogKind.Ok);
                }
            }
            catch (Exception ex)
            {
                Log("✕ 复制失败: " + ex.Message, LogKind.Err);
            }
        };
        scTop.Panel1.Controls.Add(grid);

        // ---------- 右侧详情 ----------
        pnlDetails.Dock = DockStyle.Fill;
        pnlDetails.BackColor = Theme.Panel;
        pnlDetails.Padding = new Padding(14);

        lblBig.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        lblBig.ForeColor = Theme.Text;
        lblBig.Dock = DockStyle.Top;
        lblBig.AutoSize = true;

        lblInfo.Dock = DockStyle.Top;
        lblInfo.AutoSize = true;
        lblInfo.ForeColor = Theme.Sub;
        lblInfo.Font = new Font("Microsoft YaHei UI", 9.25f);
        lblInfo.Text = "在左侧选择一个游戏";

        lblHint.Dock = DockStyle.Top;
        lblHint.AutoSize = true;
        lblHint.ForeColor = Theme.Yellow;
        lblHint.Font = new Font("Microsoft YaHei UI", 8.75f);
        lblHint.Text = "";

        // 双列流式按钮：AutoSize 随文字/字体缩放自适应，放不下自动换行，任何 DPI 都不会挤在一起
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true, // 极端缩放下的最后防线（可滚动总比被裁好）
            BackColor = Theme.Panel,
            Padding = new Padding(0, 2, 0, 0),
        };
        Button ActBtn(string t, ref Button field, EventHandler onClick, bool danger = false)
        {
            var b = new Button
            {
                Text = t,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(150, 34), // 宽随文字增长，高随字体增长
                Margin = new Padding(2, 2, 2, 2),
            };
            UiHelper.Style(b, false);
            b.Font = new Font("Microsoft YaHei UI", 9f);
            if (danger)
            {
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(84, 42, 40);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(104, 48, 44);
            }
            b.Click += onClick;
            stack.Controls.Add(b);
            field = b;
            return b;
        }
        ActBtn("打开游戏文件夹", ref btnOpenFolder, (_, _) => OpenExplorer(Selected()?.Folder));
        ActBtn("部署到 Steam 解锁", ref btnDeploy, async (_, _) => await DeploySelectedAsync());
        ActBtn("重新拉取清单", ref btnRePull, async (_, _) => await RePullSelectedAsync());
        ActBtn("上传到收集库", ref btnUploadCollect, async (_, _) => await UploadSelectedToCollectAsync());
        ActBtn("还原未破解", ref btnRestore, async (_, _) => await RestoreSelectedAsync(), danger: true);
        ActBtn("定位已部署 Lua", ref btnLocate, (_, _) => LocateDeployed());
        ActBtn("删除 Lua 文件", ref btnDelLua, (_, _) => DeleteSourceLua(), danger: true);
        ActBtn("删除游戏文件夹", ref btnDelFolder, (_, _) => DeleteGameFolder(), danger: true);
        ActBtn("重命名", ref btnRename, (_, _) => RenameSelected());
        ActBtn("抓取游戏名", ref btnFetch, async (_, _) => await FetchSelectedNameAsync());

        pnlDetails.Controls.Add(stack);
        pnlDetails.Controls.Add(lblHint);
        pnlDetails.Controls.Add(lblInfo);
        pnlDetails.Controls.Add(lblBig);
        // 顺序: Top 的添加顺序决定从上往下堆叠（后加的更靠上?）——需要反转
        pnlDetails.Controls.SetChildIndex(stack, 0);
        pnlDetails.Controls.SetChildIndex(lblHint, 1);
        pnlDetails.Controls.SetChildIndex(lblInfo, 2);
        pnlDetails.Controls.SetChildIndex(lblBig, 3);
        scTop.Panel2.Controls.Add(pnlDetails);

        // ---------- 底部日志 ----------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 168, BackColor = Color.FromArgb(18, 20, 26), Padding = new Padding(0, 6, 0, 0) };
        var logHead = new Label { Text = " 运行日志", Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Sub, BackColor = Theme.Panel };
        rtbLog.Dock = DockStyle.Fill;
        rtbLog.BackColor = Color.FromArgb(18, 20, 26);
        rtbLog.ForeColor = Theme.Text;
        rtbLog.Font = new Font("Microsoft YaHei UI", 9f);
        rtbLog.ReadOnly = true;
        rtbLog.BorderStyle = BorderStyle.None;
        rtbLog.DetectUrls = false;
        bottom.Controls.Add(rtbLog);
        bottom.Controls.Add(logHead);

        scMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            BackColor = Theme.Border,
        };
        scMain.Panel1.Controls.Add(scTop);
        scMain.Panel2.Controls.Add(bottom);

        var status = new StatusStrip { BackColor = Theme.Panel, SizingGrip = true };
        status.Items.Add(lblStatus);
        lblStatus.ForeColor = Theme.Sub;

        Controls.Add(scMain);
        Controls.Add(status);
        Controls.Add(pullBar);
        Controls.Add(top);

        // ---------- 拖拽 Steam 链接 / [appid] 文件夹 → 识别后自动拉取 ----------
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            long? id = null;
            try
            {
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
            }
            catch { }
            if (id != null)
            {
                Log("拖入内容识别 AppID " + id + "，开始拉取…", LogKind.Info);
                txtPull.Text = id.ToString();
                _ = PullFromMainAsync();
            }
            else
            {
                MessageBox.Show(this,
                    "无法从拖入的内容识别 AppID。\n支持: Steam 商店链接 / steam:// 链接 / 纯 AppID 数字 / [AppID] 文件夹",
                    "拖拽拉取", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        Log("就绪。GamerTool: " + store.S.GamerToolDir + " | Steam: " + (store.SteamDirEff == "" ? "未检测到" : store.SteamDirEff), LogKind.Info);
        Log("提示: 把 Steam 商店链接拖进窗口（或复制粘贴到上方输入框）即可直接搜索拉取。", LogKind.Warn);
        if (store.SteamDirEff == "")
            Log("⚠ 未检测到 Steam 目录，部署/还原不可用 —— 请到【设置】手动指定。", LogKind.Warn);
    }

    // ================= 数据与刷新 =================
    private GameEntry Selected()
    {
        if (grid.CurrentRow?.Tag is GameEntry e) return e;
        int i = grid.CurrentRow?.Index ?? -1;
        if (i >= 0 && i < grid.Rows.Count && grid.Rows[i].Tag is GameEntry e2) return e2;
        return null;
    }

    private void RefreshAll(bool keepSelection = true)
    {
        long sel = Selected()?.AppId ?? -1;
        entries = store.Scan();
        RebuildGrid();
        if (keepSelection && sel >= 0)
        {
            foreach (DataGridViewRow r in grid.Rows)
                if (r.Tag is GameEntry e && e.AppId == sel) { r.Selected = true; break; }
        }
        UpdateStatus();
        UpdateDetails();
    }

    private void RebuildGrid()
    {
        building = true;
        string q = txtSearch.Text.Trim();
        var baseQ = entries.Where(e =>
        {
            if (q == "") return true;
            return e.AppId.ToString().Contains(q) ||
                   (e.CustomName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   (e.FetchedName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
        });
        var view = regSort switch
        {
            1 => baseQ.OrderByDescending(e => e.RegTime ?? DateTime.MinValue).ToList(),
            2 => baseQ.OrderBy(e => e.RegTime ?? DateTime.MaxValue).ToList(),
            _ => baseQ.OrderByDescending(e => e.Deployed).ThenBy(e => e.AppId).ToList(),
        };

        grid.Columns["cReg"].HeaderText = "注册时间" + (regSort == 1 ? " ↓" : regSort == 2 ? " ↑" : "");

        grid.Rows.Clear();
        foreach (var e in view)
        {
            int i = grid.Rows.Add(
                e.DisplayName,
                e.AppId.ToString(),
                e.Deployed ? "● 已部署" : "○ 未注册",
                e.RegTime.HasValue ? e.RegTime.Value.ToString("MM-dd HH:mm") : "—",
                e.FolderExists ? (e.Empty ? "空" : "✓") : "—",
                e.LocalManifests.Count.ToString());
            grid.Rows[i].Tag = e;
            grid.Rows[i].Cells[2].Style.ForeColor = e.Deployed ? Theme.Green : Theme.Sub;
            grid.Rows[i].Cells[3].Style.ForeColor = e.RegTime.HasValue ? Theme.Text : Theme.Sub;
            grid.Rows[i].Cells[4].Style.ForeColor = !e.FolderExists ? Theme.Sub : (e.Empty ? Theme.Red : Theme.Text);
            grid.Rows[i].Cells[4].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Rows[i].Cells[5].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        building = false;
        if (grid.Rows.Count > 0) grid.Rows[0].Selected = true;
        UpdateStatus();
        UpdateDetails();
    }

    private void UpdateStatus()
    {
        int deployed = entries.Count(e => e.Deployed);
        lblStatus.Text = $"  游戏 {entries.Count} 个 · Steam 已部署 {deployed} 个      " +
                          $"GamerTool: {store.S.GamerToolDir}      Steam: {(store.SteamDirEff == "" ? "未检测到" : store.SteamDirEff)}";
    }

    private void UpdateDetails()
    {
        var e = Selected();
        bool any = e != null;
        btnOpenFolder.Enabled = any && e.FolderExists;
        btnDeploy.Enabled = any && e.SourceLuaExists;
        btnRePull.Enabled = any && e.FolderExists && !AppBusy(e.AppId);
        btnUploadCollect.Enabled = any && e.FolderExists && (e.SourceLuaExists || e.LocalManifests.Count > 0);
        btnRestore.Enabled = any && (e.Deployed || (store.SteamDirEff != "" && store.RegisteredDbIds(e).Count > 0));
        btnLocate.Enabled = any && e.Deployed;
        btnDelLua.Enabled = any && e.SourceLuaExists;
        btnDelFolder.Enabled = any && e.FolderExists;
        btnRename.Enabled = any;
        btnFetch.Enabled = any && !fetchingNames;

        if (!any)
        {
            lblBig.Text = "未选择游戏";
            lblInfo.Text = "";
            lblHint.Text = "";
            return;
        }

        lblBig.Text = e.DisplayName;
        int dcFiles = store.DepotCacheFiles(e).Count;
        var dbIds = store.RegisteredDbIds(e);
        lblInfo.Text =
            "AppID:   " + e.AppId + "\n" +
            "本地:     " + (e.FolderExists ? e.Folder : "（游戏文件夹不存在）") + "\n" +
            "Lua:      " + (e.SourceLuaExists ? "✓ 已生成" : "— 未生成") + "\n" +
            "Steam:  " + (e.Deployed ? "● 已注册解锁" : "○ 未注册") + "\n" +
            "清单:     本地 " + e.LocalManifests.Count + " 个 · depotcache 占用 " + dcFiles + " 个\n" +
            "登记库:  appids.db 命中 " + dbIds.Count + " 行";

        lblHint.Text = "";
        if (e.Empty)
            lblHint.Text = "⚠ 拉取文件夹存在但未生成解锁 Lua（拉取失败或该游戏不在清单仓库）—— 无法注册，可删除文件夹后重新拉取。";
        else if (e.Deployed && !e.FolderExists)
            lblHint.Text = "该游戏已在 Steam 注册，但本地游戏文件夹已删除 —— 仍可直接【还原未破解】。";
        else if (!e.Deployed && e.FolderExists)
            lblHint.Text = "已拉取但未部署 —— 点击【部署到 Steam】注册解锁。";
    }

    // ================= SteamTools 检测 / 安装 =================
    private void SteamToolsButton()
    {
        if (Store.SteamToolsInstalled)
        {
            Log("✓ SteamTools 已安装: " + Store.SteamToolsExe, LogKind.Ok);
            OpenExplorer(Path.GetDirectoryName(Store.SteamToolsExe));
            return;
        }
        TryInstallSteamTools();
    }

    private void TryInstallSteamTools()
    {
        var setup = Store.BundledSteamToolsSetup();
        if (setup == null)
        {
            MessageBox.Show(this,
                "未检测到 SteamTools，且程序目录下没有内置安装包（bundled\\st-setup-*.exe）。\n请从 SteamTools 官网下载安装后，点【刷新】。",
                "SteamTools", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
                "将运行内置的 SteamTools 安装向导：\n" + setup + "\n\n安装完成后点【刷新】。",
                "安装 SteamTools", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        RunSteamToolsInstaller();
    }

    private void RunSteamToolsInstaller()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Store.BundledSteamToolsSetup())
            {
                UseShellExecute = true,
                Verb = "runas", // 安装器需要管理员权限
            });
            Log("已启动 SteamTools 安装向导，请在向导中完成安装。", LogKind.Ok);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log("已取消安装（UAC 被取消）。", LogKind.Warn);
        }
        catch (Exception ex)
        {
            Log("✕ 启动安装器失败: " + ex.Message, LogKind.Err);
        }
    }

    // ================= 教程 =================
    private void ShowTutorial()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "使用教程.txt");
        if (!File.Exists(path))
            path = Path.Combine(store.S.GamerToolDir, "使用教程.txt");
        using var d = new TutorialDialog(path);
        d.ShowDialog(this);
    }

    // ================= 操作 =================
    private void Log(string msg, LogKind kind = LogKind.Info)
    {
        if (rtbLog.IsDisposed) return;
        void Do()
        {
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = kind switch
            {
                LogKind.Ok => Theme.Green,
                LogKind.Warn => Theme.Yellow,
                LogKind.Err => Theme.Red,
                LogKind.Tool => Theme.ToolC,
                _ => Theme.Text,
            };
            rtbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }
        if (rtbLog.InvokeRequired) rtbLog.BeginInvoke(Do);
        else Do();
    }

    private void OpenExplorer(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            MessageBox.Show(this, "目录不存在:\n" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }

    private void LocateDeployed()
    {
        var e = Selected();
        if (e == null || !e.Deployed) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + e.DeployedLuaPath + "\"") { UseShellExecute = true });
    }

    // ---- 并发拉取任务管理（UI 线程访问）----
    private bool AppBusy(long id) => busyApps.Contains(id);

    private void PullBegin(long? id)
    {
        if (id.HasValue) busyApps.Add(id.Value);
        activePulls++;
        UpdatePullUi();
        UpdateDetails();
    }

    private void PullEnd(long? id)
    {
        if (id.HasValue) busyApps.Remove(id.Value);
        activePulls = Math.Max(0, activePulls - 1);
        UpdatePullUi();
        UpdateDetails();
    }

    private void UpdatePullUi()
    {
        btnPullGo.Text = activePulls > 0 ? $"拉取中({activePulls})…" : "开始拉取";
    }

    private async Task PullFromMainAsync()
    {
        string input = txtPull.Text.Trim();
        if (input.Length == 0)
        {
            MessageBox.Show(this, "请输入 AppID 或游戏名（也可以把商店链接直接拖进窗口）。", "拉取", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await PullNativeAsync(input);
    }

    private async Task PullNativeAsync(string input)
    {
        long appid;
        string name = null;

        // 拖拽/粘贴的链接先尝试直接解析 AppID
        var linkId = Store.ExtractAppId(input);
        if (linkId != null)
        {
            appid = linkId.Value;
            Log("链接解析 AppID: " + appid, LogKind.Info);
            try { name = await Store.FetchGameNameAsync(appid, store.ProxyEff); } catch { }
        }
        else if (long.TryParse(input, out appid) && input.Length >= 4)
        {
            Log("输入 AppID: " + appid, LogKind.Info);
            try { name = await Store.FetchGameNameAsync(appid, store.ProxyEff); } catch { }
        }
        else
        {
            Log("搜索 Steam 商店: " + input, LogKind.Info);
            List<(long id, string name)> matches;
            try { matches = await Store.NativePuller.SearchGamesAsync(input, store.ProxyEff); }
            catch (Exception ex) { Log("✕ 搜索失败: " + ex.Message, LogKind.Err); return; }
            if (matches.Count == 0)
            {
                Log("✕ 未搜索到匹配的游戏（可尝试英文名或直接输 AppID）。", LogKind.Warn);
                return;
            }
            if (matches.Count == 1)
            {
                appid = matches[0].id;
                name = matches[0].name;
            }
            else
            {
                using var chooser = new GameChooser(matches.Take(15).ToList());
                if (chooser.ShowDialog(this) != DialogResult.OK)
                {
                    Log("已取消选择。", LogKind.Warn);
                    return;
                }
                appid = chooser.PickId;
                name = chooser.PickName;
            }
            Log("选中: " + name + "  (" + appid + ")", LogKind.Ok);
        }

        if (name != null)
        {
            if (!store.Records.TryGetValue(appid, out var rec))
            {
                rec = new GameRecord { AppId = appid };
                store.Records[appid] = rec;
            }
            rec.FetchedName = name;
            store.SaveRecords();
        }

        if (AppBusy(appid))
        {
            Log(appid + " 已在拉取中，不重复发起。", LogKind.Warn);
            return;
        }

        PullBegin(appid);
        try
        {
            Store.NativePuller.Result res;
            try
            {
                res = await Store.NativePuller.PullAsync(appid, name, store.S.GamerToolDir, store.ProxyEff,
                    (t, k) => Log(t, k), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log("✕ 原生拉取异常: " + ex.Message, LogKind.Err);
                return;
            }
            if (res == null)
            {
                Log("✕ 所有清单仓库都没有该游戏的分支（可能尚未收录）。", LogKind.Err);
                return;
            }
            Log("✓ 原生拉取完成: " + res.Dir + "（" + res.Files + " 个文件 ← " + res.Repo + "）" +
                (res.FailedFiles > 0 ? "，⚠ " + res.FailedFiles + " 个文件下载失败" : "") +
                " —— 点【部署 / 加入 SteamTools 管理】解锁", LogKind.Ok);
            txtPull.Text = appid.ToString();
        }
        finally
        {
            PullEnd(appid);
            RefreshAll();
        }
    }

    private async Task RePullSelectedAsync()
    {
        var e = Selected();
        if (e == null || !e.FolderExists) return;
        if (AppBusy(e.AppId))
        {
            Log(e.AppId + " 已在拉取中，不重复发起。", LogKind.Warn);
            return;
        }
        PullBegin(e.AppId);
        try
        {
            Log($"重新拉取 {e.DisplayName}（{e.AppId}）的最新清单，覆盖旧文件…", LogKind.Info);
            var res = await Store.NativePuller.PullAsync(e.AppId, null, store.S.GamerToolDir, store.ProxyEff,
                (t, k) => Log(t, k), CancellationToken.None, existingDir: e.Folder, force: true);
            if (res == null)
                Log("✕ 所有仓库都没有该游戏的分支。", LogKind.Err);
            else
                Log($"✓ 重新拉取完成（{res.Files} 个文件 ← {res.Repo}）" +
                    (res.FailedFiles > 0 ? "，⚠ " + res.FailedFiles + " 个文件失败" : "") +
                    "，重新【部署】一次即可更新 Steam 侧内容。", LogKind.Ok);
        }
        catch (Exception ex)
        {
            Log("✕ 重新拉取失败: " + ex.Message, LogKind.Err);
        }
        finally
        {
            PullEnd(e.AppId);
            RefreshAll();
        }
    }

    private async Task DeploySelectedAsync()
    {
        var e = Selected();
        if (e == null) return;
        try
        {
            var r = store.Deploy(e);
            Log($"✓ 已部署 {e.DisplayName}（{e.AppId}）：Lua→stplug-in、{e.LocalManifests.Count} 个清单→depotcache（共 {r.Files} 个文件）" +
                (r.DbRows > 0 ? $"，新增 SteamTools 登记 {r.DbRows} 行" : "") +
                (r.DbErrors > 0 ? "（⚠ appids.db 写入失败）" : ""), LogKind.Ok);
            RefreshAll();
            await PromptSteamRestartAsync("部署完成。Steam 需要重启后才会看到新的解锁内容。");
        }
        catch (Exception ex)
        {
            Log("✕ 部署失败: " + ex.Message, LogKind.Err);
            MessageBox.Show(this, "部署失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RestoreSelectedAsync()
    {
        var e = Selected();
        if (e == null) return;

        var dcFiles = store.DepotCacheFiles(e);
        var dbIds = store.RegisteredDbIds(e);
        string msg = $"将把「{e.DisplayName}」还原成未破解状态：\n\n" +
                     (e.Deployed ? "• 删除 Steam 注册 Lua:  " + Path.GetFileName(e.DeployedLuaPath) + "\n" : "") +
                     (store.S.CleanDepotCache && dcFiles.Count > 0 ? "• 删除 depotcache 清单 " + dcFiles.Count + " 个\n" : "") +
                     (store.S.CleanAppIdsDb && dbIds.Count > 0 ? "• 清理 appids.db 登记 " + dbIds.Count + " 行\n" : "") +
                     "\n继续？";
        if (MessageBox.Show(this, msg, "还原未破解", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try
        {
            var r = store.Restore(e, store.S.CleanDepotCache, store.S.CleanAppIdsDb);
            Log($"✓ 已还原 {e.DisplayName}（{e.AppId}）：Lua {r.LuaDeleted} · 清单 {r.ManifestsDeleted} · 登记 {r.DbRowsDeleted}" +
                (r.DbErrors > 0 ? " · ⚠ appids.db 写入失败" : ""), LogKind.Ok);
            RefreshAll();
            await PromptSteamRestartAsync("还原完成。Steam 重启后该游戏恢复为未破解状态。");
        }
        catch (Exception ex)
        {
            Log("✕ 还原失败: " + ex.Message, LogKind.Err);
            MessageBox.Show(this, "还原失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RestoreAllAsync()
    {
        var deployed = entries.Where(e => e.Deployed).ToList();
        if (deployed.Count == 0)
        {
            MessageBox.Show(this, "当前没有已部署的游戏。", "还原全部", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var preview = string.Join("\n", deployed.Take(8).Select(e => "• " + e.DisplayName + "  (" + e.AppId + ")"));
        if (deployed.Count > 8) preview += "\n• …等共 " + deployed.Count + " 个";
        if (MessageBox.Show(this,
                "将把以下所有已部署游戏还原成未破解状态（删除 stplug-in Lua、depotcache 清单、appids.db 登记）：\n\n" +
                preview + "\n\n确定继续？",
                "还原全部", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        int files = 0, rows = 0, fails = 0;
        foreach (var e in deployed)
        {
            try
            {
                var r = store.Restore(e, store.S.CleanDepotCache, store.S.CleanAppIdsDb);
                files += r.LuaDeleted + r.ManifestsDeleted;
                rows += r.DbRowsDeleted;
                fails += r.DbErrors;
            }
            catch (Exception ex)
            {
                Log("✕ " + e.AppId + " 还原失败: " + ex.Message, LogKind.Err);
            }
        }
        Log($"✓ 批量还原完成：{deployed.Count} 个游戏，删除文件 {files} 个，清理登记 {rows} 行" +
            (fails > 0 ? "（" + fails + " 个数据库错误）" : ""), LogKind.Ok);
        RefreshAll();
        await PromptSteamRestartAsync("批量还原完成。Steam 重启后这些游戏恢复为未破解状态。");
    }

    private void DeleteSourceLua()
    {
        var e = Selected();
        if (e == null || !e.SourceLuaExists) return;
        string lua = Path.Combine(e.Folder, e.AppId + ".lua");
        if (MessageBox.Show(this, "删除游戏文件夹中的解锁 Lua？（移入回收站）\n" + lua,
                "删除 Lua", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (Native.RecycleDelete(lua))
        {
            Log("✓ 已删除 " + lua + "（回收站）", LogKind.Ok);
            if (e.Deployed)
                Log("  提示: Steam 中仍有已部署副本，可用【还原未破解】移除。", LogKind.Warn);
            RefreshAll();
        }
        else
            Log("✕ 删除失败: " + lua, LogKind.Err);
    }

    private void DeleteGameFolder()
    {
        var e = Selected();
        if (e == null || !e.FolderExists) return;
        if (MessageBox.Show(this, "将整个游戏文件夹移入回收站（可恢复）：\n" + e.Folder,
                "删除游戏文件夹", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (Native.RecycleDelete(e.Folder))
        {
            Log("✓ 已删除 " + e.Folder + "（回收站）", LogKind.Ok);
            RefreshAll();
        }
        else
            Log("✕ 删除失败: " + e.Folder, LogKind.Err);
    }

    private void RenameSelected()
    {
        var e = Selected();
        if (e == null) return;
        string name = InputDialog.Show(this, "重命名", $"设置 AppID {e.AppId} 的显示名（留空恢复默认）:", e.CustomName ?? "");
        if (name == null) return;
        name = name.Trim();
        if (!store.Records.TryGetValue(e.AppId, out var rec))
        {
            rec = new GameRecord { AppId = e.AppId, FetchedName = e.FetchedName };
            store.Records[e.AppId] = rec;
        }
        rec.Name = name == "" ? null : name;
        store.SaveRecords();
        Log("✓ " + e.AppId + " 显示名 → " + (name == "" ? "（默认）" : name), LogKind.Ok);
        RefreshAll();
    }

    private async Task FetchSelectedNameAsync()
    {
        var e = Selected();
        if (e == null || fetchingNames) return;
        fetchingNames = true;
        UpdateDetails();
        try
        {
            Log("联网抓取 " + e.AppId + " 的游戏名…", LogKind.Info);
            string name = await Store.FetchGameNameAsync(e.AppId, store.ProxyEff);
            if (name == null)
            {
                Log("✕ 未获取到 " + e.AppId + " 的游戏名（可检查设置里的代理）", LogKind.Warn);
            }
            else
            {
                if (!store.Records.TryGetValue(e.AppId, out var rec))
                {
                    rec = new GameRecord { AppId = e.AppId };
                    store.Records[e.AppId] = rec;
                }
                rec.FetchedName = name;
                store.SaveRecords();
                Log("✓ " + e.AppId + " → " + name, LogKind.Ok);
                RefreshAll();
            }
        }
        finally
        {
            fetchingNames = false;
            UpdateDetails();
        }
    }

    private async Task FetchMissingNamesAsync(bool silent)
    {
        if (fetchingNames) return;
        var missing = entries.Where(e =>
            string.IsNullOrWhiteSpace(e.CustomName) && string.IsNullOrWhiteSpace(e.FetchedName)).ToList();
        if (missing.Count == 0)
        {
            if (!silent) MessageBox.Show(this, "所有游戏都已有名字。", "抓取名字", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        fetchingNames = true;
        UpdateDetails();
        if (!silent) Log("开始抓取 " + missing.Count + " 个缺失游戏名…", LogKind.Info);
        int ok = 0;
        foreach (var e in missing)
        {
            string name = null;
            try { name = await Store.FetchGameNameAsync(e.AppId, store.ProxyEff); } catch { }
            if (name != null)
            {
                ok++;
                if (!store.Records.TryGetValue(e.AppId, out var rec))
                {
                    rec = new GameRecord { AppId = e.AppId };
                    store.Records[e.AppId] = rec;
                }
                rec.FetchedName = name;
                store.SaveRecords();
                Log("✓ " + e.AppId + " → " + name, LogKind.Ok);
                RefreshAll();
            }
            await Task.Delay(300);
        }
        fetchingNames = false;
        UpdateDetails();
        Log($"游戏名抓取完成：成功 {ok} / {missing.Count}（未命中的多为已下架或子内容）", ok > 0 ? LogKind.Ok : LogKind.Warn);
    }

    private void ShowOrphans()
    {
        var ids = store.OrphanDbIds();
        if (ids.Count == 0)
        {
            MessageBox.Show(this, "appids.db 中没有孤儿登记项 —— 所有登记都有对应的解锁 Lua 覆盖。",
                "孤儿登记", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var d = new OrphanDialog(store, ids);
        d.ShowDialog(this);
        RefreshAll();
    }

    private void ShowCollector()
    {
        using var d = new CollectorDialog(store)
        {
            OnExtracted = (appid, dir) =>
            {
                Log($"✓ 清单提取完成: {dir} —— 可部署，也可在【清单收集】里上传。", LogKind.Ok);
                RefreshAll();
            },
            OnUploadDone = (ok, fail) =>
                Log($"清单收集上传结束: 成功 {ok}，失败/冲突 {fail}。" +
                    (ok > 0 ? $"分支见 github.com/{Collector.Owner}/{Collector.Repo}" : ""), ok > 0 ? LogKind.Ok : LogKind.Warn),
        };
        d.ShowDialog(this);
    }

    /// <summary>把选中游戏的本地清单直接上传到收集库（不经收集对话框扫描）。</summary>
    private async Task UploadSelectedToCollectAsync()
    {
        var e = Selected();
        if (e == null || !e.FolderExists) return;
        if (Collector.UploadToken == "")
        {
            MessageBox.Show(this,
                "当前构建没有内置上传令牌。\n\n维护者按 Secrets.cs.example 配好令牌重新编译后即可上传。",
                "上传清单", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var set = new Collector.CollectSet
        {
            AppId = e.AppId,
            Recognized = true,
            Dir = e.Folder,
            DisplayName = e.DisplayName,
            Manifests = e.LocalManifests.ToList(),
            VdfLocal = new[] { "Key.vdf", "config.vdf" }
                .Select(n => Path.Combine(e.Folder, n))
                .FirstOrDefault(File.Exists),
            HasLua = e.SourceLuaExists,
        };
        if (set.Manifests.Count == 0 && set.VdfLocal == null)
        {
            MessageBox.Show(this, "该游戏文件夹里没有清单和 Key.vdf，无法上传。\n请先拉取或提取。", "上传清单", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this,
                $"将把「{e.DisplayName}」（{e.AppId}）上传到 {Collector.RepoSlug}。\n已有分支只提交差异，不删除远端文件。\n\n继续？",
                "上传清单", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        btnUploadCollect.Enabled = false;
        try
        {
            await Collector.EnsureRepoAsync(store.ProxyEff);
            var r = await Collector.UploadSetAsync(store, set, t => Log("  " + t, LogKind.Tool), CancellationToken.None);
            if (r.Skipped)
                Log($"✓ {e.DisplayName} 远端已是最新，无需提交。", LogKind.Ok);
            else
                Log($"✓ 已上传 {e.DisplayName}：{r.UploadedFiles} 个文件，commit {r.CommitSha}。", LogKind.Ok);
        }
        catch (Collector.ApiException aex) when (aex.Status == 422)
        {
            Log($"⚠ {e.AppId} 分支在期间被他人更新（冲突），未覆盖；稍后重试。", LogKind.Warn);
        }
        catch (Exception ex)
        {
            Log("✕ 上传失败: " + ex.Message, LogKind.Err);
        }
        finally
        {
            btnUploadCollect.Enabled = true;
            UpdateDetails();
        }
    }

    private void ShowSettings()
    {
        try
        {
            using var d = new SettingsDialog(store);
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                RefreshAll(keepSelection: false);
                Log("设置已保存。", LogKind.Ok);
            }
        }
        catch (Exception ex)
        {
            Log("✕ 打开设置失败: " + ex.Message + "\n" + ex.StackTrace, LogKind.Err);
            MessageBox.Show(this, "打开设置失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PromptSteamRestartAsync(string reason)
    {
        if (!Store.SteamRunning)
        {
            Log("Steam 当前未运行，无需重启。", LogKind.Info);
            return;
        }
        if (MessageBox.Show(this, reason + "\n\n是否现在重启 Steam？（将等待 Steam 完全退出后自动重新启动）\n若重启后仍未解锁，请确认 SteamTools 正在运行，再重启一次 Steam。",
                "重启 Steam", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            Log("提示: 请稍后手动重启 Steam 使更改生效。", LogKind.Warn);
            return;
        }
        UseWaitCursor = true;
        try
        {
            Log("1/4 正在请求 Steam 退出…", LogKind.Info);
            bool ok = await Store.ShutdownSteamAsync(store.SteamDirEff);
            if (!ok)
            {
                Log("Steam 未在期限内正常退出，强制结束 steam.exe。", LogKind.Warn);
                Store.KillSteam();
                await Task.Delay(1500);
            }

            // 按正确顺序重启整套：SteamTools 必须先于 Steam 就绪，否则授权查询赶不上就会"破解失效"
            int cacheCleaned = Store.CleanSteamLicenseCache(store.SteamDirEff);
            if (cacheCleaned > 0)
                Log($"✓ 已清理 Steam 授权缓存 {cacheCleaned} 个文件（强制下次启动重新查询授权，杜绝轮流失败）", LogKind.Ok);

            if (Store.SteamToolsInstalled)
            {
                if (Store.SteamToolsRunning)
                {
                    Log("2/4 正在退出 SteamTools…", LogKind.Info);
                    await Store.KillSteamToolsAsync();
                }
                Log("3/4 先启动 SteamTools，等它就绪…", LogKind.Info);
                Store.StartSteamTools(Store.SteamToolsExe);
                for (int i = 0; i < 20 && !Store.SteamToolsRunning; i++) await Task.Delay(500);
                await Task.Delay(3500); // 给注入服务初始化时间
            }
            else
            {
                Log("⚠ 未检测到 SteamTools，仅重启 Steam（解锁依赖 SteamTools 运行）。", LogKind.Warn);
            }

            Log("4/4 启动 Steam…", LogKind.Info);
            Store.StartSteam(store.SteamDirEff);
            Log("✓ 重启完成：SteamTools 已先于 Steam 就绪。若仍未解锁，请再重启一次 Steam。", LogKind.Ok);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}

public enum LogKind { Info, Ok, Warn, Err, Tool }
