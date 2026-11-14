using Nexus.Core.Protocol;

namespace Nexus.Tests;

public class ParserTests
{
    [Fact]
    public void Duplicate_keys_are_rejected()
    {
        // A classic bypass: one layer reads the first "type", another the last.
        var json = """{"v":1,"id":"55555555-5555-4555-8555-555555555555","kind":"request","type":"pc.status","type":"power.shutdown","ts":"2026-11-14T10:00:00Z","payload":{}}""";
        Assert.Equal(ErrorCodes.Malformed, EnvelopeParser.Parse(json).Error?.Code);
    }

    [Fact]
    public void Duplicate_keys_inside_payload_are_rejected()
    {
        var json = Shared.Request("apps.launch", """{"appId":"steam","appId":"evil"}""");
        Assert.Equal(ErrorCodes.Malformed, EnvelopeParser.Parse(json).Error?.Code);
    }

    [Fact]
    public void Oversized_message_is_rejected_before_parsing()
    {
        var json = Shared.Request("pc.status", "{\"x\":\"" + new string('a', ProtocolInfo.MaxMessageBytes) + "\"}");
        Assert.Equal(ErrorCodes.TooLarge, EnvelopeParser.Parse(json).Error?.Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"v":1,}""")]
    [InlineData("""{"v":"1","id":"55555555-5555-4555-8555-555555555555","kind":"request","type":"pc.status","ts":"2026-11-14T10:00:00Z","payload":{}}""")]
    [InlineData("""{"v":1.5,"id":"55555555-5555-4555-8555-555555555555","kind":"request","type":"pc.status","ts":"2026-11-14T10:00:00Z","payload":{}}""")]
    public void Garbage_is_malformed(string json)
    {
        Assert.Equal(ErrorCodes.Malformed, EnvelopeParser.Parse(json).Error?.Code);
    }

    [Fact]
    public void Excessive_nesting_is_rejected()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "1" + new string('}', 40);
        Assert.Equal(ErrorCodes.Malformed, EnvelopeParser.Parse(Shared.Request("pc.status", deep)).Error?.Code);
    }

    [Theory]
    [InlineData("2026-11-14T10:00:00Z\\n")]
    [InlineData("2026-11-14 10:00:00Z")]
    [InlineData("2026-11-14T10:00:00")]
    [InlineData("2026-13-40T10:00:00Z")]
    public void Non_rfc3339_timestamps_are_rejected(string ts)
    {
        Assert.Equal(ErrorCodes.Malformed, EnvelopeParser.Parse(Shared.Request("pc.status", ts: ts)).Error?.Code);
    }

    [Fact]
    public void Timestamp_offset_is_honoured()
    {
        var parsed = EnvelopeParser.Parse(Shared.Request("pc.status", ts: "2026-11-14T13:00:00+03:00"));
        Assert.Equal(Shared.Now, parsed.Envelope?.Timestamp);
    }

    [Theory]
    [InlineData("steam", true)]
    [InlineData("claude-code", true)]
    [InlineData("7zip", true)]
    [InlineData("steam\n", false)]
    [InlineData("-steam", false)]
    [InlineData("Steam", false)]
    [InlineData("steam.exe", false)]
    [InlineData("c:\\steam", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Slug_rules(string? value, bool expected)
    {
        Assert.Equal(expected, Identifiers.IsSlug(value));
        Assert.True(Identifiers.IsSlug(new string('a', 64)));
        Assert.False(Identifiers.IsSlug(new string('a', 65)));
    }
}
