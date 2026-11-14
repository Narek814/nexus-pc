using System.Text;

namespace Nexus.Core.Pairing;

/// <summary>
/// Minimal QR Code encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1–40,
/// automatic mask selection. Written in-house to avoid a dependency (ARCHITECTURE.md D10);
/// correctness is checked by decoding generated codes with an independent decoder
/// (tools/check_qr.py, zxing-cpp).
/// </summary>
public sealed class QrCode
{
    // Level M rows of the standard tables, indexed by version (index 0 unused).
    private static readonly int[] EccCodewordsPerBlock =
    [
        -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
        26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
    ];

    private static readonly int[] ErrorCorrectionBlocks =
    [
        -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49,
    ];

    private const int FormatBitsLevelM = 0b00;

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    public int Version { get; }

    /// <summary>Width and height in modules (without the quiet zone).</summary>
    public int Size { get; }

    public int Mask { get; private set; }

    /// <summary>True for a dark module. Coordinates are (x = column, y = row).</summary>
    public bool this[int x, int y] => _modules[y, x];

    /// <summary>Rows as strings of '1' (dark) and '0' (light). Used by the decoder check and console output.</summary>
    public IReadOnlyList<string> ToRows()
    {
        var rows = new string[Size];
        for (var y = 0; y < Size; y++)
        {
            var row = new char[Size];
            for (var x = 0; x < Size; x++)
            {
                row[x] = _modules[y, x] ? '1' : '0';
            }
            rows[y] = new string(row);
        }
        return rows;
    }

