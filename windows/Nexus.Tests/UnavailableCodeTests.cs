using System.Text.Json;
using Nexus.Agent.Server;
using Nexus.Monitoring;

namespace Nexus.Tests;

/// <summary>
/// W-PROTO: metrics.snapshot "unavailableCodes" next to "unavailable", rgb.status "reasonCode", and the
/// response fixtures that show them. Real sensor reads (NVML, PawnIO) are only exercised on Windows CI.
/// </summary>
public sealed class UnavailableCodeTests
{
    public static TheoryData<string, string, string> Sources() => new()
    {
        { nameof(MetricsReasons.NoTemperatureSensor), UnavailableCodes.SensorMissing, "Нет датчика температуры процессора." },
        { nameof(MetricsReasons.NoClockData), UnavailableCodes.SensorMissing, "Нет данных о частоте процессора." },
        { nameof(MetricsReasons.FirstSample), UnavailableCodes.WarmingUp, "Первое измерение, загрузка появится через пару секунд." },
        { nameof(MetricsReasons.MemoryQueryFailed), UnavailableCodes.OsQueryFailed, "GlobalMemoryStatusEx не ответил." },
        { nameof(MetricsReasons.WindowsOnly), UnavailableCodes.PlatformUnsupported, "Доступно только на Windows." },
        { nameof(MetricsReasons.SensorDriverMissing), UnavailableCodes.SensorDriverMissing,
          "Нет значения от датчика процессора: нужен драйвер PawnIO (ставится вместе с OpenRGB или с pawnio.eu), затем перезапустите ПК." },
        { nameof(MetricsReasons.SensorReadFailed), UnavailableCodes.SensorReadFailed, "Ошибка чтения датчиков процессора: boom" },
        { nameof(MetricsReasons.SensorOpenFailed), UnavailableCodes.SensorReadFailed, "Не удалось открыть датчики процессора: boom" },
        { nameof(NvmlLocation.MissingReason), UnavailableCodes.GpuDriverMissing, NvmlLocation.Missing },
        { nameof(NvmlLocation.RejectedReason), UnavailableCodes.GpuLibraryRejected, NvmlLocation.Rejected },
        { nameof(NvmlLocation.LoadFailedReason), UnavailableCodes.GpuLibraryLoadFailed, NvmlLocation.LoadFailed },
    };

