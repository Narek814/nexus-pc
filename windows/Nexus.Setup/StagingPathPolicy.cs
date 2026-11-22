using Nexus.Core.Storage;

namespace Nexus.Setup;

/// <summary>One existing component of the staging path, read through a handle opened with FILE_FLAG_OPEN_REPARSE_POINT.</summary>
internal sealed record PathComponentState(string Path, bool IsReparsePoint, string? OwnerSid, IReadOnlyList<DataAce> Dacl, bool IsVolumeRoot = false);

/// <summary>
/// T5 section 1: rules for every existing component of C:\ → Program Files → NexusPC → .staging-&lt;GUID&gt;.
/// <list type="bullet">
/// <item>Any reparse point → refuse (owner decision 2, also when Program Files itself is a junction).</item>
/// <item>Owner: SYSTEM, Administrators or TrustedInstaller (the owner of C:\ on Windows 10/11 is TrustedInstaller
/// or SYSTEM/Administrators depending on how the volume was set up; all three are already trusted).</item>
/// <item>An allow ACE (not inherit-only) of any other SID carrying DELETE, WRITE_DAC, WRITE_OWNER or GENERIC_ALL
/// on the component → refuse: it could replace, rename or re-own it.</item>
/// <item>Same for FILE_DELETE_CHILD (or GENERIC_ALL) on the component's parent: deleting/renaming a child.</item>
/// <item>Everything else is allowed, explicitly including Authenticated Users' "create folders" (FILE_ADD_SUBDIRECTORY,
/// 0x4) and "create files" (0x2) on C:\ and inherit-only CREATOR OWNER ACEs: none of them can replace Program Files.</item>
/// </list>
/// Deny ACEs never grant anything and are ignored. Null when the whole path is acceptable.
/// </summary>
internal static class StagingPathPolicy
{
    public const uint Delete = 0x0001_0000;
    public const uint WriteDac = 0x0004_0000;
    public const uint WriteOwner = 0x0008_0000;
    public const uint GenericAll = 0x1000_0000;
    public const uint FileDeleteChild = 0x0000_0040;
    public const int InheritOnly = 0x08;

    public const uint DangerousOnComponent = Delete | WriteDac | WriteOwner | GenericAll;
    public const uint DangerousOnParent = FileDeleteChild | GenericAll;

    /// <summary>Components from the volume root down, in order; the first is the volume root.</summary>
    public static string? Check(IReadOnlyList<PathComponentState> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        for (var i = 0; i < components.Count; i++)
        {
            var c = components[i];
            if (c.IsReparsePoint)
            {
                return $"reparse point: {c.Path}";
            }
            if (c.OwnerSid is null || !DataDirectoryPolicy.TrustedSids.Contains(c.OwnerSid))
            {
                return $"untrusted owner {c.OwnerSid ?? "(none)"}: {c.Path}";
            }
            if (Untrusted(c.Dacl, DangerousOnComponent) is { } ace)
            {
                return $"{ace.Sid} may replace or re-own {c.Path}";
            }
            if (i + 1 < components.Count && Untrusted(c.Dacl, DangerousOnParent) is { } child)
            {
                return $"{child.Sid} may delete or rename entries of {c.Path}";
            }
        }
        return null;
    }

    private static DataAce? Untrusted(IReadOnlyList<DataAce> dacl, uint mask) =>
        dacl.FirstOrDefault(a => a.Allow && (a.Flags & InheritOnly) == 0 && (a.Mask & mask) != 0 && !DataDirectoryPolicy.TrustedSids.Contains(a.Sid));
}
