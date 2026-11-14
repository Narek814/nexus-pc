using System.Text.Json;

namespace Nexus.Agent.Storage;

/// <summary>
/// Append-only JSON-lines log of every request (SECURITY.md §3.10). Records who, what and the
/// result, never payload contents. Rotates at 5 MB, keeping one previous file.
/// </summary>
internal sealed class AuditLog
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    public AuditLog(string dataDirectory, TimeProvider time)
    {
        _path = Path.Combine(dataDirectory, "audit.log");
        _time = time;
    }

    public string FilePath => _path;

    public void Record(string? deviceId, string type, bool ok, string? error, string? remote)
    {
        // `type` may come from an unauthenticated client: cap it so the log cannot be bloated.
        var safeType = type.Length > 64 ? type[..64] : type;
        var line = JsonSerializer.Serialize(new { ts = _time.GetUtcNow(), device = deviceId, type = safeType, ok, error, remote });
        lock (_lock)
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }
            File.AppendAllText(_path, line + "\n");
        }
    }
}
