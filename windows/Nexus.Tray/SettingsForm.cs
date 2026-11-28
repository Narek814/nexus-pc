using System.Diagnostics;
using Microsoft.Win32;

namespace Nexus.Tray;

/// <summary>
/// Settings (docs/design/windows.md 6): start with Windows, language (applies after the tray restarts), version,
/// "Check for Updates" (opens the download page in the browser: no network request from the tray, no auto-update)
/// and the third-party notice installed with Nexus PC.
/// </summary>
internal sealed class SettingsForm : Form
{
    /// <summary>{DOWNLOAD_URL}: placeholder until the product site exists (owner decision 2026-11-25).</summary>
    internal const string DownloadUrl = "https://github.com/Narek814/nexus-pc/releases";

    private static readonly string NoticePath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "OpenRGB", "NEXUS-PC-NOTICE.md"));

    private readonly CheckBox _autostart = new() { AutoSize = true };
    private readonly Label _autostartStatus = new() { AutoSize = true, ForeColor = Theme.Violet, MaximumSize = new Size(460, 0) };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _languageHint = new() { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(460, 0), Visible = false };
    private readonly Button _restart = new() { AutoSize = true, MinimumSize = new Size(0, 34), Visible = false };
    private readonly string? _startLanguage = TrayText.ChosenLanguage();
    private bool _loading = true;

    /// <param name="exitTray">Ends this tray (used by "Restart Nexus PC" after the new tray process is started).</param>
    public SettingsForm(Action exitTray)
    {
        Text = TrayText.Get("SettingsTitle");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 15, 20, 15),
        };
        var heading = new Label { Text = TrayText.Get("SettingsHeading"), AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
        _autostart.Text = TrayText.Get("SettingsAutostart");
        _autostart.Checked = Autostart.IsEnabled();
        var autostartHint = new Label { Text = TrayText.Get("SettingsAutostartHint"), AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(460, 0) };

        var languageLabel = new Label { Text = TrayText.Get("SettingsLanguage"), AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
        // Language names in their own language, whatever the current one is.
        _language.Items.AddRange([TrayText.Get("SettingsLanguageSystem"), TrayText.Get("SettingsLanguageEn"), TrayText.Get("SettingsLanguageRu")]);
        _language.SelectedIndex = _startLanguage switch { "en" => 1, "ru" => 2, _ => 0 };
        _languageHint.Text = TrayText.Get("SettingsLanguageRestart");
        _restart.Text = TrayText.Get("SettingsRestart");

        var version = new Label
        {
            Text = TrayText.Format("SettingsVersion", Application.ProductVersion.Split('+')[0]),
            AutoSize = true, Margin = new Padding(3, 14, 3, 3),
        };
        var updates = new Button { Text = TrayText.Get("SettingsCheckUpdates"), AutoSize = true, MinimumSize = new Size(0, 34) };
        var licenses = new LinkLabel { Text = TrayText.Get("SettingsLicenses"), AutoSize = true, Visible = File.Exists(NoticePath), Margin = new Padding(3, 14, 3, 3),
            LinkColor = Theme.Cyan, ActiveLinkColor = Theme.Violet, VisitedLinkColor = Theme.Cyan };
        var close = new Button { Text = TrayText.Get("SettingsClose"), AutoSize = true, MinimumSize = new Size(110, 34), DialogResult = DialogResult.Cancel };

        layout.Controls.AddRange([heading, _autostart, autostartHint, _autostartStatus, languageLabel, _language, _languageHint, _restart,
            version, updates, licenses, close]);
        Controls.Add(layout);
        CancelButton = close;
        Theme.Apply(this);

        _autostart.CheckedChanged += (_, _) => ToggleAutostart();
        _language.SelectedIndexChanged += (_, _) => ChangeLanguage();
        _restart.Click += (_, _) => Restart(exitTray);
        updates.Click += (_, _) => OpenInShell(DownloadUrl);
        licenses.LinkClicked += (_, _) => OpenInShell(NoticePath);
        close.Click += (_, _) => Close();
        _loading = false;
    }

    private void ToggleAutostart()
    {
        if (_loading)
        {
            return;
        }
        _autostartStatus.Text = string.Empty;
        try
        {
            Autostart.Set(_autostart.Checked);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            TrayLog.Write(TrayLog.DefaultPath, "Autostart could not be changed: " + TrayLog.Describe(ex));
            _autostartStatus.Text = TrayText.Get("SettingsAutostartFailed");
        }
        _loading = true;
        _autostart.Checked = Autostart.IsEnabled();
        _loading = false;
    }

    private void ChangeLanguage()
    {
        if (_loading)
        {
            return;
        }
        var chosen = _language.SelectedIndex switch { 1 => "en", 2 => "ru", _ => null };
        try
        {
            TrayText.ChooseLanguage(chosen);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            TrayLog.Write(TrayLog.DefaultPath, "Language could not be saved: " + TrayLog.Describe(ex));
            _autostartStatus.Text = TrayText.Get("SettingsAutostartFailed");
            return;
        }
        var changed = chosen != _startLanguage;
        _languageHint.Visible = _restart.Visible = changed;
    }

    /// <summary>Restarts only the tray (the service and phone connections go on): a new tray waits for this one to exit.</summary>
    private static void Restart(Action exitTray)
    {
        if (Environment.ProcessPath is not { } self || !File.Exists(self))
        {
            return;
        }
        // Same user, not elevated, one fixed argument (Program.RestartArgument).
        using var _ = Process.Start(new ProcessStartInfo(self, Program.RestartArgument) { UseShellExecute = false });
        exitTray();
    }

    /// <summary>Opens a fixed https address or the installed notice file with the default program; nothing from a phone.</summary>
    private static void OpenInShell(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            TrayLog.Write(TrayLog.DefaultPath, "Could not open a link: Win32 error "
                + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>"Start Nexus PC when I sign in to Windows": the per-user Run value (moved here from the tray menu).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NexusPC.Tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
