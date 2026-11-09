# Nax-Translate

Nax-Translate is a fast, lightweight Windows desktop translator that works with your **DeepL Pro**, **Claude** or **ChatGPT** subscription. It was called Nax-DeepL before version 1.2.0. It starts quickly, stays small in the tray and keeps working when your network changes.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-11-purple)
[![License: Nax Source-Available](https://img.shields.io/badge/license-Nax%20Source--Available-blue.svg)](LICENSE)

## Features

- **Instant translation:** type or paste, and the translation appears after a short pause.
- **Global hotkey:** press `Ctrl+C+C` anywhere to translate the copied text.
- **Survives network switches:** when Windows switches networks (Wi-Fi, LAN or VPN), the app drops its old connections and retries on the new network instead of going blank.
- **Login once:** you sign in with your DeepL Pro account in the browser. The session is renewed automatically and stored encrypted for your Windows user.
- **Automatic target language:** when the text is already in the target language, German switches to English (US) and every other language switches to German.
- **Classic, Next-gen, Claude or ChatGPT:** the engine switch picks the DeepL model, or translates through your signed-in Claude or ChatGPT subscription. The status line shows which engine translated and how long it took.
- **Language swap:** one click flips source and target.
- **System tray:** the app starts in the tray with its window hidden, minimizing and closing the window hide it to the tray, and one click on the tray icon opens it again. Starting the app a second time opens the window of the running one. Both "Im Infobereich starten" (start in the tray) and "In den Infobereich minimieren" (minimize to the tray) can be switched off in the ⚙ Einstellungen menu.
- **Start with Windows:** an optional entry starts the app when you sign in to Windows.
- **German and English interface** with a dark theme.

## Requirements

- Windows 10 or 11 (64-bit)
- [.NET 11 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) (x64)
- An active **DeepL Pro** subscription for the Classic and Next-gen engines
- For the Claude engine: [Claude Code](https://claude.com/claude-code) installed (`winget install Anthropic.ClaudeCode`) and signed in once by running `claude`
- For the ChatGPT engine: the [Codex CLI](https://developers.openai.com/codex/cli) or Codex desktop app installed, and the CLI signed in with ChatGPT (`codex login`). API-key sign-ins are not used.

## The Claude engine

With **Claude** picked in the title bar, the app sends the text to the installed Claude Code tool (`claude -p`) instead of DeepL. It needs no DeepL login and no API key, because it uses the subscription you are signed in with in Claude Code. An `ANTHROPIC_API_KEY` or similar variable in your environment is not passed on, so translations are never billed to an API key. Each translation counts against that subscription's usage limit. The translation appears when it is complete, the same way as with DeepL. While Claude is picked, the app keeps one Claude Code process started and waiting, so a translation does not wait for the tool to start. A waiting process sends nothing and uses no quota, but it takes about 200 MB of memory. Every process handles exactly one translation and then ends, so no translation sees an earlier text. The waiting process is ended when you pick DeepL again or quit the app. The app waits for a one-second pause in typing before it sends, sends pasted text at once, and stops a request at once when newer text is typed. When the text is already in the target language, the request itself names the other target (German text goes to English, everything else to German), so no second request is needed. The same applies to ChatGPT.

**⚙ Einstellungen → Claude trennen** (Disconnect Claude) stops using Claude: nothing more is sent to it, a running request is stopped and the waiting process is ended. **Mit Claude verbinden** (Connect Claude) first checks that Claude Code is signed in and then starts using Claude again. The badge next to the app name shows the Claude plan while Claude is picked and connected, for example "Claude Max ✓". The choice is saved.

While Claude is picked, a switch next to the target language sets the tone. **Natural** (the default) translates the way people in Germany would naturally say it and picks du or Sie to suit the text. **Formal** always uses Sie, and **Informal** always uses du. The choice is saved, and changing it translates the current text again.

Claude Code runs in safe mode for these requests, so your own hooks, plugins, CLAUDE.md files and MCP servers are not loaded, no tools are offered and no session is saved. The model is Haiku by default, which answers a sentence in about a second. **⚙ Einstellungen → Claude: genauer (Sonnet)** switches to Sonnet, which takes two to three seconds.

## The ChatGPT engine

Pick **ChatGPT** in the engine switch to translate through the installed Codex CLI and its cached ChatGPT sign-in. The app keeps a private stdio backend ready while ChatGPT is selected and connected, and checks the backend's account status before translating. It removes API-key and endpoint overrides from the child environment and forces the OpenAI provider with ChatGPT authentication. Translations count toward your plan's Codex usage limits; no separate API key is needed.

Each new text starts a fresh ephemeral conversation on that connection, with translation instructions replacing the general coding prompt. The conversation is unloaded after completion; a translation never receives earlier translations as context. Global CLI instructions and managed policies remain in effect. Inherited MCP servers are explicitly disabled, and the app verifies the effective settings before sending text. Hooks, plugins, connected apps, command execution, browsing and prompt telemetry are disabled, with a read-only sandbox and no locally saved translation conversations. Custom backend endpoint overrides are rejected. The full answer appears only after successful completion. Changing text, language, tone or engine cancels an outdated request and terminates its backend. Failed or incomplete answers are never displayed as a finished translation, and model requests are not automatically retried. This integration requires Codex CLI 0.167 or later.

A bounded memory-only cache reuses up to 32 completed translations, keyed by text, source, target, model and tone, with a combined character budget of 100,000. It is cleared on disconnect, engine change or exit. The ready backend is also stopped when ChatGPT is deselected, disconnected or the app exits. Keeping it ready makes no model request. This reduces local startup overhead, but remote service latency can still vary. No higher-usage Fast tier is enabled.

**⚙ Einstellungen → ChatGPT trennen** stops the current ChatGPT request and blocks further sends. **Mit ChatGPT verbinden** checks the existing CLI sign-in and reconnects; it does not sign you out of Codex or change your account. The connected choice and the **Natürlich | Sie | Du** tone are saved independently from Claude. ChatGPT translations use **GPT-6 Luna with low reasoning** by default. To select another model, set `"chatgpt_model"` in the app's settings file; missing or empty values use Luna, while an explicit model is preserved. The translation limit is 5000 characters.

The translation instructions require the model to preserve facts, negations, uncertainty, attribution, names and quantities, and to avoid adding explanations or correcting the source. Natural wording may change sentence structure without changing the meaning. These instructions reduce unwanted rewriting; they cannot guarantee that every translation is accurate.

## Installation

1. Download `Nax-Translate-<version>-setup.exe` from the [latest release](https://github.com/Naxterra/Nax-Translate/releases/latest).
2. Run it. It installs for your Windows user only, into `%LOCALAPPDATA%\Programs\Nax-Translate`, so no administrator rights are needed. It adds a Start menu entry and, if you choose, a desktop shortcut.
3. Click **⚙ Einstellungen → Bei DeepL anmelden** to sign in. This opens the DeepL login in your browser.

After the first login the session is saved, and later starts connect automatically.

To uninstall, use **Settings → Apps → Installed apps**. The uninstaller removes the "Start with Windows" entry and asks whether to delete your settings and the saved login as well.

An installed Nax-DeepL is updated in place: the setup moves the program to the new folder and replaces the old shortcuts. Settings from the earlier names "Nax-DeepL" and "Better DeepL Translator" are taken over automatically on the first start, including the login and the "Start with Windows" entry.

## Usage

| Action | How |
|---|---|
| Translate | Type in the left box. The translation appears after a short pause. |
| Force translate | `Ctrl+Enter` |
| Translate clipboard | Press `Ctrl+C+C` anywhere on the system. |
| Switch languages | Use the dropdowns or click ⇌ to swap. |
| Copy result | Click **📋 Kopieren / Copy**. |
| Switch engine | Use the Classic, Next-gen, Claude and ChatGPT switch. |
| Log out of DeepL or switch account | ⚙ Einstellungen → Von DeepL abmelden |
| Connect or disconnect Claude | ⚙ Einstellungen → Mit Claude verbinden / Claude trennen |
| Connect or disconnect ChatGPT | ⚙ Einstellungen → Mit ChatGPT verbinden / ChatGPT trennen |

## Building from source

```
git clone https://github.com/Naxterra/Nax-Translate.git
cd Nax-Translate
dotnet publish Nax-Translate.csproj -c Release -r win-x64 --no-self-contained -o dist
```

Run the tests with `dotnet test`. Building requires the .NET 11 SDK.

The app icon is drawn by a small tool. To change it, edit `tools\IconMaker\Program.cs` and run `dotnet run --project tools/IconMaker -- icon.ico logo.png` from the repository root, which writes the icon in all sizes from 16 to 256 pixels and the logo for the window's header.

To build the installer, run `installer\build.ps1` in PowerShell. It needs Inno Setup 7 (`winget install JRSoftware.InnoSetup.7`) and writes the setup file to `artifacts\installer`.

## Disclaimer

This app uses DeepL's internal web API on behalf of your own authenticated DeepL Pro account. It is not affiliated with or endorsed by DeepL SE. Use it in accordance with DeepL's Terms of Service.

## Author

**Naxterra**, [github.com/Naxterra](https://github.com/Naxterra)

## License

This software is under the Nax Source-Available License 1.0. You may install and use the official release builds, privately and at work, and you may read the source code. Modifying the software, redistributing it and selling it need the written permission of the owner. See [LICENSE](LICENSE).
