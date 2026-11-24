using System.Globalization;
using System.Text.Json.Nodes;
using Nexus.Core.Session;

namespace Nexus.Core.Account;

/// <summary>What the Account window shows.</summary>
public enum AccountPhase
{
    /// <summary>Nothing known yet (no answer from the service).</summary>
    Unknown,
    /// <summary>Not signed in: the "Sign in" button.</summary>
    SignedOut,
    /// <summary>login.start sent, waiting for the code.</summary>
    Starting,
    /// <summary>The code is shown, login.status is polled.</summary>
    Pending,
    /// <summary>The PC is linked to an account: plan and "Unlink".</summary>
    SignedIn,
    /// <summary>Sign-in is not possible right now (see <see cref="AccountLoginModel.Problem"/>).</summary>
    Unavailable,
}

/// <summary>Why the last step did not work; the tray turns it into a localized text.</summary>
public enum AccountProblem
{
    None,
    RateLimited,
    PcsLimit,
    Network,
    /// <summary>No account server in this build ("disabled").</summary>
    NotConfigured,
    /// <summary>No connection to the service.</summary>
    ServiceUnavailable,
    KeyUnavailable,
    Expired,
    Failed,
}

/// <summary>Notice shown once by the tray (balloon) after a login.status answer.</summary>
public enum LoginNotice
{
    None,
    /// <summary>"Sign-in is not finished — finishing it": the attempt is still pending in the service.</summary>
    Resuming,
    SignedIn,
    /// <summary>"Sign-in did not finish, please try again".</summary>
    RetryNeeded,
}

/// <summary>
/// State of the tray's Account window and of the sign-in notices, without UI (tested on Linux). Inputs are the
/// answers of login.start / login.status / unlink on the session channel. The userCode lives only in memory here
/// and is never part of <see cref="ToString"/> or of the marker.
/// </summary>
public sealed class AccountLoginModel(ILoginMarker marker, TimeProvider? time = null)
{
    /// <summary>The marker is ignored (and erased) after this long: the code lives 5 minutes.</summary>
    public static readonly TimeSpan MarkerLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AccountPhase Phase { get; private set; } = AccountPhase.Unknown;

    public AccountProblem Problem { get; private set; }

    /// <summary>8 characters as the service gave them; null when no code is shown.</summary>
    public string? UserCode { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool Linked { get; private set; }

    public string? Plan { get; private set; }

    public string? EntitlementState { get; private set; }

    public bool ShouldPoll => Phase == AccountPhase.Pending;

    /// <summary>"ABCD-EFGH": two groups of 4.</summary>
    public string? UserCodeDisplay => FormatUserCode(UserCode);

    public TimeSpan Remaining =>
        ExpiresAt is { } at && at > _time.GetUtcNow() ? at - _time.GetUtcNow() : TimeSpan.Zero;

    /// <summary>"m:ss" of <see cref="Remaining"/>.</summary>
    public string RemainingText
    {
        get
        {
            var r = Remaining;
            var seconds = (int)Math.Ceiling(r.TotalSeconds);
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        }
    }

    public static string? FormatUserCode(string? code) =>
        code is { Length: 8 } ? string.Concat(code.AsSpan(0, 4), "-", code.AsSpan(4, 4)) : code;

    /// <summary>The "Sign in" button was pressed.</summary>
    public void BeginStart()
    {
        Phase = AccountPhase.Starting;
        Problem = AccountProblem.None;
        ClearCode();
    }

    public void OnStartReply(RpcResult reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok)
        {
            Fail(reply);
            return;
        }
        ReadAccount(reply.Result);
        if (State(reply.Result) == "pending" && Str(reply.Result, "userCode") is { Length: > 0 } code)
        {
            Phase = AccountPhase.Pending;
            Problem = AccountProblem.None;
            UserCode = code;
            ExpiresAt = ParseTime(Str(reply.Result, "expiresAt"));
            marker.Write(_time.GetUtcNow());
            return;
        }
        Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
        Problem = Linked ? AccountProblem.None : AccountProblem.Failed;
    }

