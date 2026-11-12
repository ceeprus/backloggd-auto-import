using System.Runtime.InteropServices;
using System.Text.Json;
using BackloggdLogger.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BackloggdLogger;

// The browser view for journal runs: never seen, never focused (off every screen, tool window, no activation), on
// the app's own profile. Every request goes through JournalTraffic, which lets out only the one checked save.
sealed class JournalWindow : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_APPWINDOW = 0x00040000;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const int WM_DISPLAYCHANGE = 0x007E;
    const int MA_NOACTIVATE = 3;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_NOSIZE = 0x0001;

    // Big enough for the log box's desktop layout (the calendar is part of it).
    public static readonly Size ViewSize = new(1280, 900);

    // Generous: a page that is not on screen gets its timers slowed by the browser.
    const int RunTimeoutMs = 120_000;
    const int SaveResponseMs = 20_000;

    static readonly string Script = LogWindow.LoadScript("journal.js");

    const string CommitScript = "(() => window.__journalCommit ? window.__journalCommit() : null)()";
    const string StopScript = "window.__journalStop = true;";

    // How long the page gets to answer the commit; no answer counts as pressed.
    static readonly TimeSpan CommitLimit = TimeSpan.FromSeconds(15);

    readonly JournalRun _run = new();
    readonly JournalTraffic _traffic = new();
    readonly List<(int Status, string? ContentType, string? Body)> _answers = new();
    readonly List<string> _notes = new();
    TaskCompletionSource? _saveSeen;
    TaskCompletionSource<JournalOutcome>? _result;
    WebView2? _web;
    AdBlock? _ads;
    JournalJob? _job;

    // A Mark as run instead of a journal one; the slug, script options and log label of either.
    StatusJob? _statusJob;
    string _slug = "";
    string _opts = "";
    string _label = "";
    Func<bool> _shouldStop = () => false;
    Func<int, string?, bool> _prepare = (_, _) => false;
    bool _injected;

    // The total the save will set, once the save step has started.
    int? _target;

    // The 409 the app answers a refused request with, built ahead so refusing never has to create
    // one at the moment it matters.
    CoreWebView2WebResourceResponse? _refusal;

    // The owner's editor-mode cookie as it was before the run (null: there was none), once the run
    // switched it to quick; put back at every end of the run.
    CoreWebView2Cookie? _editorCookie;
    bool _editorSwitched;
    bool _editorRestored;
    const string BackloggdUri = "https://backloggd.com/";

    // Writes the owner's cookie to state before it is switched (false: not written, so no switch),
    // and clears it once put back; a cookie a cut-off run left switched, to put back first.
    internal Func<EditorCookieSaved?, bool>? CookieStore;

    internal EditorCookieSaved? CookieLeftSwitched;

    public int Activations { get; private set; }

    // The window got focus or was found in front during the run.
    public bool WasActivated { get; private set; }


    public JournalWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        Size = ViewSize;
        Location = OffScreen(ViewSize, SystemInformation.VirtualScreen);
        Text = "Backloggd Logger journal";
        BackColor = Theme.Surface;
    }

    // Left of and above every screen, with room to spare.
    internal static Point OffScreen(Size size, Rectangle screens) => new(screens.Left - size.Width - 200, screens.Top - size.Height - 200);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle = (cp.ExStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            return cp;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            case WM_WINDOWPOSCHANGING when m.LParam != IntPtr.Zero:
                // Whoever moves it (a display change, a cascade, a restore), it stays off every screen
                // and is never activated by the move.
                var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                var size = (pos.flags & SWP_NOSIZE) != 0 ? Size : new Size(pos.cx, pos.cy);
                var off = OffScreen(size, SystemInformation.VirtualScreen);
                pos.x = off.X;
                pos.y = off.Y;
                pos.flags = (pos.flags & ~SWP_NOMOVE) | SWP_NOACTIVATE;
                Marshal.StructureToPtr(pos, m.LParam, false);
                break;
            case WM_DISPLAYCHANGE:
                base.WndProc(ref m);
                if (IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) Location = OffScreen(Size, SystemInformation.VirtualScreen); }));
                return;
        }
        base.WndProc(ref m);
    }

    // Should never happen; the run stops and the app pauses journal runs if it does.
    protected override void OnActivated(EventArgs e)
    {
        Activations++;
        WasActivated = true;
        Log.Write("journal", "the hidden journal window was activated");
        base.OnActivated(e);
    }

    // Shown so the browser view lays the page out, off every screen and without activation.
    public void ShowHidden()
    {
        Location = OffScreen(Size, SystemInformation.VirtualScreen);
        Show();
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    // The browser view's own windows belong to another process but are children of this one.
    bool InFront()
    {
        if (!IsHandleCreated) return false;
        var w = GetForegroundWindow();
        return w != IntPtr.Zero && (w == Handle || GetAncestor(w, 2) == Handle || GetAncestor(w, 3) == Handle);
    }

    public Task<JournalOutcome> RunAsync(JournalJob job, Func<bool> shouldStop, Func<int, string?, bool> prepare)
    {
        _job = job;
        _prepare = prepare;
        _slug = job.Slug;
        _label = $"{job.GameId} {job.Date:yyyy-MM-dd} +{job.Add}m";
        _opts = JsonSerializer.Serialize(new { date = job.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), add = job.Add, already = job.Already, told = job.Told });
        return RunCoreAsync(shouldStop);
    }

    // Saves a Mark as choice: the same page, filter, press and answer check as a journal run, with
    // the status check in place of the journal one.
    public Task<JournalOutcome> RunStatusAsync(StatusJob job, Func<bool> shouldStop)
    {
        _statusJob = job;
        _slug = job.Slug;
        _label = $"{job.GameId} Mark as {job.Pick.Describe()}";
        var p = job.Pick;
        _opts = JsonSerializer.Serialize(new { mode = "status", pick = new { status = p.StatusValue, playing = p.Playing, backlog = p.Backlog, wishlist = p.Wishlist } });
        return RunCoreAsync(shouldStop);
    }

    async Task<JournalOutcome> RunCoreAsync(Func<bool> shouldStop)
    {
        _shouldStop = shouldStop;
        _result = new TaskCompletionSource<JournalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await StartAsync(_slug);
        }
        catch (Exception ex)
        {
            _traffic.Closed = true;
            RestoreEditor();
            Log.Write("journal", $"{_label} browser view failed: {ex.Message}; {string.Join("; ", _notes)}");
            return JournalOutcome.Failed("browser view");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long deadline = RunTimeoutMs;
        var extended = false;
        while (!_result.Task.IsCompleted)
        {
            await Task.WhenAny(_result.Task, Task.Delay(500));
            if (_result.Task.IsCompleted || IsDisposed) break;
            if (Activations > 0 || InFront())
            {
                WasActivated = true;
                await StopPageAsync();
                Complete(JournalOutcome.Failed("activated"));
                break;
            }
            if (clock.ElapsedMilliseconds > deadline)
            {
                // Out of time; a Save pressed just now (or being pressed) still gets its answer, once.
                if ((_run.PressedTarget is null && !_run.IsCommitting) || extended)
                {
                    await StopPageAsync();
                    Complete(JournalOutcome.Failed("timeout"));
                    break;
                }
                extended = true;
                deadline = clock.ElapsedMilliseconds + SaveResponseMs + 10_000;
                continue;
            }
            // A game started or the screen got busy: stop, unless Save was pressed or is being pressed
            // (then the run only waits for the answer, and anything but a confirmed save carries the total).
            if (_run.PressedTarget is null && !_run.IsCommitting && shouldStop())
            {
                await StopPageAsync();
                Complete(JournalOutcome.Stopped);
            }
        }
        var outcome = _result.Task.IsCompleted ? await _result.Task : Close(JournalOutcome.Stopped);
        _traffic.Closed = true;
        // The page sets the editor cookie again after a save it saw succeed: stop it first, then put
        // the owner's value back.
        try
        {
            if (!IsDisposed && _web?.CoreWebView2 is { } page)
            {
                page.Stop();
                page.Navigate("about:blank");
                await Task.Delay(300);
            }
        }
        catch (Exception) { }
        RestoreEditor();
        // Put back after the page stopped: nothing is left to undo, so the written-down value goes.
        if (_editorRestored && CookieStore is { } done) done(null);
        if (WasActivated) outcome = outcome with { Activated = true };
        Log.Write("journal", $"{_label}: {outcome.Kind}{(outcome.Step is { } s ? $" at {s}" : "")}"
            + $"{(outcome.Target is { } t ? $", session total {t}m" : "")}{(outcome.Activated ? ", window was activated" : "")}; "
            + $"{string.Join("; ", _notes.Concat(_traffic.Notes))}; blocked {_ads?.Blocked ?? 0} ad requests");
        return outcome;
    }

    // Every outcome goes through here: a save that went out, or a commit still unanswered, keeps the total; a
    // refused save that never went out carries none.
    JournalOutcome Close(JournalOutcome outcome) => _run.Close(outcome, _traffic, _target);

    void Complete(JournalOutcome outcome)
    {
        if (_result is null || _result.Task.IsCompleted) return;
        var final = Close(outcome);
        _traffic.Closed = true;
        RestoreEditor();
        _result.TrySetResult(final);
    }

    // The page renders the log editor its game-log-editor-mode cookie names; the run needs the quick
    // one. The owner's value is kept to put back.
    async Task SwitchEditorAsync(CoreWebView2 core)
    {
        var cookies = core.CookieManager;
        // A run that was cut off left the owner's cookie switched: it goes back first.
        if (CookieLeftSwitched is { } left)
        {
            Apply(cookies, left);
            _notes.Add($"editor cookie a cut-off run left switched put back to {(left.Existed ? $"\"{Short(left.Value)}\"" : "absent")}");
            CookieLeftSwitched = null;
            if (CookieStore is { } clear) clear(null);
        }
        var found = (await cookies.GetCookiesAsync(BackloggdUri)).FirstOrDefault(c => c.Name == JournalEditorMode.Cookie);
        var plan = JournalEditorMode.Plan(found?.Value);
        if (!plan.Switch) return;
        // Written down before the switch, so a crash or a kill can't leave it switched for good.
        var saved = found is null ? new EditorCookieSaved(false)
            : new EditorCookieSaved(true, found.Value, found.Domain, found.Path, found.IsSession ? null : found.Expires, found.SameSite.ToString(), found.IsSecure, found.IsHttpOnly);
        if (CookieStore is { } store && !store(saved)) throw new InvalidOperationException("the editor cookie couldn't be written down");
        _editorCookie = found is null ? null : cookies.CopyCookie(found);
        var quick = found is null ? cookies.CreateCookie(JournalEditorMode.Cookie, JournalEditorMode.Quick, "backloggd.com", "/") : cookies.CopyCookie(found);
        quick.Value = JournalEditorMode.Quick;
        if (found is null)
        {
            quick.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            quick.Expires = DateTime.Now.AddDays(365);
        }
        _editorSwitched = true;
        cookies.AddOrUpdateCookie(quick);
        _notes.Add($"editor cookie {(found is null ? "absent" : $"\"{Short(found.Value)}\"")} set to quick for the run");
    }

    // Puts a written-down cookie back: the owner's value with its attributes, or none.
    static void Apply(CoreWebView2CookieManager cookies, EditorCookieSaved saved)
    {
        if (!saved.Existed)
        {
            cookies.DeleteCookies(JournalEditorMode.Cookie, BackloggdUri);
            return;
        }
        var c = cookies.CreateCookie(JournalEditorMode.Cookie, saved.Value ?? "", saved.Domain ?? "backloggd.com", saved.Path ?? "/");
        if (saved.Expires is { } expires) c.Expires = expires;
        if (Enum.TryParse<CoreWebView2CookieSameSiteKind>(saved.SameSite, out var sameSite)) c.SameSite = sameSite;
        c.IsSecure = saved.Secure;
        c.IsHttpOnly = saved.HttpOnly;
        cookies.AddOrUpdateCookie(c);
    }

    // At the next start, a cookie a cut-off run left switched goes back without a run.
    public async Task<bool> RestoreLeftCookieAsync(EditorCookieSaved saved)
    {
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Surface, TabStop = false };
        Controls.Add(_web);
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData);
        await _web.EnsureCoreWebView2Async(env);
        Apply(_web.CoreWebView2.CookieManager, saved);
        return true;
    }

    void RestoreEditor()
    {
        if (!_editorSwitched) return;
        try
        {
            if (_web?.CoreWebView2?.CookieManager is not { } cookies)
            {
                _notes.Add("editor cookie not restored: browser view gone");
                return;
            }
            if (_editorCookie is { } old) cookies.AddOrUpdateCookie(cookies.CopyCookie(old));
            else cookies.DeleteCookies(JournalEditorMode.Cookie, BackloggdUri);
            if (!_editorRestored) _notes.Add($"editor cookie restored to {(_editorCookie is { } o ? $"\"{Short(o.Value)}\"" : "absent")}");
            _editorRestored = true;
        }
        catch (Exception ex) { _notes.Add("editor cookie restore failed: " + ex.Message); }
    }

    async Task StopPageAsync()
    {
        try
        {
            if (_web?.CoreWebView2 is { } core) await core.ExecuteScriptAsync(StopScript);
        }
        catch (Exception) { }
    }

    async Task StartAsync(string slug)
    {
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Surface, TabStop = false };
        Controls.Add(_web);
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.IsMuted = true;
        var settings = core.Settings;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        // Not accepting a dialog leaves confirm() false: nothing the page asks is ever agreed to.
        core.ScriptDialogOpening += (_, e) => _notes.Add($"dialog {e.Kind} refused: {Short(e.Message)}");
        core.NewWindowRequested += (_, e) => { e.Handled = true; _notes.Add("new window refused"); };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        // Every request from every source (service and shared workers too). A runtime that can't
        // filter them all doesn't run the journal at all: there is no partial fallback.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        _refusal = env.CreateWebResourceResponse(null, 409, "Conflict", "Content-Type: text/plain");
        core.WebResourceRequested += OnRequest;
        // A crashed or exited browser process ends the run; a save that went out keeps its total.
        core.ProcessFailed += (_, e) =>
        {
            _notes.Add($"browser process failed: {e.ProcessFailedKind} {e.Reason}");
            Complete(JournalOutcome.Failed("browser process"));
        };
        core.WebResourceResponseReceived += OnResponse;
        core.WebMessageReceived += OnMessage;
        core.DOMContentLoaded += (_, _) => _ = PageReadyAsync();
        _ads = await AdBlock.AttachAsync(core);
        await SwitchEditorAsync(core);
        core.Navigate($"https://backloggd.com/games/{Uri.EscapeDataString(slug)}/");
    }

    static string Short(string? s) => s is null ? "" : s.Length <= 80 ? s : s[..80];

    async Task PageReadyAsync()
    {
        if (_injected || _slug.Length == 0 || _web?.CoreWebView2 is not { } core || _result is null) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !LogWindow.IsBackloggd(uri))
        {
            Complete(JournalOutcome.Failed("page"));
            return;
        }
        if (uri.AbsolutePath.StartsWith("/users/sign_in", StringComparison.OrdinalIgnoreCase))
        {
            Complete(JournalOutcome.Failed("signed out"));
            return;
        }
        if (uri.AbsolutePath.TrimEnd('/') != $"/games/{_slug}")
        {
            Complete(JournalOutcome.Failed("page"));
            return;
        }
        _injected = true;
        try { await core.ExecuteScriptAsync(Script.Replace("__OPTS__", _opts)); }
        catch (Exception ex)
        {
            _notes.Add("inject: " + ex.Message);
            Complete(JournalOutcome.Failed("inject"));
        }
    }

    // Reading the body uses up its stream, so the request gets the same bytes back: what was checked
    // is exactly what is sent.
    static (string? ContentType, byte[]? Body) ReadBody(CoreWebView2WebResourceRequest request)
    {
        var type = request.Headers.Contains("Content-Type") ? request.Headers.GetHeader("Content-Type") : null;
        var s = request.Content;
        if (s is null) return (type, null);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        request.Content = new MemoryStream(bytes, writable: false);
        return (type, bytes);
    }

    void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var request = e.Request;
            var (allow, reason) = _traffic.Request(request.Method, request.Uri, () => ReadBody(request), name => request.Headers.Contains(name));
            if (allow) return;
            Refuse(e);
            if (_traffic.Refused == reason) _saveSeen?.TrySetResult();
        }
        catch (Exception ex)
        {
            // Could not be checked, so it does not go out.
            Refuse(e);
            _notes.Add("request check failed: " + ex.Message);
            _saveSeen?.TrySetResult();
        }
    }

    // Answers the request here with the prebuilt 409, then builds the next one. If even that fails,
    // the request can't be held back, so the run ends at once and the page is torn down.
    void Refuse(CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            e.Response = _refusal ?? throw new InvalidOperationException("no refusal response");
        }
        catch (Exception ex)
        {
            _notes.Add("refuse failed: " + ex.Message);
            // The request may have gone out: treat it as sent, so the run keeps its total.
            _traffic.MaybeSent = true;
            _traffic.Closed = true;
            Complete(JournalOutcome.Failed("refuse failed"));
            try { _web?.CoreWebView2?.Stop(); _web?.CoreWebView2?.Navigate("about:blank"); }
            catch (Exception) { }
            return;
        }
        try { _refusal = _web!.CoreWebView2.Environment.CreateWebResourceResponse(null, 409, "Conflict", "Content-Type: text/plain"); }
        catch (Exception ex)
        {
            _refusal = null;
            _notes.Add("next refusal: " + ex.Message);
        }
    }

    async void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var request = e.Request;
            if (request.Method == "POST" && JournalRequest.DialogEndpoint().IsMatch(request.Uri))
            {
                if (e.Response.StatusCode is >= 200 and < 300) await MarkAsync("__dateModalLoaded");
                return;
            }
            // The log box loads from two answers: its editor HTML and its data. journal.js waits for both.
            if (request.Method == "POST" && JournalRequest.RenderEndpoint().IsMatch(request.Uri))
            {
                if (e.Response.StatusCode is >= 200 and < 300) await MarkAsync("__logBoxRendered");
                return;
            }
            // The log data is kept as the reference every playthrough and log value of the save must
            // match; read before the box counts as loaded, so it is there before Save can be pressed.
            if (request.Method == "GET" && JournalRequest.LogDataEndpoint().IsMatch(request.Uri))
            {
                if (e.Response.StatusCode is < 200 or >= 300 || _traffic.Armed) return;
                string? data = null;
                try
                {
                    using var stream = await e.Response.GetContentAsync();
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        data = await reader.ReadToEndAsync();
                    }
                }
                catch (Exception ex) { _notes.Add("log data body: " + ex.Message); }
                if (_traffic.Armed) return;
                _traffic.LogData = JournalLogData.Parse(data);
                _traffic.LogDataRead = true;
                _notes.Add($"log data {(data is null ? "unread" : $"{data.Length} chars")}{(_traffic.LogData is null ? ", unreadable" : "")}");
                await MarkAsync("__logBoxData");
                return;
            }
            if (request.Method is not ("POST" or "PUT" or "PATCH") || !_traffic.SaveSent || _traffic.ArmedGameId is not { } armedGame) return;
            var m = JournalRequest.SaveEndpoint().Match(request.Uri);
            if (!m.Success || m.Groups[2].Value != armedGame || _answers.Count > 0) return;
            var status = e.Response.StatusCode;
            var type = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type") : null;
            // Taken before the body is awaited, so a second answer can't jump the queue.
            var slot = _answers.Count;
            _answers.Add((status, type, null));
            string? body = null;
            try
            {
                using var stream = await e.Response.GetContentAsync();
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    body = await reader.ReadToEndAsync();
                }
            }
            catch (Exception ex) { _notes.Add("save answer body: " + ex.Message); }
            _answers[slot] = (status, type, body);
            _notes.Add($"save answered {status} {Short(type)} {body?.Length ?? -1} chars");
            _saveSeen?.TrySetResult();
        }
        catch (Exception ex)
        {
            _notes.Add("response: " + ex.Message);
        }
    }

    // The page fills the log box and the session dialog from these answers; journal.js waits for the counts.
    async Task MarkAsync(string counter)
    {
        try
        {
            if (_web?.CoreWebView2 is { } core) await core.ExecuteScriptAsync($"window.{counter} = (window.{counter} || 0) + 1;");
        }
        catch (Exception ex) { _notes.Add($"{counter}: " + ex.Message); }
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { await HandleMessageAsync(e); }
        catch (Exception ex)
        {
            _notes.Add("message: " + ex.Message);
            Complete(JournalOutcome.Failed("message handling"));
        }
    }

    async Task HandleMessageAsync(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_result is null || _result.Task.IsCompleted || (_job is null && _statusJob is null) || !LogWindow.IsBackloggd(e.Source)) return;
        string json;
        try { json = e.WebMessageAsJson; }
        catch (Exception) { return; }
        var r = JournalScriptResult.Parse(json);
        // One message per run: a second one (any script on the page could post it) is ignored.
        if (!_run.TakeMessage())
        {
            _notes.Add($"ignored a second message ({r.Kind} {r.Step})");
            return;
        }
        _notes.Add($"script {r.Kind} {r.Step}");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "timing", "error", "fields", "count", "dialogDate" })
                    if (root.TryGetProperty(name, out var v)) _notes.Add($"{name} {Short(v.GetRawText())}");
        }
        catch (JsonException) { }
        if (_statusJob is { } statusJob)
        {
            await HandleStatusAsync(r, statusJob);
            return;
        }
        var job = _job!;
        switch (r.Kind)
        {
            case JournalScriptKind.StatusReady:
                Complete(JournalOutcome.Failed("message"));
                return;
            case JournalScriptKind.Covered:
                // The told minutes are in the session and nothing was played since: only for this
                // day's session as Backloggd sent it, after a notice.
                var held = r.Current ?? -1;
                var coveredStep = held < 0 ? "covered total"
                    : r.SessionId is null ? "session id on the page missing"
                    : !job.ToldThere(held, false) || job.Add > job.Told ? "covered without the told time"
                    : JournalRequest.CheckDialog(_traffic.DialogRequests, job.Date, held, r.SessionId, covering: r.Continued).Step;
                Complete(coveredStep is null ? JournalOutcome.Covered(held, job.Told) : JournalOutcome.Failed(coveredStep));
                return;
            case JournalScriptKind.Already:
                // Only when the dialog it read is this day's session as Backloggd sent it.
                // And only for the session the unconfirmed save was for.
                var step = r.Current is not { } c ? "already total"
                    : r.SessionId is null ? "session id on the page missing"
                    : job.TriedSessionChanged(r.SessionId) ? $"tried session changed ({job.AlreadySession} then, {r.SessionId} now)"
                    : JournalRequest.CheckDialog(_traffic.DialogRequests, job.Date, c, r.SessionId, covering: r.Continued).Step;
                Complete(step is null ? JournalOutcome.Already : JournalOutcome.Failed(step));
                return;
            case JournalScriptKind.Unsupported:
                Complete(JournalOutcome.Unsupported(r.Step));
                return;
            case JournalScriptKind.Failed:
                Complete(r.Step == "stopped" ? JournalOutcome.Stopped : JournalOutcome.Failed(r.Step));
                return;
        }

        // Ready. The dialog the script edited must be the one Backloggd sent for this day's session,
        // and the log box a real log that fits the game.
        var target = r.Target!.Value;
        var (dialogStep, dialog) = JournalRequest.CheckDialog(_traffic.DialogRequests, job.Date, r.Current!.Value, r.SessionId, covering: r.Continued);
        string? why = _traffic.Refused
            ?? _traffic.CheckLogBox(r.GameId!)
            // Journal time never creates a log.
            ?? (_traffic.LogData?.NoLog == true ? "no log" : null)
            ?? (r.SessionId is null ? "session id on the page missing" : null)
            ?? (_traffic.LogData?.Log.GetValueOrDefault("status") is { } loaded && loaded != r.Log.GetValueOrDefault("status") ? "log box status differs from the log data" : null)
            ?? dialogStep
            // A session the script made for an empty day must be a page-made one on a day Backloggd has
            // no session for; an existing one must be a saved session.
            ?? (r.NewSession ? JournalRequest.CheckNewSession(_traffic.LogData, job.Date, r.PlaythroughId, dialog!.Id)
                : dialog!.Id.StartsWith('-') ? "session id negative" : null)
            // A session from an earlier day must cover this one in Backloggd's own data.
            ?? (r.Continued && !r.NewSession ? JournalRequest.CheckCovering(_traffic.LogData, job.Date, dialog!.Id) : null)
            // A rerun after an unconfirmed save adds to the same session or not at all.
            ?? (job.TriedSessionChanged(r.SessionId) ? $"tried session changed ({job.AlreadySession} then, {r.SessionId} now)" : null)
            // Sync sessions ends up on for the session's playthrough.
            ?? JournalRequest.CheckSync(_traffic.LogData, r.NewSession ? r.PlaythroughId : _traffic.LogData?.Session(dialog!.Id)?.PlaythroughId, r.PlaythroughId, r.SyncTurnedOn)
            // After a notice the script adds only play since it when the told part is there.
            ?? (r.ToldThere != job.ToldThere(r.Current.Value, r.NewSession) ? "told time check" : null)
            ?? (target != r.Current.Value + job.AddFor(r.Current.Value, r.NewSession) || job.AddFor(r.Current.Value, r.NewSession) <= 0 ? "ready total" : null)
            ?? JournalRequest.LogFits(job.Status, r.Log, _traffic.LogData?.Log.GetValueOrDefault("status"));
        if (why is not null)
        {
            Complete(JournalOutcome.Failed(why));
            return;
        }
        if (r.ToldThere) _notes.Add($"told time already there after a notice (session {r.Current.Value}m, told {job.Told}m); adding {target - r.Current.Value}m played since");
        if (Activations > 0 || InFront())
        {
            WasActivated = true;
            Complete(JournalOutcome.Failed("activated"));
            return;
        }
        if (_shouldStop())
        {
            Complete(JournalOutcome.Stopped);
            return;
        }
        if (!_prepare(target, dialog!.Id))
        {
            Complete(JournalOutcome.Failed("state not written"));
            return;
        }

        // The total is on disk. The save may go out from here, so the traffic check knows what it
        // must carry before the page is asked to press.
        _target = target;
        _traffic.Arm(new JournalExpect(r.GameId!, job.Date, r.Current.Value, target, dialog!, r.SessionBoxes, _traffic.LogData, r.CoverId,
            r.NewSession ? r.PlaythroughId : null, r.SyncTurnedOn, r.Continued && !r.NewSession));
        var toldThere = r.ToldThere;
        await PressAndConfirmAsync(target, () => JournalOutcome.Saved(target) with { ToldThere = toldThere });
    }

    // A Mark as run's message. Ready: the box shows the picks; nothing else moved (journal.js checked).
    async Task HandleStatusAsync(JournalScriptResult r, StatusJob job)
    {
        switch (r.Kind)
        {
            case JournalScriptKind.Failed:
                // No log on Backloggd yet: saving would create one, which needs facts not confirmed live.
                Complete(r.Step == "stopped" ? JournalOutcome.Stopped
                    : r.Step == "no log" ? JournalOutcome.Unsupported("new log not supported yet")
                    : JournalOutcome.Failed(r.Step));
                return;
            case JournalScriptKind.Unsupported:
                Complete(JournalOutcome.Unsupported(r.Step));
                return;
            case not JournalScriptKind.StatusReady:
                Complete(JournalOutcome.Failed("message"));
                return;
        }
        var p = job.Pick;
        string? Mismatch()
        {
            if (p.StatusValue is { } st && r.After.GetValueOrDefault("status") != st) return "status not set on the page";
            foreach (var (name, want) in new[] { ("is_playing", p.Playing), ("is_backlog", p.Backlog), ("is_wishlist", p.Wishlist) })
                if (want is { } on && r.After.GetValueOrDefault(name) != (on ? "true" : "false")) return $"{name} not set on the page";
            return null;
        }
        // A new log only when the button, the box and Backloggd's log data all say there is none.
        string? NewLogState() => _traffic.LogData is not { } data ? null
            : r.NewLog ? (!data.NoLog ? "the page offers a new log, the log data has one"
                : r.PlaythroughId != "-1" || r.LogId != "" || r.SaveLabel != "Create Log" ? "new log box state" : null)
            : data.NoLog ? "the log data has no log, the page shows one" : null;
        var why = _traffic.Refused
            ?? _traffic.CheckLogBox(r.GameId!)
            ?? NewLogState()
            ?? (_traffic.LogData?.Log.GetValueOrDefault("status") is { } loaded && loaded != r.Log.GetValueOrDefault("status") ? "log box status differs from the log data" : null)
            ?? Mismatch();
        if (why is not null)
        {
            Complete(JournalOutcome.Failed(why));
            return;
        }
        if (Activations > 0 || InFront())
        {
            WasActivated = true;
            Complete(JournalOutcome.Failed("activated"));
            return;
        }
        if (_shouldStop())
        {
            Complete(JournalOutcome.Stopped);
            return;
        }
        _traffic.ArmStatus(new StatusExpect(r.GameId!, p, _traffic.LogData, r.CoverId, r.NewLog, r.Log.GetValueOrDefault("status")));
        if (r.NewLog) _notes.Add("no log on Backloggd: this save creates it");
        // What the app records after the save: the status and Playing toggle the box now shows.
        await PressAndConfirmAsync(0, () => new JournalOutcome(JournalOutcomeKind.Saved)
        {
            SavedStatus = r.After.GetValueOrDefault("status"), SavedPlaying = r.After.GetValueOrDefault("is_playing") == "true",
        });
    }

    // Asks the page to press Save (a time limit, an unclear answer counts as pressed), then waits for
    // Backloggd's answer to the one checked save.
    async Task PressAndConfirmAsync(int target, Func<JournalOutcome> saved)
    {
        _run.Committing(target);
        var core = _web!.CoreWebView2;
        var commit = await JournalCommitResult.RunAsync(() => core.ExecuteScriptAsync(CommitScript), CommitLimit);
        _notes.Add($"commit {(commit.Pressed ? "pressed" : "refused")} {commit.Step}");
        // The run may have ended meanwhile (focus, the deadline, a browser crash): that outcome stands.
        if (_result!.Task.IsCompleted) return;
        _run.Answered(commit.Pressed || _traffic.SaveSent);
        if (!commit.Pressed && !_traffic.SaveSent)
        {
            _target = null;
            Complete(commit.Step == "stopped" ? JournalOutcome.Stopped : JournalOutcome.Failed(commit.Step));
            return;
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (_traffic.Refused is { } refused)
            {
                Complete(JournalOutcome.Failed(refused));
                return;
            }
            if (_answers.Count > 0 && _answers[0] is var (status, type, body) && (body is not null || status is < 200 or >= 300 || waited.ElapsedMilliseconds > SaveResponseMs))
            {
                var bad = JournalRequest.CheckSaveAnswer(status, type, body, _traffic.SignInSeen);
                Complete(bad is null ? saved() : JournalOutcome.Failed(bad));
                return;
            }
            var left = SaveResponseMs - (int)waited.ElapsedMilliseconds;
            if (left <= 0 || IsDisposed)
            {
                Complete(JournalOutcome.Failed(_answers.Count > 0 ? "save answer unreadable" : "save response"));
                return;
            }
            _saveSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.WhenAny(_saveSeen.Task, Task.Delay(Math.Min(left, 1000)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete(JournalOutcome.Stopped);
            _traffic.Closed = true;
            // Closing (the app exiting): stop the page, which sets the cookie after a save it saw,
            // then put it back once more. The written-down value stays, so the next start checks again.
            try { _web?.CoreWebView2?.Stop(); }
            catch (Exception) { }
            RestoreEditor();
            _web?.Dispose();
        }
        base.Dispose(disposing);
    }
}
