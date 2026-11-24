using System.Globalization;

namespace Nexus.Agent.Security;

/// <summary>Why the PC key could not be opened at start-up (E4-3a): a stable code only, no paths or key data.</summary>
internal sealed record DeviceKeyUnavailable(string Error, int Attempts, DateTimeOffset LastAttemptUtc)
{
    /// <summary>"ExceptionType/0xHRESULT": what a support person needs, nothing from the message text.</summary>
    public static string Code(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.GetType().Name + "/0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Opens the permanent PC key: the first try, then one more after each pause (2, 5, 10 s in production).
/// A key that never opens leaves the agent in "key unavailable" mode; there is no other key to fall back to.
/// </summary>
internal static class DeviceKeyOpener
{
    public static readonly IReadOnlyList<TimeSpan> DefaultPauses = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <returns>The key, or null with <paramref name="failure"/> set.</returns>
    public static IDeviceKey? Open(Func<IDeviceKey> factory, IReadOnlyList<TimeSpan> pauses, TimeProvider time, out DeviceKeyUnavailable? failure)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pauses);
        ArgumentNullException.ThrowIfNull(time);
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                var key = factory();
                failure = null;
                return key;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (attempts > pauses.Count)
                {
                    failure = new DeviceKeyUnavailable(DeviceKeyUnavailable.Code(ex), attempts, time.GetUtcNow());
                    return null;
                }
            }
            var pause = pauses[attempts - 1];
            if (pause > TimeSpan.Zero)
            {
                Thread.Sleep(pause); // start-up only, before the host runs
            }
        }
    }

    /// <summary>
    /// Production source of the key: Windows = the machine CNG key only (TPM, else Software KSP). Elsewhere only the
    /// development file key, and only when NEXUS_DEV_FILE_KEY names it; otherwise there is no key (degraded mode).
    /// Never a fallback from CNG to a file.
    /// </summary>
    public static IDeviceKey OpenDefault(Func<string, string?> environment, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows())
        {
            return DeviceKeyStore.OpenOrCreate();
        }
#if NEXUS_DEV_FILE_KEY
        if (DevFileDeviceKey.PathFromEnvironment(environment) is { } path)
        {
            return DevFileDeviceKey.OpenOrCreate(path, dataDirectory);
        }
#else
        _ = dataDirectory;
#endif
        throw new PlatformNotSupportedException("The PC key needs Windows CNG.");
    }
}
