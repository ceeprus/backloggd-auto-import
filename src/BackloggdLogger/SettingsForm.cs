using BackloggdLogger.Core;

namespace BackloggdLogger;

sealed record SettingsChoice(AppSettings Settings, bool PromptsPaused, bool StartWithWindows);

sealed class SettingsForm : ThemedWindow
{
    // The unit after the input ("seconds") is where an invalid value's fix is written, so the
    // window keeps one height.
    sealed class NumberRow(string name, SettingRange range, ThemedInput input, Label unit)
    {
        public string Name { get; } = name;
        public SettingRange Range { get; } = range;
        public ThemedInput Input { get; } = input;
        public Label Unit { get; } = unit;
        public int? Value => Range.Parse(Input.Box.Text);

        public void Set(int v) => Input.Box.Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    readonly AppSettings _initial;
    readonly Action<SettingsChoice> _save;
    readonly TableLayoutPanel _root = Stack();
    readonly TableLayoutPanel _grid = new()
    {
        ColumnCount = 3,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        BackColor = Theme.Surface,
        Margin = new Padding(0),
        Padding = new Padding(0),
    };
    readonly ThemedCheck _askStart = new("Ask when a game starts") { AlwaysShowFocusWhenFocused = true };
    readonly ThemedCheck _askClose = new("Ask when a game closes");
    readonly ThemedRadio _bottomRight = new("Bottom right");
    readonly ThemedRadio _topRight = new("Top right");
    readonly ThemedCheck _detectEmulators = new("Detect emulators automatically");
    readonly ThemedCheck _detectLaunchers = new(DetectLaunchersText);
    readonly ThemedCheck _journal = new("Add play time to the journal automatically");
    readonly ThemedCheck _blockAds = new(BlockAdsText);
    readonly ThemedCheck _startup = new("Start with Windows");
    readonly ThemedCheck _paused = new("Pause popups");
    readonly NumberRow _startSeconds;
    readonly NumberRow _closeSeconds;
    readonly NumberRow _minSession;
    readonly NumberRow _poll;
    readonly NumberRow _delay;
    readonly ThemedButton _saveButton = new("Save", ButtonKind.Primary);

    public SettingsForm(AppSettings current, bool promptsPaused, bool startWithWindows, Action<SettingsChoice> save)
    {
        _initial = current;
        _save = save;
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 600);

        AddRow(_root, Heading("Settings", Theme.S4));

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Group("Popups", first: true);
        Wide(_askStart);
        Wide(_askClose);
        _startSeconds = Number("Start popup stays for", SettingRanges.StartPopup);
        _closeSeconds = Number("Close popup stays for", SettingRanges.ClosePopup);
        var corner = Flow(_bottomRight, _topRight);
        corner.AccessibleName = "Corner";
        Labeled("Corner", corner);

        Group("Detection");
        _minSession = Number("Count a session after", SettingRanges.MinSession);
        _poll = Number("Check for games every", SettingRanges.Poll);
        _delay = Number("Wait before opening Backloggd", SettingRanges.WindowDelay);
        Wide(_detectEmulators);
        Wide(_detectLaunchers);

        Group("Backloggd");
        Wide(_journal);
        Wide(Caption(JournalCaption, Theme.TextSubtle, CheckIndent + Theme.FocusPad + ChoicePaint.Box + ChoicePaint.TextGap - LabelInset));
        Wide(_blockAds);

        Group("App");
        Wide(_startup);
        Wide(_paused);

        Group("About");
        Wide(About());

        AddRow(_root, _grid);

        // Its text lines up with the labels above it (the hover fill hugs the text).
        var reset = new ThemedButton("Reset to defaults", ButtonKind.Ghost) { SidePadding = LabelInset };
        reset.Click += (_, _) => Fill(new AppSettings());
        var cancel = new ThemedButton("Cancel", ButtonKind.Secondary);
        cancel.Click += (_, _) => Close();
        _saveButton.Click += (_, _) => TrySave();
        var left = new FlowLayoutPanel { WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
        left.Controls.Add(reset);
        var right = Footer(_saveButton, cancel);
        right.Margin = new Padding(0);
        right.Dock = DockStyle.None;
        AddRow(_root, new ActionBar(left, right) { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S5 - Theme.FocusPad, 0, 0) });

