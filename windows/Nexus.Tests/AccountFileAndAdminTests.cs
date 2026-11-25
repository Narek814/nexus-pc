using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Nexus.Agent.Account;
using Nexus.Agent.Admin;
using Nexus.Agent.Security;
using Nexus.Agent.Storage;
using Nexus.Core.Account;
using Nexus.Core.Protocol;
using Nexus.Core.Session;
using Nexus.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Tests;

/// <summary>E4-6: account.dat SYSTEM-only with migration (S1, S4), sign-in and unlink for administrators only (S2).</summary>
public sealed class AccountFileAndAdminTests : IDisposable
{
    private readonly ITestOutputHelper output;

    private const string Token = "refresh-token-value-0123456789";
    private const string Admins = DataDirectoryPolicy.Administrators;
    private const string SystemSid = DataDirectoryPolicy.LocalSystem;
    private const string Users = "S-1-5-32-545";
    private const string Everyone = "S-1-1-0";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const int Inherited = ProtectedFilePolicy.InheritedAceFlag;

    private static readonly IReadOnlySet<string> AllowedProd = new HashSet<string> { SystemSid };
    private static readonly IReadOnlySet<string> Owners = DataDirectoryPolicy.TrustedSids;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-e46-" + Guid.NewGuid().ToString("N"));

