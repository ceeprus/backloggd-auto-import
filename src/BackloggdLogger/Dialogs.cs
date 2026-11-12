using System.Diagnostics;
using System.Runtime.InteropServices;
using BackloggdLogger.Core;
using Microsoft.Win32;

namespace BackloggdLogger;

sealed class WindowPicker : ThemedWindow
{
    sealed record Entry(string Title, string ExePath)
    {
        public string ExeName => Path.GetFileName(ExePath);
        public override string ToString() => $"{Title}, {ExeName}";
    }

    readonly HashSet<string> _already;
    readonly Action<(string Title, string ExePath)> _track;
    readonly List<Entry> _all = new();
    readonly PickerList _list = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None,
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.Text,
        Font = Theme.Body,
        AccessibleName = "Open windows",
    };
    readonly ThemedInput _filter = new("Filter by window or exe name", "Filter by window or exe name");
    readonly EmptyState _empty = new(Theme.SurfaceAlt, "Refresh") { Dock = DockStyle.Fill, Visible = false };
    readonly ThemedButton _ok = new("Track game", ButtonKind.Primary) { Enabled = false };
    readonly GameIcons _icons = new();
    int _hot = -1;

    public WindowPicker(IEnumerable<string> trackedPaths, Action<(string Title, string ExePath)> track)
    {
        _track = track;
        _already = new HashSet<string>(trackedPaths, StringComparer.OrdinalIgnoreCase);
        Text = "Track a running game";
        ClientSize = new Size(520, 460);
        MinimumSize = SizeFromClientSize(new Size(520, 460));

        var root = Stack();
        AddRow(root, Heading("Track a running game", Theme.S3));
        _filter.Dock = DockStyle.Fill;
        _filter.Margin = Inset(Theme.S2);
        AddRow(root, _filter);

        var frame = new FramePanel(_list) { Dock = DockStyle.Fill, Margin = Inset() };
        frame.Controls.Add(_list);
        frame.Controls.Add(_empty);
        AddRow(root, frame, fill: true);

        var cancel = new ThemedButton("Cancel", ButtonKind.Secondary);
        cancel.Click += (_, _) => Close();
        _ok.Click += (_, _) => Accept();
        AddRow(root, Footer(_ok, cancel));
        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = cancel;
        ActiveControl = _filter.Box;

        _list.ItemHeight = Px(44);
        _list.HandleCreated += (_, _) => _list.ItemHeight = Px(44);
        _list.DrawItem += DrawEntry;
        _list.MouseDoubleClick += (_, e) => { if (_list.IndexFromPoint(e.Location) >= 0) Accept(); };
        _list.MouseWheel += (_, _) => BeginInvoke(() => SetHot(HotFromCursor()));
        _list.SelectedIndexChanged += (_, _) => _ok.Enabled = _list.SelectedItem is Entry;
        _list.MouseMove += (_, e) => SetHot(_list.IndexFromPoint(e.Location));
        _list.MouseLeave += (_, _) => SetHot(-1);
        _list.HandleCreated += (_, _) => Theme.DarkControl(_list.Handle);
        _filter.Box.TextChanged += (_, _) => ApplyFilter();
        _filter.Box.KeyDown += OnFilterKey;
        _empty.Action!.Click += (_, _) => Scan();

        Scan();
        EndBuild();
    }

    void OnFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Down or Keys.Up) || _list.Items.Count == 0) return;
        var i = _list.SelectedIndex;
        _list.SelectedIndex = e.KeyCode == Keys.Down ? Math.Min(i + 1, _list.Items.Count - 1) : Math.Max(i - 1, 0);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    void Scan()
    {
        var self = Environment.ProcessId;
        _all.Clear();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    var title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var path = ProcessPaths.Get(p.Id);
                    if (path is not null && !_already.Contains(path)) _all.Add(new Entry(title, path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        _all.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var keep = _list.SelectedItem as Entry;
        var f = _filter.Box.Text.Trim();
        var shown = f.Length == 0
            ? _all
            : _all.Where(e => e.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || e.ExeName.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Items.AddRange(shown.ToArray<object>());
            if (keep is not null && shown.Contains(keep)) _list.SelectedItem = keep;
        }
        finally { _list.EndUpdate(); }
        _hot = -1;
        var empty = shown.Count == 0;
        _empty.Message = _all.Count == 0 ? "No windows found. Start the game, then press Refresh." : NoMatch(f);
        _empty.Visible = empty;
        _list.Visible = !empty;
        _ok.Enabled = _list.SelectedItem is Entry;
    }

    // Two sentences on two lines, so the second never leaves "Refresh." alone on a line.
    internal static string NoMatch(string filter) => $"Nothing matches \"{filter}\".\nChange the filter, or start the game and press Refresh.";

    int HotFromCursor()
    {
        var p = _list.PointToClient(Cursor.Position);
        return _list.ClientRectangle.Contains(p) ? _list.IndexFromPoint(p) : -1;
    }

    void SetHot(int index)
    {
        if (index == _hot) return;
        var old = _hot;
        _hot = index;
        if (old >= 0 && old < _list.Items.Count) _list.Invalidate(_list.GetItemRectangle(old));
        if (index >= 0 && index < _list.Items.Count) _list.Invalidate(_list.GetItemRectangle(index));
    }

    const int IconSize = 20;

    // The icon sits at the row's left, centred in the 44 px row; the text starts 12 px after it.
    internal Rectangle IconRect(Rectangle row)
    {
        var s = Px(IconSize);
        return new Rectangle(row.X + Px(Theme.S3), row.Y + (row.Height - s) / 2, s, s);
    }

    internal int TextX(Rectangle row) => IconRect(row).Right + Px(Theme.S3);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _icons.Dispose();
        base.OnFormClosed(e);
    }

    void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count || _list.Items[e.Index] is not Entry entry) return;
        var g = e.Graphics;
        var b = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var raised = selected || e.Index == _hot;
        using (var brush = new SolidBrush(selected ? Theme.Highlight : raised ? Theme.Raised : Theme.SurfaceAlt)) g.FillRectangle(brush, b);
        if (selected)
            using (var brush = new SolidBrush(Theme.Accent)) g.FillRectangle(brush, b.X, b.Y, Px(2), b.Height);

        _icons.Draw(g, IconRect(b), _icons.ForExe(entry.ExePath), entry.Title, Px(4));
        var x = TextX(b);
        var w = b.Right - Px(Theme.S2) - x;
        var titleH = TextRenderer.MeasureText(g, "Ag", Theme.Body, Size.Empty, TextFormatFlags.NoPadding).Height;
        var exeH = TextRenderer.MeasureText(g, "Ag", Theme.Caption, Size.Empty, TextFormatFlags.NoPadding).Height;
        var gap = Px(2);
        var y = b.Y + (b.Height - titleH - gap - exeH) / 2;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, entry.Title, Theme.Body, new Rectangle(x, y, w, titleH), Theme.Text, flags);
        TextRenderer.DrawText(g, entry.ExeName, Theme.Caption, new Rectangle(x, y + titleH + gap, w, exeH), raised ? Theme.TextMuted : Theme.TextSubtle, flags);
    }

    void Accept()
    {
        if (_list.SelectedItem is not Entry e) return;
        _track((e.Title, e.ExePath));
        Close();
    }
}

