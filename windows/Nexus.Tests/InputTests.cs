using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Input;
using Nexus.Core.Protocol;
using Xunit;

namespace Nexus.Tests;

public sealed class InputPlannerTests
{
    private static object Parse(string type, string json)
    {
        Assert.True(CommandCatalog.V1.TryGet(type, out var spec));
        var result = spec.ParsePayload(JsonDocument.Parse(json).RootElement);
        Assert.True(result.Error is null, result.Error);
        return result.Value!;
    }

    [Fact]
    public void Move_is_one_relative_mouse_step()
    {
        var steps = InputPlanner.For(Parse("input.move", """{"dx":12,"dy":-7}"""));
        Assert.Equal([InputStep.Mouse(InputFlags.MouseMove, 12, -7)], steps);
    }

    [Theory]
    [InlineData("left", "click", new uint[] { 0x2, 0x4 })]
    [InlineData("right", "click", new uint[] { 0x8, 0x10 })]
    [InlineData("middle", "click", new uint[] { 0x20, 0x40 })]
    [InlineData("left", "double", new uint[] { 0x2, 0x4, 0x2, 0x4 })]
    [InlineData("left", "down", new uint[] { 0x2 })]
    [InlineData("left", "up", new uint[] { 0x4 })]
    public void Buttons(string button, string action, uint[] flags)
    {
        var steps = InputPlanner.For(Parse("input.button", $$"""{"button":"{{button}}","action":"{{action}}"}"""));
        Assert.Equal(flags, steps.Select(s => s.Flags));
        Assert.All(steps, s => Assert.True(s.IsMouse));
    }

    [Fact]
    public void Scroll_uses_vertical_and_horizontal_wheels()
    {
        var steps = InputPlanner.For(Parse("input.scroll", """{"dx":60,"dy":-240}"""));
        Assert.Equal([InputStep.Mouse(InputFlags.Wheel, data: -240), InputStep.Mouse(InputFlags.HWheel, data: 60)], steps);
    }

    [Fact]
    public void Text_is_unicode_with_enter_and_tab_as_keys()
    {
        var steps = InputPlanner.For(Parse("input.text", "{\"text\":\"Я\\t1\\r\\n😀\"}"));
        var expected = new List<InputStep>
        {
            InputStep.Unicode('Я', false), InputStep.Unicode('Я', true),
            InputStep.Key(0x09, 0), InputStep.Key(0x09, InputFlags.KeyUp),
            InputStep.Unicode('1', false), InputStep.Unicode('1', true),
            InputStep.Key(0x0D, 0), InputStep.Key(0x0D, InputFlags.KeyUp),
            InputStep.Unicode('\uD83D', false), InputStep.Unicode('\uD83D', true),
            InputStep.Unicode('\uDE00', false), InputStep.Unicode('\uDE00', true),
        };
        Assert.Equal(expected, steps);
    }

    [Fact]
    public void Shortcut_presses_modifiers_first_and_releases_everything_in_reverse()
    {
        var steps = InputPlanner.For(Parse("input.key", """{"key":"c","modifiers":["ctrl","shift"]}"""));
        Assert.Equal(
        [
            InputStep.Key(0xA2, 0), InputStep.Key(0xA0, 0),
            InputStep.Key(0x43, 0), InputStep.Key(0x43, InputFlags.KeyUp),
            InputStep.Key(0xA0, InputFlags.KeyUp), InputStep.Key(0xA2, InputFlags.KeyUp),
        ], steps);
    }

    [Fact]
    public void Navigation_and_win_keys_are_extended()
    {
        var steps = InputPlanner.For(Parse("input.key", """{"key":"d","modifiers":["win"]}"""));
        Assert.Equal(InputStep.Key(0x5B, InputFlags.KeyExtended), steps[0]);
        Assert.Equal(InputStep.Key(0x5B, InputFlags.KeyExtended | InputFlags.KeyUp), steps[^1]);
        var arrow = InputPlanner.For(Parse("input.key", """{"key":"left"}"""));
        Assert.Equal([InputStep.Key(0x25, InputFlags.KeyExtended), InputStep.Key(0x25, InputFlags.KeyExtended | InputFlags.KeyUp)], arrow);
        Assert.Equal((ushort)0x7B, InputPlanner.For(Parse("input.key", """{"key":"f12"}"""))[0].VirtualKey);
    }

