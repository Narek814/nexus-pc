using System.Text.Json.Nodes;
using Nexus.Core.Power;
using Nexus.Core.Protocol;
using Nexus.Core.Session;

namespace Nexus.Core.Apps;

public sealed record DesktopResult(bool Ok, string? Message = null)
{
    public static DesktopResult Success { get; } = new(true);
}

/// <summary>What Nexus.Tray does on the Windows desktop. A fake implements it in tests.</summary>
public interface IDesktop
{
    DesktopResult Launch(AppEntry app);

    /// <summary>Asks the app's windows to close (like clicking ×). Never kills: unsaved work stays safe.</summary>
    DesktopResult RequestClose(AppEntry app);

    DesktopResult Lock();

    /// <summary>Shutdown/restart from the user's own session, so apps can ask to save first.</summary>
    PowerOutcome SchedulePower(PowerAction action, TimeSpan delay, bool force, string reason);
}

/// <summary>
/// Handles agent → tray requests. The agent has already authenticated and authorized the phone and
/// validated the payload; this re-checks ids against the user's own list (defense in depth).
/// </summary>
public sealed class TrayCommandHandler(UserAppStore store, IDesktop desktop)
{
    /// <summary>Fired after an action on behalf of a phone, for a small notice in the tray.</summary>
    public event EventHandler<string>? ActionPerformed;

    public Task<RpcResult> HandleAsync(string op, JsonObject args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = op switch
        {
            SessionOps.AppsList => RpcResult.Success(new JsonObject { ["apps"] = AppsJson() }),
            SessionOps.AppsLaunch => WithApp(args, Launch),
            SessionOps.AppsClose => WithApp(args, Close),
            SessionOps.ProfilesList => RpcResult.Success(new JsonObject { ["profiles"] = ProfilesJson() }),
            SessionOps.ProfilesRun => RunProfile(args),
            SessionOps.ProfilesUpsert => UpsertProfile(args),
            SessionOps.ProfilesDelete => DeleteProfile(args),
            SessionOps.Lock => FromDesktop(desktop.Lock(), ErrorCodes.LockFailed, "Workstation locked"),
            SessionOps.PowerSchedule => SchedulePower(args),
            _ => RpcResult.Failure(ErrorCodes.UnknownCommand, $"Tray does not handle '{op}'."),
        };
        return Task.FromResult(result);
    }

