using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Windows;

/// <summary>MoveFileEx without MOVEFILE_COPY_ALLOWED: a rename on one volume, atomic, written through (T5 section 3).</summary>
[SupportedOSPlatform("windows")]
public static partial class AtomicMove
{
    private const uint ReplaceExisting = 0x1, WriteThrough = 0x8;

    /// <summary>Renames a folder or file; fails if <paramref name="to"/> exists or is on another volume.</summary>
    public static void Rename(string from, string to) => Move(from, to, WriteThrough);

    /// <summary>Renames a file over an existing one (journal update).</summary>
    public static void Replace(string from, string to) => Move(from, to, ReplaceExisting | WriteThrough);

    private static void Move(string from, string to, uint flags)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!MoveFileExW(from, to, flags))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"cannot move {from} to {to}: {new Win32Exception(error).Message}", error);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string replacement, uint flags);
}
