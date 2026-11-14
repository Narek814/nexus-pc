using System.Net.WebSockets;
using Nexus.Agent.Storage;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

/// <summary>
/// One WebSocket connection. Messages are handled strictly in order. A connection starts anonymous
/// and becomes a device after auth.begin/auth.prove; the device record is re-read for every
/// message, so revoking a device or changing its permissions takes effect immediately.
/// </summary>
internal sealed class ConnectionSession
{
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AuthLifetime = TimeSpan.FromHours(1);
    public const int MaxAuthFailures = 3;

    private readonly WebSocket _socket;
    private readonly AgentServices _services;
    private readonly string _remote;
    private readonly TokenBucket _rate;
    private DateTimeOffset? _handshakeDeadline;

    public ConnectionSession(WebSocket socket, AgentServices services, string remote)
    {
        _socket = socket;
        _services = services;
        _remote = remote;
        _rate = new TokenBucket(capacity: 40, refillPerSecond: 20, services.Time);
    }

    public Guid? AuthenticatedDevice { get; private set; }
    public DateTimeOffset AuthenticatedAt { get; private set; }
    public (Guid DeviceId, byte[] Nonce)? PendingChallenge { get; set; }
    public int AuthFailures { get; set; }
    public string Remote => _remote;

    public void MarkAuthenticated(Guid deviceId)
    {
        AuthenticatedDevice = deviceId;
        AuthenticatedAt = _services.Time.GetUtcNow();
        PendingChallenge = null;
        AuthFailures = 0;
    }

    /// <summary>Current identity, or anonymous if never authenticated, expired, or revoked.</summary>
    public ClientContext CurrentClient()
    {
        if (AuthenticatedDevice is not { } id)
        {
            return ClientContext.Anonymous;
        }
        var device = _services.Devices.Find(id);
        if (device is null || _services.Time.GetUtcNow() - AuthenticatedAt > AuthLifetime)
        {
            AuthenticatedDevice = null;
            return ClientContext.Anonymous;
        }
        return ClientContext.ForDevice(id.ToString("D"), device.Permissions);
    }

    /// <summary>Gives an anonymous connection a fresh window to authenticate (after pairing).</summary>
    public void ResetHandshakeDeadline() => _handshakeDeadline = _services.Time.GetUtcNow() + _services.HandshakeTimeout;

    public async Task RunAsync(CancellationToken stopping)
    {
        var buffer = new byte[ProtocolInfo.MaxMessageBytes];
        ResetHandshakeDeadline();

        while (_socket.State == WebSocketState.Open && !stopping.IsCancellationRequested)
        {
            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            if (CurrentClient().IsAuthenticated)
            {
                _handshakeDeadline = null;
            }
            else
            {
                _handshakeDeadline ??= _services.Time.GetUtcNow() + _services.HandshakeTimeout; // lost auth: new window
                var remaining = _handshakeDeadline.Value - _services.Time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication timeout").ConfigureAwait(false);
                    return;
                }
                // Cancelling a WebSocket receive aborts the connection, which is what a timeout should do.
                receiveCts.CancelAfter(remaining);
            }

            var (length, status) = await ReceiveAsync(buffer, receiveCts.Token).ConfigureAwait(false);
            switch (status)
            {
                case ReceiveStatus.Closed:
                    return;
                case ReceiveStatus.TooLarge:
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large").ConfigureAwait(false);
                    return;
                case ReceiveStatus.Binary:
                    await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text frames only").ConfigureAwait(false);
                    return;
            }

            if (!_rate.TryTake())
            {
                await CloseAsync(WebSocketCloseStatus.PolicyViolation, "rate limit").ConfigureAwait(false);
                return;
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
            var result = await _services.Dispatcher.HandleAsync(text, this, stopping).ConfigureAwait(false);
            await _socket.SendAsync(result.Response, WebSocketMessageType.Text, endOfMessage: true, stopping).ConfigureAwait(false);
            if (result.AfterSend is { } after)
            {
                await after().ConfigureAwait(false);
            }

            if (AuthFailures >= MaxAuthFailures)
            {
                await CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication failed").ConfigureAwait(false);
                return;
            }
        }
    }

    private enum ReceiveStatus
    {
        Message,
        Closed,
        TooLarge,
        Binary,
    }

    private async Task<(int Length, ReceiveStatus Status)> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    return (0, ReceiveStatus.TooLarge);
                }
                var result = await _socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
                    return (0, ReceiveStatus.Closed);
                }
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    return (0, ReceiveStatus.Binary);
                }
                length += result.Count;
                if (result.EndOfMessage)
                {
                    return (length, ReceiveStatus.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (0, ReceiveStatus.Closed);
        }
        catch (WebSocketException)
        {
            return (0, ReceiveStatus.Closed);
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string? description)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The peer is gone; nothing else to do.
            }
        }
    }
}

/// <summary>Simple per-connection rate limit.</summary>
internal sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly TimeProvider _time;
    private double _tokens;
    private DateTimeOffset _last;

    public TokenBucket(double capacity, double refillPerSecond, TimeProvider time)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _time = time;
        _tokens = capacity;
        _last = time.GetUtcNow();
    }

    public bool TryTake()
    {
        var now = _time.GetUtcNow();
        _tokens = Math.Min(_capacity, _tokens + (now - _last).TotalSeconds * _refillPerSecond);
        _last = now;
        if (_tokens < 1)
        {
            return false;
        }
        _tokens -= 1;
        return true;
    }
}
