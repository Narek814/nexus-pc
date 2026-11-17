using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Core.Protocol;

namespace Nexus.Core.Files;

public sealed record FilesListPayload;

public sealed record FileUploadBeginPayload(string Name, long Size, string Sha256);

public sealed record FileUploadChunkPayload(Guid UploadId, long Offset, byte[] Data);

public sealed record FileUploadRefPayload(Guid UploadId);

public sealed record FileReadPayload(string Name, long Offset, int Length);

public sealed record FileNamePayload(string Name);

public sealed record ClipboardSetPayload(string Text);

/// <summary>A refused file operation with a protocol error code.</summary>
public sealed class SharedFolderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record SharedFileInfo(string Name, long Size, DateTimeOffset Modified);

/// <summary>
/// The one folder a phone can see on the PC (Downloads\Nexus PC). Files from the phone arrive in a
/// private partial folder, are checked against their SHA-256 and only then appear in the shared folder,
/// under a sanitized, non-clashing name. The phone can list and read only plain files directly in the
/// shared folder: no subfolders, no links, no paths.
/// </summary>
public sealed class SharedFolder
{
    public const int MaxChunkBytes = 512 * 1024;
    public const long MaxFileBytes = 16L * 1024 * 1024 * 1024;
    public const int MaxListed = 500;
    public const long SpaceReserveBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan PartialLifetime = TimeSpan.FromDays(7);
    private const int MaxUploads = 8;

    private readonly string _shared;
    private readonly string _partial;
    private readonly TimeProvider _time;
    private readonly Func<string, long> _freeSpace;
    private readonly Dictionary<Guid, Upload> _uploads = [];
    private readonly Lock _lock = new();

    private sealed record Upload(string Name, long Size, string Sha256, string PartialPath, DateTimeOffset Started);

