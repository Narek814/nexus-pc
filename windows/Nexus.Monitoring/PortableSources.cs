using System.Net.NetworkInformation;

namespace Nexus.Monitoring;

/// <summary>Disks via DriveInfo: fixed, ready drives only.</summary>
internal static class DiskReader
{
    public static IReadOnlyList<DiskMetrics> Read()
    {
        var disks = new List<DiskMetrics>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.TotalSize > 0)
                {
                    disks.Add(new DiskMetrics { Name = drive.Name, TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears or refuses access is simply not listed.
            }
        }
        return disks;
    }
}

/// <summary>Network throughput from interface byte counters (all non-loopback interfaces that are up).</summary>
internal sealed class NetworkRateReader
{
    private (long Rx, long Tx, DateTimeOffset At)? _last;

    public NetworkMetrics Read(DateTimeOffset now)
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            try
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch (NetworkInformationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        var previous = _last;
        _last = (rx, tx, now);
        if (previous is not { } p || now <= p.At || rx < p.Rx || tx < p.Tx)
        {
            return new NetworkMetrics(); // first sample (or counters reset): no rate yet
        }
        var seconds = (now - p.At).TotalSeconds;
        return new NetworkMetrics { ReceiveBytesPerSecond = (rx - p.Rx) / seconds, SendBytesPerSecond = (tx - p.Tx) / seconds };
    }
}

/// <summary>Non-Windows hosts (development, CI): only what .NET can read portably.</summary>
public sealed class PortableMetricsSource : IMetricsSource
{
    private readonly NetworkRateReader _network = new();

    public MetricsSnapshot Sample(DateTimeOffset now) => new()
    {
        Timestamp = now,
        Disks = DiskReader.Read(),
        Network = _network.Read(now),
        Unavailable = new Dictionary<string, string>
        {
            ["cpu.usage"] = "Доступно только на Windows.",
            ["cpu.temperature"] = "Доступно только на Windows.",
            ["cpu.clock"] = "Доступно только на Windows.",
            ["memory"] = "Доступно только на Windows.",
            ["gpu"] = "Доступно только на Windows.",
        },
    };
}
