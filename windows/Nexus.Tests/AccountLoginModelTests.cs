using System.Text.Json.Nodes;
using Nexus.Core.Account;
using Nexus.Core.Session;
using Xunit;

namespace Nexus.Tests;

public sealed class AccountLoginModelTests : IDisposable
{
    private const string Code = "ABCDEFGH";
    private readonly Clock _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-login-" + Guid.NewGuid().ToString("N"));
    private readonly LoginMarkerFile _marker;

    public AccountLoginModelTests() => _marker = new LoginMarkerFile(Path.Combine(_dir, "login-pending.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private AccountLoginModel NewModel() => new(_marker, _time);

    private RpcResult Pending(bool linked = false) => RpcResult.Success(new JsonObject
    {
        ["state"] = "pending", ["userCode"] = Code, ["expiresAt"] = _time.GetUtcNow().AddMinutes(5).ToString("O"),
        ["linked"] = linked, ["plan"] = "free", ["entitlementState"] = "none",
    });

    private static RpcResult Status(string state, bool linked, string plan = "free", string? error = null)
    {
        var o = new JsonObject { ["state"] = state, ["linked"] = linked, ["plan"] = plan, ["entitlementState"] = linked ? "active" : "none" };
        if (error is not null)
        {
            o["error"] = error;
        }
        return RpcResult.Success(o);
    }

    private static RpcResult AdminFailed(string code) => RpcResult.Failure("admin.failed", code);

    private static RpcResult NoService() => RpcResult.Failure("session.unavailable", "no service");

    [Fact]
    public void StartShowsCodeInGroupsOfFourWithCountdownAndWritesMarkerWithoutCodes()
    {
        var model = NewModel();
        model.BeginStart();
        Assert.Equal(AccountPhase.Starting, model.Phase);
        model.OnStartReply(Pending());

        Assert.Equal(AccountPhase.Pending, model.Phase);
        Assert.True(model.ShouldPoll);
        Assert.Equal("ABCD-EFGH", model.UserCodeDisplay);
        Assert.Equal("5:00", model.RemainingText);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("3:59", model.RemainingText);

        var text = File.ReadAllText(_marker.FilePath);
        Assert.DoesNotContain(Code, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ABCD", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceCode", text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(JsonNode.Parse(text)!.AsObject());
        Assert.NotNull(_marker.Read());
        Assert.DoesNotContain(Code, model.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PollOkShowsLinkedAccountPlanAndSignedInNotice()
    {
        var model = NewModel();
        model.OnStartReply(Pending());
        Assert.Equal(LoginNotice.None, model.OnStatusReply(Pending()));
        Assert.Equal(LoginNotice.SignedIn, model.OnStatusReply(Status("ok", true, "pro")));
        Assert.Equal(AccountPhase.SignedIn, model.Phase);
        Assert.Equal("pro", model.Plan);
        Assert.Null(model.UserCode);
        Assert.False(model.ShouldPoll);
        Assert.Null(_marker.Read());
    }

    [Fact]
    public void UnlinkReturnsToSignedOut()
    {
        var model = NewModel();
        model.OnStatusReply(Status("ok", true, "pro"));
        Assert.Equal(AccountPhase.SignedIn, model.Phase);
        model.OnUnlinkReply(Status("none", false));
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.False(model.Linked);
    }

    [Theory]
    [InlineData("rate.limited", AccountProblem.RateLimited, AccountPhase.SignedOut)]
    [InlineData("pcs.limit", AccountProblem.PcsLimit, AccountPhase.SignedOut)]
    [InlineData("transient", AccountProblem.Network, AccountPhase.SignedOut)]
    [InlineData("disabled", AccountProblem.NotConfigured, AccountPhase.Unavailable)]
    [InlineData("keyUnavailable", AccountProblem.KeyUnavailable, AccountPhase.Unavailable)]
    [InlineData("bad.request", AccountProblem.Failed, AccountPhase.SignedOut)]
    public void StartErrorsAreMapped(string code, AccountProblem problem, AccountPhase phase)
    {
        var model = NewModel();
        model.BeginStart();
        model.OnStartReply(AdminFailed(code));
        Assert.Equal(problem, model.Problem);
        Assert.Equal(phase, model.Phase);
        Assert.False(model.ShouldPoll);
        Assert.Null(_marker.Read());
    }

    [Fact]
    public void NoServiceIsUnavailable()
    {
        var model = NewModel();
        model.OnStartReply(NoService());
        Assert.Equal(AccountPhase.Unavailable, model.Phase);
        Assert.Equal(AccountProblem.ServiceUnavailable, model.Problem);
    }

    [Fact]
    public void FailedPcsLimitWhilePollingIsShown()
    {
        var model = NewModel();
        model.OnStartReply(Pending());
        Assert.Equal(LoginNotice.RetryNeeded, model.OnStatusReply(Status("failed", false, error: "pcs.limit")));
        Assert.Equal(AccountProblem.PcsLimit, model.Problem);
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.Null(_marker.Read());
    }

    [Fact]
    public void TransientPollErrorKeepsCodeAndPolling()
    {
        var model = NewModel();
        model.OnStartReply(Pending());
        model.OnStatusReply(NoService());
        model.OnStatusReply(AdminFailed("transient"));
        Assert.Equal(AccountPhase.Pending, model.Phase);
        Assert.Equal("ABCD-EFGH", model.UserCodeDisplay);
    }

    [Fact]
    public void TransientErrorAfterCodeExpiryStopsPollingAndClearsMarker()
    {
        var model = NewModel();
        model.BeginStart();
        model.OnStartReply(Pending());
        Assert.True(model.ShouldPoll);
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        model.OnStatusReply(NoService());
        Assert.False(model.ShouldPoll);
        Assert.Null(model.UserCode);
        Assert.Null(_marker.Read());
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("rate.limited")]
    public void AdminTransientErrorAfterCodeExpiryStopsPolling(string code)
    {
        var model = NewModel();
        model.OnStartReply(Pending());
        _time.Advance(TimeSpan.FromMinutes(5));
        model.OnStatusReply(AdminFailed(code));
        Assert.False(model.ShouldPoll);
        Assert.Equal(AccountProblem.Expired, model.Problem);
        Assert.Null(_marker.Read());
    }

    [Fact]
    public void TransientErrorBeforeExpiryKeepsPolling()
    {
        var model = NewModel();
        model.BeginStart();
        model.OnStartReply(Pending());
        _time.Advance(TimeSpan.FromMinutes(2));
        model.OnStatusReply(AdminFailed("transient"));
        Assert.True(model.ShouldPoll);
        Assert.Equal("ABCD-EFGH", model.UserCodeDisplay);
        Assert.NotNull(_marker.Read());
    }

    [Fact]
    public void CountdownEndExpiresAndClearsMarker()
    {
        var model = NewModel();
        model.OnStartReply(Pending());
        _time.Advance(TimeSpan.FromMinutes(5));
        model.Tick();
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.Equal(AccountProblem.Expired, model.Problem);
        Assert.Null(model.UserCode);
        Assert.Null(_marker.Read());
        model.OnStatusReply(Status("expired", false)); // the late answer keeps the explanation
        Assert.Equal(AccountProblem.Expired, model.Problem);
    }

    [Fact]
    public void CancelClearsCodeAndMarker()
    {
        var model = NewModel();
        model.OnStartReply(Pending());
        model.Cancel();
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.Null(model.UserCode);
        Assert.False(model.ShouldPoll);
        Assert.Null(_marker.Read());
    }

    // ---- tray start after the tray closed during sign-in (owner decision 2026-11-24) ----

    [Fact]
    public void StartupPendingResumesPolling()
    {
        NewModel().OnStartReply(Pending()); // previous tray run
        var model = NewModel();
        Assert.Equal(LoginNotice.Resuming, model.OnStatusReply(Pending(), atStartup: true));
        Assert.True(model.ShouldPoll);
        Assert.Equal(LoginNotice.SignedIn, model.OnStatusReply(Status("ok", true)));
    }

    [Fact]
    public void StartupPendingWithoutMarkerStillResumesAndWritesMarker()
    {
        var model = NewModel();
        Assert.Equal(LoginNotice.Resuming, model.OnStatusReply(Pending(), atStartup: true));
        Assert.NotNull(_marker.Read());
    }

    [Fact]
    public void StartupOkWithMarkerNotifiesSignedIn()
    {
        _marker.Write(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(1));
        var model = NewModel();
        Assert.Equal(LoginNotice.SignedIn, model.OnStatusReply(Status("ok", true), atStartup: true));
        Assert.Null(_marker.Read());
        Assert.Equal(LoginNotice.None, NewModel().OnStatusReply(Status("ok", true), atStartup: true)); // only once
    }

    [Fact]
    public void StartupOkWithoutMarkerIsSilent()
    {
        var model = NewModel();
        Assert.Equal(LoginNotice.None, model.OnStatusReply(Status("ok", true), atStartup: true));
        Assert.Equal(AccountPhase.SignedIn, model.Phase);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("failed")]
    [InlineData("none")] // the service restarted and lost the attempt
    public void StartupEndedAttemptWithMarkerAsksToRetry(string state)
    {
        _marker.Write(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(2));
        var model = NewModel();
        Assert.Equal(LoginNotice.RetryNeeded, model.OnStatusReply(Status(state, false, error: state == "failed" ? "bad.request" : null), atStartup: true));
        Assert.Null(_marker.Read());
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("failed")]
    [InlineData("none")]
    public void StartupEndedAttemptWithoutMarkerIsSilent(string state)
    {
        var model = NewModel();
        Assert.Equal(LoginNotice.None, model.OnStatusReply(Status(state, false), atStartup: true));
        Assert.Equal(AccountProblem.None, model.Problem);
    }

    [Fact]
    public void MarkerOlderThanFiveMinutesIsErasedAndSilent()
    {
        _marker.Write(_time.GetUtcNow());
        _time.Advance(AccountLoginModel.MarkerLifetime + TimeSpan.FromSeconds(1));
        var model = NewModel();
        Assert.Equal(LoginNotice.None, model.OnStatusReply(Status("none", false), atStartup: true));
        Assert.Null(_marker.Read());
    }

    [Theory]
    [InlineData("session.unavailable", "x")]
    [InlineData("admin.failed", "disabled")]
    [InlineData("admin.failed", "keyUnavailable")]
    [InlineData("session.timeout", "x")]
    public void StartupWithoutServerShowsNothing(string code, string message)
    {
        _marker.Write(_time.GetUtcNow());
        var model = NewModel();
        Assert.Equal(LoginNotice.None, model.OnStatusReply(RpcResult.Failure(code, message), atStartup: true));
        Assert.Equal(AccountPhase.Unknown, model.Phase);
        Assert.Equal(AccountProblem.None, model.Problem);
    }

    [Fact]
    public void BrokenMarkerFileIsIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_marker.FilePath, "{not json");
        Assert.Null(_marker.Read());
        Assert.Equal(LoginNotice.None, NewModel().OnStatusReply(Status("none", false), atStartup: true));
    }

    [Fact]
    public void FormatUserCode()
    {
        Assert.Equal("ABCD-EFGH", AccountLoginModel.FormatUserCode("ABCDEFGH"));
        Assert.Null(AccountLoginModel.FormatUserCode(null));
    }
}
