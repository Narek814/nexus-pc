using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Nexus.RGB;

/// <summary>
/// OpenRGB SDK network client (protocol version 0: the server answers older clients in the old format).
/// OpenRGB must run with its SDK server on 127.0.0.1:6742 ("SDK Server" tab). Packet header:
/// "ORGB", device index, packet id, data size — all little-endian uint32.
/// </summary>
public sealed class OpenRgbClient : IRgbAdapter
{
    internal const uint RequestControllerCount = 0;
    internal const uint RequestControllerData = 1;
    internal const uint SetClientName = 50;
    internal const uint UpdateLeds = 1050;      // RGBCONTROLLER_UPDATELEDS (1051 is UPDATEZONELEDS)
    internal const uint UpdateMode = 1101;
    internal const uint SetCustomMode = 1100;
    private const int MaxPacket = 4 * 1024 * 1024;
    private static readonly byte[] Magic = "ORGB"u8.ToArray();

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OpenRgbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public string Name => "OpenRGB";

    public static async Task<OpenRgbClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var client = new OpenRgbClient(tcp);
            await client.SendAsync(0, SetClientName, Encoding.ASCII.GetBytes("Nexus PC\0"), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<RgbDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync(0, RequestControllerCount, [], cancellationToken).ConfigureAwait(false);
            var countData = await ReceiveAsync(RequestControllerCount, cancellationToken).ConfigureAwait(false);
            var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(countData), 64);
            var devices = new List<RgbDevice>();
            for (var i = 0; i < count; i++)
            {
                await SendAsync((uint)i, RequestControllerData, [], cancellationToken).ConfigureAwait(false);
                devices.Add(ControllerData.Parse(i, await ReceiveAsync(RequestControllerData, cancellationToken).ConfigureAwait(false)));
            }
            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetStaticAsync(RgbDevice device, RgbColor color, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync((uint)device.Index, SetCustomMode, [], cancellationToken).ConfigureAwait(false);
            // data_size (uint32, includes itself) + num_colors (uint16) + colors (uint32 0x00BBGGRR).
            var count = device.LedCount;
            var data = new byte[4 + 2 + 4 * count];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)count);
            for (var i = 0; i < count; i++)
            {
                data[6 + 4 * i] = color.R;
                data[6 + 4 * i + 1] = color.G;
                data[6 + 4 * i + 2] = color.B;
            }
            await SendAsync((uint)device.Index, UpdateLeds, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetModeAsync(RgbDevice device, RgbMode mode, RgbColor color, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mode);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync((uint)device.Index, UpdateMode, ControllerData.SerializeModeUpdate(mode, color), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendAsync(uint deviceIndex, uint packetId, byte[] data, CancellationToken cancellationToken)
    {
        var packet = new byte[16 + data.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)data.Length);
        data.CopyTo(packet, 16);
        await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads packets until one with <paramref name="expectedId"/> arrives (skips notifications such as device-list updates).</summary>
    private async Task<byte[]> ReceiveAsync(uint expectedId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var header = new byte[16];
        while (true)
        {
            await _stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            {
                throw new InvalidDataException("Not an OpenRGB SDK packet.");
            }
            var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (size > MaxPacket)
            {
                throw new InvalidDataException("OpenRGB packet too large.");
            }
            var data = new byte[size];
            await _stream.ReadExactlyAsync(data, timeout.Token).ConfigureAwait(false);
            if (id == expectedId)
            {
                return data;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Controller data blob (protocol 0) parsing and mode serialization.</summary>
internal static class ControllerData
{
    private static readonly string[] Types =
    [
        "motherboard", "dram", "gpu", "cooler", "ledstrip", "keyboard", "mouse", "mousemat", "headset", "headset_stand",
        "gamepad", "light", "speaker", "virtual", "storage", "case", "microphone", "accessory", "keypad",
    ];

    public static RgbDevice Parse(int index, byte[] data)
    {
        var r = new Reader(data);
        r.UInt32(); // data_size
        var type = r.Int32();
        var name = r.String();
        r.String(); // description
        r.String(); // version
        r.String(); // serial
        r.String(); // location
        var modeCount = r.UInt16();
        var activeMode = r.Int32();
        var modes = new List<RgbMode>(modeCount);
        for (var m = 0; m < modeCount; m++)
        {
            var start = r.Position;
            var modeName = r.String();
            r.Int32();  // value
            r.UInt32(); // flags
            r.UInt32(); // speed_min
            r.UInt32(); // speed_max
            r.UInt32(); // colors_min
            r.UInt32(); // colors_max
            r.UInt32(); // speed
            r.UInt32(); // direction
            var colorMode = r.UInt32();
            var colorCount = r.UInt16();
            var colors = new List<RgbColor>(colorCount);
            for (var c = 0; c < colorCount; c++)
            {
                colors.Add(r.Color());
            }
            modes.Add(new RgbMode(m, modeName, colorMode, colors, data[start..r.Position]));
        }
        var zoneCount = r.UInt16();
        for (var z = 0; z < zoneCount; z++)
        {
            r.String(); // zone name
            r.Int32();  // zone type
            r.UInt32(); // leds_min
            r.UInt32(); // leds_max
            r.UInt32(); // leds_count
            // Matrix zones: the length field is followed by height, width and height×width uint32 map
            // entries (read by count, as openrgb-python does, not by trusting the length's unit).
            if (r.UInt16() > 0)
            {
                var height = r.UInt32();
                var width = r.UInt32();
                if (height > 256 || width > 256)
                {
                    throw new InvalidDataException("Implausible OpenRGB matrix size.");
                }
                r.Skip((int)(height * width * 4));
            }
        }
        var ledCount = r.UInt16();
        for (var l = 0; l < ledCount; l++)
        {
            r.String(); // led name
            r.UInt32(); // led value
        }
        return new RgbDevice(index, name, type >= 0 && type < Types.Length ? Types[type] : "unknown", ledCount, modes, activeMode);
    }

    /// <summary>UPDATEMODE data: data_size, mode index, then the mode with its colors replaced.</summary>
    public static byte[] SerializeModeUpdate(RgbMode mode, RgbColor color)
    {
        // The mode bytes end with num_colors (uint16) + colors; rewrite the colors in place.
        var body = (byte[])mode.Raw.Clone();
        var colorBytes = mode.Colors.Count * 4;
        for (var c = 0; c < mode.Colors.Count; c++)
        {
            var offset = body.Length - colorBytes + 4 * c;
            body[offset] = color.R;
            body[offset + 1] = color.G;
            body[offset + 2] = color.B;
            body[offset + 3] = 0;
        }
        var data = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), mode.Index);
        body.CopyTo(data, 8);
        return data;
    }

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Position + count > data.Length)
            {
                throw new InvalidDataException("Truncated OpenRGB controller data.");
            }
            var span = data.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public void Skip(int count) => Take(count);

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public RgbColor Color()
        {
            var b = Take(4);
            return new RgbColor(b[0], b[1], b[2]);
        }

        /// <summary>uint16 length (including the terminating NUL) + bytes.</summary>
        public string String()
        {
            var length = UInt16();
            var bytes = Take(length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
    }
}
