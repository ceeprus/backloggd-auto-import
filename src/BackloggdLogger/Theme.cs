using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BackloggdLogger;

enum ButtonKind { Primary, Secondary, Danger, Ghost }

static class Theme
{
    public static readonly Color Surface = Hex("#16181c");
    public static readonly Color SurfaceAlt = Hex("#242832");
    public static readonly Color Raised = Hex("#30394c");
    public static readonly Color Highlight = Hex("#3f4b64");
    public static readonly Color Border = Hex("#30394c");
    public static readonly Color BorderStrong = Hex("#5f6e8a");
    public static readonly Color Text = Hex("#FFFFFF");
    public static readonly Color TextMuted = Hex("#c5c9d1");
    public static readonly Color TextSubtle = Hex("#9aa4b2");
    public static readonly Color Accent = Hex("#D63370");
    public static readonly Color AccentHover = Hex("#CF2F6B");
    public static readonly Color AccentPressed = Hex("#BF2860");
    public static readonly Color Focus = Hex("#58A6FF");
    public static readonly Color Danger = Hex("#FF7B72");

    public static readonly Color StatusPlayed = Hex("#ea377a");
    public static readonly Color StatusCompleted = Hex("#43b94f");
    public static readonly Color StatusRetired = Hex("#4b7bd4");
    public static readonly Color StatusShelved = Hex("#e69b3e");
    public static readonly Color StatusAbandoned = Hex("#ea4747");

    public static readonly Font Title = new("Segoe UI Semibold", 11f);
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Caption = new("Segoe UI", 8.25f);
    public static readonly Font ButtonFont = new("Segoe UI", 9.5f);

    public const int ControlRadius = 6;
    public const int WindowRadius = 8;
    public const int S1 = 4;
    public const int S2 = 8;
    public const int S3 = 12;
    public const int S4 = 16;
    public const int S5 = 24;
    public const int ButtonHeight = 30;
    public const int ButtonPadding = 12;
    public const int PopupButtonPadding = 8;
    public const int FocusPad = 3;

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    static Color Hex(string h) => ColorTranslator.FromHtml(h);

    public static int Px(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96f);

