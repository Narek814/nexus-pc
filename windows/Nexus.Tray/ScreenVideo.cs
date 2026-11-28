using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Nexus.Core.Screen;
using Nexus.Windows;

namespace Nexus.Tray;

/// <summary>
/// Screen → NV12 → Windows' own H.264 encoder, on one background thread, up to 30 frames a second.
/// A frame identical to the previous one is not encoded at all, so a still screen costs nothing.
/// </summary>
internal sealed class ScreenVideoStream : IScreenVideo
{
    public const int Fps = 30;

    /// <summary>A phone further behind than this skips to the newest keyframe instead of replaying old frames.</summary>
    public const int MaxBacklog = 6;

    private readonly VideoFrameBuffer _buffer = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly int _bitrateKbps;
    private volatile bool _stop;
    private volatile bool _keyframe = true;
    private string? _error;

    private ScreenVideoStream(int maxWidth, int bitrateKbps)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Width = Math.Max(2, Math.Min(maxWidth, bounds.Width) & ~1);
        Height = Math.Max(2, (int)((long)bounds.Height * Width / bounds.Width) & ~1);
        _bitrateKbps = bitrateKbps;
        _thread = new Thread(Run) { IsBackground = true, Name = "Nexus screen video" };
        _thread.Start();
    }

    public int Width { get; }

    public int Height { get; }

    public string EncoderName => "H.264 (Media Foundation)";

    public static ScreenVideoStream? Start(int maxWidth, int bitrateKbps, out string? problem)
    {
        var stream = new ScreenVideoStream(maxWidth, bitrateKbps);
        if (!stream._ready.Wait(TimeSpan.FromSeconds(10)))
        {
            stream.Dispose();
            problem = TrayText.Get("VideoEncoderTimeout");
            return null;
        }
        if (stream._error is { } error)
        {
            stream.Dispose();
            problem = TrayText.Format("VideoUnavailable", error);
            return null;
        }
        problem = null;
        return stream;
    }

    public IReadOnlyList<VideoFrame> Read(long after, TimeSpan wait)
    {
        var frames = _buffer.Read(after, wait, out var needKeyframe);
        if (needKeyframe)
        {
            _keyframe = true;
        }
        if (frames.Count > MaxBacklog)
        {
            // Showing every old frame would only add delay: jump to the newest keyframe (or ask for one).
            var list = frames.ToList();
            var key = list.FindLastIndex(f => f.Key);
            if (key < 0)
            {
                _keyframe = true;
                return [];
            }
            return list[key..];
        }
        return frames;
    }

    public void RequestKeyframe() => _keyframe = true;

    private void Run()
    {
        MediaFoundationH264Encoder? encoder = null;
        try
        {
            encoder = new MediaFoundationH264Encoder(Width, Height, Fps, _bitrateKbps);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            _error = ex.Message;
        }
        _ready.Set();
        if (encoder is null)
        {
            return;
        }

        var bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        var nv12 = new byte[Nv12.Size(Width, Height)];
        byte[]? lastHash = null;
        // After a change keep feeding frames for a second, so nothing waits inside the encoder.
        var flushFrames = 0;
        var interval = TimeSpan.FromSeconds(1.0 / Fps);
        try
        {
            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using var scaled = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            while (!_stop)
            {
                var started = Environment.TickCount64;
                try
                {
                    using (var g = Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        WindowsDesktop.DrawPointer(g, bounds);
                    }
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                        g.DrawImage(full, 0, 0, Width, Height);
                    }
                    var data = scaled.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        unsafe
                        {
                            var pixels = new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * Height);
                            var hash = System.Security.Cryptography.SHA256.HashData(pixels);
                            var force = _keyframe;
                            var changed = lastHash is null || !hash.AsSpan().SequenceEqual(lastHash);
                            if (changed)
                            {
                                flushFrames = Fps;
                            }
                            if (force || changed || flushFrames-- > 0)
                            {
                                lastHash = hash;
                                Nv12.FromBgra(pixels, data.Stride, Width, Height, nv12);
                                _keyframe = false;
                                if (force && !encoder.CanForceKeyframe)
                                {
                                    // The only sure way to get a keyframe: a fresh encoder starts with one.
                                    encoder.Dispose();
                                    encoder = new MediaFoundationH264Encoder(Width, Height, Fps, _bitrateKbps);
                                }
                                foreach (var (frame, key) in encoder.Encode(nv12, force))
                                {
                                    _buffer.Add(key, frame);
                                }
                            }
                        }
                    }
                    finally
                    {
                        scaled.UnlockBits(data);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or ExternalException)
                {
                    // Lock screen or UAC prompt: GDI cannot read the secure desktop. Try again shortly.
                    Thread.Sleep(500);
                    _keyframe = true;
                }
                var rest = interval - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (rest > TimeSpan.Zero)
                {
                    Thread.Sleep(rest);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _error = ex.Message; // encoder failure mid-stream: the phone falls back to JPEG on the next start
        }
        finally
        {
            encoder.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        _ready.Dispose();
    }
}
