using System.Text.Json;

namespace NaxTranslate.Tests;

/// <summary>Covers the Claude engine without starting the real Claude Code tool.</summary>
public class ClaudeCliTests
{
    // ── Fake Claude Code process ──

    static string Delta(string text) => JsonSerializer.Serialize(new
    {
        type = "stream_event",
        @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
    });

    static string Result(string result, bool isError = false, int? status = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["is_error"] = isError,
            ["result"] = result,
            ["api_error_status"] = status,
        });

    /// <summary>Streams the reply in small pieces and ends with the result line, like the real tool.</summary>
    static string[] Answer(string reply) =>
        [.. Enumerable.Range(0, (reply.Length + 3) / 4).Select(i => Delta(reply.Substring(i * 4, Math.Min(4, reply.Length - i * 4)))),
         Result(reply)];

    sealed class FakeProcess(string model, IEnumerable<string>? lines = null, bool hangUntilCancelled = false) : IClaudeProcess
    {
        readonly Queue<string> _lines = new(lines ?? []);
        public string   Model      { get; } = model;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public bool     HasExited  => Killed || InputClosed && _lines.Count == 0;
        public bool     Killed, InputClosed, Disposed;
        public bool     DiedBeforeWrite;              // the pipe is closed, as when the process ended
        public TimeSpan PausePerLine = TimeSpan.Zero; // how long each line takes to arrive
        public string   Stderr = "";
        public readonly List<string> Written = [];

        public bool     WriteBlocks;                  // the process never reads its input, as when it hangs at startup
        readonly TaskCompletionSource _killedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            if (DiedBeforeWrite) throw new System.IO.IOException("The pipe has been ended.");
            if (WriteBlocks)
            {
                // Like a full pipe: only killing the process ends the write, with a broken pipe.
                await _killedSignal.Task;
                throw new System.IO.IOException("The pipe has been ended.");
            }
            Written.Add(line);
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            if (PausePerLine > TimeSpan.Zero)
                await Task.Delay(PausePerLine, ct);
            if (hangUntilCancelled && _lines.Count == 0)
                await Task.Delay(Timeout.Infinite, ct);
            return _lines.Count > 0 ? _lines.Dequeue() : null;
        }

        public Task<string> StderrAsync() => Task.FromResult(Stderr);
        public void CloseInput() => InputClosed = true;
        public void Kill()       { Killed = true; _killedSignal.TrySetResult(); }
        public void Dispose()    => Disposed = true;
    }

    /// <summary>Hands out the prepared processes in order and remembers every one it started.</summary>
    sealed class FakeFactory(params Func<string, FakeProcess>[] next)
    {
        int _i;
        public readonly List<FakeProcess> Started = [];
        public IClaudeProcess Start(string model)
        {
            var p = _i < next.Length ? next[_i](model) : new FakeProcess(model);
            _i++;
            Started.Add(p);
            return p;
        }
    }

    static FakeFactory Replies(params string[][] answers) =>
        new([.. answers.Select(a => (Func<string, FakeProcess>)(m => new FakeProcess(m, a)))]);

    // ── Reading Claude's answer ──

    [Fact]
    public async Task claude_reply_split_into_detected_language_and_translation()
    {
        var f = Replies(Answer("lang: EN\nDer Bericht ist am Freitag fällig."));
        using var s = new ClaudeSession(f.Start);
        var (text, lang) = await s.TranslateAsync("The report is due on Friday.", "de");
        Assert.Equal("Der Bericht ist am Freitag fällig.", text);
        Assert.Equal("EN", lang);
    }

    [Fact]
    public async Task claude_reply_with_several_lines_keeps_its_line_breaks()
    {
        var f = Replies(Answer("LANG: en\r\n\r\nZeile eins\r\n\r\nZeile drei"));
        using var s = new ClaudeSession(f.Start);
        var (text, lang) = await s.TranslateAsync("Line one\n\nLine three", "de");
        Assert.Equal("Zeile eins\n\nZeile drei", text);
        Assert.Equal("EN", lang);
    }

    [Fact]
    public async Task translation_shown_word_by_word_without_language_line()
    {
        var f = Replies(Answer("lang: EN\nDer Bericht ist fertig."));
        using var s = new ClaudeSession(f.Start);
        var parts = new List<(string Text, string? Lang)>();
        await s.TranslateAsync("The report is done.", "de", onPartial: (t, l) => parts.Add((t, l)));
        Assert.True(parts.Count > 2, "the translation should arrive in several steps");
        Assert.All(parts, p => Assert.DoesNotContain("lang", p.Text, StringComparison.OrdinalIgnoreCase));
        Assert.All(parts, p => Assert.Equal("EN", p.Lang));
        Assert.Equal("Der Bericht ist fertig.", parts[^1].Text);
    }

    [Fact]
    public void unfinished_language_line_hidden_until_complete()
    {
        Assert.Equal("", ClaudeCli.VisiblePart("la").Text);
        Assert.Equal("", ClaudeCli.VisiblePart("lang: E").Text);
        Assert.Equal(("Der", "EN"), ClaudeCli.VisiblePart("lang: EN\n\nDer"));
        Assert.Equal(("Guten Mor", null), ClaudeCli.VisiblePart("Guten Mor"));
    }

    [Fact]
    public void claude_reply_without_language_line_is_kept_whole_as_translation()
    {
        var (text, lang) = ClaudeCli.ParseReply("Guten Morgen,\nwie geht es dir?");
        Assert.Equal("Guten Morgen,\nwie geht es dir?", text);
        Assert.Null(lang);
    }

    [Fact]
    public void translation_starting_with_two_letter_line_is_never_cut()
    {
        // "Hi" is also the code for Hindi, so only a "lang:" line may count as the language.
        var (text, lang) = ClaudeCli.ParseReply("Hi\nhow are you?");
        Assert.Equal("Hi\nhow are you?", text);
        Assert.Null(lang);
    }

    [Fact]
    public void claude_empty_reply_raises_error_instead_of_showing_blank_translation()
    {
        Assert.ThrowsAny<Exception>(() => ClaudeCli.ParseReply("  \n "));
    }

    // ── Errors ──

    [Fact]
    public async Task claude_usage_limit_answer_raises_limit_reached()
    {
        var f = Replies([Result("You've hit your limit · resets 11pm", isError: true)]);
        using var s = new ClaudeSession(f.Start);
        await Assert.ThrowsAsync<ClaudeLimitReachedException>(() => s.TranslateAsync("Hello there", "de"));
    }

    [Fact]
    public async Task claude_too_many_requests_reported_as_busy_not_as_used_up_limit()
    {
        var f = Replies([Result("API Error: 429 rate_limit_error", isError: true, status: 429)]);
        using var s = new ClaudeSession(f.Start);
        await Assert.ThrowsAsync<ClaudeBusyException>(() => s.TranslateAsync("Hello there", "de"));
    }

    [Fact]
    public async Task claude_context_limit_error_not_reported_as_usage_limit()
    {
        var f = Replies([Result("Prompt is too long: context limit exceeded", isError: true)]);
        using var s = new ClaudeSession(f.Start);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.TranslateAsync("Hello there", "de"));
        Assert.IsNotType<ClaudeLimitReachedException>(ex);
        Assert.IsNotType<ClaudeBusyException>(ex);
    }

    [Fact]
    public async Task claude_tool_ending_without_answer_reports_its_error_message()
    {
        var f = new FakeFactory(m => new FakeProcess(m) { Stderr = "Invalid API key · Please run /login\n" });
        using var s = new ClaudeSession(f.Start);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.TranslateAsync("Hello there", "de"));
        Assert.Contains("Please run /login", ex.Message);
    }

    // ── Quota safety ──

    [Fact]
    public async Task text_over_character_limit_never_sent_to_claude()
    {
        var f = new FakeFactory();
        using var s = new ClaudeSession(f.Start);
        await Assert.ThrowsAsync<ArgumentException>(
            () => s.TranslateAsync(new string('a', DeepLApi.MaxChars + 1), "de"));
        Assert.Empty(f.Started);
    }

    [Fact]
    public async Task claude_already_cancelled_translation_never_starts_tool()
    {
        var f = new FakeFactory();
        using var s = new ClaudeSession(f.Start);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => s.TranslateAsync("Hello there", "de", ct: cts.Token));
        Assert.Empty(f.Started);
    }

    [Fact]
    public async Task translation_cancelled_while_running_kills_claude_process()
    {
        var f = new FakeFactory(m => new FakeProcess(m, [Delta("lang: EN\nHal")], hangUntilCancelled: true));
        using var s = new ClaudeSession(f.Start);
        using var cts = new CancellationTokenSource();
        var run = s.TranslateAsync("Hello there", "de", ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(f.Started[0].Killed);
    }

    [Fact]
    public async Task each_claude_process_translates_only_once_so_no_text_carries_over()
    {
        var f = Replies(Answer("lang: EN\nEins"), Answer("lang: EN\nZwei"));
        using var s = new ClaudeSession(f.Start);
        await s.TranslateAsync("One", "de");
        var (second, _) = await s.TranslateAsync("Two", "de");
        Assert.Equal("Zwei", second);
        Assert.True(f.Started[0].InputClosed, "the first process must be told to end");
        Assert.Single(f.Started[0].Written);
        Assert.Single(f.Started[1].Written);
    }

    [Fact]
    public async Task next_translation_uses_process_started_ahead_of_time()
    {
        var f = Replies(Answer("lang: EN\nHallo"));
        using var s = new ClaudeSession(f.Start);
        s.WarmUp("sonnet");
        var waiting = f.Started.Single();
        await s.TranslateAsync("Hello there", "de", model: "sonnet");
        Assert.Single(waiting.Written);
        // A new process waits for the translation after that.
        Assert.Equal(2, f.Started.Count);
        Assert.True(s.HasSpare);
    }

    [Fact]
    public async Task waiting_process_for_other_model_or_too_old_is_replaced()
    {
        var f = Replies([], [], Answer("lang: EN\nHallo"));
        using var s = new ClaudeSession(f.Start);
        s.WarmUp("haiku");
        s.WarmUp("sonnet");                    // other model: the haiku process is ended
        Assert.True(f.Started[0].Killed);
        f.Started[1].StartedUtc = DateTime.UtcNow - ClaudeSession.MaxSpareAge - TimeSpan.FromMinutes(1);
        await s.TranslateAsync("Hello there", "de", model: "sonnet");
        Assert.True(f.Started[1].Killed, "a process older than the limit must not be used");
        Assert.Single(f.Started[2].Written);
    }

    [Fact]
    public async Task waiting_process_that_died_replaced_and_translation_still_succeeds()
    {
        var f = new FakeFactory(
            m => new FakeProcess(m) { DiedBeforeWrite = true },   // the waiting process ended meanwhile
            m => new FakeProcess(m, Answer("lang: EN\nHallo")));
        using var s = new ClaudeSession(f.Start);
        var (text, _) = await s.TranslateAsync("Hello there", "de");
        Assert.Equal("Hallo", text);
        Assert.Single(f.Started[1].Written);
    }

    [Fact]
    public async Task long_answer_still_arriving_is_not_cut_off_by_timeout()
    {
        var old = ClaudeSession.IdleTimeout;
        ClaudeSession.IdleTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            // Eight pieces, 100 ms apart: more than twice the timeout in total, but never silent for long.
            var f = new FakeFactory(m => new FakeProcess(m, Answer("lang: EN\nEin langer Satz.")) { PausePerLine = TimeSpan.FromMilliseconds(100) });
            using var s = new ClaudeSession(f.Start);
            var (text, _) = await s.TranslateAsync("A long sentence.", "de");
            Assert.Equal("Ein langer Satz.", text);
        }
        finally { ClaudeSession.IdleTimeout = old; }
    }

    [Fact]
    public async Task claude_silent_for_too_long_stopped_and_reported_as_timeout()
    {
        var old = ClaudeSession.IdleTimeout;
        ClaudeSession.IdleTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var f = new FakeFactory(m => new FakeProcess(m, [Delta("lang: EN\nHal")], hangUntilCancelled: true));
            using var s = new ClaudeSession(f.Start);
            await Assert.ThrowsAsync<TimeoutException>(() => s.TranslateAsync("Hello there", "de"));
            Assert.True(f.Started[0].Killed);
        }
        finally { ClaudeSession.IdleTimeout = old; }
    }

    [Fact]
    public async Task claude_hanging_before_reading_text_stopped_by_timeout_and_killed()
    {
        var old = ClaudeSession.IdleTimeout;
        ClaudeSession.IdleTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var f = new FakeFactory(m => new FakeProcess(m) { WriteBlocks = true });
            using var s = new ClaudeSession(f.Start);
            await Assert.ThrowsAsync<TimeoutException>(() => s.TranslateAsync("Hello there", "de"));
            Assert.True(f.Started[0].Killed);
            // No fresh process may be tried after the timeout, because that would send the text after all.
            Assert.All(f.Started.Skip(1), p => Assert.Empty(p.Written));
        }
        finally { ClaudeSession.IdleTimeout = old; }
    }

    [Fact]
    public async Task text_changed_while_claude_hangs_before_reading_kills_it_and_sends_nothing()
    {
        var f = new FakeFactory(m => new FakeProcess(m) { WriteBlocks = true });
        using var s = new ClaudeSession(f.Start);
        using var cts = new CancellationTokenSource();
        var run = s.TranslateAsync("Hello there", "de", ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(f.Started[0].Killed);
        Assert.All(f.Started, p => Assert.Empty(p.Written));
    }

    [Fact]
    public void switching_back_to_deepl_ends_waiting_claude_process()
    {
        var f = new FakeFactory();
        var s = new ClaudeSession(f.Start);
        s.WarmUp(null);
        s.DropSpare();
        Assert.True(f.Started.Single().Killed);
        Assert.False(s.HasSpare);
    }

    // ── How Claude Code is started ──

    [Fact]
    public void claude_run_skips_hooks_tools_and_saved_sessions()
    {
        // Without safe mode the user's own hooks (for example a review on every stop) would run
        // after each translation and use up the quota. This checks the arguments the app builds,
        // not how the real tool reacts to them.
        var args = ClaudeCli.BuildArgs("haiku").ToList();
        Assert.Contains("-p", args);
        Assert.Contains("--safe-mode", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--strict-mcp-config", args);
        var tools = args.IndexOf("--tools");
        Assert.True(tools >= 0 && args[tools + 1] == "", "all tools must be switched off");
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task empty_claude_model_setting_falls_back_to_default_model()
    {
        var f = Replies(Answer("lang: EN\nHallo"));
        using var s = new ClaudeSession(f.Start);
        await s.TranslateAsync("Hello there", "de", model: "");
        Assert.Equal(ClaudeCli.DefaultModel, f.Started[0].Model);
    }

    [Fact]
    public void api_key_variables_removed_so_claude_uses_the_subscription()
    {
        var env = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"]       = "sk-test",
            ["ANTHROPIC_AUTH_TOKEN"]    = "token",
            ["ANTHROPIC_BASE_URL"]      = "http://example.invalid",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["CLAUDECODE"]              = "1",
            ["PATH"]                    = @"C:\bin",
        };
        ClaudeCli.PrepareEnvironment(env);
        Assert.Equal(["PATH"], env.Keys.ToArray());
    }

    [Fact]
    public void signed_in_claude_code_reports_its_plan()
    {
        var auth = ClaudeCli.ParseAuthStatus("""{"loggedIn":true,"authMethod":"claude.ai","email":"someone@example.com","subscriptionType":"max"}""");
        Assert.True(auth.LoggedIn);
        Assert.Equal("Max", auth.Plan);
    }

    [Fact]
    public void signed_out_claude_code_reported_as_not_signed_in_without_plan()
    {
        var auth = ClaudeCli.ParseAuthStatus("""{"loggedIn":false,"subscriptionType":"pro"}""");
        Assert.False(auth.LoggedIn);
        Assert.Null(auth.Plan);
    }

    [Fact]
    public void unreadable_sign_in_status_counts_as_not_signed_in()
    {
        Assert.False(ClaudeCli.ParseAuthStatus("Error: something went wrong").LoggedIn);
        Assert.False(ClaudeCli.ParseAuthStatus("").LoggedIn);
    }

    [Fact]
    public void natural_tone_asks_for_idiomatic_german_as_used_in_germany()
    {
        var prompt = ClaudeCli.BuildPrompt("See you tomorrow!", "de", "auto", ClaudeCli.ToneNatural);
        Assert.Contains("Tone: natural", prompt);
        Assert.Contains("as it is used in Germany", prompt);
    }

    [Fact]
    public void formal_tone_asks_for_sie_and_informal_tone_for_du()
    {
        Assert.Contains("\"Sie\"", ClaudeCli.BuildPrompt("Can you help me?", "de", "en", ClaudeCli.ToneFormal));
        Assert.Contains("Tone: formal", ClaudeCli.BuildPrompt("Can you help me?", "de", "en", ClaudeCli.ToneFormal));
        Assert.Contains("\"du\"", ClaudeCli.BuildPrompt("Can you help me?", "de", "en", ClaudeCli.ToneInformal));
        Assert.Contains("Tone: informal", ClaudeCli.BuildPrompt("Can you help me?", "de", "en", ClaudeCli.ToneInformal));
    }

    [Fact]
    public void unknown_or_missing_tone_setting_falls_back_to_natural()
    {
        Assert.Equal(ClaudeCli.ToneNatural, ClaudeCli.NormalizeTone(null));
        Assert.Equal(ClaudeCli.ToneNatural, ClaudeCli.NormalizeTone("sarcastic"));
        Assert.Contains("Tone: natural", ClaudeCli.BuildPrompt("Hi", "de", "auto", "sarcastic"));
    }

    [Fact]
    public async Task chosen_tone_reaches_claude_with_the_text()
    {
        var f = Replies(Answer("lang: EN\nKannst du mir helfen?"));
        using var s = new ClaudeSession(f.Start);
        await s.TranslateAsync("Can you help me?", "de", tone: ClaudeCli.ToneInformal);
        Assert.Contains("Tone: informal", f.Started[0].Written.Single());
    }

    [Fact]
    public void request_names_english_for_german_text_when_german_is_target()
    {
        var prompt = ClaudeCli.BuildPrompt("Hallo Welt", "de", "auto");
        Assert.Contains("Target language: English (American) if the text is written in German, otherwise German.", prompt);
    }

    [Fact]
    public void german_on_both_sides_on_purpose_gets_no_other_target()
    {
        var prompt = ClaudeCli.BuildPrompt("Hallo Welt", "de", "de");
        Assert.Contains("Target language: German\n", prompt);
        Assert.DoesNotContain("otherwise", prompt);
    }

    [Fact]
    public void wrong_fixed_source_still_gets_other_target_for_text_in_target_language()
    {
        Assert.Equal("en-US", ClaudeCli.FallbackTarget("de", "en"));
        Assert.Equal("de", ClaudeCli.FallbackTarget("tr", "auto"));
        Assert.Null(ClaudeCli.FallbackTarget("en-GB", "en"));
    }

    [Fact]
    public async Task text_goes_to_claude_as_json_message_with_target_language_named()
    {
        var f = Replies(Answer("lang: DE\nHello"));
        using var s = new ClaudeSession(f.Start);
        await s.TranslateAsync("Hallo \"Welt\" & <tag>\nZeile zwei", "en-GB", "de");
        using var doc = JsonDocument.Parse(f.Started[0].Written.Single());
        var prompt = doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("otherwise English (British)", prompt);
        Assert.Contains("Source language: German", prompt);
        Assert.Contains("Hallo \"Welt\" & <tag>\nZeile zwei", prompt);
        // The text must never end up on the command line, where quotes could break the call.
        Assert.DoesNotContain(ClaudeCli.BuildArgs("sonnet"), a => a.Contains("Welt"));
    }
}
