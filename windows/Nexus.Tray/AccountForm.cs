using Nexus.Core.Account;
using Nexus.Core.Session;

namespace Nexus.Tray;

/// <summary>
/// Sign-in of this PC to an account (E4-5), on the UI thread. Logic is in <see cref="AccountLoginModel"/>; this class
/// only calls login.start / login.status / unlink and polls while a code is shown (also with the window closed, while
/// the tray runs; no extra background process). The userCode is never written to the log.
/// </summary>
internal sealed class AccountController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly AgentConnection _agent;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = (int)PollInterval.TotalMilliseconds };
    /// <summary>Calls run one after another (UI thread): a request made while another runs waits, it is not dropped.</summary>
    private Task _tail = Task.CompletedTask;
    private int _queued;
    private bool _disposed;

    public AccountController(AgentConnection agent, ILoginMarker marker)
    {
        _agent = agent;
        Model = new AccountLoginModel(marker);
        _poll.Tick += async (_, _) =>
        {
            if (_queued == 0) // a periodic poll is skipped while something is still running; it comes again in 5 s
            {
                await CheckAsync(atStartup: false).ConfigureAwait(true);
            }
        };
    }

    private Task Enqueue(Func<Task> step)
    {
        _queued++;
        var task = RunAfterAsync(_tail, step);
        _tail = task;
        return task;
    }

    private async Task RunAfterAsync(Task previous, Func<Task> step)
    {
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (!_disposed)
            {
                await step().ConfigureAwait(true);
            }
        }
        finally
        {
            _queued--;
        }
    }

    public AccountLoginModel Model { get; }

    public event EventHandler? Changed;

    /// <summary>Text for a balloon notice (already localized).</summary>
    public event EventHandler<string>? Notice;

    /// <summary>login.status; at tray start (and reconnect) with <paramref name="atStartup"/>.</summary>
    public Task CheckAsync(bool atStartup) => Enqueue(async () =>
    {
        if (atStartup && !_agent.IsConnected)
        {
            return;
        }
        Model.Tick(); // the code may have run out while the tray could not ask
        var reply = await _agent.CallAsync(SessionOps.LoginStatus, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        if (_disposed)
        {
            return; // an answer that arrives after the tray closed is ignored
        }
        Show(Model.OnStatusReply(reply, atStartup));
        Update();
    });

    public Task StartAsync()
    {
        if (!_disposed)
        {
            Model.BeginStart(); // shows "Getting a code…" at once, even while an earlier call is still running
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return Enqueue(async () =>
        {
            var reply = await _agent.CallAsync(SessionOps.LoginStart, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }
            Model.OnStartReply(reply);
            Update();
        });
    }

    public Task UnlinkAsync() => Enqueue(async () =>
    {
        var reply = await _agent.CallAsync(SessionOps.Unlink, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }
        Model.OnUnlinkReply(reply);
        Update();
    });

    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }
        Model.Cancel();
        Update();
    }

    /// <summary>Countdown tick from the open window.</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }
        var before = Model.Phase;
        Model.Tick();
        if (before != Model.Phase)
        {
            Update();
        }
    }

    private void Show(LoginNotice notice)
    {
        if (notice != LoginNotice.None)
        {
            Notice?.Invoke(this, TrayText.Notice(notice));
        }
    }

    private void Update()
    {
        if (_disposed)
        {
            return;
        }
        _poll.Enabled = Model.ShouldPoll;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Enabled = false;
        _poll.Dispose();
        Changed = null;
        Notice = null;
    }
}

