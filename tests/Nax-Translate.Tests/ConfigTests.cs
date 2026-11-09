using System.IO;

namespace NaxTranslate.Tests;

public sealed class ConfigTests : IDisposable
{
    readonly string _dir  = Path.Combine(Path.GetTempPath(), "NaxTranslate.Tests", Guid.NewGuid().ToString("N"));
    string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void new_settings_use_luna_for_chatgpt_without_changing_other_engines()
    {
        var cfg = Config.Load(ConfigPath);
        Assert.Equal("gpt-6-luna", cfg.ChatGptModel);
        Assert.Equal("classic", cfg.Engine);
        Assert.Equal(ClaudeCli.DefaultModel, cfg.ClaudeModel);
    }

    [Fact]
    public void stored_old_sonnet_default_moves_to_haiku_once_and_later_choice_is_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{\"claude_model\":\"sonnet\"}");
        var cfg = Config.Load(ConfigPath);
        Assert.Equal("haiku", cfg.ClaudeModel);

        // Sonnet picked in the menu afterwards must survive a restart.
        cfg.ClaudeModel = "sonnet";
        cfg.Save();
        Assert.Equal("sonnet", Config.Load(ConfigPath).ClaudeModel);
    }

    [Theory]
    [InlineData("{}", "gpt-6-luna")]
    [InlineData("{\"chatgpt_model\":\"\"}", "gpt-6-luna")]
    [InlineData("{\"chatgpt_model\":null}", "gpt-6-luna")]
    [InlineData("{\"chatgpt_model\":\"  \"}", "gpt-6-luna")]
    [InlineData("{\"chatgpt_model\":\"gpt-6.1-terra\"}", "gpt-6.1-terra")]
    public void legacy_empty_model_uses_luna_and_explicit_model_is_kept(string json, string expected)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, json);
        var cfg = Config.Load(ConfigPath);
        Assert.Equal(expected, cfg.ChatGptModel);
        cfg.Save();
        Assert.Equal(expected, Config.Load(ConfigPath).ChatGptModel);
    }

    [Fact]
    public void chatgpt_engine_connection_model_and_tone_survive_restart()
    {
        var cfg = Config.Load(ConfigPath);
        Assert.True(cfg.ChatGptConnected);
        cfg.Engine = "chatgpt";
        cfg.ChatGptConnected = false;
        cfg.ChatGptModel = "example-model";
        cfg.ChatGptTone = "formal";
        cfg.ClaudeTone = "informal";
        cfg.Save();
        var loaded = Config.Load(ConfigPath);
        Assert.Equal("chatgpt", loaded.Engine);
        Assert.False(loaded.ChatGptConnected);
        Assert.Equal("example-model", loaded.ChatGptModel);
        Assert.Equal("formal", loaded.ChatGptTone);
        Assert.Equal("informal", loaded.ClaudeTone);
    }

    [Fact]
    public void claude_disconnected_stays_disconnected_after_restart()
    {
        Assert.True(Config.Load(ConfigPath).ClaudeConnected, "Claude starts connected by default");
        var cfg = Config.Load(ConfigPath);
        cfg.ClaudeConnected = false;
        cfg.Save();

        Assert.False(Config.Load(ConfigPath).ClaudeConnected);
    }

    [Fact]
    public void login_tokens_saved_encrypted_never_in_plain_text()
    {
        var cfg = Config.Load(ConfigPath);
        cfg.DlAccess     = "access-secret-123";
        cfg.RefreshToken = "refresh-secret-456";
        cfg.Save();

        var json = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("access-secret-123", json);
        Assert.DoesNotContain("refresh-secret-456", json);

        var reloaded = Config.Load(ConfigPath);
        Assert.Equal("access-secret-123", reloaded.DlAccess);
        Assert.Equal("refresh-secret-456", reloaded.RefreshToken);
    }

    [Fact]
    public void old_plain_text_tokens_migrated_and_removed_from_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            {"dl_access":"old-access","refresh_token":"old-refresh","identity_token":"old-id","tgt_lang":"tr"}
            """);

        var cfg = Config.Load(ConfigPath);

        Assert.Equal("old-access", cfg.DlAccess);
        Assert.Equal("old-refresh", cfg.RefreshToken);
        Assert.Equal("tr", cfg.TgtLang);
        var json = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("old-access", json);
        Assert.DoesNotContain("old-refresh", json);
        Assert.DoesNotContain("old-id", json);
    }

    [Fact]
    public void settings_saved_and_reloaded_with_no_temporary_file_left()
    {
        var cfg = Config.Load(ConfigPath);
        cfg.TgtLang  = "en-GB";
        cfg.Geometry = "10,20,800,600";
        cfg.Save();
        cfg.Save();

        var reloaded = Config.Load(ConfigPath);
        Assert.Equal("en-GB", reloaded.TgtLang);
        Assert.Equal("10,20,800,600", reloaded.Geometry);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void save_cannot_write_original_settings_file_unchanged()
    {
        var cfg = Config.Load(ConfigPath);
        cfg.TgtLang = "de";
        cfg.Save();
        var before = File.ReadAllText(ConfigPath);

        // Another program holds the temporary file open, so the new settings cannot be written.
        using (new FileStream(ConfigPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            cfg.TgtLang = "tr";
            cfg.Save();
        }

        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void damaged_settings_file_loads_defaults_without_crashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ this is not json");

        var cfg = Config.Load(ConfigPath);

        Assert.Equal("de", cfg.TgtLang);
        Assert.Null(cfg.RefreshToken);
    }

    [Fact]
    public void settings_of_old_app_name_moved_with_login_intact()
    {
        var oldDir = Path.Combine(_dir, "DeepL-Translator");
        var newDir = Path.Combine(_dir, "Nax-Translate");
        var old = Config.Load(Path.Combine(oldDir, "config.json"));
        old.RefreshToken = "refresh-secret-456";
        old.TgtLang = "tr";
        old.Save();

        Config.MoveLegacyFolder(oldDir, newDir);

        Assert.False(Directory.Exists(oldDir));
        var moved = Config.Load(Path.Combine(newDir, "config.json"));
        Assert.Equal("refresh-secret-456", moved.RefreshToken);
        Assert.Equal("tr", moved.TgtLang);
    }

    [Fact]
    public void settings_of_old_app_name_never_replace_existing_new_settings()
    {
        var oldDir = Path.Combine(_dir, "DeepL-Translator");
        var newDir = Path.Combine(_dir, "Nax-Translate");
        var old = Config.Load(Path.Combine(oldDir, "config.json"));
        old.TgtLang = "tr";
        old.Save();
        var current = Config.Load(Path.Combine(newDir, "config.json"));
        current.TgtLang = "en-US";
        current.Save();

        Config.MoveLegacyFolder(oldDir, newDir);

        Assert.True(Directory.Exists(oldDir));
        Assert.Equal("en-US", Config.Load(Path.Combine(newDir, "config.json")).TgtLang);
    }

    [Fact]
    public void settings_of_nax_deepl_moved_before_older_settings_and_older_left_alone()
    {
        var olderDir = Path.Combine(_dir, "DeepL-Translator");
        var oldDir   = Path.Combine(_dir, "Nax-DeepL");
        var newDir   = Path.Combine(_dir, "Nax-Translate");
        var older = Config.Load(Path.Combine(olderDir, "config.json"));
        older.TgtLang = "tr";
        older.Save();
        var old = Config.Load(Path.Combine(oldDir, "config.json"));
        old.RefreshToken = "refresh-secret-789";
        old.Engine = "claude";
        old.Save();

        Config.MoveLegacyFolders([oldDir, olderDir], newDir);

        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(olderDir), "the older folder must not be moved onto the new one");
        var moved = Config.Load(Path.Combine(newDir, "config.json"));
        Assert.Equal("refresh-secret-789", moved.RefreshToken);
        Assert.Equal("claude", moved.Engine);
    }
}