// The picker's list: the system list box with its rows painted off screen first, so scrolling
// never shows a half-drawn row.
sealed class PickerList : ListBox
{
    const int WheelDelta = 120;
    int _wheel;

    public PickerList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    // Rows per notch: the user's wheel setting (3 by default), at most one page so no row is
    // skipped unseen; "one screen" (-1) is a page minus one row, keeping a row of context.
    internal static int Rows(int lines, int page) => lines < 0 ? Math.Max(1, page - 1) : Math.Min(lines, page);

    // The system list box moves fewer rows than the setting says, so this one follows it, one
    // notch per 120 of delta, so a smooth wheel's small steps add up instead of being dropped.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        _wheel += e.Delta;
        var notches = _wheel / WheelDelta;
        if (notches == 0 || ItemHeight <= 0) return;
        _wheel -= notches * WheelDelta;
        var page = Math.Max(1, ClientSize.Height / ItemHeight);
        var lines = SystemInformation.MouseWheelScrollLines;
        var rows = Rows(lines, page);
        TopIndex = Math.Clamp(TopIndex - notches * rows, 0, Math.Max(0, Items.Count - page));
    }
}

sealed class GameList : ListView
{
    // Room right of a sortable header's text for its triangle: 6 before it, 8 wide.
    public const int SortMarkWidth = 14;

    const int LVM_GETHEADER = 0x101F;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    public GameList()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        AccessibleName = "Tracked games";
    }

    IntPtr Header => IsHandleCreated ? SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero;

    // Raised after every repaint, which follows anything that moves the rows (scroll, wheel, keys,
    // resize), so the themed scrollbar can follow.
    public event Action? Scrolled;

    const int WM_PAINT = 0x000F;
    const int WM_CONTEXTMENU = 0x007B;

    // Right-click on the column headers (screen point).
    public event Action<Point>? HeaderMenu;

    // Right-click on a row (screen point), or null from the keyboard (Shift+F10, the Menu key).
    public event Action<Point?>? RowMenu;

    // The header passes its WM_CONTEXTMENU up to the list with its own handle in wParam; the
    // keyboard sends one with lParam -1.
    protected override void WndProc(ref Message m)
    {
        // A list that is getting narrower gets its columns narrower first, so they are never wider
        // than the list and the native horizontal scrollbar never shows up.
        if (m.Msg == WM_WINDOWPOSCHANGING && _specs is not null && m.LParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((pos.flags & SWP_NOSIZE) == 0 && pos.cx < Width) FitColumns(Math.Max(1, ClientSize.Width - (Width - pos.cx)));
        }
        if (m.Msg == WM_CONTEXTMENU)
        {
            var lp = m.LParam.ToInt64();
            Point? at = (int)lp == -1 ? null : new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            var header = Header;
            if (at is { } p && header != IntPtr.Zero && (m.WParam == header || (GetWindowRect(header, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)))
                HeaderMenu?.Invoke(p);
            else RowMenu?.Invoke(at);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT) return;
        // Whatever changed the widths or the client area without telling the list is put right here.
        if (_specs is not null && !_fitting && IsHandleCreated && Columns.Count > 0 && ColumnsWidth() != ClientSize.Width) FitColumns();
        Scrolled?.Invoke();
    }

    const int HDM_GETITEMRECT = 0x1200 + 7;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

    // A header item's rectangle in the list's client coordinates.
    public Rectangle HeaderItemBounds(int column)
    {
        var h = Header;
        var r = new RECT();
        if (h == IntPtr.Zero || SendMessage(h, HDM_GETITEMRECT, (IntPtr)column, ref r) == IntPtr.Zero) return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public int HeaderBottom => HeaderHeight();

    const int GWL_STYLE = -16;
    const int HDS_NOSIZING = 0x0800;
    const int LVM_GETCOLUMNWIDTH = 0x1000 + 29;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkControl(Handle);
        var h = Header;
        if (h == IntPtr.Zero) return;
        Theme.DarkControl(h, "DarkMode_ItemsView");
        // The widths are worked out from the window, so the dividers can't be dragged.
        SetWindowLong(h, GWL_STYLE, GetWindowLong(h, GWL_STYLE) | HDS_NOSIZING);
        FitColumns();
    }

    // Column widths

    Func<IReadOnlyList<ColumnSpec>>? _specs;
    bool _fitting;

    // The list keeps its columns at the widths ColumnLayout.Fit gives for these specs, so they always add up to
    // the client width: after a resize, when the scrollbar comes or goes, and whenever anything else moved them.
    public void SetColumnSpecs(Func<IReadOnlyList<ColumnSpec>> specs)
    {
        _specs = specs;
        FitColumns();
    }

    int ColumnsWidth()
    {
        var sum = 0;
        for (var i = 0; i < Columns.Count; i++) sum += (int)SendMessage(Handle, LVM_GETCOLUMNWIDTH, (IntPtr)i, IntPtr.Zero);
        return sum;
    }

    const int SB_HORZ = 0;
    const int WS_HSCROLL = 0x00100000;
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const int SWP_NOSIZE = 0x0001;

    [DllImport("user32.dll")]
    static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    public void FitColumns() => FitColumns(null);

    // width: the client width to fit, when the list is about to get it (it is being made narrower).
    void FitColumns(int? width)
    {
        if (_specs is null || _fitting || !IsHandleCreated || IsDisposed) return;
        var client = width ?? ClientSize.Width;
        if (client <= 0) return;
        var specs = _specs();
        if (specs.Count != Columns.Count) return;
        var widths = ColumnLayout.Fit(client, specs);
        _fitting = true;
        try
        {
            var changed = false;
            for (var i = 0; i < widths.Length; i++)
            {
                if (Columns[i].Width == widths[i]) continue;
                Columns[i].Width = widths[i];
                changed = true;
            }
            if (changed)
            {
                Invalidate();
                RepaintHeader();
            }
        }
        finally { _fitting = false; }
        if (width is not null) return;
        // Columns that were too wide for a moment (a narrower list, a new scrollbar) can leave the
        // native horizontal scrollbar behind although they fit again.
        if ((GetWindowLong(Handle, GWL_STYLE) & WS_HSCROLL) != 0 && ColumnsWidth() <= ClientSize.Width) ShowScrollBar(Handle, SB_HORZ, false);
        // The vertical scrollbar came or went meanwhile: fit what is left.
        if (ClientSize.Width != client) FitColumns(null);
    }

    // The header and every row are redrawn in full after a resize: a row is as wide as the list,
    // so nothing of the old width may stay on screen.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitColumns();
        Invalidate();
        RepaintHeader();
    }

    // The client area also changes without a resize, when the vertical scrollbar comes or goes.
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitColumns();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        FitColumns();
        Invalidate();
        RepaintHeader();
    }

    // Only the list sets a column's width.
    protected override void OnColumnWidthChanging(ColumnWidthChangingEventArgs e)
    {
        if (!_fitting && _specs is not null && e.ColumnIndex >= 0 && e.ColumnIndex < Columns.Count)
        {
            e.NewWidth = Columns[e.ColumnIndex].Width;
            e.Cancel = true;
        }
        base.OnColumnWidthChanging(e);
    }

    // The widest text in the cell font or the header in the header font (plus the sort triangle's room), plus
    // the cell's insets: a column this wide never cuts its content.
    public int ContentWidth(string header, IEnumerable<string> texts, int leftInset, int rightInset, int headerExtra)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var widest = header.Length == 0 ? 0 : TextRenderer.MeasureText(header, Theme.Caption, Size.Empty, flags).Width + headerExtra;
        foreach (var text in texts.Distinct())
            widest = Math.Max(widest, TextRenderer.MeasureText(text, Theme.Body, Size.Empty, flags).Width);
        return widest + leftInset + rightInset;
    }

    // The native list draws column lines in the empty area below the last row, so the list is
    // never taller than its rows: the frame around it fills the rest with the same color.
    public int ContentHeight(int rowHeight) =>
        HeaderHeight() + Items.Count * RowHeight(rowHeight);

    // The tallest height up to room that holds the header and whole rows only.
    public int WholeRowsHeight(int room, int rowHeight)
    {
        var header = HeaderHeight();
        var row = RowHeight(rowHeight);
        return room <= header ? room : header + (room - header) / row * row;
    }

    const int LVM_GETITEMRECT = 0x1000 + 14;

    // Read from the native list: a layout raised while the list is being refilled can come before
    // the managed item is ready, and its Bounds then throws.
    int RowHeight(int fallback)
    {
        if (!IsHandleCreated || Items.Count == 0) return fallback;
        var r = new RECT();
        return SendMessage(Handle, LVM_GETITEMRECT, IntPtr.Zero, ref r) != IntPtr.Zero && r.Bottom > r.Top ? r.Bottom - r.Top : fallback;
    }

    int HeaderHeight()
    {
        var h = Header;
        return h != IntPtr.Zero && GetWindowRect(h, out var r) ? r.Bottom - r.Top : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public void RepaintHeader()
    {
        var h = Header;
        if (h != IntPtr.Zero) InvalidateRect(h, IntPtr.Zero, true);
    }
}

