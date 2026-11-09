using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaxTranslate;

public class Config
{
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string DefaultDir = Path.Combine(AppData, "Nax-Translate");
    static readonly string DefaultPath = Path.Combine(DefaultDir, "config.json");

    // Older versions kept their settings in these folders, newest first: "Nax-DeepL" up to 1.1.x
    // and "DeepL-Translator" before that. The newest one that exists is moved once to the new name.
    static readonly string[] LegacyDirs =
        [Path.Combine(AppData, "Nax-DeepL"), Path.Combine(AppData, "DeepL-Translator")];

    [JsonIgnore] string FilePath { get; set; } = DefaultPath;

    // Login tokens are only kept in memory in plain text. On disk they are encrypted with
    // Windows DPAPI for the current user, see the *Protected properties below.
    [JsonIgnore] public string? DlAccess     { get; set; }
    [JsonIgnore] public string? RefreshToken { get; set; }

    [JsonPropertyName("dl_access_protected")]     public string? DlAccessProtected     { get; set; }
    [JsonPropertyName("refresh_token_protected")] public string? RefreshTokenProtected { get; set; }

    // Versions up to 1.0.1 stored the tokens in plain text under these names. They are read
    // once for migration and never written again.
    [JsonPropertyName("dl_access")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDlAccess { get; set; }
    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRefreshToken { get; set; }
    [JsonPropertyName("identity_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyIdentityToken { get; set; }

    [JsonPropertyName("src_lang")]  public string  SrcLang  { get; set; } = "auto";
    [JsonPropertyName("tgt_lang")]  public string  TgtLang  { get; set; } = "de";
    // "classic" or "nextgen" for DeepL, "claude", or "chatgpt".
    [JsonPropertyName("engine")]    public string  Engine   { get; set; } = "classic";
    // The model the Claude engine asks for, as an alias such as "sonnet" or "haiku".
    [JsonPropertyName("claude_model")] public string ClaudeModel { get; set; } = ClaudeCli.DefaultModel;
    // Set once the Claude model has been moved to the faster default, see Load.
    [JsonPropertyName("claude_model_reviewed")] public bool ClaudeModelReviewed { get; set; }
    // The tone of Claude translations: "natural", "formal" or "informal".
    [JsonPropertyName("claude_tone")]  public string ClaudeTone  { get; set; } = ClaudeCli.ToneNatural;
    // Whether Claude is connected. While it is not, nothing is sent to Claude and no Claude Code
    // process is kept waiting.
    [JsonPropertyName("claude_connected")] public bool ClaudeConnected { get; set; } = true;
    [JsonPropertyName("chatgpt_connected")] public bool ChatGptConnected { get; set; } = true;
    // Pin a translation model rather than inheriting the coding CLI's changing default.
    [JsonPropertyName("chatgpt_model")] public string ChatGptModel { get; set; } = ChatGptCli.DefaultModel;
    [JsonPropertyName("chatgpt_tone")] public string ChatGptTone { get; set; } = ClaudeCli.ToneNatural;
    // Whether the app starts with its window hidden, showing only the tray icon.
    [JsonPropertyName("start_in_tray")]    public bool StartInTray     { get; set; } = true;
    // Whether minimizing the window hides it to the tray instead of the taskbar.
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray  { get; set; } = true;
    [JsonPropertyName("ui_lang")]   public string  UiLang   { get; set; } = "de";
    [JsonPropertyName("geometry")]  public string? Geometry { get; set; }

    public static Config Load(string? path = null)
    {
        if (path == null) MoveLegacyFolders(LegacyDirs, DefaultDir);
        path ??= DefaultPath;
        Config cfg;
        try
        {
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { cfg = new(); }
        cfg.FilePath = path;
        cfg.ChatGptModel = ChatGptCli.NormalizeModel(cfg.ChatGptModel);
        // Up to 1.4.0 every settings file stored Sonnet, the old default, because there was no way
        // to pick a model. Sonnet now takes about three times as long as Haiku for a sentence, so
        // that stored default moves to Haiku once. A model picked in the menu afterwards is kept.
        if (!cfg.ClaudeModelReviewed)
        {
            if (string.Equals(cfg.ClaudeModel, "sonnet", StringComparison.OrdinalIgnoreCase))
                cfg.ClaudeModel = ClaudeCli.DefaultModel;
            cfg.ClaudeModelReviewed = true;
        }

        cfg.DlAccess     = Unprotect(cfg.DlAccessProtected);
        cfg.RefreshToken = Unprotect(cfg.RefreshTokenProtected);

        if (cfg.LegacyDlAccess != null || cfg.LegacyRefreshToken != null || cfg.LegacyIdentityToken != null)
        {
            cfg.DlAccess     ??= cfg.LegacyDlAccess;
            cfg.RefreshToken ??= cfg.LegacyRefreshToken;
            cfg.LegacyDlAccess = cfg.LegacyRefreshToken = cfg.LegacyIdentityToken = null;
            cfg.Save();   // rewrites the file without the plain-text tokens
        }
        return cfg;
    }

    /// <summary>
    /// Moves the first of the given old folders that exists, newest name first. A folder is never
    /// moved onto one that exists, so after the first move the older folders are left alone.
    /// </summary>
    internal static void MoveLegacyFolders(IEnumerable<string> legacyDirs, string newDir)
    {
        foreach (var legacy in legacyDirs) MoveLegacyFolder(legacy, newDir);
    }

    /// <summary>
    /// Moves the settings folder of an older version to the current name. Settings that already
    /// exist under the current name are never replaced. If the move fails, the old folder stays
    /// untouched and the app starts with default settings.
    /// </summary>
    internal static void MoveLegacyFolder(string legacyDir, string newDir)
    {
        try
        {
            if (Directory.Exists(legacyDir) && !Directory.Exists(newDir))
                Directory.Move(legacyDir, newDir);
        }
        catch { }
    }

    /// <summary>
    /// Writes the settings to a temporary file and then swaps it in, so the existing file is
    /// either kept whole or replaced whole, never left half-written.
    /// </summary>
    public void Save()
    {
        try
        {
            DlAccessProtected     = Protect(DlAccess);
            RefreshTokenProtected = Protect(RefreshToken);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp  = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    // This value keeps its old app name on purpose. Changing it would make every saved login
    // unreadable.
    static readonly byte[] Entropy = "DeepL-Translator token v1"u8.ToArray();

    static string? Protect(string? plain) =>
        plain == null ? null : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    static string? Unprotect(string? data)
    {
        if (data == null) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return null; }   // another user's or a damaged value: treat as logged out
    }
}