    [Fact]
    public void Every_key_press_is_released_in_the_same_batch()
    {
        foreach (var key in InputKeys.Keys.Keys)
        {
            var steps = InputPlanner.For(new InputKeyPayload(key, ["ctrl", "alt", "shift", "win"]));
            var down = steps.Where(s => (s.Flags & InputFlags.KeyUp) == 0).Select(s => s.VirtualKey).Order();
            var up = steps.Where(s => (s.Flags & InputFlags.KeyUp) != 0).Select(s => s.VirtualKey).Order();
            Assert.Equal(down, up);
        }
    }

    [Theory]
    [InlineData("playPause", 0xB3)]
    [InlineData("volumeUp", 0xAF)]
    [InlineData("mute", 0xAD)]
    public void Media_keys(string action, int vk)
    {
        var steps = InputPlanner.For(Parse("input.media", $$"""{"action":"{{action}}"}"""));
        Assert.Equal([InputStep.Key((ushort)vk, InputFlags.KeyExtended), InputStep.Key((ushort)vk, InputFlags.KeyExtended | InputFlags.KeyUp)], steps);
    }

    [Theory]
    [InlineData("input.move", """{"dx":1}""")]
    [InlineData("input.move", """{"dx":1,"dy":2,"x":3}""")]
    [InlineData("input.move", """{"dx":1.5,"dy":2}""")]
    [InlineData("input.scroll", """{"dx":0,"dy":0}""")]
    [InlineData("input.scroll", """{"dy":2401}""")]
    [InlineData("input.text", """{"text":""}""")]
    [InlineData("input.key", """{"key":"C"}""")]
    [InlineData("input.key", """{"key":"c","modifiers":["ctrl","ctrl"]}""")]
    [InlineData("input.key", """{"key":"c","modifiers":"ctrl"}""")]
    [InlineData("input.media", """{"action":"power"}""")]
    public void Rejects(string type, string json)
    {
        Assert.True(CommandCatalog.V1.TryGet(type, out var spec));
        Assert.NotNull(spec.ParsePayload(JsonDocument.Parse(json).RootElement).Error);
    }

    [Fact]
    public void Text_longer_than_the_limit_is_rejected()
    {
        Assert.True(CommandCatalog.V1.TryGet("input.text", out var spec));
        var json = JsonSerializer.Serialize(new { text = new string('a', InputKeys.MaxTextLength + 1) });
        Assert.NotNull(spec.ParsePayload(JsonDocument.Parse(json).RootElement).Error);
    }
}

public sealed class HeldButtonsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_dragged_button_is_released_after_the_phone_goes_quiet()
    {
        var held = new HeldButtons();
        held.Observe(new InputButtonPayload(MouseButton.Left, ButtonAction.Down), T0);
        held.Observe(new InputMovePayload(5, 5), T0.AddSeconds(3));
        Assert.Empty(held.ReleaseIfIdle(T0.AddSeconds(7)));   // still moving 4 s ago
        Assert.Equal([InputStep.Mouse(InputFlags.LeftUp)], held.ReleaseIfIdle(T0.AddSeconds(8)));
        Assert.Empty(held.ReleaseIfIdle(T0.AddSeconds(20)));  // released only once
    }

    [Fact]
    public void Nothing_to_release_after_a_normal_up()
    {
        var held = new HeldButtons();
        held.Observe(new InputButtonPayload(MouseButton.Right, ButtonAction.Down), T0);
        held.Observe(new InputButtonPayload(MouseButton.Right, ButtonAction.Up), T0.AddSeconds(1));
        Assert.Empty(held.ReleaseIfIdle(T0.AddMinutes(1)));
    }
}

