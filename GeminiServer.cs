using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NaxTranslate;

public sealed class GeminiNotFoundException() : Exception("Antigravity CLI (agy.exe) was not found.");
public sealed class GeminiSignInException() : Exception(Loc.StatusGeminiNotSignedIn);

/// <summary>One running "agy acp" process, spoken to with the Agent Client Protocol (JSON-RPC over stdio).</summary>
internal interface IGeminiConnection : IDisposable
{
    bool HasExited { get; }
    /// <summary>Sends a request and returns its result. Notifications that arrive meanwhile are kept in order.</summary>
    Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct);
    /// <summary>Takes the notifications received so far, oldest first.</summary>
    IReadOnlyList<JsonElement> TakeNotifications();
    /// <summary>True once the agent asked the app for anything (a permission, a file, a terminal). That never happens for a translation.</summary>
    bool AgentAskedForSomething { get; }
    /// <summary>Removes what Antigravity stored for one finished session.</summary>
    void ForgetSession(string sessionId);
}

/// <summary>
/// Translates with Gemini through the installed Antigravity CLI and its Google sign-in. One process
/// is kept ready while Gemini is picked; every text gets a new session with the app's own agent,
/// which has no tools, rules, skills, plugins or MCP servers and replaces the general coding prompt.
/// Antigravity's sessions are saved in the app's own data folder and removed after each translation.
/// </summary>
public sealed class GeminiServer : IChatGptClient
{
    /// <summary>Gemini 3.8 Flash with low thinking: about two seconds per sentence in a ready process.</summary>
    public const string DefaultModel = "gemini-3.8-flash-low";

    internal static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    readonly Func<IGeminiConnection> _factory;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _state = new();
    readonly TimeSpan _timeout;
    CancellationTokenSource _generation = new();
    IGeminiConnection? _connection;

    // A session started ahead of time on the current connection, with its model already set, so a
    // translation does not wait for Antigravity to start the session's worker process.
    string? _readySession;
    string _readyModel = DefaultModel;
    string _lastModel = DefaultModel;

