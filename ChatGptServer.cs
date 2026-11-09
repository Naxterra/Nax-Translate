using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NaxTranslate;

internal interface IChatGptConnection : IDisposable
{
    bool HasExited { get; }
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct);
    Task NotifyAsync(string method, object parameters, CancellationToken ct);
    Task<JsonElement> ReadEventAsync(CancellationToken ct);
}

/// <summary>A reusable authenticated connection; every translation gets a fresh ephemeral thread.</summary>
public sealed class ChatGptServer : IChatGptClient
{
    readonly Func<IReadOnlyList<string>, IChatGptConnection> _factory;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _state = new();
    readonly TimeSpan _timeout;
    readonly HashSet<string> _disabledServers = new(StringComparer.Ordinal);
    readonly Dictionary<CacheKey, (string Text, string? DetectedLang)> _cache = new();
    readonly Queue<CacheKey> _cacheOrder = new();
    int _cachedCharacters;
    readonly record struct CacheKey(string Text, string Target, string Source, string Model, string Tone);
    CancellationTokenSource _generation = new();
    IChatGptConnection? _connection;

    public ChatGptServer() : this(names => new ChatGptConnection(names)) { }
    internal ChatGptServer(Func<IReadOnlyList<string>, IChatGptConnection> factory, TimeSpan? timeout = null)
    { _factory = factory; _timeout = timeout ?? TimeSpan.FromSeconds(90); }

    CancellationToken Generation { get { lock (_state) return _generation.Token; } }

    public void WarmUp()
    {
        var generation = Generation;
        _ = WarmUpAsync(generation);
    }

    async Task WarmUpAsync(CancellationToken generation)
    {
        try { await CheckAuthAsync(generation); }
        catch (Exception) { } // A menu check or translation reports the failure without a model retry.
    }

    public void Stop()
    {
        CancellationTokenSource previous;
        IChatGptConnection? connection;
        lock (_state)
        {
            previous = _generation;
            _generation = new();
            connection = _connection;
            _connection = null;
            _cache.Clear(); _cacheOrder.Clear(); _cachedCharacters = 0;
            // The next connection reads the MCP servers of the current Codex configuration again,
            // so a server removed in the meantime is no longer switched off by name.
            _disabledServers.Clear();
        }
        previous.Cancel();
        connection?.Dispose();
        // The token may still be in use by an interrupted request, so do not dispose its source here.
    }

    void DropConnection()
    {
        IChatGptConnection? connection;
        lock (_state) { connection = _connection; _connection = null; }
        connection?.Dispose();
    }

    IChatGptConnection NewConnection(CancellationToken ct)
    {
        lock (_state)
        {
            ct.ThrowIfCancellationRequested();
            return _connection = _factory(_disabledServers.ToArray());
        }
    }

    internal static string[] EnabledMcpServers(JsonElement config)
    {
        if (!config.TryGetProperty("mcp_servers", out var servers) || servers.ValueKind == JsonValueKind.Null) return [];
        return servers.EnumerateObject().Where(s => !s.Value.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind != JsonValueKind.False).Select(s => s.Name).ToArray();
    }

