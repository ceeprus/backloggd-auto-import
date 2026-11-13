using System.Text.Json;
using System.Text.RegularExpressions;
using BackloggdLogger.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BackloggdLogger;

sealed partial class LogWindow : ThemedWindow
{
    [GeneratedRegex(@"^/games/([^/]+)/?$")]
    private static partial Regex GamePath();

    // The log box's save request; group 2 is the game id. JournalWindow confirms its saves with it too.
    [GeneratedRegex(@"^https://(www\.)?backloggd\.com/api/user/\d+/log/(\d+)/?(\?.*)?$")]
    internal static partial Regex SaveEndpoint();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();

    static bool IsSlug([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? s) => s is not null && SlugPattern().IsMatch(s);

    internal static bool IsBackloggd(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host is "backloggd.com" or "www.backloggd.com";

    internal static bool IsBackloggd(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && IsBackloggd(u);

    static readonly string Script = LoadScript("prefill.js");
    static readonly string MatchScript = LoadScript("match.js");

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Surface };
    public const string OpeningText = "Opening Backloggd...";

    readonly EmptyState _loading = new(Theme.Surface) { Message = OpeningText };
    // One height in every state: a 30 px button row, 12 above and below, and the 1 px line.
    public const int BarHeight = Theme.ButtonHeight + 2 * Theme.S3 + 1;

    readonly TableLayoutPanel _bar = new()
    {
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = BarHeight,
        ColumnCount = 2,
        RowCount = 1,
        Visible = false,
        BackColor = Theme.Surface,
        Padding = new Padding(Theme.S3, Theme.S3 - Theme.FocusPad, Theme.S3 - Theme.FocusPad, Theme.S3 - Theme.FocusPad + 1),
    };
    readonly Label _barText = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        ForeColor = Theme.Text,
        Font = Theme.Body,
        Margin = new Padding(0, 0, Theme.S3, 0),
    };
    readonly FlowLayoutPanel _barButtons = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Anchor = AnchorStyles.Right,
        BackColor = Theme.Surface,
        Margin = new Padding(0),
    };
    readonly ThemedButton _retry = new("Retry", ButtonKind.Secondary) { Visible = false };
    readonly ThemedButton _notListed = new("Not on Backloggd", ButtonKind.Secondary) { Visible = false };
    readonly ThemedButton _getWebView = new("Get WebView2", ButtonKind.Secondary) { Visible = false };
    readonly ThemedButton _change = new("Change game", ButtonKind.Secondary) { Visible = false };
    readonly ThemedButton _finish = new("Done", ButtonKind.Primary) { Visible = false };
    readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 8000 };
    string? _notice;
    string _textBeforeNotice = "";
    bool _barBeforeNotice;
    bool _loaded;
    bool _initFailed;

    readonly LogRequest? _request;
    readonly GameRecord? _game;
    readonly Action<string, string?> _slugFound;
    readonly Action _notOnBackloggd;
    readonly Action<string?, bool, bool> _saved;
    bool _prefilled;
    bool _done;
    bool _picking;
    bool _pickSawSearch;
    bool _matchInjected;
    bool _matchFailed;
    string? _formGameId;
    string? _landedSlug;
    string? _matchSlug;
    string? _matchTitle;
    string? _matchYear;
    readonly bool _matchMode;
    readonly IReadOnlyList<SearchCandidate> _candidates = Array.Empty<SearchCandidate>();
    readonly string _pickQuery = "";
    int _candidate;
    readonly System.Diagnostics.Stopwatch _searchClock = new();
    const int SearchBudgetMs = 20000;
    const int CandidateWaitMs = 8000;
    string? _matchedSlug;
    Task<string?>? _matchedTitle;

    // Time since the window was made, for the step timings written to prefill.log.
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly List<string> _marks = new();
    string? _loggingLabel;

    void Mark(string step)
    {
        if (_request is not null && _marks.Count < 40) _marks.Add($"{step} {_clock.ElapsedMilliseconds}");
    }

    public const string WebView2Page = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public LogWindow(LogRequest? request, GameRecord? game, Action<string, string?> slugFound, Action notOnBackloggd, Action<string?, bool, bool> saved)
    {
        _request = request;
        _game = game;
        _slugFound = slugFound;
        _notOnBackloggd = notOnBackloggd;
        _saved = saved;
        _matchMode = request is null && game is not null;
        if (game is not null)
        {
            var c = GameNames.SearchPlan(game.DisplayName);
            _candidates = c.Count > 0 ? c : new[] { new SearchCandidate(game.DisplayName, false) };
            var pick = GameNames.PickQuery(game.DisplayName);
            _pickQuery = pick.Length > 0 ? pick : game.DisplayName;
        }

        Text = game is null ? "Backloggd" : $"Backloggd | {game.DisplayName}";
        ClientSize = new Size(960, 780);

        foreach (var b in new[] { _getWebView, _retry, _notListed, _change, _finish })
        {
            b.Margin = new Padding(Theme.S2 - Theme.FocusPad * 2, 0, 0, 0);
            _barButtons.Controls.Add(b);
        }
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _bar.Controls.Add(_barText, 0, 0);
        _bar.Controls.Add(_barButtons, 1, 0);
        _bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, _bar.Height - 1, _bar.Width, _bar.Height - 1);
        };
        Controls.Add(_loading);
        Controls.Add(_web);
        Controls.Add(_bar);
        _web.SizeChanged += (_, _) => _loading.Bounds = _web.Bounds;
        _web.LocationChanged += (_, _) => _loading.Bounds = _web.Bounds;

        _getWebView.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WebView2Page) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("errors", "open WebView2 page: " + ex); }
        };
        _retry.Click += (_, _) =>
        {
            if (_request is not null) ShowBar(OpeningText);
            else HideBar();
            if (_initFailed)
            {
                _initFailed = false;
                if (!_loaded)
                {
                    _loading.Message = "Opening Backloggd...";
                    _loading.Visible = true;
                }
                // A failure after the browser view was set up only needs the page again.
                if (_web.CoreWebView2 is null)
                {
                    _ = SafeInitAsync();
                    return;
                }
            }
            _prefilled = false;
            _formGameId = null;
            _candidate = 0;
            _searchClock.Reset();
            _matchInjected = false;
            _matchFailed = false;
            _web.CoreWebView2?.Navigate(_picking ? PickUrl() : TargetUrl());
        };
        _finish.Click += (_, _) => Close();
        _notListed.Click += (_, _) => { _notOnBackloggd(); Close(); };
        _change.Click += (_, _) =>
        {
            _picking = true;
            _pickSawSearch = false;
            _prefilled = false;
            _formGameId = null;
            ShowBar("Pick the right game or edition.", notListed: true);
            _web.CoreWebView2?.Navigate(PickUrl());
        };
        _noticeTimer.Tick += (_, _) => EndNotice();
        Load += async (_, _) => await SafeInitAsync();
        // A log window says what it is doing in the bar until the log box is filled in.
        if (request is not null)
        {
            _loading.Message = "";
            ShowBar(OpeningText);
        }
        EndBuild();
    }

    protected override bool CloseOnEscape => false;

    void Loaded()
    {
        if (_loaded) return;
        _loaded = true;
        _loading.Visible = false;
    }

    internal static string LoadScript(string name)
    {
        using var s = typeof(LogWindow).Assembly.GetManifestResourceStream("BackloggdLogger." + name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    async Task SafeInitAsync()
    {
        try
        {
            await InitAsync();
        }
        catch (Exception ex)
        {
            Log.Write("errors", "log window init failed: " + ex);
            if (IsDisposed) return;
            _loading.Message = "Couldn't start the browser view.";
            ShowInitFailed();
        }
    }

    string TargetUrl()
    {
        if (_game is null) return "https://backloggd.com/";
        if (_matchMode) return PickUrl();
        if (_request is null) return "https://backloggd.com/";
        if (!IsSlug(_game.Slug)) return SearchUrl(_candidate);
        return $"https://backloggd.com/games/{_game.Slug}/";
    }

    static string SearchUrlFor(string query) => "https://backloggd.com/search/games/" + Uri.EscapeDataString(query) + "/";

    string SearchUrl(int candidate) => SearchUrlFor(_candidates[Math.Clamp(candidate, 0, _candidates.Count - 1)].Query);

    string PickUrl() => SearchUrlFor(_pickQuery);

    int SearchTimeLeft() => SearchBudgetMs - (int)_searchClock.ElapsedMilliseconds;

    // The title the search match found for slug ("Hollow Knight (2017)"), or null.
    string? MatchLabel(string slug) => slug == _matchSlug ? Label(_matchTitle, _matchYear) : null;

    internal static string? Label(string? title, string? year)
    {
        title = title?.Trim();
        year = year?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        return string.IsNullOrEmpty(year) ? title : $"{title} ({year})";
    }

    public static string LoggingText(string label) => $"Logging {label}.";

    public static string MatchedText(string label) => $"Matched to {label}.";

    // A game page's title is its h1 and its release year ("Hollow Knight", "2017"); the tab title
    // reads "Hollow Knight (2017)" and is the fallback.
    const string PageTitleScript =
        "(() => { const h = document.querySelector('.game-title-section h1'); const y = document.querySelector('.game-subtitle a.game-year, a.game-year'); " +
        "return JSON.stringify({ title: h ? h.textContent : null, year: y ? y.textContent : null, doc: document.title || null }); })()";

    [GeneratedRegex(@"^(.+?) \((\d{4})\)$")]
    private static partial Regex DocTitle();

    internal static string? PageLabel(string? h1, string? year, string? docTitle)
    {
        if (Label(h1, year is { } y && Regex.IsMatch(y.Trim(), @"^\d{4}$") ? y : null) is { } fromPage) return fromPage;
        var m = DocTitle().Match(docTitle?.Trim() ?? "");
        return m.Success ? Label(m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    // The title of the game page that is open now, or null when it can't be read.
    async Task<string?> PageTitleAsync()
    {
        try
        {
            if (_web.CoreWebView2 is null) return null;
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(PageTitleScript);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return PageLabel(Read("title"), Read("year"), Read("doc"));
        }
        catch (Exception ex)
        {
            Log.Write("errors", $"page title read failed for {_game?.Id}: {ex.Message}");
            return null;
        }
    }

    async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData);
        Mark("environment");
        await _web.EnsureCoreWebView2Async(env);
        Mark("view");
        var core = _web.CoreWebView2;
        // The log box is opened as soon as the page's DOM is ready, not after every image and
        // script on the page has loaded (NavigationCompleted, often seconds later).
        core.DOMContentLoaded += (_, _) => { Mark("dom"); Loaded(); TrackLanding(); _ = CheckPageAsync(0); };
        core.NavigationCompleted += (_, _) => { Mark("loaded"); Loaded(); TrackLanding(); _ = CheckPageAsync(0); };
        // A new document gets DOMContentLoaded; a page that changes its address in place (Turbo)
        // gets no such event, so it is checked once its content has been swapped in.
        core.SourceChanged += (_, e) => { TrackLanding(); if (!e.IsNewDocument) _ = CheckPageAsync(1000); };
        core.WebMessageReceived += OnMessage;
        core.WebResourceResponseReceived += OnResponse;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsBackloggd(e.Uri)) core.Navigate(e.Uri);
        };
        await SiteFixes.AttachAsync(core);
        if (BlockAds) _ads = await AdBlock.AttachAsync(core);

        Mark("navigate");
        core.Navigate(TargetUrl());
    }

    // Set before the window is shown, from the Block ads setting; a change applies to the next
    // window. Off: nothing is blocked or hidden, and prefill.log gets no "blocked" line.
    internal bool BlockAds;

    AdBlock? _ads;

    async Task CheckPageAsync(int delayMs)
    {
        if (_matchMode)
        {
            await CheckMatchModeAsync(delayMs);
            return;
        }
        if (_request is null || _game is null || _prefilled) return;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (IsDisposed || _prefilled || _web.CoreWebView2 is null) return;

            var uri = new Uri(_web.CoreWebView2.Source);
            if (!IsBackloggd(uri)) return;
            if (uri.AbsolutePath.StartsWith("/users/sign_in", StringComparison.OrdinalIgnoreCase))
            {
                ShowBar("Sign in to Backloggd, then press Retry.", retry: true);
                return;
            }

            var m = GamePath().Match(uri.AbsolutePath);
            var isSearch = uri.AbsolutePath.StartsWith("/search/games/", StringComparison.OrdinalIgnoreCase);
            if (!m.Success || !IsSlug(m.Groups[1].Value))
            {
                if (_picking)
                {
                    if (isSearch) _pickSawSearch = true;
                    ShowBar("Pick the right game or edition.", notListed: true);
                }
                else if (!IsSlug(_game.Slug))
                {
                    if (_matchFailed) ShowBar($"Couldn't find \"{_game.DisplayName}\". Pick the game from the results.", notListed: true);
                    else if (isSearch)
                    {
                        if (!_matchInjected)
                        {
                            _matchInjected = true;
                            ShowBar($"Finding \"{_game.DisplayName}\" on Backloggd...");
                            try
                            {
                                if (!_searchClock.IsRunning) _searchClock.Start();
                                var c = _candidates[_candidate];
                                var wait = Math.Clamp(SearchTimeLeft(), 1000, CandidateWaitMs);
                                await _web.CoreWebView2.ExecuteScriptAsync(
                                    MatchScript.Replace("__OPTS__", JsonSerializer.Serialize(new { name = c.Query, exact = c.Exact, wait })));
                            }
                            catch (Exception ex)
                            {
                                _matchInjected = false;
                                Log.Write("errors", $"match inject failed for {_game.Id}: {ex}");
                                if (!IsDisposed) ShowBar($"Couldn't search Backloggd for \"{_game.DisplayName}\". Pick the game from the results.", retry: true, notListed: true);
                            }
                        }
                    }
                    else ShowBar($"Open the right game for \"{_game.DisplayName}\" from the results.", notListed: true);
                }
                return;
            }

            var slug = m.Groups[1].Value;
            if (_picking && !_pickSawSearch) return;
            var store = !IsSlug(_game.Slug) || _picking;
            _picking = false;
            _prefilled = true;
            var label = MatchLabel(slug) ?? await PageTitleAsync();
            if (IsDisposed) return;
            // A game matched before titles were kept gets its title now.
            if (store || (label is not null && slug == _game.Slug && _game.BackloggdTitle is null)) _slugFound(slug, label);
            _loggingLabel = label ?? (slug == _game.Slug ? _game.BackloggdTitle : null) ?? slug;
            ShowBar(OpeningText);
            Mark("prefill");
            var opts = JsonSerializer.Serialize(new
            {
                status = _request.Status?.ToString().ToLowerInvariant(),
                playing = _request.Playing,
                backlog = _request.Backlog,
                wishlist = _request.Wishlist,
                started = _request.StartedOn is { } s ? Format.BackloggdDate(s) : null,
                finished = _request.FinishedOn is { } f ? Format.BackloggdDate(f) : null
            });
            await _web.CoreWebView2.ExecuteScriptAsync(Script.Replace("__OPTS__", opts));
        }
        catch (Exception ex)
        {
            _prefilled = false;
            Log.Write("errors", $"prefill failed for {_game.Id}: {ex}");
            if (!IsDisposed) ShowBar(PrefillFailed, retry: true, change: true);
        }
    }

    async Task CheckMatchModeAsync(int delayMs)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (IsDisposed || _web.CoreWebView2 is null || _game is null) return;
            var uri = new Uri(_web.CoreWebView2.Source);
            if (!IsBackloggd(uri)) return;
            if (uri.AbsolutePath.StartsWith("/users/sign_in", StringComparison.OrdinalIgnoreCase))
            {
                ShowBar("Sign in to Backloggd, then press Retry.", retry: true);
                return;
            }
            var m = GamePath().Match(uri.AbsolutePath);
            if (m.Success && IsSlug(m.Groups[1].Value))
            {
                var slug = m.Groups[1].Value;
                if (slug != _matchedSlug || _matchedTitle is null)
                {
                    _matchedSlug = slug;
                    _matchedTitle = SaveMatchAsync(_game, slug);
                }
                var title = await _matchedTitle;
                if (IsDisposed || slug != _matchedSlug) return;
                ShowBar(MatchedText(title ?? slug), finish: true);
                return;
            }
            ShowBar($"Pick the right game for \"{_game.DisplayName}\".", notListed: true);
        }
        catch (Exception ex)
        {
            Log.Write("errors", $"match mode check failed for {_game?.Id}: {ex}");
        }
    }

    // Stores the match with the page's title (null when it can't be read, so no slug is kept as a title).
    async Task<string?> SaveMatchAsync(GameRecord game, string slug)
    {
        var title = await PageTitleAsync();
        if (IsDisposed || slug != _matchedSlug) return title;
        _slugFound(slug, title);
        Log.Write("prefill", $"{game.Id} match mode saved slug {slug}{(title is null ? "" : $" as {title}")}");
        return title;
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsBackloggd(e.Source)) return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();
            if (type == "match")
            {
                if (_matchMode || _picking || _game is null || IsSlug(_game.Slug)) return;
                var slug = root.TryGetProperty("slug", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                if (!IsSlug(slug))
                {
                    if (root.TryGetProperty("error", out var err)) Log.Write("errors", $"match failed for {_game.Id}: {err}");
                    var empty = root.TryGetProperty("empty", out var ep) && ep.ValueKind == JsonValueKind.True;
                    var tried = _candidates[_candidate];
                    Log.Write("prefill", $"{_game.Id} no {(tried.Exact ? "exact " : "")}match for candidate {_candidate + 1}/{_candidates.Count} \"{tried.Query}\"{(empty ? ", no results" : "")}");
                    if (_candidate + 1 < _candidates.Count && SearchTimeLeft() > 1000)
                    {
                        _candidate++;
                        _matchInjected = false;
                        ShowBar($"Finding \"{_game.DisplayName}\" on Backloggd...");
                        _web.CoreWebView2.Navigate(SearchUrl(_candidate));
                        return;
                    }
                    _matchFailed = true;
                    ShowBar($"Couldn't find \"{_game.DisplayName}\". Pick the game from the results.", notListed: true);
                    return;
                }
                Log.Write("prefill", $"{_game.Id} matched {slug} with candidate {_candidate + 1}/{_candidates.Count} \"{_candidates[_candidate].Query}\"");
                _matchSlug = slug;
                _matchTitle = root.TryGetProperty("title", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
                _matchYear = root.TryGetProperty("year", out var yp) && yp.ValueKind == JsonValueKind.String ? yp.GetString() : null;
                _slugFound(slug, MatchLabel(slug));
                _web.CoreWebView2.Navigate($"https://backloggd.com/games/{Uri.EscapeDataString(slug)}/");
                return;
            }
            if (type == "logform")
            {
                if (root.TryGetProperty("gameId", out var lg) && lg.ValueKind == JsonValueKind.String) _formGameId = lg.GetString();
                return;
            }
            if (type == "needLogin")
            {
                _prefilled = false;
                ShowBar("Sign in to Backloggd, then press Retry.", retry: true, change: true);
                return;
            }
            if (type != "prefill") return;

            if (root.TryGetProperty("gameId", out var gp) && gp.ValueKind == JsonValueKind.String && gp.GetString() is { Length: > 0 } fid) _formGameId = fid;
            if (!root.TryGetProperty("missing", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var missing = arr.EnumerateArray().Select(x => x.GetString()).ToList();
            Mark("filled");
            var timing = root.TryGetProperty("timing", out var tm) && tm.ValueKind == JsonValueKind.Object ? tm.GetRawText() : "{}";
            Log.Write("prefill", $"{_game?.Id} timing ms since the window opened: {string.Join(", ", _marks)}; in the page: {timing}");
            if (missing.Count == 0)
            {
                ShowBar(LoggingText(_loggingLabel ?? _game?.DisplayName ?? ""), change: true);
                return;
            }
            Log.Write("prefill", $"{_game?.Id} {_web.CoreWebView2.Source} missing: {string.Join(", ", missing)}");
            ShowBar(PrefillFailed, retry: true, change: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Log.Write("errors", "bad web message: " + ex.Message);
        }
    }

    public const string PrefillFailed = "Couldn't fill in the log form. Fill it in by hand, or press Retry.";

    public const string InitFailed = "Couldn't start the browser view. Install the WebView2 Runtime, then press Retry.";

    const string ReadBackScript =
        "(() => { const modal = document.querySelector('#journal-game-modal'); " +
        "return JSON.stringify({ found: !!modal, status: (modal && modal.querySelector('input#status')?.value) || null, " +
        "playing: !!(modal && modal.querySelector('#playing_toggle_checkbox')?.checked) }); })()";

    async Task<(bool Found, string? Status, bool Playing)> ReadBackAsync()
    {
        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(ReadBackScript);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null) return (false, null, false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var found = root.TryGetProperty("found", out var fp) && fp.ValueKind == JsonValueKind.True;
            var status = root.TryGetProperty("status", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
            var playing = root.TryGetProperty("playing", out var pp) && pp.ValueKind == JsonValueKind.True;
            return (found, status, playing);
        }
        catch (Exception ex)
        {
            Log.Write("errors", $"save read-back failed for {_game?.Id}: {ex.Message}");
            return (false, null, false);
        }
    }

    async void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_request is null || _done) return;
        if (e.Request.Method is not ("POST" or "PUT" or "PATCH")) return;
        var m = SaveEndpoint().Match(e.Request.Uri);
        if (!m.Success) return;
        if (e.Response.StatusCode is < 200 or >= 300) return;
        var idMatches = _formGameId is not null
            ? m.Groups[2].Value == _formGameId
            : _game is not null && CurrentPath() == $"/games/{_game.Slug}/";
        if (!idMatches)
        {
            Log.Write("saves", $"ignored {_game?.Id} {e.Request.Method} {e.Request.Uri} form={_formGameId}");
            return;
        }
        _done = true;
        Log.Write("saves", $"{_game?.Id} {e.Request.Method} {e.Request.Uri} {e.Response.StatusCode}");
        var (found, status, playing) = await ReadBackAsync();
        Log.Write("saves", found
            ? $"{_game?.Id} read-back status={status ?? "null"} playing={playing}"
            : $"{_game?.Id} read-back found no log box, using the request");
        try { _saved(status, playing, found); }
        catch (Exception ex) { Log.Write("errors", "saved callback: " + ex); }
        if (!IsDisposed) BeginInvoke(new Action(Close));
    }

    void TrackLanding()
    {
        var m = GamePath().Match(CurrentPath() is { } p ? p.TrimEnd('/') : "");
        var slug = m.Success ? m.Groups[1].Value : null;
        if (slug is not null && slug != _landedSlug) _formGameId = null;
        _landedSlug = slug;
    }

    string? CurrentPath()
    {
        var src = _web.CoreWebView2?.Source;
        return Uri.TryCreate(src, UriKind.Absolute, out var u) ? (u.AbsolutePath.EndsWith('/') ? u.AbsolutePath : u.AbsolutePath + "/") : null;
    }

    void ShowInitFailed()
    {
        _initFailed = true;
        ShowBar(InitFailed, retry: true);
        _getWebView.Visible = true;
        _barButtons.Controls.SetChildIndex(_getWebView, 0);
    }

    void ShowBar(string text, bool retry = false, bool notListed = false, bool change = false, bool finish = false)
    {
        _noticeTimer.Stop();
        _notice = null;
        _getWebView.Visible = false;
        _finish.Visible = finish;
        _barText.Text = text;
        _retry.Visible = retry;
        _notListed.Visible = notListed;
        _change.Visible = change;
        _bar.Visible = true;
    }

    void HideBar() => _bar.Visible = false;

    public void Notice(string text)
    {
        if (_notice is null)
        {
            _textBeforeNotice = _barText.Text;
            _barBeforeNotice = _bar.Visible;
            if (!_bar.Visible)
                foreach (Control b in _barButtons.Controls) b.Visible = false;
        }
        _notice = text;
        _barText.Text = text;
        _bar.Visible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    void EndNotice()
    {
        _noticeTimer.Stop();
        if (_notice is not null && _barText.Text == _notice)
        {
            _barText.Text = _textBeforeNotice;
            _bar.Visible = _barBeforeNotice;
        }
        _notice = null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_ads is { } ads) Log.Write("prefill", $"{_game?.Id ?? "browse"} blocked {ads.Blocked} ad requests");
        _noticeTimer.Dispose();
        base.OnFormClosed(e);
    }
}