    public SharedFolder(string sharedFolder, string partialFolder, TimeProvider? time = null, Func<string, long>? freeSpace = null)
    {
        _shared = Path.GetFullPath(sharedFolder);
        _partial = Path.GetFullPath(partialFolder);
        _time = time ?? TimeProvider.System;
        _freeSpace = freeSpace ?? DefaultFreeSpace;
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_partial);
        RemoveStalePartials();
    }

    public string FolderPath => _shared;

    /// <summary>Called with the final path after a file from the phone is verified (the tray marks it as downloaded).</summary>
    public event EventHandler<string>? Received;

    public IReadOnlyList<SharedFileInfo> List() =>
        [.. VisibleFiles()
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxListed)
            .Select(f => new SharedFileInfo(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))];

    public long FreeBytes => _freeSpace(_shared);

    public (Guid UploadId, long Offset) Begin(FileUploadBeginPayload request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_freeSpace(_partial) < request.Size + SpaceReserveBytes)
        {
            throw new SharedFolderException(ErrorCodes.FilesNoSpace, "На диске ПК недостаточно места для этого файла.");
        }
        // Keyed by content hash: a retried upload of the same file continues where it stopped.
        var partialPath = Path.Combine(_partial, request.Sha256 + ".part");
        long offset = 0;
        if (File.Exists(partialPath))
        {
            offset = new FileInfo(partialPath).Length;
            if (offset > request.Size)
            {
                File.Delete(partialPath);
                offset = 0;
            }
        }
        if (offset == 0)
        {
            File.WriteAllBytes(partialPath, []);
        }

        var id = Guid.NewGuid();
        lock (_lock)
        {
            if (_uploads.Count >= MaxUploads)
            {
                _uploads.Remove(_uploads.MinBy(u => u.Value.Started).Key);
            }
            _uploads[id] = new Upload(request.Name, request.Size, request.Sha256, partialPath, _time.GetUtcNow());
        }
        return (id, offset);
    }

    public long Chunk(FileUploadChunkPayload chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var upload = Find(chunk.UploadId);
        var length = File.Exists(upload.PartialPath) ? new FileInfo(upload.PartialPath).Length : -1;
        if (length != chunk.Offset)
        {
            throw new SharedFolderException(ErrorCodes.FilesOffsetMismatch, $"Ожидалась часть с позиции {length}.");
        }
        if (chunk.Offset + chunk.Data.Length > upload.Size)
        {
            throw new SharedFolderException(ErrorCodes.InvalidPayload, "Данных больше, чем заявленный размер файла.");
        }
        using (var stream = new FileStream(upload.PartialPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write(chunk.Data);
        }
        return chunk.Offset + chunk.Data.Length;
    }

    public SharedFileInfo Finish(Guid uploadId)
    {
        var upload = Find(uploadId);
        var info = new FileInfo(upload.PartialPath);
        if (!info.Exists || info.Length != upload.Size)
        {
            throw new SharedFolderException(ErrorCodes.FilesOffsetMismatch, $"Файл получен не полностью ({(info.Exists ? info.Length : 0)} из {upload.Size} байт).");
        }
        if (!string.Equals(HashFile(upload.PartialPath), upload.Sha256, StringComparison.Ordinal))
        {
            Forget(uploadId);
            File.Delete(upload.PartialPath);
            throw new SharedFolderException(ErrorCodes.FilesHashMismatch, "Контрольная сумма не совпала — файл повреждён при передаче. Отправьте ещё раз.");
        }

        string target;
        lock (_lock)
        {
            target = Path.Combine(_shared, FileNames.MakeUnique(FileNames.Sanitize(upload.Name), name => File.Exists(Path.Combine(_shared, name)) || Directory.Exists(Path.Combine(_shared, name))));
            File.Move(upload.PartialPath, target);
            _uploads.Remove(uploadId);
        }
        Received?.Invoke(this, target);
        return new SharedFileInfo(Path.GetFileName(target), upload.Size, _time.GetUtcNow());
    }

    public void Cancel(Guid uploadId)
    {
        Upload? upload;
        lock (_lock)
        {
            _uploads.Remove(uploadId, out upload);
        }
        if (upload is not null && File.Exists(upload.PartialPath))
        {
            File.Delete(upload.PartialPath);
        }
    }

    public (byte[] Data, long Size) Read(FileReadPayload request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var file = Resolve(request.Name);
        var size = file.Length;
        if (request.Offset > size)
        {
            throw new SharedFolderException(ErrorCodes.FilesOffsetMismatch, $"Файл короче: {size} байт.");
        }
        var length = (int)Math.Min(request.Length, size - request.Offset);
        var data = new byte[length];
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(request.Offset, SeekOrigin.Begin);
        stream.ReadExactly(data);
        return (data, size);
    }

    public (string Sha256, long Size) Hash(string name)
    {
        var file = Resolve(name);
        return (HashFile(file.FullName), file.Length);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private IEnumerable<FileInfo> VisibleFiles() =>
        new DirectoryInfo(_shared).EnumerateFiles()
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0);

    /// <summary>Only an exact name of a listed file: never a path, never a link out of the folder.</summary>
    private FileInfo Resolve(string name) =>
        VisibleFiles().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
        ?? throw new SharedFolderException(ErrorCodes.FilesNotFound, $"Файла «{name}» нет в папке Nexus PC.");

    private Upload Find(Guid id)
    {
        lock (_lock)
        {
            return _uploads.TryGetValue(id, out var upload)
                ? upload
                : throw new SharedFolderException(ErrorCodes.FilesUploadNotFound, "Передача не найдена — начните заново (продолжится с того же места).");
        }
    }

    private void Forget(Guid id)
    {
        lock (_lock)
        {
            _uploads.Remove(id);
        }
    }

    private void RemoveStalePartials()
    {
        var cutoff = _time.GetUtcNow() - PartialLifetime;
        foreach (var file in new DirectoryInfo(_partial).EnumerateFiles("*.part"))
        {
            if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                file.Delete();
            }
        }
    }

    private static long DefaultFreeSpace(string path) => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
}

/// <summary>Turns a name sent by a phone into a safe plain Windows file name.</summary>
public static class FileNames
{
    public const int MaxLength = 120;

    private static readonly FrozenSet<string> Reserved = new[]
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Last path component only: "../../x" or "C:\Windows\x" become "x".
        var last = name.Split('/', '\\').LastOrDefault(part => part.Length > 0) ?? "";
        var builder = new StringBuilder(last.Length);
        foreach (var c in last)
        {
            builder.Append(char.IsControl(c) || "<>:\"|?*".Contains(c, StringComparison.Ordinal) ? '_' : c);
        }
        var clean = builder.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean.All(c => c == '.'))
        {
            clean = "file";
        }
        var stem = clean.Split('.')[0];
        if (Reserved.Contains(stem.TrimEnd()))
        {
            clean = "_" + clean;
        }
        if (clean.Length > MaxLength)
        {
            var extension = Path.GetExtension(clean);
            extension = extension.Length is > 0 and <= 16 ? extension : "";
            clean = clean[..(MaxLength - extension.Length)].TrimEnd('.', ' ') + extension;
        }
        return clean;
    }

    /// <summary>"photo.jpg" → "photo (2).jpg" while the name is taken.</summary>
    public static string MakeUnique(string name, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken(name))
        {
            return name;
        }
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }
}