    internal static void ValidateIsolation(JsonElement config)
    {
        if (EnabledMcpServers(config).Length != 0) throw new Exception(Loc.StatusChatGptIsolationFailed);
        var features = config.GetProperty("features");
        foreach (var name in new[] { "hooks", "plugins", "apps", "shell_tool", "view_image", "multi_agent", "memories", "computer_use", "browser_use", "code_mode", "code_mode_host" })
            if (!features.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.False)
                throw new Exception(Loc.StatusChatGptIsolationFailed);
        if (config.TryGetProperty("notify", out var notify) && notify.ValueKind != JsonValueKind.Null
            && (notify.ValueKind != JsonValueKind.Array || notify.GetArrayLength() != 0))
            throw new Exception(Loc.StatusChatGptIsolationFailed);
        if (config.GetProperty("forced_login_method").GetString() != "chatgpt"
            || config.GetProperty("model_provider").GetString() != "openai"
            || config.GetProperty("web_search").GetString() != "disabled"
            || config.GetProperty("project_doc_max_bytes").GetInt32() != 0)
            throw new Exception(Loc.StatusChatGptIsolationFailed);
        foreach (var name in new[] { "openai_base_url", "chatgpt_base_url" })
            if (config.TryGetProperty(name, out var endpoint) && endpoint.ValueKind != JsonValueKind.Null)
            {
                var host = name == "chatgpt_base_url" ? "chatgpt.com" : "api.openai.com";
                var path = name == "chatgpt_base_url" ? "/backend-api" : "/v1";
                if (endpoint.ValueKind != JsonValueKind.String || !Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var url)
                    || url.Scheme != Uri.UriSchemeHttps || url.Host != host || !url.IsDefaultPort
                    || url.AbsolutePath.TrimEnd('/') != path || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                    throw new Exception(Loc.StatusChatGptIsolationFailed);
            }
        var telemetry = config.GetProperty("otel");
        foreach (var name in new[] { "exporter", "trace_exporter", "metrics_exporter" })
            if (telemetry.GetProperty(name).GetString() != "none") throw new Exception(Loc.StatusChatGptIsolationFailed);
        if (telemetry.GetProperty("log_user_prompt").GetBoolean()
            || config.GetProperty("history").GetProperty("persistence").GetString() != "none")
            throw new Exception(Loc.StatusChatGptIsolationFailed);
    }

