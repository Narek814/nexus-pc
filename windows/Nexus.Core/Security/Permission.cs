using System.Diagnostics.CodeAnalysis;

namespace Nexus.Core.Security;

/// <summary>Per-device, per-PC capabilities (SECURITY.md §3.4).</summary>
[Flags]
[SuppressMessage("Naming", "CA1711", Justification = "Domain term for device capabilities, unrelated to .NET code access security.")]
public enum Permission
{
    None = 0,
    Power = 1 << 0,
    Apps = 1 << 1,
    Monitoring = 1 << 2,
    Input = 1 << 3,
    Screen = 1 << 4,
    Files = 1 << 5,
    Clipboard = 1 << 6,
    Rgb = 1 << 7,
    Automation = 1 << 8,
    Ai = 1 << 9,
}

public static class Permissions
{
    /// <summary>Granted right after pairing. Everything else must be enabled explicitly.</summary>
    public const Permission DefaultAfterPairing = Permission.Power | Permission.Apps | Permission.Monitoring;

    private static readonly (string Name, Permission Value)[] WireNames =
    [
        ("power", Permission.Power),
        ("apps", Permission.Apps),
        ("monitoring", Permission.Monitoring),
        ("input", Permission.Input),
        ("screen", Permission.Screen),
        ("files", Permission.Files),
        ("clipboard", Permission.Clipboard),
        ("rgb", Permission.Rgb),
        ("automation", Permission.Automation),
        ("ai", Permission.Ai),
    ];

    /// <summary>Parses wire names. Unknown names fail the whole parse instead of being ignored.</summary>
    public static bool TryParse(IEnumerable<string> names, out Permission result)
    {
        ArgumentNullException.ThrowIfNull(names);
        result = Permission.None;
        foreach (var name in names)
        {
            var match = Array.FindIndex(WireNames, p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (match < 0)
            {
                result = Permission.None;
                return false;
            }
            result |= WireNames[match].Value;
        }
        return true;
    }

    public static IReadOnlyList<string> ToWireNames(Permission permissions) =>
        [.. WireNames.Where(p => permissions.HasFlag(p.Value)).Select(p => p.Name)];
}
