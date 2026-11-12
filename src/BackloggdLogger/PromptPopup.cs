using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BackloggdLogger.Core;

namespace BackloggdLogger;

sealed record MarkAsSelection(PlayedStatus? Status, bool? Playing, bool? Backlog, bool? Wishlist);

// Every metric below is at 96 DPI and scaled by the popup's DeviceDpi (the popup has no AutoScale).
sealed class PromptPopup : Form
{
    public const int PopupWidth = 420;
    const int Gap = 12;
    const int Pad = 14;
    const int AvatarSize = 44;
    const int AvatarGap = 12;
    const int BarHeight = 2;
    const int PadLeft = Pad + AvatarSize + AvatarGap;
    const int ChipGap = 8;
    const int CloseSize = 24;
    const int CloseInset = 8;
    const int ChevronWidth = 8;
    const int ChevronGap = 6;
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_EXSTYLE = -20;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const int CS_DROPSHADOW = 0x00020000;
    const double MaxOpacity = 0.99;
    const int EnterMs = 180;
    const int EnterOffset = 16;
    const int ExitMs = 160;
    const int ResizeMs = 140;
    public const int FrameMs = 15;
    const int BusyProbeMs = 250;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    static readonly TimeSpan ResumeMin = TimeSpan.FromSeconds(3);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    // Placed popups in stack order from the corner, and popups waiting for room in arrival order.
    static readonly List<PromptPopup> Open = new();
    static readonly List<PromptPopup> Waiting = new();
    static long _arrivals;

    // Lets the UI self-check lay the stack out in a screen of its choosing. Never set by the app.
    internal static Func<Rectangle>? WorkingAreaOverride;

    static Rectangle WorkingArea => WorkingAreaOverride?.Invoke() ?? Screen.PrimaryScreen!.WorkingArea;

    static readonly Color Back = Theme.Surface;

    readonly FlowLayoutPanel _layout;
    readonly PopupCorner _corner;
    readonly bool _notice;
    readonly long _arrival = ++_arrivals;
    readonly float _scale;

    // One timer drives every moving part. Each frame reads elapsed time from a Stopwatch, so the
    // motion stays even when frames arrive late.
    readonly System.Windows.Forms.Timer _frame = new() { Interval = FrameMs };
    readonly System.Windows.Forms.Timer _keepOnTop = new() { Interval = 500 };

    readonly Stopwatch _enterClock = new();
    bool _entering;
    int _enterDx;

    readonly bool _hasCountdown;
    readonly Stopwatch _countdownClock = new();
    TimeSpan _lastCountdownTick;
    TimeSpan _lastProbe = TimeSpan.FromMilliseconds(-BusyProbeMs);
    TimeSpan _remaining;
    TimeSpan _total;
    bool _hovered;
    bool _countdownStopped;
    bool _shown;

    readonly Stopwatch _resizeClock = new();
    bool _resizing;
    int _resizeFrom;
    int _resizeTo;
    Action? _afterResize;
    int _collapsedHeight;
    bool _measured;

    readonly Stopwatch _slideClock = new();
    bool _sliding;
    int _slideFrom;
    int _slideY;

    readonly Stopwatch _fadeClock = new();
    double _fadeFrom;
    bool _closing;

    bool _sawBusy;
    readonly Func<bool>? _busyProbe;
    readonly Action<bool>? _onTimedOut;
    readonly Action<bool>? _onEnded;
    bool _ended;
    Point _final;
    bool _placed;
    bool _dwmRounded;
    bool _expanded;
    readonly MarkAsButton? _markAsButton;
    readonly MarkAsPanel? _markAsPanel;
    readonly Action<MarkAsSelection>? _keepPick;
    bool _answered;