    private static UnavailableReason Get(string name) => name switch
    {
        nameof(MetricsReasons.NoTemperatureSensor) => MetricsReasons.NoTemperatureSensor,
        nameof(MetricsReasons.NoClockData) => MetricsReasons.NoClockData,
        nameof(MetricsReasons.FirstSample) => MetricsReasons.FirstSample,
        nameof(MetricsReasons.MemoryQueryFailed) => MetricsReasons.MemoryQueryFailed,
        nameof(MetricsReasons.WindowsOnly) => MetricsReasons.WindowsOnly,
        nameof(MetricsReasons.SensorDriverMissing) => MetricsReasons.SensorDriverMissing,
        nameof(MetricsReasons.SensorReadFailed) => MetricsReasons.SensorReadFailed("boom"),
        nameof(MetricsReasons.SensorOpenFailed) => MetricsReasons.SensorOpenFailed("boom"),
        nameof(NvmlLocation.MissingReason) => NvmlLocation.MissingReason,
        nameof(NvmlLocation.RejectedReason) => NvmlLocation.RejectedReason,
        nameof(NvmlLocation.LoadFailedReason) => NvmlLocation.LoadFailedReason,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void Each_source_gives_a_listed_code_and_its_unchanged_text(string source, string code, string text)
    {
        var reason = Get(source);
        Assert.Equal(code, reason.Code);
        Assert.Equal(text, reason.Text);
        Assert.Contains(reason.Code, UnavailableCodes.All);
    }

    [Fact]
    public void The_code_list_is_the_closed_list_of_the_task()
    {
        Assert.Equal(
            ["gpuDriverMissing", "gpuInitFailed", "gpuLibraryLoadFailed", "gpuLibraryRejected", "gpuListFailed", "osQueryFailed",
             "platformUnsupported", "sensorDriverMissing", "sensorMissing", "sensorReadFailed", "warmingUp"],
            UnavailableCodes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Portable_source_gives_a_code_for_every_unavailable_key()
    {
        var snapshot = new PortableMetricsSource().Sample(DateTimeOffset.UtcNow);
        Assert.Equal(["cpu.clock", "cpu.temperature", "cpu.usage", "gpu", "memory"], snapshot.Unavailable.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(snapshot.Unavailable.Keys.Order(StringComparer.Ordinal), snapshot.UnavailableCodes.Keys.Order(StringComparer.Ordinal));
        Assert.All(snapshot.UnavailableCodes.Values, c => Assert.Equal(UnavailableCodes.PlatformUnsupported, c));
        Assert.All(snapshot.Unavailable.Values, t => Assert.Equal("Доступно только на Windows.", t));
    }

    [Fact]
    public void Windows_source_gives_a_code_for_every_unavailable_key()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        using var source = new WindowsMetricsSource();
        var snapshot = source.Sample(DateTimeOffset.UtcNow);
        Assert.Equal(snapshot.Unavailable.Keys.Order(StringComparer.Ordinal), snapshot.UnavailableCodes.Keys.Order(StringComparer.Ordinal));
        Assert.All(snapshot.UnavailableCodes.Values, c => Assert.Contains(c, UnavailableCodes.All));
        Assert.Equal(UnavailableCodes.WarmingUp, snapshot.UnavailableCodes["cpu.usage"]); // first sample
    }

    [Fact]
    public void Snapshot_json_has_unavailable_codes_with_the_same_keys()
    {
        var set = new UnavailableSet();
        set.Add("cpu.temperature", MetricsReasons.SensorDriverMissing);
        set.Add("gpu", NvmlLocation.MissingReason);
        var json = MetricsJson.Snapshot(new MetricsSnapshot { Timestamp = DateTimeOffset.UnixEpoch }.WithUnavailable(set));

        var texts = json["unavailable"]!.AsObject();
        var codes = json["unavailableCodes"]!.AsObject();
        Assert.Equal(MetricsReasons.SensorDriverMissing.Text, texts["cpu.temperature"]!.GetValue<string>());
        Assert.Equal(NvmlLocation.Missing, texts["gpu"]!.GetValue<string>());
        Assert.Equal("sensorDriverMissing", codes["cpu.temperature"]!.GetValue<string>());
        Assert.Equal("gpuDriverMissing", codes["gpu"]!.GetValue<string>());
        Assert.Equal(texts.Select(kv => kv.Key), codes.Select(kv => kv.Key));

        var empty = MetricsJson.Snapshot(new MetricsSnapshot { Timestamp = DateTimeOffset.UnixEpoch });
        Assert.Empty(empty["unavailableCodes"]!.AsObject());
    }

    // ---- shared/protocol/fixtures/responses.v1.json ----

    private static JsonElement Payload(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("message").GetProperty("payload");

    [Fact]
    public void Metrics_fixture_has_a_listed_code_for_every_unavailable_key_and_matches_agent_output()
    {
        using var doc = Shared.Load("fixtures/responses.v1.json");
        var payload = Payload(doc, "metrics.snapshot with unavailable and unavailableCodes");
        var texts = payload.GetProperty("unavailable").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var codes = payload.GetProperty("unavailableCodes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.NotEmpty(texts);
        Assert.Equal(texts.Keys.Order(StringComparer.Ordinal), codes.Keys.Order(StringComparer.Ordinal));
        Assert.All(codes.Values, c => Assert.Contains(c, UnavailableCodes.All));

        // The fixture's field set is what the agent writes.
        var agent = MetricsJson.Snapshot(new PortableMetricsSource().Sample(DateTimeOffset.UnixEpoch));
        Assert.Equal(agent.Select(kv => kv.Key).Order(StringComparer.Ordinal), payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("rgb.status unavailable", RgbService.ReasonOpenRgbNotResponding, RgbService.NotRespondingMessage, false)]
    [InlineData("rgb.status conflict", RgbService.ReasonConflict, RgbService.ConflictMessage, true)]
    public void Rgb_fixtures_carry_reason_and_reason_code_as_the_agent_writes_them(string name, string code, string reason, bool conflict)
    {
        using var doc = Shared.Load("fixtures/responses.v1.json");
        var payload = Payload(doc, name);
        Assert.False(payload.GetProperty("available").GetBoolean());
        Assert.Equal(code, payload.GetProperty("reasonCode").GetString());
        Assert.Equal(reason, payload.GetProperty("reason").GetString());
        Assert.Equal(conflict, payload.TryGetProperty("state", out var state) && state.GetString() == "conflict");
    }

    [Fact]
    public void Plan_required_fixture_for_screen_video_names_the_feature()
    {
        using var doc = Shared.Load("fixtures/responses.v1.json");
        var error = doc.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "plan.required for screen.video")
            .GetProperty("message").GetProperty("error");
        Assert.Equal("plan.required", error.GetProperty("code").GetString());
        Assert.Equal(Nexus.Core.Entitlements.PlanPolicy.ScreenVideo, error.GetProperty("details").GetProperty("feature").GetString());
    }
}
