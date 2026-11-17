using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace Nexus.Monitoring;

/// <summary>A sensor value as LibreHardwareMonitor reports it, without its types (so the choice is testable anywhere).</summary>
public readonly record struct SensorReading(string Name, string Type, float? Value);

/// <summary>Picks the CPU temperature and clock from the sensors a processor exposes.</summary>
public static class CpuSensorPicker
{
    /// <summary>
    /// Temperature: the package sensor if present (what most tools show), else the hottest core.
    /// Clock: average of the core clocks (not the bus clock), MHz.
    /// </summary>
    public static (double? Temperature, double? ClockMhz) Pick(IEnumerable<SensorReading> sensors)
    {
        var list = sensors.Where(s => s.Value is { } v && float.IsFinite(v) && v > 0).ToList();
        var temps = list.Where(s => s.Type == "Temperature" && s.Value < 150).ToList();
        double? temperature =
            temps.FirstOrDefault(s => s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)).Value
            ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)).Value
            ?? temps.FirstOrDefault(s => s.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase)).Value
            ?? (temps.Count > 0 ? temps.Max(s => s.Value) : null);
        var clocks = list.Where(s => s.Type == "Clock" && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                     && !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)).ToList();
        double? clock = clocks.Count > 0 ? Math.Round(clocks.Average(s => s.Value!.Value)) : null;
        return (temperature is null ? null : Math.Round(temperature.Value, 1, MidpointRounding.AwayFromZero), clock);
    }
}

/// <summary>
/// CPU temperature and clock through LibreHardwareMonitor (MPL-2.0; owner's permission, 2026-11-17).
/// Since 0.9.4 it reads the processor's sensors through the PawnIO driver (also used by OpenRGB),
/// not the old WinRing0 that antivirus software flags. Without the driver there are no values, and the
/// reason is shown instead of a guess. Only the CPU part is enabled.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CpuSensorReader : IDisposable
{
    private readonly Computer? _computer;
    private readonly string? _openError;

    public CpuSensorReader()
    {
        try
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
        }
#pragma warning disable CA1031 // A sensor library failure must not stop monitoring.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _openError = "Не удалось открыть датчики процессора: " + ex.Message;
            _computer = null;
        }
    }

    public (double? Temperature, double? ClockMhz, string? Problem) Read()
    {
        if (_computer is null)
        {
            return (null, null, _openError);
        }
        var readings = new List<SensorReading>();
        try
        {
            foreach (var hardware in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
            {
                hardware.Update();
                readings.AddRange(hardware.Sensors.Select(s => new SensorReading(s.Name, s.SensorType.ToString(), s.Value)));
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (null, null, "Ошибка чтения датчиков процессора: " + ex.Message);
        }
        var (temperature, clock) = CpuSensorPicker.Pick(readings);
        var problem = temperature is null
            ? "Нет значения от датчика процессора: нужен драйвер PawnIO (ставится вместе с OpenRGB или с pawnio.eu), затем перезапустите ПК."
            : null;
        return (temperature, clock, problem);
    }

    public void Dispose() => _computer?.Close();
}
