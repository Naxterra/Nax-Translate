using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace NaxTranslate;

public partial class App : Application
{
    // Named pipes are visible across all Windows sessions, while the single-instance mutex is per
    // session. With the session number in the name, a login link or a "show" request of one user
    // never reaches the app of another user who is signed in at the same time.
    static readonly string PipeName = $"Nax-Translate-Auth-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// What a second start of the app sends to the running one, so that it shows its window. With
    /// the window hidden in the tray, starting the app again would otherwise seem to do nothing.
    /// </summary>
    internal const string ShowCommand = "nax-translate:show";

    /// <summary>The switch that opens the window even when the app normally starts in the tray.</summary>
    internal const string ShowSwitch = "--show";

    /// <summary>Whether this start keeps the window hidden and shows only the tray icon.</summary>
    internal static bool ShouldStartHidden(bool startInTray, string[] args) =>
        startInTray && !HasSwitch(args, ShowSwitch);

    /// <summary>
    /// Whether a hidden start shows a short note that the app runs in the tray. A start by hand
    /// gets the note, because Windows 11 often keeps new tray icons out of sight and the start would
    /// otherwise seem to do nothing. The start at sign-in, which carries --autostart, stays quiet.
    /// </summary>
    internal static bool ShouldNotifyTrayStart(bool startsHidden, string[] args) =>
        startsHidden && !HasSwitch(args, StartupHelper.AutostartSwitch);

    static bool HasSwitch(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    static Mutex?              _mutex;
    internal Forms.NotifyIcon? TrayIcon;
    internal HotkeyManager?   Hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Draw without the graphics card. A window whose GPU surface is created while the screen is
        // locked, the display is off or the driver is busy can stay black, because WPF only redraws
        // when something changes and this window mostly sits still in the tray. The window is
        // small, so software drawing costs nothing noticeable.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // If launched with a deepl:// callback URL, forward it to the running instance
        if (e.Args.Length > 0 && e.Args[0].StartsWith("deepl://", StringComparison.OrdinalIgnoreCase))
        {
            SendToRunningApp(e.Args[0]);
            Shutdown();
            return;
        }

        // The uninstaller runs the app once with this switch, so that nothing the app wrote to
        // the registry outlives it: the "Start with Windows" entry and a login's deepl:// handler.
        if (e.Args.Length > 0 && e.Args[0] == "--uninstall-cleanup")
        {
            AuthManager.RestoreLeftoverSchemeHandler();
            StartupHelper.SetEnabled(false);
            Shutdown();
            return;
        }

        // Single instance guard. A second start asks the running app to show its window and ends.
        _mutex = new Mutex(true, "Nax-Translate-7F3A", out bool first);
        if (!first)
        {
            SendToRunningApp(ShowCommand);
            Shutdown();
            return;
        }

        // Undo the deepl:// handler change of a login that a crash cut short, then start
        // replacing dead connections whenever Windows reports a network change.
        AuthManager.RestoreLeftoverSchemeHandler();
        Net.WatchNetworkChanges();
        // Working folders that a crashed or killed earlier run of the ChatGPT engine left behind.
        TempWork.RemoveLeftovers();

        // A "Start with Windows" entry from before the rename points at the old program file, and
        // one from before 1.2.0 lacks --autostart. Writing it again fixes both.
        try
        {
            StartupHelper.MoveLegacyEntry();
            if (StartupHelper.IsEnabled()) StartupHelper.SetEnabled(true);
        }
        catch { }

        // Listen for OAuth callbacks from second instances
        _ = Task.Run(ListenForCallbacks);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Tray icon
        TrayIcon = new Forms.NotifyIcon
        {
            Text    = Loc.AppTitle,
            Icon    = LoadIcon(),
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.TrayOpen, null, (_, _) => ShowMain());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.TrayExit, null, (_, _) => ExitApp());
        TrayIcon.ContextMenuStrip = menu;
        // One click with the left button opens the window, as with other tray apps on Windows 11.
        TrayIcon.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowMain(); };

        // Global Ctrl+C+C hotkey
        Hotkey = new HotkeyManager();
        Hotkey.CtrlCCPressed += () => ((MainWindow?)MainWindow)?.OnCtrlCC();

        // The app starts in the tray unless that option is off or it was started with --show,
        // which the installer uses so that "Launch" after setup opens the window.
        var win = new MainWindow();
        var hidden = ShouldStartHidden(win.StartsInTray, e.Args);
        if (hidden) win.StartHidden();
        else        win.Show();
        if (ShouldNotifyTrayStart(hidden, e.Args))
        {
            // Shown as a Windows notification, which stays visible even when the tray icon itself
            // is in the hidden overflow area. Clicking it opens the window.
            TrayIcon.BalloonTipClicked += (_, _) => ShowMain();
            TrayIcon.ShowBalloonTip(5000, Loc.AppTitle, Loc.TrayStartedHint, Forms.ToolTipIcon.None);
        }
    }

    static void SendToRunningApp(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.Write(message);
        }
        catch { }
    }

    async Task ListenForCallbacks()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    maxNumberOfServerInstances: 1, transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var message = (await reader.ReadToEndAsync()).Trim();
                if (message == ShowCommand)
                    Dispatcher.Invoke(ShowMain);
                else if (message.Length > 0)
                    Dispatcher.Invoke(() => AuthManager.HandleCallback(message));
            }
            catch { await Task.Delay(500); }
        }
    }

    internal void ShowMain()
    {
        if (MainWindow is MainWindow w)
        {
            w.Show();
            w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    void ExitApp()
    {
        // A Claude run would keep going after the app is gone and use up quota.
        (MainWindow as MainWindow)?.StopBackgroundWork();
        (MainWindow as MainWindow)?.SaveSettings();
        TrayIcon?.Dispose();
        Hotkey?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Also covers ending the app through Windows sign-out or shutdown, which skips ExitApp.
        (MainWindow as MainWindow)?.StopBackgroundWork();
        TrayIcon?.Dispose();
        Hotkey?.Dispose();
        base.OnExit(e);
    }

    static System.Drawing.Icon LoadIcon()
    {
        var uri  = new Uri("pack://application:,,,/icon.ico");
        var info = Application.GetResourceStream(uri);
        // The tray shows small icons, so pick the size Windows uses for them at the current display
        // scale (16, 20 or 24 pixels) instead of scaling down a large one.
        return info is not null
            ? new System.Drawing.Icon(info.Stream, Forms.SystemInformation.SmallIconSize)
            : System.Drawing.SystemIcons.Application;
    }
}

