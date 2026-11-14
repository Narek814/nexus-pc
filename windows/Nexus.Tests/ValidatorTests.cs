using Nexus.Core.Apps;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Tests;

public class ValidatorTests
{
    [Fact]
    public void Same_message_id_is_rejected_the_second_time()
    {
        var validator = Shared.Validator();
        var message = Shared.Request("pc.status", id: "11111111-1111-4111-8111-111111111111");

        Assert.True(validator.Validate(message, Shared.Device()).IsSuccess);
        Assert.Equal(ErrorCodes.Replay, validator.Validate(message, Shared.Device()).Error?.Code);
    }

    [Fact]
    public void Same_message_id_from_different_devices_is_independent()
    {
        var validator = Shared.Validator();
        var message = Shared.Request("pc.status", id: "11111111-1111-4111-8111-111111111111");

        Assert.True(validator.Validate(message, Shared.Device(id: "a")).IsSuccess);
        Assert.True(validator.Validate(message, Shared.Device(id: "b")).IsSuccess);
    }

    [Fact]
    public void Replayed_message_stays_rejected_until_its_timestamp_leaves_the_window()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var validator = Shared.Validator(time);
        // Timestamp at the far edge of the window: the longest a message can stay acceptable.
        var message = Shared.Request("pc.status", id: "22222222-2222-4222-8222-222222222222", ts: "2026-11-14T10:05:00Z");

        Assert.True(validator.Validate(message, Shared.Device()).IsSuccess);
        foreach (var minutes in new[] { 1, 5, 9, 10 })
        {
            time.Now = Shared.Now.AddMinutes(minutes);
            Assert.Equal(ErrorCodes.Replay, validator.Validate(message, Shared.Device()).Error?.Code);
        }
        time.Now = Shared.Now.AddMinutes(10).AddSeconds(1);
        Assert.Equal(ErrorCodes.ClockSkew, validator.Validate(message, Shared.Device()).Error?.Code);
    }

    [Fact]
    public void Replay_guard_reports_capacity_instead_of_growing_without_bound()
    {
        var validator = new CommandValidator(CommandCatalog.V1, new ReplayGuard(capacity: 2), new FixedTimeProvider(Shared.Now));
        Assert.True(validator.Validate(Shared.Request("pc.status"), Shared.Device()).IsSuccess);
        Assert.True(validator.Validate(Shared.Request("pc.status"), Shared.Device()).IsSuccess);
        Assert.Equal(ErrorCodes.RateLimited, validator.Validate(Shared.Request("pc.status"), Shared.Device()).Error?.Code);
    }

    [Fact]
    public void Expired_entries_are_pruned_when_capacity_is_reached()
    {
        var time = new FixedTimeProvider(Shared.Now);
        var validator = new CommandValidator(CommandCatalog.V1, new ReplayGuard(capacity: 1), time);
        Assert.True(validator.Validate(Shared.Request("pc.status"), Shared.Device()).IsSuccess);

        time.Now = Shared.Now.AddMinutes(6);
        var later = Shared.Request("pc.status", ts: "2026-11-14T10:06:00Z");
        Assert.True(validator.Validate(later, Shared.Device()).IsSuccess);
    }

    [Fact]
    public void Unauthenticated_hello_is_not_tracked_by_replay_guard()
    {
        var validator = Shared.Validator();
        var hello = Shared.Request("system.hello", id: "33333333-3333-4333-8333-333333333333");
        Assert.True(validator.Validate(hello, ClientContext.Anonymous).IsSuccess);
        Assert.True(validator.Validate(hello, ClientContext.Anonymous).IsSuccess);
    }

    [Fact]
    public void Rejected_permission_does_not_consume_the_message_id()
    {
        var validator = Shared.Validator();
        var message = Shared.Request("power.sleep", id: "44444444-4444-4444-8444-444444444444");

        Assert.Equal(ErrorCodes.PermissionDenied, validator.Validate(message, Shared.Device(Permission.Monitoring)).Error?.Code);
        Assert.True(validator.Validate(message, Shared.Device(Permission.Power)).IsSuccess);
    }

    [Fact]
    public void Successful_validation_returns_typed_payloads()
    {
        var validator = Shared.Validator();

        var shutdown = validator.Validate(Shared.Request("power.shutdown", """{"delaySeconds":1800}""", extra: ""","confirmed":true"""), Shared.Device());
        Assert.Equal(new PowerPayload(1800, false), shutdown.Command?.Payload);

        var upsert = validator.Validate(Shared.Request("profiles.upsert", """{"profile":{"id":"gaming","name":"Gaming","appIds":["steam","discord"]}}"""), Shared.Device());
        var profile = Assert.IsType<ProfileUpsertPayload>(upsert.Command?.Payload).Profile;
        Assert.Equal(("gaming", "Gaming"), (profile.Id, profile.Name));
        Assert.Equal(["steam", "discord"], profile.AppIds);
    }
}
