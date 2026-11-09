using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using MenuItem = System.Windows.Controls.MenuItem;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;

namespace NaxTranslate.Tests;

[CollectionDefinition("Window translation", DisableParallelization = true)]
public sealed class WindowTranslationCollection : ICollectionFixture<WindowDispatcher>;

// These tests create hidden WPF controls on their own dispatcher. They never run App.OnStartup,
// show a window, install the keyboard hook, or use the real login or translation services.
public sealed class WindowDispatcher : IDisposable
{
    readonly Thread _thread;
    readonly Dispatcher _dispatcher;

    public WindowDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                // Load the real styles into a plain WPF application. Constructing the production
                // App would queue its startup code even without calling Run.
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var assembly = typeof(WindowDispatcher).Assembly;
                using var stream = assembly.GetManifestResourceStream(
                    assembly.GetManifestResourceNames().Single(n => n.EndsWith("AppResources.xaml")))!;
                var root = XDocument.Load(stream).Root!;
                var ns = root.Name.Namespace;
                var resources = new XElement(ns + "ResourceDictionary",
                    root.Attributes().Where(a => a.IsNamespaceDeclaration),
                    root.Element(ns + "Application.Resources")!.Elements());
                app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    public Task Run(Func<Task> test) =>
        _dispatcher.InvokeAsync(test).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(10));

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

[Collection("Window translation")]
public sealed class WindowTranslationTests(WindowDispatcher dispatcher)
{
    sealed class PendingChatGpt
    {
        public CancellationToken Token;
        public string Tone = "";
        public readonly TaskCompletionSource<(string Text, string? DetectedLang)> Answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class FakeChatGpt : IChatGptClient
    {
        public readonly List<PendingChatGpt> Requests = [];
        public int Warmups, Stops;
        public void WarmUp() => Warmups++;
        public void Stop() => Stops++;
        public Task<bool> Auth = Task.FromResult(true);
        public Task<bool> CheckAuthAsync(CancellationToken ct = default) => Auth.WaitAsync(ct);
        public Task<(string Text, string? DetectedLang)> TranslateAsync(string text, string target,
            string source, string? model, string tone, CancellationToken ct)
        {
            var request = new PendingChatGpt { Token = ct, Tone = tone };
            Requests.Add(request);
            return request.Answer.Task;
        }
    }

    sealed class PendingClaude(string model) : IClaudeProcess
    {
        public string Model => model;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public bool HasExited => Killed;
        public bool Killed;
        public readonly List<string> Written = [];
        public readonly TaskCompletionSource<string?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Written.Add(line);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken ct) => Answer.Task.WaitAsync(ct);
        public Task<string> StderrAsync() => Task.FromResult("");
        public void CloseInput() { }
        public void Kill() => Killed = true;
        public void Dispose() { }
    }

