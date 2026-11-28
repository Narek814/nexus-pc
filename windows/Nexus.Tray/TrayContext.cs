using Nexus.Core.Apps;

namespace Nexus.Tray;

/// <summary>The tray icon and its menu. Everything else opens from here.</summary>
internal sealed class TrayContext : ApplicationContext
{

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private bool _untrusted;
    private readonly UserAppStore _store;
    private readonly AgentConnection _agent;
    private readonly AccountController _account;
    private readonly SynchronizationContext _ui;
    private readonly string? _sharedFolder;
    private readonly ToolStripMenuItem _stopScreen;
    private string? _screenViewer;
    private readonly System.Windows.Forms.Timer _inputWatch = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _lightingWatch = new() { Interval = 15000 };
    private bool _lightingConflictShown;
    private readonly ToolStripMenuItem _keyRetry;
    private bool _keyUnavailable;
    private Icon _currentIcon;
    private Form? _openForm;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPC");
        _store = new UserAppStore(dataDir);

        // Files from/to the phone: only Downloads\Nexus PC; partial uploads stay in the user's private app data.
        Nexus.Core.Files.SharedFolder? files = null;
        try
        {
            files = new Nexus.Core.Files.SharedFolder(
                Path.Combine(WindowsDesktop.DownloadsFolder(), "Nexus PC"),
                Path.Combine(dataDir, "incoming"));
            files.Received += (_, path) => WindowsDesktop.MarkAsDownloaded(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the exception type and HResult: the message may carry a path or user name (W-TRAY 7).
            TrayLog.Write(TrayLog.DefaultPath, "File sharing is off: " + TrayLog.Describe(ex));
        }
        _sharedFolder = files?.FolderPath;

        var desktop = new WindowsDesktop();
        // Asked once per device, on the PC itself; the answer is remembered (Nexus.Core.Screen.ScreenApprovals).
        desktop.Confirm = deviceName =>
        {
            var allowed = false;
            _ui.Send(_ => allowed = MessageBox.Show(
                TrayText.Format("ScreenAskBody", deviceName),
                TrayText.Get("ScreenAskTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes, null);
            return allowed;
        };
        var handler = new TrayCommandHandler(_store, desktop, files: files,
            screenApprovals: new Nexus.Core.Screen.ScreenApprovals(dataDir));
        handler.ScreenSharingChanged += (_, viewer) => _ui.Post(_ => ShowScreenSharing(viewer), null);
        handler.ActionPerformed += (_, text) => _ui.Post(_ => Notify(text), null);
        // A phone that drags and then loses connection must not leave a mouse button held.
        _inputWatch.Tick += (_, _) => handler.ReleaseIdleButtons();
        _inputWatch.Start();
        // T2: tell the person at the PC once when another program has taken the OpenRGB port.
        _lightingWatch.Tick += async (_, _) => await CheckLightingAsync();
        _lightingWatch.Start();

        var agentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Agent", "Nexus.Agent.exe"));
        _status = new ToolStripMenuItem(TrayText.Get("TrayStatusConnecting")) { Enabled = false };
        var agent = new AgentConnection(handler, agentPath, message => TrayLog.Write(TrayLog.DefaultPath, message));
        _agent = agent;
        // E4-5: the sign-in attempt lives in the service's memory; the tray keeps only "started at T" (no codes).
        _account = new AccountController(agent, new Nexus.Core.Account.LoginMarkerFile(Path.Combine(dataDir, "login-pending.json")));
        _account.Notice += (_, text) => Notify(text);
        agent.ConnectedChanged += (_, connected) => _ui.Post(async _ =>
        {
            UpdateStatus(connected);
            await CheckLightingAsync(); // also reads the PC key state at once
            if (connected)
            {
                await _account.CheckAsync(atStartup: true); // a sign-in left from before this tray start
            }
        }, null);
        // The reason is already in tray.log (AgentConnection logs it); the menu shows only a fixed text.
        agent.StatusChanged += (_, status) => _ui.Post(_ =>
        {
            if (!agent.IsConnected)
            {
                _untrusted = status.StartsWith(AgentConnection.UntrustedPrefix, StringComparison.Ordinal);
                UpdateStatus(connected: false);
            }
        }, null);

        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        menu.Items.Add(_status);
        // E4-3a: shown only while the agent reports that the PC key is unavailable. Restarts the service through the
        // elevated admin command (Windows asks for administrator rights), like the other admin actions.
        _keyRetry = new ToolStripMenuItem(TrayText.KeyRetry, null, (_, _) => RunAdminCommand(agentPath, "key retry")) { Visible = false };
        menu.Items.Add(_keyRetry);
        menu.Items.Add(new ToolStripSeparator());
        // Order of docs/design/windows.md 2.1.
        menu.Items.Add(TrayText.Get("MenuPair"), null, (_, _) => Show(() => new PairingForm(_agent)));
        menu.Items.Add(TrayText.Get("MenuDevices"), null, (_, _) => ShowDevices(agentPath));
        menu.Items.Add(TrayText.Get("MenuApps"), null, (_, _) => Show(() => new AppsForm(_store)));
        menu.Items.Add(TrayText.Get("MenuLighting"), null, (_, _) => OpenRgbWindow(agentPath));
        if (_sharedFolder is not null)
        {
            menu.Items.Add(TrayText.Get("MenuFiles"), null, (_, _) => OpenSharedFolder());
        }
        menu.Items.Add(new ToolStripSeparator());
        var stopInput = new ToolStripMenuItem(TrayText.Get("MenuStopInput"));
        stopInput.Click += (_, _) =>
        {
            handler.InputPaused = !handler.InputPaused;
            stopInput.Checked = handler.InputPaused;
        };
        menu.Items.Add(stopInput);
        _stopScreen = new ToolStripMenuItem(TrayText.Get("MenuStopScreen"));
        _stopScreen.Click += (_, _) =>
        {
            handler.ScreenPaused = !handler.ScreenPaused;
            _stopScreen.Checked = handler.ScreenPaused;
        };
        menu.Items.Add(_stopScreen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayText.AccountMenu, null, (_, _) => Show(() => new AccountForm(_account)));
        menu.Items.Add(TrayText.Get("MenuSettings"), null, (_, _) => Show(() => new SettingsForm(ExitThread)));
        menu.Items.Add(TrayText.Get("MenuExit"), null, (_, _) => ExitThread());

        _currentIcon = Theme.CreateIcon(connected: false);
        _icon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = TrayText.Get("TrayTipConnecting"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        // W-TRAY 5: the window for decisions about access (docs/design/windows.md 2.1).
        _icon.DoubleClick += (_, _) => ShowDevices(agentPath);
        _agent.Start();
    }

    private void ShowDevices(string agentPath) => Show(() => new DevicesForm(_agent, agentPath));

    private void UpdateStatus(bool connected)
    {
        if (!connected)
        {
            _keyUnavailable = false;
            _keyRetry.Visible = false;
        }
        else
        {
            _untrusted = false;
        }
        _status.Text = _keyUnavailable ? TrayText.KeyUnavailable
            : connected ? TrayText.Get("TrayStatusReady")
            : TrayText.Get(_untrusted ? "TrayStatusServiceUntrusted" : "TrayStatusServiceDown");
        _icon.Text = _keyUnavailable ? TrayText.KeyUnavailableTip : IdleTip();
        if (_screenViewer is not null)
        {
            ShowScreenSharing(_screenViewer, notify: false);
        }
        var old = _currentIcon;
        _currentIcon = Theme.CreateIcon(connected);
        _icon.Icon = _currentIcon;
        old.Dispose();
    }

    private string IdleTip() => TrayText.Get(_agent.IsConnected ? "TrayTipReady" : "TrayTipServiceDown");

    /// <summary>Visible indicator while a phone sees the screen (SECURITY.md §6).</summary>
    private void ShowScreenSharing(string? viewer, bool notify = true)
    {
        _screenViewer = viewer;
        if (viewer is not null && notify)
        {
            Notify(TrayText.Format("NoticeScreenViewer", viewer));
        }
        _stopScreen.Text = viewer is null ? TrayText.Get("MenuStopScreen") : TrayText.Format("MenuStopScreenViewer", viewer);
        _icon.Text = viewer is null ? (_keyUnavailable ? TrayText.KeyUnavailableTip : IdleTip()) : ScreenViewerTip(viewer);
    }

    /// <summary>NotifyIcon.Text allows 63 characters: the device name is cut so the whole tip fits.</summary>
    private static string ScreenViewerTip(string viewer)
    {
        const int Limit = 63;
        var empty = TrayText.Format("TrayTipScreenViewer", string.Empty);
        var room = Math.Max(1, Limit - empty.Length);
        var name = viewer.Length > room ? viewer[..Math.Max(0, room - 1)] + "…" : viewer;
        var text = TrayText.Format("TrayTipScreenViewer", name);
        return text.Length > Limit ? text[..Limit] : text;
    }

    private void OpenSharedFolder()
    {
        if (_sharedFolder is not null)
        {
            Directory.CreateDirectory(_sharedFolder);
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_sharedFolder) { UseShellExecute = true });
        }
    }

    private void Notify(string text) => _icon.ShowBalloonTip(3000, "Nexus PC", text, ToolTipIcon.Info);

    private void Show(Func<Form> create)
    {
        if (_openForm is { IsDisposed: false })
        {
            _openForm.Activate();
            return;
        }
        _openForm = create();
        _openForm.Show();
    }

    /// <summary>
    /// The OpenRGB window installed with Nexus PC (to set zone sizes, check devices). Needs administrator
    /// rights like OpenRGB itself, so it goes through the agent's admin command (Windows asks once).
    /// </summary>
    private void OpenRgbWindow(string agentPath)
    {
        if (!File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "OpenRGB", "OpenRGB.exe"))))
        {
            Notify(TrayText.Get("NoticeOpenRgbMissing"));
            return;
        }
        RunAdminCommand(agentPath, "openrgb");
    }

    /// <summary>An admin command of the installed agent, elevated (Windows asks); fixed arguments only.</summary>
    private void RunAdminCommand(string agentPath, string arguments)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(agentPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (Nexus.Core.Account.ElevatedAccountCommand.IsUacDeclined(ex.NativeErrorCode))
        {
            Notify(TrayText.Get("NoticeAdminDeclined")); // the person declined the administrator prompt
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Only the Win32 code: never the command's arguments (W-TRAY 4).
            TrayLog.Write(TrayLog.DefaultPath, "Administrator command could not start: Win32 error "
                + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Notify(TrayText.Get("ProblemElevationFailed"));
        }
    }

    private async Task CheckLightingAsync()
    {
        if (!_agent.IsConnected)
        {
            return;
        }
        var status = await _agent.CallAsync(Nexus.Core.Session.SessionOps.Status).ConfigureAwait(true);
        var keyUnavailable = status.Ok && status.Result?["state"]?.GetValue<string>() == "keyUnavailable";
        if (keyUnavailable != _keyUnavailable)
        {
            _keyUnavailable = keyUnavailable;
            _keyRetry.Visible = keyUnavailable;
            UpdateStatus(_agent.IsConnected);
            if (keyUnavailable)
            {
                Notify(TrayText.KeyUnavailable);
            }
        }
        var conflict = status.Ok && status.Result?["openRgb"]?.GetValue<string>() == "conflict";
        if (conflict && !_lightingConflictShown)
        {
            Notify(TrayText.Get("NoticeLightingConflict"));
        }
        _lightingConflictShown = conflict;
    }

    protected override void ExitThreadCore()
    {
        _lightingWatch.Dispose();
        _inputWatch.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon.Dispose();
        _account.Dispose();
        _agent.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Small local log (%LOCALAPPDATA%\NexusPC\tray.log) to explain connection problems. No phone data.</summary>
internal static class TrayLog
{
    private static readonly Lock Gate = new();

    /// <summary>The one place of the tray log path: %LOCALAPPDATA%\NexusPC\tray.log.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPC", "tray.log");

    /// <summary>What an exception may say in the log: its type and HResult, never its message (paths, names).</summary>
    public static string Describe(Exception ex) =>
        ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    public static void Write(string path, string message)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the tray.
            }
        }
    }
}
