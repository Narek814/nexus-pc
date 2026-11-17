using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Protocol;
using Nexus.Core.Screen;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Tests;

public sealed class Nv12Tests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            bgra[i * 4] = b;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = r;
            bgra[i * 4 + 3] = 255;
        }
        return bgra;
    }

    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)] // white
    [InlineData(0, 0, 0, 16, 128, 128)]        // black
    [InlineData(255, 0, 0, 82, 90, 240)]       // red
    [InlineData(0, 0, 255, 41, 240, 110)]      // blue
    public void Colors_convert_to_bt601_limited_range(byte r, byte g, byte b, int y, int u, int v)
    {
        var nv12 = new byte[Nv12.Size(4, 2)];
        Nv12.FromBgra(Solid(4, 2, r, g, b), 16, 4, 2, nv12);
        Assert.All(nv12[..8], value => Assert.InRange(value, y - 1, y + 1));
        Assert.InRange(nv12[8], u - 1, u + 1);
        Assert.InRange(nv12[9], v - 1, v + 1);
        Assert.Equal(12, nv12.Length);
    }

    [Fact]
    public void Odd_sizes_are_refused() =>
        Assert.Throws<ArgumentException>(() => Nv12.FromBgra(new byte[3 * 2 * 4], 12, 3, 2, new byte[100]));
}

public sealed class VideoFrameBufferTests
{
    [Fact]
    public void Returns_only_new_frames_and_waits_for_one()
    {
        var buffer = new VideoFrameBuffer();
        buffer.Add(true, [1]);
        buffer.Add(false, [2]);
        Assert.Equal([1L], buffer.Read(0, TimeSpan.Zero, out _).Select(f => f.Seq));

        var started = Environment.TickCount64;
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            buffer.Add(false, [3]);
        });
        var frames = buffer.Read(1, TimeSpan.FromSeconds(2), out _);
        Assert.Equal([2L], frames.Select(f => f.Seq));
        Assert.InRange(Environment.TickCount64 - started, 30, 1500);
    }

    [Fact]
    public void Nothing_new_returns_empty_after_the_wait()
    {
        var buffer = new VideoFrameBuffer();
        buffer.Add(true, [1]);
        Assert.Empty(buffer.Read(0, TimeSpan.FromMilliseconds(20), out _));
    }

    [Fact]
    public void A_reader_that_fell_behind_restarts_at_a_keyframe()
    {
        var buffer = new VideoFrameBuffer(capacity: 3);
        buffer.Add(true, [0]);
        buffer.Add(false, [1]);
        buffer.Add(true, [2]);
        buffer.Add(false, [3]);
        buffer.Add(false, [4]); // frames 0 and 1 are gone
        var frames = buffer.Read(0, TimeSpan.Zero, out var needKeyframe);
        Assert.False(needKeyframe);
        Assert.Equal([2L, 3L, 4L], frames.Select(f => f.Seq));

        buffer.Add(false, [5]);
        buffer.Add(false, [6]);
        buffer.Add(false, [7]); // no keyframe left in the buffer
        Assert.Empty(buffer.Read(1, TimeSpan.Zero, out needKeyframe));
        Assert.True(needKeyframe);
    }
}

