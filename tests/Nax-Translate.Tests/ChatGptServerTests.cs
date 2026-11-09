using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NaxTranslate.Tests;

public sealed class ChatGptServerTests
{
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    sealed class Connection : IChatGptConnection
    {
        public readonly List<(string Method, JsonElement Parameters)> Calls = [];
        public readonly Queue<JsonElement> Events = new();
        public readonly Dictionary<string, object> Features = new()
        {
            ["hooks"] = false, ["plugins"] = false, ["apps"] = false, ["shell_tool"] = false,
            ["view_image"] = false, ["multi_agent"] = false, ["memories"] = false,
            ["computer_use"] = false, ["browser_use"] = false, ["code_mode"] = false, ["code_mode_host"] = false,
        };
        public readonly Dictionary<string, object> Servers = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited { get; private set; }
        public bool SignedIn = true, Ephemeral = true, Hang, Failed, IncludeWrongThread;
        public TaskCompletionSource? HoldThreadStart;
        public string? ToolKind;
        public string Reply = "lang: DE\nhonor";
        string _thread = "";
        public JsonElement Config => Json(new
        {
            features = Features, mcp_servers = Servers, notify = Array.Empty<string>(),
            forced_login_method = "chatgpt", model_provider = "openai", web_search = "disabled", project_doc_max_bytes = 0,
            otel = new { exporter = "none", trace_exporter = "none", metrics_exporter = "none", log_user_prompt = false },
            history = new { persistence = "none" },
        });

        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var p = Json(parameters);
            Calls.Add((method, p));
            switch (method)
            {
                case "initialize": return Task.FromResult(Json(new { }));
                case "config/read": return Task.FromResult(Json(new { config = Config }));
                case "account/read": return Task.FromResult(Json(new { account = SignedIn ? new { type = "chatgpt" } : null }));
                case "thread/start":
                    _thread = Guid.NewGuid().ToString("N");
                    var thread = Json(new { thread = new { id = _thread, ephemeral = Ephemeral } });
                    // Holds the connection, ignoring cancellation, like a request stuck on the server.
                    return HoldThreadStart != null ? HoldThreadStart.Task.ContinueWith(_ => thread) : Task.FromResult(thread);
                case "turn/start":
                    if (IncludeWrongThread)
                        Events.Enqueue(Json(new { method = "item/completed", @params = new { threadId = "old-thread", item = new { type = "agentMessage", text = "old answer" } } }));
                    if (!Hang)
                    {
                        if (ToolKind != null)
                            Events.Enqueue(Json(new { method = "item/started", @params = new { threadId = _thread, item = new { type = ToolKind } } }));
                        Events.Enqueue(Json(new { method = "item/completed", @params = new { threadId = _thread, item = new { type = "agentMessage", text = Reply } } }));
                        Events.Enqueue(Json(new { method = "turn/completed", @params = new { threadId = _thread, turn = new { status = Failed ? "failed" : "completed" } } }));
                    }
                    Started.TrySetResult();
                    return Task.FromResult(Json(new { turn = new { id = "turn" } }));
                default: return Task.FromResult(Json(new { }));
            }
        }

        public Task NotifyAsync(string method, object parameters, CancellationToken ct)
        { Calls.Add((method, Json(parameters))); return Task.CompletedTask; }

