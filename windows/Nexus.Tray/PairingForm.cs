using System.Globalization;
using System.Text.Json.Nodes;
using Nexus.Core.Pairing;
using Nexus.Core.Session;

namespace Nexus.Tray;

/// <summary>
/// Opens pairing on the agent, shows the QR code and the link, then asks the person to compare the
/// verification code and approve. Closing the window cancels pairing.
/// </summary>
internal sealed class PairingForm : Form
{
    private readonly AgentConnection _agent;
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 300, Height = 300, BackColor = Color.White };
    private readonly Label _title = new() { AutoSize = false, Width = 560, Height = 30, Font = new Font("Segoe UI Semibold", 13f) };
    private readonly Label _info = new() { AutoSize = false, Width = 240, Height = 220 };
    private readonly Label _countdown = new() { AutoSize = false, Width = 240, Height = 24, ForeColor = Theme.Muted };
    private readonly Button _copy = new() { Text = "Копировать ссылку", Width = 240, Height = 34, Enabled = false };
    private readonly Button _approve = new() { Text = "Подключить", Width = 115, Height = 38, Enabled = false };
    private readonly Button _reject = new() { Text = "Отклонить", Width = 115, Height = 38, Enabled = false };
    private readonly Button _restart = new() { Text = "Начать заново", Width = 240, Height = 34, Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string? _link;
    private DateTimeOffset _expiresAt;
    private bool _finished;

    public PairingForm(AgentConnection agent)
    {
        _agent = agent;
        Text = "Nexus PC — подключить iPhone";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 420);

        _title.Location = new Point(20, 15);
        _qr.Location = new Point(20, 60);
        _info.Location = new Point(340, 60);
        _countdown.Location = new Point(340, 285);
        _copy.Location = new Point(340, 315);
        _restart.Location = new Point(340, 315);
        _approve.Location = new Point(340, 365);
        _reject.Location = new Point(465, 365);
        Controls.AddRange([_title, _qr, _info, _countdown, _copy, _restart, _approve, _reject]);
        Theme.Apply(this);
        _approve.FlatAppearance.BorderColor = Theme.Cyan;

        _copy.Click += (_, _) =>
        {
            if (_link is not null)
            {
                Clipboard.SetText(_link);
                _copy.Text = "Скопировано";
            }
        };
        _approve.Click += async (_, _) => await DecideAsync(true);
        _reject.Click += async (_, _) => await DecideAsync(false);
        _restart.Click += async (_, _) => await StartAsync();
        _timer.Tick += (_, _) => UpdateCountdown();
        Shown += async (_, _) => await StartAsync();
        FormClosing += (_, _) =>
        {
            _timer.Stop();
            if (!_finished)
            {
                _ = _agent.CallAsync(SessionOps.PairCancel);
            }
        };
    }

    private async Task StartAsync()
    {
        _finished = false;
        _restart.Visible = false;
        _copy.Visible = true;
        _copy.Text = "Копировать ссылку";
        _approve.Enabled = _reject.Enabled = false;
        _title.Text = "Отсканируйте QR-код в приложении Nexus PC";
        _info.Text = "Открываю сопряжение…";

        var started = await _agent.CallAsync(SessionOps.PairStart);
        if (!started.Ok || started.Result?["link"]?.GetValue<string>() is not { } link)
        {
            ShowFailure(started.Message ?? "Не удалось открыть сопряжение.");
            return;
        }
        _link = link;
        _expiresAt = DateTimeOffset.Parse(started.Result!["expiresAt"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var old = _qr.Image;
        _qr.Image = Theme.RenderQr(QrCode.EncodeText(link), 6);
        old?.Dispose();
        _copy.Enabled = true;
        _info.Text = $"Код: {started.Result!["code"]}\n\n" +
                     "Пока приложения для iPhone нет, скопируйте ссылку и передайте её Nexus.DevClient " +
                     "(см. docs/SETUP.md).\n\nОкно можно не закрывать: подтверждение появится здесь.";
        _timer.Start();
        UpdateCountdown();

        var pending = await _agent.CallAsync(SessionOps.PairWait, null, TimeSpan.FromMinutes(3));
        if (IsDisposed)
        {
            return;
        }
        if (!pending.Ok)
        {
            ShowFailure("Время вышло, устройство не подключилось.");
            return;
        }
        _timer.Stop();
        _countdown.Text = string.Empty;
        _title.Text = "Подтвердите устройство";
        _info.Text = $"«{pending.Result!["deviceName"]}» хочет подключиться к этому ПК.\n\n" +
                     $"Код проверки: {pending.Result!["verificationCode"]}\n\n" +
                     "Подключайте, только если такой же код показан на вашем устройстве.";
        _approve.Enabled = _reject.Enabled = true;
        _approve.Focus();
    }

    private async Task DecideAsync(bool approve)
    {
        _approve.Enabled = _reject.Enabled = false;
        var result = await _agent.CallAsync(SessionOps.PairDecide, new JsonObject { ["approve"] = approve });
        _finished = true;
        _copy.Visible = false;
        if (!result.Ok)
        {
            ShowFailure(result.Message ?? "Не удалось завершить сопряжение.");
            return;
        }
        _title.Text = approve && result.Result?["approved"]?.GetValue<bool>() == true ? "Устройство подключено" : "Подключение отклонено";
        _info.Text = approve
            ? "Разрешения по умолчанию: питание, приложения, мониторинг.\nОстальное включается отдельно."
            : "Ничего не сохранено.";
    }

    private void ShowFailure(string message)
    {
        _timer.Stop();
        _finished = true;
        _title.Text = "Сопряжение не выполнено";
        _info.Text = message;
        _countdown.Text = string.Empty;
        _copy.Visible = false;
        _restart.Visible = true;
        _approve.Enabled = _reject.Enabled = false;
    }

    private void UpdateCountdown()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        _countdown.Text = left > TimeSpan.Zero ? $"Осталось {left:m\\:ss}" : string.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _qr.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
