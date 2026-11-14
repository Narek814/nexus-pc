namespace Nexus.Core.Session;

/// <summary>Operation names on the Agent ↔ Tray session channel.</summary>
public static class SessionOps
{
    // Agent → Tray: work that must happen in the user's desktop session.
    public const string AppsList = "apps.list";
    public const string AppsLaunch = "apps.launch";
    public const string AppsClose = "apps.close";
    public const string ProfilesList = "profiles.list";
    public const string ProfilesRun = "profiles.run";
    public const string ProfilesUpsert = "profiles.upsert";
    public const string ProfilesDelete = "profiles.delete";
    public const string Lock = "power.lock";
    public const string PowerSchedule = "power.schedule";

    // Tray → Agent: the person at the PC managing pairing and devices.
    public const string Status = "status";
    public const string PairStart = "pair.start";
    public const string PairWait = "pair.wait";
    public const string PairDecide = "pair.decide";
    public const string PairCancel = "pair.cancel";
    public const string DevicesList = "devices.list";
    public const string DevicesRevoke = "devices.revoke";

    /// <summary>
    /// What a Tray may ask the agent. Only operations that open pairing for the signed-in person or
    /// reduce access; granting permissions stays with elevated administrators.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedFromTray = new HashSet<string>(StringComparer.Ordinal)
    {
        Status, PairStart, PairWait, PairDecide, PairCancel, DevicesList, DevicesRevoke,
    };
}
