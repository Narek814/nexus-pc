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

    /// <summary>Injects mouse/keyboard input into the desktop (SendInput). Fails while the PC is locked.</summary>
    DesktopResult SendInput(IReadOnlyList<Input.InputStep> steps);

    /// <summary>Text on the clipboard, or null when it holds no text (an image, files, nothing).</summary>
    ClipboardRead GetClipboardText();

    DesktopResult SetClipboardText(string text);

    /// <summary>Asks the person at the PC whether this device may see the screen. Blocks until answered.</summary>
    bool ConfirmScreenShare(string deviceName);

    /// <summary>Primary monitor scaled to <paramref name="maxWidth"/>; Unchanged if its pixels hash to <paramref name="knownHash"/>.</summary>
    Screen.ScreenCapture CaptureScreen(int maxWidth, int quality, string? knownHash);
}

public sealed record ClipboardRead(bool Ok, string? Text, string? Message = null);

/// <summary>
/// Handles agent → tray requests. The agent has already authenticated and authorized the phone and
/// validated the payload; this re-checks ids against the user's own list (defense in depth).
/// </summary>
public sealed class TrayCommandHandler(UserAppStore store, IDesktop desktop, TimeProvider? time = null, Files.SharedFolder? files = null, Screen.ScreenApprovals? screenApprovals = null)
{
    /// <summary>A screen session ends by itself when the phone stops asking for frames for this long.</summary>
    public static readonly TimeSpan ScreenIdleEnd = TimeSpan.FromSeconds(10);

    private readonly Lock _screenLock = new();
    private string? _screenViewer;
    private DateTimeOffset _screenLastFrame;
    private volatile bool _screenPaused;

    /// <summary>Raised with the viewer's name when sharing starts, and with null when it ends (tray indicator).</summary>
    public event EventHandler<string?>? ScreenSharingChanged;

    /// <summary>"Запретить показ экрана" in the tray menu: ends a running session and refuses new ones.</summary>
    public bool ScreenPaused
    {
        get => _screenPaused;
        set
        {
            _screenPaused = value;
            if (value)
            {
                EndScreen();
            }
        }
    }

    /// <summary>A notice on the PC when a phone starts controlling the mouse/keyboard after this much quiet.</summary>
    public static readonly TimeSpan InputNoticeAfter = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Input.HeldButtons _held = new();
    private readonly Lock _inputLock = new();
    private DateTimeOffset _lastInput = DateTimeOffset.MinValue;
    private volatile bool _inputPaused;

