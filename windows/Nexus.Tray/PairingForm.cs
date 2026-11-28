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
#if DEBUG
    /// <summary>"Copy Link" only feeds Nexus.DevClient during development (docs/design/windows.md 5).</summary>
    private const bool ShowCopyLink = true;
#else
    private const bool ShowCopyLink = false;
#endif

    private readonly AgentConnection _agent;
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 300, Height = 300, BackColor = Color.White };
    private readonly Label _title = new() { AutoSize = false, Width = 560, Height = 30, Font = new Font("Segoe UI Semibold", 13f) };
    private readonly Label _info = new() { AutoSize = false, Width = 240, Height = 220 };
    private readonly Label _countdown = new() { AutoSize = false, Width = 240, Height = 24, ForeColor = Theme.Muted };
    private readonly Button _copy = new() { Text = TrayText.Get("PairCopyLink"), Width = 240, Height = 34, Enabled = false };
    private readonly Button _approve = new() { Text = TrayText.Get("PairApprove"), Width = 115, Height = 38, Enabled = false };
    private readonly Button _reject = new() { Text = TrayText.Get("PairReject"), Width = 115, Height = 38, Enabled = false };
    private readonly Button _restart = new() { Text = TrayText.Get("PairRestart"), Width = 240, Height = 34, Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string? _link;
    private DateTimeOffset _expiresAt;
    private bool _finished;

    public PairingForm(AgentConnection agent)
    {
        _agent = agent;
        Text = TrayText.Get("PairTitle");
        _qr.AccessibleName = TrayText.Get("PairQrA11y");
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
        Theme.MakePrimary(_approve);

        _copy.Click += (_, _) =>
        {
            if (_link is not null)
            {
                Clipboard.SetText(_link);
                _copy.Text = TrayText.Get("PairCopied");
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
        _copy.Visible = ShowCopyLink;
        _copy.Text = TrayText.Get("PairCopyLink");
        _approve.Enabled = _reject.Enabled = false;
        _title.Text = TrayText.Get("PairScanTitle");
        _info.Text = TrayText.Get("PairOpening");

        var started = await _agent.CallAsync(SessionOps.PairStart);
        if (!started.Ok || started.Result?["link"]?.GetValue<string>() is not { } link)
        {
            ShowFailure(FailureText(started.ErrorCode));
            return;
        }
        _link = link;
        _expiresAt = DateTimeOffset.Parse(started.Result!["expiresAt"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var old = _qr.Image;
        _qr.Image = Theme.RenderQr(QrCode.EncodeText(link), 6);
        old?.Dispose();
        _copy.Enabled = true;
        _info.Text = TrayText.Format("PairCode", started.Result!["code"]) + "\n\n" + TrayText.Get("PairScanHint");
        _timer.Start();
        UpdateCountdown();

        var pending = await _agent.CallAsync(SessionOps.PairWait, null, TimeSpan.FromMinutes(3));
        if (IsDisposed)
        {
            return;
        }
        if (!pending.Ok)
        {
            ShowFailure(TrayText.Get("PairTimeout"));
            return;
        }
        _timer.Stop();
        _countdown.Text = string.Empty;
        _title.Text = TrayText.Get("PairConfirmTitle");
        _info.Text = TrayText.Format("PairConfirmBody", pending.Result!["deviceName"], pending.Result!["verificationCode"]);
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
            ShowFailure(FailureText(result.ErrorCode));
            return;
        }
        _title.Text = TrayText.Get(approve && result.Result?["approved"]?.GetValue<bool>() == true ? "PairDone" : "PairDeclined");
        _info.Text = approve
            ? TrayText.Get("PairDoneBody")
            : TrayText.Get("PairDeclinedBody");
    }

    /// <summary>A plain text by the agent's error code; the agent's own message is not shown.</summary>
    private static string FailureText(string? errorCode) => errorCode switch
    {
        "pair.busy" => TrayText.Get("PairBusy"),
        "pair.deviceLimit" => TrayText.Get("PairDeviceLimit"),
        "pair.expired" => TrayText.Get("PairTimeout"),
        _ => TrayText.Get("PairFailed"),
    };

    private void ShowFailure(string message)
    {
        _timer.Stop();
        _finished = true;
        _title.Text = TrayText.Get("PairFailedTitle");
        _info.Text = message;
        _countdown.Text = string.Empty;
        _copy.Visible = false;
        _restart.Visible = true;
        _approve.Enabled = _reject.Enabled = false;
    }

    private void UpdateCountdown()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        _countdown.Text = left > TimeSpan.Zero ? TrayText.Format("PairCountdown", left.ToString(@"m\:ss", CultureInfo.InvariantCulture)) : string.Empty;
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