    public static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    public static bool Dwm(IntPtr hwnd, int attr, int value)
    {
        try { return DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)) == 0; }
        catch (Exception) { return false; }
    }

    public static bool RoundCorners(IntPtr hwnd, int preference) => Dwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, preference);

    public static void BorderColor(IntPtr hwnd, Color c) => Dwm(hwnd, DWMWA_BORDER_COLOR, ColorRef(c));

    public static void DarkTitleBar(IntPtr hwnd)
    {
        Dwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Dwm(hwnd, DWMWA_CAPTION_COLOR, ColorRef(Surface));
    }

    public static void DarkControl(IntPtr hwnd, string theme = "DarkMode_Explorer")
    {
        try { SetWindowTheme(hwnd, theme, null); }
        catch (Exception) { }
    }

    // Antialiased shapes with pixel edges on whole pixels: fills of integer rects are sharp, and a
    // 1px stroke is crisp on a rect inset by 0.5.
    public static void Crisp(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Size ButtonSize(string text, Font font, float scale, int pad, int sidePadding = ButtonPadding)
    {
        var t = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(t.Width + (int)Math.Round(sidePadding * 2 * scale) + pad * 2, (int)Math.Round(ButtonHeight * scale) + pad * 2);
    }

    public static void PaintButton(Graphics g, Rectangle client, Color parentBack, ButtonKind kind, string text, Font font,
        bool enabled, bool hot, bool pressed, bool focusRing, int pad, float radius)
    {
        g.Clear(parentBack);
        Crisp(g);
        var body = Rectangle.Inflate(client, -pad, -pad);
        Color fill;
        Color border = Color.Empty;
        Color fore;
        if (!enabled)
        {
            fill = parentBack;
            border = Border;
            fore = TextSubtle;
        }
        else
        {
            switch (kind)
            {
                case ButtonKind.Primary:
                    fill = pressed ? AccentPressed : hot ? AccentHover : Accent;
                    fore = Color.White;
                    break;
                case ButtonKind.Danger:
                    fill = pressed ? SurfaceAlt : Raised;
                    if (hot || pressed) border = Danger;
                    fore = Danger;
                    break;
                case ButtonKind.Ghost:
                    fill = pressed ? SurfaceAlt : hot ? Raised : parentBack;
                    fore = TextMuted;
                    break;
                default:
                    fill = pressed ? SurfaceAlt : hot ? Highlight : Raised;
                    fore = Text;
                    break;
            }
        }
        using (var path = Round(body, radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        if (!border.IsEmpty)
        {
            using var path = Round(new RectangleF(body.X + 0.5f, body.Y + 0.5f, body.Width - 1, body.Height - 1), radius - 0.5f);
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, text, font, body, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (focusRing)
        {
            // A whole-pixel ring filling the outer edge of the focus padding: the pen runs width/2 in from the
            // control's edge, so both edges of the ring land on whole pixels.
            var ring = FocusRingWidth(pad);
            var inset = pad - ring / 2f;
            using var path = Round(new RectangleF(body.X - inset, body.Y - inset, body.Width + inset * 2, body.Height + inset * 2), radius + inset);
            using var pen = new Pen(Focus, ring);
            g.DrawPath(pen, path);
        }
    }

    // The focus ring for a focus padding of pad: 2 px at 96 DPI (pad 3), whole pixels at any scale,
    // and always leaving a gap of at least 1 px to the control.
    public static int FocusRingWidth(int pad) => Math.Clamp((int)Math.Round(pad * 2 / 3f, MidpointRounding.AwayFromZero), 1, Math.Max(1, pad - 1));

    public static ToolTip DarkToolTip()
    {
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 500, ReshowDelay = 100 };
        tip.Popup += (_, e) =>
        {
            var text = tip.GetToolTip(e.AssociatedControl);
            var t = TextRenderer.MeasureText(text, Caption, new Size(480, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            e.ToolTipSize = new Size(t.Width + 16, t.Height + 10);
        };
        tip.Draw += (_, e) =>
        {
            using (var brush = new SolidBrush(Raised)) e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(BorderStrong)) e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Caption, Rectangle.Inflate(e.Bounds, -8, -5), Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        return tip;
    }
}

sealed class ThemedButton : Button
{
    bool _hot;
    bool _pressed;

    public ButtonKind Kind { get; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlwaysShowFocusWhenFocused { get; set; }

    // Space between the text and the button's edge at 96 DPI (12 in windows).
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SidePadding { get; init; } = Theme.ButtonPadding;

    // Other texts the button shows at times: it is as wide as the widest, so changing the text
    // never moves the buttons beside it.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string[] SizeTexts { get; init; } = Array.Empty<string>();

    public ThemedButton(string text, ButtonKind kind)
    {
        Kind = kind;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Font = Theme.ButtonFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0);
        AccessibleName = text;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = Theme.ButtonSize(Text, Font, DeviceDpi / 96f, Theme.Px(this, Theme.FocusPad), SidePadding);
        foreach (var t in SizeTexts)
        {
            var other = Theme.ButtonSize(t, Font, DeviceDpi / 96f, Theme.Px(this, Theme.FocusPad), SidePadding);
            if (other.Width > size.Width) size.Width = other.Width;
        }
        return size;
    }

    protected override void OnPaint(PaintEventArgs e) =>
        Theme.PaintButton(e.Graphics, ClientRectangle, Parent?.BackColor ?? Theme.Surface, Kind, Text, Font,
            Enabled, _hot, _pressed, Focused && (ShowFocusCues || AlwaysShowFocusWhenFocused), Theme.Px(this, Theme.FocusPad), Theme.Px(this, Theme.ControlRadius));

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _pressed = false; Invalidate(); }
        base.OnKeyUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hot = false;
        _pressed = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

sealed class CueTextBox : TextBox
{
    const int WM_PAINT = 0x000F;
    const int WM_PRINTCLIENT = 0x0318;
    const int EM_SETMARGINS = 0x00D3;
    const int EC_LEFTMARGIN = 1;
    const int EC_RIGHTMARGIN = 2;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Cue { get; set; } = "";

    public CueTextBox()
    {
        BorderStyle = BorderStyle.None;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        Font = Theme.Body;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), IntPtr.Zero);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }

    // The cue is drawn over the empty box after it paints, and after it prints into a bitmap
    // (WM_PRINTCLIENT), so a captured window shows it too.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is not (WM_PAINT or WM_PRINTCLIENT) || TextLength > 0 || Cue.Length == 0) return;
        using var g = m.Msg == WM_PRINTCLIENT ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
        TextRenderer.DrawText(g, Cue, Font, ClientRectangle, Theme.TextSubtle, BackColor,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

sealed class ThemedInput : Control
{
    public CueTextBox Box { get; } = new();

    bool _invalid;

    // An invalid value gets a danger edge; the message under the field says what to type.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Invalid
    {
        get => _invalid;
        set { if (_invalid == value) return; _invalid = value; Invalidate(); }
    }

    // A fixed width at 96 DPI (scaled when measured), or 0 to take the width the layout offers.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int FixedWidth { get; set; }

    public ThemedInput(string accessibleName, string cue = "")
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.SurfaceAlt;
        Box.Cue = cue;
        Box.AccessibleName = accessibleName;
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        TabStop = false;
        AutoSize = true;
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(FixedWidth > 0 ? Theme.Px(this, FixedWidth) : Math.Max(proposedSize.Width, Theme.Px(this, 120)), Box.PreferredHeight + Theme.Px(this, 14));

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var x = Theme.Px(this, 10);
        Box.SetBounds(x, (Height - Box.Height) / 2, Math.Max(1, Width - x * 2), Box.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Box.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.Crisp(g);
        var r = Theme.Px(this, Theme.ControlRadius);
        using (var path = Theme.Round(new RectangleF(0, 0, Width, Height), r))
        using (var brush = new SolidBrush(Theme.SurfaceAlt))
            g.FillPath(brush, path);
        if (Box.Focused)
        {
            var ring = Theme.Px(this, 2);
            using var path = Theme.Round(new RectangleF(ring / 2f, ring / 2f, Width - ring, Height - ring), r - ring / 2f);
            using var pen = new Pen(Theme.Focus, ring);
            g.DrawPath(pen, path);
        }
        else
        {
            using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), r - 0.5f);
            using var pen = new Pen(_invalid ? Theme.Danger : Theme.BorderStrong);
            g.DrawPath(pen, path);
        }
    }
}

sealed class FramePanel : Panel
{
    readonly Control _watched;

    public FramePanel(Control watched)
    {
        _watched = watched;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceAlt;
        Padding = new Padding(2);
        watched.GotFocus += (_, _) => Invalidate();
        watched.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_watched.Focused && _watched.Visible)
        {
            var ring = Theme.Px(this, 2);
            using var brush = new SolidBrush(Theme.Focus);
            g.FillRectangle(brush, 0, 0, Width, ring);
            g.FillRectangle(brush, 0, Height - ring, Width, ring);
            g.FillRectangle(brush, 0, 0, ring, Height);
            g.FillRectangle(brush, Width - ring, 0, ring, Height);
        }
        else
        {
            using var pen = new Pen(Theme.BorderStrong);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}

sealed class EmptyState : Control
{
    readonly Label _text;

    public ThemedButton? Action { get; }

    public EmptyState(Color back, string? actionText = null)
    {
        SetStyle(ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = back;
        _text = new Label
        {
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            BackColor = back,
            Font = Theme.Body,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_text);
        if (actionText is not null)
        {
            Action = new ThemedButton(actionText, ButtonKind.Secondary);
            Controls.Add(Action);
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Message
    {
        get => _text.Text;
        set { _text.Text = value; AccessibleName = value; PerformLayout(); }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var maxW = Math.Max(Theme.Px(this, 120), Width - Theme.Px(this, 48));
        _text.MaximumSize = new Size(maxW, 0);
        var ts = _text.GetPreferredSize(new Size(maxW, 0));
        var gap = Theme.Px(this, Theme.S3);
        var bs = Action is { Visible: true } ? Action.GetPreferredSize(Size.Empty) : Size.Empty;
        var total = ts.Height + (bs.IsEmpty ? 0 : gap + bs.Height);
        var y = Math.Max(0, (Height - total) / 2);
        _text.SetBounds((Width - ts.Width) / 2, y, ts.Width, ts.Height);
        Action?.SetBounds((Width - bs.Width) / 2, y + ts.Height + gap, bs.Width, bs.Height);
    }

    // Layout that ran while hidden saw the button as invisible and gave it no size.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) PerformLayout();
    }
}

class ThemedWindow : Form
{
    protected ThemedWindow()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = AppAssets.AppIcon;
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected void EndBuild()
    {
        ResumeLayout(false);
        PerformLayout();
    }

    protected virtual bool CloseOnEscape => true;

    protected int Px(int v) => Theme.Px(this, v);

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && CloseOnEscape && CancelButton is null)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
        if (Theme.RoundCorners(Handle, Theme.DWMWCP_ROUND)) Theme.BorderColor(Handle, Theme.Border);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (StartPosition != FormStartPosition.CenterScreen) return;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (Width > area.Width || Height > area.Height)
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    protected static TableLayoutPanel Stack()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(Theme.S4 - Theme.FocusPad, Theme.S4, Theme.S4 - Theme.FocusPad, Theme.S4 - Theme.FocusPad),
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    protected static void AddRow(TableLayoutPanel t, Control c, bool fill = false)
    {
        t.RowCount++;
        t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, t.RowCount - 1);
    }

    // A window's header is its heading alone; a dialog's heading has its message under it, 4 below.
    protected static Label Heading(string text, int bottom = Theme.S1) => new()
    {
        Text = text,
        Font = Theme.Title,
        ForeColor = Theme.Text,
        AutoSize = true,
        Margin = new Padding(Theme.FocusPad, 0, Theme.FocusPad, bottom),
    };

    protected static Label Line(string text, Color color, int bottom) => new()
    {
        Text = text,
        ForeColor = color,
        AutoSize = true,
        Dock = DockStyle.Fill,
        Margin = new Padding(Theme.FocusPad, 0, Theme.FocusPad, bottom),
    };

    protected static Padding Inset(int bottom = 0) => new(Theme.FocusPad, 0, Theme.FocusPad, bottom);

    protected static FlowLayoutPanel Footer(params Control[] rightToLeft)
    {
        var f = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Margin = new Padding(0, Theme.S3 - Theme.FocusPad, 0, 0),
            Padding = new Padding(0),
        };
        // Laid out right to left, but Tab goes left to right as the buttons read.
        for (var i = 0; i < rightToLeft.Length; i++)
        {
            rightToLeft[i].Margin = new Padding(i == rightToLeft.Length - 1 ? 0 : Theme.S2 - Theme.FocusPad * 2, 0, 0, 0);
            rightToLeft[i].TabIndex = rightToLeft.Length - 1 - i;
            f.Controls.Add(rightToLeft[i]);
        }
        return f;
    }
}

sealed class ThemedMenu : ContextMenuStrip
{
    public bool DwmRounded { get; private set; }

    public ThemedMenu()
    {
        Renderer = new DarkMenuRenderer();
        Font = Theme.Body;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmRounded = Theme.RoundCorners(Handle, Theme.DWMWCP_ROUNDSMALL);
        if (DwmRounded) Theme.BorderColor(Handle, Theme.Border);
    }

    public void FitItemHeights() => FitItemHeights(Items, Theme.Px(this, 28));

    // Items at least 28 px tall, in submenus too.
    static void FitItemHeights(ToolStripItemCollection items, int target)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripMenuItem menuItem) continue;
            if (menuItem.HasDropDownItems) FitItemHeights(menuItem.DropDownItems, target);
            var h = item.GetPreferredSize(Size.Empty).Height;
            if (h >= target) continue;
            var add = target - h;
            var p = item.Padding;
            item.Padding = new Padding(p.Left, p.Top + add / 2, p.Right, p.Bottom + add - add / 2);
        }
    }

    // A submenu in the same colors, corners and renderer as the menu.
    public static ToolStripMenuItem Submenu(string text)
    {
        var item = new ToolStripMenuItem(text) { DropDown = new ThemedDropDown() };
        return item;
    }
}

