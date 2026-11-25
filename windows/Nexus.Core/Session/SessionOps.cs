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
    // Account sign-in of this PC (E4-4): code shown in the tray, status, local unlink.
    public const string LoginStart = "login.start";
    public const string LoginStatus = "login.status";
    public const string Unlink = "unlink";

    /// <summary>
    /// What a Tray may ask the agent. Only operations that open pairing for the signed-in person or
    /// reduce access; granting permissions stays with elevated administrators.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedFromTray = new HashSet<string>(StringComparer.Ordinal)
    {
        Status, PairStart, PairWait, PairDecide, PairCancel, DevicesList, DevicesRevoke,
        LoginStatus,
    };

    /// <summary>
    /// E4-6 (S2): linking this PC to an account and unlinking it are for Windows administrators only. They are
    /// accepted only on the admin pipe, and there only from a client whose own token is an elevated administrator
    /// (or SYSTEM); the session pipe answers <see cref="AdminRequired"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> AdministratorOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        LoginStart, Unlink,
    };

    /// <summary>Error code for an <see cref="AdministratorOnly"/> operation from a client that is not an elevated administrator.</summary>
    public const string AdminRequired = "admin.required";

    /// <summary>How the session pipe answers <paramref name="op"/> before running it: null to run, otherwise the error code.</summary>
    public static string? SessionRefusal(string op) =>
        AllowedFromTray.Contains(op) ? null
        : AdministratorOnly.Contains(op) ? AdminRequired
        : Protocol.ErrorCodes.PermissionDenied;

    /// <summary>
    /// The admin pipe's gate: an <see cref="AdministratorOnly"/> operation runs only when the client's token was found
    /// to be an elevated administrator or SYSTEM. <paramref name="clientIsAdministrator"/> is asked only for those.
    /// </summary>
    public static string? AdminPipeRefusal(string? op, Func<bool> clientIsAdministrator)
    {
        ArgumentNullException.ThrowIfNull(clientIsAdministrator);
        return op is not null && AdministratorOnly.Contains(op) && !clientIsAdministrator() ? AdminRequired : null;
    }
}
