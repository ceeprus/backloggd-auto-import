using BackloggdLogger.Core;

namespace BackloggdLogger;

// The popups that went away unanswered, newest first. Built from the Tracked games parts, and it follows the
// queue while open.
sealed class MissedPopupsForm : ThemedWindow
{
    internal const string EmptyText = "Nothing missed.";
    internal const string AppName = "Backloggd Logger";

    enum Column { Icon, Game, What, When }

    // Fixed widths at 96 DPI; Game and What share what is left.
    const int IconWidth = 32;
    const int IconSize = 20;

    readonly AppState _state;
    readonly Action<MissedPopup> _showAgain;
    readonly GameList _list = new() { AccessibleName = "Missed popups", HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly ListScrollBar _scroll;
    readonly ActionBar _bar;
    readonly FramePanel _frame;
    readonly EmptyState _empty = new(Theme.SurfaceAlt) { Dock = DockStyle.Fill, Visible = false, Message = EmptyText };
    readonly ThemedButton _show;
    readonly ThemedButton _dismiss;
    readonly ThemedButton _dismissAll;
    readonly ImageList _rows;
    readonly GameIcons _icons = new();
    readonly ToolTip _tip = Theme.DarkToolTip();
    readonly ThemedMenu _rowMenu = new();
    // "5 min ago" grows old while the window is open.
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 };
    int _hot = -1;
    string? _tipText;

    internal Func<DateTime> Clock = () => DateTime.Now;

    public MissedPopupsForm(AppState state, Action<MissedPopup> showAgain)
    {
        _state = state;
        _showAgain = showAgain;
        Text = "Missed popups";
        ClientSize = new Size(720, 420);
        MinimumSize = SizeFromClientSize(new Size(560, 300));

        var root = Stack();
        AddRow(root, Heading("Missed popups", Theme.S3));

        // The list adds one pixel under the image, so 31 gives the 32 px row.
        _rows = new ImageList { ImageSize = new Size(1, Px(32) - 1) };
        _list.SmallImageList = _rows;
        foreach (var c in Enum.GetValues<Column>())
            _list.Columns.Add(new ColumnHeader { Text = c == Column.Icon ? "" : c.ToString(), Width = Px(80) });

        _frame = new FramePanel(_list) { Dock = DockStyle.Fill, Margin = Inset() };
        _list.Dock = DockStyle.Top;
        _scroll = new ListScrollBar(_list) { AccessibleName = "Missed popups scrollbar" };
        _frame.Controls.Add(_scroll);
        _frame.Controls.Add(_list);
        _frame.Controls.Add(_empty);
        _frame.Layout += (_, _) => FitList();
        AddRow(root, _frame, fill: true);

        var left = new FlowLayoutPanel
        {
            WrapContents = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        _show = Button("Show again", ButtonKind.Primary, ShowAgain);
        _dismiss = Button("Dismiss", ButtonKind.Secondary, Dismiss);
        left.Controls.Add(_show);
        left.Controls.Add(_dismiss);
        foreach (Control c in left.Controls) c.Margin = new Padding(0, 0, Theme.S2 - Theme.FocusPad * 2, 0);
        _dismissAll = Button("Dismiss all", ButtonKind.Secondary, DismissAll);

        _bar = new ActionBar(left, _dismissAll) { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S3 - Theme.FocusPad, 0, 0) };
        AddRow(root, _bar);
        Controls.Add(root);
        ActiveControl = _list;
        _frame.TabIndex = 0;
        _bar.TabIndex = 1;

        BuildRowMenu();

        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += DrawCell;
        _list.SelectedIndexChanged += (_, _) => UpdateActions();
        _list.MouseMove += OnListMouseMove;
        _list.MouseWheel += (_, _) => BeginInvoke(() =>
        {
            var p = _list.PointToClient(Cursor.Position);
            SetHot(_list.ClientRectangle.Contains(p) ? _list.HitTest(p).Item?.Index ?? -1 : -1);
        });
        _list.MouseLeave += (_, _) => { SetHot(-1); SetTip(null); };
        _list.DoubleClick += (_, _) => ShowAgain();
        _list.SetColumnSpecs(ColumnSpecs);
        _list.DpiChangedAfterParent += (_, _) => MeasureColumns();
        _list.RowMenu += ShowRowMenu;
        _state.MissedPopups.Changed += OnQueueChanged;
        _clock.Tick += (_, _) => RefreshWhen();
        _clock.Start();

        Refill();
        // Enter shows the newest one again straight away.
        if (_list.Items.Count > 0) { _list.Items[0].Selected = true; _list.Items[0].Focused = true; }
        EndBuild();
    }