    public AccountFileAndAdminTests(ITestOutputHelper output)
    {
        this.output = output;
        AgentTestHost.TrustTestAccount();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ---- policy ----

    private static ProtectedFileVerdict Classify(string? owner, bool isProtected, params DataAce[] dacl) =>
        ProtectedFilePolicy.Classify(owner, daclPresent: true, isProtected, dacl, AllowedProd, Owners, acceptLegacy: true);

    [Fact]
    public void System_only_protected_file_is_trusted()
    {
        Assert.Equal(ProtectedFileVerdict.Trusted, Classify(Admins, true, new DataAce(SystemSid, true, DataDirectoryPolicy.FullControl, 0)));
        Assert.Equal(ProtectedFileVerdict.Trusted, Classify(SystemSid, true, new DataAce(SystemSid, true, DataDirectoryPolicy.FullControl, 0)));
    }

    [Theory]
    [InlineData(SystemSid)]
    [InlineData(Admins)]
    public void Old_inherited_access_is_legacy(string owner)
    {
        Assert.Equal(ProtectedFileVerdict.Legacy, Classify(owner, false,
            new DataAce(SystemSid, true, DataDirectoryPolicy.FullControl, Inherited),
            new DataAce(Admins, true, DataDirectoryPolicy.FullControl, Inherited)));
    }

    public static TheoryData<string, string?, bool, DataAce[]> UntrustedCases() => new()
    {
        { "Users", Admins, false, [new(SystemSid, true, 0x1F01FF, Inherited), new(Admins, true, 0x1F01FF, Inherited), new(Users, true, 0x120089, Inherited)] },
        { "Everyone", Admins, false, [new(SystemSid, true, 0x1F01FF, Inherited), new(Everyone, true, 0x120089, Inherited)] },
        { "Authenticated Users", Admins, false, [new(SystemSid, true, 0x1F01FF, Inherited), new(AuthenticatedUsers, true, 0x120089, Inherited)] },
        { "user SID", Admins, false, [new(SystemSid, true, 0x1F01FF, Inherited), new(SomeUser, true, 0x1F01FF, Inherited)] },
        { "user SID protected", Admins, true, [new(SystemSid, true, 0x1F01FF, 0), new(SomeUser, true, 0x1F01FF, 0)] },
        { "Administrators explicit, protected", Admins, true, [new(SystemSid, true, 0x1F01FF, 0), new(Admins, true, 0x1F01FF, 0)] },
        { "not inherited, not protected", Admins, false, [new(SystemSid, true, 0x1F01FF, 0), new(Admins, true, 0x1F01FF, 0)] },
        { "deny entry", Admins, false, [new(SystemSid, true, 0x1F01FF, Inherited), new(Users, false, 0x1F01FF, Inherited)] },
        { "foreign owner", SomeUser, false, [new(SystemSid, true, 0x1F01FF, Inherited), new(Admins, true, 0x1F01FF, Inherited)] },
        { "foreign owner protected", SomeUser, true, [new(SystemSid, true, 0x1F01FF, 0)] },
        { "unknown owner", null, true, [new(SystemSid, true, 0x1F01FF, 0)] },
        { "TrustedInstaller owner, inherited", DataDirectoryPolicy.TrustedInstaller, false, [new(SystemSid, true, 0x1F01FF, Inherited)] },
        { "empty inherited", Admins, false, [] },
    };

    [Theory]
    [MemberData(nameof(UntrustedCases))]
    public void Anything_else_is_untrusted(string name, string? owner, bool isProtected, DataAce[] dacl)
    {
        Assert.True(Classify(owner, isProtected, dacl) == ProtectedFileVerdict.Untrusted, name);
    }

    [Fact]
    public void Null_dacl_is_untrusted_and_entitlement_never_accepts_legacy()
    {
        Assert.Equal(ProtectedFileVerdict.Untrusted, ProtectedFilePolicy.Classify(Admins, false, false, [], AllowedProd, Owners, acceptLegacy: true));
        DataAce[] legacy = [new(SystemSid, true, 0x1F01FF, Inherited), new(Admins, true, 0x1F01FF, Inherited)];
        Assert.Equal(ProtectedFileVerdict.Untrusted, ProtectedFilePolicy.Classify(Admins, true, false, legacy, AllowedProd, Owners, acceptLegacy: false));
        Assert.False(Nexus.Core.Entitlements.EntitlementFilePolicy.IsTrusted(Admins, true, false, legacy, AllowedProd, Owners));
    }

    [Theory]
    [InlineData("account.dat")]
    [InlineData("ACCOUNT.DAT")]
    [InlineData("account.dat.tmp")]
    public void Repair_puts_system_only_on_account_dat(string path)
    {
        Assert.IsType<DataSecurity.SystemOnlyFile>(DataDirectoryPolicy.RepairSecurityFor(path, DataObjectKind.File));
        Assert.IsType<DataSecurity.InheritedFromRoot>(DataDirectoryPolicy.RepairSecurityFor(Path.Combine("OpenRGB", path), DataObjectKind.File));
        Assert.IsType<DataSecurity.InheritedFromRoot>(DataDirectoryPolicy.RepairSecurityFor(path, DataObjectKind.Directory));
        Assert.Equal(DataDirectoryPolicy.AccountFileName, RefreshTokenStore.FileName);
    }

    [Fact]
    public void System_only_account_dat_is_not_a_scan_violation()
    {
        var state = new DataObjectState
        {
            FullPath = "account.dat", Kind = DataObjectKind.File, OwnerSid = Admins, DaclPresent = true, DaclProtected = true,
            Dacl = [new DataAce(SystemSid, true, DataDirectoryPolicy.FullControl, 0)],
        };
        Assert.Empty(DataDirectoryPolicy.CheckSecurity("account.dat", state, DataDirectoryPolicy.TrustedSids));
        Assert.True(DataDirectoryPolicy.IsRepaired(state, isRoot: false));
    }

    // ---- store with a fake access check (Linux) ----

    private sealed class FakeAccess(ProtectedFileVerdict verdict) : IProtectedFileAccess
    {
        public ProtectedFileVerdict Verdict { get; set; } = verdict;

        public List<string> Created { get; } = [];

        public int Inspected { get; private set; }

        public FileStream CreateNew(string path)
        {
            Created.Add(Path.GetFileName(path));
            Verdict = ProtectedFileVerdict.Trusted; // what CreateNew produces
            return ProtectedFile.CreateNew(path);
        }

        public ProtectedFileVerdict Inspect(FileStream opened)
        {
            Inspected++;
            return Verdict;
        }
    }

    private sealed class CountingProtector : IRefreshProtector
    {
        private readonly XorProtector _inner = new();

        public int Opened { get; private set; }

        public byte[] Protect(byte[] plain) => _inner.Protect(plain);

        public byte[] Unprotect(byte[] sealedBytes)
        {
            Opened++;
            return _inner.Unprotect(sealedBytes);
        }
    }

    private void WriteOldFile()
    {
        var sealer = new RefreshTokenStore(_dir, new XorProtector(), new FakeAccess(ProtectedFileVerdict.Trusted));
        sealer.Save(Token);
    }

    [Fact]
    public void Save_goes_through_a_protected_temp_file_then_replaces()
    {
        var access = new FakeAccess(ProtectedFileVerdict.Trusted);
        var store = new RefreshTokenStore(_dir, new XorProtector(), access);
        File.WriteAllText(Path.Combine(_dir, "account.dat.tmp"), "left from a crash");
        store.Save(Token);
        store.Save(Token + "2");
        Assert.Equal(["account.dat.tmp", "account.dat.tmp"], access.Created);
        Assert.False(File.Exists(Path.Combine(_dir, "account.dat.tmp")));
        Assert.Equal(Token + "2", store.Load());
        Assert.DoesNotContain(Token, File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Trusted_file_is_read_without_rewrite()
    {
        WriteOldFile();
        var access = new FakeAccess(ProtectedFileVerdict.Trusted);
        var store = new RefreshTokenStore(_dir, new XorProtector(), access);
        Assert.Equal(Token, store.Load());
        Assert.Empty(access.Created);
    }

    [Fact]
    public void Legacy_file_is_read_once_and_rewritten_system_only()
    {
        WriteOldFile();
        var access = new FakeAccess(ProtectedFileVerdict.Legacy);
        var untrusted = 0;
        var store = new RefreshTokenStore(_dir, new XorProtector(), access, () => untrusted++);
        Assert.Equal(Token, store.Load());
        Assert.Equal(["account.dat.tmp"], access.Created); // rewritten through the protected temp file
        Assert.Equal(ProtectedFileVerdict.Trusted, access.Verdict);
        Assert.Equal(Token, store.Load()); // the sign-in stays
        Assert.Single(access.Created);
        Assert.Equal(0, untrusted);
    }

    [Fact]
    public void Untrusted_file_is_deleted_unread_and_reported()
    {
        WriteOldFile();
        var access = new FakeAccess(ProtectedFileVerdict.Untrusted);
        var protector = new CountingProtector();
        var untrusted = 0;
        var store = new RefreshTokenStore(_dir, protector, access, () => untrusted++);
        Assert.Null(store.Load());
        Assert.Equal(0, protector.Opened); // content never unsealed
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal(1, untrusted);
        Assert.Null(store.Load()); // nothing left: "not signed in", no second report
        Assert.Equal(1, untrusted);
    }

    [Fact]
    public void Missing_file_and_io_problems_are_not_signed_in()
    {
        var access = new FakeAccess(ProtectedFileVerdict.Trusted);
        var store = new RefreshTokenStore(_dir, new XorProtector(), access);
        Assert.Null(store.Load());
        Assert.Equal(0, access.Inspected);
        Directory.CreateDirectory(store.FilePath); // a folder where the file should be
        Assert.Null(store.Load());
        Directory.Delete(store.FilePath);
        var gone = new RefreshTokenStore(Path.Combine(_dir, "missing"), new XorProtector(), access);
        Assert.Null(gone.Load());
    }

    [Fact]
    public void Real_check_rejects_a_file_others_can_read()
    {
        var store = new RefreshTokenStore(_dir, new XorProtector());
        store.Save(Token);
        Assert.Equal(Token, store.Load());
        if (OperatingSystem.IsWindows())
        {
            AddUsersRead(store.FilePath);
        }
        else
        {
            File.SetUnixFileMode(store.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
        var reported = 0;
        var again = new RefreshTokenStore(_dir, new XorProtector(), onUntrusted: () => reported++);
        Assert.Null(again.Load());
        Assert.False(File.Exists(again.FilePath));
        Assert.Equal(1, reported);
    }

    [SupportedOSPlatform("windows")]
    private static void AddUsersRead(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public async Task Untrusted_account_file_is_audited_without_path_or_content()
    {
        using var doc = EntitlementVerifierTests.Fixture();
        var host = await AgentTestHost.StartAsync(entitlementTrust: EntitlementVerifierTests.TestTrust(doc),
            accountServer: AccountServerConfig.ForTests(new Uri("https://accounts.test/"), FakeAccountServer.AnonKey),
            accountHandler: new FakeAccountServer(_ => "x"));
        try
        {
            var path = Path.Combine(host.DataDirectory, RefreshTokenStore.FileName);
            var account = host.Services.Account;
            Assert.NotNull(account);
            new RefreshTokenStore(host.DataDirectory, new XorProtector()).Save(Token);
            if (OperatingSystem.IsWindows())
            {
                AddUsersRead(path);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }
            Assert.False(account.SignedIn);
            Assert.False(File.Exists(path));
            var audit = File.ReadAllText(host.Services.Audit.FilePath);
            Assert.Contains("account.untrustedFile", audit, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, audit, StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshTokenStore.FileName, audit, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    // ---- routing ----

    [Fact]
    public void Session_pipe_routing()
    {
        Assert.Null(SessionOps.SessionRefusal(SessionOps.LoginStatus));
        Assert.Null(SessionOps.SessionRefusal(SessionOps.Status));
        Assert.Equal(SessionOps.AdminRequired, SessionOps.SessionRefusal(SessionOps.LoginStart));
        Assert.Equal(SessionOps.AdminRequired, SessionOps.SessionRefusal(SessionOps.Unlink));
        Assert.Equal(ErrorCodes.PermissionDenied, SessionOps.SessionRefusal("devices.permissions"));
    }

    [Fact]
    public void Admin_pipe_gate_asks_the_token_only_for_admin_only_operations()
    {
        var asked = 0;
        bool No() { asked++; return false; }
        Assert.Null(SessionOps.AdminPipeRefusal("status", No));
        Assert.Null(SessionOps.AdminPipeRefusal("devices.list", No));
        Assert.Null(SessionOps.AdminPipeRefusal(null, No));
        Assert.Equal(0, asked);
        Assert.Equal(SessionOps.AdminRequired, SessionOps.AdminPipeRefusal(SessionOps.LoginStart, No));
        Assert.Equal(SessionOps.AdminRequired, SessionOps.AdminPipeRefusal(SessionOps.Unlink, No));
        Assert.Null(SessionOps.AdminPipeRefusal(SessionOps.LoginStart, () => true));
        Assert.Equal(2, asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_pipe_decides_by_the_client_check_not_by_the_request(bool administrator)
    {
        var dir = Path.Combine(_dir, "agent");
        Directory.CreateDirectory(dir);
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var local = new Nexus.Agent.Server.LocalServices(new InMemoryDeviceStore(), new FakePowerController(), new AuditLog(dir, time), time);
        var admin = new AdminService(local, new DeviceKeyUnavailable("CryptographicException/0x80090016", 4, time.GetUtcNow()), () => { });
        var name = "nexus-admin-" + Guid.NewGuid().ToString("N");
        var checks = 0;
        var server = new AdminPipeServer(admin, name, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminPipeServer>.Instance,
            _ => { checks++; return administrator; });
        await server.StartAsync(CancellationToken.None);
        try
        {
            await using var client = await AdminClient.ConnectAsync(name, CancellationToken.None);
            foreach (var op in new[] { SessionOps.LoginStart, SessionOps.Unlink })
            {
                // A request field claiming to be an administrator changes nothing.
                var reply = await client.SendAsync(new JsonObject { ["op"] = op, ["admin"] = true, ["elevated"] = true }, CancellationToken.None);
                Assert.False(reply["ok"]!.GetValue<bool>());
                Assert.Equal(administrator ? AdminService.KeyUnavailable : SessionOps.AdminRequired, reply["error"]!.GetValue<string>());
            }
            var status = await client.SendAsync(new JsonObject { ["op"] = SessionOps.LoginStatus }, CancellationToken.None);
            Assert.Equal(AdminService.KeyUnavailable, status["error"]!.GetValue<string>()); // not gated
            Assert.Equal(1, checks); // once per connection
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    // ---- tray model and elevated helper ----

    [Fact]
    public void Elevated_command_line_is_one_fixed_argument()
    {
        Assert.Equal("--account-login", ElevatedAccountCommand.ArgumentFor(SessionOps.LoginStart));
        Assert.Equal("--account-unlink", ElevatedAccountCommand.ArgumentFor(SessionOps.Unlink));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElevatedAccountCommand.ArgumentFor("pair.start"));
        Assert.Equal(SessionOps.LoginStart, ElevatedAccountCommand.Parse(["--account-login"]));
        Assert.Equal(SessionOps.Unlink, ElevatedAccountCommand.Parse(["--account-unlink"]));
        Assert.Null(ElevatedAccountCommand.Parse([]));
        Assert.Null(ElevatedAccountCommand.Parse(["--account-login", "ABCDEFGH"]));
        Assert.Null(ElevatedAccountCommand.Parse(["--account-status"]));
    }

    [Theory]
    [InlineData("admin.required")]
    [InlineData("rate.limited")]
    [InlineData("pcs.limit")]
    [InlineData("transient")]
    [InlineData("disabled")]
    [InlineData("keyUnavailable")]
    [InlineData("session.unavailable")]
    [InlineData("failed")]
    public void Exit_codes_carry_only_the_error_code(string error)
    {
        var exit = ElevatedAccountCommand.ExitCodeOf(new JsonObject { ["ok"] = false, ["error"] = error });
        Assert.NotEqual(ElevatedAccountCommand.ExitOk, exit);
        Assert.Equal(error, ElevatedAccountCommand.ErrorOf(exit));
        Assert.Equal(ElevatedAccountCommand.ExitOk, ElevatedAccountCommand.ExitCodeOf(new JsonObject { ["ok"] = true, ["userCode"] = "ABCDEFGH" }));
        Assert.Equal("failed", ElevatedAccountCommand.ErrorOf(ElevatedAccountCommand.ExitCodeOf(new JsonObject { ["ok"] = false, ["error"] = "something.new" })));
        Assert.Equal("failed", ElevatedAccountCommand.ErrorOf(ElevatedAccountCommand.ExitCodeOf(null)));
    }

    private sealed class MemoryMarker : ILoginMarker
    {
        public DateTimeOffset? Value { get; set; }

        public DateTimeOffset? Read() => Value;

        public void Write(DateTimeOffset startedAt) => Value = startedAt;

        public void Clear() => Value = null;
    }

    [Fact]
    public void Declined_prompt_is_a_plain_message()
    {
        var marker = new MemoryMarker();
        var model = new AccountLoginModel(marker);
        model.BeginStart();
        Assert.False(model.OnElevatedExit(SessionOps.LoginStart, null));
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.Equal(AccountProblem.AdminDeclined, model.Problem);
        Assert.Null(marker.Value);
    }

    [Fact]
    public void Service_refusal_maps_to_admin_required()
    {
        var model = new AccountLoginModel(new MemoryMarker());
        model.BeginStart();
        Assert.False(model.OnElevatedExit(SessionOps.LoginStart, ElevatedAccountCommand.ExitCodeOfError(SessionOps.AdminRequired)));
        Assert.Equal(AccountProblem.AdminRequired, model.Problem);
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.Equal(AccountProblem.AdminRequired, AccountLoginModel.Map("admin.required"));
    }

    [Fact]
    public void Elevated_sign_in_marks_the_attempt_and_the_code_comes_from_login_status()
    {
        var marker = new MemoryMarker();
        var model = new AccountLoginModel(marker);
        model.BeginStart();
        Assert.True(model.OnElevatedExit(SessionOps.LoginStart, ElevatedAccountCommand.ExitOk));
        Assert.NotNull(marker.Value); // the normal tray's marker: a later start can resume
        Assert.Null(model.UserCode);
        var notice = model.OnStatusReply(RpcResult.Success(new JsonObject
        {
            ["state"] = "pending", ["userCode"] = "ABCDEFGH", ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"), ["linked"] = false,
        }));
        Assert.Equal(LoginNotice.None, notice);
        Assert.Equal(AccountPhase.Pending, model.Phase);
        Assert.Equal("ABCD-EFGH", model.UserCodeDisplay);
        Assert.True(model.ShouldPoll);
        Assert.DoesNotContain("ABCD", model.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Elevated_unlink_clears_the_marker_and_signs_out()
    {
        var marker = new MemoryMarker { Value = DateTimeOffset.UtcNow };
        var model = new AccountLoginModel(marker);
        model.OnStatusReply(RpcResult.Success(new JsonObject { ["state"] = "ok", ["linked"] = true, ["plan"] = "pro" }));
        Assert.Equal(AccountPhase.SignedIn, model.Phase);
        Assert.True(model.OnElevatedExit(SessionOps.Unlink, ElevatedAccountCommand.ExitOk));
        Assert.Null(marker.Value);
        Assert.False(model.Linked);
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        model.OnStatusReply(RpcResult.Success(new JsonObject { ["state"] = "none", ["linked"] = false, ["plan"] = "free" }));
        Assert.Equal(AccountPhase.SignedOut, model.Phase);
        Assert.Equal(AccountProblem.None, model.Problem);
    }

    // ---- Windows: real DACL (CI windows-latest, elevated) ----

    [SupportedOSPlatform("windows")]
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool WindowsReady()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Not Windows: real DACLs are checked by CI windows-latest.");
            return false;
        }
        if (!Elevated())
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "CI must run these tests elevated.");
            output.WriteLine("Not elevated: skipped locally.");
            return false;
        }
        return true;
    }

    [Fact]
    public void Windows_save_gives_a_protected_system_only_dacl()
    {
        if (!OperatingSystem.IsWindows() || !WindowsReady())
        {
            return;
        }
        var store = new RefreshTokenStore(_dir, new XorProtector());
        store.Save(Token);
        AssertSystemOnly(store.FilePath);
        Assert.Equal(Token, store.Load());
    }

    [Fact]
    public void Windows_old_inherited_access_is_migrated()
    {
        if (!OperatingSystem.IsWindows() || !WindowsReady())
        {
            return;
        }
        var root = Path.Combine(_dir, "data");
        CreateOldStyleFolder(root);
        var store = new RefreshTokenStore(root, new XorProtector());
        File.WriteAllBytes(store.FilePath, new XorProtector().Protect(System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"refreshToken\":\"" + Token + "\"}")));
        Assert.Equal(ProtectedFileVerdict.Legacy, ProtectedFile.Classify(store.FilePath, acceptLegacy: true));
        var reported = 0;
        var migrating = new RefreshTokenStore(root, new XorProtector(), onUntrusted: () => reported++);
        Assert.Equal(Token, migrating.Load());
        AssertSystemOnly(store.FilePath);
        Assert.Equal(Token, migrating.Load());
        Assert.Equal(0, reported);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateOldStyleFolder(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertSystemOnly(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowed = ProtectedFile.AllowedAccess(); // SYSTEM; the test account only because tests set TestTrustedSids
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Contains(rules, r => r.IdentityReference.Value == SystemSid && r.AccessControlType == AccessControlType.Allow);
        Assert.All(rules, r =>
        {
            Assert.False(r.IsInherited);
            Assert.Contains(r.IdentityReference.Value, allowed);
            Assert.NotEqual(Admins, r.IdentityReference.Value);
        });
        Assert.Equal(ProtectedFileVerdict.Trusted, ProtectedFile.Classify(path, acceptLegacy: true));
    }
}
