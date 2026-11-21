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
                "pair.cancel" => PairCancel(state),
                "devices.list" => DevicesList(),
                "devices.revoke" => await DevicesRevokeAsync(request, cancellationToken).ConfigureAwait(false),
                "devices.permissions" => await DevicesPermissionsAsync(request, cancellationToken).ConfigureAwait(false),
                "automations.owners" => AutomationsOwners(),
                "automations.assign" => await AutomationsAssignAsync(request, cancellationToken).ConfigureAwait(false),
                "openrgb.pause" => OpenRgbPause(true),
                "openrgb.resume" => OpenRgbPause(false),
                _ => Error("unknown operation"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>Lets the OpenRGB window (opened from the tray, elevated) take the SDK port, then gives it back.</summary>
    private JsonObject OpenRgbPause(bool pause)
    {
        if (services.OpenRgb is not { State: not "notInstalled" } host)
        {
            return Error("OpenRGB не установлен вместе с Nexus PC");
        }
        if (pause)
        {
            host.Pause(TimeSpan.FromSeconds(60));
        }
        else
        {
            host.Resume();
        }
        return new JsonObject { ["ok"] = true, ["state"] = host.State };
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
        // Addresses as they are now (Wi-Fi may have come up after the service started), not at start-up.
        var bound = identity.Endpoints;
        var port = bound.Count > 0 ? bound[0].Port : 0;
        var hosts = bound.Any(e => e.Address.Equals(IPAddress.Any))
            ? [.. Hosting.NetworkAddresses.EligibleLocalAddresses().Select(a => a.ToString())]
            : bound.Where(e => !IPAddress.IsLoopback(e.Address)).Select(e => e.Address.ToString()).ToList();
        if (hosts.Count == 0)
        {
            hosts = ["127.0.0.1"];
        }
        return new PairingLink(identity.PcId, Truncate(identity.PcName), [.. hosts.Distinct().Take(8)], port, identity.Fingerprint, code);
    }

    private JsonObject Status() => Ok(new JsonObject
    {
        ["pcId"] = services.Identity.PcId.ToString("D"),
        ["pcName"] = services.Identity.PcName,
        ["version"] = services.Identity.Version,
        ["fingerprint"] = services.Identity.Fingerprint,
        ["endpoints"] = new JsonArray([.. services.Identity.Endpoints.Select(e => (JsonNode)e.ToString())]),
        ["addresses"] = new JsonArray([.. Hosting.NetworkAddresses.EligibleLocalAddresses().Select(a => (JsonNode)a.ToString())]),
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

    private JsonObject PairCancel(AdminConnectionState state)
    {
        if (state.PairingSessionId is { } id)
        {
            services.Pairing.Cancel(id);
            state.PairingSessionId = null;
        }
        return Ok([]);
    }

    private JsonObject DevicesList() => Ok(new JsonObject
    {
        ["devices"] = new JsonArray([.. services.Devices.All.Select(d => (JsonNode)new JsonObject
        {
            ["deviceId"] = d.DeviceId.ToString("D"),
            ["name"] = d.Name,
            ["permissions"] = new JsonArray([.. Permissions.ToWireNames(d.Permissions).Select(p => (JsonNode)p)]),
            ["fingerprint"] = DeviceCrypto.DeviceKeyFingerprint(d.PublicKeySpki),
            ["pairedAt"] = d.PairedAt.ToString("O", CultureInfo.InvariantCulture),
        })]),
    });

    private async Task<JsonObject> DevicesRevokeAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var deviceId = ParseDeviceId(request);
        return await ChangeDeviceAsync(deviceId, () => services.Devices.Remove(deviceId), cancellationToken).ConfigureAwait(false)
            ? Ok([])
            : Error("device not found");
    }

    private async Task<JsonObject> DevicesPermissionsAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var names = request["permissions"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
        if (!Permissions.TryParse(names, out var permissions))
        {
            return Error("unknown permission name");
        }
        var deviceId = ParseDeviceId(request);
        return await ChangeDeviceAsync(deviceId, () => services.Devices.SetPermissions(deviceId, permissions), cancellationToken).ConfigureAwait(false)
            ? Ok([])
            : Error("device not found");
    }

    /// <summary>Device changes go through the automations' revocation gate, so no rule step starts on stale rights.</summary>
    private Task<bool> ChangeDeviceAsync(Guid deviceId, Func<bool> change, CancellationToken cancellationToken) =>
        services.Automations is { } automations
            ? automations.ChangeDeviceAsync(deviceId, change, cancellationToken)
            : Task.FromResult(change());

    private JsonObject AutomationsOwners() => services.Automations is { } automations
        ? Ok(new JsonObject { ["automations"] = automations.Ownerless() })
        : Error("automations are not available");

    private async Task<JsonObject> AutomationsAssignAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (services.Automations is not { } automations)
        {
            return Error("automations are not available");
        }
        var automationId = request["automationId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(automationId))
        {
            return Error("automationId is required");
        }
        if (!Guid.TryParse(request["deviceId"]?.GetValue<string>(), out var deviceId))
        {
            return Error("deviceId is not a valid id");
        }
        var error = await automations.AssignOwnerAsync(automationId, deviceId, request["fingerprint"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
        return error is null ? Ok(new JsonObject { ["automationId"] = automationId, ["deviceId"] = deviceId.ToString("D") }) : Error(error);
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