public sealed class TrayInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-trayinput-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDesktop _desktop = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly TrayCommandHandler _handler;
    private readonly List<string> _notices = [];

    public TrayInputTests()
    {
        _handler = new TrayCommandHandler(new UserAppStore(_dir), _desktop, _time);
        _handler.ActionPerformed += (_, text) => _notices.Add(text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Input_reaches_the_desktop_and_the_pc_owner_is_told_once()
    {
        Assert.True((await _handler.HandleAsync("input.move", new JsonObject { ["dx"] = 3, ["dy"] = 4 }, default)).Ok);
        _time.Now = _time.Now.AddSeconds(30);
        Assert.True((await _handler.HandleAsync("input.button", new JsonObject { ["button"] = "left", ["action"] = "click" }, default)).Ok);
        Assert.Equal(3, _desktop.Input.Count);
        Assert.Single(_notices);

        _time.Now = _time.Now.AddMinutes(3); // quiet for a while: tell again
        await _handler.HandleAsync("input.media", new JsonObject { ["action"] = "mute" }, default);
        Assert.Equal(2, _notices.Count);
    }

    [Fact]
    public async Task Tray_rejects_values_the_agent_should_have_stopped()
    {
        var result = await _handler.HandleAsync("input.key", new JsonObject { ["key"] = "capsLock" }, default);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidPayload, result.ErrorCode);
        Assert.Empty(_desktop.Input);
        Assert.False((await _handler.HandleAsync("input.everything", [], default)).Ok);
    }

    [Fact]
    public async Task Stop_in_the_tray_blocks_phone_input_until_allowed_again()
    {
        _handler.InputPaused = true;
        var result = await _handler.HandleAsync("input.move", new JsonObject { ["dx"] = 1, ["dy"] = 1 }, default);
        Assert.Equal(ErrorCodes.InputBlocked, result.ErrorCode);
        Assert.Empty(_desktop.Input);
        _handler.InputPaused = false;
        Assert.True((await _handler.HandleAsync("input.move", new JsonObject { ["dx"] = 1, ["dy"] = 1 }, default)).Ok);
    }

    [Fact]
    public async Task Locked_pc_reports_input_blocked()
    {
        _desktop.InputBlocked = true;
        var result = await _handler.HandleAsync("input.text", new JsonObject { ["text"] = "hi" }, default);
        Assert.Equal(ErrorCodes.InputBlocked, result.ErrorCode);
    }

    [Fact]
    public async Task Idle_drag_is_released_by_the_tray_timer()
    {
        await _handler.HandleAsync("input.button", new JsonObject { ["button"] = "left", ["action"] = "down" }, default);
        _handler.ReleaseIdleButtons();
        Assert.Single(_desktop.Input);
        _time.Now = _time.Now.AddSeconds(6);
        _handler.ReleaseIdleButtons();
        Assert.Equal(InputStep.Mouse(InputFlags.LeftUp), _desktop.Input.Last());
    }
}

public sealed class InputProtocolTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Input_is_off_by_default_and_works_once_granted()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        var denied = await client.RequestAsync("input.move", new JsonObject { ["dx"] = 1, ["dy"] = 1 });
        Assert.Equal(ErrorCodes.PermissionDenied, denied.ErrorCode());
        Assert.Empty(tray.Desktop.Input);

        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permissions.DefaultAfterPairing | Nexus.Core.Security.Permission.Input);
        for (var i = 0; i < 5; i++)
        {
            var moved = await client.RequestAsync("input.move", new JsonObject { ["dx"] = 10, ["dy"] = -i });
            Assert.True(moved.IsOk(), moved.ToJsonString());
        }
        Assert.Equal(5, tray.Desktop.Input.Count);

        // Five successful moves in a minute: one audit line, not five.
        var audit = await File.ReadAllLinesAsync(_host.Services.Audit.FilePath);
        Assert.Single(audit, line => line.Contains("\"input.move\"", StringComparison.Ordinal) && line.Contains("\"ok\":true", StringComparison.Ordinal));
        Assert.Single(audit, line => line.Contains("\"input.move\"", StringComparison.Ordinal) && line.Contains("permissionDenied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_tray_input_is_unavailable()
    {
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;
        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permission.Input);
        var result = await client.RequestAsync("input.key", new JsonObject { ["key"] = "enter" });
        Assert.Equal(ErrorCodes.SessionUnavailable, result.ErrorCode());
    }
}
