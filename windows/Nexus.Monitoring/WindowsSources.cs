using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Monitoring;

/// <summary>
/// Windows telemetry from documented OS and driver APIs only, no extra packages:
/// CPU usage (GetSystemTimes), memory (GlobalMemoryStatusEx), NVIDIA GPUs (NVML, installed with the
/// driver), disks and network (.NET). CPU temperature and real clock come from LibreHardwareMonitor
/// (CpuSensorReader); without its driver they are reported as unavailable instead of guessed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsMetricsSource : IMetricsSource, IDisposable
{
    private readonly NetworkRateReader _network = new();
    private readonly NvmlReader _nvml = new();
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

/// <summary>
/// NVIDIA Management Library (nvml.dll from the NVIDIA driver, in System32). If it is missing
/// (AMD/Intel GPU, old driver), GPUs are reported as unavailable with the reason.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NvmlReader : IDisposable
{
    private const int Success = 0;
    private bool? _initialized;
    private string? _initProblem;

    public IReadOnlyList<GpuMetrics> Read(out string? problem)
    {
        if (!EnsureInitialized())
        {
            problem = _initProblem;
            return [];
        }
        problem = null;
        try
        {
            if (nvmlDeviceGetCount_v2(out var count) != Success)
            {
                problem = "NVML не вернул список видеокарт.";
                return [];
            }
            var gpus = new List<GpuMetrics>();
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != Success)
                {
                    continue;
                }
                var nameBuffer = new byte[96];
                var name = nvmlDeviceGetName(device, nameBuffer, (uint)nameBuffer.Length) == Success
                    ? Encoding.UTF8.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0) is var end and >= 0 ? end : nameBuffer.Length)
                    : $"GPU {i}";
                gpus.Add(new GpuMetrics
                {
                    Name = name,
                    Usage = nvmlDeviceGetUtilizationRates(device, out var utilization) == Success ? utilization.Gpu : null,
                    Temperature = nvmlDeviceGetTemperature(device, 0, out var temperature) == Success ? temperature : null,
                    MemoryUsedBytes = nvmlDeviceGetMemoryInfo(device, out var memory) == Success ? (long)memory.Used : null,
                    MemoryTotalBytes = memory.Total > 0 ? (long)memory.Total : null,
                    CoreClockMhz = nvmlDeviceGetClockInfo(device, 0, out var core) == Success ? core : null,
                    MemoryClockMhz = nvmlDeviceGetClockInfo(device, 2, out var mem) == Success ? mem : null,
                });
            }
            return gpus;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            problem = "NVML недоступна.";
            return [];
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized is { } done)
        {
            return done;
        }
        try
        {
            var result = nvmlInit_v2();
            _initialized = result == Success;
            _initProblem = _initialized == true ? null : $"NVML не запустилась (код {result}).";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _initialized = false;
            _initProblem = "Нет NVIDIA NVML (nvml.dll): видеокарта не NVIDIA или драйвер не установлен. AMD/Intel — позже.";
        }
        return _initialized.Value;
    }

    public void Dispose()
    {
        if (_initialized == true)
        {
            _ = nvmlShutdown(); // nothing useful to do if shutdown fails while the agent stops
            _initialized = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [LibraryImport("nvml.dll")]
    private static partial int nvmlInit_v2();

    [LibraryImport("nvml.dll")]
    private static partial int nvmlShutdown();

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetCount_v2(out uint count);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);

    [LibraryImport("nvml.dll")]
    private static partial int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);
}
