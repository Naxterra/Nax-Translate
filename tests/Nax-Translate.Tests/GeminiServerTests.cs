using System.IO;
using System.Text.Json;

namespace NaxTranslate.Tests;

/// <summary>Covers the Gemini engine without starting the real Antigravity CLI.</summary>
public sealed class GeminiServerTests
{
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A fake "agy acp" process that answers like the real one, as seen in a live run.</summary>
    sealed class Connection : IGeminiConnection
    {
        public readonly List<(string Method, JsonElement Parameters)> Calls = [];
        readonly Queue<JsonElement> _notifications = new();
        public readonly List<string> Forgotten = [];
        public bool HasExited { get; private set; }
        public bool AgentAskedForSomething { get; set; }
        public bool SignedIn = true, ToolCall;
        public string StopReason = "end_turn";
        public string Reply = "lang: DE\nThe delivery will not arrive tomorrow.";
        int _sessions;

        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var p = Json(parameters);
            Calls.Add((method, p));
            switch (method)
            {
                case "initialize": return Task.FromResult(Json(new { protocolVersion = 1 }));
                case "session/new":
                    if (!SignedIn) throw new GeminiRequestException("Authentication required");
                    return Task.FromResult(Json(new { sessionId = $"session-{++_sessions}" }));
                case "session/prompt":
                    var sid = p.GetProperty("sessionId").GetString();
                    if (ToolCall)
                        _notifications.Enqueue(Json(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = sid, update = new { sessionUpdate = "tool_call", title = "run_command" } } }));
                    // The answer arrives in pieces, like the real agent_message_chunk updates.
                    foreach (var part in new[] { Reply[..Math.Min(9, Reply.Length)], Reply[Math.Min(9, Reply.Length)..] })
                        _notifications.Enqueue(Json(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = sid, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = part } } } }));
                    return Task.FromResult(Json(new { stopReason = StopReason }));
                default: return Task.FromResult(Json(new { }));
            }
        }

        public IReadOnlyList<JsonElement> TakeNotifications()
        {
            var all = _notifications.ToArray();
            _notifications.Clear();
            return all;
        }

        public void ForgetSession(string sessionId) => Forgotten.Add(sessionId);
        public void Dispose() => HasExited = true;
        public int Count(string method) => Calls.Count(c => c.Method == method);
    }

    static Task<(string Text, string? DetectedLang)> Translate(GeminiServer client, string text = "Die Lieferung kommt morgen nicht an.",
        string? model = null, string tone = "natural") =>
        client.TranslateAsync(text, "en-US", "auto", model, tone, default);

    [Fact]
    public async Task warmup_checks_sign_in_and_prepares_a_session_without_sending_text()
    {
        var connection = new Connection();
        var client = new GeminiServer(() => connection);
        try
        {
            Assert.True(await client.CheckAuthAsync());
            Assert.Equal(1, connection.Count("session/new"));
            Assert.Equal(0, connection.Count("session/prompt"));
            // The prepared session already has the default model.
            var model = connection.Calls.Single(c => c.Method == "session/set_model").Parameters;
            Assert.Equal(GeminiServer.DefaultModel, model.GetProperty("modelId").GetString());
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task translation_uses_prepared_session_and_then_closes_and_deletes_it()
    {
        var connection = new Connection();
        var client = new GeminiServer(() => connection);
        try
        {
            await client.CheckAuthAsync();
            var (text, lang) = await Translate(client);
            Assert.Equal(("The delivery will not arrive tomorrow.", "DE"), (text, lang));
            // No second session was needed for the text, and its model was not set again. (The next
            // session, prepared in the background afterwards, has its own model call.)
            Assert.Single(connection.Calls, c => c.Method == "session/set_model" && c.Parameters.GetProperty("sessionId").GetString() == "session-1");
            var prompt = connection.Calls.Single(c => c.Method == "session/prompt").Parameters;
            Assert.Equal("session-1", prompt.GetProperty("sessionId").GetString());
            Assert.Equal(ChatGptCli.BuildPrompt("Die Lieferung kommt morgen nicht an.", "en-US", "auto", "natural"),
                prompt.GetProperty("prompt")[0].GetProperty("text").GetString());
            Assert.Contains(connection.Calls, c => c.Method == "session/close" && c.Parameters.GetProperty("sessionId").GetString() == "session-1");
            Assert.Contains("session-1", connection.Forgotten);
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task every_text_gets_a_fresh_session_so_no_text_carries_over()
    {
        var connection = new Connection();
        var client = new GeminiServer(() => connection);
        try
        {
            await Translate(client, "first private text");
            await Task.Delay(100); // the next session is prepared in the background
            await Translate(client, "second text");
            var prompts = connection.Calls.Where(c => c.Method == "session/prompt").Select(c => c.Parameters.GetProperty("sessionId").GetString()).ToArray();
            Assert.Equal(2, prompts.Length);
            Assert.NotEqual(prompts[0], prompts[1]);
            Assert.DoesNotContain("first private text", connection.Calls.Last(c => c.Method == "session/prompt").Parameters.ToString());
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task tool_call_rejects_the_answer_and_drops_the_backend()
    {
        var connection = new Connection { ToolCall = true };
        var client = new GeminiServer(() => connection);
        var ex = await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(Loc.StatusGeminiUnexpectedTool, ex.Message);
        Assert.True(connection.HasExited);
        client.Stop();
    }

    [Fact]
    public async Task agent_asking_the_app_for_anything_rejects_the_answer()
    {
        var connection = new Connection { AgentAskedForSomething = true };
        var client = new GeminiServer(() => connection);
        var ex = await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(Loc.StatusGeminiUnexpectedTool, ex.Message);
        client.Stop();
    }

    [Fact]
    public async Task turn_ending_other_than_normally_is_reported_as_failure()
    {
        var connection = new Connection { StopReason = "refusal" };
        var client = new GeminiServer(() => connection);
        var ex = await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(Loc.StatusGeminiFailed, ex.Message);
        client.Stop();
    }

    [Fact]
    public async Task missing_sign_in_is_reported_without_sending_text()
    {
        var connection = new Connection { SignedIn = false };
        var client = new GeminiServer(() => connection);
        Assert.False(await client.CheckAuthAsync());
        await Assert.ThrowsAsync<GeminiSignInException>(() => Translate(client));
        Assert.Equal(0, connection.Count("session/prompt"));
        client.Stop();
    }

    [Fact]
    public async Task remark_instead_of_translation_keeps_the_backend_for_the_second_try()
    {
        var connection = new Connection { Reply = "I could not find any text to translate." };
        var made = 0;
        var client = new GeminiServer(() => { made++; return connection; });
        await Assert.ThrowsAsync<UnusableReplyException>(() => Translate(client));
        Assert.False(connection.HasExited);
        Assert.Equal(1, made);
        client.Stop();
    }

    [Fact]
    public async Task disconnect_stops_the_backend()
    {
        var connection = new Connection();
        var client = new GeminiServer(() => connection);
        await client.CheckAuthAsync();
        client.Stop();
        Assert.True(connection.HasExited);
    }

    [Fact]
    public void backend_runs_the_translation_agent_with_its_own_data_folder_and_one_worker_per_session()
    {
        var args = GeminiConnection.BuildArgs();
        Assert.Equal("acp", args[0]);
        Assert.Contains("--disable-telemetry", args);
        Assert.Contains("--include-default-customizations=false", args);
        Assert.Contains("--process-per-session", args);
        Assert.Equal(GeminiConnection.AgentName, args[args.ToList().IndexOf("--agent") + 1]);
        Assert.Contains("--app-data-dir=" + GeminiConnection.DataFolderName, args);
    }

    [Fact]
    public void translation_agent_has_no_tools_and_the_shared_translation_rules()
    {
        var agent = GeminiConnection.AgentDefinition;
        Assert.Contains("excludeDefaultComponents: true", agent);
        foreach (var empty in new[] { "tools: []", "skills: []", "rules: []", "agents: []", "plugins: []", "hooks: []" })
            Assert.Contains(empty, agent);
        Assert.Contains("inheritMcp: false", agent);
        Assert.Contains(ChatGptCli.TranslationInstructions, agent);
    }

    [Fact]
    public void api_keys_and_gateway_settings_are_not_passed_on()
    {
        var env = new Dictionary<string, string?>
        {
            ["GEMINI_API_KEY"] = "k", ["GOOGLE_API_KEY"] = "k", ["GOOGLE_APPLICATION_CREDENTIALS"] = "c.json",
            ["AGY_ADC_AUTH"] = "1", ["AGY_LLM_GATEWAY_MODELS"] = "x", ["PATH"] = @"C:\bin",
        };
        GeminiConnection.PrepareEnvironment(env);
        Assert.Equal(["PATH"], env.Keys.ToArray());
    }

    [Fact]
    public void finished_sessions_are_deleted_and_sessions_in_use_are_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "NaxTranslate.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "conversations"));
            Directory.CreateDirectory(Path.Combine(root, "annotations"));
            Directory.CreateDirectory(Path.Combine(root, "brain", "old"));
            File.WriteAllText(Path.Combine(root, "conversations", "old.db"), "private text");
            File.WriteAllText(Path.Combine(root, "annotations", "old.pbtxt"), "x");
            File.WriteAllText(Path.Combine(root, "conversations", "current.db"), "in use");

            GeminiConnection.SweepSessions(root, ["current"]);

            Assert.False(File.Exists(Path.Combine(root, "conversations", "old.db")));
            Assert.False(File.Exists(Path.Combine(root, "annotations", "old.pbtxt")));
            Assert.False(Directory.Exists(Path.Combine(root, "brain", "old")));
            Assert.True(File.Exists(Path.Combine(root, "conversations", "current.db")));

            GeminiConnection.WipeData(root);
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void answer_chunks_of_other_sessions_are_ignored()
    {
        var notes = new[]
        {
            Json(new { method = "session/update", @params = new { sessionId = "other", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "old answer" } } } }),
            Json(new { method = "session/update", @params = new { sessionId = "s", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "lang: EN\n" } } } }),
            Json(new { method = "session/update", @params = new { sessionId = "s", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Hallo" } } } }),
        };
        Assert.Equal("lang: EN\nHallo", GeminiServer.CollectAnswer("s", notes, agentAsked: false));
    }
}
