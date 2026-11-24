using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Agent.Admin;
using Nexus.Agent.Hosting;
using Nexus.Agent.Security;
using Nexus.Agent.Server;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Tests;

/// <summary>
/// E4-3a: the PC key cannot be opened. The agent runs without identity, certificate, plan token or port; local
/// functions and the admin/tray channels answer "keyUnavailable"; nothing in the data folder changes.
/// </summary>
public sealed class DeviceKeyDegradedTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-keydeg-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _eventLog = [];
    private int _factoryCalls;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        AgentTestHost.DeleteDirectory(_dir);
        return Task.CompletedTask;
    }

    private IDeviceKey FailingKey()
    {
        Interlocked.Increment(ref _factoryCalls);
        throw new IOException("Keyset does not exist at " + _dir, unchecked((int)0x80090016));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<(IHost Host, string SessionPipe, string AdminPipe)> StartDegradedAsync(int port)
    {
        AgentTestHost.TrustTestAccount();
        var sessionPipe = "nexus-session-" + Guid.NewGuid().ToString("N");
        var adminPipe = "nexus-admin-" + Guid.NewGuid().ToString("N");
        var host = AgentHost.Build([], new AgentHostOptions
        {
            DataDirectory = _dir,
            Port = port,
            BindAddresses = [IPAddress.Loopback],
            Power = new FakePowerController(),
            AdminPipeName = adminPipe,
            SessionPipeName = sessionPipe,
            TrayPath = Environment.ProcessPath,
            MetricsSource = new FakeMetricsSource(),
            OpenRgbExecutable = null,
            OpenRgbPort = 1,
            DeviceKeyFactory = FailingKey,
            DeviceKeyPauses = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            EventLog = _eventLog.Add,
        });
        await host.StartAsync();
        return (host, sessionPipe, adminPipe);
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        await AgentTestHost.DisposeHostAsync(host);
    }

    [Fact]
    public async Task KeyFails_AgentRuns_PortClosed_StatusHasNoPcId()
    {
        var port = FreePort();
        var (host, _, adminPipe) = await StartDegradedAsync(port);
        try
        {
            Assert.Equal(4, _factoryCalls); // first try + one after each of the 3 pauses
            Assert.Null(host.Services.GetService<AgentServices>()); // no identity, no pcId, no plan tracker
            Assert.NotNull(host.Services.GetService<LocalServices>());

            using (var client = new TcpClient())
            {
                await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
            }

            var admin = host.Services.GetRequiredService<AdminService>();
            var status = await admin.HandleAsync(new JsonObject { ["op"] = "status" }, new AdminConnectionState(), CancellationToken.None);
            Assert.True(status.IsOk());
            Assert.Equal("keyUnavailable", status["state"]!.GetValue<string>());
            Assert.Equal("IOException/0x80090016", status["error"]!.GetValue<string>());
            Assert.Equal(4, status["attempts"]!.GetValue<int>());
            Assert.NotNull(status["lastAttemptUtc"]);
            Assert.Null(status["pcId"]);
            Assert.Null(status["fingerprint"]);

            // Over the real admin pipe as well.
            await using (var pipe = await AdminClient.ConnectAsync(adminPipe, CancellationToken.None))
            {
                var viaPipe = await pipe.SendAsync(new JsonObject { ["op"] = "status" }, CancellationToken.None);
                Assert.Equal("keyUnavailable", viaPipe["state"]!.GetValue<string>());
                Assert.Null(viaPipe["pcId"]);
            }

            var audit = await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log"));
            var line = Assert.Single(audit.Split('\n'), l => l.Contains("\"device.keyUnavailable\"", StringComparison.Ordinal));
            Assert.Contains("\"ok\":false", line, StringComparison.Ordinal);
            Assert.Contains("IOException/0x80090016", line, StringComparison.Ordinal);
            Assert.DoesNotContain(_dir, audit, StringComparison.Ordinal); // no exception text, no paths
            var report = Assert.Single(_eventLog);
            Assert.Contains("IOException/0x80090016", report, StringComparison.Ordinal);
            Assert.DoesNotContain(_dir, report, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task KeyFails_PairingRefused_NoLinkOrQr()
    {
        var (host, _, _) = await StartDegradedAsync(FreePort());
        try
        {
            var admin = host.Services.GetRequiredService<AdminService>();
            var state = new AdminConnectionState();
            foreach (var op in new[] { "pair.start", "pair.wait", "pair.decide", "pair.cancel" })
            {
                var response = await admin.HandleAsync(new JsonObject { ["op"] = op }, state, CancellationToken.None);
                Assert.False(response.IsOk(), op);
                Assert.Equal(AdminService.KeyUnavailable, response["error"]!.GetValue<string>());
                Assert.Null(response["link"]);
            }
            Assert.Throws<InvalidOperationException>(() => admin.CreateLink("01234567"));
            // Local functions keep working.
            var devices = await admin.HandleAsync(new JsonObject { ["op"] = "devices.list" }, state, CancellationToken.None);
            Assert.True(devices.IsOk());
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task KeyFails_TrayGetsStatus_ButCannotRetry()
    {
        var (host, sessionPipe, _) = await StartDegradedAsync(FreePort());
        try
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(".", sessionPipe, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            await using var tray = new FakeTray(pipe);
            var local = host.Services.GetRequiredService<LocalServices>();
            await AgentIntegrationTests.WaitUntil(() => local.Trays.IsAvailable);

            var status = await tray.CallAgentAsync(Nexus.Core.Session.SessionOps.Status);
            Assert.True(status.Ok, status.Message);
            Assert.Equal("keyUnavailable", status.Result!["state"]!.GetValue<string>());
            Assert.Null(status.Result!["pcId"]);

            var retry = await tray.CallAgentAsync("key.retry");
            Assert.False(retry.Ok);
            Assert.Equal(ErrorCodes.PermissionDenied, retry.ErrorCode);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.DoesNotContain("device.keyRetry", await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log")), StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task KeyRetry_StopsWithNonZeroCode_SecondCallWithinMinuteIsRateLimited()
    {
        var (host, _, adminPipe) = await StartDegradedAsync(FreePort());
        try
        {
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await using var pipe = await AdminClient.ConnectAsync(adminPipe, CancellationToken.None);
            var first = await pipe.SendAsync(new JsonObject { ["op"] = "key.retry" }, CancellationToken.None);
            Assert.True(first.IsOk(), first.ToJsonString());
            await AgentIntegrationTests.WaitUntil(() => lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(AgentRunState.KeyRetryExitCode, host.Services.GetRequiredService<AgentRunState>().ExitCode);
            Assert.NotEqual(0, AgentRunState.KeyRetryExitCode);

            var admin = host.Services.GetRequiredService<AdminService>();
            var second = await admin.HandleAsync(new JsonObject { ["op"] = "key.retry" }, new AdminConnectionState(), CancellationToken.None);
            Assert.False(second.IsOk());
            Assert.Equal(AdminService.RateLimited, second["error"]!.GetValue<string>());

            var audit = await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log"));
            Assert.Single(audit.Split('\n'), l => l.Contains("\"device.keyRetry\"", StringComparison.Ordinal));
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task KeyRetry_AllowedAgainAfterOneMinute()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var dir = Path.Combine(Path.GetTempPath(), "nexus-keydeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var local = new LocalServices(new InMemoryDeviceStore(), new FakePowerController(), new Nexus.Agent.Storage.AuditLog(dir, time), time);
            var restarts = 0;
            var admin = new AdminService(local, new DeviceKeyUnavailable("CryptographicException/0x80090016", 4, time.GetUtcNow()), () => restarts++);
            var state = new AdminConnectionState();
            Assert.True((await admin.HandleAsync(new JsonObject { ["op"] = "key.retry" }, state, CancellationToken.None)).IsOk());
            time.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(AdminService.RateLimited, (await admin.HandleAsync(new JsonObject { ["op"] = "key.retry" }, state, CancellationToken.None))["error"]!.GetValue<string>());
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await admin.HandleAsync(new JsonObject { ["op"] = "key.retry" }, state, CancellationToken.None)).IsOk());
            Assert.Equal(2, restarts);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task KeyRetry_InNormalMode_IsRefusedAndDoesNotStop()
    {
        await using var host = await AgentTestHost.StartAsync();
        try
        {
            var response = await host.Admin.HandleAsync(new JsonObject { ["op"] = "key.retry" }, new AdminConnectionState(), CancellationToken.None);
            Assert.False(response.IsOk());
            var status = await host.Admin.HandleAsync(new JsonObject { ["op"] = "status" }, new AdminConnectionState(), CancellationToken.None);
            Assert.Equal("ready", status["state"]!.GetValue<string>());
            Assert.Equal(host.PcId.ToString("D"), status["pcId"]!.GetValue<string>());
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    [Fact]
    public async Task KeyFails_FilesUnchanged_ThenKeyBack_SamePcIdWithoutKeyReplaced()
    {
        Guid pcId;
        string fingerprint;
        await using (var normal = await AgentTestHost.StartAsync(_dir))
        {
            (pcId, fingerprint) = (normal.PcId, normal.Fingerprint);
            normal.Services.Entitlements.Flush(); // entitlement.json exists, so its bytes can be compared
        }
        var before = Snapshot();
        Assert.Contains("config.json", before.Keys);

        var (host, _, _) = await StartDegradedAsync(FreePort());
        await StopAsync(host);
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal)); // no new certificate or file
        foreach (var (name, bytes) in before)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(after[name]), name + " changed");
        }

        // The key works again (same key file): normal mode, the same derived pcId and certificate, no "key replaced".
        await using (var back = await AgentTestHost.StartAsync(_dir))
        {
            Assert.Equal(pcId, back.PcId);
            Assert.Equal(PcIdDerivation.FromSpki(back.Services.Identity.DeviceKey!.Spki), back.PcId);
            Assert.Equal(fingerprint, back.Fingerprint);
        }
        var audit = await File.ReadAllTextAsync(Path.Combine(_dir, "audit.log"));
        Assert.Contains("\"device.keyUnavailable\"", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("pcId.keyReplaced", audit, StringComparison.Ordinal);
    }

    /// <summary>Every file of the data folder except the append-only logs.</summary>
    private Dictionary<string, byte[]> Snapshot() =>
        Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("audit.log", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("history", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(_dir, f), File.ReadAllBytes, StringComparer.Ordinal);

    [Fact]
    public void Opener_RetriesWithEachPause_ThenSucceeds()
    {
        var calls = 0;
        using var good = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = DeviceKeyOpener.Open(() =>
        {
            if (++calls < 3)
            {
                throw new CryptographicException("not yet");
            }
            return new StubKey(good.ExportSubjectPublicKeyInfo());
        }, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], TimeProvider.System, out var failure);
        Assert.NotNull(key);
        Assert.Null(failure);
        Assert.Equal(3, calls);
        Assert.Equal(3, DeviceKeyOpener.DefaultPauses.Count);
        Assert.Equal([2, 5, 10], DeviceKeyOpener.DefaultPauses.Select(p => (int)p.TotalSeconds));
    }

    [Fact]
    public void Opener_ErrorCodeHasTypeAndHResultOnly()
    {
        var key = DeviceKeyOpener.Open(() => throw new IOException("C:\\secret\\path missing"), [], TimeProvider.System, out var failure);
        Assert.Null(key);
        Assert.Equal(1, failure!.Attempts);
        Assert.StartsWith("IOException/0x", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalServices_WithoutKey_HoldAutomationsToTheFreePlan()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-keydeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var local = new LocalServices(new InMemoryDeviceStore(), new FakePowerController(), new Nexus.Agent.Storage.AuditLog(dir, TimeProvider.System), TimeProvider.System);
            Assert.Same(Nexus.Core.Entitlements.EntitlementStatus.Free, local.EnforcedPlan);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class StubKey(byte[] spki) : IDeviceKey
    {
        public byte[] Spki { get; } = spki;
        public string Provider => "stub";
        public bool Created => false;
        public byte[] SignP1363(ReadOnlySpan<byte> data) => throw new NotSupportedException();
        public void Dispose()
        {
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>
/// E4-3a owner decision: the development/CI file key for non-Windows. Explicit flag only, never on Windows, not
/// compiled into Windows (win-* RuntimeIdentifier) builds, 0600, never a fallback.
/// </summary>
public sealed class DevFileDeviceKeyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nexus-devkey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static Func<string, string?> Env(string? value) => name => name == "NEXUS_DEV_FILE_KEY" ? value : null;

    [Fact]
    public void WithoutFlag_NoFileKey_AndNoKeyAtAll()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // on Windows the default source is the machine CNG key; the file key is refused below
        }
        Assert.Null(DevFileDeviceKey.PathFromEnvironment(Env(null)));
        Assert.Null(DevFileDeviceKey.PathFromEnvironment(Env("")));
        Assert.Throws<PlatformNotSupportedException>(() => DeviceKeyOpener.OpenDefault(Env(null), _folder));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void WithFlag_PermanentFile0600_StableAcrossOpens()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_folder, "keys", "device.key");
        byte[] spki;
        using (var key = DeviceKeyOpener.OpenDefault(Env(path), Path.Combine(_folder, "data")))
        {
            Assert.True(key.Created);
            Assert.Equal(DevFileDeviceKey.ProviderName, key.Provider);
            spki = key.Spki;
            Assert.True(PcIdDerivation.IsP256Spki(spki));
            var sig = key.SignP1363("x"u8);
            using var pub = ECDsa.Create();
            pub.ImportSubjectPublicKeyInfo(spki, out _);
            Assert.True(pub.VerifyData("x"u8, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        using (var again = DevFileDeviceKey.OpenOrCreate(path))
        {
            Assert.False(again.Created);
            Assert.Equal(spki, again.Spki);
        }
    }

    [Fact]
    public void WithFlag_RefusesOpenFile_RelativePath_AndDataFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_folder, "device.key");
        DevFileDeviceKey.OpenOrCreate(path).Dispose();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
#pragma warning disable CA1416 // non-Windows only (returned above); the analyzer does not follow the guard into lambdas
        Assert.Throws<UnauthorizedAccessException>(() => DevFileDeviceKey.OpenOrCreate(path));
        Assert.Throws<ArgumentException>(() => DevFileDeviceKey.PathFromEnvironment(Env("relative/device.key")));
        Assert.Throws<ArgumentException>(() => DevFileDeviceKey.OpenOrCreate(Path.Combine(_folder, "data", "device.key"), Path.Combine(_folder, "data")));
#pragma warning restore CA1416
    }

    [Fact]
    public void OnWindows_FileKeyIsRefusedEvenWithFlag()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
#pragma warning disable CA1416 // the point of the test: calling it on Windows must fail
        Assert.Throws<PlatformNotSupportedException>(() => DevFileDeviceKey.PathFromEnvironment(Env(Path.Combine(_folder, "device.key"))));
        Assert.Throws<PlatformNotSupportedException>(() => DevFileDeviceKey.OpenOrCreate(Path.Combine(_folder, "device.key")));
#pragma warning restore CA1416
        Assert.False(File.Exists(Path.Combine(_folder, "device.key")));
    }

    [Fact]
    public void WindowsBuilds_DoNotCompileTheFileKey()
    {
        var project = FindAgentProject();
        foreach (var rid in new[] { "win-x64", "win-arm64", "win-x86" })
        {
            Assert.DoesNotContain("NEXUS_DEV_FILE_KEY", DefineConstants(project, rid), StringComparison.Ordinal);
        }
        Assert.Contains("NEXUS_DEV_FILE_KEY", DefineConstants(project, null), StringComparison.Ordinal); // the check itself works
    }

    private static string DefineConstants(string project, string? rid)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "msbuild", project, "-nologo", "-getProperty:DefineConstants", "-p:Configuration=Release" })
        {
            start.ArgumentList.Add(arg);
        }
        if (rid is not null)
        {
            start.ArgumentList.Add("-p:RuntimeIdentifier=" + rid);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("RELEASE", output, StringComparison.Ordinal);
        return output;
    }

    private static string FindAgentProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "windows", "Nexus.Agent", "Nexus.Agent.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("windows/Nexus.Agent/Nexus.Agent.csproj not found above the test output.");
    }

    /// <summary>E4-3a tray text: the same keys in English and Russian, with the wording the plan asks for.</summary>
    [Fact]
    public void TrayStrings_KeyUnavailable_InEnglishAndRussian()
    {
        var tray = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(FindAgentProject())!)!, "Nexus.Tray");
        static Dictionary<string, string> Read(string path) =>
            System.Xml.Linq.XDocument.Load(path).Root!.Elements("data")
                .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value, StringComparer.Ordinal);
        var en = Read(Path.Combine(tray, "Strings.resx"));
        var ru = Read(Path.Combine(tray, "Strings.ru.resx"));
        Assert.Equal(en.Keys.Order(StringComparer.Ordinal), ru.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Ключ защиты ПК недоступен — подключение телефонов отключено", ru["KeyUnavailable"]);
        Assert.Equal("Повторить", ru["KeyRetry"]);
        Assert.Equal("Try again", en["KeyRetry"]);
        Assert.All(en.Values.Concat(ru.Values), v => Assert.DoesNotContain("pcId", v, StringComparison.Ordinal));
        Assert.True(en["KeyUnavailableTip"].Length <= 63 && ru["KeyUnavailableTip"].Length <= 63); // NotifyIcon.Text limit
    }
}