    sealed class PendingRequest
    {
        public CancellationToken Token;
        public readonly TaskCompletionSource<HttpResponseMessage> Response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(string text = "Old answer", string detected = "EN")
        {
            var response = FakeHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                translations = new[] { new { text, detected_source_language = detected } },
            }));
            if (!Response.TrySetResult(response)) response.Dispose();
        }
    }

    sealed class Harness : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "NaxTranslate.Tests", Guid.NewGuid().ToString("N"));
        readonly ResilientHttp _http;
        public readonly List<PendingRequest> Requests = [];
        public readonly List<PendingClaude> ClaudeProcesses = [];
        public readonly FakeChatGpt ChatGpt = new();
        public readonly Config Settings;
        public bool CompleteUnexpectedRequests;
        public readonly MainWindow Window;

        public Harness(bool claude = false, bool chatgpt = false)
        {
            var cfg = Config.Load(Path.Combine(_dir, "config.json"));
            Settings = cfg;
            cfg.ClaudeConnected = claude;
            cfg.ChatGptConnected = chatgpt;
            cfg.Engine = chatgpt ? "chatgpt" : claude ? "claude" : "classic";
            _http = new ResilientHttp(() => new FakeHandler((_, ct) =>
            {
                var request = new PendingRequest { Token = ct };
                Requests.Add(request);
                if (CompleteUnexpectedRequests && Requests.Count > 1) request.Complete();
                return request.Response.Task;
            }), attemptTimeout: TimeSpan.FromSeconds(5), retryDelays: []);
            var session = new ClaudeSession(model =>
            {
                var process = new PendingClaude(model);
                ClaudeProcesses.Add(process);
                return process;
            });
            Window = new MainWindow(cfg, _http, session, ChatGpt);
        }

        public Task Start(string text = "Hello world")
        {
            Window.SrcBox.Text = text;
            return Window.TranslateAsync();
        }

        public void Dispose()
        {
            Window.StopBackgroundWork();
            Window.Close();
            foreach (var request in Requests) request.Complete();
            foreach (var request in ChatGpt.Requests) request.Answer.TrySetCanceled();
            _http.Dispose();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("nextgen")]
    [InlineData("claude")]
    public Task changing_translation_selection_cancels_request_before_debounce(string selection) => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        var run = h.Start();
        var pending = Assert.Single(h.Requests);

        switch (selection)
        {
            case "source": h.Window.SrcCombo.SelectedItem = "Englisch"; break;
            case "target": h.Window.TgtCombo.SelectedItem = "Türkisch"; break;
            case "nextgen": h.Window.NextgenRb.IsChecked = true; break;
            case "claude": h.Window.ClaudeRb.IsChecked = true; break;
        }

        // Check synchronously, before a timer can start a replacement request.
        var cancelledImmediately = pending.Token.IsCancellationRequested;
        pending.Complete();
        await run;
        Assert.True(cancelledImmediately, "the obsolete request kept running during debounce");
        Assert.Empty(h.Window.TgtBox.Text);
        Assert.Single(h.Requests);
    });

    [Fact]
    public Task old_detection_cannot_override_newly_selected_target() => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        h.CompleteUnexpectedRequests = true;
        var run = h.Start("Hallo Welt");
        h.Window.TgtCombo.SelectedItem = "Türkisch";
        h.Requests[0].Complete(detected: "DE");
        await run;

        Assert.Equal("Türkisch", h.Window.TgtCombo.SelectedItem);
        Assert.Empty(h.Window.TgtBox.Text);
        Assert.Single(h.Requests);
    });

    [Theory]
    [InlineData("tone")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("engine")]
    [InlineData("disconnect")]
    [InlineData("text")]
    [InlineData("short text")]
    [InlineData("over limit")]
    public Task changing_claude_options_stops_active_process_immediately(string selection) => dispatcher.Run(async () =>
    {
        using var h = new Harness(claude: true);
        var run = h.Start();
        var active = h.ClaudeProcesses[0];
        Assert.Single(active.Written);

        switch (selection)
        {
            case "tone": h.Window.ToneFormalRb.IsChecked = true; break;
            case "source": h.Window.SrcCombo.SelectedItem = "Englisch"; break;
            case "target": h.Window.TgtCombo.SelectedItem = "Türkisch"; break;
            case "engine": h.Window.ClassicRb.IsChecked = true; break;
            case "text": h.Window.SrcBox.Text = "Other words"; break; // a typed change, shorter than a paste
            case "short text": h.Window.SrcBox.Text = "hi"; break;
            case "over limit": h.Window.SrcBox.Text = new string('a', DeepLApi.MaxChars + 1); break;
            case "disconnect":
                h.Window.MenuItemClaudeDisconnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                break;
        }

        var killedImmediately = active.Killed;
        // Also let a late answer arrive, as when the process exits just after cancellation.
        active.Answer.TrySetResult(JsonSerializer.Serialize(new { type = "result", result = "lang: EN\nOld answer" }));
        await run;
        Assert.True(killedImmediately, $"the old Claude process kept running during debounce; status={h.Window.StatusText.Text}; processes={h.ClaudeProcesses.Count}; killed={string.Join(',', h.ClaudeProcesses.Select(p => p.Killed))}");
        Assert.Empty(h.Window.TgtBox.Text);
        Assert.Empty(h.Requests);
        Assert.All(h.ClaudeProcesses.Skip(1), p => Assert.Empty(p.Written));
        if (selection is "engine" or "disconnect") Assert.All(h.ClaudeProcesses, p => Assert.True(p.Killed));
    });

    [Fact]
    public Task disconnected_claude_sends_nothing() => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        h.Window.ClaudeRb.IsChecked = true;
        await h.Start();
        Assert.Empty(h.Requests);
        Assert.Empty(h.ClaudeProcesses);
        Assert.Equal(Loc.StatusClaudeDisconnected, h.Window.StatusText.Text);
    });

    [Fact]
    public Task automatic_language_switch_still_translates_in_the_corrected_direction() => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        h.CompleteUnexpectedRequests = true;
        var run = h.Start("Hallo Welt");
        h.Requests[0].Complete(detected: "DE");
        await run;

        Assert.Equal("Englisch (US)", h.Window.TgtCombo.SelectedItem);
        Assert.Equal("Old answer", h.Window.TgtBox.Text);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("DeepL", h.Window.StatusText.Text);
    });

    [Fact]
    public Task late_old_response_cannot_replace_new_result() => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        var oldRun = h.Start();
        var newRun = h.Start("A newer sentence");
        h.Requests[1].Complete("New answer");
        await newRun;
        h.Requests[0].Complete();
        await oldRun;

        Assert.Equal("New answer", h.Window.TgtBox.Text);
    });

    [Theory]
    [InlineData("")]
    [InlineData("hi")]
    [InlineData("over limit")]
    public Task editing_text_cancels_request_even_when_new_text_cannot_translate(string text) => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        var run = h.Start();
        var pending = Assert.Single(h.Requests);
        h.Window.SrcBox.Text = text == "over limit" ? new string('a', DeepLApi.MaxChars + 1) : text;
        var cancelledImmediately = pending.Token.IsCancellationRequested;
        pending.Complete();
        await run;
        await h.Window.TranslateAsync();

        Assert.True(cancelledImmediately);
        Assert.Empty(h.Window.TgtBox.Text);
        Assert.Single(h.Requests);
    });

    [Fact]
    public Task stopping_window_cancels_request_and_prevents_another_translation() => dispatcher.Run(async () =>
    {
        using var h = new Harness();
        var run = h.Start();
        var pending = Assert.Single(h.Requests);
        h.Window.StopBackgroundWork();
        var cancelled = pending.Token.IsCancellationRequested;
        pending.Complete();
        await run;
        await h.Window.TranslateAsync();

        Assert.True(cancelled);
        Assert.Single(h.Requests);
        Assert.Empty(h.Window.TgtBox.Text);
    });

    [Fact]
    public Task stopping_window_kills_active_and_waiting_claude_processes() => dispatcher.Run(async () =>
    {
        using var h = new Harness(claude: true);
        var run = h.Start();
        Assert.Equal(2, h.ClaudeProcesses.Count);
        h.Window.StopBackgroundWork();
        await run;
        h.Window.StopBackgroundWork(); // App also calls this from OnExit.
        await h.Window.TranslateAsync();

        Assert.All(h.ClaudeProcesses, p => Assert.True(p.Killed));
        Assert.Single(h.ClaudeProcesses[0].Written);
        Assert.Empty(h.ClaudeProcesses[1].Written);
        Assert.Empty(h.Window.TgtBox.Text);
    });

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("tone")]
    [InlineData("deepl")]
    [InlineData("claude")]
    [InlineData("text")]
    [InlineData("short text")]
    [InlineData("over limit")]
    [InlineData("disconnect")]
    [InlineData("stop")]
    public Task changing_chatgpt_options_cancels_immediately_and_ignores_late_answers(string change) => dispatcher.Run(async () =>
    {
        using var h = new Harness(chatgpt: true);
        var run = h.Start();
        var request = Assert.Single(h.ChatGpt.Requests);
        switch (change)
        {
            case "source": h.Window.SrcCombo.SelectedItem = "Englisch"; break;
            case "target": h.Window.TgtCombo.SelectedItem = "Türkisch"; break;
            case "tone": h.Window.ToneFormalRb.IsChecked = true; break;
            case "deepl": h.Window.ClassicRb.IsChecked = true; break;
            case "claude": h.Window.ClaudeRb.IsChecked = true; break;
            case "text": h.Window.SrcBox.Text = "A newer sentence"; break;
            case "short text": h.Window.SrcBox.Text = "hi"; break;
            case "over limit": h.Window.SrcBox.Text = new string('a', DeepLApi.MaxChars + 1); break;
            case "disconnect": h.Window.MenuItemChatGptDisconnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); break;
            case "stop": h.Window.StopBackgroundWork(); break;
        }
        var cancelled = request.Token.IsCancellationRequested;
        request.Answer.SetResult(("Old answer", "DE"));
        await run;

        Assert.True(cancelled);
        Assert.Empty(h.Window.TgtBox.Text);
        Assert.Empty(h.Requests);
        Assert.Empty(h.ClaudeProcesses);
        if (change == "target") Assert.Equal("Türkisch", h.Window.TgtCombo.SelectedItem);
    });

    [Fact]
    public Task chatgpt_routing_uses_selected_tone_and_never_contacts_other_engines() => dispatcher.Run(async () =>
    {
        using var h = new Harness(chatgpt: true);
        h.Window.ToneInformalRb.IsChecked = true;
        var run = h.Start();
        var request = Assert.Single(h.ChatGpt.Requests);
        Assert.Equal("informal", request.Tone);
        request.Answer.SetResult(("Hallo Welt", "EN"));
        await run;
        Assert.Equal("Hallo Welt", h.Window.TgtBox.Text);
        Assert.Contains("ChatGPT", h.Window.StatusText.Text);
        Assert.Empty(h.Requests);
        Assert.Empty(h.ClaudeProcesses);
    });

    [Fact]
    public Task chatgpt_disconnect_blocks_sends_and_tones_stay_independent() => dispatcher.Run(async () =>
    {
        using var h = new Harness(chatgpt: true);
        h.Window.ToneFormalRb.IsChecked = true;
        h.Window.ClaudeRb.IsChecked = true;
        h.Window.ToneInformalRb.IsChecked = true;
        h.Window.ChatGptRb.IsChecked = true;
        Assert.True(h.Window.ToneFormalRb.IsChecked);
        Assert.Equal("formal", h.Settings.ChatGptTone);
        Assert.Equal("informal", h.Settings.ClaudeTone);
        h.Window.MenuItemChatGptDisconnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await h.Start();
        Assert.False(h.Settings.ChatGptConnected);
        Assert.Empty(h.ChatGpt.Requests);
        Assert.Empty(h.Requests);
        Assert.Empty(h.ClaudeProcesses);
        Assert.Equal(Loc.StatusChatGptDisconnected, h.Window.StatusText.Text);
    });

    [Fact]
    public Task chatgpt_backend_lifecycle_follows_selection_disconnect_and_exit() => dispatcher.Run(() =>
    {
        using var h = new Harness(chatgpt: true);
        Assert.True(h.ChatGpt.Warmups > 0);
        var stops = h.ChatGpt.Stops;
        h.Window.ClassicRb.IsChecked = true;
        Assert.Equal(++stops, h.ChatGpt.Stops);
        var warmups = h.ChatGpt.Warmups;
        h.Window.ChatGptRb.IsChecked = true;
        Assert.Equal(warmups + 1, h.ChatGpt.Warmups);
        h.Window.MenuItemChatGptDisconnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(++stops, h.ChatGpt.Stops);
        h.Window.StopBackgroundWork();
        Assert.Equal(++stops, h.ChatGpt.Stops);
        h.Window.StopBackgroundWork();
        Assert.Equal(stops, h.ChatGpt.Stops);
        return Task.CompletedTask;
    });

    [Fact]
    public Task disconnect_during_chatgpt_sign_in_check_cannot_reconnect_later() => dispatcher.Run(async () =>
    {
        using var h = new Harness(chatgpt: true);
        var auth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ChatGpt.Auth = auth.Task;
        h.Window.MenuItemChatGptConnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        h.Window.MenuItemChatGptDisconnect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        auth.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(h.Settings.ChatGptConnected);
        Assert.Equal(Loc.StatusChatGptDisconnectedNow, h.Window.StatusText.Text);
        Assert.Empty(h.ChatGpt.Requests);
    });

    [Fact]
    public Task text_already_in_target_language_switches_target_without_second_chatgpt_request() => dispatcher.Run(async () =>
    {
        // German text with German as target: the request already asked for English in that case,
        // so the answer is shown and only the dropdown changes. A second request would double the wait.
        using var h = new Harness(chatgpt: true);
        h.Window.TgtCombo.SelectedItem = "Deutsch";
        var run = h.Start("Hallo Welt");
        var request = Assert.Single(h.ChatGpt.Requests);
        request.Answer.SetResult(("Hello world", "DE"));
        await run;
        Assert.Single(h.ChatGpt.Requests);
        Assert.Equal("Hello world", h.Window.TgtBox.Text);
        Assert.Equal("Englisch (US)", h.Window.TgtCombo.SelectedItem);
    });

    [Fact]
    public Task pasted_text_is_translated_without_waiting_for_typing_pause() => dispatcher.Run(async () =>
    {
        using var h = new Harness(chatgpt: true);
        // Setting the whole text at once is what a paste does.
        h.Window.SrcBox.Text = "This sentence was pasted in one piece.";
        var request = Assert.Single(h.ChatGpt.Requests);
        request.Answer.SetResult(("Dieser Satz wurde am Stück eingefügt.", "EN"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Dieser Satz wurde am Stück eingefügt.", h.Window.TgtBox.Text);
    });

    [Fact]
    public void single_typed_characters_wait_for_typing_pause_but_paste_does_not()
    {
        Assert.False(MainWindow.IsPasteLike([1]));
        Assert.False(MainWindow.IsPasteLike([MainWindow.PasteLikeChars - 1]));
        Assert.True(MainWindow.IsPasteLike([MainWindow.PasteLikeChars]));
        Assert.True(MainWindow.IsPasteLike([1, 250]));
    }
}