    ThemedButton Button(string text, ButtonKind kind, Action onClick)
    {
        var b = new ThemedButton(text, kind);
        b.Click += (_, _) => onClick();
        return b;
    }

    MissedPopup? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as MissedPopup : null;

    void ShowAgain()
    {
        if (Selected is { } m) _showAgain(m);
    }

    void Dismiss()
    {
        if (Selected is { } m) _state.MissedPopups.Remove(m.Id);
    }

    void DismissAll()
    {
        var n = _state.MissedPopups.Count;
        if (n == 0) return;
        if (n > 1 && !ConfirmDialog.Ask(this, "Dismiss all", $"Dismiss all {n} missed popups?", "They leave this list. Nothing changes on Backloggd.", "Dismiss all")) return;
        _state.MissedPopups.Clear();
    }

    void OnQueueChanged()
    {
        if (IsDisposed) return;
        try { Refill(); }
        catch (Exception ex) { Log.Write("errors", "missed popups refill: " + ex); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !_list.Focused) return;
        if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift)) ShowRowMenu(null);
        else if (e.KeyCode == Keys.Enter) ShowAgain();
        else if (e.KeyCode == Keys.Delete) Dismiss();
        else return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshWhen();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _state.MissedPopups.Changed -= OnQueueChanged;
        _clock.Dispose();
        _tip.Dispose();
        _rows.Dispose();
        _icons.Dispose();
        _rowMenu.Dispose();
        base.OnFormClosed(e);
    }

    // Row menu

    internal ThemedMenu RowMenu => _rowMenu;

    void BuildRowMenu()
    {
        void Add(string text, Action act)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) => act();
            _rowMenu.Items.Add(item);
        }
        Add("Show again", ShowAgain);
        Add("Dismiss", Dismiss);
        _rowMenu.Items.Add(new ToolStripSeparator());
        Add("Dismiss all", DismissAll);
        _rowMenu.FitItemHeights();
        _rowMenu.Opening += (_, e) =>
        {
            if (Selected is null) e.Cancel = true;
        };
    }

    // A right-click selects the row under it first; the keyboard opens the menu at the selected row.
    void ShowRowMenu(Point? at)
    {
        // The key and the WM_CONTEXTMENU it also makes would otherwise open it twice.
        if (_rowMenu.Visible) return;
        if (at is { } screen)
        {
            var hit = _list.HitTest(_list.PointToClient(screen)).Item;
            if (hit is null) return;
            if (!hit.Selected)
            {
                foreach (ListViewItem i in _list.SelectedItems) i.Selected = false;
                hit.Selected = true;
            }
            hit.Focused = true;
            _list.Focus();
            _rowMenu.Show(screen);
            return;
        }
        if (_list.SelectedItems.Count == 0) return;
        var row = _list.SelectedItems[0];
        row.EnsureVisible();
        var cell = TextRect(CellRect(row, (int)Column.Game), (int)Column.Game);
        _rowMenu.Show(_list, new Point(cell.X, row.Bounds.Bottom));
    }

    // Rows

    // A notice about the app itself has no game: the row carries the app's name and avatar.
    string GameName(MissedPopup m) =>
        m.GameId is { } id && _state.Games.TryGetValue(id, out var g) ? g.DisplayName : AppName;

    string Cell(MissedPopup m, Column c, DateTime now) => c switch
    {
        Column.What => m.What,
        Column.When => Format.When(m.At, now),
        // The icon column also holds the name, so typing a name's first letters finds its row.
        _ => GameName(m),
    };

    // Keeps the selection on the same popup; when that one left the list, on the row that took its
    // place, so Delete can be pressed again.
    public void Refill()
    {
        var selectedId = Selected?.Id;
        var selectedIndex = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var top = _list.TopItem?.Index ?? 0;
        var now = Clock();
        var rows = _state.MissedPopups.Items
            .Select(m => new ListViewItem(Enum.GetValues<Column>().Select(c => Cell(m, c, now)).ToArray()) { Tag = m }).ToArray();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Items.AddRange(rows);
            var keep = rows.FirstOrDefault(i => ((MissedPopup)i.Tag!).Id == selectedId)
                ?? (selectedIndex >= 0 && rows.Length > 0 ? rows[Math.Min(selectedIndex, rows.Length - 1)] : null);
            if (keep is not null) { keep.Selected = true; keep.Focused = true; }
        }
        finally { _list.EndUpdate(); }
        // After EndUpdate: inside an update the list can't scroll to a row near the end.
        if (_list.Items.Count > 0) _list.TopItem = _list.Items[Math.Min(top, _list.Items.Count - 1)];
        if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();
        _hot = -1;
        var empty = _list.Items.Count == 0;
        var hadFocus = ContainsFocus;
        _empty.Visible = empty;
        _list.Visible = !empty;
        _bar.Visible = !empty;
        if (!empty && hadFocus && !ContainsFocus) _list.Focus();
        FitList();
        MeasureColumns();
        UpdateActions();
        _scroll.Sync();
    }

    void RefreshWhen()
    {
        var now = Clock();
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not MissedPopup m) continue;
            var text = Format.When(m.At, now);
            var cell = item.SubItems[(int)Column.When];
            if (cell.Text != text) cell.Text = text;
        }
        MeasureColumns();
    }

    // A moved or restored window is painted again in full, so nothing drawn for the old place or
    // size stays.
    void RepaintList()
    {
        _frame.Invalidate(true);
        _list.RepaintHeader();
        _scroll.Sync();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        RepaintList();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_frame is not null) RepaintList();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (_frame is not null) RepaintList();
    }

    // The list is the header plus whole rows: below the last row the native list draws its own
    // column lines, so the frame (same color) fills whatever is left under the rows.
    void FitList()
    {
        var room = _frame.DisplayRectangle.Height;
        if (room <= 0) return;
        var h = Math.Min(_list.WholeRowsHeight(room, Px(32)), _list.ContentHeight(Px(32)));
        if (_list.Height != h) _list.Height = h;
    }

    void UpdateActions()
    {
        var any = Selected is not null;
        // Read before the buttons change: disabling the focused button moves the focus on.
        var focused = ActiveControl as ThemedButton;
        _show.Enabled = any;
        _dismiss.Enabled = any;
        if (focused is { Enabled: false } || ActiveControl is ThemedButton { Enabled: false }) ActiveControl = _list;
    }

    int _whenWidth;

    // When is as wide as its widest text ("Yesterday 21:40"), so it is never cut; measured again
    // whenever the rows, their texts or the DPI change.
    void MeasureColumns()
    {
        var texts = _list.Items.Cast<ListViewItem>().Select(item => item.SubItems[(int)Column.When].Text);
        _whenWidth = _list.ContentWidth(nameof(Column.When), texts, TextInset((int)Column.When), Px(Theme.S2), 0);
        _list.FitColumns();
    }

    // Game and What share the rest 2:3. In a window too narrow for their minimums What gives up
    // width first, then Game.
    IReadOnlyList<ColumnSpec> ColumnSpecs() => new[]
    {
        ColumnSpec.Fixed(Px(IconWidth)),
        ColumnSpec.Flex(2, Px(140), Px(72), 2),
        ColumnSpec.Flex(3, Px(160), Px(72), 1),
        ColumnSpec.Fixed(_whenWidth > 0 ? _whenWidth : Px(96)),
    };

    void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        SetHot(hit.Item?.Index ?? -1);
        var col = hit.Item is not null && hit.SubItem is not null ? hit.Item.SubItems.IndexOf(hit.SubItem) : -1;
        SetTip(hit.Item is { } item ? TipFor(item, col) : null);
    }

    // A cell cut short shows its full text.
    internal string? TipFor(ListViewItem item, int col) =>
        col > 0 && col < item.SubItems.Count && Truncated(item, col) ? item.SubItems[col].Text : null;

    internal Rectangle CellRect(ListViewItem item, int col)
    {
        var row = item.Bounds;
        return col == 0 ? new Rectangle(row.X, row.Y, _list.Columns[0].Width, row.Height) : item.SubItems[col].Bounds;
    }

    // Where a column's text starts inside its cell, the same for the header and the rows: 12 in the
    // first column (the selected row's accent sits there), 8 in the others.
    internal int TextInset(int col) => Px(col == 0 ? Theme.S3 : Theme.S2);

    Rectangle TextRect(Rectangle cell, int col)
    {
        var left = TextInset(col);
        return new Rectangle(cell.X + left, cell.Y, Math.Max(0, cell.Width - left - Px(Theme.S2)), cell.Height);
    }

    const TextFormatFlags CellFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    internal bool Truncated(ListViewItem item, int col)
    {
        var room = TextRect(CellRect(item, col), col).Width;
        var need = TextRenderer.MeasureText(item.SubItems[col].Text, Theme.Body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        return need > room;
    }

    void SetTip(string? text)
    {
        if (text == _tipText) return;
        _tipText = text;
        _tip.Hide(_list);
        _tip.SetToolTip(_list, text ?? "");
    }

    void SetHot(int index)
    {
        if (index == _hot) return;
        var old = _hot;
        _hot = index;
        InvalidateRow(old);
        InvalidateRow(index);
    }

    void InvalidateRow(int index)
    {
        if (index < 0 || index >= _list.Items.Count) return;
        var b = _list.Items[index].Bounds;
        _list.Invalidate(new Rectangle(0, b.Y, _list.ClientSize.Width, b.Height));
    }

    void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        var b = e.Bounds;
        using (var brush = new SolidBrush(Theme.Surface)) g.FillRectangle(brush, b);
        using (var brush = new SolidBrush(Theme.Border)) g.FillRectangle(brush, b.Left, b.Bottom - 1, b.Width, 1);
        var left = TextInset(e.ColumnIndex);
        var rect = new Rectangle(b.X + left, b.Y, Math.Max(0, b.Width - left - Px(Theme.S2)), b.Height);
        TextRenderer.DrawText(g, e.Header?.Text ?? "", Theme.Caption, rect, Theme.TextSubtle, CellFlags);
    }

    void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null) return;
        var g = e.Graphics;
        var row = e.Item.Bounds;
        var selected = e.Item.Selected;
        var raised = selected || e.ItemIndex == _hot;
        var cell = e.ColumnIndex == 0 ? CellRect(e.Item, 0) : e.Bounds;
        using (var brush = new SolidBrush(selected ? Theme.Highlight : raised ? Theme.Raised : Theme.SurfaceAlt)) g.FillRectangle(brush, cell);
        if (e.ColumnIndex == 0 && selected)
            using (var brush = new SolidBrush(Theme.Accent)) g.FillRectangle(brush, row.X, row.Y, Px(2), row.Height);

        if (e.ColumnIndex == (int)Column.Icon)
        {
            DrawIcon(g, cell, e.Item.Tag as MissedPopup);
            return;
        }
        var color = e.ColumnIndex == (int)Column.Game ? Theme.Text : Theme.TextMuted;
        TextRenderer.DrawText(g, e.SubItem.Text, Theme.Body, TextRect(cell, e.ColumnIndex), color, CellFlags);
    }

    // The 20 px icon sits where the first column's text would start, centred in the 32 px row.
    internal Rectangle IconRect(Rectangle cell)
    {
        var s = Px(IconSize);
        return new Rectangle(cell.X + TextInset(0), cell.Y + (cell.Height - s) / 2, s, s);
    }

    void DrawIcon(Graphics g, Rectangle cell, MissedPopup? m)
    {
        if (m is null) return;
        var game = m.GameId is { } id && _state.Games.TryGetValue(id, out var record) ? record : null;
        _icons.Draw(g, IconRect(cell), game is null ? AppAssets.Avatar : _icons.For(game), GameName(m), Px(4));
    }
}
