using System.Net.Http.Headers;
using System.Text;

namespace Nexus.Notifications;

public interface INotifier
{
    /// <summary>Best effort: returns false (never throws) if delivery failed.</summary>
    Task<bool> SendAsync(AlertEvent alert, AlertSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// ntfy (https://ntfy.sh, open source): HTTP POST to server/topic; the ntfy iPhone app subscribed to
/// the topic shows it as a push even when Nexus PC is closed (D11: no paid Apple push needed).
/// </summary>
public sealed class NtfyNotifier(HttpClient http) : INotifier
{
    public async Task<bool> SendAsync(AlertEvent alert, AlertSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.NtfyEnabled || string.IsNullOrEmpty(settings.NtfyTopic))
        {
            return false;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.NtfyServer), settings.NtfyTopic))
        {
            Content = new StringContent(alert.Message, Encoding.UTF8, "text/plain"),
        };
        // Header values must be ASCII-safe for some servers: ntfy accepts RFC 2047 encoded titles.
        request.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(alert.Title)) + "?=");
        request.Headers.TryAddWithoutValidation("Priority", alert.Severity == AlertSeverity.Warning ? "high" : "default");
        request.Headers.TryAddWithoutValidation("Tags", alert.Severity == AlertSeverity.Warning ? "warning" : "computer");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Keeps the last alerts so the app can show them (in memory; persistent history is P6).</summary>
public sealed class RecentAlerts
{
    private readonly Lock _lock = new();
    private readonly LinkedList<(DateTimeOffset Time, AlertEvent Alert, bool Delivered)> _items = new();

    public void Add(DateTimeOffset time, AlertEvent alert, bool delivered)
    {
        lock (_lock)
        {
            _items.AddFirst((time, alert, delivered));
            while (_items.Count > 50)
            {
                _items.RemoveLast();
            }
        }
    }

    public IReadOnlyList<(DateTimeOffset Time, AlertEvent Alert, bool Delivered)> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }
}
