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
        // Remote input (P5): full control of the signed-in desktop, so the permission is never granted by default.
        new("input.move", Permission.Input, RiskClass.Normal, true, Input.InputParsers.Move),
        new("input.moveTo", Permission.Input, RiskClass.Normal, true, Input.InputParsers.MoveTo),
        new("input.button", Permission.Input, RiskClass.Normal, true, Input.InputParsers.Button),
        new("input.scroll", Permission.Input, RiskClass.Normal, true, Input.InputParsers.Scroll),
        new("input.text", Permission.Input, RiskClass.Normal, true, Input.InputParsers.Text),
        new("input.key", Permission.Input, RiskClass.Normal, true, Input.InputParsers.Key),
        new("input.media", Permission.Input, RiskClass.Normal, true, Input.InputParsers.Media),
        // Files (P7): only the shared folder Downloads\Nexus PC; not granted by pairing.
        new("files.list", Permission.Files, RiskClass.Read, true, PayloadParsers.Empty),
        new("files.upload.begin", Permission.Files, RiskClass.Normal, true, Files.FileParsers.UploadBegin),
        new("files.upload.chunk", Permission.Files, RiskClass.Normal, true, Files.FileParsers.UploadChunk),
        new("files.upload.finish", Permission.Files, RiskClass.Normal, true, Files.FileParsers.UploadRef),
        new("files.upload.cancel", Permission.Files, RiskClass.Normal, true, Files.FileParsers.UploadRef),
        new("files.read", Permission.Files, RiskClass.Read, true, Files.FileParsers.Read),
        new("files.hash", Permission.Files, RiskClass.Read, true, Files.FileParsers.NameRef),
        // Clipboard (P7): text only, on explicit request; not granted by pairing.
        new("clipboard.get", Permission.Clipboard, RiskClass.Normal, true, PayloadParsers.Empty),
        new("clipboard.set", Permission.Clipboard, RiskClass.Normal, true, Files.FileParsers.ClipboardSet),
        // Remote screen (P5): never granted by pairing; the PC also asks its owner once per device.
        new("screen.start", Permission.Screen, RiskClass.Normal, true, PayloadParsers.Empty),
        new("screen.frame", Permission.Screen, RiskClass.Read, true, Screen.ScreenParsers.Frame),
        new("screen.stop", Permission.Screen, RiskClass.Normal, true, PayloadParsers.Empty),
        new("screen.video", Permission.Screen, RiskClass.Read, true, Screen.ScreenVideoParsers.Video),
        // Automations (P6): rules that run on the PC; creating one also needs the permissions of its actions.
        new("automations.list", Permission.Automation, RiskClass.Read, true, PayloadParsers.Empty),
        new("automations.upsert", Permission.Automation, RiskClass.Normal, true, Automation.AutomationJson.Upsert),
        new("automations.delete", Permission.Automation, RiskClass.Normal, true, Automation.AutomationJson.Ref),
        new("automations.run", Permission.Automation, RiskClass.Normal, true, Automation.AutomationJson.Ref),
        new("automations.stop", Permission.Automation, RiskClass.Normal, true, Automation.AutomationJson.Ref),
        new("history.list", Permission.Monitoring, RiskClass.Read, true, Automation.AutomationJson.HistoryList),
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
