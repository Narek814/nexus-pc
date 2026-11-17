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
    private static readonly (string Wire, string Title)[] Known =
    [
        ("power", "Питание: выключение, сон, блокировка"),
        ("apps", "Программы и профили"),
        ("monitoring", "Мониторинг, уведомления и история"),
        ("rgb", "Подсветка"),
        ("input", "Тачпад и клавиатура (полный контроль ПК)"),
        ("files", "Файлы (папка «Загрузки\\Nexus PC»)"),
        ("clipboard", "Буфер обмена"),
        ("automation", "Автоматизации"),
    ];

    private readonly AgentConnection _agent;
    private readonly string _agentExe;
    private readonly ListBox _devices = new() { Width = 220, Height = 300 };
    private readonly CheckedListBox _permissions = new() { Width = 360, Height = 210, CheckOnClick = true };
    private readonly Label _info = new() { AutoSize = false, Width = 360, Height = 40 };
    private readonly Button _save = new() { Text = "Сохранить разрешения", Width = 220, Height = 36 };
    private readonly Button _revoke = new() { Text = "Отключить устройство", Width = 170, Height = 36 };
    private readonly Label _status = new() { AutoSize = false, Width = 600, Height = 36, ForeColor = Theme.Muted };
    private List<(string Id, string Name, HashSet<string> Permissions)> _items = [];

    public DevicesForm(AgentConnection agent, string agentExe)
    {
        _agent = agent;
        _agentExe = agentExe;
        Text = "Nexus PC — устройства и разрешения";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 400);

        var heading = new Label { Text = "Сопряжённые устройства", Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
        _devices.Location = new Point(20, 45);
        _info.Location = new Point(260, 45);
        _permissions.Location = new Point(260, 90);
        _save.Location = new Point(260, 310);
        _revoke.Location = new Point(490, 310);
        _status.Location = new Point(20, 355);
        foreach (var (_, title) in Known)
        {
            _permissions.Items.Add(title);
        }
        Controls.AddRange([heading, _devices, _info, _permissions, _save, _revoke, _status]);
        Theme.Apply(this);
        _save.FlatAppearance.BorderColor = Theme.Cyan;

        _devices.SelectedIndexChanged += (_, _) => ShowSelected();
        _save.Click += async (_, _) => await SaveAsync();
        _revoke.Click += async (_, _) => await RevokeAsync();
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync(string? select = null)
    {
        var result = await _agent.CallAsync(SessionOps.DevicesList);
        if (!result.Ok)
        {
            _status.Text = "Нет связи со службой Nexus PC: " + result.Message;
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
            _info.Text = "Устройств нет. Подключите iPhone через меню значка Nexus PC.";
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
        _info.Text = $"{item.Name}\nОтметьте, что этому устройству можно делать.";
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
            _status.Text = process.ExitCode == 0 ? $"Сохранено для «{item.Name}». Приложение на iPhone увидит изменения после перезапуска." : $"Не удалось сохранить (код {process.ExitCode}).";
        }
        catch (Win32Exception)
        {
            _status.Text = "Отменено: для изменения разрешений нужны права администратора.";
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
        if (MessageBox.Show(this, $"Отключить «{item.Name}»? Оно сразу потеряет доступ, а его автоматизации перестанут работать. Подключить снова можно через QR-код.",
                "Nexus PC", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        var result = await _agent.CallAsync(SessionOps.DevicesRevoke, new JsonObject { ["deviceId"] = item.Id });
        _status.Text = result.Ok ? $"«{item.Name}» отключено." : "Не удалось: " + result.Message;
        await LoadAsync();
    }
}
