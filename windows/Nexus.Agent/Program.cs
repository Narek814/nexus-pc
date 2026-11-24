using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Agent.Admin;
using Nexus.Agent.Hosting;
using Nexus.Agent.Storage;
using Nexus.Core.Pairing;
using Nexus.Core.Storage;

namespace Nexus.Agent;

internal static partial class Program
{
    /// <summary>LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32 (owner's decision 7, T6).</summary>
    internal const uint DllSearchDirectories = 0x00000200 | 0x00000800;

    private const string Usage = """
        Nexus PC Agent

          Nexus.Agent                       run the agent (as a Windows service or in this console)
          Nexus.Agent pair                  open pairing for 2 minutes and approve a device
          Nexus.Agent devices               list paired devices (with each device's key fingerprint)
          Nexus.Agent devices revoke <id>   remove a device; its connections lose access at once
          Nexus.Agent devices permissions <id> <p1,p2,...>
                                            set permissions: power, apps, monitoring, input, screen,
                                            files, clipboard, rgb, automation, ai (or "none")
          Nexus.Agent automations owners    list automations that have no owner (they do not run)
          Nexus.Agent automations assign <automationId> <deviceId> <fingerprint>
                                            give such an automation to a paired device; the fingerprint
                                            must match the one "devices" shows for it
          Nexus.Agent status                show agent status
          Nexus.Agent key retry             when the PC protection key was unavailable: restart the service to
                                            try again (at most once a minute)
          Nexus.Agent openrgb               open the OpenRGB window installed with Nexus PC (device set-up);
                                            lighting from the phone keeps working while it is open
          Nexus.Agent datadir check         check the data folder (owner, access, links); changes nothing
          Nexus.Agent datadir repair        back up the data folder, verify the backup, then fix its access
          Nexus.Agent datadir repair --continue <backup>
                                            continue an interrupted repair
          Nexus.Agent datadir restore-acl <backup>
                                            put back the access saved by a repair; contents are not touched
                                            (the repair stays unfinished and the service blocked until finished)
          Nexus.Agent datadir finalize <backup>
                                            finish a repair after all checks (folder, access, backup);
                                            puts back the saved service start type as the last step
                                            (datadir option: --data-dir <...\NexusPC>, default C:\ProgramData\NexusPC)

        Admin commands talk to the running agent and need an elevated (Administrator) prompt.
        datadir repair and restore-acl run without the agent: the service must be stopped.
        """;

    // Readable console output: keep Cyrillic and '+' as is (display only, never sent anywhere).
    private static readonly System.Text.Json.JsonSerializerOptions ConsoleJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        // First, before the host, Kestrel, TLS, sensors or any of our P/Invoke load a library: for the whole
        // process, a DLL loaded without its own search flags (by name, or the dependencies of a DLL) is looked for
        // only in the agent's folder and System32, never in the current folder or PATH. It cannot be undone; the
        // agent does not run without it. Only the .NET runtime itself is loaded before this point.
        if (OperatingSystem.IsWindows() && !RestrictDllSearch(out var dllSearchError))
        {
            await Console.Error.WriteLineAsync($"Cannot limit where Windows loads libraries from (error {dllSearchError}). The agent does not start.").ConfigureAwait(false);
            return 3;
        }

