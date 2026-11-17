using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Input;
using Nexus.Core.Protocol;
using Nexus.Core.Screen;
using Nexus.Core.Security;
using Xunit;

namespace Nexus.Tests;

public sealed class TrayScreenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-screen-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDesktop _desktop = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly TrayCommandHandler _handler;
    private readonly List<string?> _sharing = [];

    public TrayScreenTests()
    {
        _handler = new TrayCommandHandler(new UserAppStore(_dir), _desktop, _time, screenApprovals: new ScreenApprovals(_dir));
        _handler.ScreenSharingChanged += (_, viewer) => _sharing.Add(viewer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static JsonObject Start(string id = "d1") => new() { ["deviceId"] = id, ["deviceName"] = "iPhone" };

    [Fact]
    public async Task The_pc_owner_is_asked_once_per_device()
    {
        Assert.True((await _handler.HandleAsync("screen.start", Start(), default)).Ok);
        await _handler.HandleAsync("screen.stop", [], default);
        Assert.True((await _handler.HandleAsync("screen.start", Start(), default)).Ok);
        Assert.Equal(1, _desktop.ScreenQuestions);
        Assert.True((await _handler.HandleAsync("screen.start", Start("d2"), default)).Ok);
        Assert.Equal(2, _desktop.ScreenQuestions);
        // Remembered across restarts of the tray.
        var again = new TrayCommandHandler(new UserAppStore(_dir), _desktop, _time, screenApprovals: new ScreenApprovals(_dir));
        Assert.True((await again.HandleAsync("screen.start", Start(), default)).Ok);
        Assert.Equal(2, _desktop.ScreenQuestions);
    }

    [Fact]
    public async Task Refusal_on_the_pc_blocks_and_is_not_remembered()
    {
        _desktop.AllowScreen = false;
        Assert.Equal(ErrorCodes.ScreenDenied, (await _handler.HandleAsync("screen.start", Start(), default)).ErrorCode);
        Assert.Equal(ErrorCodes.ScreenNotStarted, (await _handler.HandleAsync("screen.frame", [], default)).ErrorCode);
        Assert.Equal(0, _desktop.Captures);
        _desktop.AllowScreen = true;
        Assert.True((await _handler.HandleAsync("screen.start", Start(), default)).Ok);
        Assert.Equal(2, _desktop.ScreenQuestions);
    }

    [Fact]
    public async Task Without_a_device_from_the_agent_nothing_is_shown()
    {
        Assert.Equal(ErrorCodes.ScreenDenied, (await _handler.HandleAsync("screen.start", [], default)).ErrorCode);
        Assert.Equal(0, _desktop.ScreenQuestions);
    }

    [Fact]
    public async Task Frames_flow_and_unchanged_pictures_are_not_resent()
    {
        await _handler.HandleAsync("screen.start", Start(), default);
        var first = await _handler.HandleAsync("screen.frame", new JsonObject { ["maxWidth"] = 960 }, default);
        Assert.True(first.Ok);
        Assert.NotNull(first.Result!["jpeg"]);
        Assert.Equal(960, first.Result["width"]!.GetValue<int>());
        var hash = first.Result["hash"]!.GetValue<string>();
        var second = await _handler.HandleAsync("screen.frame", new JsonObject { ["known"] = hash }, default);
        Assert.True(second.Result!["unchanged"]!.GetValue<bool>());
        Assert.Null(second.Result["jpeg"]);
        Assert.Equal(["iPhone"], _sharing);
    }

    [Fact]
    public async Task Stop_in_the_tray_ends_the_session_and_refuses_new_ones()
    {
        await _handler.HandleAsync("screen.start", Start(), default);
        _handler.ScreenPaused = true;
        Assert.Equal(["iPhone", null], _sharing);
        Assert.Equal(ErrorCodes.ScreenNotStarted, (await _handler.HandleAsync("screen.frame", [], default)).ErrorCode);
        Assert.Equal(ErrorCodes.ScreenBlocked, (await _handler.HandleAsync("screen.start", Start(), default)).ErrorCode);
        _handler.ScreenPaused = false;
        Assert.True((await _handler.HandleAsync("screen.start", Start(), default)).Ok);
    }

    [Fact]
    public async Task An_abandoned_session_ends_by_itself()
    {
        await _handler.HandleAsync("screen.start", Start(), default);
        _time.Now = _time.Now.AddSeconds(5);
        _handler.ReleaseIdleButtons();
        Assert.Single(_sharing);
        _time.Now = _time.Now.AddSeconds(11);
        _handler.ReleaseIdleButtons();
        Assert.Equal(["iPhone", null], _sharing);
    }

    [Fact]
    public void Absolute_move_uses_the_absolute_flag()
    {
        Assert.Equal([InputStep.Mouse(InputFlags.MouseMove | InputFlags.Absolute, 32768, 100)], InputPlanner.For(new InputMoveToPayload(32768, 100)));
    }
}

public sealed class ScreenProtocolTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Screen_needs_its_permission_and_the_agent_names_the_device()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("screen.start")).ErrorCode());

        _host.Services.Devices.SetPermissions(deviceId, Permissions.DefaultAfterPairing | Permission.Screen | Permission.Input);
        var start = await client.RequestAsync("screen.start");
        Assert.True(start.IsOk(), start.ToJsonString());
        Assert.Equal(1, tray.Desktop.ScreenQuestions);
        var frame = await client.RequestAsync("screen.frame", new JsonObject { ["maxWidth"] = 1280, ["quality"] = 60 });
        Assert.True(frame.IsOk(), frame.ToJsonString());
        Assert.Equal(1280, frame["payload"]!["width"]!.GetValue<int>());
        Assert.True((await client.RequestAsync("input.moveTo", new JsonObject { ["x"] = 100, ["y"] = 200 })).IsOk());
        Assert.Contains(tray.Desktop.Input, s => s.Flags == (InputFlags.MouseMove | InputFlags.Absolute) && s.Dx == 100 && s.Dy == 200);
        Assert.True((await client.RequestAsync("screen.stop")).IsOk());

        var titles = _host.Services.History.List(20, null).Select(e => e.Title).ToList();
        Assert.Contains("Телефон начал показ экрана ПК", titles);
        Assert.DoesNotContain(titles, t => t.Contains("frame", StringComparison.Ordinal));
    }
}