// The list's own scrollbar is the system one (a near-black track). This control covers it with one in the app's
// colors, reads the native scroll state and scrolls the list the same way.
sealed class ListScrollBar : Control
{
    const int SB_VERT = 1;
    const int SIF_ALL = 0x17;
    const int WM_VSCROLL = 0x0115;
    const int WM_MOUSEWHEEL = 0x020A;
    const int SB_PAGEUP = 2;
    const int SB_PAGEDOWN = 3;
    const int LVM_SCROLL = 0x1014;
    const int GWL_STYLE = -16;
    const int WS_VSCROLL = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    struct SCROLLINFO
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    [DllImport("user32.dll")]
    static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hWnd, int index);

    readonly GameList _list;
    SCROLLINFO _info;
    bool _hot;
    bool _dragging;
    int _dragOffset;

    public ListScrollBar(GameList list)
    {
        _list = list;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Visible = false;
        BackColor = Theme.SurfaceAlt;
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = "Tracked games scrollbar";
        list.Scrolled += Sync;
        list.SizeChanged += (_, _) => Sync();
        list.LocationChanged += (_, _) => Sync();
        list.VisibleChanged += (_, _) => Sync();
    }

    public void Sync()
    {
        if (!_list.IsHandleCreated || IsDisposed) return;
        var width = _list.Width - _list.ClientSize.Width;
        var show = _list.Visible && width > 0 && (GetWindowLong(_list.Handle, GWL_STYLE) & WS_VSCROLL) != 0;
        if (show != Visible) Visible = show;
        if (!show) return;
        var bounds = new Rectangle(_list.Right - width, _list.Top, width, _list.Height);
        if (Bounds != bounds)
        {
            // Where the bar was is painted again by whatever is there now (the list after it grew,
            // the frame after it shrank), so no copy of the bar stays behind.
            var old = Bounds;
            Bounds = bounds;
            BringToFront();
            Parent?.Invalidate(old, true);
            Invalidate();
        }
        var info = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = SIF_ALL };
        GetScrollInfo(_list.Handle, SB_VERT, ref info);
        if (info.nMin == _info.nMin && info.nMax == _info.nMax && info.nPage == _info.nPage && info.nPos == _info.nPos) return;
        _info = info;
        Invalidate();
    }

    int TrackTop => _list.HeaderBottom + Theme.Px(this, 2);

    int TrackBottom => Height - Theme.Px(this, 2);

    int Range => _info.nMax - _info.nMin + 1;

    int MaxPos => Math.Max(0, Range - (int)_info.nPage);

    internal Rectangle Thumb()
    {
        var track = Math.Max(0, TrackBottom - TrackTop);
        if (Range <= 0 || _info.nPage == 0 || MaxPos == 0) return Rectangle.Empty;
        var length = Math.Min(track, Math.Max(Theme.Px(this, 24), (int)((long)track * _info.nPage / Range)));
        var top = TrackTop + (int)((long)(track - length) * (_info.nPos - _info.nMin) / MaxPos);
        var w = Theme.Px(this, 8);
        return new Rectangle((Width - w) / 2, top, w, length);
    }

    bool OnThumb(int y)
    {
        var t = Thumb();
        return !t.IsEmpty && y >= t.Top && y < t.Bottom;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceAlt);
        var header = _list.HeaderBottom;
        if (header > 0)
        {
            using (var brush = new SolidBrush(Theme.Surface)) g.FillRectangle(brush, 0, 0, Width, header);
            using (var brush = new SolidBrush(Theme.Border)) g.FillRectangle(brush, 0, header - 1, Width, 1);
        }
        var thumb = Thumb();
        if (thumb.IsEmpty) return;
        Theme.Crisp(g);
        using var path = Theme.Round(thumb, thumb.Width / 2f);
        using var fill = new SolidBrush(_dragging || _hot ? Theme.TextMuted : Theme.TextSubtle);
        g.FillPath(fill, path);
    }

    void ScrollTo(int pos)
    {
        pos = Math.Clamp(pos, _info.nMin, _info.nMin + MaxPos);
        var rows = pos - _info.nPos;
        if (rows == 0 || _list.Items.Count == 0) return;
        SendMessage(_list.Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)(rows * _list.Items[0].Bounds.Height));
        Sync();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || Thumb().IsEmpty || e.Y < TrackTop) return;
        if (OnThumb(e.Y))
        {
            _dragging = true;
            _dragOffset = e.Y - Thumb().Top;
            Invalidate();
        }
        else
        {
            SendMessage(_list.Handle, WM_VSCROLL, (IntPtr)(e.Y < Thumb().Top ? SB_PAGEUP : SB_PAGEDOWN), IntPtr.Zero);
            Sync();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = OnThumb(e.Y);
        if (hot != _hot) { _hot = hot; Invalidate(); }
        if (!_dragging) return;
        var free = TrackBottom - TrackTop - Thumb().Height;
        if (free <= 0) return;
        ScrollTo(_info.nMin + (int)Math.Round((e.Y - _dragOffset - TrackTop) * (double)MaxPos / free));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot) { _hot = false; Invalidate(); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL && _list.IsHandleCreated)
        {
            SendMessage(_list.Handle, m.Msg, m.WParam, m.LParam);
            return;
        }
        base.WndProc(ref m);
    }
}

