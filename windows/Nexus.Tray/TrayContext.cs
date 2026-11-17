using Microsoft.Win32;
using Nexus.Core.Apps;

namespace Nexus.Tray;

/// <summary>The tray icon and its menu. Everything else opens from here.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NexusPC.Tray";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _autostart;
    private readonly UserAppStore _store;
    private readonly AgentConnection _agent;
    private readonly SynchronizationContext _ui;
    private readonly string? _sharedFolder;
    private readonly System.Windows.Forms.Timer _inputWatch = new() { Interval = 1000 };
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
            TrayLog.Write(Path.Combine(dataDir, "tray.log"), "File sharing is off: " + ex.Message);
        }
        _sharedFolder = files?.FolderPath;

        var handler = new TrayCommandHandler(_store, new WindowsDesktop(), files: files);
        handler.ActionPerformed += (_, text) => _ui.Post(_ => Notify(text), null);
        // A phone that drags and then loses connection must not leave a mouse button held.
        _inputWatch.Tick += (_, _) => handler.ReleaseIdleButtons();
        _inputWatch.Start();

        var agentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Agent", "Nexus.Agent.exe"));
        _status = new ToolStripMenuItem("Нет связи со службой") { Enabled = false };
        var logPath = Path.Combine(dataDir, "tray.log");
        var agent = new AgentConnection(handler, agentPath, message => TrayLog.Write(logPath, message));
        _agent = agent;
        agent.ConnectedChanged += (_, connected) => _ui.Post(_ => UpdateStatus(connected), null);
        agent.StatusChanged += (_, status) => _ui.Post(_ =>
        {
            if (!agent.IsConnected)
            {
                _status.Text = status.Length > 90 ? status[..90] + "…" : status;
            }
        }, null);

        _autostart = new ToolStripMenuItem("Запускать при входе в Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostartEnabled() };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Подключить iPhone…", null, (_, _) => Show(() => new PairingForm(_agent)));
        menu.Items.Add("Приложения и профили…", null, (_, _) => Show(() => new AppsForm(_store)));
        var stopInput = new ToolStripMenuItem("Запретить управление мышью и клавиатурой с телефона");
        stopInput.Click += (_, _) =>
        {
            handler.InputPaused = !handler.InputPaused;
            stopInput.Checked = handler.InputPaused;
        };
        menu.Items.Add(stopInput);
        if (_sharedFolder is not null)
        {
            menu.Items.Add("Открыть папку файлов с телефона", null, (_, _) => OpenSharedFolder());
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostart);
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _currentIcon = Theme.CreateIcon(connected: false);
        _icon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "Nexus PC — нет связи со службой",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Show(() => new AppsForm(_store));
        _agent.Start();
    }

    private void UpdateStatus(bool connected)
    {
        _status.Text = connected ? "Служба подключена" : "Нет связи со службой";
        _icon.Text = connected ? "Nexus PC — готов" : "Nexus PC — нет связи со службой";
        var old = _currentIcon;
        _currentIcon = Theme.CreateIcon(connected);
        _icon.Icon = _currentIcon;
        old.Dispose();
    }

    /// <summary>Visible notice whenever a phone did something on this PC.</summary>
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

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostartEnabled())
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        }
        _autostart.Checked = IsAutostartEnabled();
    }

    protected override void ExitThreadCore()
    {
        _inputWatch.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon.Dispose();
        _agent.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Small local log (%LOCALAPPDATA%\NexusPC\tray.log) to explain connection problems. No phone data.</summary>
internal static class TrayLog
{
    private static readonly Lock Gate = new();

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