        Controls.Add(_root);
        AcceptButton = _saveButton;
        CancelButton = cancel;
        ActiveControl = _askStart;

        Fill(current);
        _paused.Checked = promptsPaused;
        _startup.Checked = startWithWindows;
        EndBuild();
    }

    public const string RepoUrl = "https://github.com/ceeprus/backloggd-auto-import";
    public const string XUrl = "https://x.com/Ceeprus";

    // Name and version with the links on the left, the app's picture on the right.
    static TableLayoutPanel About()
    {
        var about = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        about.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        about.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        text.Controls.Add(new Label
        {
            Text = "Backloggd Logger",
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(Theme.FocusPad, 0, Theme.FocusPad, 0),
        });
        text.Controls.Add(Caption(VersionText, Theme.TextSubtle, Theme.FocusPad, Theme.S2));
        text.Controls.Add(Link("GitHub", RepoUrl));
        text.Controls.Add(Link("X (@Ceeprus)", XUrl));
        about.Controls.Add(text, 0, 0);
        about.Controls.Add(new PictureBox
        {
            Image = AppAssets.Avatar,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(72, 72),
            BackColor = Theme.Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(Theme.S3, 0, Theme.FocusPad, 0),
            AccessibleName = "Backloggd Logger icon",
        }, 1, 0);
        return about;
    }

    internal static Action<string>? OpenUrlOverride;

