using System.Globalization;

namespace Nexus.RGB;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black { get; } = new(0, 0, 0);

    /// <summary>Parses "#RRGGBB".</summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = Black;
        if (text is not { Length: 7 } || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public RgbColor Scaled(int brightnessPercent)
    {
        var f = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        return new RgbColor((byte)Math.Round(R * f), (byte)Math.Round(G * f), (byte)Math.Round(B * f));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public sealed record RgbMode(int Index, string Name, uint ColorMode, IReadOnlyList<RgbColor> Colors, byte[] Raw);

public sealed record RgbDevice(int Index, string Name, string Type, int LedCount, IReadOnlyList<RgbMode> Modes, int ActiveMode);

/// <summary>A lighting backend (adapter architecture, ТЗ §6.2). Only hardware it reports is claimed as supported.</summary>
public interface IRgbAdapter : IAsyncDisposable
{
    string Name { get; }

    Task<IReadOnlyList<RgbDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Every LED of the device to one color (switches the device to its direct/custom mode).</summary>
    Task SetStaticAsync(RgbDevice device, RgbColor color, CancellationToken cancellationToken);

    /// <summary>Switches to one of the device's own hardware effects; mode colors (if any) set to <paramref name="color"/>.</summary>
    Task SetModeAsync(RgbDevice device, RgbMode mode, RgbColor color, CancellationToken cancellationToken);
}
