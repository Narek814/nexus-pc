using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Power;
using Nexus.Core.Protocol;
using Nexus.Core.Session;

namespace Nexus.Tests;

internal sealed class FakeDesktop : IDesktop
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public HashSet<string> FailingApps { get; } = [];

    public DesktopResult Launch(AppEntry app)
    {
        Calls.Enqueue("launch:" + app.Id);
        return FailingApps.Contains(app.Id) ? new DesktopResult(false, "boom") : DesktopResult.Success;
    }

    public DesktopResult RequestClose(AppEntry app)
    {
        Calls.Enqueue("close:" + app.Id);
        return new DesktopResult(true, "closed");
    }

    public DesktopResult Lock()
    {
        Calls.Enqueue("lock");
        return DesktopResult.Success;
    }

    public PowerOutcome SchedulePower(PowerAction action, TimeSpan delay, bool force, string reason)
    {
        Calls.Enqueue($"power:{action}:{(int)delay.TotalSeconds}:{force}");
        return PowerOutcome.Success;
    }

    public ConcurrentQueue<Nexus.Core.Input.InputStep> Input { get; } = new();

    public bool InputBlocked { get; set; }

    public string? Clipboard { get; set; }

    public bool AllowScreen { get; set; } = true;

    public int ScreenQuestions { get; private set; }

    public int Captures { get; private set; }

    public bool ConfirmScreenShare(string deviceName)
    {
        ScreenQuestions++;
        return AllowScreen;
    }

    public Nexus.Core.Screen.ScreenCapture CaptureScreen(int maxWidth, int quality, string? knownHash)
    {
        Captures++;
        const string hash = "0000000000000000000000000000000000000000000000000000000000000001";
        return knownHash == hash
            ? new(true, null, maxWidth, maxWidth * 9 / 16, hash, Unchanged: true)
            : new(true, [0xFF, 0xD8, 0xFF, 0xD9], maxWidth, maxWidth * 9 / 16, hash);
    }

    public ClipboardRead GetClipboardText() => new(true, Clipboard);

    public DesktopResult SetClipboardText(string text)
    {
        Clipboard = text;
        return DesktopResult.Success;
    }

    public DesktopResult SendInput(IReadOnlyList<Nexus.Core.Input.InputStep> steps)
    {
        if (InputBlocked)
        {
            return new DesktopResult(false, "locked");
        }
        foreach (var step in steps)
        {
            Input.Enqueue(step);
        }
        return DesktopResult.Success;
    }
}

internal static class PipePair
{
    public static async Task<(Stream Server, Stream Client)> CreateAsync()
    {
        var name = "nexus-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
        return (server, client);
    }
}

