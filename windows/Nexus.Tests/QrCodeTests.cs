using Nexus.Core.Pairing;

namespace Nexus.Tests;

/// <summary>
/// Structural checks. Real readability is checked by tools/check_qr.py, which decodes codes of
/// every version with an independent decoder (zxing-cpp) — locally and in CI.
/// </summary>
public class QrCodeTests
{
    [Theory]
    [InlineData(1, new int[0])]
    [InlineData(2, new[] { 6, 18 })]
    [InlineData(7, new[] { 6, 22, 38 })]
    [InlineData(14, new[] { 6, 26, 46, 66 })]
    [InlineData(32, new[] { 6, 34, 60, 86, 112, 138 })]
    [InlineData(36, new[] { 6, 24, 50, 76, 102, 128, 154 })]
    [InlineData(40, new[] { 6, 30, 58, 86, 114, 142, 170 })]
    public void Alignment_positions_match_the_standard_table(int version, int[] expected)
    {
        Assert.Equal(expected, QrCode.AlignmentPositions(version));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(14, 1)]   // version 1-M holds 14 bytes
    [InlineData(15, 2)]
    [InlineData(213, 10)] // version 10-M holds 213 bytes
    [InlineData(214, 11)]
    [InlineData(2331, 40)]
    public void Picks_the_smallest_version_that_fits(int bytes, int version)
    {
        var qr = QrCode.EncodeBytes(new byte[bytes]);
        Assert.Equal(version, qr.Version);
        Assert.Equal(version * 4 + 17, qr.Size);
    }

    [Fact]
    public void Rejects_data_beyond_version_40()
    {
        Assert.Throws<ArgumentException>(() => QrCode.EncodeBytes(new byte[2332]));
    }

    [Fact]
    public void Finder_patterns_and_dark_module_are_in_place()
    {
        var qr = QrCode.EncodeText("nexuspc://pair?v=1");
        foreach (var (cx, cy) in new[] { (3, 3), (qr.Size - 4, 3), (3, qr.Size - 4) })
        {
            Assert.True(qr[cx, cy]);          // centre
            Assert.False(qr[cx + 2, cy]);     // light ring
            Assert.True(qr[cx + 3, cy]);      // dark border
        }
        Assert.True(qr[8, qr.Size - 8]);
        Assert.Equal(qr.Size, qr.ToRows().Count);
    }

    [Fact]
    public void Same_input_gives_the_same_code()
    {
        var link = "nexuspc://pair?v=1&c=01234567";
        Assert.Equal(QrCode.EncodeText(link).ToRows(), QrCode.EncodeText(link).ToRows());
    }
}
