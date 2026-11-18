using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Nexus.Setup;

internal sealed record SetupOptions(bool Uninstall, bool Purge, bool Quiet, bool NoTray, string? From)
{
    public static SetupOptions? Parse(IReadOnlyList<string> args)
    {
        bool uninstall = false, purge = false, quiet = false, noTray = false;
        string? from = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--uninstall": uninstall = true; break;
                case "--purge": uninstall = true; purge = true; break;
                case "--quiet": quiet = true; break;
                case "--no-tray": noTray = true; break;
                case "--from" when i + 1 < args.Count: from = args[++i]; break;
                default: return null;
            }
        }
        return new SetupOptions(uninstall, purge, quiet, noTray, from);
    }
}

/// <summary>A program to run: always an absolute path (System32, or a file the installer itself unpacked), never resolved through PATH or the current folder.</summary>
internal sealed record Command(string File, IReadOnlyList<string> Args)
{
    public override string ToString() => $"{Path.GetFileName(File)} {string.Join(' ', Args)}";
}

/// <summary>Everything the installer does, as data, so it can be checked without Windows.</summary>
internal static partial class SetupPlan
{
    public const string ServiceName = "NexusPCAgent";
    public const string ServiceDisplayName = "Nexus PC Agent";
    public const string FirewallRuleName = "Nexus PC Agent";
    public const int Port = 47800;
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "NexusPC.Tray"; // same value the tray's own "Start with Windows" uses

    public static string InstallRoot(string programFiles) => Path.Combine(programFiles, "NexusPC");

    /// <summary>Start menu entry for all users (C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Nexus PC.lnk).</summary>
    public static string StartMenuShortcut(string commonPrograms) => Path.Combine(commonPrograms, "Nexus PC.lnk");

    public static string AgentExe(string root) => Path.Combine(root, "Agent", "Nexus.Agent.exe");

    public static string TrayExe(string root) => Path.Combine(root, "Tray", "Nexus.Tray.exe");

    private static string Sys(string system32, string exe) => Path.Combine(system32, exe);

    public static Command ScQuery(string system32) => new(Sys(system32, "sc.exe"), ["query", ServiceName]);

    public static Command ScStop(string system32) => new(Sys(system32, "sc.exe"), ["stop", ServiceName]);

    public static Command ScStart(string system32) => new(Sys(system32, "sc.exe"), ["start", ServiceName]);

    public static Command ScDelete(string system32) => new(Sys(system32, "sc.exe"), ["delete", ServiceName]);

    // sc.exe takes "option=" and its value as separate arguments; the path is quoted inside the value
    // because it contains spaces (Program Files). Called directly, not through PowerShell, so the quotes survive.
    public static Command ScCreate(string system32, string agentExe) =>
        new(Sys(system32, "sc.exe"), ["create", ServiceName, "binPath=", $"\"{agentExe}\"", "start=", "auto", "DisplayName=", ServiceDisplayName]);

    public static Command ScConfig(string system32, string agentExe) =>
        new(Sys(system32, "sc.exe"), ["config", ServiceName, "binPath=", $"\"{agentExe}\"", "start=", "auto", "DisplayName=", ServiceDisplayName]);

    public static Command ScDescription(string system32) =>
        new(Sys(system32, "sc.exe"), ["description", ServiceName, "Nexus PC: управление этим ПК с iPhone (только локальная сеть)."]);

