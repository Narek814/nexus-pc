using Nexus.Core.Storage;

namespace Nexus.Tests;

/// <summary>
/// restore-acl puts back SE_DACL_AUTO_INHERITED (D:AI) exactly: the second write asks for it with
/// SE_DACL_AUTO_INHERIT_REQ, keeps every other saved control bit, and happens only when it is needed.
/// </summary>
public sealed class DataDirectoryRestoreControlTests
{
    private const int DaclPresent = 0x0004, SelfRelative = 0x8000;
    private const string Saved = "O:BAG:SYD:AI(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)(A;ID;FA;;;BA)";
    private const string AiLost = "O:BAG:SYD:(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)(A;ID;FA;;;BA)";

    [Fact]
    public void Auto_inherited_original_that_lost_ai_is_written_again_with_the_request_bit()
    {
        var control = DaclPresent | DataDirectoryPolicy.DaclAutoInherited | SelfRelative;
        Assert.Equal(control | DataDirectoryPolicy.DaclAutoInheritRequired, DataDirectoryPolicy.RestoreRewriteControl(control, Saved, AiLost));
    }

    [Fact]
    public void Protected_flag_is_kept_as_saved_in_the_second_write()
    {
        var control = DaclPresent | DataDirectoryPolicy.DaclAutoInherited | DataDirectoryPolicy.DaclProtectedControl | SelfRelative;
        var rewrite = DataDirectoryPolicy.RestoreRewriteControl(control, "O:BAD:PAI(A;;FA;;;SY)", "O:BAD:P(A;;FA;;;SY)");
        Assert.NotNull(rewrite);
        Assert.Equal(DataDirectoryPolicy.DaclProtectedControl, rewrite.Value & DataDirectoryPolicy.DaclProtectedControl);
        Assert.Equal(control, rewrite.Value & ~DataDirectoryPolicy.DaclAutoInheritRequired);
    }

    [Fact]
    public void Unprotected_original_does_not_become_protected()
    {
        var control = DaclPresent | DataDirectoryPolicy.DaclAutoInherited | SelfRelative;
        Assert.Equal(0, DataDirectoryPolicy.RestoreRewriteControl(control, Saved, AiLost)!.Value & DataDirectoryPolicy.DaclProtectedControl);
    }

    [Fact]
    public void No_second_write_when_the_first_one_was_exact()
    {
        var control = DaclPresent | DataDirectoryPolicy.DaclAutoInherited | SelfRelative;
        Assert.Null(DataDirectoryPolicy.RestoreRewriteControl(control, Saved, Saved));
    }

    [Fact]
    public void No_second_write_for_an_original_without_ai()
    {
        // The request bit would make the kernel keep the current inherited entries: never asked for without AI.
        Assert.Null(DataDirectoryPolicy.RestoreRewriteControl(DaclPresent | SelfRelative, AiLost, "O:BAG:SYD:(A;ID;FA;;;SY)"));
        Assert.Null(DataDirectoryPolicy.RestoreRewriteControl(DaclPresent | DataDirectoryPolicy.DaclProtectedControl | SelfRelative, "O:BAD:P(A;;FA;;;SY)", "O:BAD:(A;;FA;;;SY)"));
    }

    [Fact]
    public void Read_back_equal_to_the_saved_descriptor_is_an_exact_restore()
    {
        Assert.Equal(RestoreOutcome.Exact, DataDirectoryPolicy.CompareRestored(Saved, Saved));
        Assert.Equal(RestoreOutcome.Exact, DataDirectoryPolicy.CompareRestored(AiLost, AiLost));
    }

    [Theory]
    [InlineData(Saved, AiLost)]
    [InlineData("O:BAG:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)", "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData("O:DDG:DDD:AI(A;ID;FA;;;SY)", "O:DDG:DDD:(A;ID;FA;;;SY)")]
    [InlineData("O:BAD:AI", "O:BAD:")]
    public void Only_the_lost_ai_flag_is_an_incomplete_restore(string saved, string actual) =>
        Assert.Equal(RestoreOutcome.AutoInheritedLost, DataDirectoryPolicy.CompareRestored(saved, actual));

    [Theory]
    [InlineData(Saved, "O:BAG:SYD:(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)")]                    // AI lost and an entry lost
    [InlineData(Saved, "O:SYG:SYD:(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)(A;ID;FA;;;BA)")]      // AI lost and another owner
    [InlineData(Saved, "O:BAG:SYD:PAI(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)(A;ID;FA;;;BA)")]   // P added
    [InlineData(Saved, "O:BAG:SYD:P(A;;0x1301bf;;;BU)(A;ID;FA;;;SY)(A;ID;FA;;;BA)")]     // AI lost, P added
    [InlineData("O:BAG:SYD:PAI(A;;FA;;;SY)", "O:BAG:SYD:AI(A;;FA;;;SY)")]                // P lost
    [InlineData(AiLost, Saved)]                                                          // AI gained
    [InlineData(AiLost, "O:BAG:SYD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)")]                       // no AI saved, result differs
    [InlineData("O:BAG:SYD:AI(A;;FA;;;SY)", "O:BAG:SYD:AI(A;;FA;;;BA)")]                 // entries differ, flag kept
    public void Any_other_difference_is_a_failed_restore(string saved, string actual) =>
        Assert.Equal(RestoreOutcome.Different, DataDirectoryPolicy.CompareRestored(saved, actual));

    [Fact]
    public void Unreadable_read_back_is_a_failed_restore() =>
        Assert.Equal(RestoreOutcome.Different, DataDirectoryPolicy.CompareRestored(Saved, null));

    [Fact]
    public void Unreadable_result_still_gets_the_second_write()
    {
        var control = DaclPresent | DataDirectoryPolicy.DaclAutoInherited | SelfRelative;
        Assert.NotNull(DataDirectoryPolicy.RestoreRewriteControl(control, Saved, null));
    }
}
