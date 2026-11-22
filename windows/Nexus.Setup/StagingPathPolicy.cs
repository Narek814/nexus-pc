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
/// <item>From <c>strictFromLevel</c> on (NexusPC and staging, our own folders) any allow ACE of an untrusted SID with a
/// write-type right refuses, inherit-only and inheritable ACEs included: <see cref="WriteRightsOnOwnFolders"/>
/// (FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY/APPEND, FILE_WRITE_EA, FILE_DELETE_CHILD, FILE_WRITE_ATTRIBUTES, DELETE, WRITE_DAC,
/// WRITE_OWNER, MAXIMUM_ALLOWED, GENERIC_ALL, GENERIC_WRITE). Only exception: inherit-only CREATOR OWNER / CREATOR GROUP
/// (inherited from Program Files by default): they apply to whoever creates a child, which needs a write right checked here.</item>
/// <item>Above that (C:\, Program Files) everything else is allowed, explicitly including Authenticated Users' "create folders" (FILE_ADD_SUBDIRECTORY,
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

    public const uint FileAddFile = 0x2, FileAddSubdirectory = 0x4, FileWriteEa = 0x10, FileWriteAttributes = 0x100;
    public const uint MaximumAllowed = 0x0200_0000, GenericWrite = 0x4000_0000;

    public const uint WriteRightsOnOwnFolders = FileAddFile | FileAddSubdirectory | FileWriteEa | FileDeleteChild | FileWriteAttributes
        | Delete | WriteDac | WriteOwner | MaximumAllowed | GenericAll | GenericWrite;

    private const string CreatorOwner = "S-1-3-0", CreatorGroup = "S-1-3-1";

    /// <summary>Components from the volume root down, in order; the first is the volume root.</summary>
    /// <param name="strictFromLevel">Index of NexusPC in the full path (components from there on are ours).</param>
    public static string? Check(IReadOnlyList<PathComponentState> components, int strictFromLevel = int.MaxValue)
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
            // An allow ACE whose type the reader did not understand ("(ACE type n)", mask unknown) is treated as dangerous.
            if (c.Dacl.FirstOrDefault(a => a.Allow && a.Sid.StartsWith('(')) is { } unknown)
            {
                return $"unrecognised access entry {unknown.Sid} on {c.Path}";
            }
            if (i >= strictFromLevel && c.Dacl.FirstOrDefault(a => a.Allow && (a.Mask & WriteRightsOnOwnFolders) != 0
                    && !DataDirectoryPolicy.TrustedSids.Contains(a.Sid)
                    && !((a.Sid == CreatorOwner || a.Sid == CreatorGroup) && (a.Flags & InheritOnly) != 0)) is { } writer)
            {
                return $"{writer.Sid} may write into {c.Path}";
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

    /// <summary>
    /// "C:\Program Files\NexusPC\.staging-x" → ["C:\", "C:\Program Files", "C:\Program Files\NexusPC", "C:\Program Files\NexusPC\.staging-x"].
    /// Only plain drive-letter paths; anything else (UNC, \\?\, relative, "." or ".." segments) → empty, which refuses.
    /// </summary>
    public static IReadOnlyList<string> Components(string windowsPath)
    {
        ArgumentNullException.ThrowIfNull(windowsPath);
        if (windowsPath.Length < 3 || !char.IsAsciiLetter(windowsPath[0]) || windowsPath[1] != ':' || windowsPath[2] != '\\')
        {
            return [];
        }
        var segments = windowsPath[3..].TrimEnd('\\').Split('\\');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('/', StringComparison.Ordinal) || s.Contains(':', StringComparison.Ordinal)))
        {
            return [];
        }
        var list = new List<string> { windowsPath[..3] };
        foreach (var segment in segments)
        {
            list.Add(list.Count == 1 ? list[0] + segment : list[^1] + "\\" + segment);
        }
        return list;
    }

    /// <summary>
    /// The staging folder we just created: owner Administrators, protected DACL with only SYSTEM and Administrators,
    /// not a reparse point, and its final path (GetFinalPathNameByHandle) is exactly the path asked for.
    /// </summary>
    public static string? CheckCreatedStaging(string expectedPath, PathComponentState state, bool daclProtected, string? finalPath)
    {
        ArgumentNullException.ThrowIfNull(expectedPath);
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsReparsePoint)
        {
            return "staging is a reparse point";
        }
        if (!string.Equals(state.OwnerSid, DataDirectoryPolicy.Administrators, StringComparison.OrdinalIgnoreCase))
        {
            return "staging owner is not Administrators: " + (state.OwnerSid ?? "(none)");
        }
        if (!daclProtected || state.Dacl.Count == 0
            || state.Dacl.Any(a => !a.Allow || (a.Sid != DataDirectoryPolicy.LocalSystem && a.Sid != DataDirectoryPolicy.Administrators)))
        {
            return "staging DACL is not the protected SYSTEM + Administrators one";
        }
        if (finalPath is null || !string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return $"staging final path {finalPath ?? "(unknown)"} is not {expectedPath}";
        }
        return null;
    }
}
