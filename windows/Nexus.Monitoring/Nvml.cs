using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Monitoring;

/// <summary>A place NVML may be loaded from: the file and the folder its real (final) path must stay inside.</summary>
internal readonly record struct NvmlCandidate(string Path, string Root);

/// <summary>A candidate file opened by handle (the file itself, never a link's target), held open until disposed.</summary>
internal interface IOpenedLibraryFile : IDisposable
{
    bool IsReparsePoint { get; }

    bool IsDirectory { get; }

    /// <summary>The path the file system reports for the open handle (DOS form, no "\\?\"), or null if unknown.</summary>
    string? FinalPath { get; }
}

/// <summary>The operating-system side of loading NVML; tests replace it.</summary>
internal interface INativeLibraryLoader
{
    /// <summary>Opens <paramref name="path"/> without following a reparse point; null if it is missing or cannot be opened.</summary>
    IOpenedLibraryFile? Open(string path);

    /// <summary>Loads the library from this absolute path (never by name).</summary>
    bool TryLoad(string absolutePath, out IntPtr handle);

    bool TryGetExport(IntPtr handle, string name, out IntPtr address);

    void Free(IntPtr handle);
}

/// <summary>The outcome of looking for NVML: a library handle, or the reason there is none.</summary>
internal readonly record struct NvmlLoadResult(IntPtr Handle, string? LoadedPath, string? Problem);

/// <summary>
/// Where NVML (nvml.dll, installed with the NVIDIA driver) is loaded from: two absolute paths, in this order,
/// and nowhere else. There is no P/Invoke by name and no fallback to the Windows search order (current folder,
/// PATH): if neither file is there and in place, there is simply no library and GPUs are reported unavailable.
/// </summary>
internal static class NvmlLocation
{
    public const string FileName = "nvml.dll";

    /// <summary>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32 for LoadLibraryExW (Windows loader).</summary>
    public const uint LoadFlags = 0x00000100 | 0x00000800;

    public const string Missing = "Нет NVIDIA NVML (nvml.dll): видеокарта не NVIDIA или драйвер не установлен. AMD/Intel — позже.";
    public const string Rejected = "Библиотека NVIDIA (nvml.dll) лежит не на своём месте или подменена ссылкой; она не загружена ради безопасности.";
    public const string LoadFailed = "Библиотека NVIDIA (nvml.dll) есть, но не загрузилась: переустановите драйвер NVIDIA.";

    /// <summary>
    /// 1. &lt;System&gt;\nvml.dll (current drivers); 2. &lt;Program Files&gt;\NVIDIA Corporation\NVSMI\nvml.dll (older drivers).
    /// Folders that are not fully qualified (for example empty on a non-Windows system) give no candidate.
    /// </summary>
    public static IReadOnlyList<NvmlCandidate> Candidates(string systemDirectory, string programFilesDirectory)
    {
        var candidates = new List<NvmlCandidate>(2);
        if (IsFullyQualified(systemDirectory))
        {
            candidates.Add(new NvmlCandidate(Path.Combine(systemDirectory, FileName), systemDirectory));
        }
        if (IsFullyQualified(programFilesDirectory))
        {
            candidates.Add(new NvmlCandidate(Path.Combine(programFilesDirectory, "NVIDIA Corporation", "NVSMI", FileName), programFilesDirectory));
        }
        return candidates;
    }

    /// <summary>The real folders from the shell's known folders (not from environment variables).</summary>
    public static IReadOnlyList<NvmlCandidate> DefaultCandidates() =>
        Candidates(Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    /// <summary>
    /// Tries the candidates in order. Each file is opened first (without following links) and kept open while it is
    /// loaded; it is skipped if it is a reparse point or a folder, if its final path is not the expected one, or if
    /// that path leaves the candidate's root folder.
    /// </summary>
    public static NvmlLoadResult Load(IReadOnlyList<NvmlCandidate> candidates, INativeLibraryLoader loader)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(loader);
        string? problem = null;
        foreach (var candidate in candidates)
        {
            if (!IsFullyQualified(candidate.Path) || !IsFullyQualified(candidate.Root))
            {
                continue;
            }
            using var file = loader.Open(candidate.Path);
            if (file is null)
            {
                continue;
            }
            if (file.IsReparsePoint || file.IsDirectory || file.FinalPath is not { } final
                || !string.Equals(final, candidate.Path, StringComparison.OrdinalIgnoreCase)
                || !IsInside(final, candidate.Root))
            {
                problem = Rejected;
                continue;
            }
            if (loader.TryLoad(candidate.Path, out var handle) && handle != IntPtr.Zero)
            {
                return new NvmlLoadResult(handle, candidate.Path, null);
            }
            problem = LoadFailed;
        }
        return new NvmlLoadResult(IntPtr.Zero, null, problem ?? Missing);
    }

    private static bool IsFullyQualified(string path) => !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path);

    private static bool IsInside(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length;
    }
}

/// <summary>
/// NVIDIA GPUs through NVML. The library is loaded explicitly from an absolute path (<see cref="NvmlLocation"/>),
/// its functions are taken with GetExport and called through unmanaged function pointers. Without the library
/// (AMD/Intel GPU, no driver, file out of place) GPUs are reported as unavailable with the reason (D9).
/// </summary>
internal sealed unsafe class NvmlReader : IDisposable
{
    private const int Success = 0;
    private readonly INativeLibraryLoader _loader;
    private readonly IReadOnlyList<NvmlCandidate> _candidates;
    private Api _api;
    private IntPtr _library;
    private bool? _initialized;
    private string? _initProblem;