public sealed class TrayVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-video-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDesktop _desktop = new();
    private readonly TrayCommandHandler _handler;

    public TrayVideoTests() =>
        _handler = new TrayCommandHandler(new UserAppStore(_dir), _desktop, screenApprovals: new ScreenApprovals(_dir));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Video_needs_a_started_session_and_stops_with_it()
    {
        Assert.Equal(ErrorCodes.ScreenNotStarted, (await _handler.HandleAsync("screen.video", [], default)).ErrorCode);
        await _handler.HandleAsync("screen.start", new JsonObject { ["deviceId"] = "d1", ["deviceName"] = "iPhone" }, default);

        var first = await _handler.HandleAsync("screen.video", new JsonObject { ["after"] = -1, ["keyframe"] = true, ["maxWidth"] = 960 }, default);
        Assert.True(first.Ok, first.Message);
        Assert.Equal(960, first.Result!["width"]!.GetValue<int>());
        Assert.Equal(1, _desktop.Video!.Keyframes);

        _desktop.Video.Buffer.Add(true, [0, 0, 0, 1, 0x65, 1, 2]);
        var next = await _handler.HandleAsync("screen.video", new JsonObject { ["after"] = -1, ["maxWidth"] = 960 }, default);
        var frame = next.Result!["frames"]![0]!;
        Assert.True(frame["key"]!.GetValue<bool>());
        Assert.Equal(Convert.ToBase64String(new byte[] { 0, 0, 0, 1, 0x65, 1, 2 }), frame["data"]!.GetValue<string>());

        var video = _desktop.Video;
        await _handler.HandleAsync("screen.stop", [], default);
        Assert.True(video.Disposed);
    }

    [Fact]
    public async Task Changing_the_quality_restarts_the_encoder()
    {
        await _handler.HandleAsync("screen.start", new JsonObject { ["deviceId"] = "d1", ["deviceName"] = "iPhone" }, default);
        await _handler.HandleAsync("screen.video", new JsonObject { ["maxWidth"] = 960 }, default);
        var first = _desktop.Video!;
        await _handler.HandleAsync("screen.video", new JsonObject { ["maxWidth"] = 1920 }, default);
        Assert.True(first.Disposed);
        Assert.Equal(1920, _desktop.Video!.Width);
    }

    [Fact]
    public async Task No_encoder_tells_the_phone_to_use_jpeg()
    {
        _desktop.VideoProblem = "нет кодировщика";
        await _handler.HandleAsync("screen.start", new JsonObject { ["deviceId"] = "d1", ["deviceName"] = "iPhone" }, default);
        var result = await _handler.HandleAsync("screen.video", [], default);
        Assert.Equal(ErrorCodes.ScreenVideoUnavailable, result.ErrorCode);
    }
}

/// <summary>The real Windows encoder; runs only on Windows (CI windows-latest and the owner's PC).</summary>
public sealed class MediaFoundationEncoderTests(ITestOutputHelper output)
{
    [Fact]
    public void Encodes_frames_into_h264_with_sps_pps_and_a_keyframe()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Not Windows: Media Foundation is not available here.");
            return;
        }
        Nexus.Windows.MediaFoundationH264Encoder encoder;
        try
        {
            encoder = new Nexus.Windows.MediaFoundationH264Encoder(320, 240, 30, 1000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            // Windows Server without the Media Foundation feature: report, do not hide.
            output.WriteLine("H.264 encoder unavailable on this machine: " + ex.Message);
            Assert.Contains("Media Foundation", ex.Message, StringComparison.Ordinal);
            return;
        }
        using (encoder)
        {
            var frames = new List<(byte[] Data, bool Key)>();
            var nv12 = new byte[Nexus.Core.Screen.Nv12.Size(320, 240)];
            for (var i = 0; i < 20; i++)
            {
                // A moving gradient, so frames differ.
                for (var p = 0; p < 320 * 240; p++)
                {
                    nv12[p] = (byte)((p + i * 8) % 220 + 16);
                }
                Array.Fill(nv12, (byte)128, 320 * 240, 320 * 120);
                frames.AddRange(encoder.Encode(nv12, forceKeyframe: i == 10));
            }
            output.WriteLine($"{frames.Count} access units, {frames.Sum(f => f.Data.Length)} bytes, keyframes at {string.Join(",", frames.Select((f, i) => (f, i)).Where(x => x.f.Key).Select(x => x.i))}");
            Assert.NotEmpty(frames);
            Assert.True(frames[0].Key, "the first frame must be a keyframe");
            Assert.All(frames, f => Assert.True(Nexus.Windows.H264.StartsWithStartCode(f.Data), "Annex-B start code"));
            var types = Nexus.Windows.H264.NalTypes(frames[0].Data).ToList();
            Assert.Contains(7, types); // SPS
            Assert.Contains(8, types); // PPS
            Assert.Contains(5, types); // IDR
            Assert.True(frames.Count(f => f.Key) >= 2 || !encoder.CanForceKeyframe, "forced keyframe");
        }
    }
}

public sealed class H264Tests
{
    [Fact]
    public void Nal_types_are_found_after_three_and_four_byte_start_codes()
    {
        byte[] stream = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 4, 5];
        Assert.Equal([7, 8, 5], Nexus.Windows.H264.NalTypes(stream));
        Assert.True(Nexus.Windows.H264.ContainsNal(stream, 5));
        Assert.False(Nexus.Windows.H264.ContainsNal(stream, 1));
    }
}
