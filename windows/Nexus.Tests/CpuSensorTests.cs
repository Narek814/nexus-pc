using Nexus.Monitoring;
using Xunit;

namespace Nexus.Tests;

public sealed class CpuSensorPickerTests
{
    private static SensorReading T(string name, float? value) => new(name, "Temperature", value);

    private static SensorReading C(string name, float? value) => new(name, "Clock", value);

    [Fact]
    public void Intel_package_temperature_and_average_core_clock()
    {
        // Names as LibreHardwareMonitor reports them for a 12th-gen Intel CPU (owner's B660 board).
        var (temperature, clock) = CpuSensorPicker.Pick(
        [
            T("Core Max", 61), T("Core Average", 48.5f), T("CPU Package", 58.4f), T("P-Core #1", 55),
            C("Bus Speed", 100), C("P-Core #1", 4400), C("P-Core #2", 3600), C("E-Core #1", 2000),
            new("CPU Total", "Load", 12),
        ]);
        Assert.Equal(58.4, temperature);
        Assert.Equal(3333, clock);
    }

    [Fact]
    public void Amd_uses_tctl_when_there_is_no_package_sensor()
    {
        var (temperature, _) = CpuSensorPicker.Pick([T("Core (Tctl/Tdie)", 66.25f), T("CCD1 (Tdie)", 60)]);
        Assert.Equal(66.3, temperature);
    }

    [Fact]
    public void Falls_back_to_the_hottest_sensor()
    {
        var (temperature, _) = CpuSensorPicker.Pick([T("Core #1", 40), T("Core #2", 52)]);
        Assert.Equal(52, temperature);
    }

    [Fact]
    public void Missing_driver_means_no_values_not_zero()
    {
        // Without PawnIO the sensors exist but have no value (or read 0).
        var (temperature, clock) = CpuSensorPicker.Pick([T("CPU Package", null), T("Core Max", 0), C("P-Core #1", null), C("Bus Speed", 100)]);
        Assert.Null(temperature);
        Assert.Null(clock);
    }

    [Fact]
    public void Impossible_values_are_ignored()
    {
        var (temperature, _) = CpuSensorPicker.Pick([T("CPU Package", 255), T("Core Max", float.NaN), T("Core #1", 45)]);
        Assert.Equal(45, temperature);
    }
}