    /// <summary>
    /// A login.status answer. <paramref name="atStartup"/>: the tray just started (or reconnected) and checks for an
    /// attempt left from before; only then "pending" gives the <see cref="LoginNotice.Resuming"/> notice. "ok",
    /// "expired", "failed" and "none" give a notice only while the marker of this or the previous tray run is alive.
    /// </summary>
    public LoginNotice OnStatusReply(RpcResult reply, bool atStartup = false)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok && Phase == AccountPhase.Pending && ExpiresAt is { } at && _time.GetUtcNow() >= at)
        {
            Tick(); // the code ran out while the service could not be asked: stop polling, erase the marker
            return LoginNotice.None;
        }
        if (!reply.Ok)
        {
            // No service, no account server, no PC key: the tray keeps working and says nothing at startup.
            var wasPending = Phase == AccountPhase.Pending;
            if (!atStartup || Phase != AccountPhase.Unknown)
            {
                if (wasPending && IsTransient(reply))
                {
                    return LoginNotice.None; // keep the code and poll again
                }
                Fail(reply);
            }
            return LoginNotice.None;
        }
        var alive = MarkerAlive();
        ReadAccount(reply.Result);
        switch (State(reply.Result))
        {
            case "pending":
                Phase = AccountPhase.Pending;
                Problem = AccountProblem.None;
                UserCode = Str(reply.Result, "userCode") ?? UserCode;
                ExpiresAt = ParseTime(Str(reply.Result, "expiresAt")) ?? ExpiresAt;
                if (alive is null)
                {
                    marker.Write(_time.GetUtcNow());
                }
                return atStartup ? LoginNotice.Resuming : LoginNotice.None;
            case "ok":
                ClearCode();
                Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
                Problem = AccountProblem.None;
                return Finish(alive, Linked ? LoginNotice.SignedIn : LoginNotice.None);
            case "expired":
                ClearCode();
                Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
                Problem = alive is null ? Problem : AccountProblem.Expired;
                return Finish(alive, LoginNotice.RetryNeeded);
            case "failed":
                ClearCode();
                Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
                Problem = alive is null ? Problem : Map(Str(reply.Result, "error"));
                return Finish(alive, LoginNotice.RetryNeeded);
            default: // "none": nothing started, or the attempt was lost when the service restarted
                ClearCode();
                Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
                Problem = alive is null ? Problem : AccountProblem.Expired;
                return Finish(alive, LoginNotice.RetryNeeded);
        }
    }

    public void OnUnlinkReply(RpcResult reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok)
        {
            Fail(reply);
            return;
        }
        ClearCode();
        marker.Clear();
        ReadAccount(reply.Result);
        Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
        Problem = AccountProblem.None;
    }

    /// <summary>The person cancelled: stop showing the code and polling; the marker is erased.</summary>
    public void Cancel()
    {
        ClearCode();
        marker.Clear();
        Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
        Problem = AccountProblem.None;
    }

    /// <summary>Once a second while the window is open: the countdown ran out.</summary>
    public void Tick()
    {
        if (Phase == AccountPhase.Pending && ExpiresAt is { } at && _time.GetUtcNow() >= at)
        {
            ClearCode();
            marker.Clear();
            Phase = Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
            Problem = AccountProblem.Expired;
        }
    }

    public override string ToString() => $"AccountLoginModel(Phase={Phase}, Problem={Problem}, Linked={Linked})";

    /// <summary>Maps an error code of the account client to a problem; unknown codes are <see cref="AccountProblem.Failed"/>.</summary>
    public static AccountProblem Map(string? code) => code switch
    {
        "rate.limited" or "rateLimited" or "slow_down" => AccountProblem.RateLimited,
        "pcs.limit" => AccountProblem.PcsLimit,
        "transient" => AccountProblem.Network,
        "disabled" => AccountProblem.NotConfigured,
        "keyUnavailable" => AccountProblem.KeyUnavailable,
        "code.invalid" or "expired" => AccountProblem.Expired,
        "session.unavailable" or "session.timeout" => AccountProblem.ServiceUnavailable,
        _ => AccountProblem.Failed,
    };

    /// <summary>The live marker's time, or null; an old one (over <see cref="MarkerLifetime"/>) is erased.</summary>
    private DateTimeOffset? MarkerAlive()
    {
        if (marker.Read() is not { } started)
        {
            return null;
        }
        var age = _time.GetUtcNow() - started;
        if (age < TimeSpan.Zero || age > MarkerLifetime)
        {
            marker.Clear();
            return null;
        }
        return started;
    }

    private LoginNotice Finish(DateTimeOffset? alive, LoginNotice notice)
    {
        if (alive is null)
        {
            return LoginNotice.None;
        }
        marker.Clear();
        return notice;
    }

    private void Fail(RpcResult reply)
    {
        var problem = ProblemOf(reply);
        ClearCode();
        Problem = problem;
        Phase = problem is AccountProblem.NotConfigured or AccountProblem.ServiceUnavailable or AccountProblem.KeyUnavailable
            ? AccountPhase.Unavailable
            : Linked ? AccountPhase.SignedIn : AccountPhase.SignedOut;
    }

    /// <summary>The service code is in Message when the session server wraps it as "admin.failed".</summary>
    private static AccountProblem ProblemOf(RpcResult reply) =>
        reply.ErrorCode == "admin.failed" ? Map(reply.Message) : Map(reply.ErrorCode);

    private static bool IsTransient(RpcResult reply) =>
        ProblemOf(reply) is AccountProblem.Network or AccountProblem.ServiceUnavailable or AccountProblem.RateLimited;

    private void ClearCode()
    {
        UserCode = null;
        ExpiresAt = null;
    }

    private void ReadAccount(JsonObject? result)
    {
        if (result?["linked"] is JsonValue v && v.TryGetValue<bool>(out var linked))
        {
            Linked = linked;
        }
        Plan = Str(result, "plan") ?? Plan;
        EntitlementState = Str(result, "entitlementState") ?? EntitlementState;
    }

    private static string? State(JsonObject? result) => Str(result, "state");

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static DateTimeOffset? ParseTime(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
}
