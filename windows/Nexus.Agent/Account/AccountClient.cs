using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nexus.Agent.Security;
using Nexus.Core.Entitlements;

namespace Nexus.Agent.Account;

/// <summary>What <see cref="AccountClient.RefreshAsync"/> did; the dispatcher turns it into the account.refresh answer.</summary>
internal enum RefreshOutcome
{
    /// <summary>A new token was taken (or rejected by the tracker: the old state stands).</summary>
    Updated,
    /// <summary>No refresh token on this PC: nothing to ask.</summary>
    NotSignedIn,
    /// <summary>No answer from the server (network, 5xx, stale clock): the current state, no error.</summary>
    Offline,
    /// <summary>The server asked to wait (<see cref="RefreshResult.RetryAfter"/>).</summary>
    RateLimited,
    /// <summary>Refused for good: the local refresh token was erased and the plan is Free.</summary>
    Revoked,
    /// <summary>
    /// The server did not accept this request (bad.request, or an unknown 4xx code mapped to it): nothing is erased,
    /// the token and the plan stay, no retry in the same attempt; the next scheduled refresh tries again.
    /// </summary>
    Rejected,
}

internal readonly record struct RefreshResult(RefreshOutcome Outcome, int RetryAfter = 0);

/// <summary>Result of a tray operation: ok with data, or a stable error code (never a server text or a secret).</summary>
internal sealed record AccountReply(bool Ok, string? Error, JsonObject? Data)
{
    public static AccountReply Success(JsonObject data) => new(true, null, data);
    public static AccountReply Fail(string error) => new(false, error, null);
}

