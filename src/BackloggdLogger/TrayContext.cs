using BackloggdLogger.Core;

namespace BackloggdLogger;

sealed class TrayContext : ApplicationContext
{
    readonly StateStore _store = new(Paths.State);
    readonly AppState _state;
    readonly PopupPrompter _prompter;
    readonly Coordinator _coordinator;
    readonly LogWindowHost _windows;
    readonly GameWatcher _watcher;
    readonly JournalSync _journal;
    readonly NotifyIcon _tray;
    WindowPicker? _picker;
    TrackedGamesForm? _tracked;
    SettingsForm? _settings;
    MissedPopupsForm? _missed;
    ToolStripMenuItem? _missedItem;
    ToolStripMenuItem? _pauseItem;
    ToolStripMenuItem? _startupItem;

    public TrayContext()
    {
        try { _state = _store.Load(); }
        catch (Exception ex)
        {
            Log.Write("errors", "state load failed: " + ex);
            _state = new AppState();
            _store.MarkLoadFailed();
        }
        _prompter = new PopupPrompter((id, answer, start) =>
        {
            try { _coordinator!.Answer(id, answer, start); }
            catch (Exception ex) { Log.Write("errors", "answer: " + ex); }
        }, (id, status, playing, backlog, wishlist) => _coordinator!.MarkAs(id, status, playing, backlog, wishlist), () => _state.Settings)
        {
            Missed = m => _state.RecordMissed(m, DateTime.Now),
            Forget = id => _state.MissedPopups.Remove(id),
            OpenLog = OpenLogFor,
            OpenFolder = ShowInFolder,
            KeepPick = KeepPickFor,
        };
        _state.MissedPopups.Changed += MissedChanged;
        _windows = new LogWindowHost(_state, Save, () => _watcher?.AnyGameRunning ?? false);
        _coordinator = new Coordinator(_state, Save, _prompter, _windows, () => DateOnly.FromDateTime(DateTime.Today));
        _windows.Coordinator = _coordinator;
        _windows.GameChanged = () =>
        {
            _tracked?.Refill();
            // A game that just got its match may have a Mark as choice waiting for it.
            _journal?.KickStatus();
        };
        _coordinator.Changed += _ =>
        {
            try { _tracked?.Refill(); }
            catch (Exception ex) { Log.Write("errors", "tracked games refill: " + ex); }
        };

        _watcher = new GameWatcher(_state, new RegistrySteamSource(), new WindowsProcessSource(), _coordinator, new InstalledLauncherGames());
        _journal = new JournalSync(_state, Save, SaveChecked, () => _watcher.AnyPlayRunning, () => _windows.OpenWindow is not null, JournalGaveUp, _ =>
        {
            try { _tracked?.Refill(); }
            catch (Exception ex) { Log.Write("errors", "tracked games refill: " + ex); }
        }, () => _prompter.NotifyJournalPaused())
        {
            StatusSaved = _prompter.NotifyStatusSaved,
            StatusFailed = _prompter.NotifyStatusFailed,
            StatusExpired = _prompter.NotifyStatusExpired,
        };
        _coordinator.JournalDue += _ => _journal.Kick();
        _tray = new NotifyIcon { Icon = AppAssets.TrayIcon, Text = TrayText(), Visible = true, ContextMenuStrip = BuildMenu() };

        if (_store.RecoveredFromCorrupt && _store.BackupPath is { } backup)
            _prompter.NotifyFolder(CorruptNotice(backup), backup);
        if (_store.LoadFailed)
            _prompter.NotifyFolder(LoadFailedNotice, Paths.State);
        _watcher.Start();
        // Anything left from the last run goes once nothing else is going on.
        _journal.Kick();
    }

    void JournalGaveUp(GameRecord g, IReadOnlyList<PlayDay> days) =>
        _prompter.NotifyJournalFailed(g,
            Journal.NoticeBody(days.Select(d => (d.Date, d.PendingMinutes, d.TriedTotal is not null)).ToList(), DateTime.Today));