/// <summary>The "Account" window: sign-in code with countdown, or the linked account, its plan and "Unlink".</summary>
internal sealed class AccountForm : Form
{
    private readonly AccountController _account;
    private readonly Label _text = new() { AutoSize = false, Width = 440, Height = 60 };
    private readonly Label _code = new() { AutoSize = false, Width = 440, Height = 56, Font = new Font("Consolas", 26f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Cyan };
    private readonly Label _detail = new() { AutoSize = false, Width = 440, Height = 40, ForeColor = Theme.Muted };
    private readonly Label _problem = new() { AutoSize = false, Width = 440, Height = 44, ForeColor = Theme.Violet };
    private readonly Button _primary = new() { Width = 160, Height = 36 };
    private readonly Button _secondary = new() { Width = 120, Height = 36 };
    private readonly System.Windows.Forms.Timer _countdown = new() { Interval = 1000 };

    public AccountForm(AccountController account)
    {
        _account = account;
        Text = TrayText.AccountTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 330);

        var heading = new Label { Text = TrayText.AccountHeading, Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
        _text.Location = new Point(20, 50);
        _code.Location = new Point(20, 112);
        _detail.Location = new Point(20, 172);
        _problem.Location = new Point(20, 216);
        _primary.Location = new Point(20, 276);
        _secondary.Location = new Point(340, 276);
        Controls.AddRange([heading, _text, _code, _detail, _problem, _primary, _secondary]);
        Theme.Apply(this);
        Theme.MakePrimary(_primary);

        _primary.Click += async (_, _) => await PrimaryAsync().ConfigureAwait(true);
        _secondary.Click += (_, _) =>
        {
            if (_account.Model.Phase == AccountPhase.Pending)
            {
                _account.Cancel();
            }
            else
            {
                Close();
            }
        };
        _countdown.Tick += (_, _) =>
        {
            _account.Tick();
            Render();
        };
        _account.Changed += OnChanged;
        Render();
        Shown += async (_, _) => await _account.CheckAsync(atStartup: false).ConfigureAwait(true);
        _countdown.Start();
    }

    private void OnChanged(object? sender, EventArgs e) => Render();

    private async Task PrimaryAsync()
    {
        var model = _account.Model;
        if (model.Phase == AccountPhase.SignedIn)
        {
            if (MessageBox.Show(this, TrayText.AccountUnlinkConfirm, TrayText.AccountTitle, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                await _account.UnlinkAsync().ConfigureAwait(true);
            }
            return;
        }
        await _account.StartAsync().ConfigureAwait(true);
    }

    private void Render()
    {
        if (IsDisposed)
        {
            return;
        }
        var model = _account.Model;
        _code.Text = string.Empty;
        _detail.Text = string.Empty;
        _problem.Text = TrayText.Problem(model.Problem);
        _primary.Visible = true;
        _primary.Enabled = true;
        _secondary.Text = TrayText.AccountClose;
        switch (model.Phase)
        {
            case AccountPhase.Unknown:
                _text.Text = TrayText.AccountChecking;
                _primary.Visible = false;
                break;
            case AccountPhase.Starting:
                _text.Text = TrayText.AccountStarting;
                _primary.Text = TrayText.AccountSignIn;
                _primary.Enabled = false;
                break;
            case AccountPhase.Pending:
                _text.Text = TrayText.AccountCodeHint;
                _code.Text = model.UserCodeDisplay ?? string.Empty;
                _detail.Text = TrayText.AccountExpiresIn.Replace("{0}", model.RemainingText, StringComparison.Ordinal);
                _primary.Visible = false;
                _secondary.Text = TrayText.AccountCancel;
                break;
            case AccountPhase.SignedIn:
                _text.Text = TrayText.AccountLinked;
                _detail.Text = TrayText.AccountPlan.Replace("{0}", TrayText.Plan(model.Plan), StringComparison.Ordinal);
                _primary.Text = TrayText.AccountUnlink;
                break;
            case AccountPhase.Unavailable:
                _text.Text = string.Empty;
                _primary.Visible = false;
                break;
            default:
                _text.Text = TrayText.AccountSignedOut;
                _primary.Text = TrayText.AccountSignIn;
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _account.Changed -= OnChanged;
            _countdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
