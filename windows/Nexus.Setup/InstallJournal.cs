using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Setup;

/// <summary>One component replaced by the transaction, as planned before anything moved.</summary>
/// <param name="HadOld">%ProgramFiles%\NexusPC\&lt;Name&gt; existed before: it is moved to &lt;Name&gt;.old-&lt;id&gt;, not deleted.</param>
internal sealed record JournalComponent(string Name, bool HadOld);

/// <summary>
/// %ProgramFiles%\NexusPC\.install-journal.json (T5 section 3). Written before each action (through a temporary file,
/// renamed over the journal and flushed). Recovery never trusts it blindly: names must be known components, the id
/// 32 hex digits and the staging path exactly root\.staging-&lt;32 hex&gt;; anything else → nothing is touched.
/// </summary>
internal sealed record InstallJournal(string Id, string Staging, bool FirstInstall, string State, IReadOnlyList<JournalComponent> Components, string Step = "")
{
    /// <summary>B-4: service, firewall rule and Start menu entry before this run (missing in older journals → all existed).</summary>
    public RegistrationSnapshot Before { get; init; } = RegistrationSnapshot.AllExisted;

    public const string FileName = ".install-journal.json";
    public const string OldInfix = ".old-";

    public const string Prepared = "prepared";
    public const string Stopping = "stopping";
    public const string Replacing = "replacing";
    public const string Registering = "registering";
    public const string Starting = "starting";
    public const string Committed = "committed";

    private static readonly string[] States = [Prepared, Stopping, Replacing, Registering, Starting, Committed];

    /// <summary>Registration may have been changed by the failed run: re-apply it for the old version.</summary>
    public bool RegistrationTouched => State is Registering or Starting or Committed;

    public static string PathIn(string root) => Path.Join(root, FileName);

    public static string OldPath(string root, string component, string id) => Path.Join(root, component + OldInfix + id);

    public string Serialize() => new JsonObject
    {
        ["version"] = 1,
        ["id"] = Id,
        ["staging"] = Staging,
        ["firstInstall"] = FirstInstall,
        ["state"] = State,
        ["step"] = Step,
        ["serviceExisted"] = Before.Service,
        ["firewallExisted"] = Before.FirewallRule,
        ["shortcutExisted"] = Before.Shortcut,
        ["components"] = new JsonArray([.. Components.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["hadOld"] = c.HadOld })]),
    }.ToJsonString();

    public static bool IsId(string id) => id.Length == 32 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Null when malformed or not describing this install root.</summary>
    public static InstallJournal? Parse(string json, string root)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || (int?)o["version"] != 1)
            {
                return null;
            }
            var id = (string?)o["id"] ?? "";
            var staging = (string?)o["staging"] ?? "";
            var state = (string?)o["state"] ?? "";
            if (!IsId(id) || !States.Contains(state)
                || !string.Equals(staging, Path.Join(root, StagingDirectoryName.Prefix + id), StringComparison.OrdinalIgnoreCase)
                || o["components"] is not JsonArray array || o["firstInstall"] is not JsonValue first)
            {
                return null;
            }
            var components = new List<JournalComponent>();
            foreach (var item in array)
            {
                var name = (string?)item?["name"];
                if (name is null || !SetupPlan.Parts.Contains(name) || components.Any(c => c.Name == name) || item?["hadOld"] is not JsonValue hadOld)
                {
                    return null;
                }
                components.Add(new JournalComponent(name, hadOld.GetValue<bool>()));
            }
            static bool Existed(JsonNode? node) => node is not JsonValue v || v.GetValue<bool>();
            return new InstallJournal(id, staging, first.GetValue<bool>(), state, components, (string?)o["step"] ?? "")
            {
                Before = new RegistrationSnapshot(Existed(o["serviceExisted"]), Existed(o["firewallExisted"]), Existed(o["shortcutExisted"])),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Shared with StagingDirectory (which is Windows-only): the staging folder name prefix.</summary>
internal static class StagingDirectoryName
{
    public const string Prefix = ".staging-";
}