sealed class ThemedDropDown : ToolStripDropDownMenu
{
    public bool DwmRounded { get; private set; }

    public ThemedDropDown()
    {
        Renderer = new DarkMenuRenderer();
        Font = Theme.Body;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DwmRounded = Theme.RoundCorners(Handle, Theme.DWMWCP_ROUNDSMALL);
        if (DwmRounded) Theme.BorderColor(Handle, Theme.Border);
    }
}

// A menu item for a destructive action: its text is danger, as on a Danger button.
sealed class DangerMenuItem(string text) : ToolStripMenuItem(text);

sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Raised;
    public override Color MenuItemSelected => Theme.Raised;
    public override Color MenuItemSelectedGradientBegin => Theme.Raised;
    public override Color MenuItemSelectedGradientEnd => Theme.Raised;
    public override Color MenuItemPressedGradientBegin => Theme.Raised;
    public override Color MenuItemPressedGradientMiddle => Theme.Raised;
    public override Color MenuItemPressedGradientEnd => Theme.Raised;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color CheckBackground => Theme.Surface;
    public override Color CheckSelectedBackground => Theme.Raised;
    public override Color CheckPressedBackground => Theme.Raised;
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) => RoundedEdges = false;

    // Where each checked item's check box is, from OnRenderItemCheck, which runs just before its text.
    readonly Dictionary<ToolStripItem, Rectangle> _checkBoxes = new();
    static readonly Dictionary<(Font, int, TextFormatFlags), float> InkCentres = new();

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.TextSubtle : e.Item is DangerMenuItem ? Theme.Danger : Theme.Text;
        base.OnRenderItemText(e);
        if (!_checkBoxes.Remove(e.Item, out var box)) return;
        var r = e.TextRectangle;
        DrawCheck(e.Graphics, box, r.Top + InkCentre(e.TextFont ?? e.Item.Font, r.Height, e.TextFormat), e.Item.Enabled);
    }

    // The check is drawn with the text, centred on the text's ink (cap top to descender): the line
    // box's centre sits about 2 px lower, because the font's internal leading is above the letters.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) => _checkBoxes[e.Item] = e.ImageRectangle;

    static void DrawCheck(Graphics g, Rectangle r, float centreY, bool enabled)
    {
        var state = g.Save();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var s = Math.Min(r.Width, r.Height) * 0.6f;
        var x = r.X + (r.Width - s) / 2f;
        var y = centreY - s / 2f;
        using (var pen = new Pen(enabled ? Theme.Text : Theme.TextSubtle, Math.Max(1.6f, s / 7f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(x, y + s * 0.55f), new PointF(x + s * 0.38f, y + s * 0.9f), new PointF(x + s, y + s * 0.12f) });
        g.Restore(state);
    }

    // The middle of "Ap"'s ink (cap top to descender) drawn the way the item's text is, in a box of
    // the text's height: measured once per font, height and format.
    static float InkCentre(Font font, int height, TextFormatFlags flags)
    {
        var key = (font, height, flags);
        if (InkCentres.TryGetValue(key, out var y)) return y;
        y = height / 2f;
        if (height > 0)
        {
            using var bmp = new Bitmap(Math.Max(8, TextRenderer.MeasureText("Ap", font).Width + 8), height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                TextRenderer.DrawText(g, "Ap", font, new Rectangle(0, 0, bmp.Width, height), Color.White, Color.Black, flags & ~TextFormatFlags.HorizontalCenter & ~TextFormatFlags.Right);
            }
            int top = int.MaxValue, bottom = int.MinValue;
            for (var py = 0; py < bmp.Height; py++)
                for (var px = 0; px < bmp.Width; px++)
                    if (bmp.GetPixel(px, py).R >= 150) { top = Math.Min(top, py); bottom = Math.Max(bottom, py); }
            if (top <= bottom) y = (top + bottom + 1) / 2f;
        }
        InkCentres[key] = y;
        return y;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var r = e.Item.ContentRectangle;
        var y = r.Top + r.Height / 2;
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, r.Left + 4, y, r.Right - 4, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ThemedMenu { DwmRounded: true } or ThemedDropDown { DwmRounded: true }) return;
        base.OnRenderToolStripBorder(e);
    }
}

