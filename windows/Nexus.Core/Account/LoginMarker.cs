using System.Globalization;
using System.Text.Json.Nodes;

namespace Nexus.Core.Account;

/// <summary>
/// Non-secret note "a sign-in was started at T", kept in the tray's user settings so that the next tray start can tell
/// a lost attempt (the service restarted: login.status says "none") from "nothing was started". It holds only the
/// time: never the userCode or the deviceCode.
/// </summary>
public interface ILoginMarker
{
    DateTimeOffset? Read();

    void Write(DateTimeOffset startedAt);

    void Clear();
}

/// <summary>%LOCALAPPDATA%\NexusPC\login-pending.json: <c>{"startedAt":"…"}</c>. Errors never break the tray.</summary>
public sealed class LoginMarkerFile(string path) : ILoginMarker
{
    public string FilePath { get; } = path;

    public DateTimeOffset? Read()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 1024)
            {
                return null;
            }
            var node = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            return node?["startedAt"] is JsonValue v && v.TryGetValue<string>(out var s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Write(DateTimeOffset startedAt)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var json = new JsonObject { ["startedAt"] = startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
            File.WriteAllText(FilePath, json.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the note the tray only loses the "sign-in was interrupted" notice.
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
