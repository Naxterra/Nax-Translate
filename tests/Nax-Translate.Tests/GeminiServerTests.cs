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
        /// <summary>What Antigravity announces after a session starts. Null sends no list at all.</summary>
        public string[]? Commands = ["plan", "grill-me", "learn"];
        public GeminiRequestException? SessionError;
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
                    if (!SignedIn) throw new GeminiRequestException(GeminiRequestException.AuthRequiredCode, "Authentication required");
                    if (SessionError != null) throw SessionError;
                    var id = $"session-{++_sessions}";
                    if (Commands != null)
                        _notifications.Enqueue(Json(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = id, update = new
                        { sessionUpdate = "available_commands_update", availableCommands = Commands.Select(c => new { name = c, description = "" }).ToArray() } } }));
                    return Task.FromResult(Json(new { sessionId = id }));
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

    [Fact]
    public async Task session_of_the_default_coding_agent_never_gets_text()
    {
        // These are the commands agy 1.5.9 announced when it ignored the app's agent.
        var connection = new Connection { Commands = ["goal", "compact", "schedule", "browser", "plan", "grill-me"] };
        var client = new GeminiServer(() => connection);
        var check = await Assert.ThrowsAsync<Exception>(() => client.CheckAuthAsync());
        Assert.Equal(Loc.StatusGeminiAgentUnconfirmed, check.Message);
        Assert.True(connection.HasExited);
        var translate = await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(Loc.StatusGeminiAgentUnconfirmed, translate.Message);
        Assert.Equal(0, connection.Count("session/prompt"));
        client.Stop();
    }

    [Fact]
    public async Task session_that_never_reports_its_agent_commands_never_gets_text()
    {
        var connection = new Connection { Commands = null };
        var client = new GeminiServer(() => connection, agentWait: TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(Loc.StatusGeminiAgentUnconfirmed, ex.Message);
        Assert.Equal(0, connection.Count("session/prompt"));
        client.Stop();
    }

    [Fact]
    public async Task other_errors_mentioning_signatures_or_authors_are_not_mistaken_for_missing_sign_in()
    {
        var connection = new Connection { SessionError = new GeminiRequestException(-32603, "invalid argument: signature of author") };
        var client = new GeminiServer(() => connection);
        var ex = await Assert.ThrowsAsync<GeminiRequestException>(() => client.CheckAuthAsync());
        Assert.Equal(-32603, ex.Code);
        await Assert.ThrowsAsync<GeminiRequestException>(() => Translate(client));
        client.Stop();
    }

    [Fact]
    public async Task failed_background_preparation_drops_the_backend_so_the_next_text_gets_a_new_one()
    {
        var connections = new List<Connection>();
        var client = new GeminiServer(() => { var c = new Connection(); connections.Add(c); return c; });
        try
        {
            await Translate(client);
            // The session for the next text is prepared in the background after this answer. Here
            // it fails, which must not leave the first process in use.
            connections[0].SessionError = new GeminiRequestException(-32603, "internal error");
            await Translate(client);
            await WaitUntil(() => connections[0].HasExited);
            await Translate(client);
            Assert.Equal(2, connections.Count);
            Assert.Equal(1, connections[1].Count("session/prompt"));
        }
        finally { client.Stop(); }
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>The agent's standard output, fed line by line by a test.</summary>
    sealed class FedReader : TextReader
    {
        readonly System.Threading.Channels.Channel<string?> _lines = System.Threading.Channels.Channel.CreateUnbounded<string?>();
        public void Send(string line) => _lines.Writer.TryWrite(line);
        public void End() => _lines.Writer.TryWrite(null);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.ReadAsync(cancellationToken);
        public override Task<string?> ReadLineAsync() => ReadLineAsync(default).AsTask();
    }

    static (AcpChannel Channel, FedReader Output, StringWriter Input, Func<int> ToolUses) NewChannel()
    {
        var output = new FedReader();
        var input = new StringWriter();
        var uses = 0;
        return (new AcpChannel(output, input, () => Interlocked.Increment(ref uses)), output, input, () => Volatile.Read(ref uses));
    }

    [Fact]
    public async Task late_reply_to_a_request_that_gave_up_never_reaches_the_next_request()
    {
        var (channel, output, _, _) = NewChannel();
        using (var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.RequestAsync("session/close", new { }, limit.Token));
        var next = channel.RequestAsync("session/new", new { }, default);
        // The slow close answers first, then the new session.
        output.Send("""{"jsonrpc":"2.0","id":1,"result":{"closed":true}}""");
        output.Send("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s2","update":{"sessionUpdate":"usage_update"}}}""");
        output.Send("""{"jsonrpc":"2.0","id":2,"result":{"sessionId":"s2"}}""");
        var result = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s2", result.GetProperty("sessionId").GetString());
        Assert.Single(channel.TakeNotifications());
        Assert.False(channel.IsClosed);
    }

    [Fact]
    public async Task tool_call_during_a_turn_stops_the_agent_before_the_turn_ends()
    {
        var (channel, output, _, toolUses) = NewChannel();
        var prompt = channel.RequestAsync("session/prompt", new { }, default);
        output.Send("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"tool_call","title":"list files"}}}""");
        // No prompt reply was sent: the request fails as soon as the tool call shows up.
        var ex = await Assert.ThrowsAsync<Exception>(() => prompt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Loc.StatusGeminiUnexpectedTool, ex.Message);
        Assert.Equal(1, toolUses());
        Assert.True(channel.IsClosed);
        var later = await Assert.ThrowsAsync<Exception>(() => channel.RequestAsync("session/new", new { }, default));
        Assert.Equal(Loc.StatusGeminiUnexpectedTool, later.Message);
    }

    [Fact]
    public async Task agent_asking_for_a_file_or_permission_stops_the_agent()
    {
        var (channel, output, _, toolUses) = NewChannel();
        var prompt = channel.RequestAsync("session/prompt", new { }, default);
        output.Send("""{"jsonrpc":"2.0","id":"a1","method":"session/request_permission","params":{"sessionId":"s"}}""");
        var ex = await Assert.ThrowsAsync<Exception>(() => prompt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Loc.StatusGeminiUnexpectedTool, ex.Message);
        Assert.True(channel.AgentAskedForSomething);
        Assert.Equal(1, toolUses());
    }

    [Fact]
    public async Task agent_output_ending_fails_waiting_requests_instead_of_hanging()
    {
        var (channel, output, _, _) = NewChannel();
        var request = channel.RequestAsync("session/new", new { }, default);
        output.End();
        var ex = await Assert.ThrowsAsync<Exception>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Loc.StatusGeminiFailed, ex.Message);
        Assert.True(channel.IsClosed);
    }

    [Fact]
    public async Task error_reply_keeps_its_code_and_only_the_protocol_code_means_missing_sign_in()
    {
        var (channel, output, input, _) = NewChannel();
        var first = channel.RequestAsync("session/new", new { }, default);
        output.Send("""{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"Authentication required"}}""");
        var auth = await Assert.ThrowsAsync<GeminiRequestException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(auth.IsSignInRequired);
        var second = channel.RequestAsync("session/new", new { }, default);
        output.Send("""{"jsonrpc":"2.0","id":2,"error":{"code":-32602,"message":"Invalid login argument"}}""");
        var other = await Assert.ThrowsAsync<GeminiRequestException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(other.IsSignInRequired);
        // Every request went out as one JSON line with its own id.
        var sent = input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal([1, 2], sent);
    }
}
