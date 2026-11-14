using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Nexus.Agent.Server;
using Nexus.Core.Pairing;
using Nexus.Core.Security;

namespace Nexus.Agent.Admin;

/// <summary>Per admin connection: the pairing session it opened, cancelled when it disconnects.</summary>
internal sealed class AdminConnectionState
{
    public long? PairingSessionId { get; set; }
}

/// <summary>
/// Operations for the person at the PC (run elevated): open pairing, approve, list and revoke devices.
/// Transport-independent so it is tested directly; AdminPipeServer exposes it locally only.
/// </summary>
internal sealed class AdminService(AgentServices services)
{
    public async Task<JsonObject> HandleAsync(JsonObject request, AdminConnectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);
        var op = request["op"]?.GetValue<string>();
        try
        {
            return op switch
            {
                "status" => Status(),
                "pair.start" => PairStart(state),
                "pair.wait" => await PairWaitAsync(state, cancellationToken).ConfigureAwait(false),
                "pair.decide" => PairDecide(request, state),
                "devices.list" => DevicesList(),
                "devices.revoke" => DevicesRevoke(request),
                "devices.permissions" => DevicesPermissions(request),
                _ => Error("unknown operation"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return Error(ex.Message);
        }
    }

    public void ConnectionClosed(AdminConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PairingSessionId is { } id)
        {
            services.Pairing.Cancel(id); // nobody is left to approve, so nothing may be approved
        }
    }

    public PairingLink CreateLink(string code)
    {
        var identity = services.Identity;
        var external = identity.Endpoints.Where(e => !IPAddress.IsLoopback(e.Address)).ToList();
        var shown = external.Count > 0 ? external : [.. identity.Endpoints];
        var port = shown.Count > 0 ? shown[0].Port : 0;
        return new PairingLink(identity.PcId, Truncate(identity.PcName), [.. shown.Select(e => e.Address.ToString()).Distinct().Take(8)], port, identity.Fingerprint, code);
    }

    private JsonObject Status() => Ok(new JsonObject
    {
        ["pcId"] = services.Identity.PcId.ToString("D"),
        ["pcName"] = services.Identity.PcName,
        ["version"] = services.Identity.Version,
        ["fingerprint"] = services.Identity.Fingerprint,
        ["endpoints"] = new JsonArray([.. services.Identity.Endpoints.Select(e => (JsonNode)e.ToString())]),
        ["pairedDevices"] = services.Devices.All.Count,
        ["auditLog"] = services.Audit.FilePath,
    });

    private JsonObject PairStart(AdminConnectionState state)
    {
        var ticket = services.Pairing.Start(services.PairingLifetime);
        state.PairingSessionId = ticket.SessionId;
        return Ok(new JsonObject
        {
            ["link"] = CreateLink(ticket.Code).ToString(),
            ["code"] = ticket.Code,
            ["expiresAt"] = ticket.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
        });
    }

    private async Task<JsonObject> PairWaitAsync(AdminConnectionState state, CancellationToken cancellationToken)
    {
        if (state.PairingSessionId is not { } id)
        {
            return Error("call pair.start first");
        }
        var pending = await services.Pairing.WaitForRequestAsync(id, cancellationToken).ConfigureAwait(false);
        return pending is null
            ? Error("pairing expired")
            : Ok(new JsonObject { ["deviceName"] = pending.DeviceName, ["verificationCode"] = pending.VerificationCode });
    }

    private JsonObject PairDecide(JsonObject request, AdminConnectionState state)
    {
        if (state.PairingSessionId is not { } id)
        {
            return Error("call pair.start first");
        }
        var approve = request["approve"]?.GetValue<bool>() ?? false;
        var before = services.Devices.All.Select(d => d.DeviceId).ToHashSet();
        if (!services.Pairing.Decide(id, approve))
        {
            return Error("no device is waiting for approval");
        }
        state.PairingSessionId = null;
        var added = services.Devices.All.FirstOrDefault(d => !before.Contains(d.DeviceId));
        return Ok(new JsonObject { ["approved"] = approve && added is not null, ["deviceId"] = added?.DeviceId.ToString("D") });
    }

    private JsonObject DevicesList() => Ok(new JsonObject
    {
        ["devices"] = new JsonArray([.. services.Devices.All.Select(d => (JsonNode)new JsonObject
        {
            ["deviceId"] = d.DeviceId.ToString("D"),
            ["name"] = d.Name,
            ["permissions"] = new JsonArray([.. Permissions.ToWireNames(d.Permissions).Select(p => (JsonNode)p)]),
            ["pairedAt"] = d.PairedAt.ToString("O", CultureInfo.InvariantCulture),
        })]),
    });

    private JsonObject DevicesRevoke(JsonObject request) =>
        services.Devices.Remove(ParseDeviceId(request)) ? Ok([]) : Error("device not found");

    private JsonObject DevicesPermissions(JsonObject request)
    {
        var names = request["permissions"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
        if (!Permissions.TryParse(names, out var permissions))
        {
            return Error("unknown permission name");
        }
        return services.Devices.SetPermissions(ParseDeviceId(request), permissions) ? Ok([]) : Error("device not found");
    }

    private static Guid ParseDeviceId(JsonObject request) =>
        Guid.ParseExact(request["deviceId"]?.GetValue<string>() ?? string.Empty, "D");

    private static string Truncate(string name) => name.Length <= 64 ? name : name[..64];

    private static JsonObject Ok(JsonObject result)
    {
        result["ok"] = true;
        return result;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };
}
