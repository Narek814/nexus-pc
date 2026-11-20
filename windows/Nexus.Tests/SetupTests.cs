using System.IO.Compression;
using Nexus.Setup;
using Xunit;

namespace Nexus.Tests;

public sealed class SetupPlanTests
{
    private const string System32 = @"C:\Windows\system32";
    private const string AgentExe = @"C:\Program Files\NexusPC\Agent\Nexus.Agent.exe";

    [Fact]
    public void Service_is_created_with_a_quoted_path_through_sc_directly()
    {
        var create = SetupPlan.ScCreate(System32, AgentExe);
        Assert.Equal(Path.Combine(System32, "sc.exe"), create.File);
        Assert.Equal(["create", "NexusPCAgent", "binPath=", "\"" + AgentExe + "\"", "start=", "auto", "DisplayName=", "Nexus PC Agent"], create.Args);
    }

    [Theory]
    [InlineData(false, "Установка остановлена")]
    [InlineData(true, "Удаление остановлено")]
    public void Missing_privileges_refuse_the_data_folder_check_without_internal_names(bool uninstall, string stopped)
    {
        Assert.Null(SetupPlan.MissingPrivilegesText([], uninstall));
        var text = SetupPlan.MissingPrivilegesText(["SeBackupPrivilege"], uninstall);
        Assert.NotNull(text);
        Assert.StartsWith(stopped + ", ничего не изменено", text, StringComparison.Ordinal);
        Assert.Contains("проверка папки данных Nexus PC не выполнена", text, StringComparison.Ordinal);
        Assert.Contains("права", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Se", text, StringComparison.Ordinal);
        Assert.Equal(6, SetupPlan.ExitDataDirectoryUntrusted);
    }

    [Fact]
    public void Recheck_texts_tell_that_the_service_is_stopped_and_never_claim_nothing_changed()
    {
        var recheck = SetupPlan.MissingPrivilegesText(["SeBackupPrivilege"], uninstall: false, recheck: true);
        Assert.NotNull(recheck);
        Assert.StartsWith(SetupPlan.RecheckStoppedText, recheck, StringComparison.Ordinal);
        Assert.Contains("Проверка папки данных Nexus PC не выполнена", recheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Se", recheck, StringComparison.Ordinal);
        Assert.Null(SetupPlan.MissingPrivilegesText([], uninstall: false, recheck: true));
        foreach (var text in new[] { recheck, SetupPlan.GateStoppedText(recheck: true) })
        {
            Assert.Contains("служба Nexus PC остановлена", text, StringComparison.Ordinal);
            Assert.Contains("файлы программы уже обновлены", text, StringComparison.Ordinal);
            Assert.Contains("запустите установщик ещё раз", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ничего не изменено", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("служба не остановлена", text, StringComparison.OrdinalIgnoreCase);
        }
        // The first check keeps its wording.
        Assert.Equal("Установка остановлена, ничего не изменено.", SetupPlan.GateStoppedText(recheck: false));
        Assert.Equal(SetupPlan.MissingPrivilegesText(["SeBackupPrivilege"], uninstall: false), SetupPlan.MissingPrivilegesText(["SeBackupPrivilege"], uninstall: false, recheck: false));
        Assert.StartsWith("Установка остановлена, ничего не изменено: проверка папки данных Nexus PC не выполнена", SetupPlan.MissingPrivilegesText(["SeBackupPrivilege"], uninstall: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Recheck_refusal_after_the_openrgb_import_carries_code_6_and_is_not_a_step_failure()
    {
        // R11-1: Program turns these exception types into exit code 5; the recheck refusal must not be one of them.
        Exception refused = new DataDirectoryRecheckException(SetupPlan.ExitDataDirectoryUntrusted, "text of the check");
        Assert.False(refused is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception);
        Assert.Equal(6, ((DataDirectoryRecheckException)refused).ExitCode);
        Assert.Equal("text of the check", refused.Message);
    }

    [Fact]
    public void Firewall_rule_is_for_the_agent_program_on_private_networks_only()
    {
        var add = SetupPlan.FirewallAdd(System32, AgentExe);
        Assert.Equal(Path.Combine(System32, "netsh.exe"), add.File);
        Assert.Contains("profile=private", add.Args);
        Assert.Contains("program=" + AgentExe, add.Args);
        Assert.Contains("localport=47800", add.Args);
        Assert.Contains("dir=in", add.Args);
        Assert.DoesNotContain(add.Args, a => a.Contains("public", StringComparison.OrdinalIgnoreCase) || a.Contains("any", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_command_uses_an_absolute_system_path()
    {
        Command[] all =
        [
            SetupPlan.ScQuery(System32), SetupPlan.ScStop(System32), SetupPlan.ScStart(System32), SetupPlan.ScDelete(System32),
            SetupPlan.ScCreate(System32, AgentExe), SetupPlan.ScConfig(System32, AgentExe), SetupPlan.ScFailure(System32),
            SetupPlan.ScDescription(System32), SetupPlan.FirewallAdd(System32, AgentExe), SetupPlan.FirewallDelete(System32),
            SetupPlan.ListNetworks(System32), SetupPlan.MakePrivate(System32, 9),
        ];
        // A planted sc.exe next to the downloaded installer must never be the one that runs elevated.
        Assert.All(all, c => Assert.StartsWith(System32, c.File, StringComparison.Ordinal));
    }

    [Fact]
    public void Making_a_network_private_takes_only_an_interface_number()
    {
        var command = SetupPlan.MakePrivate(System32, 9);
        Assert.Equal("Set-NetConnectionProfile -InterfaceIndex 9 -NetworkCategory Private", command.Args[^1]);
    }

    [Theory]
    [InlineData(0, "SERVICE_NAME: NexusPCAgent\n        TYPE               : 10  WIN32_OWN_PROCESS\n        STATE              : 4  RUNNING\n", 4)]
    [InlineData(0, "        STATE              : 1  STOPPED\n", 1)]
    [InlineData(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060", null)]
    // Real output from the owner's Russian Windows: the label is translated.
    [InlineData(0, "Имя_службы: NexusPCAgent\n        Тип                : 10  WIN32_OWN_PROCESS\n        Состояние          : 4  RUNNING\n                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)\n        Код_выхода_Win32   : 0  (0x0)\n        Код_выхода_службы  : 0  (0x0)\n        Контрольная_точка  : 0x0\n        Ожидание           : 0x0\n", 4)]
    [InlineData(0, "        Состояние          : 1  STOPPED\n        Код_выхода_Win32   : 1077  (0x435)\n", 1)]
    [InlineData(0, "        Состояние          : 3  STOP_PENDING\n", 3)]
    public void Service_state(int exit, string output, int? state) => Assert.Equal(state, SetupPlan.ServiceState(exit, output));

    [Fact]
    public void Networks_are_parsed_from_powershell_output()
    {
        var networks = SetupPlan.ParseNetworks("9|Public|MERCUSYS_339E|Беспроводная сеть\r\n17|Private|Radmin VPN|Radmin VPN\r\nwarning text\r\n");
        Assert.Equal(2, networks.Count);
        Assert.Equal(new SetupPlan.Network(9, "Public", "MERCUSYS_339E", "Беспроводная сеть"), networks[0]);
    }

    [Theory]
    [InlineData(new string[0], false, false, false)]
    [InlineData(new[] { "--uninstall" }, true, false, false)]
    [InlineData(new[] { "--purge" }, true, true, false)]
    [InlineData(new[] { "--quiet", "--no-tray" }, false, false, true)]
    public void Options(string[] args, bool uninstall, bool purge, bool quiet)
    {
        var options = SetupOptions.Parse(args)!;
        Assert.Equal((uninstall, purge, quiet), (options.Uninstall, options.Purge, options.Quiet));
    }

    [Fact]
    public void Unknown_options_are_refused() => Assert.Null(SetupOptions.Parse(["--install-anything"]));

    private static ZipArchive Zip(params string[] names)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                zip.CreateEntry(name);
            }
        }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    [Fact]
    public void Payload_is_checked_before_anything_is_written()
    {
        using var good = Zip("Agent/Nexus.Agent.exe", "Agent/Nexus.Core.dll", "Tray/Nexus.Tray.exe", "version.txt");
        Assert.Null(SetupPlan.CheckPayload(good));
        using var missing = Zip("Agent/Nexus.Agent.exe");
        Assert.NotNull(SetupPlan.CheckPayload(missing));
        using var traversal = Zip("Agent/Nexus.Agent.exe", "Tray/Nexus.Tray.exe", "Agent/../../Windows/evil.dll");
        Assert.NotNull(SetupPlan.CheckPayload(traversal));
        using var extra = Zip("Agent/Nexus.Agent.exe", "Tray/Nexus.Tray.exe", "autorun.inf");
        Assert.NotNull(SetupPlan.CheckPayload(extra));
        using var bundled = Zip("Agent/Nexus.Agent.exe", "Tray/Nexus.Tray.exe", "OpenRGB/OpenRGB.exe", "OpenRGB/platforms/qwindows.dll", "PawnIO/PawnIO_setup.exe", "version.txt");
        Assert.Null(SetupPlan.CheckPayload(bundled));
        using var otherSetup = Zip("Agent/Nexus.Agent.exe", "Tray/Nexus.Tray.exe", "PawnIO/other.exe");
        Assert.NotNull(SetupPlan.CheckPayload(otherSetup));
    }

    [Fact]
    public void PawnIo_is_installed_silently_from_the_unpacked_file()
    {
        var command = SetupPlan.InstallPawnIo(@"C:\Temp\NexusPC-Setup-1\PawnIO\PawnIO_setup.exe");
        Assert.Equal(["-install", "-silent"], command.Args);
    }

    [Fact]
    public void Standalone_OpenRGB_settings_are_imported_without_plugins_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-orgb-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "AppData", "OpenRGB");
        var target = Path.Combine(root, "NexusPC", "OpenRGB");
        try
        {
            foreach (var file in new[] { "OpenRGB.json", "sizes.ors", "Gaming.orp", Path.Combine("plugins", "evil.dll"), Path.Combine("logs", "a.log"), "x.dll" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, file))!);
                File.WriteAllText(Path.Combine(source, file), "{}");
            }
            Assert.Equal(["Gaming.orp", "OpenRGB.json", "sizes.ors"], SetupPlan.OpenRgbFilesToImport(source, target));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "OpenRGB.json"), "{}");
            Assert.Empty(SetupPlan.OpenRgbFilesToImport(source, target));
            Assert.Empty(SetupPlan.OpenRgbFilesToImport(Path.Combine(root, "none"), Path.Combine(root, "t2")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