// Checkbox and radio in the app's colors: off is an input-style box with a strong edge, on is accent with a
// white mark, disabled an outline with subtle text. Keys work as in the native controls.
static class ChoicePaint
{
    public const int Box = 16;
    public const int TextGap = 8;
    public const int Height = 30;

    public static Size Preferred(Control c, string text)
    {
        var t = TextRenderer.MeasureText(text, c.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var pad = Theme.Px(c, Theme.FocusPad);
        return new Size(pad + Theme.Px(c, Box) + Theme.Px(c, TextGap) + t.Width + pad, Theme.Px(c, Height));
    }

    public static void Paint(Graphics g, Control c, bool round, bool on, bool hot, bool focus)
    {
        g.Clear(c.Parent?.BackColor ?? Theme.Surface);
        Theme.Crisp(g);
        var pad = Theme.Px(c, Theme.FocusPad);
        var size = Theme.Px(c, Box);
        var box = new RectangleF(pad, (c.Height - size) / 2, size, size);
        var radius = Theme.Px(c, 4);
        GraphicsPath Shape(RectangleF r) => round ? Ellipse(r) : Theme.Round(r, radius);

        Color fill, edge = Color.Empty, mark = Color.White, fore = Theme.Text;
        if (!c.Enabled)
        {
            fill = c.Parent?.BackColor ?? Theme.Surface;
            edge = Theme.Border;
            mark = Theme.TextSubtle;
            fore = Theme.TextSubtle;
        }
        else if (on) fill = hot ? Theme.AccentHover : Theme.Accent;
        else
        {
            fill = hot ? Theme.Raised : Theme.SurfaceAlt;
            edge = Theme.BorderStrong;
        }
        using (var path = Shape(box))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        if (!edge.IsEmpty)
        {
            using var path = Shape(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1));
            using var pen = new Pen(edge);
            g.DrawPath(pen, path);
        }
        if (on && round)
        {
            var d = size * 0.4f;
            using var brush = new SolidBrush(mark);
            g.FillEllipse(brush, box.X + (size - d) / 2, box.Y + (size - d) / 2, d, d);
        }
        else if (on)
        {
            using var pen = new Pen(mark, Math.Max(1.6f, size / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(box.X + size * 0.24f, box.Y + size * 0.52f),
                new PointF(box.X + size * 0.43f, box.Y + size * 0.70f),
                new PointF(box.X + size * 0.77f, box.Y + size * 0.32f),
            });
        }
        if (focus)
        {
            // Whole-pixel ring with a 1 px gap to the box, inside the focus padding at any scale.
            var width = Theme.FocusRingWidth(pad);
            var offset = pad - width / 2f;
            var ring = RectangleF.Inflate(box, offset, offset);
            using var path = round ? Ellipse(ring) : Theme.Round(ring, radius + offset);
            using var pen = new Pen(Theme.Focus, width);
            g.DrawPath(pen, path);
        }
        var x = (int)box.Right + Theme.Px(c, TextGap);
        TextRenderer.DrawText(g, c.Text, c.Font, new Rectangle(x, 0, Math.Max(0, c.Width - x), c.Height), fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    static GraphicsPath Ellipse(RectangleF r)
    {
        var p = new GraphicsPath();
        p.AddEllipse(r);
        return p;
    }
}

sealed class ThemedCheck : CheckBox
{
    bool _hot;

    // The focus ring shows from the start on the control a window opens with, not only after a key.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlwaysShowFocusWhenFocused { get; set; }

    public ThemedCheck(string text)
    {
        Text = text;
        AccessibleName = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Body;
        AutoSize = true;
        Margin = new Padding(0);
    }

    public override Size GetPreferredSize(Size proposedSize) => ChoicePaint.Preferred(this, Text);

    protected override void OnPaint(PaintEventArgs e) => ChoicePaint.Paint(e.Graphics, this, false, Checked, _hot, Focused && (ShowFocusCues || AlwaysShowFocusWhenFocused));

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { _hot = false; Invalidate(); base.OnEnabledChanged(e); }
}

sealed class ThemedRadio : RadioButton
{
    bool _hot;

    public ThemedRadio(string text)
    {
        Text = text;
        AccessibleName = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Body;
        AutoSize = true;
        Margin = new Padding(0);
    }

    public override Size GetPreferredSize(Size proposedSize) => ChoicePaint.Preferred(this, Text);

    protected override void OnPaint(PaintEventArgs e) => ChoicePaint.Paint(e.Graphics, this, true, Checked, _hot, Focused && ShowFocusCues);

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
}

sealed class Hairline : Control
{
    public Hairline()
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Theme.Border;
        Height = 1;
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(1, proposedSize.Width), 1);
}