    public NvmlReader(INativeLibraryLoader loader, IReadOnlyList<NvmlCandidate> candidates)
    {
        _loader = loader;
        _candidates = candidates;
    }

    /// <summary>The production reader: the two absolute paths on this PC, the Windows loader.</summary>
    [SupportedOSPlatform("windows")]
    public static NvmlReader ForWindows() => new(new WindowsNativeLibraryLoader(), NvmlLocation.DefaultCandidates());

    /// <summary>The path NVML was loaded from, or null.</summary>
    public string? LoadedPath { get; private set; }

    public IReadOnlyList<GpuMetrics> Read(out string? problem)
    {
        if (!EnsureInitialized())
        {
            problem = _initProblem;
            return [];
        }
        problem = null;
        uint count;
        if (_api.GetCount(&count) != Success)
        {
            problem = "NVML не вернул список видеокарт.";
            return [];
        }
        var gpus = new List<GpuMetrics>();
        var nameBuffer = new byte[96];
        for (uint i = 0; i < count; i++)
        {
            IntPtr device;
            if (_api.GetHandleByIndex(i, &device) != Success)
            {
                continue;
            }
            Array.Clear(nameBuffer);
            int nameResult;
            fixed (byte* name = nameBuffer)
            {
                nameResult = _api.GetName(device, name, (uint)nameBuffer.Length);
            }
            var deviceName = nameResult == Success
                ? Encoding.UTF8.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0) is var end and >= 0 ? end : nameBuffer.Length)
                : $"GPU {i}";
            Utilization utilization;
            uint temperature, core, memoryClock;
            var memory = default(MemoryInfo);
            gpus.Add(new GpuMetrics
            {
                Name = deviceName,
                Usage = _api.GetUtilizationRates(device, &utilization) == Success ? utilization.Gpu : null,
                Temperature = _api.GetTemperature(device, 0, &temperature) == Success ? temperature : null,
                MemoryUsedBytes = _api.GetMemoryInfo(device, &memory) == Success ? (long)memory.Used : null,
                MemoryTotalBytes = memory.Total > 0 ? (long)memory.Total : null,
                CoreClockMhz = _api.GetClockInfo(device, 0, &core) == Success ? core : null,
                MemoryClockMhz = _api.GetClockInfo(device, 2, &memoryClock) == Success ? memoryClock : null,
            });
        }
        return gpus;
    }

    private bool EnsureInitialized()
    {
        if (_initialized is { } done)
        {
            return done;
        }
        _initialized = false;
        var loaded = NvmlLocation.Load(_candidates, _loader);
        if (loaded.Handle == IntPtr.Zero)
        {
            _initProblem = loaded.Problem;
            return false;
        }
        if (!TryBind(loaded.Handle, out _api))
        {
            _loader.Free(loaded.Handle);
            _initProblem = "NVML недоступна.";
            return false;
        }
        var result = _api.Init();
        if (result != Success)
        {
            _api = default;
            _loader.Free(loaded.Handle);
            _initProblem = $"NVML не запустилась (код {result}).";
            return false;
        }
        _library = loaded.Handle;
        LoadedPath = loaded.LoadedPath;
        _initialized = true;
        _initProblem = null;
        return true;
    }

    private bool TryBind(IntPtr library, out Api api)
    {
        api = default;
        if (!Export(library, "nvmlInit_v2", out var init)
            || !Export(library, "nvmlShutdown", out var shutdown)
            || !Export(library, "nvmlDeviceGetCount_v2", out var getCount)
            || !Export(library, "nvmlDeviceGetHandleByIndex_v2", out var getHandle)
            || !Export(library, "nvmlDeviceGetName", out var getName)
            || !Export(library, "nvmlDeviceGetUtilizationRates", out var getUtilization)
            || !Export(library, "nvmlDeviceGetTemperature", out var getTemperature)
            || !Export(library, "nvmlDeviceGetMemoryInfo", out var getMemory)
            || !Export(library, "nvmlDeviceGetClockInfo", out var getClock))
        {
            return false;
        }
        api = new Api
        {
            Init = (delegate* unmanaged[Cdecl]<int>)init,
            Shutdown = (delegate* unmanaged[Cdecl]<int>)shutdown,
            GetCount = (delegate* unmanaged[Cdecl]<uint*, int>)getCount,
            GetHandleByIndex = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)getHandle,
            GetName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)getName,
            GetUtilizationRates = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)getUtilization,
            GetTemperature = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)getTemperature,
            GetMemoryInfo = (delegate* unmanaged[Cdecl]<IntPtr, MemoryInfo*, int>)getMemory,
            GetClockInfo = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)getClock,
        };
        return true;
    }

    private bool Export(IntPtr library, string name, out IntPtr address) =>
        _loader.TryGetExport(library, name, out address) && address != IntPtr.Zero;

    public void Dispose()
    {
        if (_initialized == true)
        {
            _ = _api.Shutdown(); // nothing useful to do if shutdown fails while the agent stops
            _api = default;
            _loader.Free(_library);
            _library = IntPtr.Zero;
            _initialized = null;
        }
    }

    private struct Api
    {
        public delegate* unmanaged[Cdecl]<int> Init;
        public delegate* unmanaged[Cdecl]<int> Shutdown;
        public delegate* unmanaged[Cdecl]<uint*, int> GetCount;
        public delegate* unmanaged[Cdecl]<uint, IntPtr*, int> GetHandleByIndex;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> GetName;
        public delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> GetUtilizationRates;
        public delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> GetTemperature;
        public delegate* unmanaged[Cdecl]<IntPtr, MemoryInfo*, int> GetMemoryInfo;
        public delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> GetClockInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }
}
