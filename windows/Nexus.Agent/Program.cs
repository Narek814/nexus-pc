using System.Text.Json.Nodes;
using Nexus.Agent.Admin;
using Nexus.Agent.Hosting;
using Nexus.Agent.Storage;
using Nexus.Core.Pairing;

namespace Nexus.Agent;

internal static class Program
{
    private const string Usage = """
        Nexus PC Agent

          Nexus.Agent                       run the agent (as a Windows service or in this console)
          Nexus.Agent pair                  open pairing for 2 minutes and approve a device
          Nexus.Agent devices               list paired devices
          Nexus.Agent devices revoke <id>   remove a device; its connections lose access at once
          Nexus.Agent devices permissions <id> <p1,p2,...>
                                            set permissions: power, apps, monitoring, input, screen,
                                            files, clipboard, rgb, automation, ai (or "none")
          Nexus.Agent status                show agent status
          Nexus.Agent openrgb               open the OpenRGB window installed with Nexus PC (device set-up);
                                            lighting from the phone keeps working while it is open

        Admin commands talk to the running agent and need an elevated (Administrator) prompt.
        """;

    // Readable console output: keep Cyrillic and '+' as is (display only, never sent anywhere).
    private static readonly System.Text.Json.JsonSerializerOptions ConsoleJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            var app = AgentHost.Build(args, new AgentHostOptions { DataDirectory = AgentPaths.DefaultDataDirectory() });
            await app.RunAsync().ConfigureAwait(false);
            return 0;
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
                "status" => await PrintAsync(new JsonObject { ["op"] = "status" }).ConfigureAwait(false),
                "openrgb" when args.Length == 1 => await OpenRgbWindowAsync().ConfigureAwait(false),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"Cannot reach the agent: {ex.Message}\nIs the service running, and is this prompt elevated (Run as administrator)?").ConfigureAwait(false);
            return 2;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 1;
    }

    private static bool TryCopyToClipboard(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            using var clip = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("clip.exe")
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
