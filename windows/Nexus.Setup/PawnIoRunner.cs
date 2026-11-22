using System.Runtime.Versioning;
using System.Security.Cryptography;
using Nexus.Windows;

namespace Nexus.Setup;

/// <param name="Exit">PawnIO installer exit code; null when it was not run (or was stopped).</param>
internal sealed record PawnIoRun(bool Started, int? Exit, bool RevocationUnknown, string Reason);

/// <summary>
/// T5 section 4: runs exactly the checked PawnIO_setup.exe. The caller passes the handle opened with FileShare.Read right
/// after the file was written to staging; it stays open until the PawnIO process has exited, so the file cannot be
/// written, deleted or renamed between the checks and the end of the run. Checks go through that handle: SHA-256, then
/// WinVerifyTrust (with the same handle) and the exact leaf certificate. The process is created suspended in a kill-on-close job; its image path is compared
/// with the staging path before it resumes; a different image → terminated without ever running.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PawnIoRunner
{
    public static PawnIoRun Run(FileStream held, string stagedPath, PawnIoPin pin, IReadOnlyList<string> arguments, TextWriter log, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(stagedPath);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(log);
        held.Position = 0;
        var fileSha = Convert.ToHexStringLower(SHA256.HashData(held));
        var verdict = PawnIoPolicy.Check(pin, fileSha, revocation =>
        {
            var result = Authenticode.Verify(held.SafeFileHandle, stagedPath, revocation);
            log.WriteLine($"WinVerifyTrust (revocation {(revocation ? "checked" : "not checked")}): 0x{result.Code:X8} {result.LeafSubject} {result.LeafSha256}");
            return (result.Code, result.LeafSha256);
        });
        log.WriteLine($"PawnIO {pin.Version} file SHA-256 {fileSha}: {verdict.Reason}");
        if (!verdict.Allowed)
        {
            return new PawnIoRun(false, null, false, verdict.Reason);
        }

        // L1/L2: created suspended inside a kill-on-close job; the image is compared before the first instruction runs.
        using var process = GuardedProcess.StartSuspended(stagedPath, arguments, Path.GetDirectoryName(stagedPath)!);
        var image = Authenticode.ImagePath(process.Process);
        log.WriteLine($"PawnIO process {process.Id} image (suspended): {image ?? "(unknown)"}");
        if (!PawnIoPolicy.SameImage(image, stagedPath))
        {
            process.Terminate();
            return new PawnIoRun(false, null, false, $"started image {image ?? "(unknown)"} is not {stagedPath}");
        }
        process.Resume();
        var exit = process.WaitForExit(timeout);
        if (exit is null)
        {
            process.Terminate();
            return new PawnIoRun(true, null, verdict.RevocationUnknown, "PawnIO setup did not finish in time");
        }
        return new PawnIoRun(true, exit, verdict.RevocationUnknown, verdict.Reason);
    }
}