        public async Task<JsonElement> ReadEventAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Events.Count == 0) await Task.Delay(Timeout.Infinite, ct);
            return Events.Dequeue();
        }

        public void Dispose() => HasExited = true;
    }

    static Task<(string Text, string? DetectedLang)> Translate(ChatGptServer client, string text = "ehre",
        string? model = null, string tone = "natural", CancellationToken ct = default) =>
        client.TranslateAsync(text, "en-US", "de", model, tone, ct);

    [Fact]
    public async Task warmup_checks_authentication_without_starting_a_model_turn()
    {
        var connection = new Connection();
        var client = new ChatGptServer(_ => connection);
        try
        {
            client.WarmUp();
            Assert.True(await client.CheckAuthAsync());
            Assert.DoesNotContain(connection.Calls, c => c.Method is "thread/start" or "turn/start");
            Assert.Single(connection.Calls, c => c.Method == "initialize");
        }
        finally { client.Stop(); }
        Assert.True(connection.HasExited);
    }

    [Fact]
    public async Task connection_is_reused_but_every_text_has_a_fresh_unsaved_context()
    {
        var connection = new Connection();
        var made = 0;
        var client = new ChatGptServer(_ => { made++; return connection; });
        try
        {
            await Translate(client, "private first text");
            await Translate(client, "second text");
            Assert.Equal(1, made);
            var threads = connection.Calls.Where(c => c.Method == "thread/start").ToArray();
            Assert.Equal(2, threads.Length);
            Assert.All(threads, t =>
            {
                Assert.True(t.Parameters.GetProperty("ephemeral").GetBoolean());
                Assert.Equal("gpt-6-luna", t.Parameters.GetProperty("model").GetString());
                Assert.Equal(ChatGptCli.TranslationInstructions, t.Parameters.GetProperty("baseInstructions").GetString());
                Assert.Equal(0, t.Parameters.GetProperty("dynamicTools").GetArrayLength());
                Assert.Equal("read-only", t.Parameters.GetProperty("sandbox").GetString());
            });
            var turns = connection.Calls.Where(c => c.Method == "turn/start").ToArray();
            Assert.NotEqual(turns[0].Parameters.GetProperty("threadId").GetString(), turns[1].Parameters.GetProperty("threadId").GetString());
            Assert.Contains("private first text", turns[0].Parameters.ToString());
            Assert.DoesNotContain("private first text", turns[1].Parameters.ToString());
            Assert.All(turns, t => Assert.Equal("default", t.Parameters.GetProperty("serviceTierForTurn").GetString()));
            Assert.Equal(2, connection.Calls.Count(c => c.Method == "thread/unsubscribe"));
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task repeated_translation_is_cached_but_model_and_tone_remain_distinct()
    {
        var connection = new Connection();
        var client = new ChatGptServer(_ => connection);
        try
        {
            Assert.Equal("honor", (await Translate(client)).Text);
            Assert.Equal("honor", (await Translate(client)).Text);
            Assert.Single(connection.Calls, c => c.Method == "turn/start");
            await Translate(client, tone: "formal");
            await Translate(client, model: "gpt-6.1-terra");
            Assert.Equal(3, connection.Calls.Count(c => c.Method == "turn/start"));
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task disconnect_clears_cached_text_and_closes_the_connection()
    {
        var connections = new List<Connection>();
        var client = new ChatGptServer(_ => { var c = new Connection(); connections.Add(c); return c; });
        await Translate(client);
        client.Stop();
        Assert.True(connections[0].HasExited);
        await Translate(client);
        client.Stop();
        Assert.Equal(2, connections.Count);
        Assert.All(connections, c => Assert.Single(c.Calls, call => call.Method == "turn/start"));
    }

    [Fact]
    public async Task inherited_mcp_servers_are_explicitly_disabled_before_text_is_sent()
    {
        var connections = new List<Connection>();
        var client = new ChatGptServer(disabled =>
        {
            var c = new Connection();
            c.Servers["user_tools"] = new { enabled = !disabled.Contains("user_tools") };
            connections.Add(c);
            return c;
        });
        try
        {
            await Translate(client);
            Assert.Equal(2, connections.Count);
            Assert.True(connections[0].HasExited);
            Assert.DoesNotContain(connections[0].Calls, c => c.Method == "turn/start");
            Assert.Single(connections[1].Calls, c => c.Method == "turn/start");
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task isolation_failure_never_starts_a_translation()
    {
        var connections = new List<Connection>();
        var client = new ChatGptServer(_ =>
        {
            var c = new Connection(); c.Servers["always_enabled"] = new { enabled = true };
            connections.Add(c); return c;
        });
        await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(3, connections.Count);
        Assert.All(connections, c => { Assert.True(c.HasExited); Assert.DoesNotContain(c.Calls, call => call.Method == "turn/start"); });
        client.Stop();
    }

    [Theory]
    [InlineData("hooks")]
    [InlineData("shell_tool")]
    [InlineData("apps")]
    [InlineData("plugins")]
    public async Task tools_or_hooks_cannot_be_enabled_in_an_inherited_configuration(string feature)
    {
        var connection = new Connection(); connection.Features[feature] = true;
        var client = new ChatGptServer(_ => connection);
        await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.True(connection.HasExited);
        Assert.DoesNotContain(connection.Calls, c => c.Method == "turn/start");
        client.Stop();
    }

    [Fact]
    public async Task api_key_authentication_cannot_use_even_a_cached_answer()
    {
        var connection = new Connection();
        var client = new ChatGptServer(_ => connection);
        await Translate(client);
        connection.SignedIn = false;
        await Assert.ThrowsAsync<ChatGptSignInException>(() => Translate(client));
        Assert.Single(connection.Calls, c => c.Method == "turn/start");
        Assert.True(connection.HasExited);
        client.Stop();
    }

    [Theory]
    [InlineData("commandExecution")]
    [InlineData("mcpToolCall")]
    [InlineData("fileChange")]
    public async Task unexpected_tool_activity_aborts_without_returning_the_answer(string kind)
    {
        var connection = new Connection { ToolKind = kind };
        var client = new ChatGptServer(_ => connection);
        await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.True(connection.HasExited);
        client.Stop();
    }

    [Fact]
    public async Task failed_turn_does_not_show_or_cache_a_partial_answer_or_retry()
    {
        var connection = new Connection { Failed = true, Reply = "lang: DE\npartial" };
        var made = 0;
        var client = new ChatGptServer(_ => { made++; return connection; });
        await Assert.ThrowsAsync<Exception>(() => Translate(client));
        Assert.Equal(1, made);
        Assert.Single(connection.Calls, c => c.Method == "turn/start");
        Assert.True(connection.HasExited);
        client.Stop();
    }

    [Fact]
    public async Task outdated_events_from_another_thread_cannot_replace_the_result()
    {
        var connection = new Connection { IncludeWrongThread = true };
        var client = new ChatGptServer(_ => connection);
        try { Assert.Equal("honor", (await Translate(client)).Text); }
        finally { client.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task cancellation_or_disconnect_stops_a_request_and_never_retries(bool disconnect)
    {
        var connection = new Connection { Hang = true };
        var client = new ChatGptServer(_ => connection);
        using var cancel = new CancellationTokenSource();
        var run = Translate(client, ct: cancel.Token);
        await connection.Started.Task;
        if (disconnect) client.Stop(); else cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(connection.HasExited);
        Assert.Single(connection.Calls, c => c.Method == "turn/start");
        client.Stop();
    }

    [Fact]
    public async Task timeout_stops_the_process_without_a_model_retry()
    {
        var connection = new Connection { Hang = true };
        var client = new ChatGptServer(_ => connection, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => Translate(client));
        Assert.True(connection.HasExited);
        Assert.Single(connection.Calls, c => c.Method == "turn/start");
        client.Stop();
    }

    [Fact]
    public async Task cancelled_or_oversized_text_never_starts_a_backend()
    {
        var client = new ChatGptServer(_ => throw new Exception("must not start"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Translate(client, ct: new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => Translate(client, new string('a', DeepLApi.MaxChars + 1)));
        client.Stop();
    }

    [Fact]
    public async Task cache_is_bounded_and_oldest_entry_is_evicted()
    {
        var connection = new Connection();
        var client = new ChatGptServer(_ => connection);
        try
        {
            for (var i = 0; i < 33; i++) await Translate(client, "word-" + i);
            await Translate(client, "word-32");
            Assert.Equal(33, connection.Calls.Count(c => c.Method == "turn/start"));
            await Translate(client, "word-0");
            Assert.Equal(34, connection.Calls.Count(c => c.Method == "turn/start"));
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task exited_backend_is_replaced_on_the_next_user_request()
    {
        var connections = new List<Connection>();
        var client = new ChatGptServer(_ => { var c = new Connection(); connections.Add(c); return c; });
        try
        {
            await client.CheckAuthAsync();
            connections[0].Dispose();
            Assert.Equal("honor", (await Translate(client)).Text);
            Assert.Equal(2, connections.Count);
            Assert.DoesNotContain(connections[0].Calls, c => c.Method == "turn/start");
        }
        finally { client.Stop(); }
    }

    [Fact]
    public void inherited_telemetry_cannot_export_translation_prompts()
    {
        var config = JsonNode.Parse(new Connection().Config.GetRawText())!.AsObject();
        config["otel"]!["log_user_prompt"] = true;
        Assert.Throws<Exception>(() => ChatGptServer.ValidateIsolation(Json(config)));
        config["otel"]!["log_user_prompt"] = false;
        config["otel"]!["exporter"] = "otlp-http";
        Assert.Throws<Exception>(() => ChatGptServer.ValidateIsolation(Json(config)));
    }

    [Theory]
    [InlineData("https://chatgpt.com/backend-api/", true)]
    [InlineData("https://chatgpt.com/backend-api", true)]
    [InlineData("http://chatgpt.com/backend-api/", false)]
    [InlineData("https://example.invalid/backend-api/", false)]
    [InlineData("https://chatgpt.com/backend-api/?key=test", false)]
    [InlineData("https://user@chatgpt.com/backend-api/", false)]
    public void only_the_standard_chatgpt_endpoint_is_accepted(string endpoint, bool accepted)
    {
        var config = JsonNode.Parse(new Connection().Config.GetRawText())!.AsObject();
        config["chatgpt_base_url"] = endpoint;
        if (accepted) ChatGptServer.ValidateIsolation(Json(config));
        else Assert.Throws<Exception>(() => ChatGptServer.ValidateIsolation(Json(config)));
    }

    [Fact]
    public void server_arguments_disable_inherited_servers_and_keep_stdio_private()
    {
        var args = ChatGptConnection.BuildArgs(["user_tools"]);
        Assert.Equal("app-server", args[0]);
        Assert.Contains("--stdio", args);
        Assert.DoesNotContain("--listen", args);
        Assert.Contains("mcp_servers.user_tools.enabled=false", args);
        Assert.Contains("notify=[]", args);
        Assert.Contains("otel.log_user_prompt=false", args);
        Assert.Contains("otel.exporter=\"none\"", args);
        Assert.Contains("features.hooks=false", args);
        Assert.Contains("forced_login_method=\"chatgpt\"", args);
        Assert.Throws<Exception>(() => ChatGptConnection.BuildArgs(["invalid.name"]));
    }

    [Fact]
    public async Task waiting_too_long_for_busy_connection_reports_timeout_not_bare_cancellation()
    {
        var connection = new Connection { HoldThreadStart = new() };
        var client = new ChatGptServer(_ => connection, TimeSpan.FromMilliseconds(300));
        try
        {
            // The first request holds the one connection and does not give it back in time.
            var first = Translate(client, "first text");
            await Task.Delay(50);
            await Assert.ThrowsAsync<TimeoutException>(() => Translate(client, "second text"));
            connection.HoldThreadStart.SetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => first);
        }
        finally { client.Stop(); }
    }

    [Fact]
    public async Task mcp_server_removed_from_codex_config_is_not_switched_off_after_reconnect()
    {
        var names = new List<string[]>();
        var withServer = true;
        var client = new ChatGptServer(n =>
        {
            names.Add(n.ToArray());
            var c = new Connection();
            // The first start finds a server in the Codex configuration; the restart switches it off.
            if (withServer && n.Count == 0) c.Servers["notes"] = new { command = "notes.exe" };
            return c;
        });
        try
        {
            await Translate(client);
            Assert.Equal(["notes"], names[^1]);

            // The owner removes the server from Codex and reconnects ChatGPT.
            withServer = false;
            client.Stop();
            await Translate(client, "another text");
            Assert.Empty(names[^1]);
        }
        finally { client.Stop(); }
    }

    [Fact]
    public void leftover_working_folders_are_removed_at_start_and_last_one_takes_parent_along()
    {
        var root = Path.Combine(Path.GetTempPath(), "NaxTranslate.Tests", Guid.NewGuid().ToString("N"), "Nax-Translate");
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "left.txt"), "x");

        Assert.True(TempWork.Remove(b));
        Assert.True(Directory.Exists(root), "the parent stays while another folder is in it");
        TempWork.RemoveLeftovers(root);
        Assert.False(Directory.Exists(root));
        Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
    }
}
