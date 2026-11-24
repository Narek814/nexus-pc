using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nexus.Agent.Account;
using Nexus.Agent.Admin;
using Nexus.Agent.Security;
using Nexus.Core.Entitlements;
using Nexus.Core.Protocol;

namespace Nexus.Tests;

/// <summary>Collects every formatted log line (to prove secrets never reach the log).</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}

/// <summary>Test sealer: reversible XOR so the file on disk is not the plain token (DPAPI itself is Windows only).</summary>
internal sealed class XorProtector : IRefreshProtector
{
    public byte[] Protect(byte[] plain) => [.. plain.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] sealedBytes) => Protect(sealedBytes);
}

/// <summary>
/// Fake account server (Edge Functions pc-login-start/poll, pc-token): checks headers, the signed string and the
/// P1363 signature with the PC key, fresh nonce per request; rotation is derive(old) and idempotent.
/// </summary>
internal sealed class FakeAccountServer : HttpMessageHandler
{
    private readonly Func<long, string> _issue;
    private readonly HashSet<string> _nonces = new(StringComparer.Ordinal);
    private byte[]? _spki;

    public FakeAccountServer(Func<long, string> issue) => _issue = issue;

    public const string AnonKey = "anon-test-key";
    public string DeviceCode { get; } = B64u(RandomNumberGenerator.GetBytes(32));
    public string UserCode { get; } = "ABCD2345";
    public List<(string Function, JsonObject Body)> Requests { get; } = [];
    /// <summary>Polls answered pending before ok.</summary>
    public int PendingPolls { get; set; } = 1;
    /// <summary>Refresh tokens the server still accepts: old → new.</summary>
    public Dictionary<string, string> Rotations { get; } = new(StringComparer.Ordinal);
    public string? CurrentRefresh { get; private set; }
    /// <summary>Next pc-token answers are lost this many times (processed, then the connection drops).</summary>
    public int LoseTokenAnswers { get; set; }
    public string? TokenError { get; set; }
    /// <summary>HTTP status for <see cref="TokenError"/> (default: 429 for rate.limited, else 401).</summary>
    public HttpStatusCode? TokenStatus { get; set; }
    public string? PollError { get; set; }
    public int TokenRetryAfter { get; set; } = 3600;
    public string EntitlementState { get; set; } = "active";
    public List<string> Issued { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(AnonKey, request.Headers.GetValues("apikey").Single());
        Assert.Equal("Bearer " + AnonKey, request.Headers.Authorization!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        var function = request.RequestUri!.AbsolutePath.Split('/')[^1];
        Assert.StartsWith("/functions/v1/", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
        var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (Requests)
        {
            Requests.Add((function, body));
        }
        var nonce = body["nonce"]!.GetValue<string>();
        Assert.InRange(FromB64u(nonce).Length, 16, 32);
        Assert.True(_nonces.Add(nonce), "nonce reused");
        var pcId = Guid.ParseExact(body["pcId"]!.GetValue<string>(), "D");
        Assert.Equal(body["pcId"]!.GetValue<string>(), body["pcId"]!.GetValue<string>().ToLowerInvariant());
        var ts = body["ts"]!.GetValue<long>();
        string signed;
        switch (function)
        {
            case "pc-login-start":
                Assert.Equal(["pcId", "spki", "name", "nonce", "ts", "sig"], body.Select(p => p.Key));
                _spki = FromB64u(body["spki"]!.GetValue<string>());
                Assert.Equal(pcId, Nexus.Core.Security.PcIdDerivation.FromSpki(_spki));
                signed = PcLoginStrings.Start(pcId, body["spki"]!.GetValue<string>(), body["name"]!.GetValue<string>(), nonce, ts);
                break;
            case "pc-login-poll":
                Assert.Equal(["pcId", "deviceCode", "nonce", "ts", "sig"], body.Select(p => p.Key));
                signed = PcLoginStrings.Consume(pcId, body["deviceCode"]!.GetValue<string>(), nonce, ts);
                break;
            case "pc-token":
                Assert.Equal(["pcId", "refreshToken", "nonce", "ts", "sig"], body.Select(p => p.Key));
                signed = PcLoginStrings.Refresh(pcId, body["refreshToken"]!.GetValue<string>(), nonce, ts);
                break;
            default:
                return Answer(HttpStatusCode.NotFound, new JsonObject { ["error"] = "bad.request" });
        }
        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(_spki!, out _);
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(signed), FromB64u(body["sig"]!.GetValue<string>()),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "bad signature for " + function);
        }
        switch (function)
        {
            case "pc-login-start":
                return Answer(HttpStatusCode.OK, new JsonObject { ["deviceCode"] = DeviceCode, ["userCode"] = UserCode, ["expiresIn"] = 300, ["interval"] = 5 });
            case "pc-login-poll":
                Assert.Equal(DeviceCode, body["deviceCode"]!.GetValue<string>());
                if (PollError is { } pollError)
                {
                    var refusal = new JsonObject { ["error"] = pollError };
                    if (pollError is "slow_down" or "rate.limited")
                    {
                        refusal["retryAfter"] = 10;
                    }
                    return Answer(HttpStatusCode.TooManyRequests, refusal);
                }
                if (PendingPolls-- > 0)
                {
                    return Answer(HttpStatusCode.Accepted, new JsonObject { ["error"] = "pending" });
                }
                CurrentRefresh = B64u(RandomNumberGenerator.GetBytes(32));
                return Answer(HttpStatusCode.OK, Grant(CurrentRefresh));
            default:
                if (TokenError is { } error)
                {
                    var refused = new JsonObject { ["error"] = error };
                    if (error == "rate.limited")
                    {
                        refused["retryAfter"] = TokenRetryAfter;
                    }
                    return Answer(TokenStatus ?? (error == "rate.limited" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized), refused);
                }
                var old = body["refreshToken"]!.GetValue<string>();
                if (!Rotations.TryGetValue(old, out var fresh))
                {
                    if (old != CurrentRefresh)
                    {
                        return Answer(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "reuse.detected" });
                    }
                    fresh = B64u(SHA256.HashData(Encoding.ASCII.GetBytes("derive|" + old)));
                    Rotations[old] = fresh; // a repeat with the same old token gets the same new one
                    CurrentRefresh = fresh;
                }
                if (LoseTokenAnswers > 0)
                {
                    LoseTokenAnswers--;
                    throw new HttpRequestException("connection reset");
                }
                return Answer(HttpStatusCode.OK, Grant(fresh));
        }
    }

    private JsonObject Grant(string refresh)
    {
        var token = _issue(Issued.Count);
        Issued.Add(token);
        return new JsonObject { ["refreshToken"] = refresh, ["entitlement"] = token, ["entitlementState"] = EntitlementState };
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, JsonObject body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        return response;
    }

    internal static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromB64u(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }
}