    public PromptPopup(string title, string body, IReadOnlyList<(string Label, Action? OnClick)> buttons, TimeSpan? timeout,
        Func<bool>? busyProbe = null, Action<bool>? onTimedOut = null, Action<MarkAsSelection?>? openLog = null,
        PopupCorner corner = PopupCorner.BottomRight, bool notice = false, Action<bool>? onEnded = null,
        Action<MarkAsSelection>? keepPick = null)
    {
        _keepPick = keepPick;
        _busyProbe = busyProbe;
        _onTimedOut = onTimedOut;
        _onEnded = onEnded;
        _corner = corner;
        _notice = notice;
        _scale = DeviceDpi / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // Topmost comes from the window style and RaiseAboveGame, not the TopMost property: with that set, showing
        // the form takes the keyboard focus from a window of the app that is in front.
        StartPosition = FormStartPosition.Manual;
        BackColor = Back;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Opacity = 0;
        Font = Theme.Body;
        Padding = new Padding(Px(PadLeft), Px(Pad), Px(Pad), Px(Pad));

        var chips = new List<Control>();
        var labels = new List<string>();
        if (openLog is not null)
        {
            var open = new ChipButton("Open log", true, _scale) { TabStop = false };
            open.Click += (_, _) =>
            {
                if (_closing) return;
                var choice = _expanded && _markAsPanel!.Choice.Changed ? _markAsPanel.Selection : null;
                _answered = true;
                try { openLog(choice); }
                finally { End(byUser: true); BeginFade(); }
            };
            chips.Add(open);
            labels.Add(open.Text);
            _markAsButton = new MarkAsButton("Mark as", _scale) { TabStop = false };
            _markAsButton.Click += (_, _) => { if (!_closing) ToggleMarkAs(); };
            chips.Add(_markAsButton);
            labels.Add(_markAsButton.Text);
        }
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, onClick) = buttons[i];
            var b = new ChipButton(label, openLog is null && i == 0, _scale) { TabStop = false };
            b.Click += (_, _) =>
            {
                if (_closing) return;
                _answered = true;
                try { onClick?.Invoke(); }
                finally { End(byUser: true); BeginFade(); }
            };
            chips.Add(b);
            labels.Add(label);
        }
        for (var i = 0; i < chips.Count; i++) chips[i].Margin = new Padding(0, 0, i == chips.Count - 1 ? 0 : Px(ChipGap), 0);
        Width = WidthFor(_scale, Theme.ButtonFont, labels, openLog is not null);

        var close = new CloseButton(_scale)
        {
            Size = new Size(Px(CloseSize), Px(CloseSize)),
            Location = new Point(Width - Px(CloseSize) - Px(CloseInset), Px(CloseInset)),
            TabStop = false,
            AccessibleName = "Close",
        };
        close.Click += (_, _) => { End(byUser: true); BeginFade(); };

        _layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Back,
        };
        var inner = Width - Padding.Horizontal;
        var beside = inner - Px(CloseSize) - Px(CloseInset);
        _layout.Controls.Add(new Label
        {
            Text = "Backloggd Logger",
            Font = Theme.Caption,
            ForeColor = Theme.TextSubtle,
            AutoSize = true,
            MaximumSize = new Size(beside, 0),
            Margin = new Padding(0, 0, 0, Px(Theme.S1)),
        });
        _layout.Controls.Add(new Label
        {
            Text = title,
            Font = Theme.Title,
            ForeColor = Theme.Text,
            AutoSize = true,
            MaximumSize = new Size(beside, 0),
            Margin = new Padding(0, 0, 0, Px(body.Length > 0 ? Theme.S1 : Theme.S3)),
        });
        if (body.Length > 0)
        {
            _layout.Controls.Add(new Label
            {
                Text = body,
                ForeColor = Theme.TextMuted,
                AutoSize = true,
                MaximumSize = new Size(inner, 0),
                Margin = new Padding(0, 0, 0, Px(Theme.S3)),
            });
        }

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Back,
        };
        foreach (var c in chips) row.Controls.Add(c);
        _layout.Controls.Add(row);
        if (openLog is not null)
        {
            _markAsPanel = new MarkAsPanel(_scale) { Width = inner, Visible = false, Margin = new Padding(0, Px(Theme.S2), 0, 0) };
            _markAsPanel.Height = _markAsPanel.GetPreferredSize(Size.Empty).Height;
            _layout.Controls.Add(_markAsPanel);
        }

        Controls.Add(_layout);
        Controls.Add(close);
        close.BringToFront();
        _layout.MouseDown += (_, _) => StopCountdown();
        StopCountdownOnMouseDown(_layout);

        _frame.Tick += (_, _) => OnFrame();
        _keepOnTop.Tick += (_, _) => RaiseAboveGame();

        if (timeout is { } t)
        {
            _hasCountdown = true;
            _remaining = t;
            _total = t;
        }
    }

    int Px(int v) => (int)Math.Round(v * _scale);

    // Says once how the popup ended: by the user, or without an answer. A changed Mark as choice is kept whenever
    // it ends without one of its buttons; Skip, Never ask again and Open log keep nothing.
    void End(bool byUser)
    {
        if (_ended) return;
        _ended = true;
        if (!_answered && _keepPick is not null && _expanded && _markAsPanel is { } panel && panel.Choice.Changed)
        {
            try { _keepPick(panel.Selection); }
            catch (Exception ex) { Log.Write("errors", "popup keep pick: " + ex); }
        }
        try { _onEnded?.Invoke(byUser); }
        catch (Exception ex) { Log.Write("errors", "popup ended: " + ex); }
    }

    static int Scaled(int v, float scale) => (int)Math.Round(v * scale);

    static Size ChipSize(string text, Font font, float scale) => Theme.ButtonSize(text, font, scale, 0, Theme.PopupButtonPadding);

    static Size MarkAsSize(string text, Font font, float scale)
    {
        var s = ChipSize(text, font, scale);
        return new Size(s.Width + Scaled(ChevronGap + ChevronWidth, scale), s.Height);
    }

    // 420 at 96 DPI scaled, or wider when the scaled buttons need more room on their one row.
    internal static int WidthFor(float scale, Font buttonFont, IReadOnlyList<string> labels, bool markAs)
    {
        var row = 0;
        for (var i = 0; i < labels.Count; i++)
            row += (markAs && i == 1 ? MarkAsSize(labels[i], buttonFont, scale) : ChipSize(labels[i], buttonFont, scale)).Width;
        row += Scaled(ChipGap, scale) * Math.Max(0, labels.Count - 1);
        return Math.Max(Scaled(PopupWidth, scale), Scaled(PadLeft, scale) + row + Scaled(Pad, scale));
    }

    internal bool HasCountdown => _hasCountdown;

    internal bool IsNotice => _notice;

    internal bool IsWaiting => Waiting.Contains(this);

    internal bool CountdownRunning => _hasCountdown && _shown && _placed && !_countdownStopped && !_closing;

    // Width of the countdown line for the time left right now; 0 once the countdown is stopped.
    internal int CountdownWidth =>
        _total > TimeSpan.Zero ? (int)Math.Round(Width * Math.Clamp(_remaining.TotalMilliseconds / _total.TotalMilliseconds, 0, 1)) : 0;

    Rectangle BarRect => new(0, Height - Px(BarHeight), Width, Px(BarHeight));

    void OnFrame()
    {
        if (IsDisposed) return;
        if (_entering) StepEnter();
        if (_sliding) StepSlide();
        if (_entering || _sliding) PlaceWindow();
        if (_resizing) StepResize();
        if (CountdownRunning) StepCountdown();
        if (_closing) StepFade();
        if (!IsDisposed && !_entering && !_sliding && !_resizing && !CountdownRunning && !_closing) _frame.Stop();
    }

    void Animate()
    {
        if (!IsDisposed && !_frame.Enabled) _frame.Start();
    }

    void StepCountdown()
    {
        var now = _countdownClock.Elapsed;
        var dt = now - _lastCountdownTick;
        _lastCountdownTick = now;
        if (!_sawBusy && _busyProbe is { } probe && (now - _lastProbe).TotalMilliseconds >= BusyProbeMs)
        {
            _lastProbe = now;
            try { _sawBusy = probe(); }
            catch { }
        }
        if (Bounds.Contains(Cursor.Position))
        {
            _hovered = true;
            return;
        }
        if (_hovered)
        {
            _hovered = false;
            // At least 3 s after the cursor leaves; the total grows with it, so the line goes on
            // from the same width instead of jumping back.
            if (_remaining < ResumeMin && _remaining > TimeSpan.Zero)
            {
                _total = TimeSpan.FromTicks((long)(_total.Ticks * (ResumeMin.TotalMilliseconds / _remaining.TotalMilliseconds)));
                _remaining = ResumeMin;
            }
        }
        _remaining -= dt;
        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            try { _onTimedOut?.Invoke(_sawBusy); }
            catch (Exception ex) { Log.Write("errors", "popup timeout: " + ex); }
            End(byUser: false);
            BeginFade();
        }
        else Invalidate(BarRect);
    }

    void StopCountdownOnMouseDown(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            c.MouseDown += (_, _) => StopCountdown();
            StopCountdownOnMouseDown(c);
        }
    }

    void StopCountdown()
    {
        if (_countdownStopped || _closing) return;
        _countdownStopped = true;
        _countdownClock.Stop();
        _total = TimeSpan.Zero;
        Invalidate(BarRect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        StopCountdown();
        base.OnMouseDown(e);
    }

    void ToggleMarkAs()
    {
        if (_markAsPanel is null || _markAsButton is null) return;
        StopCountdown();
        _expanded = !_expanded;
        _markAsButton.Expanded = _expanded;
        if (_expanded)
        {
            _markAsPanel.Visible = true;
            ResizeTo(MakeRoom(MeasureHeight()), null);
        }
        else ResizeTo(_collapsedHeight, () => _markAsPanel.Visible = false);
    }

    int MeasureHeight() =>
        Math.Max(_layout.GetPreferredSize(new Size(Width - Padding.Horizontal, 0)).Height + Padding.Vertical, Px(Pad) * 2 + Px(AvatarSize));

    void ResizeTo(int height, Action? after)
    {
        _afterResize = after;
        _resizeFrom = Height;
        _resizeTo = height;
        _resizing = true;
        _resizeClock.Restart();
        Animate();
    }

    void StepResize()
    {
        var t = Math.Min(1.0, _resizeClock.ElapsedMilliseconds / (double)ResizeMs);
        var ease = 1 - Math.Pow(1 - t, 3);
        SetHeight((int)Math.Round(_resizeFrom + (_resizeTo - _resizeFrom) * ease));
        if (t < 1.0) return;
        _resizing = false;
        _resizeClock.Stop();
        var after = _afterResize;
        _afterResize = null;
        after?.Invoke();
        BringBack(_corner);
    }

    // The stack: popups of one corner sit in a column from it, 12 px apart, never overlapping or leaving the working
    // area. For room, the oldest notices are dismissed first, then popups wait and come back in order.

    bool StacksUp => _corner == PopupCorner.BottomRight;

    // The height the stack has to plan for: a popup that is growing counts at its final height.
    int PlannedHeight => _resizing ? Math.Max(Height, _resizeTo) : Height;

    int Room => WorkingArea.Height - 2 * Px(Gap);

    static List<PromptPopup> Stack(PopupCorner corner) =>
        Open.Where(p => p._corner == corner && !p._closing && !p.IsDisposed).ToList();

    int TopFor(int near, int height)
    {
        var area = WorkingArea;
        return StacksUp ? area.Bottom - Px(Gap) - near - height : area.Top + Px(Gap) + near;
    }

    static void Arrange(PopupCorner corner, bool animate)
    {
        var near = 0;
        foreach (var p in Stack(corner))
        {
            p.MoveTo(p.TopFor(near, p.Height), animate);
            near += p.Height + p.Px(Gap);
        }
    }

    int CurrentY => _sliding ? _slideY : _final.Y;

    void MoveTo(int y, bool animate)
    {
        var x = WorkingArea.Right - Px(Gap) - Width;
        var from = CurrentY;
        _final = new Point(x, y);
        if (!IsHandleCreated || !Visible)
        {
            _sliding = false;
            Location = new Point(x + Px(EnterOffset), y);
            return;
        }
        if (animate && from != y)
        {
            _slideFrom = from;
            _slideY = from;
            _slideClock.Restart();
            _sliding = true;
            Animate();
        }
        else
        {
            _sliding = false;
            PlaceWindow();
        }
    }

    void PlaceWindow()
    {
        if (IsHandleCreated && !IsDisposed)
            SetWindowPos(Handle, IntPtr.Zero, _final.X + _enterDx, CurrentY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void StepSlide()
    {
        var t = Math.Min(1.0, _slideClock.ElapsedMilliseconds / (double)ResizeMs);
        var ease = 1 - Math.Pow(1 - t, 3);
        _slideY = (int)Math.Round(_slideFrom + (_final.Y - _slideFrom) * ease);
        if (t < 1.0) return;
        _sliding = false;
        _slideClock.Stop();
    }

    // Keeps the edge at the popup's corner still (bottom for bottom right, top for top right); the
    // popups beyond it move with the growth.
    void SetHeight(int height)
    {
        if (height == Height || !IsHandleCreated || IsDisposed) return;
        var near = 0;
        foreach (var p in Stack(_corner))
        {
            if (p == this) break;
            near += p.Height + p.Px(Gap);
        }
        _sliding = false;
        _final = new Point(_final.X, TopFor(near, height));
        SetWindowPos(Handle, IntPtr.Zero, _final.X + _enterDx, _final.Y, Width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        Arrange(_corner, animate: false);
        Invalidate();
    }

    void Measure()
    {
        if (_measured) return;
        _measured = true;
        Height = _collapsedHeight = MeasureHeight();
    }

    // Puts the popup at the far end of its corner's column, dismissing the oldest notices when
    // that is what it takes. False when even then there is no room.
    bool TryPlace()
    {
        while (true)
        {
            var stack = Stack(_corner);
            var used = stack.Sum(p => p.PlannedHeight + p.Px(Gap));
            if (stack.Count == 0 || used + Height <= Room)
            {
                Open.Add(this);
                _placed = true;
                Arrange(_corner, animate: true);
                return true;
            }
            if (stack.FirstOrDefault(p => p._notice) is not { } notice) return false;
            notice.BeginFade(reflow: false);
        }
    }

    // Frees room for this popup to grow to target; returns the height it may grow to.
    int MakeRoom(int target)
    {
        var changed = false;
        while (true)
        {
            var stack = Stack(_corner);
            var total = stack.Sum(p => (p == this ? target : p.PlannedHeight) + p.Px(Gap)) - Px(Gap);
            if (total <= Room) break;
            if (stack.FirstOrDefault(p => p != this && p._notice) is { } notice)
            {
                notice.BeginFade(reflow: false);
                changed = true;
                continue;
            }
            var i = stack.IndexOf(this);
            var other = i < stack.Count - 1 ? stack[^1] : i > 0 ? stack[i - 1] : null;
            if (other is null)
            {
                target = Math.Max(Height, Room);
                break;
            }
            other.Hold();
            changed = true;
        }
        if (changed) Arrange(_corner, animate: false);
        return target;
    }

    void Hold()
    {
        Open.Remove(this);
        _placed = false;
        _countdownClock.Stop();
        _entering = false;
        _sliding = false;
        _enterDx = 0;
        Waiting.Add(this);
        Waiting.Sort((a, b) => Math.Sign(a._arrival - b._arrival));
        Hide();
    }

    // A waiting notice stays waiting while a question that arrived after it waits too: the question
    // would dismiss it to make room the moment it came back, so it would only flash.
    static void BringBack(PopupCorner corner)
    {
        if (Open.Any(p => p._corner == corner && p._resizing && !p.IsDisposed)) return;
        var skipped = false;
        var placed = false;
        foreach (var w in Waiting.Where(w => w._corner == corner).ToList())
        {
            if (w.IsDisposed || w._closing)
            {
                Waiting.Remove(w);
                continue;
            }
            if (w._notice && Waiting.Any(q => q._corner == corner && !q._notice && q._arrival > w._arrival && !q.IsDisposed && !q._closing))
            {
                skipped = true;
                continue;
            }
            if (!w.TryPlace()) return;
            Waiting.Remove(w);
            w.Show();
            placed = true;
        }
        // The questions are in: a skipped notice may come back now if there is room left.
        if (skipped && placed) BringBack(corner);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && !Visible)
        {
            if (!_placed)
            {
                Measure();
                var queued = Waiting.Any(w => w != this && w._corner == _corner && w._arrival < _arrival && !w.IsDisposed);
                if (_closing || queued || !TryPlace())
                {
                    if (!_closing && !Waiting.Contains(this))
                    {
                        Waiting.Add(this);
                        Waiting.Sort((a, b) => Math.Sign(a._arrival - b._arrival));
                    }
                    return;
                }
                Waiting.Remove(this);
            }
            if (_shown)
            {
                Opacity = 0;
                base.SetVisibleCore(true);
                StartEnter();
                return;
            }
        }
        base.SetVisibleCore(value);
    }

    // Closes every popup, waiting ones too. Used by the UI self-check between its sections.
    internal static void CloseAll()
    {
        foreach (var w in Waiting.ToList()) w.Dispose();
        Waiting.Clear();
        foreach (var p in Open.ToList()) p.Close();
    }

    void StartEnter()
    {
        RaiseAboveGame();
        _entering = true;
        _enterDx = Px(EnterOffset);
        _enterClock.Restart();
        if (CountdownRunning) _countdownClock.Start();
        Animate();
    }

    void StepEnter()
    {
        var t = Math.Min(1.0, _enterClock.ElapsedMilliseconds / (double)EnterMs);
        var ease = 1 - Math.Pow(1 - t, 3);
        _enterDx = (int)Math.Round(Px(EnterOffset) * (1 - ease));
        Opacity = Math.Min(ease, MaxOpacity);
        if (t < 1.0) return;
        _entering = false;
        _enterClock.Stop();
    }

    void BeginFade(bool reflow = true)
    {
        if (_closing) return;
        _closing = true;
        _entering = false;
        _countdownClock.Stop();
        _fadeFrom = Opacity;
        _fadeClock.Restart();
        // A fading popup takes no clicks, so the popups that slide under it stay usable.
        if (IsHandleCreated)
            SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)(GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT));
        Animate();
        if (!reflow) return;
        Arrange(_corner, animate: true);
        BringBack(_corner);
    }

    void StepFade()
    {
        var t = Math.Min(1.0, _fadeClock.ElapsedMilliseconds / (double)ExitMs);
        Opacity = _fadeFrom * (1 - t * t);
        if (t < 1.0) return;
        _frame.Stop();
        Close();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    class NoFocusButton : Button
    {
        protected bool Hot;
        protected bool Pressed;

        public NoFocusButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Back;
        }

        protected override bool ShowFocusCues => false;
        protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    }

    sealed class ChipButton : NoFocusButton
    {
        readonly ButtonKind _kind;
        readonly float _scale;

        public ChipButton(string text, bool primary, float scale)
        {
            _kind = primary ? ButtonKind.Primary : ButtonKind.Secondary;
            _scale = scale;
            Text = text;
            Font = Theme.ButtonFont;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            AccessibleName = text;
        }

        public override Size GetPreferredSize(Size proposedSize) => ChipSize(Text, Font, _scale);

        protected override void OnPaint(PaintEventArgs e) =>
            Theme.PaintButton(e.Graphics, ClientRectangle, Back, _kind, Text, Font, true, Hot, Pressed, false, 0, Scaled(Theme.ControlRadius, _scale));
    }

    // A 10 px x in text-subtle in a 24 px hit area; hover fills a raised square and turns it white. Strokes are
    // 1.75 px through pixel centres: GDI+ antialiasing leaves a 1.5 px diagonal at about 70% of its color.
    sealed class CloseButton(float scale) : NoFocusButton
    {
        internal const int Glyph = 10;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Back);
            Theme.Crisp(g);
            if (Hot)
            {
                using var path = Theme.Round(new RectangleF(0, 0, Width, Height), 4 * scale);
                using var brush = new SolidBrush(Pressed ? Theme.Highlight : Theme.Raised);
                g.FillPath(brush, path);
            }
            using var pen = new Pen(Hot ? Theme.Text : Theme.TextSubtle, Math.Max(1.75f, 1.75f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var half = (int)Math.Round(Glyph * scale / 2);
            float a = Width / 2 - half, b = Width / 2 + half;
            g.DrawLine(pen, a, a, b, b);
            g.DrawLine(pen, b, a, a, b);
        }
    }

    sealed class MarkAsButton : NoFocusButton
    {
        readonly float _scale;
        bool _expanded;

        public MarkAsButton(string text, float scale)
        {
            _scale = scale;
            Text = text;
            Font = Theme.ButtonFont;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.ButtonDropDown;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded
        {
            get => _expanded;
            set { _expanded = value; AccessibleDescription = value ? "Expanded" : "Collapsed"; Invalidate(); }
        }

        public override Size GetPreferredSize(Size proposedSize) => MarkAsSize(Text, Font, _scale);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Back);
            Theme.Crisp(g);
            var fill = Pressed ? Theme.SurfaceAlt : _expanded || Hot ? Theme.Highlight : Theme.Raised;
            using (var path = Theme.Round(new RectangleF(0, 0, Width, Height), Scaled(Theme.ControlRadius, _scale)))
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            var gap = Scaled(ChevronGap, _scale);
            var chevron = Scaled(ChevronWidth, _scale);
            var tw = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags).Width;
            var x = (Width - tw - gap - chevron) / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, tw + 1, Height), Theme.Text, flags);
            var cx = x + tw + gap + chevron / 2f;
            var cy = Height / 2f;
            var dy = (_expanded ? -2f : 2f) * _scale;
            var hw = 4 * _scale;
            using var pen = new Pen(Theme.Text, 1.5f * _scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cx - hw, cy - dy), new PointF(cx, cy + dy), new PointF(cx + hw, cy - dy) });
        }
    }

    sealed class MarkAsPanel : Control
    {
        const int SectionGap = 8;
        const int RowHeight = 30;
        const int RowGap = 4;
        const int ToggleHeight = 30;
        const int ToggleGap = 8;
        const int Square = 9;
        const int Inset = 12;
        const int IconSize = 12;
        const int IconGap = 6;
        const int NameGap = 8;
        const int ToggleCount = 3;

        static readonly (PlayedStatus Status, string Name, string Description, Color Color)[] Statuses =
        {
            (PlayedStatus.Played, "Played", "Nothing specific", Theme.StatusPlayed),
            (PlayedStatus.Completed, "Completed", "Beat your main objective", Theme.StatusCompleted),
            (PlayedStatus.Retired, "Retired", "No ending to reach", Theme.StatusRetired),
            (PlayedStatus.Shelved, "Shelved", "May pick up later", Theme.StatusShelved),
            (PlayedStatus.Abandoned, "Abandoned", "Staying unfinished", Theme.StatusAbandoned),
        };

        static readonly string[] ToggleNames = { "Playing", "Backlog", "Wishlist" };

        readonly MarkAsChoice _choice = new();
        readonly float _scale;
        int _hot = -1;
        int _pressed = -1;

        public MarkAsPanel(float scale)
        {
            _scale = scale;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Back;
            AccessibleName = "Mark as";
            AccessibleRole = AccessibleRole.Grouping;
        }

        int S(int v) => Scaled(v, _scale);

        public MarkAsChoice Choice => _choice;

        public MarkAsSelection Selection => new(_choice.Status, _choice.Sent(Shelf.Playing), _choice.Sent(Shelf.Backlog), _choice.Sent(Shelf.Wishlist));

        int RowsTop => 1 + S(SectionGap);

        int TogglesTop => RowsTop + Statuses.Length * S(RowHeight) + (Statuses.Length - 1) * S(RowGap) + S(SectionGap) + 1 + S(SectionGap);

        public override Size GetPreferredSize(Size proposedSize) => new(Width, TogglesTop + S(ToggleHeight));

        Rectangle ItemRect(int i)
        {
            if (i < Statuses.Length) return new Rectangle(0, RowsTop + i * (S(RowHeight) + S(RowGap)), Width, S(RowHeight));
            var t = i - Statuses.Length;
            var w = (Width - S(ToggleGap) * (ToggleCount - 1)) / ToggleCount;
            var x = t * (w + S(ToggleGap));
            return new Rectangle(x, TogglesTop, t == ToggleCount - 1 ? Width - x : w, S(ToggleHeight));
        }

        int ItemAt(Point p)
        {
            for (var i = 0; i < Statuses.Length + ToggleCount; i++)
                if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        void Choose(int i)
        {
            if (i < Statuses.Length) _choice.Pick(Statuses[i].Status);
            else _choice.Toggle((Shelf)(i - Statuses.Length));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hit = ItemAt(e.Location);
            if (hit != _hot) { _hot = hit; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = -1;
            _pressed = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = ItemAt(e.Location); Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var hit = ItemAt(e.Location);
                if (hit >= 0 && hit == _pressed) Choose(hit);
                _pressed = -1;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Back);
            using (var line = new SolidBrush(Theme.Border))
            {
                g.FillRectangle(line, 0, 0, Width, 1);
                g.FillRectangle(line, 0, TogglesTop - S(SectionGap) - 1, Width, 1);
            }
            for (var i = 0; i < Statuses.Length; i++) PaintRow(g, i);
            for (var t = 0; t < ToggleCount; t++) PaintToggle(g, t);
        }

        const TextFormatFlags LabelFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;

        // Names are short and always drawn in full, measured against the room they really have.
        internal Rectangle NameRect(Graphics g, int i)
        {
            var r = ItemRect(i);
            var x = r.X + S(Inset) + S(Square) + S(NameGap);
            var room = Math.Max(0, r.Right - S(Inset) - x);
            var w = TextRenderer.MeasureText(g, Statuses[i].Name, Theme.Body, new Size(room, r.Height), LabelFlags).Width;
            return new Rectangle(x, r.Y, Math.Min(w + 1, room), r.Height);
        }

        internal Rectangle ToggleLabelRect(Graphics g, int t)
        {
            var r = ItemRect(Statuses.Length + t);
            var room = Math.Max(0, r.Width - S(IconSize) - S(IconGap));
            var w = Math.Min(TextRenderer.MeasureText(g, ToggleNames[t], Theme.ButtonFont, new Size(room, r.Height), LabelFlags).Width + 1, room);
            var x = r.X + (r.Width - S(IconSize) - S(IconGap) - w) / 2;
            return new Rectangle(x + S(IconSize) + S(IconGap), r.Y, w, r.Height);
        }

        void PaintRow(Graphics g, int i)
        {
            var (status, name, description, color) = Statuses[i];
            var r = ItemRect(i);
            var selected = _choice.Status == status;
            var lit = selected || _pressed == i;
            var radius = S(Theme.ControlRadius);
            Theme.Crisp(g);
            if (lit || _hot == i)
            {
                using var path = Theme.Round(r, radius);
                using var brush = new SolidBrush(lit ? Theme.Highlight : Theme.Raised);
                g.FillPath(brush, path);
            }
            else
            {
                using var path = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius - 0.5f);
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            using (var brush = new SolidBrush(color))
                g.FillRectangle(brush, r.X + S(Inset), r.Y + (r.Height - S(Square)) / 2, S(Square), S(Square));
            var nameRect = NameRect(g, i);
            TextRenderer.DrawText(g, name, Theme.Body, nameRect, Theme.Text, LabelFlags);
            var descX = nameRect.Right + S(NameGap);
            TextRenderer.DrawText(g, description, Theme.Caption, new Rectangle(descX, r.Y, Math.Max(0, r.Right - S(Inset) - descX), r.Height),
                lit ? Theme.TextMuted : Theme.TextSubtle,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        void PaintToggle(Graphics g, int t)
        {
            var i = Statuses.Length + t;
            var r = ItemRect(i);
            var on = _choice.IsOn((Shelf)t);
            var pressed = _pressed == i;
            var hot = _hot == i;
            var fill = on
                ? pressed ? Theme.AccentPressed : hot ? Theme.AccentHover : Theme.Accent
                : pressed ? Theme.SurfaceAlt : hot ? Theme.Highlight : Theme.Raised;
            var fore = on ? Color.White : Theme.Text;
            Theme.Crisp(g);
            using (var path = Theme.Round(r, S(Theme.ControlRadius)))
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            var label = ToggleLabelRect(g, t);
            var y = r.Y + (r.Height - S(IconSize)) / 2f;
            var state = g.Save();
            g.TranslateTransform(label.X - S(IconGap) - S(IconSize), y);
            g.ScaleTransform(_scale, _scale);
            using (var pen = new Pen(fore, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                DrawIcon(g, pen, t);
            g.Restore(state);
            TextRenderer.DrawText(g, ToggleNames[t], Theme.ButtonFont, label, fore, LabelFlags);
        }

        // Drawn in a 12 px box at the origin; the caller scales it.
        static void DrawIcon(Graphics g, Pen pen, int t)
        {
            switch (t)
            {
                case 0:
                    g.DrawPolygon(pen, new[] { new PointF(2, 1), new PointF(2, 11), new PointF(11, 6) });
                    break;
                case 1:
                    g.DrawRectangle(pen, 0.75f, 2, 2.5f, 9.5f);
                    g.DrawRectangle(pen, 4.75f, 0.5f, 2.5f, 11);
                    g.DrawRectangle(pen, 8.75f, 3, 2.5f, 8.5f);
                    break;
                default:
                    g.DrawRectangle(pen, 0.75f, 4, 10.5f, 2.5f);
                    g.DrawRectangle(pen, 1.75f, 6.5f, 8.5f, 5);
                    g.DrawLine(pen, 6, 4, 6, 11.5f);
                    g.DrawBezier(pen, 6, 4, 4, 0.5f, 1.5f, 2, 6, 4);
                    g.DrawBezier(pen, 6, 4, 8, 0.5f, 10.5f, 2, 6, 4);
                    break;
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dwmRounded = Theme.RoundCorners(Handle, Theme.DWMWCP_ROUND);
        if (_dwmRounded) Theme.BorderColor(Handle, Theme.Border);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Back);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(AppAssets.Avatar, new Rectangle(Px(Pad), Px(Pad), Px(AvatarSize), Px(AvatarSize)));
        g.PixelOffsetMode = PixelOffsetMode.Default;
        if (!_dwmRounded)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        var w = CountdownWidth;
        if (w > 0)
        {
            using var brush = new SolidBrush(Theme.TextSubtle);
            g.FillRectangle(brush, 0, Height - Px(BarHeight), w, Px(BarHeight));
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Measure();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _keepOnTop.Start();
        _shown = true;
        if (CountdownRunning) _lastCountdownTick = TimeSpan.Zero;
        StartEnter();
    }

    void RaiseAboveGame()
    {
        if (IsHandleCreated && !IsDisposed && Visible)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // A popup that waited for room and was never shown has no close event.
    protected override void Dispose(bool disposing)
    {
        if (disposing) End(byUser: false);
        base.Dispose(disposing);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _frame.Stop();
        _frame.Dispose();
        _keepOnTop.Stop();
        _keepOnTop.Dispose();
        var placed = Open.Remove(this);
        Waiting.Remove(this);
        _placed = false;
        End(byUser: false);
        base.OnFormClosed(e);
        if (!placed) return;
        Arrange(_corner, animate: true);
        BringBack(_corner);
    }
}