    public static Command ScFailure(string system32) =>
        new(Sys(system32, "sc.exe"), ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"]);

    public static Command FirewallDelete(string system32) =>
        new(Sys(system32, "netsh.exe"), ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"]);

    /// <summary>Inbound TCP 47800 for the agent program only, private networks only (never Public).</summary>
    public static Command FirewallAdd(string system32, string agentExe) =>
        new(Sys(system32, "netsh.exe"), ["advfirewall", "firewall", "add", "rule", $"name={FirewallRuleName}", "dir=in", "action=allow",
            $"program={agentExe}", "protocol=TCP", $"localport={Port.ToString(CultureInfo.InvariantCulture)}", "profile=private", "enable=yes"]);

    public static Command ListNetworks(string system32) =>
        new(Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", "Get-NetConnectionProfile | ForEach-Object { \"$($_.InterfaceIndex)|$($_.NetworkCategory)|$($_.Name)|$($_.InterfaceAlias)\" }"]);

    public static Command MakePrivate(string system32, int interfaceIndex) =>
        new(Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", $"Set-NetConnectionProfile -InterfaceIndex {interfaceIndex.ToString(CultureInfo.InvariantCulture)} -NetworkCategory Private"]);

    public static string OpenRgbExe(string root) => Path.Combine(root, "OpenRGB", "OpenRGB.exe");

    /// <summary>PawnIO's own installer, shipped unmodified ("This installer can be redistributed unmodified").</summary>
    public const string PawnIoSetup = "PawnIO/PawnIO_setup.exe";

    public const string PawnIoUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    public static Command InstallPawnIo(string unpackedSetup) => new(unpackedSetup, ["-install", "-silent"]);

    /// <summary>
    /// Files of a standalone OpenRGB's settings (%APPDATA%\OpenRGB) to carry over to the copy Nexus PC runs:
    /// settings, zone sizes, profiles. Never plugins (code that would run with the service's rights) or logs.
    /// Nothing when the bundled copy already has its own settings.
    /// </summary>
    public static IReadOnlyList<string> OpenRgbFilesToImport(string source, string target)
    {
        if (!Directory.Exists(source) || File.Exists(Path.Combine(target, "OpenRGB.json")))
        {
            return [];
        }
        string[] skipped = ["plugins", "logs"];
        return [.. Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(source, file))
            .Where(relative => !skipped.Contains(relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0], StringComparer.OrdinalIgnoreCase))
            .Where(relative => !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>sc query STATE code: 1 stopped, 2 start pending, 3 stop pending, 4 running; null if the service does not exist.</summary>
    public static int? ServiceState(int exitCode, string output)
    {
        if (exitCode == 1060)
        {
            return null; // ERROR_SERVICE_DOES_NOT_EXIST
        }
        var match = StateRegex().Match(output);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public sealed record Network(int Index, string Category, string Name, string Alias);

    public static IReadOnlyList<Network> ParseNetworks(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Where(p => p.Length == 4 && int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(p => new Network(int.Parse(p[0], CultureInfo.InvariantCulture), p[1], p[2], p[3]))];

    /// <summary>Folders of the payload copied to Program Files\NexusPC (OpenRGB is absent from development builds).</summary>
    public static readonly IReadOnlyList<string> Parts = ["Agent", "Tray", "OpenRGB"];

    /// <summary>The payload must contain the agent and the tray, and nothing outside its known folders.</summary>
    public static string? CheckPayload(ZipArchive zip)
    {
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        if (names.Any(n => n.StartsWith('/') || n.Contains("..", StringComparison.Ordinal) || n.Contains(':', StringComparison.Ordinal)))
        {
            return "Повреждённый установщик: недопустимые пути внутри.";
        }
        if (!names.Contains("Agent/Nexus.Agent.exe") || !names.Contains("Tray/Nexus.Tray.exe"))
        {
            return "Повреждённый установщик: нет Nexus.Agent.exe или Nexus.Tray.exe.";
        }
        if (names.Any(n => !Parts.Any(p => n.StartsWith(p + "/", StringComparison.Ordinal)) && n != "version.txt" && n != PawnIoSetup))
        {
            return "Повреждённый установщик: лишние файлы.";
        }
        return null;
    }

    // The label is translated ("STATE", "Состояние"…), but the code and its English name are not:
    // "Состояние          : 4  RUNNING" (found on the owner's Russian Windows).
    [GeneratedRegex(@":\s*([1-7])\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StateRegex();
}
