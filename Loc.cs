namespace NaxTranslate;

public static class Loc
{
    public static string Current { get; private set; } = "de";
    public static event Action? Changed;

    public static void SetLanguage(string lang)
    {
        if (Current == lang) return;
        Current = lang;
        Changed?.Invoke();
    }

    static bool En => Current == "en";

    public static string AppTitle        => "Nax-Translate";
    public static string AboutBlurb      => En ? "A fast translator for Windows with DeepL Pro, Claude and ChatGPT."
                                               : "Ein schneller Übersetzer für Windows mit DeepL Pro, Claude und ChatGPT.";
    public static string AboutAuthor     => En ? "Author"                                  : "Autor";
    public static string BtnConnect      => En ? "Settings"                                : "Einstellungen";
    public static string MenuLogin        => En ? "Log in to DeepL"                         : "Bei DeepL anmelden";
    public static string MenuSwitch      => En ? "Switch DeepL account"                    : "DeepL-Konto wechseln";
    public static string MenuLogout      => En ? "Log out of DeepL"                        : "Von DeepL abmelden";
    public static string MenuClaudeConnect    => En ? "Connect Claude"                     : "Mit Claude verbinden";
    public static string MenuClaudeDisconnect => En ? "Disconnect Claude"                  : "Claude trennen";
    public static string MenuClaudeFast       => En ? "Claude: fast (Haiku)"              : "Claude: schnell (Haiku)";
    public static string MenuClaudeCareful    => En ? "Claude: more careful (Sonnet)"     : "Claude: genauer (Sonnet)";
    public static string MenuChatGptConnect => En ? "Connect ChatGPT" : "Mit ChatGPT verbinden";
    public static string MenuChatGptDisconnect => En ? "Disconnect ChatGPT" : "ChatGPT trennen";
    public static string StatusChatGptChecking => En ? "Checking the ChatGPT sign-in…" : "ChatGPT-Anmeldung wird geprüft…";
    public static string StatusChatGptConnected => En ? "Connected to ChatGPT." : "Mit ChatGPT verbunden.";
    public static string StatusChatGptDisconnectedNow => En ? "ChatGPT is disconnected. No text is sent until you connect it again." : "ChatGPT ist getrennt. Bis zur nächsten Verbindung wird kein Text verschickt.";
    public static string StatusChatGptDisconnected => En ? "ChatGPT is disconnected. Click ⚙ Settings → Connect ChatGPT." : "ChatGPT ist getrennt. Bitte ⚙ Einstellungen → Mit ChatGPT verbinden.";
    public static string StatusChatGptNotSignedIn => En ? "Sign in to the Codex CLI with ChatGPT using \"codex login\", then connect again. API-key sign-ins are not used." : "Mit „codex login“ bei ChatGPT anmelden, dann erneut verbinden. API-Schlüssel werden nicht verwendet.";
    public static string StatusChatGptNotFound => En ? "Install the Codex CLI or Codex desktop app, sign in with ChatGPT, then connect again." : "Codex CLI oder Codex-App installieren, mit ChatGPT anmelden und erneut verbinden.";
    public static string StatusChatGptAuthTimeout => En ? "ChatGPT sign-in check timed out." : "Zeitüberschreitung bei der ChatGPT-Anmeldeprüfung.";
    public static string StatusChatGptTimeout => En ? "ChatGPT translation timed out. Please try again." : "Zeitüberschreitung bei der ChatGPT-Übersetzung. Bitte erneut versuchen.";
    public static string StatusChatGptFailed => En ? "ChatGPT could not complete the translation. Check your sign-in and usage limit, then try again." : "ChatGPT konnte die Übersetzung nicht abschließen. Anmeldung und Nutzungslimit prüfen und erneut versuchen.";
    public static string StatusChatGptUnexpectedTool => En ? "ChatGPT tried to use a tool. The translation was rejected." : "ChatGPT wollte ein Werkzeug verwenden. Die Übersetzung wurde verworfen.";
    public static string StatusChatGptIsolationFailed => En ? "The ChatGPT backend could not be isolated. No text was sent." : "Das ChatGPT-Backend konnte nicht isoliert werden. Es wurde kein Text verschickt.";
    public static string StatusClaudeChecking => En ? "Checking the Claude Code sign-in…" : "Anmeldung bei Claude Code wird geprüft…";
    public static string StatusClaudeConnected(string? plan) =>
        plan != null
            ? (En ? $"Connected to Claude ({plan})." : $"Mit Claude verbunden ({plan}).")
            : (En ? "Connected to Claude." : "Mit Claude verbunden.");
    public static string StatusClaudeDisconnectedNow => En ? "Claude is disconnected. Nothing is sent to Claude until you connect it again."
                                                           : "Claude ist getrennt. Bis zur nächsten Verbindung wird nichts an Claude geschickt.";
    public static string StatusClaudeDisconnected => En ? "Claude is disconnected. Click ⚙ Settings → Connect Claude."
                                                        : "Claude ist getrennt. Bitte ⚙ Einstellungen → Mit Claude verbinden.";
    public static string StatusClaudeNotSignedIn => En ? "Claude Code is not signed in. Run \"claude\" in a terminal once and sign in, then connect again."
                                                       : "Claude Code ist nicht angemeldet. Einmal „claude“ in einem Terminal starten und anmelden, dann erneut verbinden.";
    public static string BtnClear        => En ? "Clear"                                   : "Löschen";
    public static string BtnCopy         => En ? "Copy"                                    : "Kopieren";
    public static string StatusReady     => En ? "Ready"                                   : "Bereit";
    public static string StatusChecking  => En ? "Checking login…"                         : "Anmeldung wird geprüft…";
    public static string StatusBrowser   => En ? "Log in in the browser, then click \"Open app\". If nothing happens, copy the page address from the address bar."
                                               : "Im Browser anmelden, dann „App öffnen“ klicken. Passiert nichts, die Adresse aus der Adressleiste kopieren.";
    public static string StatusLoggedIn  => En ? "Logged in."                              : "Angemeldet.";
    public static string StatusLoggedOut => En ? "Logged out."                             : "Abgemeldet.";
    public static string StatusCancelled => En ? "Login cancelled or failed."              : "Anmeldung abgebrochen oder fehlgeschlagen.";
    public static string StatusNotAuth   => En ? "Not logged in to DeepL. Click ⚙ Settings." : "Nicht bei DeepL angemeldet. Bitte ⚙ Einstellungen klicken.";
    public static string StatusCopied    => En ? "Copied."                                 : "Kopiert.";
    public static string StatusClipboardBusy => En ? "The clipboard is in use by another program. Please try again."
                                                   : "Die Zwischenablage wird von einem anderen Programm benutzt. Bitte noch einmal versuchen.";
    public static string StatusUnusableReply => En ? "The answer was not a translation, even after asking twice. Please try again or pick another engine."
                                                   : "Die Antwort war auch beim zweiten Versuch keine Übersetzung. Bitte noch einmal versuchen oder eine andere Übersetzung wählen.";
    public static string NoTranslation   => En ? "No translation received."                : "Keine Übersetzung erhalten.";
    public static string StatusClaudeBusy => En ? "Claude is busy right now (too many requests). Please try again in a moment."
                                                : "Claude ist gerade ausgelastet (zu viele Anfragen). Bitte gleich noch einmal versuchen.";
    public static string StatusLimitReached => En ? "DeepL is not accepting more translations right now because a usage limit was reached. Please try again later."
                                                  : "DeepL nimmt gerade keine weiteren Übersetzungen an, weil ein Nutzungslimit erreicht ist. Bitte später noch einmal versuchen.";
    public static string StatusClaudeLimit(string detail) =>
        En ? $"Claude is not accepting more requests right now because your subscription's usage limit was reached ({detail})."
           : $"Claude nimmt gerade keine weiteren Anfragen an, weil das Nutzungslimit deines Abos erreicht ist ({detail}).";
    public static string StatusClaudeNotFound => En ? "Claude Code is not installed. Install it with \"winget install Anthropic.ClaudeCode\" and sign in once by running \"claude\"."
                                                    : "Claude Code ist nicht installiert. Mit „winget install Anthropic.ClaudeCode“ installieren und einmal „claude“ starten, um dich anzumelden.";
    public static string StatusTooLong(int length) =>
        En ? $"The text has {length} characters and was not sent. Please shorten it to {DeepLApi.MaxChars}."
           : $"Der Text hat {length} Zeichen und wurde nicht verschickt. Bitte auf {DeepLApi.MaxChars} kürzen.";
    public static string StatusTranslating => En ? "Translating…"                          : "Übersetze…";
    public static string StatusError     => En ? "Error: "                                 : "Fehler: ";
    public static string StatusOffline   => En ? "No connection to DeepL. Retrying automatically…"
                                               : "Keine Verbindung zu DeepL. Neuer Versuch läuft automatisch…";

