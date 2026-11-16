using Nexus.Core.Apps;
using Nexus.Core.Security;

namespace Nexus.Tests;

public class ModelTests
{
    [Fact]
    public void Unknown_permission_name_fails_whole_parse()
    {
        Assert.False(Permissions.TryParse(["power", "root"], out var result));
        Assert.Equal(Permission.None, result);
    }

    [Fact]
    public void Permission_names_round_trip()
    {
        var all = Enum.GetValues<Permission>().Aggregate(Permission.None, (a, p) => a | p);
        Assert.True(Permissions.TryParse(Permissions.ToWireNames(all), out var parsed));
        Assert.Equal(all, parsed);
        Assert.Equal(10, Permissions.ToWireNames(all).Count);
    }

    [Fact]
    public void Default_pairing_permissions_exclude_sensitive_capabilities()
    {
        var sensitive = Permission.Input | Permission.Screen | Permission.Files | Permission.Clipboard | Permission.Automation | Permission.Ai;
        Assert.Equal(Permission.None, Permissions.DefaultAfterPairing & sensitive);
    }

    [Theory]
    [InlineData("Gaming", true)]
    [InlineData("Ночной режим", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("tab\there", false)]
    [InlineData("del\u007F", false)]
    public void Profile_name_rules(string name, bool expected)
    {
        Assert.Equal(expected, ProfileRules.IsValidName(name));
    }

    [Fact]
    public void Profile_name_length_counts_code_points_like_json_schema()
    {
        Assert.True(ProfileRules.IsValidName(string.Concat(Enumerable.Repeat("🎮", 64)))); // 128 UTF-16 units
        Assert.False(ProfileRules.IsValidName(string.Concat(Enumerable.Repeat("🎮", 65))));
        Assert.True(ProfileRules.IsValidName(new string('я', 64)));
    }

    [Fact]
    public void Profile_apps_must_exist_in_registry()
    {
        var registry = new AppRegistry();
        registry.Add(new AppEntry("steam", "Steam", AppLaunchKind.Executable, @"C:\Program Files (x86)\Steam\steam.exe", CloseAllowed: true));

        var profile = new ProfileDefinition("gaming", "Gaming", ["steam", "discord"]);

        Assert.Equal(["discord"], ProfileRules.FindUnknownApps(profile, registry));
    }

    [Fact]
    public void Registry_rejects_invalid_and_duplicate_ids()
    {
        var registry = new AppRegistry();
        Assert.Throws<ArgumentException>(() => registry.Add(new AppEntry("..\\cmd", "x", AppLaunchKind.Executable, "cmd.exe", false)));
        Assert.Throws<ArgumentException>(() => registry.Add(new AppEntry("blank", "x", AppLaunchKind.Executable, " ", false)));
        registry.Add(new AppEntry("obs", "OBS", AppLaunchKind.Executable, "obs64.exe", false));
        Assert.Throws<InvalidOperationException>(() => registry.Add(new AppEntry("obs", "OBS 2", AppLaunchKind.Executable, "obs.exe", false)));
    }
}

public class NetworkAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.0.106", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.19.144.1", true)]
    [InlineData("100.101.102.103", true)]   // Tailscale
    [InlineData("::ffff:192.168.0.106", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("2001:db8::1", false)]
    public void Only_loopback_private_and_tailscale_connections_are_served(string address, bool allowed)
    {
        Assert.Equal(allowed, Nexus.Agent.Hosting.NetworkAddresses.IsAllowedLocalAddress(System.Net.IPAddress.Parse(address)));
    }
}

public class WakeTargetTests
{
    [Theory]
    [InlineData("192.168.0.102", "255.255.255.0", "192.168.0.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    [InlineData("172.16.5.9", "255.255.255.252", "172.16.5.11")]
    public void Broadcast_address_of_the_subnet(string ip, string mask, string expected)
    {
        Assert.Equal(expected, Nexus.Agent.Hosting.NetworkAddresses.BroadcastOf(System.Net.IPAddress.Parse(ip), System.Net.IPAddress.Parse(mask)).ToString());
    }
}
