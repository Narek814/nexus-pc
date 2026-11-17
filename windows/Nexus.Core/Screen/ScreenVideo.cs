using System.Text.Json;
using Nexus.Core.Protocol;

namespace Nexus.Core.Screen;

/// <summary>One encoded H.264 access unit (Annex-B byte stream).</summary>
public sealed record VideoFrame(long Seq, bool Key, byte[] Data);

/// <summary>A running encoder on the PC: frames are numbered; the phone asks for everything after the last one it has.</summary>
public interface IScreenVideo : IDisposable
{
    int Width { get; }

    int Height { get; }

    string EncoderName { get; }

    /// <summary>Frames after <paramref name="after"/>, waiting up to <paramref name="wait"/> if there are none yet.</summary>
    IReadOnlyList<VideoFrame> Read(long after, TimeSpan wait);

    /// <summary>Next frame will be a keyframe (phone joined, or its decoder failed).</summary>
    void RequestKeyframe();
}

public sealed record ScreenVideoPayload(long After, bool Keyframe, int MaxWidth, int BitrateKbps);

public static class ScreenVideoParsers
{
    public static PayloadResult Video(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || p.EnumerateObject().Any(x => x.Name is not ("after" or "keyframe" or "maxWidth" or "bitrateKbps")))
        {
            return PayloadResult.Invalid("Only 'after', 'keyframe', 'maxWidth' and 'bitrateKbps' are allowed.");
        }
        long after = -1;
        if (p.TryGetProperty("after", out var a) && (a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out after) || after < -1))
        {
            return PayloadResult.Invalid("'after' must be a frame number (-1 for none).");
        }
        var keyframe = false;
        if (p.TryGetProperty("keyframe", out var k))
        {
            if (k.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return PayloadResult.Invalid("'keyframe' must be true or false.");
            }
            keyframe = k.GetBoolean();
        }
        var maxWidth = 1280;
        if (p.TryGetProperty("maxWidth", out var w) && (w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out maxWidth) || maxWidth is < ScreenParsers.MinWidth or > ScreenParsers.MaxWidth))
        {
            return PayloadResult.Invalid($"'maxWidth' must be {ScreenParsers.MinWidth}..{ScreenParsers.MaxWidth}.");
        }
        var bitrate = 3000;
        if (p.TryGetProperty("bitrateKbps", out var b) && (b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out bitrate) || bitrate is < 300 or > 20000))
        {
            return PayloadResult.Invalid("'bitrateKbps' must be 300..20000.");
        }
        return PayloadResult.Ok(new ScreenVideoPayload(after, keyframe, maxWidth, bitrate));
    }
}

/// <summary>Frames waiting for the phone. Keeps the newest ones; a phone that fell behind gets a fresh keyframe.</summary>
public sealed class VideoFrameBuffer(int capacity = 120)
{
    private readonly LinkedList<VideoFrame> _frames = new();
    private readonly object _gate = new();
    private long _next;

    public long Add(bool key, byte[] data)
    {
        lock (_gate)
        {
            var seq = _next++;
            _frames.AddLast(new VideoFrame(seq, key, data));
            while (_frames.Count > capacity)
            {
                _frames.RemoveFirst();
            }
            Monitor.PulseAll(_gate);
            return seq;
        }
    }

    /// <summary>
    /// Frames after <paramref name="after"/>, waiting up to <paramref name="wait"/> for one. If the reader fell
    /// so far behind that frames were dropped, only frames from the newest keyframe on are returned (and
    /// <paramref name="needKeyframe"/> tells the caller to ask the encoder for one when there is none).
    /// </summary>
    public IReadOnlyList<VideoFrame> Read(long after, TimeSpan wait, out bool needKeyframe)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        lock (_gate)
        {
            while (_frames.Count == 0 || _frames.Last!.Value.Seq <= after)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !Monitor.Wait(_gate, TimeSpan.FromMilliseconds(remaining)))
                {
                    break;
                }
            }
            var pending = _frames.Where(f => f.Seq > after).ToList();
            var first = _frames.First?.Value.Seq ?? _next;
            needKeyframe = false;
            if (after >= 0 && after + 1 < first)
            {
                var key = pending.FindLastIndex(f => f.Key);
                needKeyframe = key < 0;
                pending = key < 0 ? [] : pending[key..];
            }
            return pending;
        }
    }
}

/// <summary>
/// BGRA (32-bit, as GDI captures it) to NV12 (what H.264 encoders take): full-resolution Y plane,
/// then interleaved U/V at half resolution. BT.601 limited range, as decoders assume by default.
/// </summary>
public static class Nv12
{
    public static int Size(int width, int height) => width * height * 3 / 2;

    public static void FromBgra(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("Width and height must be even.");
        }
        if (nv12.Length < Size(width, height))
        {
            throw new ArgumentException("NV12 buffer is too small.", nameof(nv12));
        }
        var uv = nv12[(width * height)..];
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            var yRow = nv12.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                yRow[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            }
        }
        for (var y = 0; y < height; y += 2)
        {
            var top = bgra.Slice(y * stride, width * 4);
            var bottom = bgra.Slice((y + 1) * stride, width * 4);
            var uvRow = uv.Slice(y / 2 * width, width);
            for (var x = 0; x < width; x += 2)
            {
                // Average of the 2x2 block.
                var i = x * 4;
                var b = (top[i] + top[i + 4] + bottom[i] + bottom[i + 4] + 2) >> 2;
                var g = (top[i + 1] + top[i + 5] + bottom[i + 1] + bottom[i + 5] + 2) >> 2;
                var r = (top[i + 2] + top[i + 6] + bottom[i + 2] + bottom[i + 6] + 2) >> 2;
                uvRow[x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                uvRow[x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
    }
}
