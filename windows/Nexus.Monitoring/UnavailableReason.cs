namespace Nexus.Monitoring;

/// <summary>
/// Why a metric could not be read: a stable machine code (docs/API.md, "unavailableCodes") and the
/// human-readable text that older clients show. The code is set by the source, never derived from the text.
/// </summary>
public readonly record struct UnavailableReason(string Code, string Text);

/// <summary>The codes the agent sends in metrics.snapshot "unavailableCodes" (the list may grow later).</summary>
public static class UnavailableCodes
{
    public const string SensorDriverMissing = "sensorDriverMissing";
    public const string SensorReadFailed = "sensorReadFailed";
    public const string SensorMissing = "sensorMissing";
    public const string WarmingUp = "warmingUp";
    public const string OsQueryFailed = "osQueryFailed";
    public const string GpuDriverMissing = "gpuDriverMissing";
    public const string GpuLibraryRejected = "gpuLibraryRejected";
    public const string GpuLibraryLoadFailed = "gpuLibraryLoadFailed";
    public const string GpuInitFailed = "gpuInitFailed";
    public const string GpuListFailed = "gpuListFailed";
    public const string PlatformUnsupported = "platformUnsupported";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SensorDriverMissing, SensorReadFailed, SensorMissing, WarmingUp, OsQueryFailed, GpuDriverMissing,
        GpuLibraryRejected, GpuLibraryLoadFailed, GpuInitFailed, GpuListFailed, PlatformUnsupported,
    };
}

/// <summary>Reasons given by the CPU, memory and portable sources (texts unchanged from before the codes).</summary>
public static class MetricsReasons
{
    public static readonly UnavailableReason NoTemperatureSensor = new(UnavailableCodes.SensorMissing, "Нет датчика температуры процессора.");
    public static readonly UnavailableReason NoClockData = new(UnavailableCodes.SensorMissing, "Нет данных о частоте процессора.");
    public static readonly UnavailableReason FirstSample = new(UnavailableCodes.WarmingUp, "Первое измерение, загрузка появится через пару секунд.");
    public static readonly UnavailableReason MemoryQueryFailed = new(UnavailableCodes.OsQueryFailed, "GlobalMemoryStatusEx не ответил.");
    public static readonly UnavailableReason WindowsOnly = new(UnavailableCodes.PlatformUnsupported, "Доступно только на Windows.");

    public static readonly UnavailableReason SensorDriverMissing = new(UnavailableCodes.SensorDriverMissing,
        "Нет значения от датчика процессора: нужен драйвер PawnIO (ставится вместе с OpenRGB или с pawnio.eu), затем перезапустите ПК.");

    public static UnavailableReason SensorOpenFailed(string message) =>
        new(UnavailableCodes.SensorReadFailed, "Не удалось открыть датчики процессора: " + message);

    public static UnavailableReason SensorReadFailed(string message) =>
        new(UnavailableCodes.SensorReadFailed, "Ошибка чтения датчиков процессора: " + message);
}

/// <summary>Collects reasons into the two parallel maps of <see cref="MetricsSnapshot"/>, so their keys always match.</summary>
public sealed class UnavailableSet
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _codes = new(StringComparer.Ordinal);

    public void Add(string key, UnavailableReason reason)
    {
        _texts[key] = reason.Text;
        _codes[key] = reason.Code;
    }

    public IReadOnlyDictionary<string, string> Texts => _texts;

    public IReadOnlyDictionary<string, string> Codes => _codes;
}