    static LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            BackColor = Theme.Surface,
            LinkColor = Theme.Link,
            ActiveLinkColor = Theme.Link,
            VisitedLinkColor = Theme.Link,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(Theme.FocusPad, 0, Theme.FocusPad, Theme.S1),
            AccessibleDescription = url,
        };
        link.LinkClicked += (_, _) =>
        {
            OpenUrl(url);
        };
        return link;
    }

    internal static void OpenUrl(string url)
    {
        try { if (OpenUrlOverride is { } open) open(url); else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("errors", "open link: " + ex.Message); }
    }

    public static string VersionText => "Version " + (typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "unknown");

    public const string DetectLaunchersText = "Detect Epic, GOG and Battle.net games";

    public const string BlockAdsText = "Block ads in the Backloggd window";

    public const string JournalCaption = "Adds each day's play time to the Backloggd journal after the game closes.";

    // Lines the box of a full-width checkbox up with the first glyph of the labels above it, which
    // the label control draws a few pixels in from its edge. In 96 DPI units: the window scales it.
    const int LabelInset = 3;
    const int CheckIndent = LabelInset;

    void Group(string title, bool first = false)
    {
        if (!first)
            Wide(new Hairline { Dock = DockStyle.Fill, Margin = new Padding(Theme.FocusPad, Theme.S3, Theme.FocusPad, Theme.S3) });
        Wide(Caption(title, Theme.TextSubtle, Theme.FocusPad, Theme.S1));
    }

    static Label Caption(string text, Color color, int left, int bottom = 0) => new()
    {
        Text = text,
        Font = Theme.Caption,
        ForeColor = color,
        AutoSize = true,
        Margin = new Padding(left, 0, Theme.FocusPad, bottom),
    };

    static FlowLayoutPanel Flow(params Control[] items)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        for (var i = 0; i < items.Length; i++)
        {
            items[i].Margin = new Padding(0, 0, i == items.Length - 1 ? 0 : Theme.S3, 0);
            f.Controls.Add(items[i]);
        }
        return f;
    }

    void Wide(Control c)
    {
        if (c is ThemedCheck) c.Margin = new Padding(CheckIndent, 0, 0, 0);
        var row = _grid.RowCount++;
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Controls.Add(c, 0, row);
        _grid.SetColumnSpan(c, 3);
    }

    Label Labeled(string label, Control control, Control? after = null)
    {
        var row = _grid.RowCount++;
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label
        {
            Text = label,
            ForeColor = Theme.Text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(Theme.FocusPad, 0, Theme.S4, 0),
        };
        control.Anchor = AnchorStyles.Left;
        _grid.Controls.Add(l, 0, row);
        _grid.Controls.Add(control, 1, row);
        if (after is null) _grid.SetColumnSpan(control, 2);
        else
        {
            after.Anchor = AnchorStyles.Left;
            _grid.Controls.Add(after, 2, row);
        }
        return l;
    }

    NumberRow Number(string label, SettingRange range)
    {
        var input = new ThemedInput(label) { FixedWidth = 64 };
        input.Box.MaxLength = 4;
        input.Box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        input.Margin = new Padding(Theme.FocusPad, Theme.S1, Theme.S2, Theme.S1);
        var unit = new Label { Text = Unit, ForeColor = Theme.TextMuted, AutoSize = true, Margin = new Padding(0) };
        Labeled(label, input, unit);

        var n = new NumberRow(label, range, input, unit);
        input.Box.TextChanged += (_, _) => ShowError(n);
        return n;
    }

    const string Unit = "seconds";

    IEnumerable<NumberRow> Numbers => new[] { _startSeconds, _closeSeconds, _minSession, _poll, _delay };

    void ShowError(NumberRow n)
    {
        var message = n.Range.Check(n.Input.Box.Text);
        n.Unit.Text = message ?? Unit;
        n.Unit.ForeColor = message is null ? Theme.TextMuted : Theme.Danger;
        n.Input.Box.AccessibleDescription = message;
        n.Input.Invalid = message is not null;
        _saveButton.Enabled = Numbers.All(r => r is null || r.Value is not null);
    }

    void Fill(AppSettings s)
    {
        _askStart.Checked = s.AskOnStart;
        _askClose.Checked = s.AskOnClose;
        _startSeconds.Set(s.StartPopupSeconds);
        _closeSeconds.Set(s.ClosePopupSeconds);
        _bottomRight.Checked = s.PopupCorner == PopupCorner.BottomRight;
        _topRight.Checked = s.PopupCorner == PopupCorner.TopRight;
        _minSession.Set(s.MinSessionSeconds);
        _poll.Set(s.PollSeconds);
        _delay.Set(s.WindowDelayAfterGameSeconds);
        _detectEmulators.Checked = s.DetectEmulators;
        _detectLaunchers.Checked = s.DetectLauncherGames;
        _journal.Checked = s.AutoAddJournalTime;
        _blockAds.Checked = s.BlockAds;
    }

    public void SyncApp(bool? promptsPaused = null, bool? startWithWindows = null)
    {
        if (promptsPaused is { } p) _paused.Checked = p;
        if (startWithWindows is { } s) _startup.Checked = s;
    }

    void TrySave()
    {
        foreach (var n in Numbers) ShowError(n);
        if (Numbers.FirstOrDefault(n => n.Value is null) is { } bad)
        {
            bad.Input.Box.Focus();
            bad.Input.Box.Select(bad.Input.Box.TextLength, 0);
            return;
        }
        var settings = _initial with
        {
            AskOnStart = _askStart.Checked,
            AskOnClose = _askClose.Checked,
            StartPopupSeconds = _startSeconds.Value!.Value,
            ClosePopupSeconds = _closeSeconds.Value!.Value,
            PopupCorner = _topRight.Checked ? PopupCorner.TopRight : PopupCorner.BottomRight,
            MinSessionSeconds = _minSession.Value!.Value,
            PollSeconds = _poll.Value!.Value,
            WindowDelayAfterGameSeconds = _delay.Value!.Value,
            DetectEmulators = _detectEmulators.Checked,
            DetectLauncherGames = _detectLaunchers.Checked,
            AutoAddJournalTime = _journal.Checked,
            BlockAds = _blockAds.Checked,
        };
        _save(new SettingsChoice(settings, _paused.Checked, _startup.Checked));
        Close();
    }

    // As tall as the content, but never taller than the working area; on a screen too short for it
    // the content scrolls.
    void FitHeight()
    {
        var h = _root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var max = area.Height - (Height - ClientSize.Height);
        if (h > max)
        {
            _root.Dock = DockStyle.Top;
            _root.Height = h;
            AutoScroll = true;
            h = max;
        }
        if (h != ClientSize.Height) ClientSize = new Size(ClientSize.Width, h);
    }

    protected override void OnLoad(EventArgs e)
    {
        FitHeight();
        base.OnLoad(e);
    }
}
