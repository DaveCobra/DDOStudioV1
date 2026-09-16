using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DDOAssetStudio;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) ReportFatal(ex);
            };
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
        }
    }

    static void ReportFatal(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio");
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "startup.log");
            File.AppendAllText(log, $"[{DateTime.Now:O}]\r\n{ex}\r\n\r\n");
            MessageBox.Show(
                $"DDO Studio could not start.\n\n{ex.Message}\n\nA diagnostic log was written to:\n{log}",
                "DDO Studio startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            try { MessageBox.Show(ex.ToString(), "DDO Studio startup error"); } catch { }
        }
    }
}

public sealed class AssetRow
{
    public uint DbId { get; set; }
    public string Name { get; set; } = "";
    public uint PhysObj { get; set; }
    public uint VisualDesc { get; set; }
    public uint Appearance { get; set; }
    public uint Setup { get; set; }
    public uint WeenieType { get; set; }
    public string WeenieTypeName { get; set; } = "Unknown";
    public string AssetClass { get; set; } = "Unknown";
    public JsonObject? ItemData { get; set; }
    public bool IsEquippable { get; set; }
    public string EquipmentKind { get; set; } = "";
    public string[] EquipSlots { get; set; } = Array.Empty<string>();
    public string[] CompatibleSlotsRaw { get; set; } = Array.Empty<string>();
    public string[] PrecludedSlotsRaw { get; set; } = Array.Empty<string>();
    public string WeaponTypeName { get; set; } = "";
    public string ArmorTypeName { get; set; } = "";
    public bool CanMainHand { get; set; }
    public bool CanOffHand { get; set; }
    public bool IsTwoHanded { get; set; }
    public bool IsIndexOnly { get; set; }
    public bool HasStandaloneModel => Setup != 0;
    public override string ToString() => Name;
}

public sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(6, 9, 13);
    static readonly Color Panel = Color.FromArgb(10, 14, 20);
    static readonly Color PanelAlt = Color.FromArgb(16, 23, 32);
    static readonly Color Border = Color.FromArgb(38, 47, 58);
    static readonly Color TextMain = Color.FromArgb(235, 237, 240);
    static readonly Color TextMuted = Color.FromArgb(139, 149, 163);
    static readonly Color Accent = Color.FromArgb(210, 171, 92);
    static readonly Color AccentHover = Color.FromArgb(240, 207, 138);

    readonly TextBox ddoPath = new() { Dock = DockStyle.Fill };
    readonly Button browse = new() { Text = "Browse…", AutoSize = false, Size = new Size(96, 38) };
    readonly Label backendStatus = new() { Text = "Starting services…", AutoSize = true };
    readonly TextBox search = new() { PlaceholderText = "Search renderable models — kobold, dragon, skeleton…", Dock = DockStyle.Fill };
    readonly Button searchButton = new() { Text = "Search", AutoSize = false, Size = new Size(96, 38) };
    readonly Button viewAllButton = new() { Text = "View All", AutoSize = false, Size = new Size(100, 38) };
    readonly CheckBox hideDuplicates = new() { Text = "Hide duplicates", AutoSize = true, Checked = true };
    readonly ListView results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
    readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Button exportButton = new() { Text = "Export GLB", AutoSize = false, Size = new Size(116, 42), Enabled = false };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    readonly Label status = new() { Text = "Ready", AutoSize = true };

    WebView2? viewer;
    readonly Panel viewerHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(6, 18, 28) };
    readonly Label previewStatus = new() { Text = "3D preview: waiting for selection", AutoSize = true, Padding = new Padding(8, 7, 8, 7) };

    readonly HttpClient http;
    readonly int backendPort;
    readonly string previewCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio", "PreviewCache");
    readonly string appearanceDiagnosticsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio", "AppearanceDiagnostics");
    readonly string appearanceDiagnosticsLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio", "appearance-diagnostics.log");
    readonly string equipmentDiagnosticsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio", "WearableDiagnostics");
    readonly string animationAliasesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio", "animation-aliases.json");
    readonly string builtInAnimationAliasesPath = Path.Combine(AppContext.BaseDirectory, "viewer", "animation-names.json");
    readonly Dictionary<string, string> builtInAnimationAliases = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> animationAliases = new(StringComparer.OrdinalIgnoreCase);
    const string DressingDefaultAnimationId = "0x0500005C";
    const string UniversalPreferredAnimationId = "0x05000440";
    const string WeaponPreferredAnimationId = "0x05005943";

    Process? backend;
    Process? previewExporter;
    JsonObject? index;
    AssetRow? selected;
    bool viewerReady;
    bool viewerMessagesHooked;
    int previewGeneration;
    bool lastBrowseWasViewAll;
    string? selectedAnimationId;
    readonly List<string> compatibleAnimationIds = new();
    Panel? appHeader;
    FlowLayoutPanel? appNav;
    TableLayoutPanel? appTop;
    TableLayoutPanel? appSearchBar;
    TableLayoutPanel? appBottom;
    SplitContainer? appMainSplit;
    bool presentationMode;
    FormBorderStyle savedBorderStyle;
    FormWindowState savedWindowState;

    readonly ComboBox dressingRace = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 34 };
    readonly ComboBox dressingGender = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 34 };
    readonly ComboBox dressingBaseModel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 34 };
    readonly ComboBox dressingDisplayMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 34 };
    readonly ListView dressingSlots = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly Label dressingInfo = new() { Dock = DockStyle.Bottom, Height = 54, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 4, 10, 4), AutoEllipsis = true };
    readonly Button dressingLoadBase = new() { Text = "Load Base Character", Height = 38, Dock = DockStyle.Top };
    readonly Button dressingChooseItem = new() { Text = "Choose Equipment…", Height = 36, Dock = DockStyle.Top };
    readonly Button dressingClearItem = new() { Text = "Clear Slot", Height = 34, Dock = DockStyle.Top };
    readonly Button dressingAnimations = new() { Text = "Animations", Height = 36, Dock = DockStyle.Fill };
    readonly Button dressingDefaultIdle = new() { Text = "Default Idle", Height = 36, Dock = DockStyle.Fill };
    readonly Dictionary<string, AssetRow> dressingEquipment = new(StringComparer.OrdinalIgnoreCase);
    AssetRow? dressingBase;
    Panel? dressingPanel;
    Control? assetLibraryPanel;
    bool dressingRoomMode;

    sealed class CharacterTemplateChoice
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "";
        public string Race { get; init; } = "";
        public string Gender { get; init; } = "";
        public int Score { get; init; }
        public override string ToString() => $"{Name}  [0x{Id:X8}]";
    }

    sealed class EquipmentChoice
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string WeaponType { get; init; } = "";
        public uint WeenieType { get; init; }
        public string[] Slots { get; init; } = Array.Empty<string>();
        public bool CanMain { get; init; }
        public bool CanOff { get; init; }
        public bool TwoHanded { get; init; }
        public bool AutoWearable { get; init; }
        public override string ToString() => Name;
    }

    sealed record DressingAppearanceSelection(uint Id, string Slot, uint Appearance);

    static int FindAvailableLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public MainForm()
    {
        backendPort = FindAvailableLoopbackPort();
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{backendPort}/") };
        Text = "DDO Studio 1.7.2";
        Width = 1720;
        Height = 1040;
        MinimumSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10.5f);
        Directory.CreateDirectory(previewCache);
        Directory.CreateDirectory(appearanceDiagnosticsDir);
        Directory.CreateDirectory(equipmentDiagnosticsDir);
        LoadBuiltInAnimationAliases();
        LoadAnimationAliases();

        results.Columns.Add("Name", 320);
        results.Columns.Add("Type", 170);
        results.Columns.Add("Object ID", 118);
        results.Columns.Add("Setup", 118);
        results.OwnerDraw = true;
        results.DrawColumnHeader += DrawResultsColumnHeader;
        results.DrawItem += (_, e) => { if (results.View != View.Details) e.DrawDefault = true; };
        results.DrawSubItem += DrawResultsSubItem;

        var header = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(18, 8, 18, 6), BackColor = Color.FromArgb(5, 8, 12) };
        var title = new Label { Text = "DDO STUDIO", AutoSize = true, Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold), ForeColor = TextMain, Location = new Point(18, 10) };
        var subtitle = new Label { Text = "3D MODEL VIEWER", AutoSize = true, Font = new Font("Segoe UI Semibold", 7.8f), ForeColor = Accent, Location = new Point(20, 38) };
        var version = new Label { Text = "v1.7.2", AutoSize = true, Font = new Font("Consolas", 8.5f), ForeColor = TextMuted, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        version.Location = new Point(header.Width - version.Width - 24, 23);
        header.Resize += (_, _) => version.Location = new Point(header.ClientSize.Width - version.Width - 24, 23);
        header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(version);

        var nav = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(18, 5, 12, 5), BackColor = Color.FromArgb(7, 11, 16), WrapContents = false };
        Button NavButton(string text, bool active = false)
        {
            var b = new Button { Text = text, Width = 150, Height = 32, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.Flat, BackColor = active ? Color.FromArgb(48, 39, 23) : Color.FromArgb(11, 16, 23), ForeColor = active ? AccentHover : TextMain, Font = new Font("Segoe UI Semibold", 9.4f), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = active ? Accent : Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(26, 34, 44);
            return b;
        }
        var navViewer = NavButton("3D Viewer", true);
        nav.Controls.Add(navViewer);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 52, ColumnCount = 4, Padding = new Padding(16, 7, 16, 7), BackColor = Color.FromArgb(8, 12, 17) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "DDO INSTALL", AutoSize = false, Width = 92, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = TextMuted }, 0, 0);
        top.Controls.Add(ddoPath, 1, 0);
        top.Controls.Add(browse, 2, 0);
        top.Controls.Add(backendStatus, 3, 0);

        ddoPath.Margin = new Padding(0, 0, 8, 0);
        browse.Margin = new Padding(0, 0, 10, 0);
        backendStatus.Margin = new Padding(0, 8, 0, 0);
        search.Margin = new Padding(0, 4, 8, 4);
        searchButton.Margin = new Padding(0, 2, 8, 2);
        details.Font = new Font("Consolas", 10.5f);
        details.BorderStyle = BorderStyle.FixedSingle;
        previewStatus.Font = new Font("Segoe UI", 9.5f);
        status.Font = new Font("Segoe UI", 9.5f);

        var searchBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 54, ColumnCount = 4, Padding = new Padding(16, 7, 16, 7), BackColor = Color.FromArgb(8, 12, 17) };
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchBar.Controls.Add(search, 0, 0);
        searchBar.Controls.Add(searchButton, 1, 0);
        searchBar.Controls.Add(viewAllButton, 2, 0);
        searchBar.Controls.Add(hideDuplicates, 3, 0);

        var left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 1 };
        var libraryTitle = new Label { Text = "  MODEL LIBRARY", Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Accent, BackColor = Color.FromArgb(10, 14, 20) };
        var detailTitle = new Label { Text = "  MODEL DETAILS", Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = TextMuted, BackColor = Color.FromArgb(10, 14, 20) };
        left.Panel1.Controls.Add(results);
        left.Panel1.Controls.Add(libraryTitle);
        left.Panel2.Controls.Add(details);
        left.Panel2.Controls.Add(detailTitle);
        assetLibraryPanel = left;

        var viewerPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1, 0, 0, 0), BackColor = Border };
        viewerPanel.Controls.Add(viewerHost);
        viewerPanel.Controls.Add(previewStatus);
        previewStatus.Dock = DockStyle.Bottom;

        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 1, BackColor = Border };
        mainSplit.Panel1.Controls.Add(left);
        mainSplit.Panel2.Controls.Add(viewerPanel);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 52, ColumnCount = 3, Padding = new Padding(16, 7, 16, 7), BackColor = Color.FromArgb(7, 11, 16) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(exportButton, 0, 0);
        bottom.Controls.Add(progress, 1, 0);
        bottom.Controls.Add(status, 2, 0);

        Controls.Add(mainSplit);
        Controls.Add(bottom);
        Controls.Add(searchBar);
        Controls.Add(top);
        Controls.Add(nav);
        Controls.Add(header);

        appHeader = header;
        appNav = nav;
        appTop = top;
        appSearchBar = searchBar;
        appBottom = bottom;
        appMainSplit = mainSplit;

        ApplyTheme(this);
        header.BackColor = Color.FromArgb(5, 8, 12);
        nav.BackColor = Color.FromArgb(7, 11, 16);
        top.BackColor = Color.FromArgb(8, 12, 17);
        searchBar.BackColor = Color.FromArgb(8, 12, 17);
        bottom.BackColor = Color.FromArgb(7, 11, 16);
        viewerHost.BackColor = Color.FromArgb(5, 8, 12);
        previewStatus.BackColor = Panel;
        previewStatus.ForeColor = TextMuted;
        StylePrimaryButton(exportButton);
        StylePrimaryButton(searchButton);
        StyleSecondaryButton(viewAllButton);
        StyleSecondaryButton(browse);

        ddoPath.Text = DetectDdoPath() ?? "";
        browse.Click += (_, _) => BrowseDdo();
        searchButton.Click += async (_, _) => await SearchAsync();
        viewAllButton.Click += async (_, _) => await ViewAllAsync();
        search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SearchAsync();
            }
        };
        navViewer.Click += async (_, _) =>
        {
            left.Visible = true;
            searchBar.Visible = true;
            subtitle.Text = "3D MODEL VIEWER";
            var maxViewerSplit = Math.Max(mainSplit.Panel1MinSize, mainSplit.ClientSize.Width - mainSplit.Panel2MinSize - mainSplit.SplitterWidth);
            mainSplit.SplitterDistance = Math.Clamp(340, mainSplit.Panel1MinSize, maxViewerSplit);
            await EnsureViewerAsync();
        };
        results.SelectedIndexChanged += async (_, _) => await SelectRowAsync();
        exportButton.Click += async (_, _) => await ExportAsync();


        Shown += async (_, _) =>
        {
            // SplitContainer validates SplitterDistance against its *current* size.
            // Set minimums/distances only after the form has completed its first layout.
            mainSplit.Panel1MinSize = 300;
            mainSplit.Panel2MinSize = 680;
            var mainMax = Math.Max(mainSplit.Panel1MinSize, mainSplit.ClientSize.Width - mainSplit.Panel2MinSize - mainSplit.SplitterWidth);
            mainSplit.SplitterDistance = Math.Clamp(340, mainSplit.Panel1MinSize, mainMax);

            left.Panel1MinSize = 220;
            left.Panel2MinSize = 100;
            var leftMax = Math.Max(left.Panel1MinSize, left.ClientSize.Height - left.Panel2MinSize - left.SplitterWidth);
            left.SplitterDistance = Math.Clamp((int)(left.ClientSize.Height * 0.68), left.Panel1MinSize, leftMax);

            await EnsureViewerAsync();
            if (Directory.Exists(ddoPath.Text)) await EnsureBackendAsync();
        };
        FormClosing += (_, _) =>
        {
            try
            {
                if (previewExporter is { HasExited: false })
                {
                    previewExporter.Kill(true);
                    previewExporter.WaitForExit(3000);
                }
            }
            catch { }
            try
            {
                if (backend is { HasExited: false })
                {
                    backend.Kill(true);
                    backend.WaitForExit(3000);
                }
            }
            catch { }
        };
    }

    static void SetNavState(Button viewerButton, Button dressingButton, bool dressing)
    {
        void Apply(Button b, bool active)
        {
            b.BackColor = active ? Color.FromArgb(48, 39, 23) : Color.FromArgb(11, 16, 23);
            b.ForeColor = active ? AccentHover : TextMain;
            b.FlatAppearance.BorderColor = active ? Accent : Border;
        }
        Apply(viewerButton, !dressing);
        Apply(dressingButton, dressing);
    }

    Panel BuildDressingRoomPanel()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(12) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, BackColor = Bg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 174));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var hero = new Panel { Dock = DockStyle.Fill, BackColor = Panel };
        hero.Controls.Add(new Label { Text = "DRESSING ROOM", Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI Semibold", 14, FontStyle.Bold), ForeColor = TextMain, Padding = new Padding(8, 4, 0, 0) });
        hero.Controls.Add(new Label { Text = "Build a character from DDO's race/body records and equip indexed game items.", Dock = DockStyle.Bottom, Height = 24, Font = new Font("Segoe UI", 8.5f), ForeColor = TextMuted, Padding = new Padding(8, 0, 4, 5) });
        root.Controls.Add(hero, 0, 0);

        var baseCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = PanelAlt, Padding = new Padding(8) };
        baseCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        baseCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) baseCard.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        Label L(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextMuted, Font = new Font("Segoe UI Semibold", 8.5f) };
        baseCard.Controls.Add(L("Race"), 0, 0); baseCard.Controls.Add(dressingRace, 1, 0);
        baseCard.Controls.Add(L("Body"), 0, 1); baseCard.Controls.Add(dressingGender, 1, 1);
        baseCard.Controls.Add(L("Base"), 0, 2); baseCard.Controls.Add(dressingBaseModel, 1, 2);
        root.Controls.Add(baseCard, 0, 1);
        root.Controls.Add(dressingLoadBase, 0, 2);

        var display = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Bg, Padding = new Padding(0, 7, 0, 7) };
        display.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        display.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        display.Controls.Add(new Label { Text = "Display", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextMuted }, 0, 0);
        dressingDisplayMode.Items.AddRange(new object[] { "Character + Equipment", "Character Only", "Equipment Only" });
        dressingDisplayMode.SelectedIndex = 0;
        display.Controls.Add(dressingDisplayMode, 1, 0);
        root.Controls.Add(display, 0, 3);

        root.Controls.Add(new Label { Text = "EQUIPMENT", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 8.5f), Padding = new Padding(4, 0, 0, 0) }, 0, 4);
        dressingSlots.Columns.Add("Slot", 96);
        dressingSlots.Columns.Add("Item", 180);
        dressingSlots.Columns.Add("State", 92);
        dressingSlots.BackColor = Panel;
        dressingSlots.ForeColor = TextMain;
        dressingSlots.BorderStyle = BorderStyle.FixedSingle;
        dressingSlots.OwnerDraw = true;
        dressingSlots.DrawColumnHeader += DrawResultsColumnHeader;
        dressingSlots.DrawItem += (_, e) => { if (dressingSlots.View != View.Details) e.DrawDefault = true; };
        dressingSlots.DrawSubItem += DrawResultsSubItem;
        root.Controls.Add(dressingSlots, 0, 5);

        var equipButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Bg };
        equipButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        equipButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        equipButtons.Controls.Add(dressingChooseItem, 0, 0);
        equipButtons.Controls.Add(dressingClearItem, 0, 1);
        root.Controls.Add(equipButtons, 0, 6);
        var animationButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Bg, Padding = new Padding(0, 4, 0, 4) };
        animationButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        animationButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        animationButtons.Controls.Add(dressingAnimations, 0, 0);
        animationButtons.Controls.Add(dressingDefaultIdle, 1, 0);
        root.Controls.Add(animationButtons, 0, 7);
        dressingInfo.BackColor = Panel;
        dressingInfo.ForeColor = TextMuted;
        dressingInfo.Text = "Choose a race/body candidate, load it, then double-click an equipment slot.";
        root.Controls.Add(dressingInfo, 0, 8);
        host.Controls.Add(root);
        RefreshDressingSlotRows();
        return host;
    }

    static readonly string[] PlayableRaceOrder =
    {
        "Human", "Elf", "Dwarf", "Halfling", "Half-Orc", "Half-Elf", "Dragonborn", "Tiefling", "Gnome", "Warforged",
        "Drow", "Aasimar", "Wood Elf", "Shifter", "Tabaxi", "Eladrin", "Korobokuru",
        "Bladeforged", "Deep Gnome", "Purple Dragon Knight", "Shadar-kai", "Scourge Aasimar", "Tiefling Scoundrel",
        "Wood Elf Ranger", "Razorclaw Shifter", "Tabaxi Trailblazer", "Eladrin Chaosmancer"
    };

    static int PlayableRaceSort(string race)
    {
        int i = Array.FindIndex(PlayableRaceOrder, x => string.Equals(x, race, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 1000 : i;
    }

    IEnumerable<CharacterTemplateChoice> CharacterTemplateChoices()
    {
        if (index?["CharacterTemplates"] is not JsonObject templates) yield break;
        foreach (var kv in templates)
        {
            if (!uint.TryParse(kv.Key, out var id) || kv.Value is not JsonObject o) continue;
            yield return new CharacterTemplateChoice
            {
                Id = id,
                Name = o["Name"]?.ToString() ?? $"0x{id:X8}",
                Race = o["Race"]?.ToString() ?? "Unknown",
                Gender = o["Gender"]?.ToString() ?? "Unspecified",
                Score = o["CandidateScore"]?.GetValue<int>() ?? 0
            };
        }
    }

    async Task PopulateDressingRoomAsync()
    {
        if (!await EnsureBackendAsync()) return;
        try
        {
            await EnsureIndexAsync();
            var races = CharacterTemplateChoices().Select(x => x.Race).Where(x => !string.IsNullOrWhiteSpace(x) && x != "Unknown").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(PlayableRaceSort).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            string? keep = dressingRace.SelectedItem?.ToString();
            dressingRace.BeginUpdate(); dressingRace.Items.Clear();
            dressingRace.Items.AddRange(races.Cast<object>().ToArray()); dressingRace.EndUpdate();
            if (keep != null && dressingRace.Items.Contains(keep)) dressingRace.SelectedItem = keep;
            else if (dressingRace.Items.Count > 0) dressingRace.SelectedIndex = 0;
            dressingInfo.Text = races.Length == 0 ? "No race/body candidates were indexed. Rebuild the asset library." : $"{races.Length:N0} race families discovered from local DDO character records.";
        }
        catch (Exception ex)
        {
            dressingInfo.Text = "Dressing Room index error: " + ex.Message;
        }
    }

    void PopulateDressingGenders()
    {
        if (dressingRace.SelectedItem == null) return;
        string race = dressingRace.SelectedItem.ToString() ?? "";
        var genders = CharacterTemplateChoices().Where(x => string.Equals(x.Race, race, StringComparison.OrdinalIgnoreCase)).Select(x => x.Gender).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        dressingGender.BeginUpdate(); dressingGender.Items.Clear(); dressingGender.Items.AddRange(genders.Cast<object>().ToArray()); dressingGender.EndUpdate();
        if (dressingGender.Items.Count > 0) dressingGender.SelectedIndex = 0;
        PopulateDressingBaseModels();
    }

    void PopulateDressingBaseModels()
    {
        if (dressingRace.SelectedItem == null || dressingGender.SelectedItem == null) return;
        string race = dressingRace.SelectedItem.ToString() ?? "";
        string gender = dressingGender.SelectedItem.ToString() ?? "";
        var choices = CharacterTemplateChoices()
            .Where(x => string.Equals(x.Race, race, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Gender, gender, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        dressingBaseModel.BeginUpdate(); dressingBaseModel.Items.Clear(); dressingBaseModel.Items.AddRange(choices.Cast<object>().ToArray()); dressingBaseModel.EndUpdate();
        if (dressingBaseModel.Items.Count > 0) dressingBaseModel.SelectedIndex = 0;
        dressingInfo.Text = choices.Length == 0 ? "No base-model candidates match that race/body." : $"{choices.Length:N0} base-model candidate(s). Pick a specific record or use the top-ranked DDO candidate.";
    }

    async Task LoadDressingBaseAsync()
    {
        if (dressingBaseModel.SelectedItem is not CharacterTemplateChoice choice) return;
        if (!await EnsureBackendAsync()) return;
        dressingInfo.Text = $"Resolving {choice.Name}…";
        var row = await ResolveAsync(choice.Id, choice.Name);
        if (row == null || row.Setup == 0)
        {
            dressingInfo.Text = "That character record did not resolve to a renderable Setup.";
            return;
        }
        dressingBase = row;
        selected = row;
        details.Text = $"DRESSING ROOM BASE\r\n   {row.Name}\r\n   Race: {choice.Race}\r\n   Body: {choice.Gender}\r\n   DbProperties: 0x{row.DbId:X8}\r\n   Setup: 0x{row.Setup:X8}\r\n\r\nAnimations remain available in the viewer dock and follow this base skeleton.";
        selectedAnimationId = DressingDefaultAnimationId;
        await RefreshDressedCharacterAsync();
        dressingInfo.Text = $"Base loaded: {row.Name}. Default idle {DressingDefaultAnimationId} is looped when compatible. Double-click a slot to equip an item.";
        RefreshDressingSlotRows();
    }

    IEnumerable<EquipmentChoice> EquipmentChoicesForSlot(string slot)
    {
        if (index?["Equipment"] is not JsonObject equipment) yield break;
        foreach (var kv in equipment)
        {
            if (!uint.TryParse(kv.Key, out var id) || kv.Value is not JsonObject e) continue;
            var displaySlots = JsonStringArray(e["CompatibleSlots"]);
            bool canMain = e["CanMainHand"]?.GetValue<bool>() ?? false;
            bool canOff = e["CanOffHand"]?.GetValue<bool>() ?? false;
            uint wt = e["WeenieType"]?.GetValue<uint>() ?? 0;
            bool unclassifiedWearable = displaySlots.Length == 0 && (wt is 0x00030081 or 0x00040081); // Clothing / Armor
            bool autoWearable = unclassifiedWearable && (slot is "Head" or "Armor" or "Cloak");
            bool matches = slot switch
            {
                "Main Hand" => canMain,
                    _ => displaySlots.Any(x => string.Equals(x, slot, StringComparison.OrdinalIgnoreCase)) || autoWearable
            };
            if (!matches) continue;
            yield return new EquipmentChoice
            {
                Id = id,
                Name = e["Name"]?.ToString() ?? $"0x{id:X8}",
                Kind = e["EquipmentKind"]?.ToString() ?? "Equipment",
                WeaponType = e["WeaponType"]?.ToString() ?? "",
                WeenieType = wt,
                Slots = displaySlots,
                CanMain = canMain,
                CanOff = canOff,
                TwoHanded = e["IsTwoHanded"]?.GetValue<bool>() ?? false,
                AutoWearable = autoWearable
            };
        }
    }

    string SelectedDressingSlot() => dressingSlots.SelectedItems.Count == 1 ? dressingSlots.SelectedItems[0].Text : "";

    DressingAppearanceSelection[] DressingAppearanceSelections() => dressingEquipment
        .Where(kv => kv.Key is not "Main Hand" and not "Off Hand")
        .Select(kv => new DressingAppearanceSelection(kv.Value.DbId, kv.Key, kv.Value.Appearance))
        .Where(x => x.Id != 0)
        .OrderBy(x => x.Id)
        .ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    async Task RefreshDressedCharacterAsync()
    {
        if (dressingBase == null) return;
        string resumeAnimation = !string.IsNullOrWhiteSpace(selectedAnimationId) ? selectedAnimationId! : DressingDefaultAnimationId;
        bool characterOnly = dressingDisplayMode.SelectedIndex == 1;
        bool equipmentOnly = dressingDisplayMode.SelectedIndex == 2;
        var wearableSelections = characterOnly ? Array.Empty<DressingAppearanceSelection>() : DressingAppearanceSelections();
        int generation = ++previewGeneration;
        await PreviewAsync(dressingBase, generation, wearableSelections);
        if (generation != previewGeneration) return;
        await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.clearDressingRoomItems && window.ddoViewer.clearDressingRoomItems();");
        if (!characterOnly)
        {
            foreach (var kv in dressingEquipment.Where(x => x.Key is "Main Hand" or "Off Hand").ToArray())
                await ApplyDressingItemAsync(kv.Key, kv.Value, applyVisibility: false);
        }
        await ApplyDressingDisplayModeAsync();
        await PlayDressingAnimationAsync(resumeAnimation, setStatus: false);
        if (equipmentOnly && wearableSelections.Length == 0 && !dressingEquipment.Keys.Any(x => x is "Main Hand" or "Off Hand"))
            dressingInfo.Text = "Equipment Only: no visual equipment is selected.";
    }

    async Task ChooseDressingEquipmentAsync()
    {
        string slot = SelectedDressingSlot();
        if (string.IsNullOrWhiteSpace(slot))
        {
            MessageBox.Show("Select an equipment slot first.", "Dressing Room");
            return;
        }
        if (dressingBase == null)
        {
            MessageBox.Show("Load a base character first.", "Dressing Room");
            return;
        }
        await EnsureIndexAsync();
        if (slot == "Off Hand" && dressingEquipment.TryGetValue("Main Hand", out var main) && main.IsTwoHanded)
        {
            MessageBox.Show($"{main.Name} is two-handed and precludes the Off Hand slot.", "Dressing Room");
            return;
        }
        var choices = EquipmentChoicesForSlot(slot).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        using var picker = new EquipmentPickerForm(slot, choices);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedChoice == null) return;
        var choice = picker.SelectedChoice;
        dressingInfo.Text = $"Resolving {choice.Name}…";
        var row = await ResolveAsync(choice.Id, choice.Name);
        if (row == null)
        {
            dressingInfo.Text = "The selected equipment record could not be resolved.";
            return;
        }
        dressingEquipment[slot] = row;
        if (slot == "Main Hand" && row.IsTwoHanded)
        {
            dressingEquipment.Remove("Off Hand");
            await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.removeDressingRoomItem && window.ddoViewer.removeDressingRoomItem('Off Hand');");
        }
        RefreshDressingSlotRows();
        await RefreshDressedCharacterAsync();
    }

    async Task ClearDressingSlotAsync()
    {
        string slot = SelectedDressingSlot();
        if (string.IsNullOrWhiteSpace(slot)) return;
        dressingEquipment.Remove(slot);
        await RefreshDressedCharacterAsync();
        RefreshDressingSlotRows();
        dressingInfo.Text = $"Cleared {slot}.";
    }

    void RefreshDressingSlotRows()
    {
        string[] slots = { "Head", "Armor", "Cloak", "Main Hand", "Off Hand" };
        dressingSlots.BeginUpdate(); dressingSlots.Items.Clear();
        foreach (var slot in slots)
        {
            string name = dressingEquipment.TryGetValue(slot, out var row) ? row.Name : "None";
            string state = "Ready";
            if (slot == "Off Hand" && dressingEquipment.TryGetValue("Main Hand", out var main) && main.IsTwoHanded)
            {
                name = "Locked by two-handed weapon";
                state = "Locked";
            }
            else if (dressingEquipment.TryGetValue(slot, out row))
            {
                if (slot is "Main Hand" or "Off Hand")
                    state = row.Setup != 0 ? "Attachment resolver" : "No standalone model";
                else if (row.Appearance != 0)
                    state = $"Wearable APR 0x{row.Appearance:X8}";
                else
                    state = "Wearable resolver";
            }
            var li = new ListViewItem(slot);
            li.SubItems.Add(name); li.SubItems.Add(state);
            dressingSlots.Items.Add(li);
        }
        dressingSlots.EndUpdate();
        if (dressingSlots.Items.Count > 0 && dressingSlots.SelectedItems.Count == 0) dressingSlots.Items[0].Selected = true;
    }

    async Task<string?> BuildDressingItemPreviewAsync(AssetRow row)
    {
        if (row.Setup == 0) return null;
        var previewFile = Path.Combine(previewCache, $"dress_{row.DbId:X8}_setup_{row.Setup:X8}_v163.glb");
        if (!File.Exists(previewFile) || new FileInfo(previewFile).Length < 64)
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "exporter", "DDOGlbExporter.exe");
            if (!File.Exists(exe)) return null;
            var psi = new ProcessStartInfo(exe, $"0x{row.Setup:X8} \"{previewFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ConfigureExporterEnvironment(psi);
            using var p = Process.Start(psi)!;
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception((stderr + Environment.NewLine + stdout).Trim());
        }
        return $"https://ddocache.ddo/{Uri.EscapeDataString(Path.GetFileName(previewFile))}?v={File.GetLastWriteTimeUtc(previewFile).Ticks}";
    }

    async Task ApplyDressingItemAsync(string slot, AssetRow row, bool applyVisibility = true)
    {
        if (dressingBase == null) return;

        // Wearables are not standalone scene props. Their APR/WState appearance is folded
        // into the base character by the wearable resolver. Rendering their raw Setup as a
        // second model is what produced fence/random-texture artifacts in 1.7.2.
        if (slot is not "Main Hand" and not "Off Hand")
        {
            dressingInfo.Text = row.Appearance != 0
                ? $"{row.Name}: resolving wearable appearance 0x{row.Appearance:X8} for {slot}."
                : $"{row.Name}: no safe wearable appearance was resolved; raw standalone geometry is intentionally suppressed.";
            return;
        }

        if (row.Setup == 0)
        {
            dressingInfo.Text = $"{row.Name} has no standalone Setup to attach to the {slot}.";
            return;
        }
        try
        {
            string? url = await BuildDressingItemPreviewAsync(row);
            if (url == null) return;
            string slotJs = JsonSerializer.Serialize(slot);
            string urlJs = JsonSerializer.Serialize(url);
            string optionsJs = JsonSerializer.Serialize(new
            {
                weaponType = row.WeaponTypeName,
                twoHanded = row.IsTwoHanded,
                itemName = row.Name
            });
            await RunViewerScriptAsync($"window.ddoViewer && window.ddoViewer.setDressingRoomItem && window.ddoViewer.setDressingRoomItem({slotJs},{urlJs},{optionsJs});");

            // 1.7.2: resolve the equipped item's own effect graph, not the base character's.
            // The viewer queues this payload if the weapon GLB is still loading, then parents
            // auxiliary models / aura sprites to the weapon source so they follow the hand.
            await ResolveVisualEffectsAsync(row, previewGeneration, slot);

            if (applyVisibility) await ApplyDressingDisplayModeAsync();
            dressingInfo.Text = $"Equipped {row.Name} in {slot}. Hand attachment and item VFX use the 1.7.2 equipment resolver.";
        }
        catch (Exception ex)
        {
            dressingInfo.Text = $"Could not preview {row.Name}: {ex.Message}";
        }
    }

    async Task ApplyDressingDisplayModeAsync()
    {
        string mode = dressingDisplayMode.SelectedIndex == 2 ? "equipment" : "combined";
        await RunViewerScriptAsync($"window.ddoViewer && window.ddoViewer.setDressingRoomVisibility && window.ddoViewer.setDressingRoomVisibility('{mode}');");
    }

    void LoadBuiltInAnimationAliases()
    {
        builtInAnimationAliases.Clear();
        try
        {
            if (!File.Exists(builtInAnimationAliasesPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(builtInAnimationAliasesPath));
            if (map == null) return;
            foreach (var kv in map)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    builtInAnimationAliases[kv.Key] = kv.Value.Trim();
        }
        catch { }
    }

    void LoadAnimationAliases()
    {
        animationAliases.Clear();
        try
        {
            if (!File.Exists(animationAliasesPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(animationAliasesPath));
            if (map == null) return;
            foreach (var kv in map)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    animationAliases[kv.Key] = kv.Value.Trim();
        }
        catch { }
    }

    void SaveAnimationAliases()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(animationAliasesPath)!);
            File.WriteAllText(animationAliasesPath, JsonSerializer.Serialize(animationAliases, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    Dictionary<string, string> EffectiveAnimationAliases()
    {
        var merged = new Dictionary<string, string>(builtInAnimationAliases, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in animationAliases) merged[kv.Key] = kv.Value;
        return merged;
    }

    async Task PushAnimationAliasesAsync()
    {
        string js = JsonSerializer.Serialize(EffectiveAnimationAliases());
        await RunViewerScriptAsync($"window.ddoViewer && window.ddoViewer.setAnimationAliases && window.ddoViewer.setAnimationAliases({js});");
    }

    async Task<bool> PlayDressingAnimationAsync(string id, bool setStatus = true)
    {
        if (!dressingRoomMode || dressingBase == null || string.IsNullOrWhiteSpace(id)) return false;
        if (!compatibleAnimationIds.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            if (setStatus) dressingInfo.Text = $"Animation {id} is not compatible with this base skeleton; leaving the character in its current/static pose.";
            return false;
        }
        await RunViewerScriptAsync($"window.ddoViewer && window.ddoViewer.playAnimationById && window.ddoViewer.playAnimationById({JsonSerializer.Serialize(id)}, true);");
        selectedAnimationId = id;
        if (setStatus) dressingInfo.Text = $"Playing {id} on a loop.";
        return true;
    }

    sealed class EquipmentPickerForm : Form
    {
        readonly List<EquipmentChoice> all;
        readonly TextBox filter = new() { Dock = DockStyle.Top, Height = 36, PlaceholderText = "Search this equipment slot…" };
        readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        readonly Button choose = new() { Text = "Equip Selected", Dock = DockStyle.Bottom, Height = 42 };
        public EquipmentChoice? SelectedChoice { get; private set; }

        public EquipmentPickerForm(string slot, List<EquipmentChoice> choices)
        {
            all = choices;
            Text = $"Choose {slot} — DDO Studio";
            Width = 820; Height = 680; StartPosition = FormStartPosition.CenterParent;
            BackColor = Bg; ForeColor = TextMain; Font = new Font("Segoe UI", 10f);
            list.Columns.Add("Name", 360); list.Columns.Add("Kind", 150); list.Columns.Add("Type", 140); list.Columns.Add("ID", 110);
            list.BackColor = Panel; list.ForeColor = TextMain; list.BorderStyle = BorderStyle.FixedSingle;
            filter.BackColor = PanelAlt; filter.ForeColor = TextMain; filter.BorderStyle = BorderStyle.FixedSingle;
            StylePrimaryButton(choose);
            Controls.Add(list); Controls.Add(choose); Controls.Add(filter);
            filter.TextChanged += (_, _) => RefreshRows();
            choose.Click += (_, _) => Accept();
            list.DoubleClick += (_, _) => Accept();
            RefreshRows();
        }

        void RefreshRows()
        {
            string q = filter.Text.Trim();
            var rows = string.IsNullOrWhiteSpace(q) ? all : all.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Kind.Contains(q, StringComparison.OrdinalIgnoreCase) || x.WeaponType.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            list.BeginUpdate(); list.Items.Clear();
            foreach (var c in rows)
            {
                var li = new ListViewItem(c.Name) { Tag = c };
                li.SubItems.Add(c.Kind); li.SubItems.Add(c.WeaponType); li.SubItems.Add($"0x{c.Id:X8}");
                list.Items.Add(li);
            }
            list.EndUpdate();
        }

        void Accept()
        {
            if (list.SelectedItems.Count != 1) return;
            SelectedChoice = list.SelectedItems[0].Tag as EquipmentChoice;
            if (SelectedChoice == null) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    void DrawResultsColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(PanelAlt);
        using var border = new Pen(Border);
        using var headerFont = new Font("Segoe UI Semibold", 9.5f);
        e.Graphics.FillRectangle(bg, e.Bounds);
        e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
        var rect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, headerFont, rect, TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void DrawResultsSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        bool selectedRow = e.Item.Selected;
        var bgColor = selectedRow ? Color.FromArgb(55, 45, 27) : Panel;
        var fgColor = selectedRow ? Color.White : TextMain;
        using var bg = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(bg, e.Bounds);
        var rect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, rect, fgColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    static void ApplyTheme(Control root)
    {
        root.BackColor = Bg;
        root.ForeColor = TextMain;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox t:
                    t.BackColor = PanelAlt; t.ForeColor = TextMain; t.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListView l:
                    l.BackColor = Panel; l.ForeColor = TextMain; l.BorderStyle = BorderStyle.FixedSingle;
                    l.HeaderStyle = ColumnHeaderStyle.Nonclickable;
                    break;
                case ComboBox cb:
                    cb.BackColor = PanelAlt; cb.ForeColor = TextMain; cb.FlatStyle = FlatStyle.Flat;
                    break;
                case SplitContainer sc:
                    sc.BackColor = Border; sc.Panel1.BackColor = Bg; sc.Panel2.BackColor = Bg;
                    break;
                case TableLayoutPanel or FlowLayoutPanel:
                    c.BackColor = Bg;
                    break;
                case Panel p when p.BackColor == SystemColors.Control:
                    p.BackColor = Bg;
                    break;
            }
            ApplyTheme(c);
        }
        root.BackColor = root is Panel p2 && p2.BackColor != SystemColors.Control ? p2.BackColor : root.BackColor;
    }

    static void StylePrimaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.BackColor = Accent; b.ForeColor = Color.FromArgb(20, 20, 20);
        b.UseVisualStyleBackColor = false; b.Font = new Font("Segoe UI Semibold", 10f); b.Cursor = Cursors.Hand;
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = AccentHover; };
        b.MouseLeave += (_, _) => b.BackColor = Accent;
    }

    static void StyleSecondaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Border; b.FlatAppearance.BorderSize = 1; b.BackColor = PanelAlt; b.ForeColor = TextMain;
        b.UseVisualStyleBackColor = false; b.Font = new Font("Segoe UI Semibold", 9.5f); b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 48, 57);
    }

    static void StyleToggleButton(CheckBox b)
    {
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Border; b.FlatAppearance.BorderSize = 1; b.FlatAppearance.CheckedBackColor = Color.FromArgb(89, 70, 34);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 48, 57); b.UseVisualStyleBackColor = false;
        b.BackColor = PanelAlt; b.ForeColor = TextMain; b.Font = new Font("Segoe UI Semibold", 10f); b.Cursor = Cursors.Hand;
    }

    static string? DetectDdoPath()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\bc8a6440-918f-11dd-ad8b-0800200c9a66_is1");
            var p = k?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return p;
        }
        catch { }
        var common = @"C:\Program Files (x86)\StandingStoneGames\Dungeons & Dragons Online";
        return Directory.Exists(common) ? common : null;
    }

    void BrowseDdo()
    {
        using var f = new FolderBrowserDialog { Description = "Choose your Dungeons & Dragons Online folder", UseDescriptionForTitle = true };
        if (f.ShowDialog() == DialogResult.OK) ddoPath.Text = f.SelectedPath;
    }

    async Task EnsureHumanoidAnimationTestAssetAsync()
    {
        try
        {
            var outGlb = Path.Combine(previewCache, "humanoid_test_58bone.glb");
            if (File.Exists(outGlb) && new FileInfo(outGlb).Length > 64) return;

            var exe = Path.Combine(AppContext.BaseDirectory, "exporter", "DDOGlbExporter.exe");
            if (!File.Exists(exe) || !Directory.Exists(ddoPath.Text)) return;

            Directory.CreateDirectory(previewCache);
            uint[] setups = { 0x04000007, 0x040001D7, 0x0400066A, 0x04000674, 0x0400068B, 0x0400068C };
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Studio", "AnimationDiagnostics");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "playback-test-export.log");

            foreach (var setup in setups)
            {
                try
                {
                    if (File.Exists(outGlb)) File.Delete(outGlb);
                    var psi = new ProcessStartInfo(exe, $"0x{setup:X8} \"{outGlb}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    ConfigureExporterEnvironment(psi);
                    using var proc = Process.Start(psi)!;
                    string stdout = await proc.StandardOutput.ReadToEndAsync();
                    string stderr = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    await File.AppendAllTextAsync(logPath, $"\r\n===== 0x{setup:X8} =====\r\n{stdout}\r\n{stderr}\r\n");
                    if (proc.ExitCode == 0 && File.Exists(outGlb) && new FileInfo(outGlb).Length > 64)
                    {
                        await File.WriteAllTextAsync(Path.Combine(previewCache, "humanoid_test_setup.txt"), $"0x{setup:X8}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    try { await File.AppendAllTextAsync(logPath, $"\r\n0x{setup:X8} ERROR: {ex}\r\n"); } catch { }
                }
            }
        }
        catch { }
    }

    async Task EnsureViewerAsync()
    {
        if (viewerReady) return;
        var viewerDir = Path.Combine(AppContext.BaseDirectory, "viewer");
        var indexHtml = Path.Combine(viewerDir, "index.html");
        if (!File.Exists(indexHtml))
        {
            previewStatus.Text = "3D preview unavailable: viewer files are missing.";
            return;
        }

        try
        {
            previewStatus.Text = "3D preview: starting viewer…";

            await EnsureHumanoidAnimationTestAssetAsync();

            // Construct WebView2 lazily. If WebView2 is unavailable or broken, the
            // search/export UI still opens and remains usable.
            if (viewer == null)
            {
                viewer = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(6, 18, 28) };
                viewerHost.Controls.Add(viewer);
            }

            // WebView2's default user-data folder is created beside the EXE. That works
            // in a portable folder, but an installed copy lives under Program Files where
            // normal users cannot write. Always place the browser profile in LocalAppData.
            var webViewData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDO Studio", "WebView2");
            Directory.CreateDirectory(webViewData);
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, webViewData);
            await viewer.EnsureCoreWebView2Async(webViewEnvironment);
            viewer.CoreWebView2.Settings.AreDevToolsEnabled = false;
            viewer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            if (!viewerMessagesHooked)
            {
                viewer.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try { _ = HandleViewerMessageAsync(e.TryGetWebMessageAsString()); } catch { }
                };
                viewerMessagesHooked = true;
            }
            viewer.CoreWebView2.SetVirtualHostNameToFolderMapping("viewer.ddo", viewerDir, CoreWebView2HostResourceAccessKind.Allow);
            viewer.CoreWebView2.SetVirtualHostNameToFolderMapping("ddocache.ddo", previewCache, CoreWebView2HostResourceAccessKind.Allow);

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => ready.TrySetResult(e.IsSuccess);
            viewer.NavigationCompleted += OnNav;
            viewer.CoreWebView2.Navigate("https://viewer.ddo/index.html");
            var ok = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            viewer.NavigationCompleted -= OnNav;
            if (!ok) throw new InvalidOperationException("The embedded viewer page could not be loaded.");

            viewerReady = true;
            await PushAnimationAliasesAsync();
            previewStatus.Text = "3D preview: ready";
        }
        catch (Exception ex)
        {
            previewStatus.Text = "3D preview unavailable. WebView2 Runtime may be missing.";
            details.Text = $"3D viewer startup error:\r\n{ex.Message}\r\n\r\nModel search and export still work.";
        }
    }

    async Task RunViewerScriptAsync(string script)
    {
        if (!viewerReady) await EnsureViewerAsync();
        if (!viewerReady || viewer?.CoreWebView2 == null) return;
        try { await viewer.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    async Task HandleViewerMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var messageType = typeEl.GetString();

            if (messageType == "searchAnimations")
            {
                string q = root.TryGetProperty("q", out var qEl) ? (qEl.GetString() ?? "") : "";
                string statusFilter = root.TryGetProperty("status", out var stEl) ? (stEl.GetString() ?? "all") : "all";
                string havokType = root.TryGetProperty("havokType", out var htEl) ? (htEl.GetString() ?? "all") : "all";
                int page = root.TryGetProperty("page", out var pgEl) && pgEl.TryGetInt32(out var pg) ? Math.Max(1, pg) : 1;
                int pageSize = root.TryGetProperty("pageSize", out var psEl) && psEl.TryGetInt32(out var ps) ? Math.Clamp(ps, 25, 1000) : 250;
                int? bones = root.TryGetProperty("boneCount", out var bcEl) && bcEl.ValueKind == JsonValueKind.Number && bcEl.TryGetInt32(out var bc) ? bc : null;
                float? minDuration = root.TryGetProperty("minDuration", out var mnEl) && mnEl.ValueKind == JsonValueKind.Number && mnEl.TryGetSingle(out var mn) ? mn : null;
                float? maxDuration = root.TryGetProperty("maxDuration", out var mxEl) && mxEl.ValueKind == JsonValueKind.Number && mxEl.TryGetSingle(out var mx) ? mx : null;
                var parts = new List<string>
                {
                    $"q={Uri.EscapeDataString(q)}", $"status={Uri.EscapeDataString(statusFilter)}", $"havokType={Uri.EscapeDataString(havokType)}",
                    $"page={page}", $"pageSize={pageSize}"
                };
                if (bones.HasValue) parts.Add($"boneCount={bones.Value}");
                if (minDuration.HasValue) parts.Add($"minDuration={minDuration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                if (maxDuration.HasValue) parts.Add($"maxDuration={maxDuration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                status.Text = "Searching global animation index…";
                using var sr = await http.GetAsync("AnimationBrowser/search?" + string.Join("&", parts));
                var body = await sr.Content.ReadAsStringAsync();
                if (!sr.IsSuccessStatusCode) throw new InvalidOperationException($"Animation search failed: {(int)sr.StatusCode} {body}");
                await RunViewerScriptAsync($"window.ddoViewer.setGlobalAnimationResults({body});");
                status.Text = "Animation browser ready";
                return;
            }

            if (messageType == "togglePresentation")
            {
                BeginInvoke(new Action(TogglePresentationMode));
                return;
            }

            if (messageType == "renameAnimation")
            {
                string? renameId = root.TryGetProperty("id", out var ridEl) ? ridEl.GetString() : null;
                string? alias = root.TryGetProperty("name", out var rnEl) ? rnEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(renameId))
                {
                    if (string.IsNullOrWhiteSpace(alias)) animationAliases.Remove(renameId);
                    else animationAliases[renameId] = alias.Trim();
                    SaveAnimationAliases();
                    await PushAnimationAliasesAsync();
                    status.Text = string.IsNullOrWhiteSpace(alias) ? $"Animation name reset — {renameId}" : $"Animation renamed — {renameId} → {alias.Trim()}";
                }
                return;
            }

            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) return;

            if (messageType == "preflightAnimation")
            {
                status.Text = $"Inspecting animation {id}…";
                using var pr = await http.GetAsync($"AnimationBrowser/{Uri.EscapeDataString(id)}/preflight");
                var body = await pr.Content.ReadAsStringAsync();
                if (!pr.IsSuccessStatusCode)
                    body = JsonSerializer.Serialize(new { id, status = "error", playable = false, detail = $"{(int)pr.StatusCode} {pr.StatusCode}: {body}" });
                await RunViewerScriptAsync($"window.ddoViewer.setAnimationPreflight({body});");
                status.Text = $"Animation inspected — {id}";
                return;
            }

            if (messageType == "playAnimation")
            {
                status.Text = $"Decoding compatible animation {id}…";
                previewStatus.Text = $"Animation: decoding {id}…";
                try
                {
                    using var dr = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/decode?frames=true");
                    var body = await dr.Content.ReadAsStringAsync();
                    if (!dr.IsSuccessStatusCode)
                    {
                        string diagnosticHint = "";
                        try
                        {
                            var failDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Studio", "AnimationDiagnostics", "RawHKX");
                            Directory.CreateDirectory(failDir);
                            var safeFail = id.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("/", "_").Replace("\\", "_");
                            var failHkx = Path.Combine(failDir, $"anim_{safeFail}.hkx");
                            var failInspect = Path.Combine(failDir, $"anim_{safeFail}.inspect.json");
                            var failError = Path.Combine(failDir, $"anim_{safeFail}.decode-error.json");
                            using (var rr = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/raw"))
                                if (rr.IsSuccessStatusCode) await File.WriteAllBytesAsync(failHkx, await rr.Content.ReadAsByteArrayAsync());
                            using (var ir = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/inspect"))
                                if (ir.IsSuccessStatusCode) await File.WriteAllTextAsync(failInspect, await ir.Content.ReadAsStringAsync());
                            await File.WriteAllTextAsync(failError, body);
                            diagnosticHint = $" Diagnostics saved to {failDir}.";
                        }
                        catch { }
                        var errJs = JsonSerializer.Serialize($"{(int)dr.StatusCode} {dr.StatusCode}: {body}{diagnosticHint}");
                        var idJs = JsonSerializer.Serialize(id);
                        await RunViewerScriptAsync($"window.ddoViewer.animationPlaybackError({idJs}, {errJs});");
                        status.Text = $"Animation decode failed — {id}";
                        previewStatus.Text = $"Animation decode failed: {id}";
                        return;
                    }

                    Directory.CreateDirectory(previewCache);
                    var safePlay = id.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("/", "_").Replace("\\", "_");
                    var playPath = Path.Combine(previewCache, $"anim_{safePlay}.playback.json");
                    await File.WriteAllTextAsync(playPath, body);
                    var url = $"https://ddocache.ddo/{Uri.EscapeDataString(Path.GetFileName(playPath))}?v={File.GetLastWriteTimeUtc(playPath).Ticks}";
                    await RunViewerScriptAsync($"window.ddoViewer.loadDecodedAnimation({JsonSerializer.Serialize(url)}, {JsonSerializer.Serialize(id)});");
                    selectedAnimationId = id;
                    status.Text = $"Animation loaded — {id}";
                    previewStatus.Text = $"Animation playback: {id}";
                }
                catch (Exception playEx)
                {
                    await RunViewerScriptAsync($"window.ddoViewer.animationPlaybackError({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(playEx.Message)});");
                    status.Text = "Animation playback failed";
                }
                return;
            }

            if (messageType != "exportAnimation") return;
            status.Text = $"Exporting Havok animation {id}…";
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Studio", "AnimationDiagnostics", "RawHKX");
            Directory.CreateDirectory(dir);
            var safe = id.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("/", "_").Replace("\\", "_");
            var hkxPath = Path.Combine(dir, $"anim_{safe}.hkx");
            var inspectPath = Path.Combine(dir, $"anim_{safe}.inspect.json");
            var decodedPath = Path.Combine(dir, $"anim_{safe}.decoded.json");

            using (var r = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/raw"))
            {
                r.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(hkxPath, await r.Content.ReadAsByteArrayAsync());
            }
            try
            {
                using var ir = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/inspect");
                if (ir.IsSuccessStatusCode) await File.WriteAllTextAsync(inspectPath, await ir.Content.ReadAsStringAsync());
            }
            catch { }

            bool decodedOk = false;
            string decodedSummary = "Native decode was not available for this record.";
            try
            {
                using var dr = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/decoded-file");
                if (dr.IsSuccessStatusCode)
                {
                    await File.WriteAllBytesAsync(decodedPath, await dr.Content.ReadAsByteArrayAsync());
                    decodedOk = true;
                    using var summaryResponse = await http.GetAsync($"AnimationCatalog/{Uri.EscapeDataString(id)}/decode?frames=false");
                    if (summaryResponse.IsSuccessStatusCode)
                    {
                        using var summaryDoc = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
                        var d = summaryDoc.RootElement.GetProperty("decoded");
                        decodedSummary = $"Decoded {d.GetProperty("transformTrackCount").GetInt32()} tracks, {d.GetProperty("numFrames").GetInt32()} frames, {d.GetProperty("duration").GetSingle():0.###} seconds.";
                    }
                }
            }
            catch (Exception dex) { decodedSummary = "Native decoder: " + dex.Message; }

            status.Text = decodedOk ? $"Havok animation decoded — {id}" : $"Havok animation exported — {id}";
            previewStatus.Text = decodedOk ? $"Decoded animation saved: {Path.GetFileName(decodedPath)}" : $"Animation sample saved: {Path.GetFileName(hkxPath)}";
            MessageBox.Show($"Saved raw DDO Havok animation:\r\n{hkxPath}\r\n\r\n{decodedSummary}\r\n\r\n" + (decodedOk ? $"Full native decode:\r\n{decodedPath}\r\n\r\nTrack indices are decoded; the remaining job is resolving DDO's track-to-skeleton-joint binding." : "The raw HKX and inspect JSON were still preserved for unsupported records."), "DDO Studio — Native Havok Decoder");
        }
        catch (Exception ex)
        {
            status.Text = "Animation export failed";
            MessageBox.Show(ex.Message, "DDO Studio — Animation Export");
        }
    }

    void ConfigureExporterEnvironment(ProcessStartInfo psi)
    {
        if (http.BaseAddress != null)
            psi.Environment["DDO_STUDIO_API_BASE"] = http.BaseAddress.ToString();
        if (!string.IsNullOrWhiteSpace(ddoPath.Text))
            psi.Environment["DDO_INSTALL_PATH"] = ddoPath.Text;
    }

    async Task<bool> EnsureBackendAsync()
    {
        if (!File.Exists(Path.Combine(ddoPath.Text, "client_general.dat")))
        {
            MessageBox.Show("That folder does not look like a DDO installation.");
            return false;
        }
        try
        {
            using var r = await http.GetAsync("RawDat/IdRanges");
            if (r.IsSuccessStatusCode)
            {
                backendStatus.Text = "● Game data ready"; backendStatus.ForeColor = Color.FromArgb(111, 207, 151);
                return true;
            }
        }
        catch { }

        var exe = Path.Combine(AppContext.BaseDirectory, "backend", "DdoDatApi.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show("The bundled backend is missing. Rebuild DDO Studio.");
            return false;
        }
        backendStatus.Text = "Starting game data…"; backendStatus.ForeColor = Accent;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio");
        Directory.CreateDirectory(appData);
        var backendLog = Path.Combine(appData, "backend.log");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["DDO_INSTALL_PATH"] = ddoPath.Text;
        psi.Environment["DDO_ASSET_STUDIO_DATA"] = appData;
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{backendPort}";
        try
        {
            File.AppendAllText(backendLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DDO Studio 1.7.2 launching bundled backend on 127.0.0.1:{backendPort}.{Environment.NewLine}");
        }
        catch { }
        backend = Process.Start(psi);
        if (backend != null)
        {
            void LogLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                try { File.AppendAllText(backendLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); } catch { }
            }
            backend.OutputDataReceived += (_, e) => LogLine(e.Data);
            backend.ErrorDataReceived += (_, e) => LogLine(e.Data);
            backend.BeginOutputReadLine();
            backend.BeginErrorReadLine();
        }
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            try
            {
                using var r = await http.GetAsync("RawDat/IdRanges");
                if (r.IsSuccessStatusCode)
                {
                    backendStatus.Text = "● Game data ready"; backendStatus.ForeColor = Color.FromArgb(111, 207, 151);
                    return true;
                }
            }
            catch { }
        }
        backendStatus.Text = "Game data unavailable"; backendStatus.ForeColor = Color.FromArgb(232, 111, 111);
        MessageBox.Show($"The local DDO data service could not start.\r\n\r\nCheck that the selected DDO folder is correct. A diagnostic log was written to:\r\n{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio", "backend.log")}", "DDO Studio");
        return false;
    }

    async Task EnsureIndexAsync()
    {
        if (index != null) return;

        using (var first = await http.GetAsync("Cache/Download"))
        {
            if (first.IsSuccessStatusCode)
            {
                index = JsonNode.Parse(await first.Content.ReadAsStringAsync())!.AsObject();
                return;
            }
        }

        status.Text = "Preparing asset library for first use…";
        using (var kick = await http.PostAsync("Cache/Rebuild", null))
        {
            if (!kick.IsSuccessStatusCode && (int)kick.StatusCode != 208)
                throw new InvalidOperationException($"DDO Studio could not prepare the asset library (HTTP {(int)kick.StatusCode}).");
        }

        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(1000);
            if (i % 5 == 0) status.Text = $"Preparing asset library… {i / 60}:{i % 60:00}";
            using var metadata = await http.GetAsync("Cache/Metadata");
            if (!metadata.IsSuccessStatusCode) continue;
            using var download = await http.GetAsync("Cache/Download");
            if (!download.IsSuccessStatusCode) continue;
            index = JsonNode.Parse(await download.Content.ReadAsStringAsync())!.AsObject();
            status.Text = "Asset library ready ✓";
            return;
        }

        throw new InvalidOperationException("DDO Studio is still preparing the asset library. Please try again in a moment.");
    }

    async Task RebuildAsync()
    {
        if (!await EnsureBackendAsync()) return;
        SetBusy(true, "Building DDO search index… this can take a little while.");
        try
        {
            await http.PostAsync("Cache/Rebuild", null);
            for (int i = 0; i < 600; i++)
            {
                await Task.Delay(1000);
                using var r = await http.GetAsync("Cache/Metadata");
                if (r.IsSuccessStatusCode)
                {
                    index = null;
                    status.Text = "Index ready ✓";
                    return;
                }
            }
            MessageBox.Show("Index build is still running. You can try Search again in a moment.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
        finally { SetBusy(false, status.Text); }
    }

    async Task SearchAsync()
    {
        var terms = (search.Text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return;

        lastBrowseWasViewAll = false;
        if (!await EnsureBackendAsync()) return;
        SetBusy(true, "Searching models…");
        results.Items.Clear();
        selected = null;
        exportButton.Enabled = false;
        await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.clear();");
        previewStatus.Text = "3D preview: waiting for selection";
        try
        {
            await EnsureIndexAsync();
            var lookup = index?["NameLookup"]?.AsObject() ?? throw new Exception("NameLookup is missing from the index.");
            var candidates = lookup
                .Where(kv => terms.All(term => (kv.Value?.ToString() ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                .Take(750)
                .ToList();

            int found = 0;
            var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in candidates)
            {
                if (!uint.TryParse(kv.Key, out var id)) continue;
                try
                {
                    var row = await ResolveAsync(id, kv.Value?.ToString() ?? $"0x{id:X8}");
                    if (row == null || !row.HasStandaloneModel || !IsLibraryBrowsableAsset(row)) continue;
                    if (hideDuplicates.Checked)
                    {
                        string dedupeKey = $"{row.AssetClass}:{row.VisualDesc:X8}:{row.Setup:X8}:{row.Appearance:X8}";
                        if (!seenAssets.Add(dedupeKey)) continue;
                    }
                    AddResultRow(row);
                    found++;
                }
                catch { }
            }
            status.Text = $"Search complete — {found} model(s)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DDO Studio");
            status.Text = "Search failed";
        }
        finally { SetBusy(false, status.Text); }
    }

    void AddResultRow(AssetRow row)
    {
        var li = new ListViewItem(row.Name) { Tag = row };
        li.SubItems.Add(row.AssetClass);
        li.SubItems.Add($"0x{row.DbId:X8}");
        li.SubItems.Add(row.Setup != 0 ? $"0x{row.Setup:X8}" : "—");
        results.Items.Add(li);
    }

    async Task ViewAllAsync()
    {
        if (!await EnsureBackendAsync()) return;
        lastBrowseWasViewAll = true;
        SetBusy(true, "Loading renderable objects…");
        results.Items.Clear();
        selected = null;
        exportButton.Enabled = false;
        selectedAnimationId = null;
        await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.clear();");
        previewStatus.Text = "3D preview: waiting for selection";
        try
        {
            await EnsureIndexAsync();
            var lookup = index?["NameLookup"]?.AsObject() ?? throw new Exception("NameLookup is missing from the index.");
            var entries = lookup.ToList();
            int processed = 0, found = 0;
            var rows = new List<AssetRow>();
            var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in entries)
            {
                if (!uint.TryParse(kv.Key, out var id)) continue;
                try
                {
                    var row = await ResolveAsync(id, kv.Value?.ToString() ?? $"0x{id:X8}");
                    processed++;
                    if (processed % 100 == 0) status.Text = $"Scanning objects… {processed:N0}/{entries.Count:N0}";
                    if (row == null || !row.HasStandaloneModel || !IsLibraryBrowsableAsset(row)) continue;
                    if (hideDuplicates.Checked)
                    {
                        string dedupeKey = $"{row.AssetClass}:{row.VisualDesc:X8}:{row.Setup:X8}:{row.Appearance:X8}";
                        if (!seenAssets.Add(dedupeKey)) continue;
                    }
                    rows.Add(row);
                }
                catch { processed++; }
            }

            results.BeginUpdate();
            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                AddResultRow(row);
                found++;
            }
            results.EndUpdate();
            status.Text = $"View All — {found:N0} renderable model(s)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DDO Studio");
            status.Text = "View All failed";
        }
        finally { SetBusy(false, status.Text); }
    }

    async Task<AssetRow?> ResolveAsync(uint id, string name)
    {
        JsonNode? db = null;
        try
        {
            using var r = await http.GetAsync($"DbProperties/{id}");
            if (r.IsSuccessStatusCode)
                db = JsonNode.Parse(await r.Content.ReadAsStringAsync());
        }
        catch { }
        if (db == null) return null;

        JsonObject? itemData = null;
        uint weenieType = 0;
        string weenieTypeName = "Unknown";
        bool equippable = false;
        string equipmentKind = "";
        string[] equipSlots = Array.Empty<string>();
        string[] compatibleSlotsRaw = Array.Empty<string>();
        string[] precludedSlotsRaw = Array.Empty<string>();
        string weaponTypeName = "";
        string armorTypeName = "";
        bool canMainHand = false, canOffHand = false, isTwoHanded = false;
        try
        {
            using var ir = await http.GetAsync($"Item/data/{id}");
            if (ir.IsSuccessStatusCode)
            {
                itemData = JsonNode.Parse(await ir.Content.ReadAsStringAsync()) as JsonObject;
                weenieType = itemData?["weenieType"]?.GetValue<uint>() ?? 0;
                weenieTypeName = itemData?["weenieTypeName"]?.ToString() ?? "Unknown";
                equippable = IsEquippableWeenieType(weenieType);
                equipmentKind = itemData?["equipmentKind"]?.ToString() ?? "";
                equipSlots = JsonStringArray(itemData?["compatibleSlots"]);
                compatibleSlotsRaw = JsonStringArray(itemData?["compatibleSlotsRaw"]);
                precludedSlotsRaw = JsonStringArray(itemData?["precludedSlotsRaw"]);
                weaponTypeName = itemData?["weaponTypeName"]?.ToString() ?? "";
                armorTypeName = itemData?["armorTypeName"]?.ToString() ?? "";
                canMainHand = itemData?["canMainHand"]?.GetValue<bool>() ?? false;
                canOffHand = itemData?["canOffHand"]?.GetValue<bool>() ?? false;
                isTwoHanded = itemData?["isTwoHanded"]?.GetValue<bool>() ?? false;
            }
        }
        catch { }

        uint phys = FindPropertyDid(db, "PhysObj", 0x47000000, 0x47FFFFFF);
        uint vis = 0, setup = 0, appearance = 0;
        bool entityNpc = false;
        if (phys != 0)
        {
            try
            {
                using var er = await http.GetAsync($"EntityDesc/0x{phys:X8}");
                if (er.IsSuccessStatusCode)
                {
                    var ent = JsonNode.Parse(await er.Content.ReadAsStringAsync());
                    vis = ParseDid(ent?["visualDescId"]?.ToString());
                    entityNpc = HasPropertyEnumValue(ent, "Render_LODClass", "NPC");
                }
            }
            catch { }
        }

        // 0x1F000013 is the generic purple-bag/inventory placeholder, not the worn model.
        if (equippable && vis == 0x1F000013) vis = 0;

        if (vis != 0)
        {
            try
            {
                var directVisual = await ResolveVisualDescriptionAsync(vis);
                if (directVisual.Setup != 0) setup = directVisual.Setup;
                if (directVisual.Appearance != 0) appearance = directVisual.Appearance;
            }
            catch { }
        }

        // Equippable records can expose an inventory placeholder visual while their renderable
        // data lives deeper in the relationship chain. Preserve this resolver for standalone
        // weapon/shield browsing and for internal composition paths.
        bool wearableNeedsAppearance = weenieType is 0x00030081 or 0x00040081; // Clothing / Armor
        if (equippable && (setup == 0 || vis == 0 || (wearableNeedsAppearance && appearance == 0)))
        {
            try
            {
                // Even when the VisualDescription already supplied an authoritative Setup,
                // wearable records may keep their APR in a shallow WState branch. Recover only
                // missing pieces here; the direct VisualDescription -> Setup edge is re-applied
                // below and remains authoritative.
                var resolved = await ResolveEquipmentRenderableAsync(id);
                if (setup == 0 && resolved.Setup != 0) setup = resolved.Setup;
                if (vis == 0 && resolved.VisualDesc != 0) vis = resolved.VisualDesc;
                if (appearance == 0 && resolved.Appearance != 0) appearance = resolved.Appearance;
            }
            catch { }
        }

        // If relationship discovery recovers a VisualDescription, resolve that record's own
        // direct Setup afterwards and let the direct edge win over unrelated graph candidates.
        if (equippable && vis != 0)
        {
            try
            {
                var directVisual = await ResolveVisualDescriptionAsync(vis);
                if (directVisual.Setup != 0) setup = directVisual.Setup;
                if (appearance == 0 && directVisual.Appearance != 0) appearance = directVisual.Appearance;
            }
            catch { }
        }

        bool structuralCreature = !equippable && (
            entityNpc ||
            HasPropertyName(db, "Creature_Species") ||
            HasPropertyName(db, "Creature_Genus") ||
            HasPropertyName(db, "Character_Class") ||
            HasPropertyName(db, "Combat_InnateAttack_Array"));
        string assetClass = ClassifyAsset(weenieTypeName, equippable, setup != 0, structuralCreature);
        // 1.7.2: the Asset Library is a model browser, not a raw database browser.
        // Records without a real Setup cannot be previewed/exported and only produce
        // 0x00000000 exporter failures, so never surface them as Library rows.
        if (setup == 0) return null;

        return new AssetRow
        {
            DbId = id,
            Name = name,
            PhysObj = phys,
            VisualDesc = vis,
            Appearance = appearance,
            Setup = setup,
            WeenieType = weenieType,
            WeenieTypeName = weenieTypeName,
            AssetClass = assetClass,
            ItemData = itemData,
            IsEquippable = equippable,
            EquipmentKind = equipmentKind,
            EquipSlots = equipSlots,
            CompatibleSlotsRaw = compatibleSlotsRaw,
            PrecludedSlotsRaw = precludedSlotsRaw,
            WeaponTypeName = weaponTypeName,
            ArmorTypeName = armorTypeName,
            CanMainHand = canMainHand,
            CanOffHand = canOffHand,
            IsTwoHanded = isTwoHanded
        };
    }

    async Task<(uint Setup, uint Appearance)> ResolveVisualDescriptionAsync(uint visualId)
    {
        if (visualId == 0 || visualId == 0x1F000013) return (0, 0);

        try
        {
            using var vr = await http.GetAsync($"RawDat/General/0x{visualId:X8}");
            if (!vr.IsSuccessStatusCode) return (0, 0);

            var blob = await vr.Content.ReadAsByteArrayAsync();
            uint setup = 0, appearance = 0;

            // VisualDescription records are compact and their DIDs are not guaranteed to be
            // four-byte aligned. Scan byte-by-byte, then validate every candidate against the
            // correct DAT before accepting it.
            for (int o = 0; o + 4 <= blob.Length; o++)
            {
                uint candidate = BitConverter.ToUInt32(blob, o);
                if (setup == 0 && candidate is >= 0x04000000 and <= 0x04FFFFFF)
                {
                    try
                    {
                        using var sr = await http.GetAsync($"RawDat/General/0x{candidate:X8}");
                        if (sr.IsSuccessStatusCode) setup = candidate;
                    }
                    catch { }
                }
                else if (appearance == 0 && candidate is >= 0x20000000 and <= 0x20FFFFFF)
                {
                    try
                    {
                        using var ar = await http.GetAsync($"RawDat/General/0x{candidate:X8}");
                        if (ar.IsSuccessStatusCode) appearance = candidate;
                    }
                    catch { }
                }

                if (setup != 0 && appearance != 0) break;
            }

            return (setup, appearance);
        }
        catch
        {
            return (0, 0);
        }
    }

    async Task<(uint VisualDesc, uint Setup, uint Appearance)> ResolveEquipmentRenderableAsync(uint dbId)
    {
        using var r = await http.GetAsync($"EquipmentChain/0x{dbId:X8}?depth=4&maxNodes=500");
        if (!r.IsSuccessStatusCode) return (0, 0, 0);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
            return (0, 0, 0);

        var direct = new HashSet<uint>();
        if (root.TryGetProperty("structuredPropertyReferences", out var directElement) && directElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in directElement.EnumerateArray())
                if (d.TryGetProperty("id", out var de) && de.TryGetUInt32(out var did)) direct.Add(did);
        }

        uint setup = 0, visual = 0, appearance = 0;
        int bestSetupScore = int.MaxValue, bestVisualScore = int.MaxValue, bestAppearanceScore = int.MaxValue;

        foreach (var n in nodesElement.EnumerateArray())
        {
            if (!n.TryGetProperty("id", out var ie) || !ie.TryGetUInt32(out var nid)) continue;
            int depth = n.TryGetProperty("depth", out var dep) && dep.TryGetInt32(out var dv) ? dv : 99;
            byte prefix = (byte)(nid >> 24);
            int renderMeshRefs = 0;
            int setupRefs = 0;
            if (n.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (var re in refs.EnumerateArray())
                {
                    if (!re.TryGetProperty("id", out var rie) || !rie.TryGetUInt32(out var rid)) continue;
                    byte rp = (byte)(rid >> 24);
                    if (rp == 0x06) renderMeshRefs++;
                    else if (rp == 0x04) setupRefs++;
                }
            }

            // Strongly prefer structured/direct property references, shallow records, and
            // Setup records that themselves fan out into RenderMeshes.
            int score = depth * 100 - (direct.Contains(nid) ? 250 : 0);
            if (prefix == 0x04)
            {
                score -= renderMeshRefs * 80;
                if (score < bestSetupScore) { bestSetupScore = score; setup = nid; }
            }
            else if (prefix == 0x1F)
            {
                score -= setupRefs * 40;
                if (nid != 0x1F000013 && score < bestVisualScore) { bestVisualScore = score; visual = nid; }
            }
            else if (prefix == 0x20 && score < bestAppearanceScore)
            {
                bestAppearanceScore = score; appearance = nid;
            }
        }

        // A VisualDescription -> Setup edge is stronger evidence than a Setup found elsewhere
        // in the broad equipment relationship graph. Prefer the direct visual chain whenever
        // we recovered a non-placeholder visual.
        if (visual != 0)
        {
            var directVisual = await ResolveVisualDescriptionAsync(visual);
            if (directVisual.Setup != 0) setup = directVisual.Setup;
            if (appearance == 0 && directVisual.Appearance != 0) appearance = directVisual.Appearance;
        }

        return (visual, setup, appearance);
    }

    static bool IsEquippableWeenieType(uint wt) => wt is 0x00010081 or 0x00020081 or 0x00030081 or 0x00040081 or 0x00070081;

    static bool IsLibraryBrowsableAsset(AssetRow row)
    {
        if (!row.IsEquippable) return true;
        // Standalone weapons and shields render reliably and belong in the public model browser.
        // Clothing, armor, jewelry, and other worn/composed equipment remain internal-only.
        return row.WeenieType is 0x00010081 or 0x00020081;
    }

    static string ClassifyAsset(string typeName, bool equippable, bool hasModel, bool structuralCreature)
    {
        if (equippable) return "Equippable Item";
        if (structuralCreature ||
            typeName.Contains("Creature", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("NPC", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Monster", StringComparison.OrdinalIgnoreCase)) return "Creature / NPC";
        if (typeName.Contains("Building", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Scenery", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Door", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Portal", StringComparison.OrdinalIgnoreCase)) return "World Object";
        if (hasModel) return "Renderable Object";
        return string.IsNullOrWhiteSpace(typeName) || typeName == "Unknown" ? "Data Object" : typeName;
    }

    static bool HasPropertyName(JsonNode? node, string propertyName)
    {
        if (node is JsonObject o)
        {
            if (string.Equals(o["propertyName"]?.ToString(), propertyName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var kv in o) if (HasPropertyName(kv.Value, propertyName)) return true;
        }
        else if (node is JsonArray a)
        {
            foreach (var v in a) if (HasPropertyName(v, propertyName)) return true;
        }
        return false;
    }

    static bool HasPropertyEnumValue(JsonNode? node, string propertyName, string enumValue)
    {
        if (node is JsonObject o)
        {
            if (string.Equals(o["propertyName"]?.ToString(), propertyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o["enumValue"]?.ToString(), enumValue, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var kv in o) if (HasPropertyEnumValue(kv.Value, propertyName, enumValue)) return true;
        }
        else if (node is JsonArray a)
        {
            foreach (var v in a) if (HasPropertyEnumValue(v, propertyName, enumValue)) return true;
        }
        return false;
    }

    static uint FindPropertyDid(JsonNode node, string prop, uint lo, uint hi)
    {
        if (node is JsonObject o)
        {
            if (string.Equals(o["propertyName"]?.ToString(), prop, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in o)
                {
                    uint v = ParseDid(kv.Value?.ToString());
                    if (v >= lo && v <= hi) return v;
                }
            }
            foreach (var kv in o)
            {
                if (kv.Value != null)
                {
                    var v = FindPropertyDid(kv.Value, prop, lo, hi);
                    if (v != 0) return v;
                }
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var n in a)
            {
                if (n != null)
                {
                    var v = FindPropertyDid(n, prop, lo, hi);
                    if (v != 0) return v;
                }
            }
        }
        return 0;
    }

    static uint ParseDid(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        try { return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(s[2..], 16) : Convert.ToUInt32(s); }
        catch { return 0; }
    }


    async Task MineAnimationsAsync()
    {
        if (!await EnsureBackendAsync()) return;
        SetBusy(true, "Mining client_anim.dat…");
        try
        {
            using var r = await http.GetAsync("AnimationCatalog/ids?refresh=true");
            r.EnsureSuccessStatusCode();
            var json = await r.Content.ReadAsStringAsync();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Studio", "AnimationDiagnostics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "animation-catalog.json");
            await File.WriteAllTextAsync(path, json);
            var root = JsonNode.Parse(json);
            var count = root?["count"]?.GetValue<int>() ?? 0;

            status.Text = $"Scanning {count:N0} animation records for Havok Tagfiles…";
            using var hr = await http.GetAsync("AnimationCatalog/havok?limit=50000");
            hr.EnsureSuccessStatusCode();
            var havokJson = await hr.Content.ReadAsStringAsync();
            var havokPath = Path.Combine(dir, "animation-havok-index.json");
            await File.WriteAllTextAsync(havokPath, havokJson);
            var hroot = JsonNode.Parse(havokJson);
            var havokCount = hroot?["havokCandidates"]?.GetValue<int>() ?? 0;
            status.Text = $"Animation DAT cataloged — {count:N0} records, {havokCount:N0} Havok candidates";
            MessageBox.Show($"Animation mining pass found {count:N0} records in client_anim.dat and {havokCount:N0} Havok-looking candidates in the full animation pass.\r\n\r\nSaved:\r\n{path}\r\n{havokPath}\r\n\r\nUpload animation-havok-index.json next; that will let us choose real DDO Tagfiles for decoder testing.", "DDO Studio");
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            status.Text = "Animation mining failed";
            MessageBox.Show(ex.Message, "Animation Miner");
        }
        finally { SetBusy(false, status.Text); }
    }

    async Task SaveCharacterDiagnosticAsync()
    {
        var row = selected;
        if (row == null) return;
        status.Text = "Building character diagnostic…";
        try
        {
            var url = $"CharacterDiagnostic/0x{row.VisualDesc:X8}?dbId=0x{row.DbId:X8}&physObj=0x{row.PhysObj:X8}&setup=0x{row.Setup:X8}";
            using var r = await http.GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
                throw new Exception($"Diagnostic endpoint returned {(int)r.StatusCode}: {body}");

            Directory.CreateDirectory(appearanceDiagnosticsDir);
            var safe = string.Concat(row.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (string.IsNullOrWhiteSpace(safe)) safe = "character";
            if (safe.Length > 64) safe = safe[..64];
            var path = Path.Combine(appearanceDiagnosticsDir, $"{safe}_db_{row.DbId:X8}_visual_{row.VisualDesc:X8}.json");
            await System.IO.File.WriteAllTextAsync(path, body);
            LogAppearanceDiagnostic($"CHARACTER DIAGNOSTIC SAVED {path}");
            details.Text += $"\r\n\r\nCHARACTER DIAGNOSTIC SAVED\r\n   {path}";
            status.Text = "Character diagnostic saved";
        }
        catch (Exception ex)
        {
            LogAppearanceDiagnostic($"CHARACTER DIAGNOSTIC FAILED for {row.Name} db=0x{row.DbId:X8}: {ex}");
            details.Text += $"\r\n\r\nCHARACTER DIAGNOSTIC FAILED\r\n   {ex.Message}";
            status.Text = "Diagnostic failed";
        }
        finally
        {
        }
    }

    async Task InspectEquipmentChainAsync()
    {
        var row = selected;
        if (row == null || !row.IsEquippable) return;
        status.Text = "Inspecting wearable state…";
        try
        {
            using var r = await http.GetAsync($"WearableDiagnostic/0x{row.DbId:X8}");
            var body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
                throw new Exception($"Wearable inspector returned {(int)r.StatusCode}: {body}");

            Directory.CreateDirectory(equipmentDiagnosticsDir);
            var safe = string.Concat(row.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (string.IsNullOrWhiteSpace(safe)) safe = "wearable";
            if (safe.Length > 64) safe = safe[..64];
            var path = Path.Combine(equipmentDiagnosticsDir, $"{safe}_db_{row.DbId:X8}_wearable.json");
            await System.IO.File.WriteAllTextAsync(path, body);

            int direct = 0, wstates = 0;
            bool placeholder = false;
            try
            {
                var json = JsonNode.Parse(body);
                direct = json?["directReferences"]?.AsArray().Count ?? 0;
                wstates = json?["wstates"]?.AsArray().Count ?? 0;
                placeholder = json?["placeholderSeenDirectly"]?.GetValue<bool>() ?? false;
            }
            catch { }

            details.Text += $"\r\n\r\nWEARABLE INSPECTOR\r\n   Saved: {path}\r\n   Direct typed DAT references: {direct}\r\n   Direct WState records: {wstates}\r\n   Purple-bag placeholder 0x1F000013 seen directly: {(placeholder ? "yes" : "no")}\r\n\r\n   This focused report stays anchored to the item and its directly referenced gameplay state instead of wandering into shared world/scene data.";
            status.Text = $"Wearable diagnostic saved — {wstates} WState record(s)";
        }
        catch (Exception ex)
        {
            details.Text += $"\r\n\r\nWEARABLE INSPECTION FAILED\r\n   {ex.Message}";
            LogAppearanceDiagnostic($"WEARABLE INSPECTION FAILED for {row.Name} db=0x{row.DbId:X8}: {ex}");
            status.Text = "Wearable inspection failed";
        }
        finally
        {
        }
    }

    void OpenAppearanceDiagnosticsFolder()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(appearanceDiagnosticsDir);
            Directory.CreateDirectory(equipmentDiagnosticsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = root,
                UseShellExecute = true
            });
            status.Text = $"Diagnostics: {root}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the diagnostics folder.\r\n\r\n{root}\r\n\r\n{ex.Message}", "DDO Studio");
        }
    }

    void LogAppearanceDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(appearanceDiagnosticsDir);
            System.IO.File.AppendAllText(appearanceDiagnosticsLog, $"[{DateTime.Now:O}] {message}\r\n");
        }
        catch { }
    }

    async Task SelectRowAsync()
    {
        selected = results.SelectedItems.Count == 1 ? results.SelectedItems[0].Tag as AssetRow : null;
        selectedAnimationId = null;
        compatibleAnimationIds.Clear();
        int generation = ++previewGeneration;

        if (selected == null)
        {
            exportButton.Enabled = false;
            details.Text = "";
            await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.clear();");
            previewStatus.Text = "3D preview: waiting for selection";
            return;
        }

        var row = selected;
        if (row.IsIndexOnly)
        {
            status.Text = $"Resolving {row.Name}…";
            var resolved = await ResolveAsync(row.DbId, row.Name);
            if (generation != previewGeneration) return;
            if (resolved == null)
            {
                details.Text = $"{row.Name}\r\n\r\nThis indexed equipment record could not be resolved from the current DDO data files.";
                previewStatus.Text = "3D preview: item could not be resolved";
                return;
            }
            row = resolved;
            selected = resolved;
            if (results.SelectedItems.Count == 1)
            {
                var item = results.SelectedItems[0];
                item.Tag = resolved;
                if (item.SubItems.Count > 5) item.SubItems[5].Text = resolved.Setup != 0 ? $"0x{resolved.Setup:X8}" : "—";
            }
        }
        if (!row.HasStandaloneModel)
        {
            // Defensive guard for stale rows from an older cache/session.
            details.Text = $"{row.Name}\r\n\r\nThis record does not resolve to a renderable Setup and is hidden from the 1.7.2 Library.";
            exportButton.Enabled = false;
            await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.clear();");
            previewStatus.Text = "3D preview: unrenderable record filtered";
            return;
        }
        exportButton.Enabled = true;

        var sb = new System.Text.StringBuilder();
        sb.Append($"{row.Name}\r\n\r\nASSET CLASSIFICATION\r\n   Class:               {row.AssetClass}\r\n   WeenieType:          {row.WeenieTypeName} (0x{row.WeenieType:X8})\r\n   DbProperties:        0x{row.DbId:X8}");

        if (row.IsEquippable && !row.HasStandaloneModel)
        {
            sb.Append("\r\n   Standalone model:    no shallow renderable Setup resolved");
            sb.Append("\r\n   Placeholder visual:  0x1F000013 = generic purple bag (ignored)");
            AppendEquipmentClassification(sb, row);
            AppendItemData(sb, row.ItemData);
            sb.Append("\r\n\r\nWEARABLE APPEARANCE\r\n   No standalone Setup was found in the item's shallow equipment chain. This item may only exist as an equipped composition.\r\n   This item appears to exist only as an equipped composition.");
            details.Text = sb.ToString();
            await RunViewerScriptAsync("window.ddoViewer && window.ddoViewer.clear();");
            previewStatus.Text = "3D preview: equipped-only item — no standalone Setup resolved";
            return;
        }
        if (row.IsEquippable && row.HasStandaloneModel)
        {
            sb.Append("\r\n   Standalone model:    resolved from equipment chain");
            sb.Append("\r\n   Placeholder visual:  0x1F000013 = generic purple bag (ignored)");
        }

        var appearanceText = row.Appearance != 0 ? $"0x{row.Appearance:X8}" : "not found";
        sb.Append($"\r\n\r\nMODEL CHAIN\r\n   PhysObj:             0x{row.PhysObj:X8}\r\n   VisualDescription:   0x{row.VisualDesc:X8}\r\n   Setup:               0x{row.Setup:X8}\r\n   Appearance:          {appearanceText}");
        AppendEquipmentClassification(sb, row);
        AppendItemData(sb, row.ItemData);
        sb.Append("\r\n\r\nCHARACTER / OBJECT COMPOSITION\r\n   Renderable assets continue through the normal VisualDescription → Setup → RenderMesh pipeline. Character diagnostics remain available for complex composed creatures.\r\n\r\nThe 3D preview is generated automatically. Export saves a permanent GLB wherever you choose.");
        details.Text = sb.ToString();
        await AppendMaterialSummaryAsync(row, generation);
        await AppendAppearanceDiagnosticAsync(row, generation);
        await PreviewAsync(row, generation);
    }

    static string[] JsonStringArray(JsonNode? node)
    {
        if (node is not JsonArray a) return Array.Empty<string>();
        return a.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
    }

    static void AppendEquipmentClassification(System.Text.StringBuilder sb, AssetRow row)
    {
        if (!row.IsEquippable) return;
        sb.Append("\r\n\r\nEQUIPMENT CLASSIFICATION");
        sb.Append($"\r\n   Kind:                {(string.IsNullOrWhiteSpace(row.EquipmentKind) ? "Equippable Item" : row.EquipmentKind)}");
        sb.Append($"\r\n   Equip slot(s):       {(row.EquipSlots.Length == 0 ? "not declared" : string.Join(", ", row.EquipSlots))}");
        if (!string.IsNullOrWhiteSpace(row.WeaponTypeName)) sb.Append($"\r\n   Weapon type:         {row.WeaponTypeName}");
        if (!string.IsNullOrWhiteSpace(row.ArmorTypeName)) sb.Append($"\r\n   Armor type:          {row.ArmorTypeName}");
        if (row.CanMainHand || row.CanOffHand)
        {
            var hands = new List<string>();
            if (row.CanMainHand) hands.Add("Main Hand");
            if (row.CanOffHand) hands.Add("Off Hand");
            sb.Append($"\r\n   Hand compatibility:  {string.Join(", ", hands)}");
        }
        if (row.IsTwoHanded) sb.Append("\r\n   Handedness:          Two-Handed (precludes Off Hand)");
        if (row.PrecludedSlotsRaw.Length > 0) sb.Append($"\r\n   Precludes:           {string.Join(", ", row.PrecludedSlotsRaw)}");
    }

    static void AppendItemData(System.Text.StringBuilder sb, JsonObject? itemData)
    {
        if (itemData == null) return;
        sb.Append("\r\n\r\nITEM DETAILS");
        var desc = itemData["description"]?.ToString();
        if (!string.IsNullOrWhiteSpace(desc)) sb.Append($"\r\n   Description: {StripHtml(desc)}");

        if (itemData["tableRows"] is JsonArray rows)
        {
            foreach (var n in rows)
            {
                if (n is not JsonObject r) continue;
                var field = r["field"]?.ToString();
                var value = r["value"]?.ToString();
                if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value) || string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append($"\r\n   {field,-22} {StripHtml(value)}");
            }
        }

        if (itemData["effects"] is JsonArray effects && effects.Count > 0)
        {
            sb.Append("\r\n\r\nEFFECTS / STATS");
            foreach (var n in effects)
            {
                if (n is not JsonObject e) continue;
                var name = e["name"]?.ToString();
                var description = e["description"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                sb.Append($"\r\n   • {StripHtml(name)}");
                if (!string.IsNullOrWhiteSpace(description)) sb.Append($": {StripHtml(description)}");
            }
        }
    }

    static string StripHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    async Task AppendAppearanceDiagnosticAsync(AssetRow row, int generation)
    {
        if (row.Appearance == 0)
        {
            details.Text += "\r\n\r\nAPPEARANCE DIAGNOSTIC\r\n   No 0x20 Appearance record was found in this VisualDescription.";
            LogAppearanceDiagnostic($"{row.Name} db=0x{row.DbId:X8}: no Appearance ID found");
            return;
        }
        try
        {
            using var r = await http.GetAsync($"AppearanceDiagnostic/0x{row.Appearance:X8}");
            if (!r.IsSuccessStatusCode || generation != previewGeneration)
            {
                details.Text += $"\r\n\r\nAPPEARANCE DIAGNOSTIC\r\n   Appearance 0x{row.Appearance:X8} could not be decoded ({(int)r.StatusCode}).\r\n   Diagnostics folder: {appearanceDiagnosticsDir}";
                LogAppearanceDiagnostic($"{row.Name} db=0x{row.DbId:X8} appearance=0x{row.Appearance:X8}: endpoint returned {(int)r.StatusCode} {r.StatusCode}");
                return;
            }
            var json = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int bytes = root.GetProperty("byteLength").GetInt32();
            string sha1 = root.GetProperty("sha1").GetString() ?? "";
            var refs = root.GetProperty("references").EnumerateArray().ToArray();
            var groups = refs.GroupBy(x => x.GetProperty("type").GetString() ?? "Unknown")
                             .OrderBy(g => g.Key)
                             .ToArray();

            var sb = new System.Text.StringBuilder();
            sb.Append($"\r\n\r\nAPPEARANCE DIAGNOSTIC\r\n   0x{row.Appearance:X8}   {bytes:N0} bytes   SHA1 {sha1[..Math.Min(12, sha1.Length)]}");
            if (groups.Length == 0) sb.Append("\r\n   No aligned asset references found yet.");
            foreach (var g in groups)
            {
                var ids = g.Select(x => x.GetProperty("hex").GetString() ?? "")
                           .Where(x => x.Length > 0).Take(10).ToArray();
                sb.Append($"\r\n   {g.Key,-18} {g.Count(),3}: {string.Join(", ", ids)}{(g.Count() > 10 ? " …" : "")}");
            }

            if (root.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("attempts", out var attempts))
            {
                var successful = attempts.EnumerateArray().FirstOrDefault(a => a.TryGetProperty("success", out var ok) && ok.GetBoolean());
                if (successful.ValueKind != JsonValueKind.Undefined)
                    sb.Append($"\r\n   SDK parser: {successful.GetProperty("type").GetString()} (consumed {successful.GetProperty("consumed").GetInt64():N0} bytes)");
                else
                    sb.Append("\r\n   SDK parser: no Appearance parser succeeded; raw reference scan is active.");
            }
            sb.Append("\r\n   Diagnostic JSON is saved automatically so different character archetypes can be compared.");
            if (generation == previewGeneration) details.Text += sb.ToString();

            try
            {
                Directory.CreateDirectory(appearanceDiagnosticsDir);
                var safe = string.Concat(row.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                if (string.IsNullOrWhiteSpace(safe)) safe = "character";
                if (safe.Length > 64) safe = safe[..64];
                var path = Path.Combine(appearanceDiagnosticsDir, $"{safe}_db_{row.DbId:X8}_appearance_{row.Appearance:X8}.json");
                await System.IO.File.WriteAllTextAsync(path, json);
                LogAppearanceDiagnostic($"SAVED {path}");
                if (generation == previewGeneration)
                {
                    details.Text += $"\r\n   Saved: {path}";
                    status.Text = "Appearance diagnostic saved";
                }
            }
            catch (Exception saveEx)
            {
                LogAppearanceDiagnostic($"SAVE FAILED for {row.Name} db=0x{row.DbId:X8}: {saveEx}");
                if (generation == previewGeneration)
                    details.Text += $"\r\n   SAVE FAILED: {saveEx.Message}\r\n   Target: {appearanceDiagnosticsDir}";
            }
        }
        catch (Exception ex)
        {
            if (generation == previewGeneration)
                details.Text += $"\r\n\r\nAPPEARANCE DIAGNOSTIC\r\n   Error: {ex.Message}\r\n   Diagnostics folder: {appearanceDiagnosticsDir}";
            LogAppearanceDiagnostic($"ERROR for {row.Name} db=0x{row.DbId:X8} appearance=0x{row.Appearance:X8}: {ex}");
        }
    }

    async Task AppendMaterialSummaryAsync(AssetRow row, int generation)
    {
        try
        {
            using var statusResponse = await http.GetAsync("AssetRelations/status");
            if (!statusResponse.IsSuccessStatusCode || generation != previewGeneration) return;
            using var statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            if (!statusDoc.RootElement.GetProperty("ready").GetBoolean())
            {
                details.Text += "\r\n\r\nMaterials: relationship database not built yet. Open Texture Browser and click Build Relationships.";
                return;
            }
            using var r = await http.GetAsync($"AssetRelations/setup/0x{row.Setup:X8}");
            if (!r.IsSuccessStatusCode || generation != previewGeneration) return;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var mats = doc.RootElement.GetProperty("materials").EnumerateArray().ToArray();
            var sb = new System.Text.StringBuilder();
            sb.Append($"\r\n\r\nMATERIALS ({mats.Length})");
            foreach (var m in mats.Take(8))
            {
                uint mid = m.GetProperty("materialId").GetUInt32();
                sb.Append($"\r\n0x{mid:X8}");
                var props = m.GetProperty("properties").EnumerateArray().ToArray();
                foreach (var p in props.Take(12))
                {
                    string name = p.GetProperty("propertyName").GetString() ?? "Texture";
                    uint tid = p.GetProperty("textureId").GetUInt32();
                    string role = name.Equals("DiffuseMap", StringComparison.OrdinalIgnoreCase) ? " [GLB base color]"
                        : name.Equals("NormalMap", StringComparison.OrdinalIgnoreCase) ? " [GLB normal]"
                        : " [indexed]";
                    string surfaceText = "";
                    if (p.TryGetProperty("surfaces", out var surfacesElement))
                    {
                        var surfaces = surfacesElement.EnumerateArray().Select(x => x.GetUInt32()).ToArray();
                        if (surfaces.Length > 0)
                            surfaceText = " -> " + string.Join(", ", surfaces.Take(3).Select(x => $"0x{x:X8}")) + (surfaces.Length > 3 ? " …" : "");
                        else
                            surfaceText = " -> no RenderSurface indexed";
                    }
                    sb.Append($"\r\n   {name,-14} 0x{tid:X8}{surfaceText}{role}");
                }
            }
            var allProps = mats.SelectMany(m => m.GetProperty("properties").EnumerateArray()).ToArray();
            int diffuseCount = allProps.Count(p => string.Equals(p.GetProperty("propertyName").GetString(), "DiffuseMap", StringComparison.OrdinalIgnoreCase));
            int normalCount = allProps.Count(p => string.Equals(p.GetProperty("propertyName").GetString(), "NormalMap", StringComparison.OrdinalIgnoreCase));
            int surfaceCount = allProps.Count(p => p.TryGetProperty("surfaces", out var se) && se.GetArrayLength() > 0);
            sb.Append($"\r\n\r\nMATERIAL DIAGNOSTICS\r\n   Diffuse maps: {diffuseCount}   Normal maps: {normalCount}   Surface chains: {surfaceCount}/{allProps.Length}");
            sb.Append("\r\n   Exporter maps DiffuseMap -> glTF baseColorTexture and NormalMap -> glTF normalTexture.");
            if (mats.Length > 8) sb.Append($"\r\n…plus {mats.Length - 8} more material(s)");
            if (generation == previewGeneration) details.Text += sb.ToString();
        }
        catch { }
    }

    static bool IsStandaloneWearable(AssetRow row)
        => row.IsEquippable
           && row.Setup != 0
           && row.Appearance is >= 0x20000000 and <= 0x20FFFFFF
           && row.WeenieType is 0x00030081 or 0x00040081; // Clothing / Armor

    async Task<bool> HasDirectVisualSetupAsync(AssetRow row)
    {
        if (row.VisualDesc == 0 || row.Setup == 0) return false;
        try
        {
            var direct = await ResolveVisualDescriptionAsync(row.VisualDesc);
            return direct.Setup != 0 && direct.Setup == row.Setup;
        }
        catch
        {
            return false;
        }
    }

    static string WearableSlotHint(AssetRow row)
    {
        var declared = row.EquipSlots.FirstOrDefault(x => !string.Equals(x, "Main Hand", StringComparison.OrdinalIgnoreCase) && !string.Equals(x, "Off Hand", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(declared)) return declared;
        if (row.EquipmentKind.Contains("Head", StringComparison.OrdinalIgnoreCase)) return "Head";
        if (row.EquipmentKind.Contains("Cloak", StringComparison.OrdinalIgnoreCase)) return "Cloak";
        if (row.EquipmentKind.Contains("Armor", StringComparison.OrdinalIgnoreCase)) return "Armor";
        return ""; // Blank intentionally lets the APR resolver consider both worn-body and head keys.
    }

    async Task<string?> PrepareNpcAppearanceCompositionAsync(AssetRow row, int generation = -1, IEnumerable<DressingAppearanceSelection>? equipmentSelections = null)
    {
        bool npc = string.Equals(row.AssetClass, "Creature / NPC", StringComparison.OrdinalIgnoreCase);
        bool wearable = IsStandaloneWearable(row);
        bool explicitEquipmentComposition = equipmentSelections?.Any(x => x.Id != 0) == true;
        if (row.Setup == 0 || (!npc && !wearable && !explicitEquipmentComposition)) return null;

        try
        {
            string endpoint = $"NpcAppearance/0x{row.DbId:X8}?baseSetup=0x{row.Setup:X8}";
            if (row.VisualDesc != 0) endpoint += $"&visual=0x{row.VisualDesc:X8}";
            if (row.PhysObj != 0) endpoint += $"&physObj=0x{row.PhysObj:X8}";
            var equipmentList = equipmentSelections?.Where(x => x.Id != 0).ToList() ?? new List<DressingAppearanceSelection>();
            // Wearable appearance tables describe mesh/material replacements against a resolved
            // base Setup. Feed internal composition requests through the APR resolver rather than
            // treating the shallow item record as a self-contained character model.
            if (wearable)
                equipmentList.Add(new DressingAppearanceSelection(row.DbId, WearableSlotHint(row), row.Appearance));
            var equipmentArray = equipmentList
                .GroupBy(x => (x.Id, Slot: x.Slot ?? "", x.Appearance))
                .Select(g => g.First())
                .OrderBy(x => x.Id)
                .ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (equipmentArray.Length > 0)
            {
                var tokens = equipmentArray.Select(x => $"0x{x.Id:X8}|{x.Slot.Replace("|", "")}|0x{x.Appearance:X8}");
                endpoint += "&equipment=" + Uri.EscapeDataString(string.Join(",", tokens));
            }

            LogAppearanceDiagnostic($"APR composition START for {row.Name} db=0x{row.DbId:X8} setup=0x{row.Setup:X8} visual=0x{row.VisualDesc:X8}");
            using var r = await http.GetAsync(endpoint);
            if (generation >= 0 && generation != previewGeneration) return null;

            string safe = string.Concat(row.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            Directory.CreateDirectory(appearanceDiagnosticsDir);
            string equipmentTag = equipmentArray.Length == 0 ? "" : "_dress_" + string.Join("_", equipmentArray.Select(x => x.Id.ToString("X8")));
            if (equipmentTag.Length > 96) equipmentTag = equipmentTag[..96];
            string diagPath = Path.Combine(appearanceDiagnosticsDir, $"{safe}_db_{row.DbId:X8}{equipmentTag}_composition.json");
            string body = await r.Content.ReadAsStringAsync();

            if (!r.IsSuccessStatusCode)
            {
                string failureJson = JsonSerializer.Serialize(new
                {
                    format = "DDO Studio NPC Appearance Composition Error",
                    version = "1.7.2",
                    generatedUtc = DateTime.UtcNow,
                    dbId = row.DbId,
                    dbHex = $"0x{row.DbId:X8}",
                    setup = $"0x{row.Setup:X8}",
                    visual = row.VisualDesc == 0 ? null : $"0x{row.VisualDesc:X8}",
                    endpoint,
                    httpStatus = (int)r.StatusCode,
                    httpReason = r.ReasonPhrase,
                    response = body
                }, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(diagPath, failureJson);
                LogAppearanceDiagnostic($"APR composition HTTP {(int)r.StatusCode} for {row.Name}; diagnostic={diagPath}");
                return null;
            }

            await File.WriteAllTextAsync(diagPath, body);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("resolvedAppearance", out var resolved) || resolved.ValueKind != JsonValueKind.Object)
            {
                LogAppearanceDiagnostic($"APR composition has no resolvedAppearance for {row.Name}; diagnostic={diagPath}");
                return null;
            }

            int selectors = resolved.TryGetProperty("selectorCount", out var sc) && sc.TryGetInt32(out var sv) ? sv : 0;
            int meshOps = resolved.TryGetProperty("meshReplacements", out var mr) && mr.ValueKind == JsonValueKind.Array ? mr.GetArrayLength() : 0;
            int materialOps = resolved.TryGetProperty("materialMods", out var mm) && mm.ValueKind == JsonValueKind.Array ? mm.GetArrayLength() : 0;
            int setupOps = resolved.TryGetProperty("setupReplacements", out var sr) && sr.ValueKind == JsonValueKind.Array ? sr.GetArrayLength() : 0;

            LogAppearanceDiagnostic($"APR composition SAVED {diagPath}; selectors={selectors} meshes={meshOps} materials={materialOps} setups={setupOps}");
            if (selectors <= 0 || meshOps + materialOps + setupOps <= 0) return null;

            if (generation < 0 || generation == previewGeneration)
                details.Text += $"\r\n\r\nAPPEARANCE COMPOSITION\r\n   APR composition: {selectors} selector(s), {meshOps} mesh replacement(s), {materialOps} material modifier(s), {setupOps} Setup replacement(s).";
            return diagPath;
        }
        catch (Exception ex)
        {
            LogAppearanceDiagnostic($"APR composition failed for {row.Name} db=0x{row.DbId:X8}: {ex}");
            return null;
        }
    }

    static string[] WearableReplacementMeshes(string? compositionPath)
    {
        if (string.IsNullOrWhiteSpace(compositionPath) || !File.Exists(compositionPath)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(compositionPath));
            if (!doc.RootElement.TryGetProperty("resolvedAppearance", out var resolved) || resolved.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();
            var equipmentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resolved.TryGetProperty("selectedParts", out var selectedParts) && selectedParts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in selectedParts.EnumerateArray())
                {
                    string source = part.TryGetProperty("source", out var src) ? (src.GetString() ?? "") : "";
                    if (!source.StartsWith("$equipment", StringComparison.OrdinalIgnoreCase)) continue;
                    string apr = part.TryGetProperty("aprFile", out var a) ? (a.GetString() ?? "") : "";
                    string key = part.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                    if (apr.Length > 0 && key.Length > 0) equipmentKeys.Add(apr + "|" + key);
                }
            }
            if (equipmentKeys.Count == 0 || !resolved.TryGetProperty("meshReplacements", out var replacements) || replacements.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var meshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var op in replacements.EnumerateArray())
            {
                string apr = op.TryGetProperty("aprFile", out var a) ? (a.GetString() ?? "") : "";
                string key = op.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                if (!equipmentKeys.Contains(apr + "|" + key)) continue;
                string mesh = op.TryGetProperty("meshDid", out var m) ? (m.GetString() ?? "") : "";
                if (mesh.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) mesh = mesh[2..];
                if (mesh.Length > 0) meshes.Add(mesh.ToUpperInvariant());
            }
            return meshes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    async Task PreviewAsync(AssetRow row, int generation, IEnumerable<DressingAppearanceSelection>? dressingEquipmentSelections = null, string? previewLabel = null, AssetRow? visualEffectSource = null)
    {
        if (!await EnsureBackendAsync()) return;
        await EnsureViewerAsync();
        if (!viewerReady) return;

        var dressingSelections = dressingEquipmentSelections?.Where(x => x.Id != 0).ToArray() ?? Array.Empty<DressingAppearanceSelection>();

        // 1.7.2: direct Library previews stay standalone. Do not inject a Human/player
        // body merely because the selected asset is Clothing/Armor. The selected item's own
        // resolved Setup/APR composition remains the preview source.

        string effectiveLabel = string.IsNullOrWhiteSpace(previewLabel) ? row.Name : previewLabel;
        string? appearanceComposition = await PrepareNpcAppearanceCompositionAsync(row, generation, dressingSelections);
        if (generation != previewGeneration) return;
        string dressKey = dressingSelections.Length == 0 ? "" : "_dress_" + StableDressingFingerprint(dressingSelections);
        bool directVisualSetup = IsStandaloneWearable(row) && await HasDirectVisualSetupAsync(row);
        bool appearanceOnly = appearanceComposition != null && IsStandaloneWearable(row) && !directVisualSetup;
        var previewFile = Path.Combine(previewCache, appearanceComposition != null
            ? $"composed_{row.DbId:X8}_setup_{row.Setup:X8}{dressKey}_v172.glb"
            : $"setup_{row.Setup:X8}{dressKey}_v172.glb");
        try
        {
            previewStatus.Text = File.Exists(previewFile) ? $"3D preview: loading cached {effectiveLabel}…" : $"3D preview: building {effectiveLabel}…";

            if (!File.Exists(previewFile) || new FileInfo(previewFile).Length < 64)
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "exporter", "DDOGlbExporter.exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("The bundled exporter is missing.", exe);

                string appearanceArg = appearanceComposition != null ? $" --appearance \"{appearanceComposition}\"" : "";
                string appearanceOnlyArg = appearanceOnly ? " --appearance-only" : "";
                var psi = new ProcessStartInfo(exe, $"0x{row.Setup:X8} \"{previewFile}\"{appearanceArg}{appearanceOnlyArg}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                ConfigureExporterEnvironment(psi);
                previewExporter = Process.Start(psi)!;
                string stdout = await previewExporter.StandardOutput.ReadToEndAsync();
                string stderr = await previewExporter.StandardError.ReadToEndAsync();
                await previewExporter.WaitForExitAsync();
                try
                {
                    var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Asset Studio");
                    Directory.CreateDirectory(logDir);
                    var logPath = Path.Combine(logDir, "exporter.log");
                    await File.WriteAllTextAsync(logPath, $"DDO Studio 1.7.2 exporter diagnostics\r\nAsset: {row.Name}\r\nSetup: 0x{row.Setup:X8}\r\nAppearance: {appearanceComposition ?? "<none>"}\r\nAppearance-only wearable: {appearanceOnly}\r\nTime: {DateTime.Now:O}\r\n\r\nSTDOUT\r\n{stdout}\r\n\r\nSTDERR\r\n{stderr}");
                }
                catch { }
                if (generation != previewGeneration) return;
                if (previewExporter.ExitCode != 0) throw new Exception((stderr + Environment.NewLine + stdout).Trim());
            }

            if (generation != previewGeneration) return;
            var url = $"https://ddocache.ddo/{Uri.EscapeDataString(Path.GetFileName(previewFile))}?v={File.GetLastWriteTimeUtc(previewFile).Ticks}";
            var urlJs = JsonSerializer.Serialize(url);
            var nameJs = JsonSerializer.Serialize(effectiveLabel);
            // WebView GLB loading is asynchronous. Clear the previous effect request before
            // replacing the model. Model transforms remain independent from the pedestal.
            await RunViewerScriptAsync("window.ddoViewer.setVisualEffects && window.ddoViewer.setVisualEffects(null);");
            var modelDefaults = row.WeenieType == 0x00020081
                ? new { positionX = 0.0, positionY = 1.40, positionZ = 0.0, rotationX = 90.0, rotationY = 0.0, rotationZ = 0.0 }
                : new { positionX = 0.0, positionY = 0.0, positionZ = 0.0, rotationX = 0.0, rotationY = 0.0, rotationZ = 0.0 };
            string defaultsJs = JsonSerializer.Serialize(modelDefaults);
            await RunViewerScriptAsync($"window.ddoViewer.loadModel({urlJs}, {nameJs}, {defaultsJs});");
            if (generation != previewGeneration) return;
            var wearableMeshIds = dressingSelections.Length > 0 ? WearableReplacementMeshes(appearanceComposition) : Array.Empty<string>();
            await RunViewerScriptAsync($"window.ddoViewer && window.ddoViewer.setDressingWearableMeshes && window.ddoViewer.setDressingWearableMeshes({JsonSerializer.Serialize(wearableMeshIds)});");

            // 1.7.2: APR mesh/material/setup changes are baked into the primary GLB.
            // Clear the older overlay channel so the composed model is the single source of truth.
            await RunViewerScriptAsync("window.ddoViewer.setCharacterAppearanceParts && window.ddoViewer.setCharacterAppearanceParts([]);");
            if (generation != previewGeneration) return;

            // Resolve PSDescription / particle branches separately from the base mesh.
            // Download any particle textures to the same virtual-host cache as the GLB so the
            // viewer can render them without depending on browser CORS behavior.
            await ResolveVisualEffectsAsync(visualEffectSource ?? row, generation);
            if (generation != previewGeneration) return;

            // 1.7.2: populate the viewer from the selected Setup's actual skeleton size.
            // The API inventories DbAnimator.BoneCount and returns only exact-count candidates.
            try
            {
                using var ar = await http.GetAsync($"AnimationSkeletonMatcher/compatible/0x{row.Setup:X8}?limit=5000");
                if (ar.IsSuccessStatusCode && generation == previewGeneration)
                {
                    var aj = await ar.Content.ReadAsStringAsync();
                    using var ad = JsonDocument.Parse(aj);
                    var root = ad.RootElement;
                    int jointCount = root.GetProperty("jointCount").GetInt32();
                    int totalCompatible = root.GetProperty("totalCompatible").GetInt32();
                    int totalBoneCountMatches = root.TryGetProperty("totalBoneCountMatches", out var tb) ? tb.GetInt32() : totalCompatible;
                    int hiddenWrongFamily = root.TryGetProperty("hiddenWrongFamily", out var hw) ? hw.GetInt32() : 0;
                    int decodeFailed = root.TryGetProperty("decodeFailed", out var df) ? df.GetInt32() : 0;
                    int decodedForFamilyCheck = root.TryGetProperty("decodedForFamilyCheck", out var dc) ? dc.GetInt32() : 0;
                    bool truncated = root.TryGetProperty("truncated", out var te) && te.GetBoolean();
                    string binding = root.TryGetProperty("playbackBinding", out var be) ? (be.GetString() ?? "family-filtered-direct-index") : "family-filtered-direct-index";
                    var candidates = root.GetProperty("animations").EnumerateArray().Select(x => new
                    {
                        id = x.GetProperty("id").GetString(),
                        boneCount = x.GetProperty("boneCount").GetInt32(),
                        duration = x.GetProperty("duration").GetSingle(),
                        byteLength = x.GetProperty("byteLength").GetInt32(),
                        label = AnimationDisplayName(x.GetProperty("id").GetString() ?? "", x.TryGetProperty("label", out var le) && le.ValueKind != JsonValueKind.Null ? le.GetString() : null),
                        linked = true,
                        compatible = true,
                        validatedFamily = x.TryGetProperty("validatedFamily", out var ve) && ve.GetBoolean(),
                        familyTier = x.TryGetProperty("familyTier", out var ft) ? (ft.GetString() ?? "count-only") : "count-only",
                        familyScore = x.TryGetProperty("familyScore", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetSingle() : 0f,
                        familyMatched = x.TryGetProperty("familyMatched", out var fm) && fm.ValueKind == JsonValueKind.Number ? fm.GetInt32() : 0,
                        familyEligible = x.TryGetProperty("familyEligible", out var fe) && fe.ValueKind == JsonValueKind.Number ? fe.GetInt32() : 0
                    }).ToArray();
                    compatibleAnimationIds.Clear();
                    compatibleAnimationIds.AddRange(candidates.Select(x => x.id).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));

                    // Weapon previews use the curated spinning presentation clip when that exact
                    // animation is compatible. Other models prefer the universal idle when available.
                    // If a weapon does not support the spinning clip, it falls back to the universal idle.
                    string? preferredAnimation = null;
                    bool forcePreferredLoop = false;
                    if (row.WeenieType == 0x00020081)
                    {
                        preferredAnimation = compatibleAnimationIds
                            .FirstOrDefault(x => string.Equals(x, WeaponPreferredAnimationId, StringComparison.OrdinalIgnoreCase));
                        forcePreferredLoop = !string.IsNullOrWhiteSpace(preferredAnimation);
                    }
                    preferredAnimation ??= compatibleAnimationIds
                        .FirstOrDefault(x => string.Equals(x, UniversalPreferredAnimationId, StringComparison.OrdinalIgnoreCase));

                    string candidatesJs = JsonSerializer.Serialize(candidates);
                    string preferredJs = JsonSerializer.Serialize(preferredAnimation);
                    await RunViewerScriptAsync($"window.ddoViewer.setCompatibleAnimations({candidatesJs}, {jointCount}, {JsonSerializer.Serialize(binding)}, {preferredJs}, {(forcePreferredLoop ? "true" : "false")});");
                    await PushAnimationAliasesAsync();
                    if (!string.IsNullOrWhiteSpace(preferredAnimation))
                        details.Text += $"\r\n   Default animation:   {preferredAnimation}";
                    details.Text += $"\r\n\r\nCOMPATIBLE ANIMATIONS\r\n   Skeleton: {jointCount} joints\r\n   Bone-count candidates: {totalBoneCountMatches:N0}\r\n   Pre-decoded for family check: {decodedForFamilyCheck:N0}\r\n   Hidden wrong-family: {hiddenWrongFamily:N0}\r\n   Decode failures hidden: {decodeFailed:N0}\r\n   Visible compatible animations: {totalCompatible:N0}{(truncated ? " (first 5,000 shown)" : "")}\r\n   Binding: {binding}\r\n   Wrong-family clips are filtered before they reach the animation panel.";
                }
                else if (generation == previewGeneration)
                {
                    await RunViewerScriptAsync("window.ddoViewer.setCompatibleAnimations([], 0, 'unavailable');");
                    details.Text += $"\r\n\r\nCOMPATIBLE ANIMATIONS\r\n   No parsed skeleton compatibility catalog was available for Setup 0x{row.Setup:X8}.";
                }
            }
            catch (Exception animEx)
            {
                if (generation == previewGeneration)
                {
                    await RunViewerScriptAsync("window.ddoViewer.setCompatibleAnimations([], 0, 'error');");
                    details.Text += $"\r\n\r\nCOMPATIBLE ANIMATIONS\r\n   Error: {animEx.Message}";
                }
            }

            previewStatus.Text = $"3D preview: {row.Name} ✓";
        }
        catch (Exception ex)
        {
            if (generation != previewGeneration) return;
            previewStatus.Text = "3D preview failed";
            details.Text += $"\r\n\r\nPreview error:\r\n{ex.Message}";
        }
    }

    // 1.7.2 bakes APR appearance operations into the primary GLB before it reaches
    // the viewer. The older separate-NPC-layer overlay path was removed deliberately.

    async Task ResolveVisualEffectsAsync(AssetRow row, int generation, string? dressingSlot = null)
    {
        string? slotJs = string.IsNullOrWhiteSpace(dressingSlot) ? null : JsonSerializer.Serialize(dressingSlot);

        async Task ClearTargetAsync()
        {
            if (slotJs == null)
                await RunViewerScriptAsync("window.ddoViewer.setVisualEffects && window.ddoViewer.setVisualEffects(null);");
            else
                await RunViewerScriptAsync($"window.ddoViewer.setDressingRoomItemVisualEffects && window.ddoViewer.setDressingRoomItemVisualEffects({slotJs},null);");
        }

        async Task ApplyTargetAsync(string payloadJs)
        {
            if (slotJs == null)
                await RunViewerScriptAsync($"window.ddoViewer.setVisualEffects && window.ddoViewer.setVisualEffects({payloadJs});");
            else
                await RunViewerScriptAsync($"window.ddoViewer.setDressingRoomItemVisualEffects && window.ddoViewer.setDressingRoomItemVisualEffects({slotJs},{payloadJs});");
        }

        try
        {
            var parts = new List<string>();
            if (row.DbId != 0) parts.Add($"db=0x{row.DbId:X8}");
            if (row.VisualDesc != 0) parts.Add($"visual=0x{row.VisualDesc:X8}");
            if (row.Setup != 0) parts.Add($"setup=0x{row.Setup:X8}");
            if (row.Appearance != 0) parts.Add($"appearance=0x{row.Appearance:X8}");
            if (parts.Count == 0)
            {
                await ClearTargetAsync();
                return;
            }

            using var r = await http.GetAsync("VisualEffect/resolve?" + string.Join("&", parts));
            if (!r.IsSuccessStatusCode || generation != previewGeneration)
            {
                await ClearTargetAsync();
                return;
            }

            var json = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string effectMode = root.TryGetProperty("effectMode", out var eme) ? (eme.GetString() ?? "none") : "none";
            var ps = root.TryGetProperty("particleDescriptions", out var pe)
                ? pe.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var tex = root.TryGetProperty("particleTextures", out var te)
                ? te.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var auxSetups = root.TryGetProperty("auxiliaryEffectSetups", out var ase)
                ? ase.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var auxMeshes = root.TryGetProperty("auxiliaryEffectMeshes", out var ame)
                ? ame.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var auxMaterials = root.TryGetProperty("auxiliaryEffectMaterials", out var ama)
                ? ama.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var auxTextures = root.TryGetProperty("auxiliaryEffectTextures", out var ate)
                ? ate.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var auraTextures = root.TryGetProperty("auraCandidateTextures", out var acte)
                ? acte.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var runtimeTextures = root.TryGetProperty("runtimeCandidateTextures", out var rcte)
                ? rcte.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var runtimeScripts = root.TryGetProperty("runtimeScriptAnchors", out var rsa)
                ? rsa.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var runtimeStates = root.TryGetProperty("runtimeStateAnchors", out var rsta)
                ? rsta.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var runtimeEffects = root.TryGetProperty("runtimeEffectCandidates", out var reca)
                ? reca.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();
            var runtimeSetups = root.TryGetProperty("runtimeCandidateSetups", out var rcse)
                ? rcse.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string?>();

            var payload = new List<object>();
            foreach (var id in tex.Concat(auxTextures).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(16))
            {
                if (id == null) continue;
                string safe = id.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                string png = Path.Combine(previewCache, $"vfx_{safe}.png");
                try
                {
                    if (!File.Exists(png) || new FileInfo(png).Length < 32)
                    {
                        using var ir = await http.GetAsync($"Image/{Uri.EscapeDataString(id)}");
                        if (!ir.IsSuccessStatusCode) continue;
                        await using var fs = File.Create(png);
                        await ir.Content.CopyToAsync(fs);
                    }
                    if (new FileInfo(png).Length < 32) continue;
                    string url = $"https://ddocache.ddo/{Uri.EscapeDataString(Path.GetFileName(png))}?v={File.GetLastWriteTimeUtc(png).Ticks}";
                    payload.Add(new { id, url });
                }
                catch { }
            }

            bool renderAuxiliaryModels = root.TryGetProperty("renderAuxiliaryModels", out var ram) && ram.GetBoolean();
            var auxiliaryModels = new List<object>();
            if (renderAuxiliaryModels && auxSetups.Length > 0)
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "exporter", "DDOGlbExporter.exe");
                foreach (var setupId in auxSetups.Take(8))
                {
                    if (setupId == null || generation != previewGeneration) continue;
                    string safeSetup = setupId.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                    string glb = Path.Combine(previewCache, $"vfx_setup_{safeSetup}_v163.glb");
                    try
                    {
                        if (!File.Exists(glb) || new FileInfo(glb).Length < 64)
                        {
                            var psi = new ProcessStartInfo(exe, $"{setupId} \"{glb}\"")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };
                            ConfigureExporterEnvironment(psi);
                            using var proc = Process.Start(psi);
                            if (proc == null) continue;
                            string stdout = await proc.StandardOutput.ReadToEndAsync();
                            string stderr = await proc.StandardError.ReadToEndAsync();
                            await proc.WaitForExitAsync();
                            if (proc.ExitCode != 0 || !File.Exists(glb) || new FileInfo(glb).Length < 64)
                                continue;
                        }
                        string glbUrl = $"https://ddocache.ddo/{Uri.EscapeDataString(Path.GetFileName(glb))}?v={File.GetLastWriteTimeUtc(glb).Ticks}";
                        auxiliaryModels.Add(new { setup = setupId, url = glbUrl });
                    }
                    catch { }
                }
            }

            async Task<List<object>> LoadTexturePayloadAsync(IEnumerable<string?> ids, string prefix, int max)
            {
                var result = new List<object>();
                foreach (var id in ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(max))
                {
                    if (id == null) continue;
                    string safe = id.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                    string png = Path.Combine(previewCache, $"{prefix}_{safe}.png");
                    try
                    {
                        if (!File.Exists(png) || new FileInfo(png).Length < 32)
                        {
                            using var ir = await http.GetAsync($"Image/{Uri.EscapeDataString(id)}");
                            if (!ir.IsSuccessStatusCode) continue;
                            await using var fs = File.Create(png);
                            await ir.Content.CopyToAsync(fs);
                        }
                        if (new FileInfo(png).Length < 32) continue;
                        string url = $"https://ddocache.ddo/{Uri.EscapeDataString(Path.GetFileName(png))}?v={File.GetLastWriteTimeUtc(png).Ticks}";
                        result.Add(new { id, url });
                    }
                    catch { }
                }
                return result;
            }

            var runtimeAuraPayload = await LoadTexturePayloadAsync(runtimeTextures, "runtime_aura", 18);
            var runtimeTextureSet = runtimeTextures.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var auraPayload = await LoadTexturePayloadAsync(auraTextures.Where(x => x == null || !runtimeTextureSet.Contains(x)), "aura", 18);
            var combinedAuraPayload = runtimeAuraPayload.Concat(auraPayload).Take(24).ToList();

            if (generation != previewGeneration) return;
            bool weaponLike = slotJs != null
                || row.CanMainHand
                || row.CanOffHand
                || row.WeenieTypeName.Contains("Weapon", StringComparison.OrdinalIgnoreCase)
                || row.EquipmentKind.Contains("Weapon", StringComparison.OrdinalIgnoreCase);

            string payloadJs = JsonSerializer.Serialize(new
            {
                particleDescriptions = ps,
                textures = payload,
                auraTextures = combinedAuraPayload,
                auxiliaryModels,
                auxiliaryMeshes = auxMeshes,
                auxiliaryMaterials = auxMaterials,
                effectMode,
                anchorMode = weaponLike ? "weapon" : "model",
                weaponType = row.WeaponTypeName,
                twoHanded = row.IsTwoHanded,
                itemName = row.Name,
                dbId = row.DbId == 0 ? null : $"0x{row.DbId:X8}",
                approximate = ps.Length > 0 && auxiliaryModels.Count == 0
            });
            await ApplyTargetAsync(payloadJs);

            if (slotJs == null && (ps.Length > 0 || auxMeshes.Length > 0 || auxiliaryModels.Count > 0 || combinedAuraPayload.Count > 0))
            {
                details.Text += $"\r\n\r\nVISUAL EFFECTS\r\n   Additional model effects were resolved for this asset.";
            }
        }
        catch (Exception ex)
        {
            if (generation == previewGeneration)
            {
                try { await ClearTargetAsync(); } catch { }
                if (slotJs == null)
                    details.Text += $"\r\n\r\nVISUAL EFFECTS\r\n   Resolver error: {ex.Message}";
            }
        }
    }

    void TogglePresentationMode()
    {
        if (viewer?.CoreWebView2 == null) return;
        presentationMode = !presentationMode;
        if (presentationMode)
        {
            savedBorderStyle = FormBorderStyle;
            savedWindowState = WindowState;
            if (appHeader != null) appHeader.Visible = false;
            if (appNav != null) appNav.Visible = false;
            if (appTop != null) appTop.Visible = false;
            if (appSearchBar != null) appSearchBar.Visible = false;
            if (appBottom != null) appBottom.Visible = false;
            previewStatus.Visible = false;
            if (appMainSplit != null) appMainSplit.Panel1Collapsed = true;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            if (appHeader != null) appHeader.Visible = true;
            if (appNav != null) appNav.Visible = true;
            if (appTop != null) appTop.Visible = true;
            if (appSearchBar != null) appSearchBar.Visible = true;
            if (appBottom != null) appBottom.Visible = true;
            previewStatus.Visible = true;
            if (appMainSplit != null) appMainSplit.Panel1Collapsed = false;
            FormBorderStyle = savedBorderStyle;
            WindowState = savedWindowState;
        }
        _ = RunViewerScriptAsync($"window.ddoViewer?.setPresentationMode({presentationMode.ToString().ToLowerInvariant()});");
    }

    async Task SaveAnimationBindingDiagnosticAsync()
    {
        if (selected == null || selected.Setup == 0)
        {
            MessageBox.Show("Select a rigged/renderable model first.", "DDO Studio — Animation Binding Inspector");
            return;
        }

        try
        {
            SetBusy(true, "Inspecting animation binding metadata…");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDO Studio", "AnimationDiagnostics", "Bindings");
            Directory.CreateDirectory(dir);

            // 58 is our known decoded test clip's track count. The backend also reports the Setup's
            // actual joint count so candidate track-to-joint maps can be scored without assuming a specific rig.
            string url = $"AnimationRelationship/setup/0x{selected.Setup:X8}?trackCount=58&animationId=0x05000051&maxDepth=3&maxNodes=256";
            using var r = await http.GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode)
                throw new Exception($"Binding inspector returned HTTP {(int)r.StatusCode}: {body}");

            string safe = string.Concat(selected.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (string.IsNullOrWhiteSpace(safe)) safe = $"setup_{selected.Setup:X8}";
            string path = Path.Combine(dir, $"{safe}_setup_{selected.Setup:X8}_relationships.json");

            // Pretty-print when possible so the diagnostic is easy to inspect/upload.
            try
            {
                using var doc = JsonDocument.Parse(body);
                body = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { }
            await File.WriteAllTextAsync(path, body);

            int seedCount = 0, nodeCount = 0, edgeCount = 0, pathCount = 0, strongMapCount = 0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("seeds", out var seeds)) seedCount = seeds.GetArrayLength();
                if (root.TryGetProperty("nodes", out var nodes)) nodeCount = nodes.GetArrayLength();
                if (root.TryGetProperty("edges", out var edges)) edgeCount = edges.GetArrayLength();
                if (root.TryGetProperty("paths", out var paths)) pathCount = paths.GetArrayLength();
                if (root.TryGetProperty("strongCandidateTrackMaps", out var maps)) strongMapCount = maps.GetArrayLength();
            }
            catch { }

            // 1.7.2: in the same button click, also introspect the actual DbAnimator and the WStates
            // reached by the 1.7.2 relationship graph. This avoids another manual diagnostic action.
            string introspectionPath = Path.Combine(dir, $"{safe}_setup_{selected.Setup:X8}_animator_introspection.json");
            int successfulParses = 0;
            try
            {
                using var ir = await http.GetAsync("AnimationIntrospection/inspect/0x05000051?maxDepth=6");
                var ibody = await ir.Content.ReadAsStringAsync();
                if (ir.IsSuccessStatusCode)
                {
                    try
                    {
                        using var idoc = JsonDocument.Parse(ibody);
                        var iroot = idoc.RootElement;
                        if (iroot.TryGetProperty("animationSdkParseAttempts", out var attempts))
                            successfulParses += attempts.EnumerateArray().Count(x => x.TryGetProperty("success", out var ok) && ok.GetBoolean());
                        if (iroot.TryGetProperty("wstates", out var ws))
                            foreach (var w in ws.EnumerateArray())
                                if (w.TryGetProperty("sdkParseAttempts", out var wa))
                                    successfulParses += wa.EnumerateArray().Count(x => x.TryGetProperty("success", out var ok) && ok.GetBoolean());
                        ibody = JsonSerializer.Serialize(iroot, new JsonSerializerOptions { WriteIndented = true });
                    }
                    catch { }
                    await File.WriteAllTextAsync(introspectionPath, ibody);
                }
                else
                {
                    await File.WriteAllTextAsync(introspectionPath, ibody);
                }
            }
            catch (Exception ix)
            {
                await File.WriteAllTextAsync(introspectionPath, "Introspection request failed: " + ix);
            }

            // 1.7.2: inventory Setup skeleton sizes and Animator bone counts so we can
            // identify which skeleton family is compatible with the decoded 58-track sample.
            string skeletonMatchPath = Path.Combine(dir, $"{safe}_setup_{selected.Setup:X8}_skeleton_match.json");
            int exactSkeletonCandidates = 0;
            int exactBoneCountAnimators = 0;
            try
            {
                using var mr = await http.GetAsync("AnimationSkeletonMatcher/match/0x05000051?radius=96&scanAllAnimators=true");
                var mbody = await mr.Content.ReadAsStringAsync();
                try
                {
                    using var mdoc = JsonDocument.Parse(mbody);
                    var mroot = mdoc.RootElement;
                    if (mroot.TryGetProperty("exactSkeletonCandidates", out var sc)) exactSkeletonCandidates = sc.GetArrayLength();
                    if (mroot.TryGetProperty("exactBoneCountAnimators", out var ac)) exactBoneCountAnimators = ac.GetArrayLength();
                    mbody = JsonSerializer.Serialize(mroot, new JsonSerializerOptions { WriteIndented = true });
                }
                catch { }
                await File.WriteAllTextAsync(skeletonMatchPath, mbody);
            }
            catch (Exception mex)
            {
                await File.WriteAllTextAsync(skeletonMatchPath, JsonSerializer.Serialize(new { error = mex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            }

            details.Text += $"\r\n\r\nANIMATION RELATIONSHIP + SDK INTROSPECTION\r\n   Setup: 0x{selected.Setup:X8}\r\n   Animation: 0x05000051\r\n   Seeds: {seedCount}\r\n   Graph nodes: {nodeCount}\r\n   Graph edges: {edgeCount}\r\n   Setup/animation paths: {pathCount}\r\n   Strong 58-track maps: {strongMapCount}\r\n   SDK parse successes: {successfulParses}\r\n   Relationship JSON: {path}\r\n   Introspection JSON: {introspectionPath}";
            previewStatus.Text = $"Diagnostics saved — {nodeCount} graph nodes, {successfulParses} SDK parses";
            MessageBox.Show($"Animation diagnostics saved:\r\n\r\n{path}\r\n{introspectionPath}\r\n{skeletonMatchPath}\r\n\r\nGraph nodes: {nodeCount}\r\nGraph edges: {edgeCount}\r\nSDK parse successes: {successfulParses}\r\n58-bone Setup candidates: {exactSkeletonCandidates}\r\n58-bone Animators: {exactBoneCountAnimators}\r\n\r\nThe three JSON files can be inspected together when investigating animation binding.", "DDO Studio — Animation Binding Inspector");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DDO Studio — Binding Inspector");
        }
        finally { SetBusy(false, status.Text); }
    }

    async Task ExportAsync()
    {
        if (selected == null) return;
        if (!await EnsureBackendAsync()) return;
        using var f = new SaveFileDialog
        {
            Filter = "glTF Binary (*.glb)|*.glb",
            FileName = SafeName(selected.Name) + ".glb",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DDO Studio", "Exports")
        };
        Directory.CreateDirectory(f.InitialDirectory!);
        if (f.ShowDialog() != DialogResult.OK) return;

        var cached = Path.Combine(previewCache, $"setup_{selected.Setup:X8}.glb");
        SetBusy(true, $"Exporting {selected.Name}…");
        try
        {
            string? appearanceComposition = await PrepareNpcAppearanceCompositionAsync(selected);
            // 1.7.2: a clean export rebuilds the composed GLB so APR appearance and
            // every compatible skeleton-family animation can be embedded together as Blender Actions.
            var exe = Path.Combine(AppContext.BaseDirectory, "exporter", "DDOGlbExporter.exe");
            if (!File.Exists(exe)) { MessageBox.Show("The bundled exporter is missing."); return; }

            string animationListArg = "";
            string? animationListPath = null;
            if (compatibleAnimationIds.Count > 0)
            {
                var exportTemp = Path.Combine(Path.GetTempPath(), "DDOStudio", "ExportLists");
                Directory.CreateDirectory(exportTemp);
                animationListPath = Path.Combine(exportTemp, $"setup_{selected.Setup:X8}_animations.txt");
                await File.WriteAllLinesAsync(animationListPath, compatibleAnimationIds.Distinct(StringComparer.OrdinalIgnoreCase));
                animationListArg = $" --animation-list \"{animationListPath}\"";
            }
            else if (!string.IsNullOrWhiteSpace(selectedAnimationId))
            {
                animationListArg = $" --animation {selectedAnimationId}";
            }

            string appearanceArg = appearanceComposition != null ? $" --appearance \"{appearanceComposition}\"" : "";
            bool directVisualSetup = IsStandaloneWearable(selected) && await HasDirectVisualSetupAsync(selected);
            string appearanceOnlyArg = appearanceComposition != null && IsStandaloneWearable(selected) && !directVisualSetup ? " --appearance-only" : "";
            var psi = new ProcessStartInfo(exe, $"0x{selected.Setup:X8} \"{f.FileName}\"{appearanceArg}{appearanceOnlyArg}{animationListArg}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            ConfigureExporterEnvironment(psi);
            var p = Process.Start(psi)!;
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception(stderr + Environment.NewLine + stdout);
            status.Text = "Export complete ✓";
            details.Text += "\r\n\r\n" + stdout;
            MessageBox.Show($"Exported successfully:\n\n{f.FileName}", "DDO Studio");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed");
            status.Text = "Export failed";
        }
        finally { SetBusy(false, status.Text); }
    }

    string? AnimationDisplayName(string id, string? fallback)
    {
        // Curated built-in names provide stable labels in a clean installation. A user's local
        // animation-aliases.json remains an override layer and is never bundled into releases.
        if (!string.IsNullOrWhiteSpace(id) && animationAliases.TryGetValue(id, out var alias) && !string.IsNullOrWhiteSpace(alias)) return alias;
        if (!string.IsNullOrWhiteSpace(id) && builtInAnimationAliases.TryGetValue(id, out var builtIn) && !string.IsNullOrWhiteSpace(builtIn)) return builtIn;
        return null;
    }

    static string StableDressingFingerprint(IEnumerable<DressingAppearanceSelection> selections)
    {
        uint hash = 2166136261;
        foreach (var x in selections.OrderBy(x => x.Id).ThenBy(x => x.Slot, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Appearance))
        {
            hash ^= x.Id; hash *= 16777619;
            hash ^= x.Appearance; hash *= 16777619;
            foreach (char ch in x.Slot ?? "") { hash ^= char.ToUpperInvariant(ch); hash *= 16777619; }
        }
        return hash.ToString("X8");
    }

    static string StableIdFingerprint(IEnumerable<uint> ids)
    {
        uint hash = 2166136261;
        foreach (var id in ids.OrderBy(x => x))
        {
            hash ^= id;
            hash *= 16777619;
        }
        return hash.ToString("X8");
    }

    static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    void SetBusy(bool b, string text)
    {
        progress.Visible = b;
        searchButton.Enabled = !b;
        viewAllButton.Enabled = !b;
        exportButton.Enabled = !b && selected != null;
        status.Text = text;
        UseWaitCursor = b;
    }
}

public sealed class TextureBrowserForm : Form
{
    sealed class TextureRow
    {
        public string Dat { get; set; } = "";
        public uint Id { get; set; }
        public override string ToString() => $"0x{Id:X8}";
    }

    readonly HttpClient http;
    readonly TextBox filter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter by surface ID — e.g. 41005B6A" };
    readonly Button refresh = new() { Text = "Build Texture Index", AutoSize = false, Size = new Size(154, 38) };
    readonly Button relations = new() { Text = "Build Relationships", AutoSize = false, Size = new Size(162, 38) };
    readonly Button export = new() { Text = "Export PNG", AutoSize = false, Size = new Size(116, 38), Enabled = false };
    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(15, 17, 20) };
    readonly TextBox info = new() { Dock = DockStyle.Bottom, Height = 230, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Label status = new() { Text = "Build the texture index to enumerate RenderSurface records in the installed DDO client.", AutoSize = true };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    readonly List<TextureRow> all = new();
    TextureRow? selected;

    static readonly Color Bg = Color.FromArgb(6, 9, 13);
    static readonly Color Panel = Color.FromArgb(10, 14, 20);
    static readonly Color TextMain = Color.FromArgb(235, 237, 240);
    static readonly Color TextMuted = Color.FromArgb(139, 149, 163);
    static readonly Color Accent = Color.FromArgb(210, 171, 92);

    public TextureBrowserForm(HttpClient client)
    {
        http = client;
        Text = "DDO Studio — Texture Browser";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(900, 600);
        Font = new Font("Segoe UI", 10.5f);
        BackColor = Bg;
        ForeColor = TextMain;

        list.Columns.Add("Surface ID", 150);
        list.Columns.Add("DAT", 110);
        list.Columns.Add("Dimensions", 120);
        list.Columns.Add("Format", 90);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 12, 12, 8) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(filter, 0, 0);
        top.Controls.Add(relations, 1, 0);
        top.Controls.Add(refresh, 2, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 455 };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(preview);
        split.Panel2.Controls.Add(info);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 8, 12, 12) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(export, 0, 0);
        bottom.Controls.Add(progress, 1, 0);
        bottom.Controls.Add(status, 2, 0);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);

        foreach (Control c in new Control[] { filter, info, list }) { c.BackColor = Panel; c.ForeColor = TextMain; }
        status.ForeColor = TextMuted;
        refresh.BackColor = Panel; refresh.ForeColor = TextMain; refresh.FlatStyle = FlatStyle.Flat;
        relations.BackColor = Panel; relations.ForeColor = TextMain; relations.FlatStyle = FlatStyle.Flat;
        export.BackColor = Accent; export.ForeColor = Color.Black; export.FlatStyle = FlatStyle.Flat;

        refresh.Click += async (_, _) => await LoadIndexAsync(true);
        relations.Click += async (_, _) => await BuildRelationsAsync();
        filter.TextChanged += (_, _) => ApplyFilter();
        list.SelectedIndexChanged += async (_, _) => await PreviewSelectedAsync();
        export.Click += async (_, _) => await ExportAsync();
        Shown += async (_, _) =>
        {
            await LoadIndexAsync(false);
            await RefreshRelationshipButtonAsync();
        };
    }

    async Task LoadIndexAsync(bool force)
    {
        try
        {
            SetBusy(true, force ? "Rebuilding texture index…" : "Loading texture index…");
            using var r = await http.GetAsync($"TextureCatalog/ids?refresh={(force ? "true" : "false")}");
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            all.Clear();
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                all.Add(new TextureRow
                {
                    Dat = e.GetProperty("dat").GetString() ?? "",
                    Id = e.GetProperty("id").GetUInt32()
                });
            }
            ApplyFilter();
            status.Text = $"Texture index ready — {all.Count:N0} RenderSurface record(s)";
        }
        catch (Exception ex)
        {
            status.Text = "Texture index failed";
            info.Text = "Texture catalog error:\r\n" + ex.Message + "\r\n\r\nSee backend.log in the local application-data folder for additional diagnostics.";
        }
        finally { SetBusy(false, status.Text); }
    }

    async Task RefreshRelationshipButtonAsync()
    {
        try
        {
            using var r = await http.GetAsync("AssetRelations/status");
            if (!r.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            bool ready = doc.RootElement.GetProperty("ready").GetBoolean();
            bool building = doc.RootElement.GetProperty("building").GetBoolean();
            relations.Text = ready ? "Relationships Ready ✓" : building ? "Building Relationships…" : "Build Relationships";
            relations.Enabled = !building;
        }
        catch { }
    }

    async Task BuildRelationsAsync()
    {
        try
        {
            SetBusy(true, "Building material / texture relationships…");
            relations.Enabled = false;
            relations.Text = "Building Relationships…";
            using var start = await http.PostAsync("AssetRelations/build?force=true", null);
            start.EnsureSuccessStatusCode();
            for (int i = 0; i < 7200; i++)
            {
                await Task.Delay(500);
                using var r = await http.GetAsync("AssetRelations/status");
                r.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                bool building = root.GetProperty("building").GetBoolean();
                bool ready = root.GetProperty("ready").GetBoolean();
                string stage = root.GetProperty("stage").GetString() ?? "Building";
                int done = root.GetProperty("done").GetInt32();
                int total = root.GetProperty("total").GetInt32();
                status.Text = total > 0 ? $"{stage} — {done:N0}/{total:N0}" : stage;
                if (!building)
                {
                    string error = root.GetProperty("error").GetString() ?? "";
                    if (!ready) throw new Exception(string.IsNullOrWhiteSpace(error) ? "Relationship build did not complete." : error);
                    relations.Text = "Relationships Ready ✓";
                    status.Text = "Material / texture relationship database ready ✓";
                    if (selected != null) await PreviewSelectedAsync();
                    return;
                }
            }
            throw new TimeoutException("Relationship indexing is still running. You can leave the app open and check again shortly.");
        }
        catch (Exception ex)
        {
            relations.Text = "Build Relationships";
            MessageBox.Show(this, ex.Message, "Relationship index", MessageBoxButtons.OK, MessageBoxIcon.Error);
            status.Text = "Relationship build failed";
        }
        finally
        {
            SetBusy(false, status.Text);
            relations.Enabled = true;
        }
    }

    async Task<string> RelationshipTextAsync(TextureRow row)
    {
        try
        {
            using var sr = await http.GetAsync("AssetRelations/status");
            if (!sr.IsSuccessStatusCode) return "\r\n\r\nUsed By: relationship database unavailable.";
            using var sd = JsonDocument.Parse(await sr.Content.ReadAsStringAsync());
            if (!sd.RootElement.GetProperty("ready").GetBoolean())
                return "\r\n\r\nUsed By: not indexed yet. Click Build Relationships to connect textures to materials, meshes, and models.";

            using var r = await http.GetAsync($"AssetRelations/texture/0x{row.Id:X8}");
            if (!r.IsSuccessStatusCode) return "\r\n\r\nUsed By: no material relationships found.";
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var sb = new System.Text.StringBuilder();
            sb.Append("\r\n\r\nUSED BY");
            var textureIds = root.GetProperty("textureIds").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            var modifiers = root.GetProperty("modifiers").EnumerateArray().ToArray();
            var materials = root.GetProperty("materials").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            var meshes = root.GetProperty("meshes").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            var setups = root.GetProperty("setups").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            var propNames = modifiers.SelectMany(m => m.GetProperty("properties").EnumerateArray())
                .Select(p => p.GetProperty("propertyName").GetString() ?? "Texture")
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            sb.Append($"\r\nRole: {(propNames.Length > 0 ? string.Join(", ", propNames) : "Texture property")}");
            if (textureIds.Length > 0) sb.Append("\r\nRenderTexture: " + string.Join(", ", textureIds.Take(8).Select(x => $"0x{x:X8}")) + (textureIds.Length > 8 ? " …" : ""));
            sb.Append($"\r\nMaterial modifiers: {modifiers.Length:N0}");
            sb.Append($"\r\nMaterial instances: {materials.Length:N0}");
            sb.Append($"\r\nRender meshes: {meshes.Length:N0}");
            sb.Append($"\r\nSetups / models: {setups.Length:N0}");
            if (materials.Length > 0) sb.Append("\r\nMaterials: " + string.Join(", ", materials.Take(12).Select(x => $"0x{x:X8}")) + (materials.Length > 12 ? " …" : ""));
            if (setups.Length > 0) sb.Append("\r\nModels: " + string.Join(", ", setups.Take(12).Select(x => $"0x{x:X8}")) + (setups.Length > 12 ? " …" : ""));
            return sb.ToString();
        }
        catch (Exception ex) { return $"\r\n\r\nUsed By lookup failed: {ex.Message}"; }
    }

    void ApplyFilter()
    {
        string q = filter.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        list.BeginUpdate();
        list.Items.Clear();
        int shown = 0;
        foreach (var row in all)
        {
            string hex = row.Id.ToString("X8");
            if (q.Length > 0 && !hex.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.Dat.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            var li = new ListViewItem($"0x{row.Id:X8}") { Tag = row };
            li.SubItems.Add(row.Dat);
            li.SubItems.Add("");
            li.SubItems.Add("");
            list.Items.Add(li);
            if (++shown >= 25000) break;
        }
        list.EndUpdate();
        if (shown >= 25000) status.Text = $"Showing first 25,000 of {all.Count:N0}; type an ID to narrow the list.";
    }

    async Task PreviewSelectedAsync()
    {
        if (list.SelectedItems.Count == 0) return;
        selected = list.SelectedItems[0].Tag as TextureRow;
        if (selected == null) return;
        export.Enabled = false;
        try
        {
            status.Text = $"Decoding 0x{selected.Id:X8}…";
            using var mr = await http.GetAsync($"TextureCatalog/{selected.Dat}/0x{selected.Id:X8}/meta");
            mr.EnsureSuccessStatusCode();
            using var md = JsonDocument.Parse(await mr.Content.ReadAsStringAsync());
            int w = md.RootElement.GetProperty("width").GetInt32();
            int h = md.RootElement.GetProperty("height").GetInt32();
            string fmt = md.RootElement.GetProperty("format").GetString() ?? "?";
            bool dec = md.RootElement.GetProperty("decodable").GetBoolean();
            list.SelectedItems[0].SubItems[2].Text = $"{w} × {h}";
            list.SelectedItems[0].SubItems[3].Text = fmt;
            info.Text = $"RenderSurface: 0x{selected.Id:X8}\r\nDAT: {selected.Dat}\r\nSize: {w} × {h}\r\nFormat: {fmt}\r\nDecoder: {(dec ? "supported" : "not yet supported")}";
            info.Text += await RelationshipTextAsync(selected);
            preview.Image?.Dispose(); preview.Image = null;
            if (!dec) { status.Text = $"0x{selected.Id:X8}: unsupported format {fmt}"; return; }
            var png = await http.GetByteArrayAsync($"TextureCatalog/{selected.Dat}/0x{selected.Id:X8}/png");
            using var ms = new MemoryStream(png);
            using var img = Image.FromStream(ms);
            preview.Image = new Bitmap(img);
            export.Enabled = true;
            status.Text = $"0x{selected.Id:X8} — {w} × {h} {fmt}";
        }
        catch (Exception ex)
        {
            info.Text = $"RenderSurface 0x{selected.Id:X8}\r\n\r\nPreview failed:\r\n{ex.Message}";
            status.Text = "Texture preview failed";
        }
    }

    async Task ExportAsync()
    {
        if (selected == null) return;
        using var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"surface_{selected.Id:X8}.png" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var png = await http.GetByteArrayAsync($"TextureCatalog/{selected.Dat}/0x{selected.Id:X8}/png");
            await File.WriteAllBytesAsync(dlg.FileName, png);
            status.Text = $"Exported {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Texture export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void SetBusy(bool busy, string text)
    {
        progress.Visible = busy;
        refresh.Enabled = !busy;
        relations.Enabled = !busy;
        status.Text = text;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) preview.Image?.Dispose();
        base.Dispose(disposing);
    }
}