/// <summary>E4-4: the service's client of the account server, with a fake HTTP server.</summary>
public sealed class AccountClientTests : IDisposable
{
    private static readonly AccountServerConfig Server = AccountServerConfig.ForTests(new Uri("https://accounts.test/"), FakeAccountServer.AnonKey);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-account-" + Guid.NewGuid().ToString("N"));
    private readonly System.Text.Json.JsonDocument _doc = EntitlementVerifierTests.Fixture();
    private readonly FakeEntitlementClock _clock;
    private readonly FixedTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly TestKey _key;
    private readonly Guid _pcId;
    private readonly EntitlementTracker _tracker;
    private readonly CapturingLogger _log = new();
    private readonly List<string> _audit = [];
    private readonly RefreshTokenStore _store;

    public AccountClientTests()
    {
        Directory.CreateDirectory(_dir);
        _clock = new FakeEntitlementClock(_time.Now.ToUnixTimeSeconds());
        _key = new TestKey(_ecdsa);
        _pcId = Nexus.Core.Security.PcIdDerivation.FromSpki(_key.Spki);
        _tracker = new EntitlementTracker(new EntitlementVerifier(EntitlementVerifierTests.TestTrust(_doc)), _pcId, new MemoryEntitlementStore(), _clock);
        _store = new RefreshTokenStore(_dir, new XorProtector());
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
        _doc.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class TestKey(ECDsa ecdsa) : IDeviceKey
    {
        public byte[] Spki { get; } = ecdsa.ExportSubjectPublicKeyInfo();
        public string Provider => "test";
        public bool Created => false;
        public byte[] SignP1363(ReadOnlySpan<byte> data) => ecdsa.SignData(data.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        public void Dispose()
        {
        }
    }

    private string Issue(long n, string plan = "pro")
    {
        var iat = _clock.Wall + n;
        return EntitlementVerifierTests.Sign(_doc, plan == "pro"
            ? EntitlementVerifierTests.Payload(_pcId, iat, iat + 86_400, iat + 86_400 + 604_800)
            : EntitlementVerifierTests.Payload(_pcId, iat, iat + 86_400, iat + 86_400, plan: "free", features: ""));
    }

    private (AccountClient Client, FakeAccountServer Fake) Create(IReadOnlyList<TimeSpan>? pauses = null)
    {
        var fake = new FakeAccountServer(n => Issue(n));
        var client = new AccountClient(Server, fake, _key, _pcId, "Мой\u0007 ПК\u200B", _tracker, _store, _time, _log,
            pauses ?? [TimeSpan.Zero, TimeSpan.Zero], (action, ok, error) => _audit.Add($"{action} {ok} {error}"));
        return (client, fake);
    }

    private async Task<(AccountClient Client, FakeAccountServer Fake)> SignedInAsync()
    {
        var (client, fake) = Create();
        Assert.True((await client.StartLoginAsync(CancellationToken.None)).Ok);
        _time.Now = _time.Now.AddSeconds(5);
        Assert.Equal("pending", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        _time.Now = _time.Now.AddSeconds(5);
        Assert.Equal("ok", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        return (client, fake);
    }

    [Fact]
    public void Signed_strings_match_the_backend_vectors()
    {
        // backend/tests/ts/pop.test.ts, "signed strings are exactly as in docs/BACKEND.md"
        var pc = Guid.Parse("86a77e46-afba-4ab0-8f08-68ff922e9cc2");
        Assert.Equal("nexus-pc-login-v1|start|86a77e46-afba-4ab0-8f08-68ff922e9cc2|SPKI|8c2c82aa1ea194b04c0f785e6bc6bdb54a0ff4744e46bba95b74b4db6303a15e|NONCE|1795348800",
            PcLoginStrings.Start(pc, "SPKI", "Мой ПК", "NONCE", 1795348800));
        Assert.Equal("nexus-pc-login-v1|consume|86a77e46-afba-4ab0-8f08-68ff922e9cc2|ecdba0e2f6615760b196edd49a2f1bf0d0d6dfccbf0ad8fe9ee55722dcca42a4|N|5",
            PcLoginStrings.Consume(pc, "DC", "N", 5));
        Assert.Equal("nexus-pc-login-v1|refresh|86a77e46-afba-4ab0-8f08-68ff922e9cc2|3165be96f5833ae07a93e8baa073df6bae2dc719814af10288f844d637d8721a|N|5",
            PcLoginStrings.Refresh(pc, "RT", "N", 5));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PcLoginStrings.Sha256Hex("abc"));
        Assert.Equal(PcLoginStrings.Id(Guid.Parse("86A77E46-AFBA-4AB0-8F08-68FF922E9CC2")), PcLoginStrings.Id(pc));
    }

    [Fact]
    public void Nonce_is_fresh_b64u_of_16_to_32_bytes()
    {
        var a = PcLoginStrings.NewNonce();
        var b = PcLoginStrings.NewNonce();
        Assert.NotEqual(a, b);
        Assert.InRange(FakeAccountServer.FromB64u(a).Length, 16, 32);
        Assert.DoesNotContain('=', a);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }

    [Fact]
    public void Pc_name_is_cleaned_for_the_server()
    {
        Assert.Equal("Мой ПК", AccountClient.SafeName("Мой\u0007 ПК\u200B"));
        Assert.Equal("PC", AccountClient.SafeName("\u0001\u2028"));
        Assert.Equal(64, AccountClient.SafeName(new string('x', 100)).Length);
    }

    [Fact]
    public void Server_config_is_off_without_url_or_key_and_https_only()
    {
        Assert.Null(AccountServerConfig.FromBuild()); // no domain yet: the build carries nothing
        Assert.Null(AccountServerConfig.TryCreate(null, "k"));
        Assert.Null(AccountServerConfig.TryCreate("https://x.supabase.co", ""));
        Assert.Null(AccountServerConfig.TryCreate("http://x.supabase.co", "k"));
        Assert.Null(AccountServerConfig.TryCreate("https://u:p@x.supabase.co", "k"));
        Assert.Null(AccountServerConfig.TryCreate("https://x.supabase.co/?a=1", "k"));
        Assert.Equal("https://x.supabase.co/functions/v1/pc-token", AccountServerConfig.TryCreate("https://x.supabase.co", "k")!.Function("pc-token").ToString());
    }

    [Fact]
    public async Task Full_cycle_start_poll_token_refresh_unlink()
    {
        var (client, fake) = Create();
        var started = await client.StartLoginAsync(CancellationToken.None);
        Assert.True(started.Ok);
        Assert.Equal("ABCD2345", started.Data!["userCode"]!.GetValue<string>());
        Assert.Null(started.Data["deviceCode"]);
        Assert.Equal("Мой ПК", fake.Requests[0].Body["name"]!.GetValue<string>());

        // Before the interval: no request; then 202 pending; then ok.
        Assert.Equal("pending", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        Assert.Single(fake.Requests);
        _time.Now = _time.Now.AddSeconds(5);
        Assert.Equal("pending", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        _time.Now = _time.Now.AddSeconds(5);
        var done = (await client.LoginStatusAsync(CancellationToken.None)).Data!;
        Assert.Equal("ok", done["state"]!.GetValue<string>());
        Assert.True(done["linked"]!.GetValue<bool>());
        Assert.Equal("pro", done["plan"]!.GetValue<string>());
        Assert.Null(done["email"]);
        Assert.Equal(EntitlementState.Active, _tracker.Current.State);
        Assert.Equal(fake.CurrentRefresh, _store.Load());
        Assert.DoesNotContain(fake.CurrentRefresh!, File.ReadAllText(_store.FilePath)); // sealed on disk

        var first = fake.CurrentRefresh!;
        Assert.Equal(RefreshOutcome.Updated, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.NotEqual(first, _store.Load());
        Assert.Equal(fake.CurrentRefresh, _store.Load());
        Assert.Equal(first, fake.Requests[^1].Body["refreshToken"]!.GetValue<string>());
        Assert.Equal(RefreshOutcome.Updated, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(fake.CurrentRefresh, _store.Load());

        var unlinked = await client.UnlinkAsync(CancellationToken.None);
        Assert.True(unlinked.Ok);
        Assert.False(unlinked.Data!["linked"]!.GetValue<bool>());
        Assert.Null(_store.Load());
        Assert.False(File.Exists(_store.FilePath));
        Assert.Equal("free", _tracker.Current.Plan);
        var requests = fake.Requests.Count;
        Assert.Equal(RefreshOutcome.NotSignedIn, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(requests, fake.Requests.Count);
        Assert.Equal("none", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        AssertNoSecrets(fake);
    }

    [Fact]
    public async Task Lost_answer_is_repeated_with_the_same_old_token_and_a_new_nonce()
    {
        var (client, fake) = await SignedInAsync();
        var old = _store.Load()!;
        fake.LoseTokenAnswers = 1;
        Assert.Equal(RefreshOutcome.Updated, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        var tokenRequests = fake.Requests.Where(r => r.Function == "pc-token").ToList();
        Assert.Equal(2, tokenRequests.Count);
        Assert.All(tokenRequests, r => Assert.Equal(old, r.Body["refreshToken"]!.GetValue<string>()));
        Assert.NotEqual(tokenRequests[0].Body["nonce"]!.GetValue<string>(), tokenRequests[1].Body["nonce"]!.GetValue<string>());
        Assert.NotEqual(tokenRequests[0].Body["sig"]!.GetValue<string>(), tokenRequests[1].Body["sig"]!.GetValue<string>());
        Assert.Equal(fake.Rotations[old], _store.Load());
    }

    [Fact]
    public async Task All_answers_lost_keeps_the_old_token_and_the_plan()
    {
        var (client, fake) = await SignedInAsync();
        var old = _store.Load()!;
        fake.LoseTokenAnswers = 10;
        Assert.Equal(RefreshOutcome.Offline, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(3, fake.Requests.Count(r => r.Function == "pc-token"));
        Assert.Equal(old, _store.Load());
        Assert.Equal(EntitlementState.Active, _tracker.Current.State);
        // Next time (the server rotated already): the same old token gives the same new one.
        fake.LoseTokenAnswers = 0;
        Assert.Equal(RefreshOutcome.Updated, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(fake.Rotations[old], _store.Load());
    }

    [Fact]
    public async Task Retries_stop_at_the_60_second_window()
    {
        var (client, fake) = await SignedInAsync();
        var slow = new AccountClient(Server, fake, _key, _pcId, "PC", _tracker, _store, _time, _log, [TimeSpan.FromSeconds(61)]);
        fake.LoseTokenAnswers = 10;
        Assert.Equal(RefreshOutcome.Offline, (await slow.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(1, fake.Requests.Count(r => r.Function == "pc-token"));
        client.Dispose();
    }

    [Theory]
    [InlineData("auth.required")]
    [InlineData("pc.not_found")]
    [InlineData("revoked")]
    [InlineData("reuse.detected")]
    [InlineData("pc.key_required")]
    public async Task Refusal_erases_the_refresh_token_and_goes_free(string error)
    {
        var (client, fake) = await SignedInAsync();
        Assert.True(_tracker.Current.Has(PlanPolicy.RgbSync));
        fake.TokenError = error;
        Assert.Equal(RefreshOutcome.Revoked, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Null(_store.Load());
        Assert.Equal("free", _tracker.Current.Plan);
        Assert.Equal(EntitlementState.Revoked, _tracker.Current.State);
        Assert.Contains($"account.token False {error}", _audit);
    }

    [Fact]
    public async Task Login_status_is_not_ok_after_the_token_was_revoked()
    {
        var (client, fake) = await SignedInAsync();
        fake.TokenError = "revoked";
        Assert.Equal(RefreshOutcome.Revoked, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        var data = (await client.LoginStatusAsync(CancellationToken.None)).Data!;
        Assert.NotEqual("ok", data["state"]!.GetValue<string>());
        Assert.False(data["linked"]!.GetValue<bool>());
        Assert.Equal("free", _tracker.Current.Plan);
        Assert.Equal(EntitlementState.Revoked, _tracker.Current.State);
    }

    [Fact]
    public async Task Unpolled_login_never_changes_the_plan()
    {
        var (client, fake) = Create();
        var plan = _tracker.Current.Plan;
        var state = _tracker.Current.State;
        Assert.True((await client.StartLoginAsync(CancellationToken.None)).Ok);
        _time.Now = _time.Now.AddHours(2);
        Assert.Equal("expired", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        Assert.Null(_store.Load());
        Assert.DoesNotContain(fake.Requests, r => r.Function == "pc-login-poll");
        Assert.Equal(plan, _tracker.Current.Plan);
        Assert.Equal(state, _tracker.Current.State);
    }

    [Theory]
    [InlineData("bad.request", 400)]
    [InlineData("some.future_code", 400)]
    [InlineData("some.future_code", 403)]
    public async Task Bad_request_keeps_the_token_and_the_plan_without_retries(string error, int status)
    {
        var (client, fake) = await SignedInAsync();
        var old = _store.Load();
        fake.TokenError = error;
        fake.TokenStatus = (HttpStatusCode)status;
        Assert.Equal(RefreshOutcome.Rejected, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(old, _store.Load());
        Assert.Equal(EntitlementState.Active, _tracker.Current.State);
        Assert.True(_tracker.Current.Has(PlanPolicy.RgbSync));
        Assert.Equal(1, fake.Requests.Count(r => r.Function == "pc-token"));
        Assert.Contains("account.token False bad.request", _audit);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("some.future_code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_5xx_error_is_logged_only_as_internal()
    {
        var (client, fake) = await SignedInAsync();
        var old = _store.Load();
        fake.TokenError = "db.secret_detail_xyz";
        fake.TokenStatus = HttpStatusCode.InternalServerError;
        lock (_log.Lines)
        {
            _log.Lines.Clear();
        }
        Assert.Equal(RefreshOutcome.Offline, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(old, _store.Load());
        Assert.Contains(_log.Lines, l => l.Contains("internal", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines.Concat(_audit), l => l.Contains("db.secret_detail_xyz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revoked_entitlement_state_goes_free_without_grace_but_keeps_the_token()
    {
        var (client, fake) = await SignedInAsync();
        fake.EntitlementState = "revoked";
        Assert.Equal(RefreshOutcome.Updated, (await client.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(EntitlementState.Revoked, _tracker.Current.State);
        Assert.Equal(fake.CurrentRefresh, _store.Load());
    }

    [Fact]
    public async Task Rate_limited_keeps_everything_and_passes_retry_after()
    {
        var (client, fake) = await SignedInAsync();
        var old = _store.Load();
        fake.TokenError = "rate.limited";
        fake.TokenRetryAfter = 120;
        var result = await client.RefreshAsync(CancellationToken.None);
        Assert.Equal(RefreshOutcome.RateLimited, result.Outcome);
        Assert.Equal(120, result.RetryAfter);
        Assert.Equal(old, _store.Load());
        Assert.Equal(EntitlementState.Active, _tracker.Current.State);
        Assert.Equal(1, fake.Requests.Count(r => r.Function == "pc-token"));
    }

    [Fact]
    public async Task Expired_code_is_expired_and_erases_nothing()
    {
        var (client, fake) = Create();
        _store.Save("old-refresh");
        Assert.True((await client.StartLoginAsync(CancellationToken.None)).Ok);
        _time.Now = _time.Now.AddMinutes(6);
        Assert.Equal("expired", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        Assert.Equal("old-refresh", _store.Load());
        Assert.DoesNotContain(fake.Requests, r => r.Function == "pc-login-poll"); // deviceCode forgotten, never sent again
        Assert.Equal("expired", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("code.invalid", "expired", false)]
    [InlineData("pc.key_required", "failed", true)]
    [InlineData("pcs.limit", "failed", false)]
    public async Task Poll_refusals(string error, string state, bool erases)
    {
        var (client, fake) = Create();
        _store.Save("old-refresh");
        fake.PollError = error;
        Assert.True((await client.StartLoginAsync(CancellationToken.None)).Ok);
        _time.Now = _time.Now.AddSeconds(5);
        var status = (await client.LoginStatusAsync(CancellationToken.None)).Data!;
        Assert.Equal(state, status["state"]!.GetValue<string>());
        Assert.Equal(erases ? null : "old-refresh", _store.Load());
        if (erases)
        {
            Assert.Equal("free", _tracker.Current.Plan);
        }
    }

    [Fact]
    public async Task Slow_down_waits_longer_and_rate_limited_poll_stays_pending()
    {
        var (client, fake) = Create();
        fake.PollError = "slow_down";
        Assert.True((await client.StartLoginAsync(CancellationToken.None)).Ok);
        _time.Now = _time.Now.AddSeconds(5);
        Assert.Equal("pending", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        var polls = fake.Requests.Count(r => r.Function == "pc-login-poll");
        _time.Now = _time.Now.AddSeconds(5);
        Assert.Equal("pending", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        Assert.Equal(polls, fake.Requests.Count(r => r.Function == "pc-login-poll")); // interval grew to 10 s
        fake.PollError = "rate.limited";
        _time.Now = _time.Now.AddSeconds(6);
        Assert.Equal("pending", (await client.LoginStatusAsync(CancellationToken.None)).Data!["state"]!.GetValue<string>());
        Assert.Equal(polls + 1, fake.Requests.Count(r => r.Function == "pc-login-poll"));
    }

    [Fact]
    public void Refresh_file_is_replaced_atomically_and_unreadable_means_signed_out()
    {
        _store.Save("one");
        _store.Save("two");
        Assert.Equal("two", _store.Load());
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
        File.WriteAllText(_store.FilePath + ".tmp", "half");
        Assert.Equal("two", _store.Load()); // a left-over temp file is discarded
        File.WriteAllBytes(_store.FilePath, [1, 2, 3]);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task No_connection_is_offline_not_an_error()
    {
        var (client, fake) = await SignedInAsync();
        var old = _store.Load();
        var down = new AccountClient(Server, new ThrowingHandler(), _key, _pcId, "PC", _tracker, _store, _time, _log, []);
        Assert.Equal(RefreshOutcome.Offline, (await down.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(old, _store.Load());
        Assert.False((await down.StartLoginAsync(CancellationToken.None)).Ok);
        client.Dispose();
        _ = fake;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route " + request.RequestUri);
    }

    private void AssertNoSecrets(FakeAccountServer fake)
    {
        var secrets = new List<string> { fake.DeviceCode, FakeAccountServer.AnonKey };
        secrets.AddRange(fake.Rotations.Keys);
        secrets.AddRange(fake.Rotations.Values);
        secrets.AddRange(fake.Issued);
        Assert.NotEmpty(_log.Lines);
        foreach (var line in _log.Lines.Concat(_audit))
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            }
        }
    }
}

/// <summary>E4-4 through the real agent: tray operations, account.refresh, disabled and key-unavailable modes.</summary>
public sealed class AccountAgentTests
{
    [Fact]
    public async Task Without_the_pc_key_tray_login_answers_key_unavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-account-deg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
            var local = new Nexus.Agent.Server.LocalServices(new InMemoryDeviceStore(), new FakePowerController(), new Nexus.Agent.Storage.AuditLog(dir, time), time);
            var admin = new AdminService(local, new DeviceKeyUnavailable("CryptographicException/0x80090016", 4, time.GetUtcNow()), () => { });
            foreach (var op in new[] { "login.start", "login.status", "unlink" })
            {
                var reply = await admin.HandleAsync(new JsonObject { ["op"] = op }, new AdminConnectionState(), CancellationToken.None);
                Assert.Equal(AdminService.KeyUnavailable, reply["error"]!.GetValue<string>());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Without_server_config_tray_login_is_disabled_and_refresh_answers_local_state()
    {
        using var doc = EntitlementVerifierTests.Fixture();
        var host = await AgentTestHost.StartAsync(entitlementTrust: EntitlementVerifierTests.TestTrust(doc));
        try
        {
            Assert.Null(host.Services.Account);
            var admin = new AdminService(host.Services);
            foreach (var op in new[] { "login.start", "login.status", "unlink" })
            {
                var reply = await admin.HandleAsync(new JsonObject { ["op"] = op }, new AdminConnectionState(), CancellationToken.None);
                Assert.False(reply["ok"]!.GetValue<bool>());
                Assert.Equal(AccountClient.Disabled, reply["error"]!.GetValue<string>());
            }
            var (client, _, key) = await host.PairedClientAsync();
            await using var _ = client;
            using var __ = key;
            Assert.True((await client.RequestAsync("account.refresh")).IsOk());
        }
        finally
        {
            await host.DisposeAsync();
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    [Fact]
    public async Task Tray_login_then_account_refresh_goes_through_pc_token()
    {
        using var doc = EntitlementVerifierTests.Fixture();
        var clock = new FakeEntitlementClock(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Guid pcId = default;
        var fake = new FakeAccountServer(n => EntitlementVerifierTests.Sign(doc,
            EntitlementVerifierTests.Payload(pcId, clock.Wall + n, clock.Wall + n + 86_400, clock.Wall + n + 86_400 + 604_800)));
        var host = await AgentTestHost.StartAsync(entitlementTrust: EntitlementVerifierTests.TestTrust(doc), entitlementClock: clock,
            accountServer: AccountServerConfig.ForTests(new Uri("https://accounts.test/"), FakeAccountServer.AnonKey), accountHandler: fake);
        try
        {
            pcId = host.PcId;
            var account = host.Services.Account;
            Assert.NotNull(account);
            Assert.Contains("login.start", Nexus.Core.Session.SessionOps.AllowedFromTray);
            Assert.Contains("login.status", Nexus.Core.Session.SessionOps.AllowedFromTray);
            Assert.Contains("unlink", Nexus.Core.Session.SessionOps.AllowedFromTray);
            fake.PendingPolls = 0;
            var admin = new AdminService(host.Services);
            var start = await admin.HandleAsync(new JsonObject { ["op"] = "login.start" }, new AdminConnectionState(), CancellationToken.None);
            Assert.True(start["ok"]!.GetValue<bool>(), start.ToJsonString());
            Assert.Equal(fake.UserCode, start["userCode"]!.GetValue<string>());
            Assert.Null(start["deviceCode"]);
            // The interval (5 s) has to pass before the poll: wait on real time briefly.
            JsonObject status;
            var until = DateTime.UtcNow.AddSeconds(20);
            do
            {
                await Task.Delay(500);
                status = await admin.HandleAsync(new JsonObject { ["op"] = "login.status" }, new AdminConnectionState(), CancellationToken.None);
            }
            while (status["state"]!.GetValue<string>() == "pending" && DateTime.UtcNow < until);
            Assert.Equal("ok", status["state"]!.GetValue<string>());
            Assert.Equal("pro", status["plan"]!.GetValue<string>());

            var (client, _, key) = await host.PairedClientAsync();
            await using var _ = client;
            using var __ = key;
            var before = fake.Requests.Count(r => r.Function == "pc-token");
            var refreshed = await client.RequestAsync("account.refresh");
            Assert.True(refreshed.IsOk(), refreshed.ToJsonString());
            Assert.Equal("pro", refreshed["payload"]!["plan"]!.GetValue<string>());
            Assert.Equal(before + 1, fake.Requests.Count(r => r.Function == "pc-token"));
            Assert.Equal(ErrorCodes.RateLimited, (await client.RequestAsync("account.refresh")).ErrorCode()); // 1 per minute stays
            Assert.Equal(before + 1, fake.Requests.Count(r => r.Function == "pc-token"));

            var unlink = await admin.HandleAsync(new JsonObject { ["op"] = "unlink" }, new AdminConnectionState(), CancellationToken.None);
            Assert.True(unlink["ok"]!.GetValue<bool>());
            Assert.Equal("free", host.Services.Entitlements.Current.Plan);

            var audit = File.ReadAllText(host.Services.Audit.FilePath);
            foreach (var secret in fake.Rotations.Keys.Concat(fake.Rotations.Values).Append(fake.DeviceCode).Concat(fake.Issued))
            {
                Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);

            }
            Assert.Contains("account.login", audit, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }
}