    // The user's click on a notice's Open log, so the window opens at once. After the app gave up on
    // a Mark as choice, the box opens with that choice in it and the user saves it there.
    void OpenLogFor(string gameId)
    {
        if (!_state.Games.TryGetValue(gameId, out var g)) return;
        if (g.PickGaveUp && _journal.TakeOverPick(gameId) is { } pick)
        {
            _windows.Open(Actions.MarkAs(g, pick.Status, pick.Playing, pick.Backlog, pick.Wishlist, DateOnly.FromDateTime(DateTime.Today)));
            return;
        }
        _windows.Open(Actions.PendingRequest(g) ?? LogRequest.OpenOnly(g.Id));
    }

    // A Mark as choice the close popup was left with goes to Backloggd by itself; a game with no
    // match keeps it until it has one, and the user is told.
    void KeepPickFor(string gameId, StatusPick pick)
    {
        var kept = _journal.KeepPick(gameId, pick);
        Log.Write("journal", $"{gameId} Mark as {pick.Describe()}: {kept}");
        if (kept == PickKept.NoMatch && _state.Games.TryGetValue(gameId, out var g)) _prompter.NotifyPickUnmatched(g, pick);
    }

    // The missed queue changed: it is saved, and the tray's count follows. The open Missed popups
    // window follows the queue by itself.
    void MissedChanged()
    {
        Save();
        if (_missedItem is { } item) item.Text = Format.MissedMenu(_state.MissedPopups.Count);
        if (_tray is not null) _tray.Text = TrayText();
    }

    void OpenMissed()
    {
        if (Raise(_missed)) return;
        _missed = new MissedPopupsForm(_state, m => _prompter.ShowAgain(m));
        _missed.FormClosed += (_, _) => _missed = null;
        _missed.Show();
    }

    // Open folder shows where the file is, so the notices name only the file and stay short.
    internal static string CorruptNotice(string backup) =>
        $"Settings file was unreadable. Started fresh. The old file is kept as {Path.GetFileName(backup)}.";

    internal const string LoadFailedNotice = "Couldn't read your settings file. Changes this session go to state.recovered.json.";

    internal static string StartupFailedNotice(bool turningOn) =>
        $"Couldn't turn {(turningOn ? "on" : "off")} Start with Windows. Windows blocked the change. Try again later.";

    static void ShowInFolder(string file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", File.Exists(file) ? $"/select,\"{file}\"" : $"\"{Path.GetDirectoryName(file)}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Write("errors", "open folder: " + ex); }
    }

    void Save()
    {
        try { _store.Save(_state); }
        catch (Exception ex) { Log.Write("errors", "state save failed: " + ex); }
    }

    // A save the next start will read: false when it failed, or when this session writes to
    // state.recovered.json because state.json couldn't be read.
    bool SaveChecked()
    {
        if (_store.LoadFailed) return false;
        try
        {
            _store.Save(_state);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("errors", "state save failed: " + ex);
            return false;
        }
    }

    static bool Raise(Form? form)
    {
        if (form is null || form.IsDisposed) return false;
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        return true;
    }

    string TrayText() => Format.TrayText(_state.PromptsPaused, _state.MissedPopups.Count);

