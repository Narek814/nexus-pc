using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexus.Core.Session;

namespace Nexus.Tray;

/// <summary>
/// Paired devices and what each may do. Revoking goes through the agent pipe (anyone at the PC may
/// reduce access); granting runs the agent's admin command elevated, so Windows asks for administrator
/// rights each time (P11, replaces typing "devices permissions" in PowerShell).
/// </summary>
internal sealed class DevicesForm : Form
{
    private static readonly (string Wire, string Key)[] Known =
    [
        ("power", "PermPower"),
        ("apps", "PermApps"),
        ("monitoring", "PermMonitoring"),
        ("rgb", "PermRgb"),
        ("input", "PermInput"),
        ("screen", "PermScreen"),
        ("files", "PermFiles"),
        ("clipboard", "PermClipboard"),
        ("automation", "PermAutomation"),
    ];

    private readonly AgentConnection _agent;
    private readonly string _agentExe;
    private readonly ListBox _devices = new() { Width = 220, Height = 300 };
    private readonly CheckedListBox _permissions = new() { Width = 360, Height = 210, CheckOnClick = true, IntegralHeight = false };
    private readonly Label _info = new() { AutoSize = false, Width = 360, Height = 40 };
    private readonly Button _save = new() { Text = TrayText.Get("DevicesSave"), Width = 220, Height = 36 };
    private readonly Button _revoke = new() { Text = TrayText.Get("DevicesRevoke"), Width = 170, Height = 36 };
    private readonly Label _status = new() { AutoSize = false, Width = 600, Height = 36, ForeColor = Theme.Muted };
    private List<(string Id, string Name, HashSet<string> Permissions)> _items = [];

    public DevicesForm(AgentConnection agent, string agentExe)
    {
        _agent = agent;
        _agentExe = agentExe;
        Text = TrayText.Get("DevicesTitle");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 400);

        var heading = new Label { Text = TrayText.Get("DevicesHeading"), Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
        _devices.Location = new Point(20, 45);
        _info.Location = new Point(260, 45);
        _permissions.Location = new Point(260, 90);
        _save.Location = new Point(260, 310);
        _revoke.Location = new Point(490, 310);
        _status.Location = new Point(20, 355);
        foreach (var (_, key) in Known)
        {
            _permissions.Items.Add(TrayText.Get(key));
        }
        Controls.AddRange([heading, _devices, _info, _permissions, _save, _revoke, _status]);
        Theme.Apply(this);
        Theme.MakePrimary(_save);

        _devices.SelectedIndexChanged += (_, _) => ShowSelected();
        _save.Click += async (_, _) => await SaveAsync();
        _revoke.Click += async (_, _) => await RevokeAsync();
        Shown += async (_, _) =>
        {
            _save.Enabled = _revoke.Enabled = false;
            _status.Text = TrayText.Get("LoadingText");
            await LoadAsync();
            if (_status.Text == TrayText.Get("LoadingText"))
            {
                _status.Text = string.Empty;
            }
        };
    }

    private async Task LoadAsync(string? select = null)
    {
        var result = await _agent.CallAsync(SessionOps.DevicesList);
        if (!result.Ok)
        {
            _status.Text = TrayText.Get("DevicesServiceDown");
            return;
        }
        _items = [.. (result.Result?["devices"]?.AsArray() ?? []).OfType<JsonObject>().Select(d => (
            d["deviceId"]!.GetValue<string>(),
            d["name"]!.GetValue<string>(),
            (d["permissions"]?.AsArray() ?? []).Select(p => p!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)))];
        _devices.Items.Clear();
        foreach (var item in _items)
        {
            _devices.Items.Add(item.Name);
        }
        var index = select is null ? 0 : _items.FindIndex(i => i.Id == select);
        if (_items.Count > 0)
        {
            _devices.SelectedIndex = Math.Max(0, index);
        }
        else
        {
            _info.Text = TrayText.Get("DevicesEmpty");
        }
        _save.Enabled = _revoke.Enabled = _items.Count > 0;
    }

    private void ShowSelected()
    {
        if (_devices.SelectedIndex < 0)
        {
            return;
        }
        var item = _items[_devices.SelectedIndex];
        _info.Text = item.Name + "\n" + TrayText.Get("DevicesHint");
        for (var i = 0; i < Known.Length; i++)
        {
            _permissions.SetItemChecked(i, item.Permissions.Contains(Known[i].Wire));
        }
    }

    private async Task SaveAsync()
    {
        if (_devices.SelectedIndex < 0)
        {
            return;
        }
        var item = _items[_devices.SelectedIndex];
        // Keep permissions this window does not show (future features) as they are.
        var wanted = item.Permissions.Where(p => Known.All(k => k.Wire != p)).ToList();
        wanted.AddRange(Known.Where((_, i) => _permissions.GetItemChecked(i)).Select(k => k.Wire));
        if (!Guid.TryParse(item.Id, out var id))
        {
            return;
        }
        // Only a GUID and fixed permission names go on the command line; the program is in Program Files.
        var start = new ProcessStartInfo(_agentExe, $"devices permissions {id:D} {(wanted.Count == 0 ? "none" : string.Join(',', wanted))}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                // The code goes to the log only; the window shows a plain text.
                TrayLog.Write(TrayLog.DefaultPath, "Saving permissions failed: exit code "
                    + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            _status.Text = process.ExitCode == 0 ? TrayText.Format("DevicesSaved", item.Name) : TrayText.Get("DevicesSaveFailed");
        }
        catch (Win32Exception ex) when (Nexus.Core.Account.ElevatedAccountCommand.IsUacDeclined(ex.NativeErrorCode))
        {
            _status.Text = TrayText.Get("DevicesAdminDeclined");
        }
        catch (Win32Exception ex)
        {
            // Only the Win32 code: never the command line (device id, permissions).
            TrayLog.Write(TrayLog.DefaultPath, "Permissions step could not start: Win32 error "
                + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _status.Text = TrayText.Get("ProblemElevationFailed");
        }
        await LoadAsync(item.Id);
    }

    private async Task RevokeAsync()
    {
        if (_devices.SelectedIndex < 0)
        {
            return;
        }
        var item = _items[_devices.SelectedIndex];
        if (MessageBox.Show(this, TrayText.Format("DevicesRevokeConfirm", item.Name),
                "Nexus PC", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        // A revocation waits for an automation step that has already started (up to 15 s per step).
        var result = await _agent.CallAsync(SessionOps.DevicesRevoke, new JsonObject { ["deviceId"] = item.Id }, TimeSpan.FromSeconds(60));
        _status.Text = result.Ok ? TrayText.Format("DevicesRevoked", item.Name)
            : result.ErrorCode == AgentConnection.SessionTimeout ? TrayText.Format("DevicesRevokePending", item.Name)
            : TrayText.Get("DevicesRevokeFailed");
        // Optional: the device had shutdown or restart automations; one of them may have scheduled a shutdown already.
        if (result.Ok && result.Result?["warning"] is JsonValue value && value.TryGetValue<string>(out var warning) && warning.Length > 0)
        {
            MessageBox.Show(this, warning, "Nexus PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        await LoadAsync();
    }
}
