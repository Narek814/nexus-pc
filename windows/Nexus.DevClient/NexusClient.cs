using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.DevClient;

/// <summary>
/// Minimal protocol client, the reference for what NexusKit (Swift) must do:
/// pin the certificate, pair, authenticate by signing the transcript, send requests.
/// </summary>
internal sealed class NexusClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly TimeProvider _time;

    private NexusClient(ClientWebSocket socket, TimeProvider time)
    {
        _socket = socket;
        _time = time;
    }

    public WebSocketState State => _socket.State;

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public static async Task<NexusClient> ConnectAsync(string host, int port, string pinnedFingerprint, CancellationToken cancellationToken, TimeProvider? time = null, Action<ClientWebSocketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(pinnedFingerprint);
        var expected = Encoding.ASCII.GetBytes(pinnedFingerprint.ToUpperInvariant());
        var socket = new ClientWebSocket();
        // Trust exactly one certificate: the one whose fingerprint came from the pairing QR/link.
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))), expected);

        // Always a direct connection to the PC: a system/HTTPS proxy must never relay LAN traffic
        // (found in the cloud test environment, where HTTPS_PROXY broke connections to non-loopback hosts).
        socket.Options.Proxy = null;
        configure?.Invoke(socket.Options);
        var hostPart = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
        try
        {
            await socket.ConnectAsync(new Uri($"wss://{hostPart}:{port.ToString(CultureInfo.InvariantCulture)}/v1/ws"), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NexusClient(socket, time ?? TimeProvider.System);
    }

    /// <summary>Sends a request and returns the response envelope.</summary>
    public async Task<JsonObject> RequestAsync(string type, JsonObject? payload = null, bool confirmed = false, Guid? id = null, CancellationToken cancellationToken = default)
    {
        var envelope = new JsonObject
        {
            ["v"] = ProtocolInfo.Version,
            ["id"] = (id ?? Guid.NewGuid()).ToString("D"),
            ["kind"] = "request",
            ["type"] = type,
            ["ts"] = _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["payload"] = payload ?? [],
        };
        if (confirmed)
        {
            envelope["confirmed"] = true;
        }
        return await SendRawAsync(envelope.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonObject> SendRawAsync(string json, CancellationToken cancellationToken = default)
    {
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"The agent closed the connection: {result.CloseStatus} {result.CloseStatusDescription}");
            }
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonNode.Parse(buffer.ToArray())!.AsObject();
            }
        }
    }

    public Task<JsonObject> PairAsync(string code, string deviceName, ECDsa key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return RequestAsync("pair.request", new JsonObject
        {
            ["code"] = code,
            ["deviceName"] = deviceName,
            ["publicKey"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        }, cancellationToken: cancellationToken);
    }

    /// <summary>auth.begin → sign transcript → auth.prove. Returns the auth.prove response.</summary>
    public async Task<JsonObject> AuthenticateAsync(Guid pcId, Guid deviceId, ECDsa key, string pinnedFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var begin = await RequestAsync("auth.begin", new JsonObject { ["deviceId"] = deviceId.ToString("D") }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (begin["ok"]?.GetValue<bool>() != true)
        {
            return begin;
        }
        var nonce = Convert.FromBase64String(begin["payload"]!["nonce"]!.GetValue<string>());
        var transcript = DeviceCrypto.AuthTranscript(pcId, deviceId, nonce, pinnedFingerprint);
        var signature = key.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return await RequestAsync("auth.prove", new JsonObject { ["signature"] = Convert.ToBase64String(signature) }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Already gone.
            }
        }
        _socket.Dispose();
    }
}