public class JsonLineChannelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Calls_work_in_both_directions_at_once()
    {
        var (s, c) = await PipePair.CreateAsync();
        await using var a = new JsonLineChannel(s);
        await using var b = new JsonLineChannel(c);
        using var stop = new CancellationTokenSource();

        var runA = a.RunAsync((op, args, _) => Task.FromResult(RpcResult.Success(new JsonObject { ["from"] = "a", ["op"] = op })), stop.Token);
        var runB = b.RunAsync(async (op, args, ct) =>
        {
            await Task.Delay(50, ct);
            return RpcResult.Success(new JsonObject { ["from"] = "b", ["echo"] = args["x"]?.GetValue<int>() });
        }, stop.Token);

        var calls = Enumerable.Range(0, 20).Select(i => a.CallAsync("ping", new JsonObject { ["x"] = i }, Timeout, CancellationToken.None)).ToList();
        var reverse = await b.CallAsync("hello", null, Timeout, CancellationToken.None);

        var results = await Task.WhenAll(calls);
        Assert.Equal(Enumerable.Range(0, 20), results.Select(r => r.Result!["echo"]!.GetValue<int>()));
        Assert.Equal("a", reverse.Result!["from"]!.GetValue<string>());
        Assert.Equal("hello", reverse.Result!["op"]!.GetValue<string>());
        await stop.CancelAsync();
    }

    [Fact]
    public async Task Results_can_be_embedded_in_other_json()
    {
        // Regression: results used to stay attached to the parsed message ("node already has a parent").
        var (s, c) = await PipePair.CreateAsync();
        await using var a = new JsonLineChannel(s);
        await using var b = new JsonLineChannel(c);
        _ = b.RunAsync((_, args, _) => Task.FromResult(RpcResult.Success(new JsonObject { ["echo"] = args })), CancellationToken.None);
        _ = a.RunAsync((_, _, _) => Task.FromResult(RpcResult.Success()), CancellationToken.None);

        var result = await a.CallAsync("x", new JsonObject { ["k"] = 1 }, Timeout, CancellationToken.None);
        var outer = new JsonObject { ["payload"] = result.Result };
        Assert.Equal(1, outer["payload"]!["echo"]!["k"]!.GetValue<int>());
    }

    [Fact]
    public async Task Handler_exception_becomes_an_error_reply()
    {
        var (s, c) = await PipePair.CreateAsync();
        await using var a = new JsonLineChannel(s);
        await using var b = new JsonLineChannel(c);
        _ = b.RunAsync((_, _, _) => throw new InvalidOperationException("secret detail"), CancellationToken.None);
        _ = a.RunAsync((_, _, _) => Task.FromResult(RpcResult.Success()), CancellationToken.None);

        var result = await a.CallAsync("x", null, Timeout, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("internal", result.ErrorCode);
        Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_call_fails_when_the_other_side_disconnects()
    {
        var (s, c) = await PipePair.CreateAsync();
        await using var a = new JsonLineChannel(s);
        var b = new JsonLineChannel(c);
        var neverAnswers = new TaskCompletionSource<RpcResult>();
        _ = b.RunAsync((_, _, _) => neverAnswers.Task, CancellationToken.None);
        var runA = a.RunAsync((_, _, _) => Task.FromResult(RpcResult.Success()), CancellationToken.None);

        var call = a.CallAsync("slow", null, Timeout, CancellationToken.None);
        await Task.Delay(100);
        await b.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => call);
        neverAnswers.SetResult(RpcResult.Success());
    }

    [Fact]
    public async Task Dispose_during_traffic_does_not_throw()
    {
        // Regression (windows-latest): disposing while replies were being written threw
        // "The stream is currently in use by a previous operation".
        for (var round = 0; round < 20; round++)
        {
            var (s, c) = await PipePair.CreateAsync();
            var a = new JsonLineChannel(s);
            await using var b = new JsonLineChannel(c);
            _ = b.RunAsync((_, _, _) => Task.FromResult(RpcResult.Success()), CancellationToken.None);
            var run = a.RunAsync((_, args, _) => Task.FromResult(RpcResult.Success(args)), CancellationToken.None);
            var calls = Enumerable.Range(0, 50).Select(_ => b.CallAsync("x", new JsonObject { ["pad"] = new string('x', 2000) }, Timeout, CancellationToken.None)).ToList();
            await a.DisposeAsync();
            await a.DisposeAsync(); // idempotent
            foreach (var call in calls)
            {
                try
                {
                    await call;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
                {
                }
            }
            try
            {
                await run;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Call_times_out()
    {
        var (s, c) = await PipePair.CreateAsync();
        await using var a = new JsonLineChannel(s);
        await using var b = new JsonLineChannel(c);
        var neverAnswers = new TaskCompletionSource<RpcResult>();
        _ = b.RunAsync((_, _, _) => neverAnswers.Task, CancellationToken.None);
        _ = a.RunAsync((_, _, _) => Task.FromResult(RpcResult.Success()), CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => a.CallAsync("slow", null, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        neverAnswers.SetResult(RpcResult.Success());
    }
}

public sealed class UserAppStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-apps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Apps_and_profiles_persist_and_ids_come_from_names()
    {
        var store = new UserAppStore(_dir);
        var steam = store.AddApp("Steam", AppLaunchKind.Executable, @"C:\Steam\steam.exe", true, @"C:\Steam\steam.exe");
        var steam2 = store.AddApp("Steam", AppLaunchKind.Shortcut, @"C:\x\Steam.lnk", false, null);
        var telegram = store.AddApp("Телеграм", AppLaunchKind.Executable, @"C:\t.exe", false, null);
        Assert.Equal(("steam", "steam-2", "telegram"), (steam.Id, steam2.Id, telegram.Id));
        Assert.Empty(store.UpsertProfile(new ProfileDefinition("gaming", "Gaming", ["steam", "telegram"])));

        var reloaded = new UserAppStore(_dir);
        Assert.Equal(3, reloaded.Apps.Count);
        Assert.Equal(AppLaunchKind.Shortcut, reloaded.FindApp("steam-2")!.Kind);
        Assert.Equal(["steam", "telegram"], reloaded.FindProfile("gaming")!.AppIds);
    }

    [Fact]
    public void Profile_with_unknown_apps_is_rejected_and_not_saved()
    {
        var store = new UserAppStore(_dir);
        store.AddApp("Steam", AppLaunchKind.Executable, "steam.exe", false, null);
        Assert.Equal(["cmd"], store.UpsertProfile(new ProfileDefinition("evil", "Evil", ["steam", "cmd"])));
        Assert.Null(store.FindProfile("evil"));
    }

    [Fact]
    public void Removing_an_app_removes_it_from_profiles()
    {
        var store = new UserAppStore(_dir);
        store.AddApp("Steam", AppLaunchKind.Executable, "steam.exe", false, null);
        store.AddApp("Discord", AppLaunchKind.Executable, "discord.exe", false, null);
        store.UpsertProfile(new ProfileDefinition("gaming", "Gaming", ["steam", "discord"]));

        Assert.True(store.RemoveApp("discord"));
        Assert.Equal(["steam"], store.FindProfile("gaming")!.AppIds);
        Assert.Equal(["steam"], new UserAppStore(_dir).FindProfile("gaming")!.AppIds);
    }

    [Fact]
    public void Profile_limit_is_enforced()
    {
        var store = new UserAppStore(_dir);
        for (var i = 0; i < ProfileRules.MaxProfiles; i++)
        {
            store.UpsertProfile(new ProfileDefinition($"p{i}", $"P{i}", []));
        }
        Assert.Throws<InvalidOperationException>(() => store.UpsertProfile(new ProfileDefinition("one-more", "x", [])));
        Assert.Empty(store.UpsertProfile(new ProfileDefinition("p0", "Renamed", []))); // replacing is fine
    }

    [Theory]
    [InlineData("Visual Studio Code", "visual-studio-code")]
    [InlineData("OBS Studio 30.1", "obs-studio-30-1")]
    [InlineData("Ночной режим", "nochnoy-rezhim")]
    [InlineData("🎮🎮", "app")]
    [InlineData("  --Steam--  ", "steam")]
    public void Slugify(string name, string expected)
    {
        var slug = Identifiers.Slugify(name, []);
        Assert.Equal(expected, slug);
        Assert.True(Identifiers.IsSlug(slug));
    }
}

public sealed class TrayCommandHandlerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-tray-" + Guid.NewGuid().ToString("N"));
    private readonly UserAppStore _store;
    private readonly FakeDesktop _desktop = new();
    private readonly TrayCommandHandler _handler;

    public TrayCommandHandlerTests()
    {
        _store = new UserAppStore(_dir);
        _store.AddApp("Steam", AppLaunchKind.Executable, @"C:\Program Files (x86)\Steam\steam.exe", true, @"C:\Program Files (x86)\Steam\steam.exe");
        _store.AddApp("Discord", AppLaunchKind.Shortcut, @"C:\Users\me\Discord.lnk", true, null);
        _handler = new TrayCommandHandler(_store, _desktop);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private Task<RpcResult> Call(string op, JsonObject? args = null) => _handler.HandleAsync(op, args ?? [], CancellationToken.None);

    [Fact]
    public async Task App_list_never_contains_local_paths()
    {
        var result = await Call(SessionOps.AppsList);
        var json = result.Result!.ToJsonString();
        Assert.Contains("\"id\":\"steam\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Program Files", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".lnk", json, StringComparison.Ordinal);
        // Discord has no process path, so it cannot be closed even though closing was allowed.
        Assert.False(result.Result!["apps"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "discord")!["canClose"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Launch_only_known_apps()
    {
        Assert.True((await Call(SessionOps.AppsLaunch, new JsonObject { ["appId"] = "steam" })).Ok);
        Assert.Equal(ErrorCodes.AppNotFound, (await Call(SessionOps.AppsLaunch, new JsonObject { ["appId"] = "cmd" })).ErrorCode);
        Assert.Equal(ErrorCodes.AppNotFound, (await Call(SessionOps.AppsLaunch, new JsonObject { ["appId"] = @"C:\Windows\System32\cmd.exe" })).ErrorCode);
        Assert.Equal(["launch:steam"], _desktop.Calls);
    }

    [Fact]
    public async Task Close_requires_permission_and_a_process_path()
    {
        Assert.True((await Call(SessionOps.AppsClose, new JsonObject { ["appId"] = "steam" })).Ok);
        Assert.Equal(ErrorCodes.AppCloseNotAllowed, (await Call(SessionOps.AppsClose, new JsonObject { ["appId"] = "discord" })).ErrorCode);
        Assert.Equal(["close:steam"], _desktop.Calls);
    }

    [Fact]
    public async Task Profile_run_reports_each_app()
    {
        _store.UpsertProfile(new ProfileDefinition("gaming", "Gaming", ["steam", "discord"]));
        _desktop.FailingApps.Add("discord");

        var result = await Call(SessionOps.ProfilesRun, new JsonObject { ["profileId"] = "gaming" });

        Assert.True(result.Ok);
        Assert.Equal(1, result.Result!["started"]!.GetValue<int>());
        var items = result.Result!["results"]!.AsArray();
        Assert.True(items[0]!["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.AppLaunchFailed, items[1]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Profile_upsert_from_phone_cannot_reference_unknown_apps()
    {
        var bad = await Call(SessionOps.ProfilesUpsert, new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = "x", ["name"] = "X", ["appIds"] = new JsonArray("steam", "powershell") },
        });
        Assert.Equal(ErrorCodes.ProfileInvalid, bad.ErrorCode);

        var good = await Call(SessionOps.ProfilesUpsert, new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = "dev", ["name"] = "Developer", ["appIds"] = new JsonArray("steam") },
        });
        Assert.True(good.Ok);
        Assert.True((await Call(SessionOps.ProfilesDelete, new JsonObject { ["profileId"] = "dev" })).Ok);
        Assert.Equal(ErrorCodes.ProfileNotFound, (await Call(SessionOps.ProfilesDelete, new JsonObject { ["profileId"] = "dev" })).ErrorCode);
    }

    [Fact]
    public async Task Lock_and_power_reach_the_desktop()
    {
        Assert.True((await Call(SessionOps.Lock)).Ok);
        Assert.True((await Call(SessionOps.PowerSchedule, new JsonObject { ["action"] = "restart", ["delaySeconds"] = 60, ["force"] = false, ["reason"] = "r" })).Ok);
        Assert.Equal(ErrorCodes.InvalidPayload, (await Call(SessionOps.PowerSchedule, new JsonObject { ["action"] = "format-c", ["delaySeconds"] = 1 })).ErrorCode);
        Assert.Equal(["lock", "power:Restart:60:False"], _desktop.Calls);
    }

    [Fact]
    public async Task Unknown_operation_is_refused()
    {
        Assert.Equal(ErrorCodes.UnknownCommand, (await Call("shell.exec", new JsonObject { ["cmd"] = "calc" })).ErrorCode);
        Assert.Empty(_desktop.Calls);
    }
}
