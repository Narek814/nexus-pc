using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Monitoring;

/// <summary>
/// Windows telemetry from documented OS and driver APIs only, no extra packages:
/// CPU usage (GetSystemTimes), memory (GlobalMemoryStatusEx), NVIDIA GPUs (NVML, installed with the
/// driver, loaded from two absolute paths only: Nvml.cs), disks and network (.NET). CPU temperature and
/// real clock come from LibreHardwareMonitor (CpuSensorReader); without its driver they are reported as
/// unavailable instead of guessed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsMetricsSource : IMetricsSource, IDisposable
{
    private readonly NetworkRateReader _network = new();
    private readonly NvmlReader _nvml = NvmlReader.ForWindows();
    private readonly CpuSensorReader _cpuSensors = new();
    private (long Idle, long Kernel, long User)? _lastCpu;

    public MetricsSnapshot Sample(DateTimeOffset now)
    {
        var unavailable = new Dictionary<string, string>();
        var (cpuTemperature, cpuClock, sensorProblem) = _cpuSensors.Read();
        if (cpuTemperature is null)
        {
            unavailable["cpu.temperature"] = sensorProblem ?? "Нет датчика температуры процессора.";
        }
        if (cpuClock is null)
        {
            unavailable["cpu.clock"] = sensorProblem ?? "Нет данных о частоте процессора.";
        }

        var cpuUsage = ReadCpuUsage();
        if (cpuUsage is null)
        {
            unavailable["cpu.usage"] = "Первое измерение, загрузка появится через пару секунд.";
        }

        var memory = ReadMemory();
        if (memory is null)
        {
            unavailable["memory"] = "GlobalMemoryStatusEx не ответил.";
        }

        var gpus = _nvml.Read(out var gpuProblem);
        if (gpuProblem is not null)
        {
            unavailable["gpu"] = gpuProblem;
        }

        return new MetricsSnapshot
        {
            Timestamp = now,
            Cpu = new CpuMetrics { Usage = cpuUsage, Temperature = cpuTemperature, ClockMhz = cpuClock },
            Memory = memory ?? new MemoryMetrics(),
            Gpus = gpus,
            Disks = DiskReader.Read(),
            Network = _network.Read(now),
            Unavailable = unavailable,
        };
    }

    private double? ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }
        var previous = _lastCpu;
        _lastCpu = (idle, kernel, user);
        if (previous is not { } p)
        {
            return null;
        }
        // Kernel time includes idle time.
        var total = (kernel - p.Kernel) + (user - p.User);
        var busy = total - (idle - p.Idle);
        return total > 0 ? Math.Clamp(100.0 * busy / total, 0, 100) : null;
    }

    private static MemoryMetrics? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }
        return new MemoryMetrics
        {
            TotalBytes = (long)status.TotalPhys,
            UsedBytes = (long)(status.TotalPhys - status.AvailPhys),
        };
    }

    public void Dispose()
    {
        _nvml.Dispose();
        _cpuSensors.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