sealed class TrackedGamesForm : ThemedWindow
{
    const string NotMatched = "Not matched";
    internal const string TreatAsEmulator = "Treat as emulator";
    internal const string TreatAsOneGame = "Treat as one game";
    internal const string ShowEmulatorsText = "Show emulators";

    // Fixed widths at 96 DPI; Name and Source share what is left.
    const int IconWidth = 32;
    const int IconSize = 20;

    readonly AppState _state;
    readonly Action _save;
    readonly Action<GameRecord> _findMatch;
    readonly Action<GameRecord> _openLog;
    readonly GameList _list = new();
    readonly ListScrollBar _scroll;
    readonly ActionBar _bar;
    readonly FramePanel _frame;
    readonly EmptyState _empty = new(Theme.SurfaceAlt, "Track a running game...") { Dock = DockStyle.Fill, Visible = false };
    readonly ThemedCheck _showEmulators = new(ShowEmulatorsText);
    readonly List<ThemedButton> _actions = new();

    // Actions that make sense only for a game: an emulator row has no Backloggd page or status.
    readonly List<ThemedButton> _gameActions = new();
    readonly ThemedButton _emulatorToggle;
    readonly ImageList _rows;
    readonly GameIcons _icons = new();
    readonly ToolTip _tip = Theme.DarkToolTip();
    readonly ThemedMenu _headerMenu = new();
    readonly ThemedMenu _rowMenu = new();
    readonly List<TrackedColumn> _columns = new();
    int _hot = -1;
    string? _tipText;

    public TrackedGamesForm(AppState state, Action save, Action trackRunningGame, Action<GameRecord> findMatch, Action<GameRecord> openLog)
    {
        _state = state;
        _findMatch = findMatch;
        _openLog = openLog;
        _save = save;
        Text = "Tracked games";
        ClientSize = new Size(920, 520);
        // The smallest window still shows four rows above an action row that has wrapped to two.
        MinimumSize = SizeFromClientSize(new Size(640, 400));

        var root = Stack();
        root.ColumnCount = 2;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // The heading and Show emulators share one row, both centred on it, with the same margin
        // under them.
        var heading = Heading("Tracked games", Theme.S3);
        heading.Anchor = AnchorStyles.Left;
        AddRow(root, heading);
        _showEmulators.Anchor = AnchorStyles.Right;
        _showEmulators.Margin = new Padding(Theme.S3, 0, 0, Theme.S3);
        _showEmulators.Checked = View.ShowEmulators;
        root.Controls.Add(_showEmulators, 1, 0);

        // The list adds one pixel under the image, so 31 gives the 32 px row.
        _rows = new ImageList { ImageSize = new Size(1, Px(32) - 1) };
        _list.SmallImageList = _rows;
        BuildColumns();

        _frame = new FramePanel(_list) { Dock = DockStyle.Fill, Margin = Inset() };
        _list.Dock = DockStyle.Top;
        _scroll = new ListScrollBar(_list);
        _frame.Controls.Add(_scroll);
        _frame.Controls.Add(_list);
        _frame.Controls.Add(_empty);
        _frame.Layout += (_, _) => FitList();
        AddRow(root, _frame, fill: true);
        root.SetColumnSpan(_frame, 2);

        var left = new FlowLayoutPanel
        {
            WrapContents = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        left.Controls.Add(GameAction(ActionButton("Open log", ButtonKind.Secondary, OpenLog)));
        left.Controls.Add(ActionButton("Rename...", ButtonKind.Secondary, Rename));
        left.Controls.Add(GameAction(ActionButton("Find on Backloggd...", ButtonKind.Secondary, FindOnBackloggd)));
        left.Controls.Add(GameAction(ActionButton("Reset status", ButtonKind.Secondary, ResetStatus)));
        left.Controls.Add(GameAction(ActionButton("Never ask again", ButtonKind.Secondary, NeverAsk)));
        _emulatorToggle = ActionButton(TreatAsEmulator, ButtonKind.Secondary, ToggleEmulator, TreatAsOneGame);
        left.Controls.Add(_emulatorToggle);
        foreach (Control c in left.Controls) c.Margin = new Padding(0, 0, Theme.S2 - Theme.FocusPad * 2, 0);
        var remove = ActionButton("Remove", ButtonKind.Danger, Remove);

        _bar = new ActionBar(left, remove) { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S3 - Theme.FocusPad, 0, 0) };
        AddRow(root, _bar);
        root.SetColumnSpan(_bar, 2);
        Controls.Add(root);
        ActiveControl = _list;
        // Reading order: the header's check, then the list, then the actions.
        _showEmulators.TabIndex = 0;
        _frame.TabIndex = 1;
        _bar.TabIndex = 2;

        BuildHeaderMenu();
        BuildRowMenu();

        _showEmulators.CheckedChanged += (_, _) => SetShowEmulators(_showEmulators.Checked);
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += DrawCell;
        _list.ColumnClick += OnColumnClick;
        _list.SelectedIndexChanged += (_, _) => UpdateActions();
        _list.MouseMove += OnListMouseMove;
        _list.MouseWheel += (_, _) => BeginInvoke(() =>
        {
            var p = _list.PointToClient(Cursor.Position);
            SetHot(_list.ClientRectangle.Contains(p) ? _list.HitTest(p).Item?.Index ?? -1 : -1);
        });
        _list.MouseLeave += (_, _) => { SetHot(-1); SetTip(null); };
        _list.DoubleClick += (_, _) => Rename();
        _list.SetColumnSpecs(ColumnSpecs);
        _list.DpiChangedAfterParent += (_, _) => MeasureColumns();
        _list.HeaderMenu += p => _headerMenu.Show(p);
        _list.RowMenu += ShowRowMenu;
        _empty.Action!.Click += (_, _) => trackRunningGame();

        Refill();
        EndBuild();
    }