    private JsonArray AppsJson() =>
        // Names and capabilities only: local paths never leave the PC.
        [.. store.Apps.Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id,
            ["name"] = a.DisplayName,
            ["canClose"] = a.CloseAllowed && a.ProcessPath is not null,
        })];

    private JsonArray ProfilesJson() =>
        [.. store.Profiles.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["appIds"] = new JsonArray([.. p.AppIds.Select(id => (JsonNode)id)]),
        })];

    private RpcResult WithApp(JsonObject args, Func<AppEntry, RpcResult> action)
    {
        var id = args["appId"]?.GetValue<string>();
        return Identifiers.IsSlug(id) && store.FindApp(id!) is { } app
            ? action(app)
            : RpcResult.Failure(ErrorCodes.AppNotFound, $"App '{id}' is not in the list on this PC.");
    }

    private RpcResult Launch(AppEntry app)
    {
        var result = desktop.Launch(app);
        if (result.Ok)
        {
            ActionPerformed?.Invoke(this, $"Started {app.DisplayName}");
        }
        return result.Ok
            ? RpcResult.Success(new JsonObject { ["appId"] = app.Id, ["launched"] = true })
            : RpcResult.Failure(ErrorCodes.AppLaunchFailed, result.Message ?? $"Could not start {app.DisplayName}.");
    }

    private RpcResult Close(AppEntry app)
    {
        if (!app.CloseAllowed || app.ProcessPath is null)
        {
            return RpcResult.Failure(ErrorCodes.AppCloseNotAllowed, $"Closing {app.DisplayName} from a phone is not allowed on this PC.");
        }
        var result = desktop.RequestClose(app);
        return result.Ok
            ? RpcResult.Success(new JsonObject { ["appId"] = app.Id, ["closeRequested"] = true, ["detail"] = result.Message })
            : RpcResult.Failure(ErrorCodes.AppLaunchFailed, result.Message ?? "Could not close the app.");
    }

    private RpcResult RunProfile(JsonObject args)
    {
        var id = args["profileId"]?.GetValue<string>();
        if (!Identifiers.IsSlug(id) || store.FindProfile(id!) is not { } profile)
        {
            return RpcResult.Failure(ErrorCodes.ProfileNotFound, $"Profile '{id}' does not exist on this PC.");
        }

        var results = new JsonArray();
        var started = 0;
        foreach (var appId in profile.AppIds)
        {
            var item = new JsonObject { ["appId"] = appId };
            if (store.FindApp(appId) is not { } app)
            {
                item["ok"] = false;
                item["error"] = ErrorCodes.AppNotFound;
            }
            else
            {
                var launched = desktop.Launch(app);
                item["ok"] = launched.Ok;
                if (launched.Ok)
                {
                    started++;
                }
                else
                {
                    item["error"] = ErrorCodes.AppLaunchFailed;
                    item["message"] = launched.Message;
                }
            }
            results.Add(item);
        }
        ActionPerformed?.Invoke(this, $"Profile {profile.Name}: started {started} of {profile.AppIds.Count}");
        return RpcResult.Success(new JsonObject { ["profileId"] = profile.Id, ["started"] = started, ["results"] = results });
    }

    private RpcResult UpsertProfile(JsonObject args)
    {
        if (args["profile"] is not JsonObject p
            || p["id"]?.GetValue<string>() is not { } id || !Identifiers.IsSlug(id)
            || p["name"]?.GetValue<string>() is not { } name || !ProfileRules.IsValidName(name)
            || p["appIds"] is not JsonArray appIdsJson || appIdsJson.Count > ProfileRules.MaxApps)
        {
            return RpcResult.Failure(ErrorCodes.InvalidPayload, "Invalid profile.");
        }
        var appIds = appIdsJson.Select(n => n?.GetValue<string>() ?? string.Empty).ToList();
        if (appIds.Any(a => !Identifiers.IsSlug(a)) || appIds.Distinct(StringComparer.Ordinal).Count() != appIds.Count)
        {
            return RpcResult.Failure(ErrorCodes.InvalidPayload, "Invalid app ids.");
        }
        try
        {
            var unknown = store.UpsertProfile(new ProfileDefinition(id, name, appIds));
            return unknown.Count == 0
                ? RpcResult.Success(new JsonObject { ["profileId"] = id })
                : RpcResult.Failure(ErrorCodes.ProfileInvalid, $"Not in the app list on this PC: {string.Join(", ", unknown)}.");
        }
        catch (InvalidOperationException ex)
        {
            return RpcResult.Failure(ErrorCodes.ProfileLimit, ex.Message);
        }
    }

    private RpcResult DeleteProfile(JsonObject args)
    {
        var id = args["profileId"]?.GetValue<string>();
        return Identifiers.IsSlug(id) && store.DeleteProfile(id!)
            ? RpcResult.Success(new JsonObject { ["profileId"] = id })
            : RpcResult.Failure(ErrorCodes.ProfileNotFound, $"Profile '{id}' does not exist on this PC.");
    }

    private RpcResult SchedulePower(JsonObject args)
    {
        var action = args["action"]?.GetValue<string>() switch
        {
            "restart" => PowerAction.Restart,
            "shutdown" => PowerAction.Shutdown,
            _ => (PowerAction?)null,
        };
        var delay = args["delaySeconds"]?.GetValue<int>() ?? -1;
        if (action is null || delay is < 0 or > PowerPayload.MaxDelaySeconds)
        {
            return RpcResult.Failure(ErrorCodes.InvalidPayload, "Invalid power request.");
        }
        var reason = args["reason"]?.GetValue<string>() ?? "Nexus PC";
        var outcome = desktop.SchedulePower(action.Value, TimeSpan.FromSeconds(delay), args["force"]?.GetValue<bool>() ?? false, reason.Length > 200 ? reason[..200] : reason);
        return outcome.Ok ? RpcResult.Success() : RpcResult.Failure(outcome.ErrorCode ?? ErrorCodes.PowerFailed, outcome.Message ?? "Power action failed.");
    }

    private RpcResult FromDesktop(DesktopResult result, string errorCode, string notice)
    {
        if (result.Ok)
        {
            ActionPerformed?.Invoke(this, notice);
            return RpcResult.Success();
        }
        return RpcResult.Failure(errorCode, result.Message ?? "Failed.");
    }
}