public static class FileParsers
{
    public const int MaxClipboardChars = 100_000;

    public static PayloadResult UploadBegin(JsonElement p)
    {
        if (!Only(p, "name", "size", "sha256") || !Str(p, "name", out var name) || name.Length is 0 or > 255
            || !p.TryGetProperty("size", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out var size)
            || size < 0 || size > SharedFolder.MaxFileBytes
            || !Str(p, "sha256", out var sha) || !IsSha256(sha))
        {
            return PayloadResult.Invalid($"'name' 1..255 characters, 'size' 0..{SharedFolder.MaxFileBytes}, 'sha256' 64 lowercase hex.");
        }
        return PayloadResult.Ok(new FileUploadBeginPayload(name, size, sha));
    }

    public static PayloadResult UploadChunk(JsonElement p)
    {
        if (!Only(p, "uploadId", "offset", "data") || !Id(p, out var id) || !Long(p, "offset", out var offset)
            || !Str(p, "data", out var text) || text.Length == 0 || text.Length > (SharedFolder.MaxChunkBytes + 2) / 3 * 4)
        {
            return PayloadResult.Invalid($"'uploadId', 'offset' ≥ 0 and 'data' (base64, ≤ {SharedFolder.MaxChunkBytes} bytes) are required.");
        }
        var buffer = new byte[SharedFolder.MaxChunkBytes];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written == 0)
        {
            return PayloadResult.Invalid("'data' must be base64.");
        }
        return PayloadResult.Ok(new FileUploadChunkPayload(id, offset, buffer[..written]));
    }

    public static PayloadResult UploadRef(JsonElement p) =>
        Only(p, "uploadId") && Id(p, out var id)
            ? PayloadResult.Ok(new FileUploadRefPayload(id))
            : PayloadResult.Invalid("'uploadId' is required.");

    public static PayloadResult Read(JsonElement p)
    {
        if (!Only(p, "name", "offset", "length") || !Name(p, out var name) || !Long(p, "offset", out var offset)
            || !p.TryGetProperty("length", out var l) || l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out var length)
            || length is < 1 or > SharedFolder.MaxChunkBytes)
        {
            return PayloadResult.Invalid($"'name', 'offset' ≥ 0 and 'length' 1..{SharedFolder.MaxChunkBytes} are required.");
        }
        return PayloadResult.Ok(new FileReadPayload(name, offset, length));
    }

    public static PayloadResult NameRef(JsonElement p) =>
        Only(p, "name") && Name(p, out var name)
            ? PayloadResult.Ok(new FileNamePayload(name))
            : PayloadResult.Invalid("'name' must be a file name (no path).");

    public static PayloadResult ClipboardSet(JsonElement p) =>
        Only(p, "text") && Str(p, "text", out var text) && text.Length is > 0 and <= MaxClipboardChars
            ? PayloadResult.Ok(new ClipboardSetPayload(text))
            : PayloadResult.Invalid($"'text' must be 1..{MaxClipboardChars} characters.");

    public static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool Name(JsonElement p, out string name) =>
        Str(p, "name", out name) && name.Length is > 0 and <= 255 && name.IndexOfAny(['/', '\\', ':']) < 0 && name is not ("." or "..");

    private static bool Only(JsonElement p, params string[] allowed) =>
        p.ValueKind == JsonValueKind.Object && p.EnumerateObject().All(x => allowed.Contains(x.Name, StringComparer.Ordinal));

    private static bool Str(JsonElement p, string name, out string value)
    {
        value = "";
        if (!p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = v.GetString()!;
        return true;
    }

    private static bool Long(JsonElement p, string name, out long value)
    {
        value = 0;
        return p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value) && value >= 0;
    }

    private static bool Id(JsonElement p, out Guid id)
    {
        id = Guid.Empty;
        return Str(p, "uploadId", out var text) && Guid.TryParseExact(text, "D", out id);
    }
}