    TrackedListView View => _state.Settings.TrackedList;

    void SetView(TrackedListView view)
    {
        if (view == View) return;
        _state.Settings = _state.Settings with { TrackedList = view };
        _save();
    }

    internal static string Title(TrackedColumn c) => c switch
    {
        TrackedColumn.Icon => "Icon",
        TrackedColumn.Name => "Name",
        TrackedColumn.Status => "Status",
        TrackedColumn.TimePlayed => "Time played",
        TrackedColumn.Added => "Added",
        TrackedColumn.Backloggd => "Backloggd",
        _ => "Source",
    };

    // The icon column's header is empty: the picture needs no label.
    static string HeaderText(TrackedColumn c) => c == TrackedColumn.Icon ? "" : Title(c);

    // The list's column index of c, or -1 while it is hidden.
    internal int ColumnIndex(TrackedColumn c) => _columns.IndexOf(c);

    TrackedColumn ColumnAt(int index) => index >= 0 && index < _columns.Count ? _columns[index] : TrackedColumn.Name;

    internal string CellText(ListViewItem item, TrackedColumn c) => ColumnIndex(c) is var i and >= 0 ? item.SubItems[i].Text : "";

    void BuildColumns()
    {
        var view = View;
        _columns.Clear();
        _columns.AddRange(Enum.GetValues<TrackedColumn>().Where(view.Shows));
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Columns.Clear();
            foreach (var c in _columns) _list.Columns.Add(new ColumnHeader { Text = HeaderText(c), Tag = c, Width = Px(80) });
        }
        finally { _list.EndUpdate(); }
    }

    ThemedButton ActionButton(string text, ButtonKind kind, Action onClick, params string[] sizeTexts)
    {
        var b = new ThemedButton(text, kind) { Enabled = false, SizeTexts = sizeTexts };
        b.Click += (_, _) => onClick();
        _actions.Add(b);
        return b;
    }

    ThemedButton GameAction(ThemedButton b)
    {
        _gameActions.Add(b);
        return b;
    }

    GameRecord? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as GameRecord : null;

    // Actions on the selected game: the row may move (rename, a status change under a status
    // sort), so it is scrolled into view again.
    void Run(Action<GameRecord> act, bool games = false)
    {
        if (Selected is not { } g || (games && g.IsEmulator)) return;
        act(g);
        // A game set to Never takes its missed popups with it.
        _state.PruneMissed(DateTime.Now);
        _save();
        Refill(reveal: true);
    }

    void Rename() => Run(g =>
    {
        var name = TextPrompt.Ask(this, g.DisplayName);
        if (!string.IsNullOrWhiteSpace(name)) g.DisplayName = name.Trim();
    });

    void FindOnBackloggd()
    {
        if (Selected is { IsEmulator: false } g) _findMatch(g);
    }

    void OpenLog()
    {
        if (Selected is { IsEmulator: false } g) _openLog(g);
    }

    void ResetStatus() => Run(g => { g.Status = GameStatus.Unknown; g.PendingStart = null; g.Asked = false; }, games: true);

    void NeverAsk() => Run(g => Actions.Apply(g, PromptAnswer.Never, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today)), games: true);

    // A game turned into an emulator stays in view: Show emulators turns on with it.
    void ToggleEmulator() => Run(g =>
    {
        if (g.ExePath is null) return;
        EmulatorTitles.SetEmulator(g, !g.IsEmulator);
        if (g.IsEmulator && !View.ShowEmulators)
        {
            SetView(View with { ShowEmulators = true });
            _showEmulators.Checked = true;
        }
    });

    void Remove()
    {
        if (Selected is not { } g) return;
        if (!ConfirmDialog.Ask(this, "Remove game", $"Remove {g.DisplayName}?", "The app forgets this game. Your Backloggd log is not touched.", "Remove")) return;
        _state.Games.Remove(g.Id);
        _state.PruneMissed(DateTime.Now);
        _save();
        Refill(reveal: true);
        ActiveControl = _list;
    }

    void SetShowEmulators(bool on)
    {
        if (_showEmulators.Checked != on) _showEmulators.Checked = on;
        if (View.ShowEmulators == on) return;
        SetView(View with { ShowEmulators = on });
        Refill(reveal: true);
    }

    void SetColumnShown(TrackedColumn c, bool shown)
    {
        SetView(shown ? View.Show(c) : View.Hide(c));
        BuildColumns();
        Refill(reveal: true);
        _list.RepaintHeader();
    }

    void SetSort(TrackedColumn c, bool descending)
    {
        if (!TrackedListView.CanSort(c)) return;
        SetView(View with { SortColumn = c, SortDescending = descending });
        Refill(reveal: true);
        _list.RepaintHeader();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (_list.Focused && (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))) ShowRowMenu(null);
        else if (e.KeyCode == Keys.F2 || (e.KeyCode == Keys.Enter && _list.Focused)) Rename();
        else if (e.KeyCode == Keys.Delete && _list.Focused) Remove();
        else return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    // Activation picks up changes made while the window was in the background; the first one comes
    // right after the constructor filled the list, so it has nothing to pick up.
    bool _activated;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_activated) Refill();
        _activated = true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tip.Dispose();
        _rows.Dispose();
        _icons.Dispose();
        _headerMenu.Dispose();
        _rowMenu.Dispose();
        base.OnFormClosed(e);
    }

    // Menus

    internal ThemedMenu HeaderMenu => _headerMenu;

    internal ThemedMenu RowMenu => _rowMenu;

    void BuildHeaderMenu()
    {
        foreach (var c in Enum.GetValues<TrackedColumn>())
        {
            var item = new ToolStripMenuItem(Title(c)) { Tag = c, Enabled = c != TrackedColumn.Name };
            item.Click += (_, _) => SetColumnShown(c, !View.Shows(c));
            _headerMenu.Items.Add(item);
        }
        _headerMenu.Items.Add(new ToolStripSeparator());
        var sortBy = ThemedMenu.Submenu("Sort by");
        foreach (var c in Enum.GetValues<TrackedColumn>().Where(TrackedListView.CanSort))
        {
            var item = new ToolStripMenuItem(Title(c)) { Tag = c };
            item.Click += (_, _) => SetSort(c, View.SortDescending);
            sortBy.DropDownItems.Add(item);
        }
        sortBy.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (text, descending) in new[] { ("Ascending", false), ("Descending", true) })
        {
            var item = new ToolStripMenuItem(text) { Tag = descending };
            item.Click += (_, _) => SetSort(View.SortColumn, descending);
            sortBy.DropDownItems.Add(item);
        }
        _headerMenu.Items.Add(sortBy);
        _headerMenu.Items.Add(new ToolStripSeparator());
        var emulators = new ToolStripMenuItem(ShowEmulatorsText);
        emulators.Click += (_, _) => SetShowEmulators(!View.ShowEmulators);
        _headerMenu.Items.Add(emulators);
        _headerMenu.FitItemHeights();
        _headerMenu.Opening += (_, _) => SyncHeaderMenu();
        SyncHeaderMenu();
    }

    void SyncHeaderMenu()
    {
        var view = View;
        foreach (ToolStripItem item in _headerMenu.Items)
        {
            if (item is not ToolStripMenuItem m) continue;
            if (m.Tag is TrackedColumn c) m.Checked = view.Shows(c);
            else if (m.Text == ShowEmulatorsText) m.Checked = view.ShowEmulators;
            else if (m.HasDropDownItems)
                foreach (var sub in m.DropDownItems.OfType<ToolStripMenuItem>())
                    sub.Checked = sub.Tag is TrackedColumn sc ? sc == view.SortColumn : sub.Tag is bool d && d == view.SortDescending;
        }
    }

    void BuildRowMenu()
    {
        void Add(ToolStripMenuItem item, Action act)
        {
            item.Click += (_, _) => act();
            _rowMenu.Items.Add(item);
        }
        Add(new ToolStripMenuItem("Open log"), OpenLog);
        Add(new ToolStripMenuItem("Find on Backloggd..."), FindOnBackloggd);
        Add(new ToolStripMenuItem("Rename..."), Rename);
        Add(new ToolStripMenuItem("Reset status"), ResetStatus);
        Add(new ToolStripMenuItem("Never ask again"), NeverAsk);
        Add(new ToolStripMenuItem(TreatAsEmulator), ToggleEmulator);
        _rowMenu.Items.Add(new ToolStripSeparator());
        Add(new DangerMenuItem("Remove"), Remove);
        _rowMenu.FitItemHeights();
        _rowMenu.Opening += (_, e) =>
        {
            SyncRowMenu();
            if (Selected is null) e.Cancel = true;
        };
    }

    // The same rules as the buttons.
    void SyncRowMenu()
    {
        var g = Selected;
        var game = g is { IsEmulator: false };
        foreach (var item in _rowMenu.Items.OfType<ToolStripMenuItem>())
        {
            item.Enabled = item.Text switch
            {
                "Open log" or "Find on Backloggd..." or "Reset status" or "Never ask again" => game,
                TreatAsEmulator or TreatAsOneGame => g?.ExePath is not null,
                _ => g is not null,
            };
            if (item.Text is TreatAsEmulator or TreatAsOneGame) item.Text = g is { IsEmulator: true } ? TreatAsOneGame : TreatAsEmulator;
        }
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
        var name = ColumnIndex(TrackedColumn.Name);
        var cell = TextRect(CellRect(row, name), name);
        _rowMenu.Show(_list, new Point(cell.X, row.Bounds.Bottom));
    }

    // Rows

    internal static string StatusLabel(GameRecord g)
    {
        if (g.IsEmulator) return "Emulator";
        if (g.Status == GameStatus.Never) return "Never ask";
        if (g.PendingStart is not null) return "Queued";
        return g.Status switch
        {
            GameStatus.Playing => "Playing",
            GameStatus.Finished => "Finished",
            GameStatus.Dropped => "Dropped",
            _ => g.Asked ? "Ignored" : "Not logged yet",
        };
    }

    static string Source(GameRecord g)
    {
        if (GameIds.IsSteam(g.Id)) return "Steam";
        if (GameIds.Launcher(g.Id) is { } launcher) return launcher;
        if (g.IsEmulator) return $"{Path.GetFileName(g.ExePath)} (emulator)";
        return Path.GetFileName(g.ExePath ?? g.EmulatorExe ?? g.Id);
    }

    static string? Matched(GameRecord g) => g.BackloggdTitle ?? g.Slug;

    static string Cell(GameRecord g, TrackedColumn c) => c switch
    {
        TrackedColumn.Status => StatusLabel(g),
        TrackedColumn.TimePlayed => Format.Minutes(g.TotalMinutes),
        TrackedColumn.Added => Format.Added(g.AddedAt, DateTime.Today),
        TrackedColumn.Backloggd => Matched(g) ?? NotMatched,
        TrackedColumn.Source => Source(g),
        // The icon column also holds the name, so typing a name's first letters finds its row.
        _ => g.DisplayName,
    };

    IEnumerable<GameRecord> Sorted()
    {
        var view = View;
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        var desc = view.SortDescending;
        var all = _state.Games.Values.Where(g => view.ShowEmulators || !g.IsEmulator);
        IOrderedEnumerable<GameRecord> By<T>(Func<GameRecord, T> key, IComparer<T>? c = null) =>
            desc ? all.OrderByDescending(key, c) : all.OrderBy(key, c);
        var sorted = view.SortColumn switch
        {
            TrackedColumn.Status => By(StatusLabel, cmp),
            TrackedColumn.TimePlayed => By(g => g.TotalMinutes),
            TrackedColumn.Added => By(g => g.AddedAt),
            // Unmatched games stay at the end either way.
            TrackedColumn.Backloggd => (desc ? all.OrderBy(g => Matched(g) is null).ThenByDescending(Matched, cmp) : all.OrderBy(g => Matched(g) is null).ThenBy(Matched, cmp)),
            TrackedColumn.Source => By(Source, cmp),
            _ => By(g => g.DisplayName, cmp),
        };
        return sorted.ThenBy(g => g.DisplayName, cmp);
    }

    // reveal scrolls the selected row into view; refills from outside (a session ended, the window
    // was activated) keep the scroll position the user chose.
    public void Refill(bool reveal = false)
    {
        var selectedId = Selected?.Id;
        var top = _list.TopItem?.Index ?? 0;
        if (_showEmulators.Checked != View.ShowEmulators) _showEmulators.Checked = View.ShowEmulators;
        var rows = Sorted().Select(g => new ListViewItem(_columns.Select(c => Cell(g, c)).ToArray()) { Tag = g }).ToArray();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Items.AddRange(rows);
            if (rows.FirstOrDefault(i => ((GameRecord)i.Tag!).Id == selectedId) is { } item) { item.Selected = true; item.Focused = true; }
        }
        finally { _list.EndUpdate(); }
        // After EndUpdate: inside an update the list can't scroll to a row near the end.
        if (_list.Items.Count > 0) _list.TopItem = _list.Items[Math.Min(top, _list.Items.Count - 1)];
        if (reveal && _list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();
        _hot = -1;
        var empty = _list.Items.Count == 0;
        _empty.Message = _state.Games.Count == 0
            ? "No games yet. Play a Steam game, or use Track a running game for others."
            : "Only emulators are tracked. Turn on Show emulators to see them.";
        _empty.Visible = empty;
        if (_empty.Action is { } track) track.Visible = _state.Games.Count == 0;
        _list.Visible = !empty;
        _bar.Visible = !empty;
        // With no games at all there is nothing to show or hide.
        _showEmulators.Visible = _state.Games.Count > 0;
        FitList();
        MeasureColumns();
        UpdateActions();
        _scroll.Sync();
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

    // A moved or restored window is painted again in full: the frame, the header, the rows and the
    // scrollbar, so nothing drawn for the old place or size stays.
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

    void UpdateActions()
    {
        var g = Selected;
        var any = g is not null;
        // Read before the buttons change: in an active window, disabling the focused button makes
        // Windows move the focus on to the next control before this method could send it to the list.
        var focused = ActiveControl as ThemedButton;
        foreach (var b in _actions) b.Enabled = any;
        foreach (var b in _gameActions) b.Enabled = g is { IsEmulator: false };
        _emulatorToggle.Enabled = g?.ExePath is not null;
        var toggle = g is { IsEmulator: true } ? TreatAsOneGame : TreatAsEmulator;
        if (_emulatorToggle.Text != toggle) _emulatorToggle.Text = _emulatorToggle.AccessibleName = toggle;
        if (focused is { Enabled: false } || ActiveControl is ThemedButton { Enabled: false }) ActiveControl = _list;
    }

    // Widths at 96 DPI. Status, Time played and Added are as wide as their widest text. Name, Backloggd and Source
    // share the rest by weight; in a window too narrow, Source gives up width first, then Backloggd, then Name.
    static ColumnSpec Flexible(TrackedColumn c, Func<int, int> px) => c switch
    {
        TrackedColumn.Name => ColumnSpec.Flex(5, px(160), px(72), 3),
        TrackedColumn.Backloggd => ColumnSpec.Flex(3, px(120), px(48), 2),
        _ => ColumnSpec.Flex(3, px(100), px(48), 1),
    };

    static bool SizedToContent(TrackedColumn c) => c is TrackedColumn.Status or TrackedColumn.TimePlayed or TrackedColumn.Added;

    readonly Dictionary<TrackedColumn, int> _contentWidths = new();

    // Measured again whenever the rows, the columns shown or the DPI change.
    void MeasureColumns()
    {
        _contentWidths.Clear();
        for (var i = 0; i < _columns.Count; i++)
        {
            var c = _columns[i];
            if (!SizedToContent(c)) continue;
            var col = i;
            var texts = _list.Items.Cast<ListViewItem>().Select(item => item.SubItems[col].Text);
            _contentWidths[c] = _list.ContentWidth(HeaderText(c), texts, TextInset(i), Px(Theme.S2), Px(GameList.SortMarkWidth));
        }
        _list.FitColumns();
    }

    IReadOnlyList<ColumnSpec> ColumnSpecs() => _columns.Select(c =>
        c == TrackedColumn.Icon ? ColumnSpec.Fixed(Px(IconWidth))
        : SizedToContent(c) ? ColumnSpec.Fixed(_contentWidths.GetValueOrDefault(c, Px(80)))
        : Flexible(c, Px)).ToList();

    void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var c = ColumnAt(e.Column);
        if (!TrackedListView.CanSort(c)) return;
        var view = View;
        SetSort(c, c == view.SortColumn ? !view.SortDescending : c is TrackedColumn.TimePlayed or TrackedColumn.Added);
    }

    void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        SetHot(hit.Item?.Index ?? -1);
        var col = hit.Item is not null && hit.SubItem is not null ? hit.Item.SubItems.IndexOf(hit.SubItem) : -1;
        SetTip(hit.Item is { Tag: GameRecord g } item ? TipFor(item, g, col) : null);
    }

    // Source shows the exe path, Backloggd the game's address, Time played what the app added to the journal; a
    // cell cut short shows its full text.
    internal string? TipFor(ListViewItem item, GameRecord g, int col)
    {
        if (col < 0 || col >= item.SubItems.Count || ColumnAt(col) == TrackedColumn.Icon) return null;
        var text = item.SubItems[col].Text;
        var full = Truncated(item, col) ? text : null;
        var extra = ColumnAt(col) == TrackedColumn.Source ? g.ExePath ?? g.EmulatorExe ?? g.LauncherExe
            : ColumnAt(col) == TrackedColumn.Backloggd && g.Slug is { } slug ? $"backloggd.com/games/{slug}/"
            : ColumnAt(col) == TrackedColumn.TimePlayed && g.SentJournalMinutes > 0 ? SentTip(g.SentJournalMinutes)
            : null;
        if (full is not null && extra is not null && extra != full) return full + Environment.NewLine + extra;
        return full ?? extra;
    }

    internal string? TipFor(ListViewItem item, GameRecord g, TrackedColumn c) => TipFor(item, g, ColumnIndex(c));

    internal static string SentTip(int minutes) => $"Sent to Backloggd: {Format.Minutes(minutes)}";

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
        if (ColumnAt(col) == TrackedColumn.Icon) return false;
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
        var text = e.Header?.Text ?? "";
        var rect = new Rectangle(b.X + left, b.Y, Math.Max(0, b.Width - left - Px(Theme.S2)), b.Height);
        TextRenderer.DrawText(g, text, Theme.Caption, rect, Theme.TextSubtle, CellFlags);
        if (ColumnAt(e.ColumnIndex) != View.SortColumn || text.Length == 0) return;
        var tw = TextRenderer.MeasureText(g, text, Theme.Caption, Size.Empty, TextFormatFlags.NoPadding).Width;
        var x = rect.X + tw + Px(6);
        var cy = b.Y + b.Height / 2f;
        float hw = Px(4), hh = Px(2);
        var pts = View.SortDescending
            ? new[] { new PointF(x, cy - hh), new PointF(x + hw * 2, cy - hh), new PointF(x + hw, cy + hh) }
            : new[] { new PointF(x, cy + hh), new PointF(x + hw * 2, cy + hh), new PointF(x + hw, cy - hh) };
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var tri = new SolidBrush(Theme.TextSubtle);
        g.FillPolygon(tri, pts);
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

        var game = e.Item.Tag as GameRecord;
        var column = ColumnAt(e.ColumnIndex);
        if (column == TrackedColumn.Icon)
        {
            DrawIcon(g, cell, game);
            return;
        }
        var color = column switch
        {
            TrackedColumn.Name or TrackedColumn.Status => Theme.Text,
            TrackedColumn.TimePlayed => game?.TotalMinutes > 0 ? Theme.TextMuted : Theme.TextSubtle,
            TrackedColumn.Added => Theme.TextMuted,
            TrackedColumn.Backloggd => game?.Slug is null ? Theme.TextSubtle : Theme.TextMuted,
            _ => Theme.TextSubtle,
        };
        if (raised && color == Theme.TextSubtle) color = Theme.TextMuted;
        TextRenderer.DrawText(g, e.SubItem.Text, Theme.Body, TextRect(cell, e.ColumnIndex), color, CellFlags);
    }

    // The 20 px icon sits where the first column's text would start, centred in the 32 px row.
    internal Rectangle IconRect(Rectangle cell)
    {
        var s = Px(IconSize);
        return new Rectangle(cell.X + TextInset(0), cell.Y + (cell.Height - s) / 2, s, s);
    }

    void DrawIcon(Graphics g, Rectangle cell, GameRecord? game) =>
        _icons.Draw(g, IconRect(cell), game is null ? null : _icons.For(game), game?.DisplayName, Px(4));
}