/// <summary>
/// The service's client of the account server (E4-4): sign-in with a code shown on the PC (pc-login-start, then
/// pc-login-poll), refresh rotation (pc-token) for account.refresh, and local unlink. Every request is signed by the
/// PC key with a fresh nonce and ts; the refresh token lives only in <see cref="RefreshTokenStore"/> (DPAPI
/// LocalMachine) and the deviceCode only in memory. Neither of them, nor the token of rights, is ever logged.
/// One operation at a time.
/// </summary>
internal sealed partial class AccountClient : IDisposable
{
    public const string Disabled = "disabled";
    /// <summary>A rotation whose answer was lost may be repeated with the same old token within this window (server rule).</summary>
    public static readonly TimeSpan IdempotentWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan[] DefaultRetryPauses = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];
    private const int MaxResponseBytes = 64 * 1024;

    private readonly AccountServerConfig _config;
    private readonly HttpClient _http;
    private readonly IDeviceKey _key;
    private readonly Guid _pcId;
    private readonly string _pcName;
    private readonly EntitlementTracker _entitlements;
    private readonly RefreshTokenStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly IReadOnlyList<TimeSpan> _retryPauses;
    private readonly Action<string, bool, string?>? _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoginAttempt? _login;
    private string? _loginResult; // "ok", "expired" or an error code of the last finished attempt

    public AccountClient(AccountServerConfig config, HttpMessageHandler handler, IDeviceKey key, Guid pcId, string pcName,
        EntitlementTracker entitlements, RefreshTokenStore store, TimeProvider time, ILogger log,
        IReadOnlyList<TimeSpan>? retryPauses = null, Action<string, bool, string?>? audit = null)
    {
        _config = config;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxResponseBytes };
        _key = key;
        _pcId = pcId;
        _pcName = SafeName(pcName);
        _entitlements = entitlements;
        _store = store;
        _time = time;
        _log = log;
        _retryPauses = retryPauses ?? DefaultRetryPauses;
        _audit = audit;
    }

    public bool SignedIn => _store.Load() is not null;

    // ---- login.start / login.status ----

    public async Task<AccountReply> StartLoginAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _login = null;
            _loginResult = null;
            var spki = PcLoginStrings.B64u(_key.Spki);
            var reply = await PostSignedAsync("pc-login-start", (nonce, ts) => new JsonObject
            {
                ["pcId"] = PcLoginStrings.Id(_pcId), ["spki"] = spki, ["name"] = _pcName, ["nonce"] = nonce, ["ts"] = ts,
                ["sig"] = Sign(PcLoginStrings.Start(_pcId, spki, _pcName, nonce, ts)),
            }, cancellationToken).ConfigureAwait(false);
            if (reply.Error is { } error)
            {
                Record("account.login.start", false, error);
                if (error == "pc.key_required")
                {
                    Wipe();
                }
                return AccountReply.Fail(error);
            }
            var body = reply.Body!;
            if (Str(body, "deviceCode") is not { Length: >= 22 and <= 256 } deviceCode || Str(body, "userCode") is not { Length: 8 } userCode
                || Int(body, "expiresIn") is not ({ } expiresIn and > 0 and <= 3600) || Int(body, "interval") is not ({ } interval and > 0 and <= 300))
            {
                Record("account.login.start", false, "internal");
                return AccountReply.Fail("internal");
            }
            var now = _time.GetUtcNow();
            _login = new LoginAttempt(deviceCode, userCode, now.AddSeconds(expiresIn), TimeSpan.FromSeconds(interval)) { NextPollAt = now.AddSeconds(interval) };
            Record("account.login.start", true, null);
            return AccountReply.Success(LoginView("pending", _login));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>pending (polls the server when the interval has passed), ok, expired or none; never the deviceCode.</summary>
    public async Task<AccountReply> LoginStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_login is { } login)
            {
                await PollAsync(login, cancellationToken).ConfigureAwait(false);
            }
            if (_login is { } still)
            {
                return AccountReply.Success(LoginView("pending", still));
            }
            var state = _loginResult switch
            {
                null => _store.Load() is not null ? "ok" : "none",
                "ok" or "expired" => _loginResult,
                _ => "failed",
            };
            var view = new JsonObject { ["state"] = state };
            if (state == "failed")
            {
                view["error"] = _loginResult;
            }
            return AccountReply.Success(WithAccount(view));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PollAsync(LoginAttempt login, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now >= login.ExpiresAt)
        {
            Finish("expired");
            return;
        }
        if (now < login.NextPollAt)
        {
            return;
        }
        var reply = await PostSignedAsync("pc-login-poll", (nonce, ts) => new JsonObject
        {
            ["pcId"] = PcLoginStrings.Id(_pcId), ["deviceCode"] = login.DeviceCode, ["nonce"] = nonce, ["ts"] = ts,
            ["sig"] = Sign(PcLoginStrings.Consume(_pcId, login.DeviceCode, nonce, ts)),
        }, cancellationToken).ConfigureAwait(false);
        now = _time.GetUtcNow();
        switch (reply.Error)
        {
            case null:
                if (Str(reply.Body!, "refreshToken") is not { Length: > 0 and <= 256 } refresh || Str(reply.Body!, "entitlement") is not { } token)
                {
                    login.NextPollAt = now + login.Interval;
                    return;
                }
                _store.Save(refresh);
                TakeEntitlement(token, Str(reply.Body!, "entitlementState"));
                Finish("ok");
                return;
            case "pending" or Transient or "rate.limited" or "request.stale" or "nonce.replay":
                login.NextPollAt = now + login.Interval;
                return;
            case "slow_down":
                login.Interval += TimeSpan.FromSeconds(5);
                login.NextPollAt = now + TimeSpan.FromSeconds(Math.Max(reply.RetryAfter ?? 0, (int)login.Interval.TotalSeconds));
                return;
            case "code.invalid":
                Finish("expired");
                return;
            case "pc.key_required":
                Wipe();
                Finish("pc.key_required");
                return;
            default:
                Finish(reply.Error); // pcs.limit, bad.request
                return;
        }
    }

    private void Finish(string result)
    {
        _login = null; // the deviceCode is forgotten
        _loginResult = result;
        Record("account.login", result == "ok", result == "ok" ? null : result);
    }

    // ---- unlink ----

    /// <summary>
    /// Local unlink: erases the refresh token and moves the plan to Free. Releasing the PC on the server (pc-unlink)
    /// needs the owner's account session, so it is done from the app; the server then answers pc-token with revoked.
    /// </summary>
    public async Task<AccountReply> UnlinkAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _login = null;
            _loginResult = null;
            if (_store.Load() is not null)
            {
                Wipe();
            }
            Record("account.unlink", true, null);
            return AccountReply.Success(WithAccount(new JsonObject { ["state"] = "none" }));
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- account.refresh ----

    /// <summary>
    /// pc-token: rotates the refresh token and takes the new token of rights. The new refresh token is saved
    /// (atomically) before anything else. A lost answer is repeated with the same old token and a new nonce, ts and
    /// signature, only within <see cref="IdempotentWindow"/> of the first try; the server answers the same new token.
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.Load() is not { } old)
            {
                return new RefreshResult(RefreshOutcome.NotSignedIn);
            }
            var first = _time.GetUtcNow();
            for (var attempt = 0; ; attempt++)
            {
                var reply = await PostSignedAsync("pc-token", (nonce, ts) => new JsonObject
                {
                    ["pcId"] = PcLoginStrings.Id(_pcId), ["refreshToken"] = old, ["nonce"] = nonce, ["ts"] = ts,
                    ["sig"] = Sign(PcLoginStrings.Refresh(_pcId, old, nonce, ts)),
                }, cancellationToken).ConfigureAwait(false);
                switch (reply.Error)
                {
                    case null:
                        if (Str(reply.Body!, "refreshToken") is not { Length: > 0 and <= 256 } fresh || Str(reply.Body!, "entitlement") is not { } token)
                        {
                            break; // malformed answer: as if lost, repeat with the old token
                        }
                        _store.Save(fresh);
                        TakeEntitlement(token, Str(reply.Body!, "entitlementState"));
                        Record("account.token", true, null);
                        return new RefreshResult(RefreshOutcome.Updated);
                    case "rate.limited":
                        Record("account.token", false, "rate.limited");
                        return new RefreshResult(RefreshOutcome.RateLimited, Math.Clamp(reply.RetryAfter ?? 60, 1, 86_400));
                    case Transient or "request.stale" or "nonce.replay":
                        break;
                    case "bad.request":
                        // Our request (or an unknown 4xx code) was not accepted: this says nothing about the token.
                        Record("account.token", false, "bad.request");
                        return new RefreshResult(RefreshOutcome.Rejected);
                    default:
                        // auth.required, pc.not_found, revoked, reuse.detected, pc.key_required (and any other known
                        // refusal): the token is no longer valid; erase and wait for a new sign-in.
                        Wipe();
                        Record("account.token", false, reply.Error);
                        return new RefreshResult(RefreshOutcome.Revoked);
                }
                if (attempt >= _retryPauses.Count || _time.GetUtcNow() + _retryPauses[attempt] - first >= IdempotentWindow)
                {
                    Record("account.token", false, reply.Error);
                    return new RefreshResult(RefreshOutcome.Offline);
                }
                await Task.Delay(_retryPauses[attempt], _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- common ----

    private void TakeEntitlement(string token, string? state)
    {
        var check = _entitlements.Accept(token);
        if (!check.Ok)
        {
            LogTokenRejected(_log, check.Reason);
        }
        if (state == "revoked")
        {
            _entitlements.MarkRevoked();
        }
    }

    /// <summary>revoked / pc.key_required / unlink: no refresh token on disk, plan Free at once.</summary>
    private void Wipe()
    {
        try
        {
            _store.Delete();
        }
        catch (IOException ex)
        {
            LogDeleteFailed(_log, ex.GetType().Name);
        }
        _entitlements.MarkRevoked();
    }

    private const string Transient = "transient";

    private sealed record Reply(JsonObject? Body, string? Error, int? RetryAfter);

    private async Task<Reply> PostSignedAsync(string function, Func<string, long, JsonObject> build, CancellationToken cancellationToken)
    {
        var body = build(PcLoginStrings.NewNonce(), _time.GetUtcNow().ToUnixTimeSeconds());
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Function(function))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("apikey", _config.AnonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AnonKey);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonObject? json = null;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (json is null)
            {
                LogUnreadable(_log, function, (int)response.StatusCode);
                return new Reply(null, Transient, null);
            }
            if (response.StatusCode == HttpStatusCode.OK && json["error"] is null)
            {
                return new Reply(json, null, null);
            }
            var code = Str(json, "error") is { } e && KnownErrors.Contains(e) ? e : ((int)response.StatusCode >= 500 ? "internal" : "bad.request");
            int? retryAfter = Int(json, "retryAfter") ?? (response.Headers.RetryAfter?.Delta is { } d ? (int)d.TotalSeconds : null);
            if (code != "pending")
            {
                LogServerError(_log, function, code);
            }
            return new Reply(null, code == "internal" ? Transient : code, retryAfter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogNoConnection(_log, function, ex.GetType().Name);
            return new Reply(null, Transient, null);
        }
    }

    private static readonly HashSet<string> KnownErrors = new(StringComparer.Ordinal)
    {
        "bad.request", "auth.required", "reauth.required", "code.invalid", "pending", "slow_down", "rate.limited", "pcs.limit",
        "pc.key_required", "request.stale", "nonce.replay", "revoked", "reuse.detected", "pc.not_found", "internal",
    };

    private string Sign(string text) => PcLoginStrings.B64u(_key.SignP1363(Encoding.ASCII.GetBytes(text)));

    private JsonObject LoginView(string state, LoginAttempt login) => WithAccount(new JsonObject
    {
        ["state"] = state,
        ["userCode"] = login.UserCode,
        ["expiresAt"] = login.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
    });

    /// <summary>The PC never learns the account's email (docs/API.md): "linked" and the plan only.</summary>
    private JsonObject WithAccount(JsonObject view)
    {
        var plan = _entitlements.Current;
        view["linked"] = _store.Load() is not null;
        view["plan"] = plan.Plan;
        view["entitlementState"] = plan.WireState;
        return view;
    }

    private void Record(string action, bool ok, string? error)
    {
        _audit?.Invoke(action, ok, error);
        LogAction(_log, action, ok ? "ok" : error ?? "failed");
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>1..64 characters without control, format or separator characters (server rule); "PC" if nothing is left.</summary>
    internal static string SafeName(string name)
    {
        var builder = new StringBuilder();
        foreach (var rune in (name ?? "").EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate || rune == Rune.ReplacementChar)
            {
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength > 64)
            {
                break;
            }
            builder.Append(rune.ToString());
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 ? "PC" : result;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private sealed class LoginAttempt(string deviceCode, string userCode, DateTimeOffset expiresAt, TimeSpan interval)
    {
        public string DeviceCode { get; } = deviceCode;
        public string UserCode { get; } = userCode;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public TimeSpan Interval { get; set; } = interval;
        public DateTimeOffset NextPollAt { get; set; }
    }

    // Only codes, function names and exception type names: never tokens, codes, bodies or URLs with secrets.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Account server sent a token of rights the agent did not accept ({Reason})")]
    private static partial void LogTokenRejected(ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the account file ({Error})")]
    private static partial void LogDeleteFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account server {Function}: unreadable answer (HTTP {Status})")]
    private static partial void LogUnreadable(ILogger logger, string function, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Account server {Function}: {Error}")]
    private static partial void LogServerError(ILogger logger, string function, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account server {Function}: no connection ({Error})")]
    private static partial void LogNoConnection(ILogger logger, string function, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Account {Action}: {Result}")]
    private static partial void LogAction(ILogger logger, string action, string result);
}
