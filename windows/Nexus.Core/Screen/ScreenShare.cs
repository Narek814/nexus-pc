using System.Text.Json;
using Nexus.Core.Protocol;

namespace Nexus.Core.Screen;

public sealed record ScreenFramePayload(int MaxWidth, int Quality, string? Known);

/// <summary>One captured frame: JPEG bytes, or Unchanged when the picture equals the hash the phone already has.</summary>
public sealed record ScreenCapture(bool Ok, byte[]? Jpeg, int Width, int Height, string? Hash, bool Unchanged = false, string? Message = null);

public static class ScreenParsers
{
    public const int MinWidth = 320;
    public const int MaxWidth = 1920;

    public static PayloadResult Frame(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || p.EnumerateObject().Any(x => x.Name is not ("maxWidth" or "quality" or "known")))
        {
            return PayloadResult.Invalid("Only 'maxWidth', 'quality' and 'known' are allowed.");
        }
        var maxWidth = 1280;
        var quality = 60;
        if (p.TryGetProperty("maxWidth", out var w) && (w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out maxWidth) || maxWidth is < MinWidth or > MaxWidth))
        {
            return PayloadResult.Invalid($"'maxWidth' must be {MinWidth}..{MaxWidth}.");
        }
        if (p.TryGetProperty("quality", out var q) && (q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out quality) || quality is < 20 or > 95))
        {
            return PayloadResult.Invalid("'quality' must be 20..95.");
        }
        string? known = null;
        if (p.TryGetProperty("known", out var k))
        {
            known = k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (known is null || !Files.FileParsers.IsSha256(known))
            {
                return PayloadResult.Invalid("'known' must be a frame hash.");
            }
        }
        return PayloadResult.Ok(new ScreenFramePayload(maxWidth, quality, known));
    }
}

/// <summary>
/// Devices the person at the PC allowed to see the screen (asked once per device, on the PC itself —
/// SECURITY.md §6). Stored in the user's own profile, not changeable from the phone.
/// </summary>
public sealed class ScreenApprovals
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private HashSet<string> _approved;

    public ScreenApprovals(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "screen-approvals.json");
        _approved = Load(_path);
    }

    public bool IsApproved(string deviceId)
    {
        lock (_lock)
        {
            return _approved.Contains(deviceId);
        }
    }

    public void Approve(string deviceId)
    {
        lock (_lock)
        {
            if (_approved.Add(deviceId))
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_approved.Order()));
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _approved = [];
            File.WriteAllText(_path, "[]");
        }
    }

    private static HashSet<string> Load(string path)
    {
        try
        {
            return File.Exists(path) ? [.. JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? []] : [];
        }
        catch (JsonException)
        {
            return []; // damaged: ask again
        }
    }
}