// Buttons on the left that wrap only when the window is too narrow for one row, and one button
// pinned to the right. The bar is exactly as tall as the rows it needs.
sealed class ActionBar : Control
{
    readonly FlowLayoutPanel _left;
    readonly Control _right;

    public ActionBar(FlowLayoutPanel left, Control right)
    {
        _left = left;
        _right = right;
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Theme.Surface;
        AutoSize = true;
        left.AutoSize = false;
        left.Dock = DockStyle.None;
        Controls.Add(left);
        Controls.Add(right);
    }

    int Gap => Theme.Px(this, Theme.S2);

    int LeftHeight(int width) =>
        _left.GetPreferredSize(new Size(Math.Max(1, width - _right.PreferredSize.Width - Gap), 0)).Height;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var w = proposedSize.Width is > 1 and < int.MaxValue / 2 ? proposedSize.Width : Width > 0 ? Width : int.MaxValue / 4;
        return new Size(w, Math.Max(_right.PreferredSize.Height, LeftHeight(w)));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var rs = _right.PreferredSize;
        _right.SetBounds(Width - rs.Width, 0, rs.Width, rs.Height);
        _left.SetBounds(0, 0, Math.Max(1, Width - rs.Width - Gap), LeftHeight(Width));
    }
}

sealed class RenameDialog : ThemedWindow
{
    readonly ThemedInput _input = new("Game name");
    readonly TableLayoutPanel _root = Stack();

