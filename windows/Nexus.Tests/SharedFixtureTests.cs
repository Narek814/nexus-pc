using System.Text.Json;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Tests;

/// <summary>Runs shared/protocol/fixtures — the same cases NexusKit (Swift) will run.</summary>
public class SharedFixtureTests
{
    public static TheoryData<string> CaseNames()
    {
        using var doc = Shared.Load("fixtures/requests.v1.json");
        var data = new TheoryData<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Fixture_case_matches_expected_outcome(string name)
    {
        using var doc = Shared.Load("fixtures/requests.v1.json");
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);

        var ctx = c.GetProperty("context");
        var time = new FixedTimeProvider(DateTimeOffset.Parse(ctx.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Permissions.TryParse(ctx.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!), out var permissions));
        var client = ctx.GetProperty("authenticated").GetBoolean() ? ClientContext.ForDevice("fixture-device", permissions) : ClientContext.Anonymous;

        var result = Shared.Validator(time).Validate(c.GetProperty("message").GetRawText(), client);

        var expect = c.GetProperty("expect");
        if (expect.GetProperty("ok").GetBoolean())
        {
            Assert.True(result.IsSuccess, $"expected success, got {result.Error?.Code}: {result.Error?.Message}");
        }
        else
        {
            Assert.False(result.IsSuccess, "expected failure, got success");
            Assert.Equal(expect.GetProperty("error").GetString(), result.Error!.Code);
        }
    }

    [Fact]
    public void Catalog_matches_schema_command_enum()
    {
        using var schema = Shared.Load("schemas/request.v1.schema.json");
        var schemaTypes = schema.RootElement.GetProperty("allOf").EnumerateArray()
            .Last().GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal);

        var catalogTypes = CommandCatalog.V1.All.Select(c => c.Type).Order(StringComparer.Ordinal);

        Assert.Equal(schemaTypes, catalogTypes);
    }

    [Fact]
    public void Every_catalog_command_is_covered_by_a_successful_fixture()
    {
        using var doc = Shared.Load("fixtures/requests.v1.json");
        var covered = doc.RootElement.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("expect").GetProperty("ok").GetBoolean())
            .Select(c => c.GetProperty("message").GetProperty("type").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(CommandCatalog.V1.All, c => Assert.Contains(c.Type, covered));
    }

    [Fact]
    public void Only_power_shutdown_and_restart_are_critical_in_v1()
    {
        var critical = CommandCatalog.V1.All.Where(c => c.Risk == RiskClass.Critical).Select(c => c.Type).Order(StringComparer.Ordinal);
        Assert.Equal(["power.restart", "power.shutdown"], critical);
    }

    [Fact]
    public void Only_hello_is_available_without_authentication()
    {
        Assert.Equal(["system.hello"], CommandCatalog.V1.All.Where(c => !c.RequiresAuth).Select(c => c.Type));
    }
}
