using System.Text.RegularExpressions;
using Nexus.Core.Automation;

namespace Nexus.Core.Entitlements;

/// <summary>
/// Command → feature (COMMERCIAL_PLAN section 0, matrix accepted with the code-architect recommendations).
/// Free commands need no feature. Pro commands need a feature granted by an active or grace token. The feature
/// ids are a proposal for the owner to approve (ENT-2 report).
/// </summary>
public static partial class PlanPolicy
{
    /// <summary>App profiles (profiles.*).</summary>
    public const string Profiles = "profiles";
    /// <summary>Lighting control beyond status (rgb.apply, rgb.profiles.*).</summary>
    public const string RgbSync = "rgb.sync";
    /// <summary>Automation Studio: everything except automations.list; triggered runs too.</summary>
    public const string Automations = "automations";
    /// <summary>metrics.history 1h and 24h (10m is Free).</summary>
    public const string MonitoringExtended = "monitoring.extended";
    /// <summary>files.* without the Free limit.</summary>
    public const string FilesUnlimited = "files.unlimited";
    /// <summary>Feature named in plan.limit for the Free file transfer limit.</summary>
    public const string Files = "files";

    public static readonly IReadOnlyList<string> ProFeatures = [Profiles, RgbSync, Automations, MonitoringExtended, FilesUnlimited];

    /// <summary>
    /// Free file transfer limit in bytes per UTC day; null = no limit. The owner has not set it yet (OPEN in
    /// COMMERCIAL_PLAN section 0): the counter runs, nothing is refused until a value is chosen.
    /// </summary>
    public static long? FreeFileBytesPerDay => null;

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*(\\.[a-z][a-zA-Z0-9]*){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureIdPattern();

    public static bool IsFeatureId(string s) => s.Length is >= 1 and <= 64 && FeatureIdPattern().IsMatch(s);

    /// <summary>
    /// Feature the command needs, or null for always/Free commands. <paramref name="metricsRange"/> is the
    /// validated range of metrics.history. Unlisted commands (system.hello, pair.*, auth.*, account.refresh,
    /// power.*, pc.status, apps.*, input.*, screen.*, metrics.snapshot, rgb.status, automations.list, files.*,
    /// clipboard.*, history.list) are Free.
    /// </summary>
    public static string? RequiredFeature(string type, string? metricsRange = null) => type switch
    {
        "profiles.list" or "profiles.run" or "profiles.upsert" or "profiles.delete" => Profiles,
        "rgb.apply" or "rgb.profiles.upsert" or "rgb.profiles.delete" or "rgb.profiles.apply" => RgbSync,
        "automations.upsert" or "automations.delete" or "automations.run" or "automations.stop" => Automations,
        "metrics.history" when metricsRange is not "10m" => MonitoringExtended,
        _ => null,
    };

    /// <summary>Commands counted against the Free file transfer limit.</summary>
    public static bool IsFileTransfer(string type) => type is "files.upload.chunk" or "files.read";

    /// <summary>Features a rule needs to run: Automation Studio itself plus what its actions use.</summary>
    public static IReadOnlyList<string> RequiredFor(AutomationDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var needed = new List<string> { Automations };
        foreach (var action in rule.Actions)
        {
            var feature = action.Kind switch
            {
                "profile" => Profiles,
                "rgb" or "rgbProfile" => RgbSync,
                _ => null,
            };
            if (feature is not null && !needed.Contains(feature))
            {
                needed.Add(feature);
            }
        }
        return needed;
    }
}
