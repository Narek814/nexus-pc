using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Monitoring;

/// <summary>
/// Opens a library file by handle with FILE_FLAG_OPEN_REPARSE_POINT (a symbolic link or junction is opened
/// itself, not followed), reports its attributes and final path, and keeps it open with read sharing only
/// (nobody can replace, rename or delete it) while it is loaded by its absolute path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsNativeLibraryLoader : INativeLibraryLoader
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x0001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public IOpenedLibraryFile? Open(string path)
    {
        var handle = CreateFileW(path, FileReadAttributes, FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        try
        {
            var attributes = File.GetAttributes(handle);
            return new OpenedFile(handle, (attributes & FileAttributes.ReparsePoint) != 0, (attributes & FileAttributes.Directory) != 0, FinalPath(handle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            handle.Dispose();
            return null;
        }
    }

    /// <summary>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32: dependencies only from the DLL's folder and System32.</summary>
    internal const uint LoadFlags = NvmlLocation.LoadFlags;

    /// <summary>
    /// LoadLibraryExW with <see cref="LoadFlags"/>: exactly this file (the flags require a fully qualified path;
    /// a relative one fails), and its dependencies never from the current folder or PATH. NativeLibrary.TryLoad is
    /// not used: for an absolute path .NET replaces these flags with LOAD_WITH_ALTERED_SEARCH_PATH.
    /// </summary>
    public bool TryLoad(string absolutePath, out IntPtr handle)
    {
        handle = LoadLibraryExW(absolutePath, IntPtr.Zero, LoadFlags);
        return handle != IntPtr.Zero;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    public bool TryGetExport(IntPtr handle, string name, out IntPtr address) => NativeLibrary.TryGetExport(handle, name, out address);

    // On Windows NativeLibrary.Free is FreeLibrary and TryGetExport is GetProcAddress on the HMODULE.
    public void Free(IntPtr handle) => NativeLibrary.Free(handle);

    /// <summary>"\\?\C:\..." becomes "C:\..."; a network ("\\?\UNC\...") or unreadable path gives null (rejected).</summary>
    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
            {
                length = GetFinalPathNameByHandleW(handle, chars, (uint)buffer.Length, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
            }
            if (length == 0)
            {
                return null;
            }
            if (length < buffer.Length)
            {
                var path = new string(buffer, 0, (int)length);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            }
            if (length > 32768)
            {
                return null;
            }
            buffer = new char[length + 1];
        }
    }

    private sealed class OpenedFile(SafeFileHandle handle, bool isReparsePoint, bool isDirectory, string? finalPath) : IOpenedLibraryFile
    {
        public bool IsReparsePoint => isReparsePoint;

        public bool IsDirectory => isDirectory;

        public string? FinalPath => finalPath;

        public void Dispose() => handle.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);
}