    public static string ToneNatural      => En ? "Natural"                                 : "Natürlich";
    public static string ToneFormal       => En ? "Formal"                                  : "Sie";
    public static string ToneInformal     => En ? "Informal"                                : "Du";
    public static string ToneNaturalTip   => En ? "Translates naturally and picks du or Sie to suit the text."
                                                : "Übersetzt natürlich und wählt du oder Sie passend zum Text.";
    public static string ToneFormalTip    => En ? "Uses the formal form of address (Sie)." : "Verwendet die förmliche Anrede (Sie).";
    public static string ToneInformalTip  => En ? "Uses the informal form of address (du)." : "Verwendet die vertraute Anrede (du).";

    public static string MenuStartup      => En ? "Start with Windows"                      : "Mit Windows starten";
    public static string MenuAbout        => En ? "About"                                   : "Über";
    public static string MenuStartInTray  => En ? "Start in the tray"                       : "Im Infobereich starten";
    public static string MenuMinimizeToTray => En ? "Minimize to the tray"                  : "In den Infobereich minimieren";
    public static string TrayOpen         => En ? "Open"                                    : "Öffnen";
    public static string TrayExit         => En ? "Exit"                                    : "Beenden";
    public static string TrayStartedHint  => En ? "Runs in the tray. Click here or the tray icon to open it, or press Ctrl+C+C to translate."
                                                : "Läuft im Infobereich. Zum Öffnen hier oder auf das Symbol klicken, oder mit Strg+C+C übersetzen.";

    public static string StatusTranslated(string? lang, string engine, TimeSpan took)
    {
        var secs = took.TotalSeconds.ToString("0.0", En ? System.Globalization.CultureInfo.InvariantCulture
                                                        : System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        return lang != null
            ? (En ? $"Translated by {engine} in {secs} s (detected: {lang})" : $"Übersetzt mit {engine} in {secs} s (erkannt: {lang})")
            : (En ? $"Translated by {engine} in {secs} s" : $"Übersetzt mit {engine} in {secs} s");
    }
}