    public static QrCode EncodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EncodeBytes(Encoding.UTF8.GetBytes(text));
    }

    public static QrCode EncodeBytes(ReadOnlySpan<byte> data)
    {
        var version = 1;
        while (true)
        {
            var countBits = version <= 9 ? 8 : 16;
            var neededBits = 4 + countBits + data.Length * 8;
            if (neededBits <= DataCodewords(version) * 8)
            {
                break;
            }
            if (++version > 40)
            {
                throw new ArgumentException($"Data too long for a QR code ({data.Length} bytes).", nameof(data));
            }
        }

        var bits = new BitBuffer();
        bits.Append(0b0100, 4); // byte mode
        bits.Append(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data)
        {
            bits.Append(b, 8);
        }

        var capacityBits = DataCodewords(version) * 8;
        bits.Append(0, Math.Min(4, capacityBits - bits.Length)); // terminator
        bits.Append(0, (8 - bits.Length % 8) % 8);
        for (var pad = 0xEC; bits.Length < capacityBits; pad ^= 0xEC ^ 0x11)
        {
            bits.Append(pad, 8);
        }

        var qr = new QrCode(version);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(AddErrorCorrectionAndInterleave(bits.ToBytes(), version));
        qr.ApplyBestMask();
        return qr;
    }

    // ---- Layout -----------------------------------------------------------------------------

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var positions = AlignmentPositions(Version);
        var n = positions.Length;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                // Skip the three corners occupied by finder patterns.
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0))
                {
                    continue;
                }
                DrawAlignment(positions[i], positions[j]);
            }
        }

        DrawFormatBits(0); // reserve; real bits are drawn after choosing the mask
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                {
                    SetFunction(x, y, distance != 2 && distance != 4);
                }
            }
        }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private void DrawFormatBits(int mask)
    {
        var data = (FormatBitsLevelM << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }
        var bits = ((data << 10) | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
        {
            SetFunction(8, i, Bit(bits, i));
        }
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, Bit(bits, i));
        }

        for (var i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        }
        for (var i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        }
        SetFunction(8, Size - 8, true); // the always-dark module
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }
        var rem = Version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }
        var bits = (Version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] codewords)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5; // skip the vertical timing column
            }
            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < codewords.Length * 8)
                    {
                        _modules[y, x] = Bit(codewords[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyBestMask()
    {
        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = Penalty();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }
            ApplyMask(mask); // XOR again to undo
        }
        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (!_isFunction[y, x] && invert)
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    /// <summary>Standard penalty rules N1–N4; only used to pick the most scannable mask.</summary>
    private int Penalty()
    {
        var penalty = 0;

        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                var runColor = false;
                var run = 0;
                for (var b = 0; b < Size; b++)
                {
                    var color = pass == 0 ? _modules[a, b] : _modules[b, a];
                    if (b > 0 && color == runColor)
                    {
                        run++;
                        if (run == 5)
                        {
                            penalty += 3;
                        }
                        else if (run > 5)
                        {
                            penalty++;
                        }
                    }
                    else
                    {
                        runColor = color;
                        run = 1;
                    }
                }
            }
        }

        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                {
                    penalty += 3;
                }
            }
        }

        bool[] finderLike = [true, false, true, true, true, false, true];
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                for (var b = 0; b + 7 <= Size; b++)
                {
                    var match = true;
                    for (var k = 0; k < 7 && match; k++)
                    {
                        match = (pass == 0 ? _modules[a, b + k] : _modules[b + k, a]) == finderLike[k];
                    }
                    if (match && (LightRun(pass, a, b - 4, b) || LightRun(pass, a, b + 7, b + 11)))
                    {
                        penalty += 40;
                    }
                }
            }
        }

        var dark = 0;
        foreach (var module in _modules)
        {
            if (module)
            {
                dark++;
            }
        }
        var total = Size * Size;
        var k5 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        penalty += k5 * 10;
        return penalty;
    }

    // Light modules (or outside the symbol, which counts as light) in [from, to) along a row/column.
    private bool LightRun(int pass, int line, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (i >= 0 && i < Size && (pass == 0 ? _modules[line, i] : _modules[i, line]))
            {
                return false;
            }
        }
        return true;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    // ---- Codewords --------------------------------------------------------------------------

    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }
        return result;
    }

    private static int DataCodewords(int version) =>
        RawDataModules(version) / 8 - EccCodewordsPerBlock[version] * ErrorCorrectionBlocks[version];

    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }
        var count = version / 7 + 2;
        var step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = version * 4 + 17 - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }
        return result;
    }

    private static byte[] AddErrorCorrectionAndInterleave(byte[] data, int version)
    {
        var blocks = ErrorCorrectionBlocks[version];
        var eccLength = EccCodewordsPerBlock[version];
        var rawCodewords = RawDataModules(version) / 8;
        var shortBlocks = blocks - rawCodewords % blocks;
        var shortBlockLength = rawCodewords / blocks;

        var divisor = ReedSolomonDivisor(eccLength);
        var allBlocks = new List<byte[]>();
        for (int i = 0, offset = 0; i < blocks; i++)
        {
            var dataLength = shortBlockLength - eccLength + (i < shortBlocks ? 0 : 1);
            var blockData = data.AsSpan(offset, dataLength).ToArray();
            offset += dataLength;
            var ecc = ReedSolomonRemainder(blockData, divisor);
            // Short blocks get a placeholder byte so all blocks have equal length for interleaving.
            var block = new byte[shortBlockLength + 1];
            blockData.CopyTo(block, 0);
            ecc.CopyTo(block, block.Length - eccLength);
            allBlocks.Add(block);
        }

        var result = new List<byte>(rawCodewords);
        for (var i = 0; i < shortBlockLength + 1; i++)
        {
            for (var j = 0; j < allBlocks.Count; j++)
            {
                // The placeholder sits at index (shortBlockLength - eccLength) of short blocks.
                if (i != shortBlockLength - eccLength || j >= shortBlocks)
                {
                    result.Add(allBlocks[j][i]);
                }
            }
        }
        return [.. result];
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        // Coefficients from highest to lowest power, excluding the leading 1.
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }
        return result;
    }

    // GF(2^8) with the QR polynomial x^8 + x^4 + x^3 + x^2 + 1.
    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Append(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes()
        {
            var result = new byte[_bits.Count / 8];
            for (var i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    result[i >> 3] |= (byte)(0x80 >> (i & 7));
                }
            }
            return result;
        }
    }
}
