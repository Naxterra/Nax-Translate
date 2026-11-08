using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard    = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NaxTranslate;

public partial class MainWindow : Window
{
    // ── State ──
    readonly Config           _cfg;
    readonly ResilientHttp?   _http;
    readonly DispatcherTimer  _debounce;
    CancellationTokenSource   _cts  = new();
    string?                   _token;
    bool                      _nextGen;
    bool                      _claude;          // the Claude engine is picked instead of DeepL
    bool                      _chatgpt;
    bool                      _changingTone;
    readonly IChatGptClient    _chatGpt;
    readonly CancellationTokenSource _lifetime = new();
    int                       _chatGptConnectionVersion;
    bool                      _busy;            // a translation is running right now
    readonly ClaudeSession    _claudeSession;
    string                    _lastSrc = "";
    string?                   _detected;        // language DeepL detected in the shown translation
    bool                      _failed;          // last translation could not reach DeepL
    bool                      _autoSwitching;   // the app itself is changing the target language
    bool                      _stopped;
    readonly SemaphoreSlim    _refreshLock = new(1, 1);

    // ── Auto-refresh and offline-retry timers ──
    readonly DispatcherTimer  _refresh;
    readonly DispatcherTimer  _retry;
    static readonly TimeSpan  FirstRetryDelay = TimeSpan.FromSeconds(5);

    public MainWindow() : this(Config.Load()) { }

    // Separate settings and transports let window tests exercise the real event handlers
    // without reading the user's login or contacting either translation service.
    internal MainWindow(Config config, ResilientHttp? http = null, ClaudeSession? claudeSession = null,
        IChatGptClient? chatGpt = null)
    {
        _cfg = config;
        _http = http;
        _claudeSession = claudeSession ?? new();
        _chatGpt = chatGpt ?? new ChatGptServer();
        InitializeComponent();

        // Timers must be created before setting combo selections (which fire events)
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = TranslateAsync(); };

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _refresh.Tick += async (_, _) =>
        {
            if (_stopped) return;
            // Replaces a waiting Claude process once it is too old, so the next translation
            // still finds one ready instead of starting the tool then.
            if (_claude && _cfg.ClaudeConnected) _claudeSession.WarmUp(_cfg.ClaudeModel);
            if (_chatgpt && _cfg.ChatGptConnected) _chatGpt.WarmUp();
            await RefreshTokenAsync();
        };
        _refresh.Start();

        // While DeepL is unreachable, try again with a pause that doubles up to five minutes.
        // A request that timed out may still count against the quota, so retrying every few
        // seconds for hours could use it up. A network change or an edit retries right away,
        // see OnNetworkReset and SrcBox_Changed.
        _retry = new DispatcherTimer { Interval = FirstRetryDelay };
        _retry.Tick += (_, _) =>
        {
            if (!_failed) { _retry.Stop(); return; }
            _retry.Interval = RetryBackoff.Next(_retry.Interval);
            _ = TranslateAsync();
        };
        (_http ?? Net.Shared).Reset += OnConnectionsReset;

        // Restore UI language before populating combos (language names depend on it)
        Loc.SetLanguage(_cfg.UiLang);
        if (_cfg.UiLang == "en") LangEnRb.IsChecked = true;
        else                     LangDeRb.IsChecked  = true;
        Loc.Changed += ApplyLocalization;
        // The language was set above, before this handler existed, so apply the texts once now.
        // Otherwise an English interface would start with German labels and without tooltips.
        ApplyLocalization();

        // Populate dropdowns
        SrcCombo.ItemsSource = DeepLApi.SrcLangs.Keys;
        TgtCombo.ItemsSource = DeepLApi.TgtLangs.Keys;

        // Restore config
        SrcCombo.SelectedItem = DeepLApi.SrcLangs.Keys.FirstOrDefault(
            k => DeepLApi.SrcLangs[k] == _cfg.SrcLang, "Automatisch");
        TgtCombo.SelectedItem = DeepLApi.TgtLangs.Keys.FirstOrDefault(
            k => DeepLApi.TgtLangs[k] == _cfg.TgtLang, "Deutsch");

        if      (_cfg.Engine == "chatgpt") ChatGptRb.IsChecked = true;
        else if (_cfg.Engine == "claude")  ClaudeRb.IsChecked  = true;
        else if (_cfg.Engine == "nextgen") NextgenRb.IsChecked = true;
        else                               ClassicRb.IsChecked = true;

