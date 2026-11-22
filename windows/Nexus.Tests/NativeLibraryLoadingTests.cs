using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Monitoring;
using Xunit.Abstractions;

namespace Nexus.Tests;

/// <summary>
/// T6: NVML only from two absolute paths, no P/Invoke by name, System32-only search for our P/Invoke, and the
/// agent's process-wide DLL search order (owner's decision 7). The Windows tests run on CI windows-latest;
/// the process-wide setting is only ever applied in a child process (DllSearchProbe).
/// </summary>
public sealed class NativeLibraryLoadingTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string Sys = TestPaths.Abs("/win/system32");
    private static readonly string Pf = TestPaths.Abs("/pf");
    private static readonly string First = Path.Combine(Sys, "nvml.dll");
    private static readonly string Second = Path.Combine(Pf, "NVIDIA Corporation", "NVSMI", "nvml.dll");
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var path in _cleanup)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // a child process may still hold a file for a moment; %TEMP% is cleaned by the runner
            }
        }
    }

    // ---- candidates (any OS) ----

    [Fact]
    public void Nvml_candidates_are_exactly_two_absolute_paths_in_order()
    {
        var candidates = NvmlLocation.Candidates(Sys, Pf);
        Assert.Equal([First, Second], candidates.Select(c => c.Path));
        Assert.Equal([Sys, Pf], candidates.Select(c => c.Root));
        Assert.All(candidates, c => Assert.True(Path.IsPathFullyQualified(c.Path), c.Path));
    }

    [Fact]
    public void Nvml_candidates_are_never_relative()
    {
        Assert.Empty(NvmlLocation.Candidates("", ""));
        Assert.Empty(NvmlLocation.Candidates("system32", TestPaths.Rel("pf/x")));
        Assert.Equal([Second], NvmlLocation.Candidates("", Pf).Select(c => c.Path));
        Assert.All(NvmlLocation.DefaultCandidates(), c => Assert.True(Path.IsPathFullyQualified(c.Path), c.Path));
        // A relative candidate given directly is never opened or loaded.
        var loader = new FakeLoader();
        Assert.Equal(IntPtr.Zero, NvmlLocation.Load([new NvmlCandidate("nvml.dll", Sys), new NvmlCandidate(First, "system32")], loader).Handle);
        Assert.Empty(loader.Opened);
        Assert.Empty(loader.Loaded);
    }

    // ---- loading with a fake loader (any OS) ----

    [Fact]
    public void Missing_files_load_nothing_and_gpus_are_unavailable_with_the_reason()
    {
        var loader = new FakeLoader();
        using var reader = new NvmlReader(loader, NvmlLocation.Candidates(Sys, Pf));
        var gpus = reader.Read(out var problem);
        Assert.Empty(gpus);
        Assert.Equal(NvmlLocation.Missing, problem);
        Assert.Equal([First, Second], loader.Opened); // only the two absolute paths are ever touched
        Assert.Empty(loader.Loaded);                  // and nothing is loaded at all
        reader.Read(out var again);
        Assert.Equal(NvmlLocation.Missing, again);
        Assert.Equal(2, loader.Opened.Count);         // the outcome is kept, not retried every sample
    }

    [Fact]
    public void A_reparse_point_first_candidate_is_skipped_for_the_second()
    {
        var loader = new FakeLoader();
        loader.Files[First] = new FakeFile { IsReparsePoint = true, FinalPath = First };
        loader.Files[Second] = new FakeFile { FinalPath = Second };
        var result = NvmlLocation.Load(NvmlLocation.Candidates(Sys, Pf), loader);
        Assert.Equal(Second, result.LoadedPath);
        Assert.Equal([Second], loader.Loaded);
        Assert.All(loader.Files.Values, f => Assert.True(f.Disposed, "every opened file is closed"));
    }

    [Fact]
    public void A_second_candidate_whose_final_path_leaves_program_files_is_not_loaded()
    {
        var loader = new FakeLoader();
        var elsewhere = TestPaths.Abs("/users/x/NVIDIA Corporation/NVSMI/nvml.dll");
        loader.Files[Second] = new FakeFile { FinalPath = elsewhere }; // e.g. a junction on a parent folder
        var result = NvmlLocation.Load(NvmlLocation.Candidates(Sys, Pf), loader);
        Assert.Equal(IntPtr.Zero, result.Handle);
        Assert.Equal(NvmlLocation.Rejected, result.Problem);
        Assert.Empty(loader.Loaded);

        // Even with the same final path, a root that does not contain it is refused.
        var outside = new FakeLoader();
        outside.Files[Second] = new FakeFile { FinalPath = Second };
        Assert.Equal(IntPtr.Zero, NvmlLocation.Load([new NvmlCandidate(Second, TestPaths.Abs("/other"))], outside).Handle);
        Assert.Empty(outside.Loaded);
    }

    [Fact]
    public void A_first_candidate_that_is_a_folder_moved_or_unreadable_is_not_loaded()
    {
        foreach (var file in new[]
                 {
                     new FakeFile { IsDirectory = true, FinalPath = First },
                     new FakeFile { FinalPath = TestPaths.Abs("/temp/nvml.dll") },
                     new FakeFile { FinalPath = null },
                 })
        {
            var loader = new FakeLoader();
            loader.Files[First] = file;
            var result = NvmlLocation.Load(NvmlLocation.Candidates(Sys, Pf), loader);
            Assert.Equal(NvmlLocation.Rejected, result.Problem);
            Assert.Empty(loader.Loaded);
        }
    }

    [Fact]
    public void A_file_in_place_that_fails_to_load_gives_the_reason()
    {
        var loader = new FakeLoader { LoadSucceeds = false };
        loader.Files[First] = new FakeFile { FinalPath = First };
        var result = NvmlLocation.Load(NvmlLocation.Candidates(Sys, Pf), loader);
        Assert.Equal(IntPtr.Zero, result.Handle);
        Assert.Equal(NvmlLocation.LoadFailed, result.Problem);
        Assert.Equal([First], loader.Loaded);
    }

    [Fact]
    public void Loaded_nvml_is_read_through_function_pointers_and_released()
    {
        var loader = FakeNvml.Loader();
        using (var reader = new NvmlReader(loader, NvmlLocation.Candidates(Sys, Pf)))
        {
            var gpus = reader.Read(out var problem);
            Assert.Null(problem);
            Assert.Equal(First, reader.LoadedPath);
            var gpu = Assert.Single(gpus);
            Assert.Equal("Fake RTX", gpu.Name);
            Assert.Equal(42.0, gpu.Usage);
            Assert.Equal(61.0, gpu.Temperature);
            Assert.Equal(3L << 30, gpu.MemoryUsedBytes);
            Assert.Equal(8L << 30, gpu.MemoryTotalBytes);
            Assert.Equal(1800.0, gpu.CoreClockMhz);
            Assert.Equal(7000.0, gpu.MemoryClockMhz);
            Assert.Equal(1, FakeNvml.Inits);
        }
        Assert.Equal(1, FakeNvml.Shutdowns);
        Assert.Equal([FakeLoader.Handle], loader.Freed);
    }

    [Fact]
    public void Nvml_without_a_function_or_failing_init_is_released_with_the_reason()
    {
        var incomplete = FakeNvml.Loader();
        incomplete.Exports.Remove("nvmlDeviceGetClockInfo");
        using (var reader = new NvmlReader(incomplete, NvmlLocation.Candidates(Sys, Pf)))
        {
            Assert.Empty(reader.Read(out var problem));
            Assert.Equal("NVML недоступна.", problem);
            Assert.Equal([FakeLoader.Handle], incomplete.Freed);
        }
        Assert.Single(incomplete.Freed); // not freed twice on dispose

        var failing = FakeNvml.Loader(initResult: 9);
        using var failed = new NvmlReader(failing, NvmlLocation.Candidates(Sys, Pf));
        Assert.Empty(failed.Read(out var initProblem));
        Assert.Equal("NVML не запустилась (код 9).", initProblem);
        Assert.Equal([FakeLoader.Handle], failing.Freed);
    }

    // ---- assemblies (any OS) ----

    [Fact]
    public void No_assembly_of_ours_imports_nvml_by_name()
    {
        if (TrayAssembly() is null)
        {
            TraySourcesAreChecked();
        }
        foreach (var path in OurAssemblies())
        {
            var imports = PInvokes(path).Select(p => p.Module).Distinct().ToList();
            output.WriteLine($"{Path.GetFileName(path)}: {string.Join(", ", imports)}");
            Assert.DoesNotContain(imports, m => m.Contains("nvml", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Our_windows_assemblies_limit_pinvoke_search_to_system32()
    {
        const int system32 = (int)DllImportSearchPath.System32;
        var tray = TrayAssembly();
        if (tray is null)
        {
            TraySourcesAreChecked();
        }
        foreach (var path in new[] { AssemblyPath("Nexus.Agent"), AssemblyPath("Nexus.Windows"), AssemblyPath("Nexus.Monitoring"), AssemblyPath("NexusPC-Setup"), tray, AssemblyPath("Nexus.Tests") }.OfType<string>())
        {
            Assert.True(AssemblySearchPaths(path) == system32, Path.GetFileName(path) + " must have [assembly: DefaultDllImportSearchPaths(System32)]");
            foreach (var (method, module, own) in PInvokes(path))
            {
                Assert.True(own is null || own == system32, $"{Path.GetFileName(path)}: {method} ({module}) widens the search with its own attribute");
            }
        }
    }

    [Fact]
    public void Agent_process_search_is_the_application_folder_and_system32_only()
    {
        // LOAD_LIBRARY_SEARCH_APPLICATION_DIR (0x200) | LOAD_LIBRARY_SEARCH_SYSTEM32 (0x800): no USER_DIRS, no DEFAULT_DIRS.
        Assert.Equal(0x00000A00u, Nexus.Agent.Program.DllSearchDirectories);
    }

    [Fact]
    public void Nvml_is_loaded_with_dependencies_from_its_folder_and_system32_only()
    {
        Assert.Equal(0x00000900u, NvmlLocation.LoadFlags);
    }

    // ---- Windows (CI windows-latest) ----

    [Fact]
    public void Windows_loader_uses_the_restricted_flags_and_never_searches()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        var loader = new WindowsNativeLibraryLoader();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // With LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR a bare name is invalid: proves the flags reach LoadLibraryExW
        // (a plain search for a missing name fails with 126, not 87).
        // A name no process can have loaded (unique, exists nowhere): the result cannot come from an already-loaded module.
        Assert.False(loader.TryLoad("nexus-t6-absent-" + Guid.NewGuid().ToString("N") + ".dll", out var none));
        Assert.Equal(87, Marshal.GetLastPInvokeError()); // ERROR_INVALID_PARAMETER
        Assert.Equal(IntPtr.Zero, none);
        // A system DLL by absolute path loads, its system dependencies resolve, exports and Free work.
        Assert.True(loader.TryLoad(Path.Combine(system, "dbghelp.dll"), out var module));
        try
        {
            Assert.True(loader.TryGetExport(module, "SymInitializeW", out var export) && export != IntPtr.Zero);
        }
        finally
        {
            loader.Free(module);
        }
    }

    [Fact]
    public void Windows_nvml_candidates_are_system32_then_nvsmi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(
            [Path.Combine(system, "nvml.dll"), Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll")],
            NvmlLocation.DefaultCandidates().Select(c => c.Path));
    }

    [Fact]
    public void Windows_without_nvidia_gpu_metrics_are_unavailable_with_the_reason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        using var source = new WindowsMetricsSource();
        var snapshot = source.Sample(DateTimeOffset.UtcNow);
        if (NvmlLocation.DefaultCandidates().Any(c => File.Exists(c.Path)))
        {
            output.WriteLine("NVML is installed here: GPUs " + snapshot.Gpus.Count + ", reason " + snapshot.Unavailable.GetValueOrDefault("gpu"));
            Assert.True(snapshot.Gpus.Count > 0 || snapshot.Unavailable.ContainsKey("gpu"));
            return;
        }
        Assert.Empty(snapshot.Gpus);
        Assert.Equal(NvmlLocation.Missing, snapshot.Unavailable["gpu"]);
    }

    [Fact]
    public void Windows_loader_refuses_links_and_loads_a_file_in_place_while_holding_it()
    {
        if (!OperatingSystem.IsWindows() || !Elevated())
        {
            return; // CI windows-latest (elevated): symbolic links need the privilege
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var name = NotLoadedSystemDll(system);
        var real = Path.Combine(system, name);
        var temp = NewTempFolder("nexus-t6-links-");
        var link = Path.Combine(temp, name);
        File.CreateSymbolicLink(link, real);
        var junction = Path.Combine(temp, "sys");
        Directory.CreateSymbolicLink(junction, system);
        var loader = new WindowsNativeLibraryLoader();

        using (var opened = loader.Open(link))
        {
            Assert.NotNull(opened);
            Assert.True(opened.IsReparsePoint, "the link itself is opened, not its target");
        }
        using (var opened = loader.Open(Path.Combine(junction, name)))
        {
            Assert.NotNull(opened);
            Assert.False(opened.IsReparsePoint);
            Assert.Equal(real, opened.FinalPath, ignoreCase: true); // the folder link shows in the final path
        }
        Assert.Null(loader.Open(Path.Combine(temp, "missing.dll")));

        // A link first, a folder link second, the real file third: only the real file is loaded.
        var result = NvmlLocation.Load(
            [new NvmlCandidate(link, temp), new NvmlCandidate(Path.Combine(junction, name), temp), new NvmlCandidate(real, system)],
            loader);
        Assert.Equal(real, result.LoadedPath);
        Assert.NotEqual(IntPtr.Zero, result.Handle);
        try
        {
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => string.Equals(Path.GetFileName(m.FileName), name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(real, module.FileName, ignoreCase: true);
        }
        finally
        {
            loader.Free(result.Handle);
        }
    }

    [Fact]
    public void Windows_absolute_nvml_path_never_falls_back_to_path_in_a_child_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        var run = RunProbe("absolute");
        Assert.Equal("False", run.Get("absolute.tryload"));
        Assert.Equal(nameof(DllNotFoundException), run.Get("absolute.load")); // a found stub would be BadImageFormat
        Assert.Equal("0", run.Get("absolute.gpus"));
        Assert.Equal(NvmlLocation.Missing, run.Get("absolute.problem"));
        run.AssertNoDecoyLoaded();
    }

    [Fact]
    public void Windows_child_without_the_agent_setting_does_find_the_decoys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        // Control: proves the decoys would be seen through PATH and the current folder by a load by name,
        // so the agent test below fails if the setting does not work.
        var run = RunProbe("control");
        if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll")))
        {
            Assert.NotEqual("126", run.Get("byname.path"));
        }
        Assert.NotEqual("126", run.Get("byname.cwd"));
        Assert.NotEqual("0", run.Get("byname.cwd"));
        run.AssertNoDecoyLoaded();
    }

    [Fact]
    public void Windows_agent_dll_search_setting_ignores_path_and_current_folder_and_keeps_everything_working()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // runs on the windows-latest CI job and on the owner's PC
        }
        var control = RunProbe("control");
        var agent = RunProbe("agent");
        Assert.Equal("ok", agent.Get("restrict"));
        Assert.Equal("126", agent.Get("byname.path")); // ERROR_MOD_NOT_FOUND: PATH is not searched
        Assert.Equal("126", agent.Get("byname.cwd"));  // nor the current folder
        Assert.Equal("False", agent.Get("absolute.tryload"));
        Assert.Equal(nameof(DllNotFoundException), agent.Get("absolute.load"));
        agent.AssertNoDecoyLoaded();
        agent.AssertModulesOnlyFromTrustedFolders();

        // Same results as without the setting: NVML, LibreHardwareMonitor, system calls, Kestrel/TLS, admin pipe.
        foreach (var key in new[]
                 {
                     "nvml.gpus", "nvml.problem", "nvml.path", "metrics.cpu", "metrics.memory", "metrics.disks", "metrics.gpus",
                     "metrics.gpu", "metrics.cpu.temperature", "system.session", "system.image", "system.service", "system.power", "agent",
                 })
        {
            Assert.True(control.Get(key) == agent.Get(key), $"{key}: without the setting '{control.Get(key)}', with it '{agent.Get(key)}'");
        }
        Assert.Equal("ok", agent.Get("agent"));
        Assert.Equal("True", agent.Get("metrics.cpu"));
        Assert.Equal("True", agent.Get("system.image"));
        Assert.Equal("True", agent.Get("system.service"));
        if (!NvmlLocation.DefaultCandidates().Any(c => File.Exists(c.Path)))
        {
            Assert.Equal(NvmlLocation.Missing, agent.Get("nvml.problem"));
            Assert.Equal(NvmlLocation.Missing, agent.Get("metrics.gpu"));
        }
    }

    // ---- helpers ----

    [SupportedOSPlatform("windows")]
    private ProbeRun RunProbe(string mode)
    {
        var root = NewTempFolder("nexus-t6-probe-");
        var pathDecoys = Path.Combine(root, "on-path");
        var cwd = Path.Combine(root, "cwd");
        Directory.CreateDirectory(pathDecoys);
        Directory.CreateDirectory(cwd);
        // Empty stubs, not code: they only show whether the loader looked there.
        File.WriteAllBytes(Path.Combine(pathDecoys, "nvml.dll"), []);
        File.WriteAllBytes(Path.Combine(cwd, DllSearchProbe.CwdDecoy), []);
        var report = Path.Combine(root, "report.txt");
        var missing = Path.Combine(root, "absent", "nvml.dll");

        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(dotnet), "dotnet.exe not found at " + dotnet);
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "exec", typeof(TestHelperProgram).Assembly.Location, "--dll-search-probe", mode, report, missing })
        {
            start.ArgumentList.Add(argument);
        }
        // Only the child's PATH gets the decoy folder, in front.
        start.Environment["PATH"] = pathDecoys + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        Assert.True(child.WaitForExit(TimeSpan.FromMinutes(3)), "the probe did not finish");
        Assert.True(child.ExitCode == 0 && File.Exists(report), $"probe {mode} failed ({child.ExitCode}): {stdout.Result} {stderr.Result}");
        var lines = File.ReadAllLines(report);
        output.WriteLine($"--- probe {mode}");
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }
        var run = new ProbeRun(lines, [pathDecoys, cwd], Path.GetDirectoryName(dotnet)!);
        Assert.True(run.Get("exception") is null, $"probe {mode}: {run.Get("exception")}");
        return run;
    }

    [SupportedOSPlatform("windows")]
    private sealed class ProbeRun(string[] lines, string[] decoyFolders, string dotnetRoot)
    {
        private readonly Dictionary<string, string> _values = lines
            .Where(l => !l.StartsWith("module=", StringComparison.Ordinal))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

        public List<string> Modules { get; } = [.. lines.Where(l => l.StartsWith("module=", StringComparison.Ordinal)).Select(l => l["module=".Length..])];

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void AssertNoDecoyLoaded()
        {
            Assert.NotEmpty(Modules);
            foreach (var module in Modules)
            {
                Assert.DoesNotContain(decoyFolders, d => module.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Assert.False(string.Equals(Path.GetFileName(module), DllSearchProbe.CwdDecoy, StringComparison.OrdinalIgnoreCase), module);
            }
        }

        /// <summary>Every module comes from Windows, the .NET installation or the test's own folder.</summary>
        public void AssertModulesOnlyFromTrustedFolders()
        {
            var trusted = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), dotnetRoot, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) };
            foreach (var module in Modules)
            {
                Assert.True(trusted.Any(t => module.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "module from an unexpected folder: " + module);
            }
        }
    }

    private string NewTempFolder(string prefix)
    {
        var folder = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _cleanup.Add(folder);
        return folder;
    }

    [SupportedOSPlatform("windows")]
    private static bool Elevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var elevated = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Assert.False(!elevated && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "CI must run these tests elevated.");
        return elevated;
    }

    /// <summary>A System32 DLL that is not a KnownDLL and not loaded yet, so the load really opens the file.</summary>
    private static string NotLoadedSystemDll(string system)
    {
        var loaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => Path.GetFileName(m.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = new[] { "dbghelp.dll", "winmm.dll", "msacm32.dll", "avifil32.dll", "mapi32.dll", "wer.dll" }
            .FirstOrDefault(n => !loaded.Contains(n) && File.Exists(Path.Combine(system, n)));
        Assert.True(name is not null, "no unloaded System32 DLL to try");
        return name!;
    }

    private static string AssemblyPath(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)?.Location is { Length: > 0 } location
            ? location
            : Path.Combine(AppContext.BaseDirectory, name + ".dll");

    private static string WindowsFolder()
    {
        var tfmFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(tfmFolder, "..", "..", "..", ".."));
    }

    /// <summary>
    /// Nexus.Tray (net10.0-windows) is not referenced by the tests, so "dotnet test" may not build it (CI): its
    /// build output next to ours if present, else null and <see cref="TraySourcesAreChecked"/> checks its sources.
    /// </summary>
    private static string? TrayAssembly()
    {
        var tfmFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var configuration = Path.GetFileName(Path.GetDirectoryName(tfmFolder)!);
        var tray = Path.Combine(WindowsFolder(), "Nexus.Tray", "bin", configuration, "net10.0-windows", "Nexus.Tray.dll");
        return File.Exists(tray) ? tray : null;
    }

    /// <summary>
    /// Without Nexus.Tray.dll: its sources (all *.cs outside bin/obj) must set the assembly attribute to System32,
    /// never set another search path, and never import nvml. No sources either is a failure, not a pass.
    /// </summary>
    private void TraySourcesAreChecked()
    {
        var folder = Path.Combine(WindowsFolder(), "Nexus.Tray");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal)).ToList()
            : [];
        Assert.True(files.Count > 0, "neither Nexus.Tray.dll nor the Nexus.Tray sources were found under " + folder);
        output.WriteLine($"Nexus.Tray.dll not built: checking {files.Count} source files");
        var assemblyAttribute = new System.Text.RegularExpressions.Regex(
            @"\[\s*assembly\s*:\s*(System\.Runtime\.InteropServices\.)?DefaultDllImportSearchPaths(Attribute)?\s*\(\s*(System\.Runtime\.InteropServices\.)?DllImportSearchPath\.System32\s*\)\s*\]");
        var anySearchPaths = new System.Text.RegularExpressions.Regex(@"DefaultDllImportSearchPaths(Attribute)?\s*\(([^)]*)\)");
        var nvmlImport = new System.Text.RegularExpressions.Regex(@"(DllImport|LibraryImport)(Attribute)?\s*\(\s*""[^""]*nvml",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var found = false;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            found |= assemblyAttribute.IsMatch(text);
            Assert.False(nvmlImport.IsMatch(text), file + " imports nvml by name");
            foreach (System.Text.RegularExpressions.Match match in anySearchPaths.Matches(text))
            {
                Assert.True(match.Groups[2].Value.Trim().EndsWith("DllImportSearchPath.System32", StringComparison.Ordinal), $"{file}: {match.Value} is not System32 only");
            }
        }
        Assert.True(found, "Nexus.Tray must have [assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]");
    }

    private static IEnumerable<string> OurAssemblies() =>
        [.. new[] { "Nexus.Core", "Nexus.Agent", "Nexus.Windows", "Nexus.Monitoring", "Nexus.RGB", "NexusPC-Setup", "Nexus.DevClient", "Nexus.Tests" }.Select(AssemblyPath), .. TrayAssembly() is { } tray ? [tray] : Array.Empty<string>()];

    /// <summary>DllImport and LibraryImport (its generated DllImport) both end up as P/Invoke metadata.</summary>
    private static List<(string Method, string Module, int? Own)> PInvokes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var result = new List<(string, string, int?)>();
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            var import = method.GetImport();
            if (import.Module.IsNil)
            {
                continue;
            }
            result.Add((md.GetString(method.Name), md.GetString(md.GetModuleReference(import.Module).Name), SearchPaths(md, method.GetCustomAttributes())));
        }
        return result;
    }

    private static int? AssemblySearchPaths(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return SearchPaths(md, md.GetAssemblyDefinition().GetCustomAttributes());
    }

    private static int? SearchPaths(MetadataReader md, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }
            var constructor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind == HandleKind.TypeReference
                && md.GetString(md.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name) == nameof(DefaultDllImportSearchPathsAttribute))
            {
                var value = md.GetBlobReader(attribute.Value);
                value.ReadUInt16(); // prolog
                return value.ReadInt32();
            }
        }
        return null;
    }

    // ---- fakes ----

    private sealed class FakeFile : IOpenedLibraryFile
    {
        public bool IsReparsePoint { get; init; }

        public bool IsDirectory { get; init; }

        public string? FinalPath { get; init; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeLoader : INativeLibraryLoader
    {
        public static readonly IntPtr Handle = 0x1234;

        public Dictionary<string, FakeFile> Files { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IntPtr> Exports { get; } = new(StringComparer.Ordinal);

        public List<string> Opened { get; } = [];

        public List<string> Loaded { get; } = [];

        public List<IntPtr> Freed { get; } = [];

        public bool LoadSucceeds { get; init; } = true;

        public IOpenedLibraryFile? Open(string path)
        {
            Opened.Add(path);
            return Files.GetValueOrDefault(path);
        }

        public bool TryLoad(string absolutePath, out IntPtr handle)
        {
            Assert.True(Path.IsPathFullyQualified(absolutePath), absolutePath);
            Assert.True(Files.TryGetValue(absolutePath, out var file) && !file.Disposed, "loaded while the checked file is held open");
            Loaded.Add(absolutePath);
            handle = LoadSucceeds ? Handle : IntPtr.Zero;
            return LoadSucceeds;
        }

        public bool TryGetExport(IntPtr handle, string name, out IntPtr address)
        {
            Assert.Equal(Handle, handle);
            return Exports.TryGetValue(name, out address);
        }

        public void Free(IntPtr handle) => Freed.Add(handle);
    }

    /// <summary>NVML stand-ins as real unmanaged (cdecl) function pointers, so the reader's calls run on any OS.</summary>
    private static class FakeNvml
    {
        private static readonly List<Delegate> Keep = [];
        private static int _initResult;

        public static int Inits { get; private set; }

        public static int Shutdowns { get; private set; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndex(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Name(IntPtr device, IntPtr buffer, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Utilization(IntPtr device, out NvmlReader.Utilization value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Temperature(IntPtr device, int sensor, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Memory(IntPtr device, out NvmlReader.MemoryInfo value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Clock(IntPtr device, int type, out uint mhz);

        public static FakeLoader Loader(int initResult = 0)
        {
            _initResult = initResult;
            Inits = 0;
            Shutdowns = 0;
            var loader = new FakeLoader();
            loader.Files[First] = new FakeFile { FinalPath = First };
            void Add(string name, Delegate function)
            {
                Keep.Add(function);
                loader.Exports[name] = Marshal.GetFunctionPointerForDelegate(function);
            }
            Add("nvmlInit_v2", new NoArgs(() => { Inits++; return _initResult; }));
            Add("nvmlShutdown", new NoArgs(() => { Shutdowns++; return 0; }));
            Add("nvmlDeviceGetCount_v2", new Count((out uint count) => { count = 1; return 0; }));
            Add("nvmlDeviceGetHandleByIndex_v2", new HandleByIndex((uint index, out IntPtr device) => { device = 0x77 + (nint)index; return 0; }));
            Add("nvmlDeviceGetName", new Name((device, buffer, length) =>
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("Fake RTX\0");
                Marshal.Copy(bytes, 0, buffer, Math.Min(bytes.Length, (int)length));
                return device == 0x77 ? 0 : 1;
            }));
            Add("nvmlDeviceGetUtilizationRates", new Utilization((IntPtr _, out NvmlReader.Utilization value) => { value = new NvmlReader.Utilization { Gpu = 42, Memory = 10 }; return 0; }));
            Add("nvmlDeviceGetTemperature", new Temperature((IntPtr _, int _, out uint value) => { value = 61; return 0; }));
            Add("nvmlDeviceGetMemoryInfo", new Memory((IntPtr _, out NvmlReader.MemoryInfo value) => { value = new NvmlReader.MemoryInfo { Total = 8UL << 30, Used = 3UL << 30, Free = 5UL << 30 }; return 0; }));
            Add("nvmlDeviceGetClockInfo", new Clock((IntPtr _, int type, out uint mhz) => { mhz = type == 0 ? 1800u : 7000u; return 0; }));
            return loader;
        }
    }
}
