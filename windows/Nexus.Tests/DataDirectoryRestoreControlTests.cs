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
    public void Unreadable_result_still_gets_the_second_write()
    {
        var control = DaclPresent | DataDirectoryPolicy.DaclAutoInherited | SelfRelative;
        Assert.NotNull(DataDirectoryPolicy.RestoreRewriteControl(control, Saved, null));
    }
}
