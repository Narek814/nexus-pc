using System.Text.Json.Nodes;
using Nexus.Core.Session;

namespace Nexus.Core.Account;

/// <summary>
/// E4-6 (S2): "Sign in" and "Unlink" run in a second, elevated Nexus.Tray.exe started through UAC with one fixed
/// argument. That process sends the operation on the admin pipe and returns only an exit code; the normal tray then
/// asks login.status (which carries the userCode) on the session pipe. So the userCode is never on a command line,
/// in an exit code or in a log. Logic only; the process start is in Nexus.Tray.
/// </summary>
public static class ElevatedAccountCommand
{
    public const string LoginArgument = "--account-login";
    public const string UnlinkArgument = "--account-unlink";

    public const int ExitOk = 0;
    /// <summary>Exit codes 10 + index: the agent's error codes the tray tells apart (anything else is "failed").</summary>
    private const int ErrorBase = 10;
    private static readonly string[] Errors =
    [
        "failed", SessionOps.AdminRequired, "session.unavailable", "disabled", "keyUnavailable", "rate.limited", "pcs.limit", "transient",
        ElevationError,
    ];

    /// <summary>The elevated step itself could not start (not an answer of the service): shown as "could not start the administrator step".</summary>
    public const string ElevationError = "elevation";

    /// <summary>ERROR_CANCELLED: the person declined the administrator prompt.</summary>
    public const int UacCancelled = 1223;

    /// <summary>True only for a declined administrator prompt ("Cancelled"); any other Win32 error is "elevation".</summary>
    public static bool IsUacDeclined(int nativeError) => nativeError == UacCancelled;

    /// <summary>
    /// W-TRAY 6: the tray's own exe for the elevated step. <see cref="ElevationError"/> when the path is missing or the
    /// file is not there (nothing to start), otherwise null. <paramref name="fileExists"/> is File.Exists in the tray.
    /// </summary>
    public static string? CheckElevatedPath(string? processPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return string.IsNullOrWhiteSpace(processPath) || !fileExists(processPath) ? ElevationError : null;
    }

    /// <summary>The one argument for <paramref name="op"/> (login.start or unlink); anything else is refused.</summary>
    public static string ArgumentFor(string op) => op switch
    {
        SessionOps.LoginStart => LoginArgument,
        SessionOps.Unlink => UnlinkArgument,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "not an elevated account operation"),
    };

    /// <summary>The operation of an elevated start, or null when the command line is not exactly one known argument.</summary>
    public static string? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count != 1 ? null : args[0] switch
        {
            LoginArgument => SessionOps.LoginStart,
            UnlinkArgument => SessionOps.Unlink,
            _ => null,
        };
    }

    /// <summary>The admin pipe's answer as an exit code: only success or the error code, nothing from the result.</summary>
    public static int ExitCodeOf(JsonObject? response)
    {
        if (response?["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var success) && success)
        {
            return ExitOk;
        }
        var error = response?["error"] is JsonValue e && e.TryGetValue<string>(out var s) ? s : null;
        return ExitCodeOfError(error);
    }

    public static int ExitCodeOfError(string? error)
    {
        var index = error is null ? -1 : Array.IndexOf(Errors, error == "rateLimited" ? "rate.limited" : error);
        return ErrorBase + Math.Max(index, 0);
    }

    /// <summary>The error code behind a non-zero exit code ("failed" for unknown ones).</summary>
    public static string ErrorOf(int exitCode)
    {
        var index = exitCode - ErrorBase;
        return index >= 0 && index < Errors.Length ? Errors[index] : Errors[0];
    }
}
