using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Nexus.Agent.Admin;
using Nexus.Monitoring;

namespace Nexus.Tests;

/// <summary>
/// Child-process side of NativeLibraryLoadingTests (T6). The process-wide DLL search setting cannot be undone,
/// so it is only ever applied here, in a separate process started by the test:
///   dotnet exec Nexus.Tests.dll --dll-search-probe &lt;absolute|control|agent&gt; &lt;report file&gt; &lt;missing absolute path&gt;
/// The test starts it with a folder holding an empty "nvml.dll" stub first in PATH and a folder holding an empty
/// "nexus-cwd-decoy.dll" stub as the current folder. The stubs are not code: loading one can only fail. Each line
/// of the report is "key=value". Nothing outside the test's temporary folders is changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DllSearchProbe
{
    public const string CwdDecoy = "nexus-cwd-decoy.dll";

    public static int Run(string mode, string reportPath, string missingPath)
    {
        var report = new List<string>();
        void Add(string key, object? value) => report.Add(key + "=" + Convert.ToString(value, CultureInfo.InvariantCulture)?.ReplaceLineEndings(" "));
        try
        {
            if (mode == "agent")
            {
                // The agent's own call, before anything else here loads a library.
                Add("restrict", Nexus.Agent.Program.RestrictDllSearch(out var error) ? "ok" : "error " + error);
            }

            // NVML by absolute path only: a missing absolute path is never searched for elsewhere.
            Add("absolute.tryload", NativeLibrary.TryLoad(missingPath, out var none));
            if (none != IntPtr.Zero)
            {
                NativeLibrary.Free(none);
            }
            try
            {
                NativeLibrary.Free(NativeLibrary.Load(missingPath));
                Add("absolute.load", "loaded");
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                Add("absolute.load", ex.GetType().Name);
            }
            using (var reader = new NvmlReader(new WindowsNativeLibraryLoader(), [new NvmlCandidate(missingPath, Path.GetDirectoryName(missingPath)!)]))
            {
                var gpus = reader.Read(out var problem);
                Add("absolute.gpus", gpus.Count);
                Add("absolute.problem", problem);
            }
            if (mode == "absolute")
            {
                return Finish(report, reportPath);
            }

            // By name with the process search order: without the restriction the stubs in PATH and in the current
            // folder are found (the load then fails on the stub: error other than 126); with it they are not (126).
            Add("byname.path", LoadByNameError("nvml.dll"));
            Add("byname.cwd", LoadByNameError(CwdDecoy));

            // What the agent loads: production NVML reader, LibreHardwareMonitor, system calls, Kestrel + TLS, admin pipe.
            using (var nvml = NvmlReader.ForWindows())
            {
                var gpus = nvml.Read(out var problem);
                Add("nvml.gpus", gpus.Count);
                Add("nvml.problem", problem);
                Add("nvml.path", nvml.LoadedPath);
            }
            using (var source = new WindowsMetricsSource())
            {
                source.Sample(DateTimeOffset.UtcNow);
                Thread.Sleep(600);
                var snapshot = source.Sample(DateTimeOffset.UtcNow);
                Add("metrics.cpu", snapshot.Cpu.Usage is not null);
                Add("metrics.memory", snapshot.Memory.TotalBytes > 0);
                Add("metrics.disks", snapshot.Disks.Count > 0);
                Add("metrics.gpus", snapshot.Gpus.Count);
                Add("metrics.gpu", snapshot.Unavailable.GetValueOrDefault("gpu"));
                Add("metrics.cpu.temperature", snapshot.Cpu.Temperature is null ? "unavailable" : "available"); // compared: availability only
                Add("metrics.cpu.temperature.detail", snapshot.Unavailable.GetValueOrDefault("cpu.temperature") ?? "value " + snapshot.Cpu.Temperature);
            }
            Add("system.session", Nexus.Windows.WindowsProcesses.ActiveConsoleSessionId() is not null);
            Add("system.image", string.Equals(Nexus.Windows.WindowsProcesses.ImagePath(Environment.ProcessId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
            Add("system.service", Nexus.Windows.WindowsServiceInfo.RunningProcessId("EventLog") is > 0);
            Add("system.power", new Nexus.Windows.WindowsPowerController().CheckAvailable());
            Add("agent", AgentScenarioAsync().GetAwaiter().GetResult());
        }
#pragma warning disable CA1031 // Any failure is reported to the parent test, which fails with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Add("exception", ex.GetType().Name + ": " + ex.Message);
        }
        return Finish(report, reportPath);
    }

    /// <summary>A real agent host (TLS, WebSocket, pairing, signed auth, metrics with the Windows source, admin pipe).</summary>
    private static async Task<string> AgentScenarioAsync()
    {
        var pipe = "NexusPC.Test." + Guid.NewGuid().ToString("N");
        using var metrics = new WindowsMetricsSource();
        await using var host = await AgentTestHost.StartAsync(adminPipe: pipe, metrics: metrics);
        try
        {
            var (client, _, key) = await host.PairedClientAsync();
            await using var _ = client;
            using var __ = key;
            var snapshot = await client.RequestAsync("metrics.snapshot");
            await using var admin = await AdminClient.ConnectAsync(pipe, CancellationToken.None);
            var status = await admin.SendAsync(new JsonObject { ["op"] = "status" }, CancellationToken.None);
            return snapshot.IsOk() && status.IsOk() ? "ok" : "failed " + snapshot.ToJsonString() + " " + status.ToJsonString();
        }
        finally
        {
            AgentTestHost.DeleteDirectory(host.DataDirectory);
        }
    }

    private static int Finish(List<string> report, string reportPath)
    {
        foreach (System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)
        {
            report.Add("module=" + module.FileName);
        }
        File.WriteAllLines(reportPath, report);
        return 0;
    }

    /// <summary>LoadLibraryExW(name, 0): the process search order. 0 if something real was loaded (then freed).</summary>
    private static int LoadByNameError(string name)
    {
        var module = LoadLibraryExW(name, IntPtr.Zero, 0);
        if (module != IntPtr.Zero)
        {
            FreeLibrary(module);
            return 0;
        }
        return Marshal.GetLastPInvokeError();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
