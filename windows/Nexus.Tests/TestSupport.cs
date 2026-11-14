using System.Text.Json;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Shared
{
    public static string PathTo(string relative) => Path.Combine(AppContext.BaseDirectory, "shared", relative);

    public static JsonDocument Load(string relative) => JsonDocument.Parse(File.ReadAllText(PathTo(relative)));

    public static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    public static CommandValidator Validator(FixedTimeProvider? time = null) =>
        CommandValidator.CreateDefault(time ?? new FixedTimeProvider(Now));

    public static ClientContext Device(Permission permissions = Permissions.DefaultAfterPairing, string id = "device-1") =>
        ClientContext.ForDevice(id, permissions);

    public static string Request(string type, string payload = "{}", string? id = null, string ts = "2026-11-14T10:00:00Z", string extra = "") =>
        $$"""{"v":1,"id":"{{id ?? Guid.NewGuid().ToString()}}","kind":"request","type":"{{type}}","ts":"{{ts}}","payload":{{payload}}{{extra}}}""";
}