        // Restore geometry
        if (!string.IsNullOrEmpty(_cfg.Geometry))
        {
            var p = _cfg.Geometry.Split(',');
            if (p.Length == 4 &&
                double.TryParse(p[0], out var l) && double.TryParse(p[1], out var t) &&
                double.TryParse(p[2], out var w) && double.TryParse(p[3], out var h))
            { Left = l; Top = t; Width = w; Height = h; }
        }

        // Reflect current startup registry state
        MenuItemStartup.IsChecked = StartupHelper.IsEnabled();
        MenuItemStartInTray.IsChecked    = _cfg.StartInTray;
        MenuItemMinimizeToTray.IsChecked = _cfg.MinimizeToTray;

        Loaded          += OnLoaded;
        Closing         += OnClosing;
        StateChanged    += OnStateChanged;

        // Repaint the whole window when it is shown again and after the screen was locked or the
        // computer slept, so a surface lost in the meantime never leaves it black.
        IsVisibleChanged += (_, _) => { if (IsVisible) ForceRedraw(); };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SizeChanged     += (_, _) => RememberGeometry();
        LocationChanged += (_, _) => RememberGeometry();
    }

    // ──────────────────────────────────── Startup ────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Before the window is first shown, so it never flashes the solid background.
        TitleBar.Apply(this);
    }

    /// <summary>Whether the app should start with this window hidden in the tray.</summary>
    public bool StartsInTray => _cfg.StartInTray;

    async void OnLoaded(object s, RoutedEventArgs e) => await RunStartupChecksAsync();

    /// <summary>
    /// Called instead of showing the window when the app starts in the tray. The window's Loaded
    /// event only comes once it is shown, so the startup checks are started here, and Ctrl+C+C
    /// finds a fresh login right away.
    /// </summary>
    public void StartHidden() => _ = RunStartupChecksAsync();

    bool _startupChecksStarted;

    async Task RunStartupChecksAsync()
    {
        // Runs once, whether the window was shown at start or only later.
        if (_stopped || _startupChecksStarted) return;
        _startupChecksStarted = true;
        try
        {
            await RefreshTokenAsync();
            // With Claude picked and connected, check the Claude Code sign-in once, so the badge
            // shows the plan and a missing sign-in is reported before the first translation.
            if (!_stopped && _claude && _cfg.ClaudeConnected) await CheckClaudeAsync(quiet: true);
            if (!_stopped && _chatgpt && _cfg.ChatGptConnected) await CheckChatGptAsync(quiet: true);
        }
        catch (Exception ex)
        {
            // After a hidden start nobody awaits this task, so an error would otherwise vanish.
            // The status line shows it, and the next translation or menu action tries again.
            SetStatus(Loc.StatusError + ex.Message);
        }
    }

    // Minimizing hides the window to the tray when that option is on, so it leaves the taskbar.
    void OnStateChanged(object? s, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _cfg.MinimizeToTray)
        {
            SaveSettings();
            Hide();
        }
    }

    void StartInTray_Click(object s, RoutedEventArgs e)
    {
        _cfg.StartInTray = MenuItemStartInTray.IsChecked;
        _cfg.Save();
    }

    void MinimizeToTray_Click(object s, RoutedEventArgs e)
    {
        _cfg.MinimizeToTray = MenuItemMinimizeToTray.IsChecked;
        _cfg.Save();
    }

    /// <summary>
    /// Asks Claude Code whether it is signed in and shows the result in the badge and, unless
    /// <paramref name="quiet"/>, in the status line. Returns true when it is signed in.
    /// </summary>
    async Task<bool> CheckClaudeAsync(bool quiet)
    {
        if (!quiet) SetStatus(Loc.StatusClaudeChecking);
        try
        {
            var auth = await ClaudeCli.CheckAuthAsync();
            if (!auth.LoggedIn)
            {
                _claudeBadge = "";
                UpdateBadge();
                SetStatus(Loc.StatusClaudeNotSignedIn);
                return false;
            }
            _claudeBadge = auth.Plan != null ? $"Claude {auth.Plan} ✓" : "Claude ✓";
            UpdateBadge();
            if (!quiet) SetStatus(Loc.StatusClaudeConnected(auth.Plan));
            return true;
        }
        catch (ClaudeNotFoundException)
        {
            _claudeBadge = "";
            UpdateBadge();
            SetStatus(Loc.StatusClaudeNotFound);
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.StatusError + ex.Message);
            return false;
        }
    }

    async void ClaudeConnect_Click(object s, RoutedEventArgs e)
    {
        if (!await CheckClaudeAsync(quiet: false)) return;
        _cfg.ClaudeConnected = true;
        _cfg.Save();
        if (_claude) _claudeSession.WarmUp(_cfg.ClaudeModel);
        UpdateBadge();
        // Translate what is in the box now that Claude can be reached.
        _lastSrc = "";
        _debounce.Stop();
        _debounce.Start();
    }

    void ClaudeDisconnect_Click(object s, RoutedEventArgs e)
    {
        _cfg.ClaudeConnected = false;
        _cfg.Save();
        // Stop a Claude request that is still running and end the waiting process, so nothing
        // more reaches Claude and no Claude Code process stays in memory.
        if (_claude) CancelRunningTranslation();
        _claudeSession.DropSpare();
        UpdateBadge();
        SetStatus(Loc.StatusClaudeDisconnectedNow);
    }

    async Task<bool> CheckChatGptAsync(bool quiet, int? connectionVersion = null)
    {
        if (!quiet) SetStatus(Loc.StatusChatGptChecking);
        try
        {
            var loggedIn = await _chatGpt.CheckAuthAsync(_lifetime.Token);
            if (_stopped || connectionVersion.HasValue && connectionVersion != _chatGptConnectionVersion) return false;
            _chatGptBadge = loggedIn ? "ChatGPT ✓" : "";
            UpdateBadge();
            if ((!quiet || _chatgpt) && (!quiet || _cfg.ChatGptConnected))
                SetStatus(loggedIn ? Loc.StatusChatGptConnected : Loc.StatusChatGptNotSignedIn);
            return loggedIn;
        }
        catch (OperationCanceledException) when (_stopped) { return false; }
        catch (Exception ex)
        {
            if (_stopped || connectionVersion.HasValue && connectionVersion != _chatGptConnectionVersion) return false;
            _chatGptBadge = "";
            UpdateBadge();
            if (!_stopped && (!quiet || _chatgpt))
                SetStatus(ex is ChatGptNotFoundException ? Loc.StatusChatGptNotFound : Loc.StatusError + ex.Message);
            return false;
        }
    }

    async void ChatGptConnect_Click(object s, RoutedEventArgs e)
    {
        var version = ++_chatGptConnectionVersion;
        if (!await CheckChatGptAsync(quiet: false, connectionVersion: version) || _stopped || version != _chatGptConnectionVersion) return;
        _cfg.ChatGptConnected = true;
        _cfg.Save();
        UpdateBadge();
        if (_chatgpt) { _chatGpt.WarmUp(); InvalidateTranslation(); _debounce.Stop(); _debounce.Start(); }
    }

    void ChatGptDisconnect_Click(object s, RoutedEventArgs e)
    {
        _chatGptConnectionVersion++;
        _cfg.ChatGptConnected = false;
        _cfg.Save();
        if (_chatgpt) { InvalidateTranslation(); _debounce.Stop(); }
        _chatGpt.Stop();
        UpdateBadge();
        SetStatus(Loc.StatusChatGptDisconnectedNow);
    }

    /// <summary>
    /// Makes sure a usable access token is loaded. Never throws: when DeepL cannot be reached,
    /// the stored login is kept and the status shows that the app is offline.
    /// </summary>
    async Task RefreshTokenAsync(bool force = false)
    {
        await _refreshLock.WaitAsync();
        try
        {
            // 1. Current token still valid → nothing to do
            if (!force && _token != null && !DeepLApi.IsTokenExpired(_token))
                return;

            SetAuthStatus(Loc.StatusChecking);

            // 2. Standard OIDC refresh token
            if (!string.IsNullOrEmpty(_cfg.RefreshToken))
            {
                var result = await AuthManager.RefreshAsync(_cfg.RefreshToken);
                if (result.HasValue)
                {
                    var (access, refresh) = result.Value;
                    _token        = access;
                    _cfg.DlAccess = access;
                    if (refresh != null) _cfg.RefreshToken = refresh;
                    _cfg.Save();
                    SetBadge("Pro ✓"); SetAuthStatus(Loc.StatusReady); return;
                }
            }

            // 3. Cached access token
            if (!force && !string.IsNullOrEmpty(_cfg.DlAccess) && !DeepLApi.IsTokenExpired(_cfg.DlAccess))
            {
                _token = _cfg.DlAccess;
                SetBadge("Pro ✓"); SetAuthStatus(Loc.StatusReady); return;
            }

            _token = null;
            SetBadge("");
            SetAuthStatus(Loc.StatusNotAuth);
        }
        catch (Exception)
        {
            // Offline or DeepL had a server error. Keep whatever login we have and try again later.
            _token ??= _cfg.DlAccess;
            SetAuthStatus(Loc.StatusOffline);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    void OnNetworkReset()
    {
        // Windows reports several changes while a network switch settles, so wait a moment
        // and then retry what failed, using fresh connections.
        if (_stopped || !_failed) return;
        _retry.Stop();
        _retry.Interval = TimeSpan.FromSeconds(1.5);
        _retry.Start();
    }

    void OnConnectionsReset() => Dispatcher.BeginInvoke(OnNetworkReset);

    void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) Dispatcher.BeginInvoke(ForceRedraw);
    }

    void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(ForceRedraw);
    }

    // ──────────────────────────────── Translation ────────────────────────────────────

    internal async Task TranslateAsync()
    {
        if (_stopped) return;
        var src = SrcBox.Text.Trim();
        if (src.Length < 3) return;
        if (src.Length > DeepLApi.MaxChars)
        {
            _cts.Cancel();
            SetStatus(Loc.StatusTooLong(src.Length));
            return;
        }
        if (src == _lastSrc && !_failed) return;
        _lastSrc = src;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var srcLang = DeepLApi.SrcLangs[(string)SrcCombo.SelectedItem!];
        var tgtLang = DeepLApi.TgtLangs[(string)TgtCombo.SelectedItem!];

        // The engine is fixed for this whole translation, even if the toggle changes meanwhile.
        var claude = _claude;
        var chatgpt = _chatgpt;
        var nextGen = _nextGen;
        var tone   = chatgpt ? _cfg.ChatGptTone : _cfg.ClaudeTone;
        var engineName = chatgpt ? "ChatGPT" : claude ? "Claude" : "DeepL";

        // While Claude is disconnected, nothing is sent to it. Clearing _lastSrc translates the
        // text as soon as Claude is connected again.
        if (claude && !_cfg.ClaudeConnected)
        {
            _lastSrc = "";
            SetStatus(Loc.StatusClaudeDisconnected);
            return;
        }
        if (chatgpt && !_cfg.ChatGptConnected)
        {
            _lastSrc = "";
            SetStatus(Loc.StatusChatGptDisconnected);
            return;
        }

        // Claude's answer is shown when it is complete, like DeepL's, instead of word by word.
        Task<(string Text, string? DetectedLang)> Run(string tgt) => chatgpt
            ? _chatGpt.TranslateAsync(src, tgt, srcLang, _cfg.ChatGptModel, tone, ct)
            : claude
            ? _claudeSession.TranslateAsync(src, tgt, srcLang, _cfg.ClaudeModel, tone: tone, ct: ct)
            : DeepLApi.TranslateAsync(src, tgt, srcLang, _token, nextGen, ct, http: _http);

        SetStatus(Loc.StatusTranslating);
        var started = System.Diagnostics.Stopwatch.StartNew();
        _busy = true;
        try
        {
            (string Text, string? Detected) result;
            try
            {
                result = await Run(tgtLang);
            }
            catch (AuthExpiredException)
            {
                // The token ran out between refresh checks. Renew it once and try again.
                await RefreshTokenAsync(force: true);
                ct.ThrowIfCancellationRequested();
                if (_token == null) { _failed = false; return; }   // status already says "not logged in"
                result = await Run(tgtLang);
            }
            ct.ThrowIfCancellationRequested();

            // The text is already in the target language, so switch the target the way the
            // official app does and translate again. A fixed source that does not match the text
            // is corrected to the detected language. The dropdowns show the new languages.
            if (DeepLApi.AutoSwitch(srcLang, result.Detected, tgtLang) is { } sw)
            {
                _autoSwitching = true;
                try
                {
                    TgtCombo.SelectedItem = DeepLApi.TgtLangs.First(kv => kv.Value == sw.Tgt).Key;
                    SrcCombo.SelectedItem = DeepLApi.SrcLangs.First(kv => kv.Value == sw.Src).Key;
                }
                finally { _autoSwitching = false; }
                _cfg.TgtLang = sw.Tgt;
                _cfg.SrcLang = sw.Src;
                _cfg.Save();
                srcLang = sw.Src;
                result = await Run(sw.Tgt);
                ct.ThrowIfCancellationRequested();
            }

            TgtBox.Text = result.Text;
            _detected   = result.Detected;
            _failed = false;
            _retry.Stop();
            _retry.Interval = FirstRetryDelay;
            SetStatus(Loc.StatusTranslated(result.Detected, engineName, started.Elapsed));
        }
        catch (OperationCanceledException) { }
        catch (ChatGptNotFoundException)
        {
            if (ct.IsCancellationRequested) return;
            _failed = false;
            _lastSrc = "";
            SetStatus(Loc.StatusChatGptNotFound);
        }
        catch (ClaudeLimitReachedException ex)
        {
            if (ct.IsCancellationRequested) return;
            // No automatic retry, because every attempt counts against the limit.
            _failed  = false;
            _lastSrc = "";
            SetStatus(Loc.StatusClaudeLimit(ex.Message));
        }
        catch (ClaudeBusyException)
        {
            if (ct.IsCancellationRequested) return;
            _failed  = false;
            _lastSrc = "";
            SetStatus(Loc.StatusClaudeBusy);
        }
        catch (ClaudeNotFoundException)
        {
            if (ct.IsCancellationRequested) return;
            _failed  = false;
            _lastSrc = "";
            SetStatus(Loc.StatusClaudeNotFound);
        }
        catch (OfflineException)
        {
            if (ct.IsCancellationRequested) return;
            _failed = true;
            if (!_retry.IsEnabled) _retry.Start();
            SetStatus(Loc.StatusOffline);
        }
        catch (LimitReachedException)
        {
            if (ct.IsCancellationRequested) return;
            // No automatic retry, because every attempt counts against the limit. The next edit
            // or Ctrl+Enter tries again.
            _failed  = false;
            _lastSrc = "";
            SetStatus(Loc.StatusLimitReached);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            // Not a network problem, so retrying the same text would fail the same way.
            // Clearing _lastSrc lets Ctrl+Enter or the next edit try again.
            _failed  = false;
            _lastSrc = "";
            SetStatus(Loc.StatusError + ex.Message);
        }
        finally
        {
            // Only the newest translation clears the flag. An older one that was cancelled must
            // not mark a newer one as finished.
            if (_cts.Token == ct) _busy = false;
        }
    }

    /// <summary>Stops everything that runs in the background, for when the app exits.</summary>
    public void StopBackgroundWork()
    {
        if (_stopped) return;
        _stopped = true;
        _lifetime.Cancel();
        _debounce.Stop();
        _refresh.Stop();
        _retry.Stop();
        (_http ?? Net.Shared).Reset -= OnConnectionsReset;
        Loc.Changed -= ApplyLocalization;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        CancelRunningTranslation();
        _claudeSession.Dispose();
        _chatGpt.Stop();
    }

    /// <summary>
    /// Stops the translation that is still running, if any. Its text is outdated, and a Claude
    /// run would otherwise go on and use up quota. Clearing _lastSrc makes sure the text is
    /// translated again even if it is changed back to what was being translated.
    /// </summary>
    public void CancelRunningTranslation()
    {
        if (!_busy) return;
        _cts.Cancel();
        _busy    = false;
        _lastSrc = "";
        SetStatus(Loc.StatusReady);
    }

    void InvalidateTranslation()
    {
        // The input or translation options changed. Cancel before waiting for the debounce,
        // otherwise an old answer could be shown under the new options or switch them back.
        CancelRunningTranslation();
        _lastSrc = "";
        _failed = false;
        _retry.Stop();
        _retry.Interval = FirstRetryDelay;
    }

    // ──────────────────────────────── Event handlers ─────────────────────────────────

    void SrcBox_Changed(object s, TextChangedEventArgs e)
    {
        CharCount.Text = $"{SrcBox.Text.Length} / {DeepLApi.MaxChars}";
        // The running translation is for text that no longer exists, so stop it right away
        // instead of when the next one starts. Otherwise its result could overwrite the box, and
        // it would never be stopped when the text gets too short to translate.
        InvalidateTranslation();
        // Text over the limit still goes through the debounce, so TranslateAsync can say why
        // nothing is translated.
        _debounce.Stop();
        _debounce.Start();
    }

    void SrcBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _debounce.Stop();
            _ = TranslateAsync();
            e.Handled = true;
        }
    }

    void Lang_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_autoSwitching) return;
        if (SrcCombo.SelectedItem is null || TgtCombo.SelectedItem is null) return;
        InvalidateTranslation();
        _cfg.SrcLang = DeepLApi.SrcLangs[(string)SrcCombo.SelectedItem];
        _cfg.TgtLang = DeepLApi.TgtLangs[(string)TgtCombo.SelectedItem];
        _cfg.Save();
        _debounce.Stop();
        _debounce.Start();
    }

    void Tone_Changed(object s, RoutedEventArgs e)
    {
        if (_changingTone) return;
        var tone = ToneFormalRb.IsChecked   == true ? ClaudeCli.ToneFormal
                        : ToneInformalRb.IsChecked == true ? ClaudeCli.ToneInformal
                        :                                    ClaudeCli.ToneNatural;
        if (_chatgpt) _cfg.ChatGptTone = tone;
        else _cfg.ClaudeTone = tone;
        _cfg.Save();
        if (!_claude && !_chatgpt) return;
        InvalidateTranslation();
        _debounce.Stop();
        _debounce.Start();
    }

    void Engine_Changed(object s, RoutedEventArgs e)
    {
        InvalidateTranslation();
        _nextGen       = NextgenRb.IsChecked == true;
        _claude        = ClaudeRb.IsChecked  == true;
        _chatgpt       = ChatGptRb.IsChecked == true;
        _cfg.Engine    = _chatgpt ? "chatgpt" : _claude ? "claude" : _nextGen ? "nextgen" : "classic";
        _cfg.Save();
        // Every subscription-model request takes time and counts against its usage
        // limit, so wait for a longer pause in typing before sending.
        _debounce.Interval = TimeSpan.FromMilliseconds(_claude || _chatgpt ? 1500 : 800);
        UpdateBadge();
        // Each subscription engine remembers its own tone. DeepL hides the tone switch.
        TonePanel.Visibility = _claude || _chatgpt ? Visibility.Visible : Visibility.Collapsed;
        _changingTone = true;
        try
        {
            var tone = ClaudeCli.NormalizeTone(_chatgpt ? _cfg.ChatGptTone : _cfg.ClaudeTone);
            ToneFormalRb.IsChecked = tone == ClaudeCli.ToneFormal;
            ToneInformalRb.IsChecked = tone == ClaudeCli.ToneInformal;
            ToneNaturalRb.IsChecked = tone == ClaudeCli.ToneNatural;
        }
        finally { _changingTone = false; }
        if (IsLoaded)
            SetStatus(_chatgpt ? (_cfg.ChatGptConnected ? Loc.StatusReady : Loc.StatusChatGptDisconnected)
                              : _claude ? (_cfg.ClaudeConnected ? Loc.StatusReady : Loc.StatusClaudeDisconnected)
                              : (_token != null ? Loc.StatusReady : Loc.StatusNotAuth));
        // Start Claude Code ahead of time, so the first translation does not wait for it. With
        // DeepL picked, or Claude disconnected, no Claude process is kept around.
        if (_claude && _cfg.ClaudeConnected) _claudeSession.WarmUp(_cfg.ClaudeModel);
        else                                 _claudeSession.DropSpare();
        if (_chatgpt && _cfg.ChatGptConnected) _chatGpt.WarmUp();
        else _chatGpt.Stop();
        // The first switch to Claude after start checks the sign-in, so the badge shows the plan.
        if (IsLoaded && _claude && _cfg.ClaudeConnected && _claudeBadge.Length == 0)
            _ = CheckClaudeAsync(quiet: true);
        if (IsLoaded && _chatgpt && _cfg.ChatGptConnected && _chatGptBadge.Length == 0)
            _ = CheckChatGptAsync(quiet: true);
        _debounce.Stop();
        _debounce.Start();
    }

    void Swap_Click(object s, RoutedEventArgs e)
    {
        if (SrcCombo.SelectedItem is not string srcName || TgtCombo.SelectedItem is not string tgtName) return;
        var (newSrc, newTgt) = DeepLApi.Swap(DeepLApi.SrcLangs[srcName], DeepLApi.TgtLangs[tgtName], _detected);

        SrcCombo.SelectedItem = DeepLApi.SrcLangs.First(kv => kv.Value == newSrc).Key;
        TgtCombo.SelectedItem = DeepLApi.TgtLangs.First(kv => kv.Value == newTgt).Key;

        (SrcBox.Text, TgtBox.Text) = (TgtBox.Text, SrcBox.Text);
        _detected = null;
        _lastSrc  = "";
    }

    void Clear_Click(object s, RoutedEventArgs e)
    {
        SrcBox.Clear();
        TgtBox.Clear();
        _detected = null;
        _lastSrc  = "";
        SetStatus(Loc.StatusReady);
    }

    void Copy_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TgtBox.Text)) return;
        // Another program can hold the clipboard for a moment, which makes the call throw.
        try   { Clipboard.SetText(TgtBox.Text); SetStatus(Loc.StatusCopied); }
        catch (Exception) { SetStatus(Loc.StatusClipboardBusy); }
    }

    void Settings_Click(object s, RoutedEventArgs e)
    {
        // Update which auth items are visible before opening
        bool loggedIn = _token != null;
        MenuItemLogin.Visibility  = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        MenuItemSwitch.Visibility = loggedIn ? Visibility.Visible   : Visibility.Collapsed;
        MenuItemLogout.Visibility = loggedIn ? Visibility.Visible   : Visibility.Collapsed;
        var claudeOn = _cfg.ClaudeConnected;
        MenuItemClaudeConnect.Visibility    = claudeOn ? Visibility.Collapsed : Visibility.Visible;
        MenuItemClaudeDisconnect.Visibility = claudeOn ? Visibility.Visible   : Visibility.Collapsed;
        MenuItemChatGptConnect.Visibility = _cfg.ChatGptConnected ? Visibility.Collapsed : Visibility.Visible;
        MenuItemChatGptDisconnect.Visibility = _cfg.ChatGptConnected ? Visibility.Visible : Visibility.Collapsed;

        var cm = SettingsBtn.ContextMenu;
        cm.PlacementTarget = SettingsBtn;
        // Below the button with the right edges aligned, so the menu stays inside the window.
        // The 10 is the room the menu template leaves on its right for the shadow.
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        cm.CustomPopupPlacementCallback = (popup, target, _) =>
            [new(new System.Windows.Point(target.Width - popup.Width + 10, target.Height + 4),
                 System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
        cm.IsOpen = true;
    }

    async void Login_Click(object s, RoutedEventArgs e)         => await DoLoginAsync();
    async void SwitchAccount_Click(object s, RoutedEventArgs e) => await DoLoginAsync();

    void Logout_Click(object s, RoutedEventArgs e)
    {
        _token             = null;
        _cfg.DlAccess      = null;
        _cfg.RefreshToken  = null;
        _cfg.Save();
        SetBadge("");
        SetStatus(Loc.StatusLoggedOut);
    }

    void Startup_Click(object s, RoutedEventArgs e)
    {
        StartupHelper.SetEnabled(MenuItemStartup.IsChecked);
    }

    void About_Click(object s, RoutedEventArgs e)
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        System.Windows.MessageBox.Show(
            $"{Loc.AppTitle}\nVersion {ver}\n\n{Loc.AboutBlurb}\n\n{Loc.AboutAuthor}: Naxterra\nhttps://github.com/Naxterra/Nax-Translate",
            Loc.MenuAbout,
            MessageBoxButton.OK,
            MessageBoxImage.None);
    }

    async Task DoLoginAsync()
    {
        SetStatus(Loc.StatusBrowser);

        // Some browsers never start the app from DeepL's "App öffnen" page. As a way around
        // that, the user can copy the page's address, and the login picks it up from the
        // clipboard. AuthManager only accepts it when its state matches this login.
        var clipWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        clipWatch.Tick += (_, _) =>
        {
            string text;
            try { text = Clipboard.GetText().Trim(); }
            catch (Exception) { return; }   // another program holds the clipboard right now
            if (!text.Contains("code=", StringComparison.Ordinal) || !AuthManager.HandleCallback(text))
                return;
            // The address holds a one-time login code, so it should not stay in the clipboard.
            try { Clipboard.Clear(); } catch (Exception) { }
        };
        clipWatch.Start();

        (string Access, string? Refresh, string? Identity)? result;
        try
        {
            result = await AuthManager.LoginAsync();
        }
        catch (Exception)
        {
            SetStatus(Loc.StatusOffline);
            return;
        }
        finally
        {
            clipWatch.Stop();
        }
        if (!result.HasValue)
        {
            SetStatus(Loc.StatusCancelled);
            return;
        }
        var (access, refresh, _) = result.Value;
        _token             = access;
        _cfg.DlAccess      = access;
        _cfg.RefreshToken  = refresh;
        _cfg.Save();
        SetBadge("Pro ✓");
        SetStatus(Loc.StatusLoggedIn);
    }

    void UiLang_Changed(object s, RoutedEventArgs e)
    {
        var lang = LangEnRb.IsChecked == true ? "en" : "de";
        _cfg.UiLang = lang;
        _cfg.Save();
        Loc.SetLanguage(lang);
    }

    // Called by App when Ctrl+C+C fires
    public void OnCtrlCC()
    {
        // Ctrl+C+C fires right after another program copied, so the clipboard can still be busy.
        string text;
        try   { text = Clipboard.GetText().Trim(); }
        catch (Exception) { SetStatus(Loc.StatusClipboardBusy); return; }
        if (text.Length < 2) return;

        Show();
        WindowState = WindowState.Normal;
        Activate();
        SrcBox.Text = text;
        SrcBox.CaretIndex = text.Length;
        // _lastSrc is kept, so the same clipboard is not sent again when it is already
        // translated. A failed attempt is still retried, because TranslateAsync checks _failed.
        _debounce.Stop();
        _ = TranslateAsync();
    }

    // ──────────────────────────── Minimize to tray ───────────────────────────────────

    void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        if (_stopped) return;
        e.Cancel = true;
        SaveSettings();
        Hide();
    }

    // ──────────────────────────── Localization ────────────────────────────────────────

    void ApplyLocalization()
    {
        TitleText.Text          = Loc.AppTitle;
        ConnectLabel.Text       = Loc.BtnConnect;
        ClearLabel.Text         = Loc.BtnClear;
        CopyLabel.Text          = Loc.BtnCopy;
        MenuItemLogin.Header    = Loc.MenuLogin;
        MenuItemSwitch.Header   = Loc.MenuSwitch;
        MenuItemLogout.Header   = Loc.MenuLogout;
        MenuItemClaudeConnect.Header    = Loc.MenuClaudeConnect;
        MenuItemClaudeDisconnect.Header = Loc.MenuClaudeDisconnect;
        MenuItemChatGptConnect.Header = Loc.MenuChatGptConnect;
        MenuItemChatGptDisconnect.Header = Loc.MenuChatGptDisconnect;
        MenuItemStartup.Header  = Loc.MenuStartup;
        MenuItemStartInTray.Header    = Loc.MenuStartInTray;
        MenuItemMinimizeToTray.Header = Loc.MenuMinimizeToTray;
        MenuItemAbout.Header    = Loc.MenuAbout;
        ToneNaturalRb.Content   = Loc.ToneNatural;
        ToneFormalRb.Content    = Loc.ToneFormal;
        ToneInformalRb.Content  = Loc.ToneInformal;
        ToneNaturalRb.ToolTip   = Loc.ToneNaturalTip;
        ToneFormalRb.ToolTip    = Loc.ToneFormalTip;
        ToneInformalRb.ToolTip  = Loc.ToneInformalTip;
        Title                   = Loc.AppTitle;
    }

    // ──────────────────────────── Helpers ────────────────────────────────────────────

    // The badge shows the login of the picked engine: "Pro ✓" for DeepL, and "Claude Max ✓" or
    // similar for a connected Claude.
    string _deeplBadge  = "";
    string _claudeBadge = "";
    string _chatGptBadge = "";

    void SetBadge(string text)
    {
        _deeplBadge = text;
        UpdateBadge();
    }

    void UpdateBadge()
    {
        var text = _chatgpt ? (_cfg.ChatGptConnected ? _chatGptBadge : "")
                 : _claude ? (_cfg.ClaudeConnected ? _claudeBadge : "") : _deeplBadge;
        Badge.Text           = text;
        BadgePill.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetStatus(string text) => StatusText.Text = text;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    /// <summary>Asks Windows to repaint the whole window, which makes WPF draw all of it again.</summary>
    void ForceRedraw()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    // Messages about the DeepL login. While the Claude engine is picked they would only confuse,
    // because Claude needs no DeepL login.
    void SetAuthStatus(string text)
    {
        if (_claude || _chatgpt) { if (StatusText.Text == Loc.StatusChecking) SetStatus(Loc.StatusReady); return; }
        SetStatus(text);
    }

    // Moving or resizing only updates the setting in memory. It is written to disk when the
    // window is hidden or the app exits, instead of on every pixel of a drag.
    void RememberGeometry()
    {
        // Left/Top are -32000 while minimized because the WPF WindowState update lags.
        if (WindowState != WindowState.Normal || Left < -10000 || Top < -10000) return;
        _cfg.Geometry = $"{Left:F0},{Top:F0},{Width:F0},{Height:F0}";
    }

    public void SaveSettings() => _cfg.Save();
}
