using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nexus.Setup;

/// <summary>One file of payload.zip as listed by CI in manifest.json.</summary>
internal sealed record ManifestEntry(string Path, long Size, string Sha256);

/// <summary>
/// T5 section 2. manifest.json (written by CI, stored inside payload.zip, not listed in itself):
/// <c>{"version":1,"files":[{"path":"Agent/Nexus.Agent.exe","size":123,"sha256":"&lt;64 lowercase hex&gt;"}]}</c>.
/// The manifest travels in the same unsigned installer, so it only detects changes after extraction,
/// never a replaced NexusPC-Setup.exe (docs/SETUP.md).
/// </summary>
internal static class PayloadManifest
{
    public const string FileName = "manifest.json";
    public const int MaxEntries = 20000;

    public const int MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>Reads at most <see cref="MaxManifestBytes"/> + 1 bytes; longer, empty or not valid UTF-8 → null.</summary>
    public static IReadOnlyList<ManifestEntry>? Read(Stream manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length <= MaxManifestBytes && (read = manifest.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxManifestBytes + 1 - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length is 0 or > MaxManifestBytes)
        {
            return null;
        }
        try
        {
            return Parse(new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Archive entry name with "\" turned into "/" (Compress-Archive on Windows may write "\"), done exactly once; every
    /// path check then runs on this name, so "a\b" and "a/b" are the same entry. manifest.json itself must use "/".
    /// </summary>
    public static string ArchiveName(string entryFullName)
    {
        ArgumentNullException.ThrowIfNull(entryFullName);
        return entryFullName.Replace('\\', '/');
    }

    /// <summary>The archive entry for a manifest path, matched by normalized name; null if absent or ambiguous.</summary>
    public static ZipArchiveEntry? FindEntry(ZipArchive zip, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var matches = zip.Entries.Where(e => string.Equals(ArchiveName(e.FullName), manifestPath, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 && ArchiveName(matches[0].FullName) == manifestPath ? matches[0] : null;
    }

    /// <summary>Stage U0: payload size × 2 (staging and the installed copy) plus 64 MB.</summary>
    public static long RequiredFreeBytes(IReadOnlyList<ManifestEntry> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var total = manifest.Aggregate(0L, (sum, e) => checked(sum + e.Size));
        return checked((total * 2) + (64L * 1024 * 1024));
    }

    /// <summary>Parses manifest.json; null when malformed (unknown version, bad hash, negative size, bad path, duplicates).</summary>
    public static IReadOnlyList<ManifestEntry>? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() is 0 or > MaxEntries)
            {
                return null;
            }
            var list = new List<ManifestEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object
                    || !f.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                    || !f.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size < 0
                    || !f.TryGetProperty("sha256", out var h) || h.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                var path = p.GetString()!;
                var hash = h.GetString()!;
                if (!IsSafeRelativePath(path) || !IsSha256Hex(hash) || !seen.Add(path) || path == FileName)
                {
                    return null;
                }
                list.Add(new ManifestEntry(path, size, hash));
            }
            return list;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Forward slashes, no leading slash, no drive or stream colon, no backslash, no empty / "." / ".." segment,
    /// no control characters, no trailing dot or space in a segment (Windows would silently strip it).
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400 || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*')
                || IsReservedDeviceName(segment))
            {
                return false;
            }
        }
        return true;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows device names ("NUL", "com1.txt", "Lpt3 .log"), with or without extension, any case.</summary>
    public static bool IsReservedDeviceName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return ReservedDeviceNames.Contains(stem);
    }

    private static bool IsSha256Hex(string hash) =>
        hash.Length == 64 && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// The archive's file entries must be exactly the manifest (plus manifest.json itself): nothing extra,
    /// nothing missing, no unsafe path, no two names differing only in case, sizes as listed.
    /// Directory entries ("Agent/") are allowed only when empty and with a safe path. Null when it matches.
    /// </summary>
    public static string? CheckArchive(IReadOnlyList<(string FullName, long Length)> archive, IReadOnlyList<ManifestEntry> manifest)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(manifest);
        var expected = manifest.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestSeen = false;
        foreach (var (rawName, length) in archive)
        {
            var fullName = ArchiveName(rawName);
            var isDirectory = fullName.EndsWith('/');
            var name = isDirectory ? fullName[..^1] : fullName;
            if (!IsSafeRelativePath(name))
            {
                return "unsafe path: " + fullName;
            }
            if (!seen.Add(name))
            {
                return "duplicate name: " + fullName;
            }
            if (isDirectory)
            {
                if (length != 0)
                {
                    return "directory entry with data: " + fullName;
                }
                continue;
            }
            if (name == FileName)
            {
                manifestSeen = true;
                continue;
            }
            if (!expected.TryGetValue(name, out var entry))
            {
                return "not in manifest: " + fullName;
            }
            if (entry.Size != length)
            {
                return "size differs: " + fullName;
            }
        }
        if (!manifestSeen)
        {
            return "no manifest";
        }
        var missing = manifest.FirstOrDefault(e => !seen.Contains(e.Path));
        return missing is null ? null : "missing: " + missing.Path;
    }

    public static string? CheckArchive(ZipArchive zip, IReadOnlyList<ManifestEntry> manifest)
    {
        ArgumentNullException.ThrowIfNull(zip);
        return CheckArchive([.. zip.Entries.Select(e => (e.FullName, e.Length))], manifest);
    }

    /// <summary>SHA-256 of what was actually written, read back through the caller's handle. Null when it matches.</summary>
    public static string? CheckWritten(ManifestEntry entry, Stream writtenContent)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(writtenContent);
        writtenContent.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(writtenContent));
        if (writtenContent.Length != entry.Size)
        {
            return string.Create(CultureInfo.InvariantCulture, $"size differs: {entry.Path}");
        }
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(entry.Sha256))
            ? null
            : "SHA-256 differs: " + entry.Path;
    }
}