    async Task<IChatGptConnection> ReadyAsync(CancellationToken ct)
    {
        IChatGptConnection? connection;
        lock (_state) connection = _connection;
        if (connection is { HasExited: false })
        {
            // Check again before accepting text, including after user configuration changes.
            var cfg = (await connection.RequestAsync("config/read", new { includeLayers = false }, ct)).GetProperty("config");
            var enabled = EnabledMcpServers(cfg);
            if (enabled.Length == 0) { ValidateIsolation(cfg); return connection; }
            lock (_state) foreach (var name in enabled) _disabledServers.Add(name);
            DropConnection();
        }
        else DropConnection();

        // Empty tables merge with user configuration. Discover MCP names without starting a
        // thread, then restart with an explicit disabled flag for each inherited server.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            connection = NewConnection(ct);
            await connection.RequestAsync("initialize", new
            {
                clientInfo = new { name = "nax_translate", title = "Nax-Translate", version = typeof(ChatGptServer).Assembly.GetName().Version!.ToString(3) },
                capabilities = new { experimentalApi = true },
            }, ct);
            await connection.NotifyAsync("initialized", new { }, ct);
            var cfg = (await connection.RequestAsync("config/read", new { includeLayers = false }, ct)).GetProperty("config");
            var enabled = EnabledMcpServers(cfg);
            if (enabled.Length == 0) { ValidateIsolation(cfg); return connection; }
            lock (_state) foreach (var name in enabled) _disabledServers.Add(name);
            DropConnection();
        }
        throw new Exception(Loc.StatusChatGptIsolationFailed);
    }

    static async Task<bool> IsChatGptAsync(IChatGptConnection connection, CancellationToken ct)
    {
        var result = await connection.RequestAsync("account/read", new { refreshToken = false }, ct);
        return result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object
            && account.TryGetProperty("type", out var type) && type.GetString() == "chatgpt";
    }

    /// <summary>
    /// Waits for the one connection to be free. Running out of time while another request still
    /// holds it is reported as the same timeout as a slow answer, not as a bare cancellation. The
    /// connection is not dropped here, because the other request is still using it.
    /// </summary>
    async Task WaitForGateAsync(CancellationToken timeout, CancellationToken ct, CancellationToken generation, string message)
    {
        try { await _gate.WaitAsync(timeout); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
        { throw new TimeoutException(message); }
    }

    public async Task<bool> CheckAuthAsync(CancellationToken ct = default)
    {
        var generation = Generation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, generation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await WaitForGateAsync(timeout.Token, ct, generation, Loc.StatusChatGptAuthTimeout);
        try
        {
            var connection = await ReadyAsync(timeout.Token);
            var signedIn = await IsChatGptAsync(connection, timeout.Token);
            if (!signedIn) DropConnection();
            return signedIn;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
        { DropConnection(); throw new TimeoutException(Loc.StatusChatGptAuthTimeout); }
        catch { DropConnection(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<(string Text, string? DetectedLang)> TranslateAsync(string text, string target,
        string source, string? model, string tone, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (text.Length > DeepLApi.MaxChars) throw new ArgumentException($"Text exceeds {DeepLApi.MaxChars} characters.", nameof(text));
        var generation = Generation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, generation);
        timeout.CancelAfter(_timeout);
        await WaitForGateAsync(timeout.Token, ct, generation, Loc.StatusChatGptTimeout);
        try
        {
            var connection = await ReadyAsync(timeout.Token);
            if (!await IsChatGptAsync(connection, timeout.Token)) throw new ChatGptSignInException();
            var key = new CacheKey(text, target, source, ChatGptCli.NormalizeModel(model), ClaudeCli.NormalizeTone(tone));
            lock (_state)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_cache.TryGetValue(key, out var cached)) return cached;
            }
            var started = await connection.RequestAsync("thread/start", new
            {
                model = ChatGptCli.NormalizeModel(model), modelProvider = "openai", ephemeral = true,
                approvalPolicy = "never", sandbox = "read-only", dynamicTools = Array.Empty<object>(), environments = Array.Empty<object>(),
                baseInstructions = ChatGptCli.TranslationInstructions, developerInstructions = "",
                config = new Dictionary<string, object> { ["model_reasoning_effort"] = "low" },
            }, timeout.Token);
            var thread = started.GetProperty("thread");
            if (!thread.GetProperty("ephemeral").GetBoolean()) throw new Exception(Loc.StatusChatGptIsolationFailed);
            var threadId = thread.GetProperty("id").GetString()!;
            await connection.RequestAsync("turn/start", new
            {
                threadId, effort = "low", serviceTierForTurn = "default",
                input = new[] { new { type = "text", text = ClaudeCli.BuildPrompt(text, target, source, tone) } },
            }, timeout.Token);
            string? answer = null;
            while (true)
            {
                var message = await connection.ReadEventAsync(timeout.Token);
                var method = message.GetProperty("method").GetString();
                var p = message.GetProperty("params");
                if (!p.TryGetProperty("threadId", out var tid) || tid.GetString() != threadId) continue;
                if (method is "item/started" or "item/completed")
                {
                    var item = p.GetProperty("item");
                    var kind = item.GetProperty("type").GetString();
                    if (kind is not ("userMessage" or "agentMessage" or "reasoning")) throw new Exception(Loc.StatusChatGptUnexpectedTool);
                    if (method == "item/completed" && kind == "agentMessage") answer = item.GetProperty("text").GetString();
                }
                if (method == "turn/completed")
                {
                    if (p.GetProperty("turn").GetProperty("status").GetString() != "completed" || string.IsNullOrWhiteSpace(answer))
                        throw new Exception(Loc.StatusChatGptFailed);
                    var result = ClaudeCli.ParseReply(answer);
                    // Unload the old context before reusing the connection for another text.
                    await connection.RequestAsync("thread/unsubscribe", new { threadId }, timeout.Token);
                    lock (_state)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        // Stop() swaps the generation and empties the cache under this lock before it
                        // cancels the old token. A result that finished in between belongs to the old
                        // connection and must not land in the emptied cache.
                        if (generation == _generation.Token && text.Length + result.Text.Length <= 100_000)
                        {
                            while (_cacheOrder.Count > 0 && (_cacheOrder.Count >= 32
                                   || _cachedCharacters + text.Length + result.Text.Length > 100_000))
                            {
                                var old = _cacheOrder.Dequeue();
                                if (_cache.Remove(old, out var oldResult)) _cachedCharacters -= old.Text.Length + oldResult.Text.Length;
                            }
                            _cache[key] = result;
                            _cacheOrder.Enqueue(key);
                            _cachedCharacters += text.Length + result.Text.Length;
                        }
                    }
                    return result;
                }
                if (method == "error") throw new Exception(Loc.StatusChatGptFailed);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
        { DropConnection(); throw new TimeoutException(Loc.StatusChatGptTimeout); }
        catch { DropConnection(); throw; } // No model retry; the request may already count against usage.
        finally { _gate.Release(); }
    }
}

/// <summary>Private stdio process. It never opens a socket listener or persists translation messages.</summary>
internal sealed class ChatGptConnection : IChatGptConnection
{
    readonly Process _process;
    readonly string _directory;
    readonly Queue<JsonElement> _events = new();
    int _id, _disposed;
    public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }

    internal static IReadOnlyList<string> BuildArgs(IReadOnlyList<string> disabledServers)
    {
        var args = new List<string> { "app-server", "--stdio" };
        var cli = ChatGptCli.BuildArgs(null);
        for (var i = 0; i < cli.Count; i++)
            if (cli[i] == "-c") { args.Add("-c"); args.Add(cli[++i]); }
        foreach (var value in new[] { "notify=[]", "hooks={}", "mcp_servers={}", "otel.exporter=\"none\"",
            "otel.trace_exporter=\"none\"", "otel.metrics_exporter=\"none\"", "otel.log_user_prompt=false" })
        { args.Add("-c"); args.Add(value); }
        foreach (var name in disabledServers)
        {
            if (!Regex.IsMatch(name, "\\A[A-Za-z0-9_-]+\\z")) throw new Exception(Loc.StatusChatGptIsolationFailed);
            args.Add("-c"); args.Add($"mcp_servers.{name}.enabled=false");
        }
        return args;
    }

    internal ChatGptConnection(IReadOnlyList<string> disabledServers)
    {
        var args = BuildArgs(disabledServers);
        var exe = ChatGptCli.FindExecutable() ?? throw new ChatGptNotFoundException();
        _directory = TempWork.NewFolder();
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        ChatGptCli.PrepareEnvironment(start.Environment);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        _process = new Process { StartInfo = start };
        try { _process.Start(); }
        catch (Win32Exception) { _process.Dispose(); TempWork.Remove(_directory); throw new ChatGptNotFoundException(); }
        _ = DrainErrorsAsync();
    }

    async Task DrainErrorsAsync()
    {
        try { while (await _process.StandardError.ReadLineAsync() != null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    async Task SendAsync(object message, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        var line = await _process.StandardOutput.ReadLineAsync(ct);
        if (line == null || line.Length > 1_000_000) throw new Exception(Loc.StatusChatGptFailed);
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        var id = ++_id;
        await SendAsync(new { id, method, @params = parameters }, ct);
        while (true)
        {
            var message = await ReadAsync(ct);
            if (message.TryGetProperty("id", out var responseId))
            {
                if (message.TryGetProperty("method", out _) || responseId.ValueKind != JsonValueKind.Number || responseId.GetInt32() != id)
                    throw new Exception(Loc.StatusChatGptUnexpectedTool);
                if (message.TryGetProperty("error", out _)) throw new Exception(Loc.StatusChatGptFailed);
                return message.GetProperty("result");
            }
            if (_events.Count >= 1024) throw new Exception(Loc.StatusChatGptFailed);
            _events.Enqueue(message);
        }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken ct) => SendAsync(new { method, @params = parameters }, ct);
    public async Task<JsonElement> ReadEventAsync(CancellationToken ct)
    {
        var message = _events.Count > 0 ? _events.Dequeue() : await ReadAsync(ct);
        if (message.TryGetProperty("id", out _)) throw new Exception(Loc.StatusChatGptUnexpectedTool);
        return message;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        // Usually the killed process is gone within milliseconds, so its folder is removed right
        // here, which also covers the app exiting before a background cleanup could run. A process
        // that still holds the folder is waited for in the background.
        try { _process.WaitForExit(500); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        if (TempWork.Remove(_directory)) { _process.Dispose(); return; }
        _ = CleanupAsync();
    }

    async Task CleanupAsync()
    {
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        finally
        {
            _process.Dispose();
            TempWork.Remove(_directory);
        }
    }
}

/// <summary>
/// The private working folders of the ChatGPT engine, %TEMP%\Nax-Translate\&lt;id&gt;. Nothing of the
/// owner's is ever in them, so leftovers can be removed without asking.
/// </summary>
internal static class TempWork
{
    internal static string Root => Path.Combine(Path.GetTempPath(), "Nax-Translate");

    internal static string NewFolder()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Deletes the folder and, once it is the last one, the Nax-Translate folder too. True when it is gone.</summary>
    internal static bool Remove(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return !Directory.Exists(dir); }
    }

    /// <summary>
    /// Removes the folders a crashed or killed earlier run left behind. Called once at start, before
    /// the first ChatGPT connection, so no folder in use is touched.
    /// </summary>
    internal static void RemoveLeftovers(string? root = null)
    {
        root ??= Root;
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root)) Remove(dir);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