    /// <summary>The person at the PC switched phone control off in the tray menu ("Стоп").</summary>
    public bool InputPaused
    {
        get => _inputPaused;
        set => _inputPaused = value;
    }

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
            _ when op.StartsWith("input.", StringComparison.Ordinal) => Input(op, args),
            _ when op.StartsWith("files.", StringComparison.Ordinal) => Files(op, args),
            "clipboard.get" => ClipboardGet(),
            "screen.start" => ScreenStart(args),
            "screen.frame" => ScreenFrame(args),
            "screen.stop" => ScreenStop(),
            "clipboard.set" => ClipboardSet(args),
            _ => RpcResult.Failure(ErrorCodes.UnknownCommand, $"Tray does not handle '{op}'."),
        };
        return Task.FromResult(result);
    }

    /// <summary>Called by the tray every second: releases a dragged button and ends an abandoned screen session.</summary>
    public void ReleaseIdleButtons()
    {
        bool ended;
        lock (_screenLock)
        {
            ended = _screenViewer is not null && _time.GetUtcNow() - _screenLastFrame > ScreenIdleEnd;
        }
        if (ended)
        {
            EndScreen();
        }
        var steps = _held.ReleaseIfIdle(_time.GetUtcNow());
        if (steps.Count > 0)
        {
            desktop.SendInput(steps);
        }
    }

    private RpcResult Input(string op, JsonObject args)
    {
        // Same parser as the agent (defense in depth): only whitelisted keys and bounded values.
        if (!CommandCatalog.V1.TryGet(op, out var spec) || spec.RequiredPermission != Security.Permission.Input)
        {
            return RpcResult.Failure(ErrorCodes.UnknownCommand, $"Tray does not handle '{op}'.");
        }
        if (_inputPaused)
        {
            return RpcResult.Failure(ErrorCodes.InputBlocked, "Управление с телефона выключено на ПК (меню Nexus PC в трее).");
        }
        var parsed = spec.ParsePayload(System.Text.Json.JsonSerializer.SerializeToElement(args));
        if (parsed.Value is not { } payload)
        {
            return RpcResult.Failure(ErrorCodes.InvalidPayload, parsed.Error ?? "Invalid input.");
        }

        var now = _time.GetUtcNow();
        bool notice;
        lock (_inputLock)
        {
            notice = now - _lastInput > InputNoticeAfter;
            _lastInput = now;
        }
        if (notice)
        {
            ActionPerformed?.Invoke(this, "Телефон управляет мышью и клавиатурой. Запретить: правой кнопкой по значку Nexus PC.");
        }

        var steps = Nexus.Core.Input.InputPlanner.For(payload);
        _held.Observe(payload, now);
        if (steps.Count == 0)
        {
            return RpcResult.Success([]);
        }
        var result = desktop.SendInput(steps);
        return result.Ok
            ? RpcResult.Success([])
            : RpcResult.Failure(ErrorCodes.InputBlocked, result.Message ?? "Windows did not accept the input.");
    }

    private static bool TryParse(string op, JsonObject args, Security.Permission permission, out object payload, out RpcResult? error)
    {
        payload = null!;
        error = null;
        if (!CommandCatalog.V1.TryGet(op, out var spec) || spec.RequiredPermission != permission)
        {
            error = RpcResult.Failure(ErrorCodes.UnknownCommand, $"Tray does not handle '{op}'.");
            return false;
        }
        var parsed = spec.ParsePayload(System.Text.Json.JsonSerializer.SerializeToElement(args));
        if (parsed.Value is null)
        {
            error = RpcResult.Failure(ErrorCodes.InvalidPayload, parsed.Error ?? "Invalid payload.");
            return false;
        }
        payload = parsed.Value;
        return true;
    }

    private RpcResult Files(string op, JsonObject args)
    {
        if (files is null)
        {
            return RpcResult.Failure(ErrorCodes.FilesFailed, "Папка для файлов недоступна на этом ПК.");
        }
        if (!TryParse(op, args, Security.Permission.Files, out var payload, out var error))
        {
            return error!;
        }
        try
        {
            switch (payload)
            {
                case EmptyPayload:
                    return RpcResult.Success(new JsonObject
                    {
                        ["folder"] = files.FolderPath,
                        ["freeBytes"] = files.FreeBytes,
                        ["files"] = new JsonArray([.. files.List().Select(f => (JsonNode)new JsonObject
                        {
                            ["name"] = f.Name,
                            ["size"] = f.Size,
                            ["modified"] = f.Modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                        })]),
                    });
                case Files.FileUploadBeginPayload begin:
                    var (uploadId, offset) = files.Begin(begin);
                    return RpcResult.Success(new JsonObject { ["uploadId"] = uploadId.ToString("D"), ["offset"] = offset });
                case Files.FileUploadChunkPayload chunk:
                    return RpcResult.Success(new JsonObject { ["received"] = files.Chunk(chunk) });
                case Files.FileUploadRefPayload reference when op == "files.upload.finish":
                    var done = files.Finish(reference.UploadId);
                    ActionPerformed?.Invoke(this, $"Получен файл с телефона: {done.Name}");
                    return RpcResult.Success(new JsonObject { ["name"] = done.Name, ["size"] = done.Size });
                case Files.FileUploadRefPayload reference:
                    files.Cancel(reference.UploadId);
                    return RpcResult.Success([]);
                case Files.FileReadPayload read:
                    var (data, size) = files.Read(read);
                    return RpcResult.Success(new JsonObject
                    {
                        ["data"] = Convert.ToBase64String(data),
                        ["size"] = size,
                        ["eof"] = read.Offset + data.Length >= size,
                    });
                case Files.FileNamePayload name:
                    var (sha256, length) = files.Hash(name.Name);
                    return RpcResult.Success(new JsonObject { ["sha256"] = sha256, ["size"] = length });
                default:
                    return RpcResult.Failure(ErrorCodes.UnknownCommand, $"Tray does not handle '{op}'.");
            }
        }
        catch (Files.SharedFolderException ex)
        {
            return RpcResult.Failure(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RpcResult.Failure(ErrorCodes.FilesFailed, "Ошибка диска на ПК: " + ex.Message);
        }
    }

    private RpcResult ScreenStart(JsonObject args)
    {
        if (_screenPaused)
        {
            return RpcResult.Failure(ErrorCodes.ScreenBlocked, "Показ экрана запрещён на ПК (меню Nexus PC в трее).");
        }
        // Filled in by the agent from the authenticated session, never by the phone.
        var deviceId = args["deviceId"]?.GetValue<string>();
        var deviceName = args["deviceName"]?.GetValue<string>() ?? "Устройство";
        if (deviceId is null || screenApprovals is null)
        {
            return RpcResult.Failure(ErrorCodes.ScreenDenied, "Показ экрана недоступен.");
        }
        if (!screenApprovals.IsApproved(deviceId))
        {
            if (!desktop.ConfirmScreenShare(deviceName))
            {
                return RpcResult.Failure(ErrorCodes.ScreenDenied, "На ПК не разрешили показ экрана.");
            }
            screenApprovals.Approve(deviceId);
        }
        bool started;
        lock (_screenLock)
        {
            started = _screenViewer is null;
            _screenViewer = deviceName;
            _screenLastFrame = _time.GetUtcNow();
        }
        if (started)
        {
            ScreenSharingChanged?.Invoke(this, deviceName);
        }
        return RpcResult.Success([]);
    }

    private RpcResult ScreenFrame(JsonObject args)
    {
        lock (_screenLock)
        {
            if (_screenViewer is null || _screenPaused)
            {
                return RpcResult.Failure(ErrorCodes.ScreenNotStarted, "Показ экрана не начат.");
            }
            _screenLastFrame = _time.GetUtcNow();
        }
        if (!TryParse("screen.frame", args, Security.Permission.Screen, out var payload, out var error))
        {
            return error!;
        }
        var request = (Screen.ScreenFramePayload)payload;
        var frame = desktop.CaptureScreen(request.MaxWidth, request.Quality, request.Known);
        if (!frame.Ok)
        {
            return RpcResult.Failure(ErrorCodes.ScreenBlocked, frame.Message ?? "Экран сейчас недоступен (заблокирован или открыт запрос администратора).");
        }
        var result = new JsonObject { ["width"] = frame.Width, ["height"] = frame.Height, ["hash"] = frame.Hash };
        if (frame.Unchanged)
        {
            result["unchanged"] = true;
        }
        else
        {
            result["jpeg"] = Convert.ToBase64String(frame.Jpeg!);
        }
        return RpcResult.Success(result);
    }

    private RpcResult ScreenStop()
    {
        EndScreen();
        return RpcResult.Success([]);
    }

    private void EndScreen()
    {
        bool ended;
        lock (_screenLock)
        {
            ended = _screenViewer is not null;
            _screenViewer = null;
        }
        if (ended)
        {
            ScreenSharingChanged?.Invoke(this, null);
        }
    }

    private RpcResult ClipboardGet()
    {
        var read = desktop.GetClipboardText();
        if (!read.Ok)
        {
            return RpcResult.Failure(ErrorCodes.ClipboardFailed, read.Message ?? "Буфер обмена занят другой программой.");
        }
        // The person at the PC should know their clipboard was read.
        ActionPerformed?.Invoke(this, "Телефон прочитал буфер обмена");
        return RpcResult.Success(new JsonObject { ["text"] = read.Text });
    }

    private RpcResult ClipboardSet(JsonObject args)
    {
        if (!TryParse("clipboard.set", args, Security.Permission.Clipboard, out var payload, out var error))
        {
            return error!;
        }
        var result = desktop.SetClipboardText(((Files.ClipboardSetPayload)payload).Text);
        if (!result.Ok)
        {
            return RpcResult.Failure(ErrorCodes.ClipboardFailed, result.Message ?? "Буфер обмена занят другой программой.");
        }
        ActionPerformed?.Invoke(this, "Телефон положил текст в буфер обмена");
        return RpcResult.Success([]);
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
