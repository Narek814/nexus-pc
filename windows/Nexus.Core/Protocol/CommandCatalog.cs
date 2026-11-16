using System.Collections.Frozen;
using System.Text.Json;
using Nexus.Core.Security;

namespace Nexus.Core.Protocol;

public enum RiskClass
{
    /// <summary>Reads state only.</summary>
    Read,
    /// <summary>Changes state, easily reversible (launch an app, lock).</summary>
    Normal,
    /// <summary>Disruptive or privacy-sensitive; requires explicit confirmation.</summary>
    Critical,
}

public sealed record CommandSpec(
    string Type,
    Permission RequiredPermission,
    RiskClass Risk,
    bool RequiresAuth,
    Func<JsonElement, PayloadResult> ParsePayload);

/// <summary>
/// The complete whitelist of commands (ARCHITECTURE.md D2). There is intentionally no
/// "run a command line" or "launch a path" entry. Keep in sync with the schema enum.
/// </summary>
public sealed class CommandCatalog
{
    public static CommandCatalog V1 { get; } = new(
    [
        new("system.hello", Permission.None, RiskClass.Read, RequiresAuth: false, PayloadParsers.Empty),
        new("pair.request", Permission.None, RiskClass.Normal, RequiresAuth: false, PayloadParsers.PairRequest),
        new("auth.begin", Permission.None, RiskClass.Read, RequiresAuth: false, PayloadParsers.AuthBegin),
        new("auth.prove", Permission.None, RiskClass.Read, RequiresAuth: false, PayloadParsers.AuthProve),
        new("pc.status", Permission.Monitoring, RiskClass.Read, true, PayloadParsers.Empty),
        new("metrics.snapshot", Permission.Monitoring, RiskClass.Read, true, PayloadParsers.Empty),
        new("metrics.history", Permission.Monitoring, RiskClass.Read, true, PayloadParsers.MetricsHistory),
        new("alerts.get", Permission.Monitoring, RiskClass.Read, true, PayloadParsers.Empty),
        new("alerts.set", Permission.Monitoring, RiskClass.Normal, true, PayloadParsers.AlertsSet),
        new("alerts.test", Permission.Monitoring, RiskClass.Normal, true, PayloadParsers.Empty),
        new("rgb.status", Permission.Rgb, RiskClass.Read, true, PayloadParsers.Empty),
        new("rgb.apply", Permission.Rgb, RiskClass.Normal, true, PayloadParsers.RgbApply),
        new("rgb.profiles.upsert", Permission.Rgb, RiskClass.Normal, true, PayloadParsers.RgbProfileUpsert),
        new("rgb.profiles.delete", Permission.Rgb, RiskClass.Normal, true, PayloadParsers.ProfileRef),
        new("rgb.profiles.apply", Permission.Rgb, RiskClass.Normal, true, PayloadParsers.ProfileRef),
        new("power.shutdown", Permission.Power, RiskClass.Critical, true, PayloadParsers.Power),
        new("power.restart", Permission.Power, RiskClass.Critical, true, PayloadParsers.Power),
        new("power.sleep", Permission.Power, RiskClass.Normal, true, PayloadParsers.Empty),
        new("power.lock", Permission.Power, RiskClass.Normal, true, PayloadParsers.Empty),
        new("power.cancel", Permission.Power, RiskClass.Normal, true, PayloadParsers.Empty),
        new("apps.list", Permission.Apps, RiskClass.Read, true, PayloadParsers.Empty),
        new("apps.launch", Permission.Apps, RiskClass.Normal, true, PayloadParsers.AppRef),
        new("apps.close", Permission.Apps, RiskClass.Normal, true, PayloadParsers.AppRef),
        new("profiles.list", Permission.Apps, RiskClass.Read, true, PayloadParsers.Empty),
        new("profiles.run", Permission.Apps, RiskClass.Normal, true, PayloadParsers.ProfileRef),
        new("profiles.upsert", Permission.Apps, RiskClass.Normal, true, PayloadParsers.ProfileUpsert),
        new("profiles.delete", Permission.Apps, RiskClass.Normal, true, PayloadParsers.ProfileRef),
    ]);

    private readonly FrozenDictionary<string, CommandSpec> _commands;

    public CommandCatalog(IEnumerable<CommandSpec> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands.ToFrozenDictionary(c => c.Type, StringComparer.Ordinal);
    }

    public IEnumerable<CommandSpec> All => _commands.Values;

    public bool TryGet(string type, out CommandSpec spec) => _commands.TryGetValue(type, out spec!);
}
