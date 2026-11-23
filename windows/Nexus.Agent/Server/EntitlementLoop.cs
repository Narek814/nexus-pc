using Microsoft.Extensions.Hosting;
using Nexus.Core.Entitlements;

namespace Nexus.Agent.Server;

/// <summary>Advances the entitlement clock once a minute (uptime budget, rollback detection, periodic save) and saves on stop.</summary>
internal sealed class EntitlementLoop(EntitlementTracker tracker) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                tracker.Tick();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            tracker.Flush();
        }
        catch (IOException)
        {
            // Data folder unavailable while stopping: the last periodic save stands.
        }
    }
}