    public string Value => _input.Box.Text;

    public RenameDialog(string initial)
    {
        Text = "Rename game";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 160);

        AddRow(_root, Heading("Rename game"));
        AddRow(_root, Line("Shown in popups and used to search Backloggd.", Theme.TextMuted, Theme.S3));
        _input.Dock = DockStyle.Fill;
        _input.Margin = Inset();
        _input.Box.Text = initial;
        AddRow(_root, _input);
        var save = new ThemedButton("Save", ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new ThemedButton("Cancel", ButtonKind.Secondary) { DialogResult = DialogResult.Cancel };
        AddRow(_root, Footer(save, cancel));
        Controls.Add(_root);
        AcceptButton = save;
        CancelButton = cancel;
        ActiveControl = _input.Box;
        save.Enabled = initial.Trim().Length > 0;
        _input.Box.TextChanged += (_, _) => save.Enabled = _input.Box.Text.Trim().Length > 0;
        EndBuild();
    }

    protected override void OnLoad(EventArgs e)
    {
        foreach (var l in _root.Controls.OfType<Label>()) l.MaximumSize = new Size(ClientSize.Width - _root.Padding.Horizontal - l.Margin.Horizontal, 0);
        ClientSize = new Size(ClientSize.Width, _root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _input.Box.Select(_input.Box.TextLength, 0);
    }
}

static class TextPrompt
{
    public static string? Ask(IWin32Window owner, string initial)
    {
        using var form = new RenameDialog(initial);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Value : null;
    }
}

sealed class ConfirmDialog : ThemedWindow
{
    readonly TableLayoutPanel _root = Stack();
    readonly Label _heading;
    readonly Label _message;

