using System.Globalization;

namespace Nexus.Notifications;

/// <summary>
/// Detects that the PC went down without the agent stopping cleanly (power loss, crash, hard reset):
/// a marker file is refreshed while running and removed on a clean stop. If it is still there at the
/// next start, the previous shutdown was unexpected; its last heartbeat tells roughly when.
/// </summary>
public sealed class PowerLifecycle(string markerPath, TimeProvider time)
{
    /// <summary>Call at start. Returns the last heartbeat of an unclean previous run, or null.</summary>
    public DateTimeOffset? Start()
    {
        DateTimeOffset? unclean = null;
        if (File.Exists(markerPath))
        {
            unclean = DateTimeOffset.TryParse(File.ReadAllText(markerPath), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                ? last
                : File.GetLastWriteTimeUtc(markerPath);
        }
        Heartbeat();
        return unclean;
    }

    public void Heartbeat() => File.WriteAllText(markerPath, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));

    public void CleanStop()
    {
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }
}
