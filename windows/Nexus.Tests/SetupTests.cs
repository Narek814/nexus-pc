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
    [InlineData("Microsoft.AspNetCore.App 10.0.15 [C:\\x]\nMicrosoft.NETCore.App 10.0.15 [C:\\x]\nMicrosoft.WindowsDesktop.App 10.0.15 [C:\\x]\n", "")]
    [InlineData("Microsoft.NETCore.App 10.0.15 [C:\\x]\nMicrosoft.WindowsDesktop.App 10.0.15 [C:\\x]\n", "Microsoft.AspNetCore.App")]
    [InlineData("Microsoft.AspNetCore.App 8.0.1 [C:\\x]\nMicrosoft.WindowsDesktop.App 8.0.1 [C:\\x]\n", "Microsoft.AspNetCore.App,Microsoft.WindowsDesktop.App")]
    public void Runtime_check(string output, string missing) =>
        Assert.Equal(missing, string.Join(',', SetupPlan.MissingRuntimes(output.Replace("\n", "\r\n", StringComparison.Ordinal))));

    [Theory]
    [InlineData(0, "SERVICE_NAME: NexusPCAgent\n        TYPE               : 10  WIN32_OWN_PROCESS\n        STATE              : 4  RUNNING\n", 4)]
    [InlineData(0, "        STATE              : 1  STOPPED\n", 1)]
    [InlineData(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060", null)]
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
    }
}