    ConfirmDialog(string title, string heading, string message, string confirm)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 160);

        _heading = Heading(heading);
        AddRow(_root, _heading);
        _message = Line(message, Theme.TextMuted, Theme.S2);
        AddRow(_root, _message);
        var ok = new ThemedButton(confirm, ButtonKind.Danger) { DialogResult = DialogResult.OK };
        var cancel = new ThemedButton("Cancel", ButtonKind.Secondary) { DialogResult = DialogResult.Cancel, AlwaysShowFocusWhenFocused = true };
        AddRow(_root, Footer(ok, cancel));
        Controls.Add(_root);
        AcceptButton = cancel;
        CancelButton = cancel;
        ActiveControl = cancel;
        EndBuild();
    }

    protected override void OnLoad(EventArgs e)
    {
        foreach (var l in new[] { _heading, _message }) l.MaximumSize = new Size(ClientSize.Width - _root.Padding.Horizontal - l.Margin.Horizontal, 0);
        ClientSize = new Size(ClientSize.Width, _root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
        base.OnLoad(e);
    }

    public static bool Ask(IWin32Window owner, string title, string heading, string message, string confirm)
    {
        using var form = new ConfirmDialog(title, heading, message, confirm);
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}

static class StartupEntry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "BackloggdLogger";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (path is null) throw new InvalidOperationException("process path unavailable");
                key.SetValue(Name, $"\"{path}\"");
            }
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("errors", "startup entry: " + ex);
            return false;
        }
    }
}
