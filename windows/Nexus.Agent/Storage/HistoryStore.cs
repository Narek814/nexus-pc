using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Agent.Storage;

internal sealed record HistoryEntry(DateTimeOffset Ts, string Kind, string Title, bool Ok, string? Detail, string? Device);

/// <summary>
/// PC history for the phone (P6): what happened, when and from which device — commands that change
/// something, automation runs, alerts. Never payload contents (no file names, typed or clipboard text).
/// JSON lines, rotated at 2 MB keeping one previous file.
/// </summary>
internal sealed class HistoryStore
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    public HistoryStore(string dataDirectory, TimeProvider time)
    {
        _path = Path.Combine(dataDirectory, "history.log");
        _time = time;
    }

    public void Record(string kind, string title, bool ok = true, string? detail = null, string? device = null)
    {
        var entry = new HistoryEntry(_time.GetUtcNow(), kind, Cap(title, 120)!, ok, Cap(detail, 300), Cap(device, 64));
        var line = JsonSerializer.Serialize(entry, Json);
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                File.AppendAllText(_path, line + "\n");
            }
            catch (IOException)
            {
                // History is best effort; it must never break a command.
            }
        }
    }

    /// <summary>Newest first, strictly older than <paramref name="before"/> when given.</summary>
    public IReadOnlyList<HistoryEntry> List(int limit, DateTimeOffset? before)
    {
        var entries = new List<HistoryEntry>();
        lock (_lock)
        {
            foreach (var path in new[] { _path, _path + ".1" })
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } entry && (before is null || entry.Ts < before))
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // a line cut by a power loss
                    }
                }
            }
        }
        return [.. entries.OrderByDescending(e => e.Ts).Take(limit)];
    }

    public static JsonObject ToJson(HistoryEntry e)
    {
        var json = new JsonObject
        {
            ["ts"] = e.Ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["kind"] = e.Kind,
            ["title"] = e.Title,
            ["ok"] = e.Ok,
        };
        if (e.Detail is not null)
        {
            json["detail"] = e.Detail;
        }
        if (e.Device is not null)
        {
            json["device"] = e.Device;
        }
        return json;
    }

    private static string? Cap(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "…";
}