        if (args.Length == 0)
        {
            using var app = AgentHost.Build(args, new AgentHostOptions { DataDirectory = AgentPaths.DefaultDataDirectory() });
            var runState = app.Services.GetRequiredService<AgentRunState>();
            // "key.retry" stops the host on purpose: a non-zero exit code, so the service manager's recovery
            // ("restart") starts the agent again. Set before the service reports that it stopped.
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
                {
                    if (runState.ExitCode != 0)
                    {
                        Environment.ExitCode = runState.ExitCode;
                    }
                });
            await app.RunAsync().ConfigureAwait(false);
            return runState.ExitCode;
        }

        try
        {
            return args[0] switch
            {
                "pair" => await PairAsync().ConfigureAwait(false),
                "devices" when args.Length == 1 => await PrintAsync(new JsonObject { ["op"] = "devices.list" }).ConfigureAwait(false),
                "devices" when args.Length == 3 && args[1] == "revoke" =>
                    await PrintAsync(new JsonObject { ["op"] = "devices.revoke", ["deviceId"] = args[2] }).ConfigureAwait(false),
                "devices" when args.Length == 4 && args[1] == "permissions" =>
                    await PrintAsync(new JsonObject
                    {
                        ["op"] = "devices.permissions",
                        ["deviceId"] = args[2],
                        ["permissions"] = args[3] == "none" ? new JsonArray() : new JsonArray([.. args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => (JsonNode)p)]),
                    }).ConfigureAwait(false),
                "automations" when args.Length == 2 && args[1] == "owners" =>
                    await PrintAsync(new JsonObject { ["op"] = "automations.owners" }).ConfigureAwait(false),
                "automations" when args.Length == 5 && args[1] == "assign" =>
                    await PrintAsync(new JsonObject
                    {
                        ["op"] = "automations.assign",
                        ["automationId"] = args[2],
                        ["deviceId"] = args[3],
                        ["fingerprint"] = args[4],
                    }).ConfigureAwait(false),
                "status" => await PrintAsync(new JsonObject { ["op"] = "status" }).ConfigureAwait(false),
                "key" when args.Length == 2 && args[1] == "retry" =>
                    await PrintAsync(new JsonObject { ["op"] = "key.retry" }).ConfigureAwait(false),
                "openrgb" when args.Length == 1 => await OpenRgbWindowAsync().ConfigureAwait(false),
                "datadir" => DataDirectory(args[1..]),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Cannot reach the agent: {ex.Message}\nIs the service running, and is this prompt elevated (Run as administrator)?").ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>
    /// SetDefaultDllDirectories(<see cref="DllSearchDirectories"/>) for this process. Internal for the test that runs
    /// it in a separate child process (the setting stays for the life of the process).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool RestrictDllSearch(out int error)
    {
        if (SetDefaultDllDirectories(DllSearchDirectories))
        {
            error = 0;
            return true;
        }
        error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return false;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 1;
    }

    /// <summary>
    /// "datadir": runs here in the elevated console (the service must be stopped). Errors are reported by
    /// the command itself with its own exit codes, never as "Cannot reach the agent".
    /// </summary>
    private static int DataDirectory(string[] args)
    {
        if (DataDirectoryCommandLine.Parse(args) is not { } command)
        {
            Console.WriteLine("Usage: Nexus.Agent datadir check | repair | repair --continue <backup> | restore-acl <backup> | finalize <backup>  [--data-dir <...\\NexusPC>]");
            return 1;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("datadir works on Windows only.");
            return 1;
        }
        return Nexus.Windows.DataDirectoryCommand.Run(command, new ConsoleRepairConsole());
    }

    private sealed class ConsoleRepairConsole : IRepairConsole
    {
        public void WriteLine(string text) => Console.WriteLine(text);

        public string? ReadLine() => Console.ReadLine();
    }

    private static bool TryCopyToClipboard(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            using var clip = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "clip.exe"))
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (clip is null)
            {
                return false;
            }
            clip.StandardInput.Write(text);
            clip.StandardInput.Close();
            return clip.WaitForExit(3000) && clip.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The service's OpenRGB server steps aside, the window opens with the same settings folder (and serves
    /// the phone meanwhile); when it is closed the service starts its own server again.
    /// </summary>
    private static async Task<int> OpenRgbWindowAsync()
    {
        var exe = Nexus.Agent.Server.OpenRgbHost.DefaultExecutable;
        if (!File.Exists(exe))
        {
            await Console.Error.WriteLineAsync("OpenRGB is not installed with Nexus PC: " + exe).ConfigureAwait(false);
            return 1;
        }
        await using var client = await AdminClient.ConnectAsync(AdminPipeServer.DefaultPipeName, CancellationToken.None).ConfigureAwait(false);
        var paused = await client.SendAsync(new JsonObject { ["op"] = "openrgb.pause" }, CancellationToken.None).ConfigureAwait(false);
        if (paused["ok"]?.GetValue<bool>() != true)
        {
            await Console.Error.WriteLineAsync(paused["error"]?.ToString()).ConfigureAwait(false);
            return 1;
        }
        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        var config = Nexus.Agent.Server.OpenRgbHost.ConfigDirectory(AgentPaths.DefaultDataDirectory());
        foreach (var argument in Nexus.Agent.Server.OpenRgbHost.GuiArguments(config, Nexus.Agent.Server.RgbService.DefaultPort))
        {
            start.ArgumentList.Add(argument);
        }
        try
        {
            using var window = System.Diagnostics.Process.Start(start);
            if (window is not null)
            {
                await window.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await client.SendAsync(new JsonObject { ["op"] = "openrgb.resume" }, CancellationToken.None).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> PrintAsync(JsonObject request)
    {
        await using var client = await AdminClient.ConnectAsync(AdminPipeServer.DefaultPipeName, CancellationToken.None).ConfigureAwait(false);
        var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(response.ToJsonString(ConsoleJson));
        return response["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    private static async Task<int> PairAsync()
    {
        await using var client = await AdminClient.ConnectAsync(AdminPipeServer.DefaultPipeName, CancellationToken.None).ConfigureAwait(false);
        var started = await client.SendAsync(new JsonObject { ["op"] = "pair.start" }, CancellationToken.None).ConfigureAwait(false);
        if (started["ok"]?.GetValue<bool>() != true)
        {
            Console.Error.WriteLine(started["error"]);
            return 1;
        }

        Console.WriteLine("Pairing is open for 2 minutes. Keep this window open.");
        Console.WriteLine();
        Console.WriteLine($"  Code: {started["code"]}");
        Console.WriteLine($"  Link: {started["link"]}");
        Console.WriteLine();
        if (TryCopyToClipboard(started["link"]!.GetValue<string>()))
        {
            Console.WriteLine("The link is copied to the clipboard. (Nexus.Tray shows the same as a QR code.)");
        }
        Console.WriteLine("Waiting for a device...");

        var pending = await client.SendAsync(new JsonObject { ["op"] = "pair.wait" }, CancellationToken.None).ConfigureAwait(false);
        if (pending["ok"]?.GetValue<bool>() != true)
        {
            Console.Error.WriteLine($"No device paired: {pending["error"]}.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Device \"{pending["deviceName"]}\" wants to pair.");
        Console.WriteLine($"Verification code: {pending["verificationCode"]} — it must match the code shown on the device.");
        Console.Write("Approve? [y/N] ");
        var approve = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

        var decided = await client.SendAsync(new JsonObject { ["op"] = "pair.decide", ["approve"] = approve }, CancellationToken.None).ConfigureAwait(false);
        if (decided["ok"]?.GetValue<bool>() != true)
        {
            Console.Error.WriteLine($"Could not complete pairing: {decided["error"]}.");
            return 1;
        }
        Console.WriteLine(decided["approved"]?.GetValue<bool>() == true
            ? $"Paired. Device id: {decided["deviceId"]}. Default permissions: power, apps, monitoring."
            : "Rejected. Nothing was stored.");
        return 0;
    }
}
