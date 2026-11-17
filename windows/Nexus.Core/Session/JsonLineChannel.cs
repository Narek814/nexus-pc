using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Core.Session;

public sealed record RpcResult(bool Ok, JsonObject? Result, string? ErrorCode, string? Message)
{
    public static RpcResult Success(JsonObject? result = null) => new(true, result ?? [], null, null);

    public static RpcResult Failure(string code, string message) => new(false, null, code, message);
}

public delegate Task<RpcResult> RpcHandler(string op, JsonObject args, CancellationToken cancellationToken);

/// <summary>
/// Request/response in both directions over one stream, one JSON object per line.
/// Used between Nexus.Agent (service) and Nexus.Tray (user session) on a local named pipe.
/// <code>
/// {"t":"req","id":1,"op":"apps.launch","args":{"appId":"steam"}}
/// {"t":"res","id":1,"ok":true,"result":{}}
/// {"t":"res","id":2,"ok":false,"error":{"code":"apps.notFound","message":"…"}}
/// </code>
/// </summary>
public sealed class JsonLineChannel : IAsyncDisposable
{
    public const int MaxLineChars = 2 * 1024 * 1024; // file chunks (512 KiB as base64) pass through

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcResult>> _pending = new();
    private long _nextId;
    private int _disposed;

    public JsonLineChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>Calls the other side. Fails with <see cref="IOException"/> if the channel closes first.</summary>
    public async Task<RpcResult> CallAsync(string op, JsonObject? args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<RpcResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new JsonObject { ["t"] = "req", ["id"] = id, ["op"] = op, ["args"] = args ?? [] }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Reads until the stream ends. Each incoming request runs concurrently with the read loop.</summary>
    public async Task RunAsync(RpcHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var running = new List<Task>();
        try
        {
            while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(line);
                }
                catch (JsonException) when (Volatile.Read(ref _disposed) == 1)
                {
                    // We are closing: the reader handed back the half-received last line. That is a normal end.
                    break;
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Channel message is not valid JSON.", ex);
                }
                if (parsed is not JsonObject message)
                {
                    throw new InvalidDataException("Channel message is not a JSON object.");
                }
                switch (message["t"]?.GetValue<string>())
                {
                    case "res":
                        if (message["id"]?.GetValue<long>() is { } resId && _pending.TryGetValue(resId, out var completion))
                        {
                            completion.TrySetResult(message["ok"]?.GetValue<bool>() == true
                                // DeepClone: detach from the parsed message so callers can embed the result elsewhere.
                                ? RpcResult.Success((message["result"] as JsonObject)?.DeepClone().AsObject())
                                : RpcResult.Failure(message["error"]?["code"]?.GetValue<string>() ?? "internal", message["error"]?["message"]?.GetValue<string>() ?? ""));
                        }
                        break;
                    case "req":
                        running.RemoveAll(t => t.IsCompleted);
                        running.Add(HandleAsync(handler, message, cancellationToken));
                        break;
                    default:
                        throw new InvalidDataException("Unknown channel message type.");
                }
            }
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IOException("The session channel closed."));
            }
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task HandleAsync(RpcHandler handler, JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"]?.GetValue<long>() ?? 0;
        RpcResult result;
        try
        {
            var args = (message["args"] as JsonObject)?.DeepClone().AsObject() ?? [];
            result = await handler(message["op"]?.GetValue<string>() ?? string.Empty, args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A handler bug must become an error reply, not kill the channel.
        catch (Exception)
#pragma warning restore CA1031
        {
            result = RpcResult.Failure("internal", "Internal error.");
        }

        var reply = new JsonObject { ["t"] = "res", ["id"] = id, ["ok"] = result.Ok };
        if (result.Ok)
        {
            reply["result"] = result.Result ?? [];
        }
        else
        {
            reply["error"] = new JsonObject { ["code"] = result.ErrorCode, ["message"] = result.Message };
        }
        try
        {
            await WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The other side is gone; nobody is waiting for this reply.
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null && line.Length > MaxLineChars)
        {
            throw new InvalidDataException("Channel message too long.");
        }
        return line;
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var text = message.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Safe to call while a read or a reply is still in flight (found by windows-latest CI): the stream
    /// is closed first, which aborts pending I/O; then reader and writer are released, ignoring
    /// errors from the aborted operations. The write lock is not disposed, as an in-flight write may
    /// still release it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            _reader.Dispose();
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pending operation was aborted by closing the stream.
        }
    }
}
