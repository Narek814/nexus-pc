using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Nexus.Core.Account;

namespace Nexus.Tray;

/// <summary>
/// E4-6 (S2): "Sign in" and "Unlink" for administrators only. The normal tray starts this same Nexus.Tray.exe
/// elevated (UAC, Verb "runas") with one fixed argument; that process sends the operation on the admin pipe, whose
/// access and token check admit only elevated administrators, and exits with a code (<see cref="ElevatedAccountCommand"/>).
/// Nothing from the answer (the userCode in particular) leaves the elevated process: no window, no log, no output.
/// </summary>
internal static class ElevatedAccountProcess
{
    private const string AdminPipeName = "NexusPC.Admin";

    /// <summary>Normal tray: runs the elevated step and waits for it. Null when the administrator prompt was declined.</summary>
    public static async Task<int?> RunAsync(string op)
    {
        var self = Environment.ProcessPath;
        if (self is null)
        {
            return ElevatedAccountCommand.ExitCodeOfError("failed");
        }
        var start = new ProcessStartInfo(self, ElevatedAccountCommand.ArgumentFor(op))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return ElevatedAccountCommand.ExitCodeOfError("failed");
            }
            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ElevatedAccountCommand.IsUacDeclined(ex.NativeErrorCode))
        {
            return null; // the person declined the administrator prompt
        }
        catch (Win32Exception ex)
        {
            // Only the Win32 code: never the arguments or anything from the account answer.
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPC", "tray.log");
            TrayLog.Write(log, "Administrator step could not start: Win32 error "
                + ex.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ElevatedAccountCommand.ExitCodeOfError("failed");
        }
    }

    /// <summary>Elevated process: one request on the admin pipe, the exit code from its answer.</summary>
    public static int RunElevated(string op)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return ElevatedAccountCommand.ExitCodeOfError(Nexus.Core.Session.SessionOps.AdminRequired);
            }
        }
        try
        {
            // Identification is enough for the service to check this token; it cannot act as this user.
            using var pipe = new NamedPipeClientStream(".", AdminPipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(new JsonObject { ["op"] = op }.ToJsonString());
            var read = reader.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(45)) || read.Result is not { } line)
            {
                return ElevatedAccountCommand.ExitCodeOfError("session.unavailable");
            }
            return ElevatedAccountCommand.ExitCodeOf(JsonNode.Parse(line) as JsonObject);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or System.Text.Json.JsonException
            or AggregateException)
        {
            return ElevatedAccountCommand.ExitCodeOfError("session.unavailable");
        }
    }
}