    void OpenSettings()
    {
        if (Raise(_settings)) return;
        _settings = new SettingsForm(_state.Settings, _state.PromptsPaused, StartupEntry.IsEnabled, ApplySettings);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    // Settings saved in the window go to the state file; Pause popups and Start with Windows go
    // through the tray items, so the menu, the tooltip and the registry stay in step.
    void ApplySettings(SettingsChoice choice)
    {
        // The Tracked games view (columns, sort, emulators) is set in its own window, maybe while
        // Settings was open, so the stored one is kept.
        _state.Settings = choice.Settings with { TrackedList = _state.Settings.TrackedList };
        Save();
        _watcher?.SettingsChanged();
        if (_state.Settings.AutoAddJournalTime) _journal?.Kick();
        if (_pauseItem is { } pause && pause.Checked != choice.PromptsPaused) pause.Checked = choice.PromptsPaused;
        if (_startupItem is { } startup && startup.Checked != choice.StartWithWindows) startup.Checked = choice.StartWithWindows;
    }

    void OpenPicker()
    {
        if (Raise(_picker)) return;
        var tracked = _state.Games.Values.Select(g => g.ExePath).OfType<string>().ToList();
        _picker = new WindowPicker(tracked, TrackPicked);
        _picker.FormClosed += (_, _) => _picker = null;
        _picker.Show();
    }

    void TrackPicked((string Title, string ExePath) p)
    {
        var id = GameIds.Exe(p.ExePath);
        if (!_state.Games.ContainsKey(id))
            _state.Games[id] = new GameRecord { Id = id, DisplayName = PickName(p.Title, p.ExePath), ExePath = p.ExePath.ToLowerInvariant(), AddedAt = DateTime.Now };
        Save();
        _tracked?.Refill();
    }

    // A known emulator is named after itself, not after the game its window shows now; its games
    // get records of their own.
    static string PickName(string windowTitle, string exePath)
    {
        if (EmulatorTitles.Detect(Path.GetFileName(exePath)) is { } kind) return EmulatorTitles.Name(kind);
        string? product = null;
        try { product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).ProductName; }
        catch (Exception ex)
        {
            Log.Write("errors", $"product name read failed for {exePath}: {ex.Message}");
        }
        return GameNames.PickName(windowTitle, product, exePath);
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ThemedMenu();

        menu.Items.Add("Open Backloggd window", null, (_, _) => _windows.OpenBrowse());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Track a running game...", null, (_, _) => OpenPicker());

        menu.Items.Add("Tracked games...", null, (_, _) =>
        {
            if (Raise(_tracked)) return;
            _tracked = new TrackedGamesForm(_state, Save, OpenPicker, g => _windows.OpenMatch(g.Id),
                g => _windows.Open(Actions.PendingRequest(g) ?? LogRequest.OpenOnly(g.Id)));
            _tracked.FormClosed += (_, _) => _tracked = null;
            _tracked.Show();
        });
        _missedItem = new ToolStripMenuItem(Format.MissedMenu(_state.MissedPopups.Count));
        _missedItem.Click += (_, _) => OpenMissed();
        menu.Items.Add(_missedItem);
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause popups") { Checked = _state.PromptsPaused, CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            _state.PromptsPaused = pause.Checked;
            Save();
            if (_tray is not null) _tray.Text = TrayText();
            _settings?.SyncApp(promptsPaused: pause.Checked);
        };
        menu.Items.Add(pause);
        _pauseItem = pause;

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEntry.IsEnabled, CheckOnClick = true };
        var reverting = false;
        startup.CheckedChanged += (_, _) =>
        {
            if (reverting) return;
            var want = startup.Checked;
            if (!StartupEntry.Set(want))
            {
                reverting = true;
                try { startup.Checked = StartupEntry.IsEnabled; }
                finally { reverting = false; }
                _prompter.Notify(StartupFailedNotice(want));
            }
            _settings?.SyncApp(startWithWindows: startup.Checked);
        };
        menu.Items.Add(startup);
        _startupItem = startup;

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            // Popups still on screen were never answered: they go to the missed queue.
            PromptPopup.CloseAll();
            _tray.Visible = false;
            _tray.Dispose();
            _picker?.Close();
            _tracked?.Close();
            _missed?.Close();
            _settings?.Close();
            _watcher.Dispose();
            _journal.Dispose();
            _windows.Dispose();
            ExitThread();
        });
        menu.FitItemHeights();
        return menu;
    }
}