    public GeminiServer() : this(() => new GeminiConnection()) { }
    internal GeminiServer(Func<IGeminiConnection> factory, TimeSpan? timeout = null)
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
        catch (Exception) { } // A menu check or translation reports the failure.
    }

    public void Stop()
    {
        IGeminiConnection? connection;
        lock (_state)
        {
            var previous = _generation;
            _generation = new();
            connection = _connection;
            _connection = null;
            _readySession = null;
            // Cancelled inside the lock, as for ChatGPT, so no request outlives the disconnect unnoticed.
            previous.Cancel();
        }
        connection?.Dispose();
    }

    void DropConnection()
    {
        IGeminiConnection? connection;
        lock (_state) { connection = _connection; _connection = null; _readySession = null; }
        connection?.Dispose();
    }

    /// <summary>Starts a session and sets its model, without sending any text. Null when not signed in.</summary>
    async Task<string?> PrepareSessionAsync(IGeminiConnection connection, string model, CancellationToken ct)
    {
        var sessionId = await NewSessionAsync(connection, GeminiConnection.WorkingDirectoryOf(connection), ct);
        if (sessionId == null) return null;
        await connection.RequestAsync("session/set_model", new { sessionId, modelId = model }, ct);
        connection.TakeNotifications();
        lock (_state) { _readySession = sessionId; _readyModel = model; }
        return sessionId;
    }

    /// <summary>After a translation, prepares the session for the next one while the window shows the result.</summary>
    async Task PrepareNextAsync(CancellationToken generation)
    {
        try
        {
            await _gate.WaitAsync(generation);
            try
            {
                IGeminiConnection? connection;
                string model;
                lock (_state) { connection = _connection; model = _lastModel; if (_readySession != null) return; }
                if (connection is not { HasExited: false }) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generation);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await PrepareSessionAsync(connection, model, timeout.Token);
            }
            finally { _gate.Release(); }
        }
        catch (Exception) { } // The next translation starts its own session instead.
    }

    async Task<IGeminiConnection> ReadyAsync(CancellationToken ct)
    {
        IGeminiConnection? connection;
        lock (_state) connection = _connection;
        if (connection is { HasExited: false }) return connection;
        DropConnection();
        lock (_state)
        {
            ct.ThrowIfCancellationRequested();
            connection = _connection = _factory();
        }
        await connection.RequestAsync("initialize", new
        {
            protocolVersion = 1,
            // The app offers no file or terminal access, so the agent cannot ask for any.
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
        }, ct);
        return connection;
    }

    async Task WaitForGateAsync(CancellationToken timeout, CancellationToken ct, CancellationToken generation, string message)
    {
        try { await _gate.WaitAsync(timeout); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
        { throw new TimeoutException(message); }
    }

    /// <summary>Starts a session, which needs a valid sign-in but makes no model request.</summary>
    static async Task<string?> NewSessionAsync(IGeminiConnection connection, string cwd, CancellationToken ct)
    {
        try
        {
            var result = await connection.RequestAsync("session/new", new { cwd, mcpServers = Array.Empty<object>() }, ct);
            return result.TryGetProperty("sessionId", out var id) ? id.GetString() : null;
        }
        catch (GeminiRequestException ex) when (ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                                             || ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase)
                                             || ex.Message.Contains("sign", StringComparison.OrdinalIgnoreCase))
        { return null; }
    }

    static async Task CloseSessionAsync(IGeminiConnection connection, string sessionId)
    {
        try { await connection.RequestAsync("session/close", new { sessionId }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { } // Closing only frees memory early; the files are removed either way.
        connection.ForgetSession(sessionId);
    }

    public async Task<bool> CheckAuthAsync(CancellationToken ct = default)
    {
        var generation = Generation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, generation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await WaitForGateAsync(timeout.Token, ct, generation, Loc.StatusGeminiAuthTimeout);
        try
        {
            var connection = await ReadyAsync(timeout.Token);
            // A session can only be started while signed in. It becomes the ready session for the
            // first translation, so the check costs nothing extra.
            lock (_state) if (_readySession != null) return true;
            string model;
            lock (_state) model = _lastModel;
            if (await PrepareSessionAsync(connection, model, timeout.Token) == null) { DropConnection(); return false; }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
        { DropConnection(); throw new TimeoutException(Loc.StatusGeminiAuthTimeout); }
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
        await WaitForGateAsync(timeout.Token, ct, generation, Loc.StatusGeminiTimeout);
        model = NormalizeModel(model);
        var translated = false;
        try
        {
            var connection = await ReadyAsync(timeout.Token);
            string? sessionId;
            string readyModel;
            lock (_state) { sessionId = _readySession; readyModel = _readyModel; _readySession = null; _lastModel = model; }
            sessionId ??= await PrepareSessionAsync(connection, model, timeout.Token) ?? throw new GeminiSignInException();
            lock (_state) if (_readySession == sessionId) _readySession = null;
            if (connection is GeminiConnection) GeminiConnection.SweepSessions(GeminiConnection.DataRoot, [sessionId]);
            try
            {
                if (readyModel != model)
                    await connection.RequestAsync("session/set_model", new { sessionId, modelId = model }, timeout.Token);
                connection.TakeNotifications();
                var result = await connection.RequestAsync("session/prompt", new
                {
                    sessionId,
                    prompt = new[] { new { type = "text", text = ChatGptCli.BuildPrompt(text, target, source, tone) } },
                }, timeout.Token);
                var answer = CollectAnswer(sessionId, connection.TakeNotifications(), connection.AgentAskedForSomething);
                if (!result.TryGetProperty("stopReason", out var stop) || stop.GetString() != "end_turn")
                    throw new Exception(Loc.StatusGeminiFailed);
                translated = true;
                return ClaudeCli.ParseReply(answer);
            }
            finally
            {
                // Closing ends the session's worker, so its saved conversation can be deleted at once.
                if (!timeout.IsCancellationRequested) await CloseSessionAsync(connection, sessionId);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
        { DropConnection(); throw new TimeoutException(Loc.StatusGeminiTimeout); }
        catch (UnusableReplyException) { throw; } // the connection is fine; the window asks once more
        catch { DropConnection(); throw; }        // no model retry; the request may already count against usage
        finally
        {
            _gate.Release();
            if (translated) _ = PrepareNextAsync(generation);
        }
    }

    /// <summary>
    /// Joins the answer chunks of one session. Any sign of a tool, or the agent asking the app for
    /// something, ends the translation without showing the answer, as for ChatGPT.
    /// </summary>
    internal static string CollectAnswer(string sessionId, IReadOnlyList<JsonElement> notifications, bool agentAsked)
    {
        if (agentAsked) throw new Exception(Loc.StatusGeminiUnexpectedTool);
        var answer = new StringBuilder();
        foreach (var n in notifications)
        {
            if (!n.TryGetProperty("method", out var method) || method.GetString() != "session/update") continue;
            var p = n.GetProperty("params");
            if (!p.TryGetProperty("sessionId", out var sid) || sid.GetString() != sessionId) continue;
            var update = p.GetProperty("update");
            var kind = update.TryGetProperty("sessionUpdate", out var k) ? k.GetString() : null;
            if (kind is "tool_call" or "tool_call_update") throw new Exception(Loc.StatusGeminiUnexpectedTool);
            if (kind == "agent_message_chunk" && update.TryGetProperty("content", out var content)
                && content.TryGetProperty("type", out var type) && type.GetString() == "text")
                answer.Append(content.GetProperty("text").GetString());
        }
        return answer.ToString();
    }
}

/// <summary>An error answer to a JSON-RPC request.</summary>
internal sealed class GeminiRequestException(string message) : Exception(message);

/// <summary>
/// A private "agy acp" process. It runs in its own working folder that holds only the app's agent
/// definition, keeps Antigravity's data in the app's own subfolder of .gemini, and never opens a socket.
/// </summary>
internal sealed class GeminiConnection : IGeminiConnection
{
    internal const string AgentName = "nax-translate";
    /// <summary>Antigravity's data folder for the app, inside the .gemini folder. It only ever holds the app's sessions.</summary>
    internal const string DataFolderName = "nax-translate-agy";

    internal static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", DataFolderName);

    /// <summary>
    /// The app's agent. excludeDefaultComponents drops Antigravity's built-in tools and coding prompt,
    /// and the empty lists stop rules, skills, plugins, hooks, subagents and MCP servers from being
    /// inherited. The text after the heading is the whole system prompt.
    /// </summary>
    internal static string AgentDefinition =>
        "---\n" +
        $"name: {AgentName}\n" +
        "description: Translation engine for Nax-Translate. Translates the given text and does nothing else.\n" +
        "excludeDefaultComponents: true\n" +
        "tools: []\nskills: []\nrules: []\nagents: []\nplugins: []\nhooks: []\n" +
        "inheritMcp: false\nsubagent: false\nmainAgent: true\n" +
        "---\n" +
        "# Translation engine\n" +
        ChatGptCli.TranslationInstructions + "\n";

    /// <summary>
    /// --process-per-session serves every session from its own worker, which ends when the session
    /// is closed. Only then does Antigravity release the session's saved conversation, so the app can
    /// delete it right after each translation instead of keeping it until Gemini is disconnected.
    /// </summary>
    internal static IReadOnlyList<string> BuildArgs() =>
    [
        "acp", "--agent", AgentName, "--disable-telemetry",
        "--include-default-customizations=false", "--app-data-dir=" + DataFolderName, "--process-per-session",
    ];

    /// <summary>Removes API keys and gateway settings, so translations always use the Google sign-in.</summary>
    internal static void PrepareEnvironment(IDictionary<string, string?> env)
    {
        foreach (var key in env.Keys.ToArray())
            if (key.StartsWith("GEMINI_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("AGY_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)) env.Remove(key);
    }

    internal static string? FindExecutable()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var path = Path.Combine(dir.Trim('"'), "agy.exe"); if (File.Exists(path)) return path; }
            catch (ArgumentException) { }
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>
    /// Deletes the app's Antigravity data folder: the sessions of a run that ended without cleaning up,
    /// for example after a crash. Called at start and after the process is stopped.
    /// </summary>
    internal static void WipeData(string? root = null)
    {
        root ??= DataRoot;
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string WorkingDirectoryOf(IGeminiConnection connection) =>
        connection is GeminiConnection real ? real._directory : Path.GetTempPath();

    readonly Process _process;
    readonly string _directory;
    readonly Queue<JsonElement> _notifications = new();
    int _id, _disposed;
    public bool AgentAskedForSomething { get; private set; }
    public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }

    internal GeminiConnection()
    {
        var exe = FindExecutable() ?? throw new GeminiNotFoundException();
        _directory = TempWork.NewFolder();
        var agents = Path.Combine(_directory, ".agents", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, AgentName + ".md"), AgentDefinition, new UTF8Encoding(false));
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        PrepareEnvironment(start.Environment);
        foreach (var arg in BuildArgs()) start.ArgumentList.Add(arg);
        _process = new Process { StartInfo = start };
        try { _process.Start(); }
        catch (Win32Exception) { _process.Dispose(); TempWork.Remove(_directory); throw new GeminiNotFoundException(); }
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

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        var id = ++_id;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct);
            if (line == null || line.Length > 1_000_000) throw new Exception(Loc.StatusGeminiFailed);
            if (line.Length == 0) continue;
            JsonElement message;
            try { using var doc = JsonDocument.Parse(line); message = doc.RootElement.Clone(); }
            catch (JsonException) { continue; } // a stray log line
            var hasMethod = message.TryGetProperty("method", out _);
            var hasId = message.TryGetProperty("id", out var messageId);
            if (hasMethod && hasId)
            {
                // The agent asks the app for something (a permission, a file, a terminal). The app
                // offers none of that, so it is refused, and the translation will not be shown.
                AgentAskedForSomething = true;
                await SendAsync(new { jsonrpc = "2.0", id = messageId, error = new { code = -32601, message = "Not available" } }, ct);
                continue;
            }
            if (hasMethod)
            {
                if (_notifications.Count >= 4096) throw new Exception(Loc.StatusGeminiFailed);
                _notifications.Enqueue(message);
                continue;
            }
            if (!hasId || messageId.ValueKind != JsonValueKind.Number || messageId.GetInt32() != id) continue;
            if (message.TryGetProperty("error", out var error))
                throw new GeminiRequestException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
            return message.TryGetProperty("result", out var result) ? result : default;
        }
    }

    public IReadOnlyList<JsonElement> TakeNotifications()
    {
        var all = _notifications.ToArray();
        _notifications.Clear();
        return all;
    }

    /// <summary>
    /// Deletes a closed session's saved conversation and side files. The session's worker releases
    /// them a moment after the close, so what is still locked is retried in the background for a few seconds.
    /// </summary>
    public void ForgetSession(string sessionId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sessionId, @"\A[A-Za-z0-9-]+\z")) return;
        if (!TryDeleteSession(DataRoot, sessionId)) _ = RetryDeleteAsync(sessionId);
    }

    static async Task RetryDeleteAsync(string sessionId)
    {
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(200);
            if (TryDeleteSession(DataRoot, sessionId)) return;
        }
    }

    /// <summary>Deletes every file Antigravity keeps for one session. True when none is left.</summary>
    internal static bool TryDeleteSession(string root, string sessionId)
    {
        var done = true;
        foreach (var path in new[]
        {
            Path.Combine(root, "conversations", sessionId + ".db"),
            Path.Combine(root, "conversations", sessionId + ".db-wal"),
            Path.Combine(root, "conversations", sessionId + ".db-shm"),
            Path.Combine(root, "annotations", sessionId + ".pbtxt"),
        })
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { done = false; }
        try { var brain = Path.Combine(root, "brain", sessionId); if (Directory.Exists(brain)) Directory.Delete(brain, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { done = false; }
        return done;
    }

    /// <summary>
    /// Deletes the saved conversations of every session except the given ones, which are still in
    /// use. Catches anything an earlier cleanup could not remove, for example after a cancelled request.
    /// </summary>
    internal static void SweepSessions(string root, IReadOnlyCollection<string> keep)
    {
        try
        {
            var dir = Path.Combine(root, "conversations");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.db"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!keep.Contains(id)) TryDeleteSession(root, id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        try { _process.WaitForExit(1000); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        _process.Dispose();
        TempWork.Remove(_directory);
        // Antigravity also keeps summaries and caches of the sessions; none of it is needed again.
        WipeData();
    }
}
