using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nexus.Core.Storage;

namespace Nexus.Windows;

/// <summary>
/// The data folder as Nexus.Core.Storage sees it, through handles only: every object is opened with
/// FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS (a link is reported, never followed),
/// folders are listed through their handle, and owner and DACL are read and written with
/// Get/SetKernelObjectSecurity on that handle — no name-based and no recursive ACL calls.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DataDirectoryFileSystem : IDataFileSystem
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileReadData = 0x0001; // FILE_LIST_DIRECTORY for folders
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeDevice = 0x40;
    private const uint AttributeReparsePoint = 0x400;
    private const uint AttributeOffline = 0x1000;
    private const uint AttributeRecallOnOpen = 0x40000;
    private const uint AttributeRecallOnDataAccess = 0x400000;
    private const int FileTypeDisk = 1;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int FileIdInfo = 18;
    private const int FileRenameInfo = 3;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorInsufficientBuffer = 122;
    public const int ErrorInvalidOwner = 1307;
    private const uint OwnerSecurityInformation = 1, GroupSecurityInformation = 2, DaclSecurityInformation = 4;

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public IDataObject Open(string fullPath, DataAccess access)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var rights = ReadControl | FileReadAttributes | FileReadData | Synchronize;
        if (access is DataAccess.ChangeSecurity or DataAccess.Rename)
        {
            rights |= WriteDac | WriteOwner;
        }
        if (access == DataAccess.Rename)
        {
            rights |= Delete;
        }
        var share = access == DataAccess.Inspect ? ShareRead | ShareWrite | ShareDelete : ShareRead;
        // SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION: a named pipe planted under a file name cannot impersonate this process.
        var handle = CreateFileW(Extended(fullPath), rights, share, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint | FlagBackupSemantics | SqosIdentification, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure("cannot open " + fullPath, error);
        }
        try
        {
            return new DataObject(handle, ReadState(handle, fullPath));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> ListChildren(IDataObject directory)
    {
        if (directory is not DataObject { State.Kind: DataObjectKind.Directory } open)
        {
            throw new ArgumentException("not an open folder", nameof(directory));
        }
        var names = new List<string>();
        var buffer = new byte[64 * 1024];
        var infoClass = FileIdBothDirectoryRestartInfo;
        while (true)
        {
            if (!GetFileInformationByHandleEx(open.Handle, infoClass, buffer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNoMoreFiles)
                {
                    return names;
                }
                throw Failure("cannot list " + open.State.FullPath, error);
            }
            infoClass = FileIdBothDirectoryInfo;
            var offset = 0;
            while (true)
            {
                // FILE_ID_BOTH_DIR_INFO: NextEntryOffset @0, FileNameLength @60, FileName @104.
                var next = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
                var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 60));
                var name = Encoding.Unicode.GetString(buffer, offset + 104, nameBytes);
                if (name is not ("." or ".."))
                {
                    names.Add(name);
                }
                if (next == 0)
                {
                    break;
                }
                offset += next;
            }
        }
    }

    public string ExpectedRootFinalPath(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(full) ?? throw new DataAccessException("the data folder has no parent: " + full);
        // The parent (C:\ProgramData) is resolved normally; only the data folder and what is inside must not be links.
        using var handle = CreateFileW(Extended(parent), FileReadAttributes | Synchronize, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting, FlagBackupSemantics | SqosIdentification, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw Failure("cannot open " + parent, Marshal.GetLastPInvokeError());
        }
        var resolved = FinalPath(handle) ?? throw new DataAccessException("cannot resolve " + parent);
        return Path.Join(resolved, Path.GetFileName(full));
    }

    public long AvailableBytes(string directory)
    {
        if (!GetDiskFreeSpaceExW(directory, out var available, out _, out _))
        {
            throw Failure("cannot read the free space of " + directory, Marshal.GetLastPInvokeError());
        }
        return (long)Math.Min(available, long.MaxValue);
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> Siblings(string path, string suffixPrefix)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
        {
            return [];
        }
        return [.. Directory.EnumerateDirectories(parent, Path.GetFileName(full) + suffixPrefix + "*")];
    }

    public void CreateProtectedDirectory(string path)
    {
        var error = TryCreateProtectedDirectory(path, withOwner: true);
        if (error != 0)
        {
            throw Failure("cannot create " + path, error);
        }
    }

    /// <summary>
    /// CreateDirectory with the security descriptor in place from the first moment (owner Administrators,
    /// protected DACL: SYSTEM and Administrators, inherited by everything inside). 0 on success,
    /// otherwise the Win32 error (ERROR_ALREADY_EXISTS when the folder is already there).
    /// </summary>
    public static int TryCreateProtectedDirectory(string path, bool withOwner)
    {
        ArgumentNullException.ThrowIfNull(path);
        var descriptor = Binary(ProtectedRootDescriptor(withOwner ? AdministratorsSid : null));
        unsafe
        {
            fixed (byte* sd = descriptor)
            {
                var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), Descriptor = (IntPtr)sd, InheritHandle = 0 };
                return CreateDirectoryW(path, ref attributes) ? 0 : Marshal.GetLastPInvokeError();
            }
        }
    }

    public static bool IsAlreadyExists(int error) => error == ErrorAlreadyExists;

    /// <summary>Opens an object for attribute queries only (identification-level impersonation); null if it cannot be opened.</summary>
    internal static SafeFileHandle? OpenForQuery(string path)
    {
        var handle = CreateFileW(path, FileReadAttributes, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, SqosIdentification, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    /// <summary>A new folder owned by Administrators whose DACL is inherited from its parent; fails if it exists.</summary>
    public void CreateDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var descriptor = Binary(OwnerOnlyDescriptor());
        unsafe
        {
            fixed (byte* sd = descriptor)
            {
                var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), Descriptor = (IntPtr)sd, InheritHandle = 0 };
                if (!CreateDirectoryW(Extended(path), ref attributes))
                {
                    throw Failure("cannot create " + path, Marshal.GetLastPInvokeError());
                }
            }
        }
    }

    /// <summary>A new file owned by Administrators whose DACL is inherited from its folder; fails if it exists.</summary>
    public Stream CreateFile(string path) => new FileStream(CreateOwnedFile(path, CreateNew), FileAccess.Write);

    private const uint CreateNew = 1;

    private static SafeFileHandle CreateOwnedFile(string path, uint disposition)
    {
        ArgumentNullException.ThrowIfNull(path);
        var descriptor = Binary(OwnerOnlyDescriptor());
        unsafe
        {
            fixed (byte* sd = descriptor)
            {
                var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), Descriptor = (IntPtr)sd, InheritHandle = 0 };
                var handle = CreateFileW(Extended(path), GenericWrite | FileReadAttributes, 0, (IntPtr)(&attributes), disposition, FileAttributeNormal | SqosIdentification, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw Failure("cannot create " + path, error);
                }
                return handle;
            }
        }
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileAttributeNormal = 0x80;
    private const uint SqosIdentification = 0x00100000 | 0x00010000; // SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION

    /// <summary>\\?\ form of a full path: no reinterpretation of names (trailing dots, device names, length limits).</summary>
    public static string Extended(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>Owner Administrators, no DACL in the descriptor: the new object inherits its access from its folder.</summary>
    private static RawSecurityDescriptor OwnerOnlyDescriptor() =>
        new(ControlFlags.SelfRelative, AdministratorsSid, null, null, null);

    public void FlushToDisk(Stream stream)
    {
        if (stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        else
        {
            stream?.Flush();
        }
    }

    public DataObjectId IdOf(Stream created)
    {
        if (created is not FileStream file)
        {
            throw new ArgumentException("not a file made by CreateFile", nameof(created));
        }
        var info = new byte[52];
        if (!GetFileInformationByHandle(file.SafeFileHandle, info))
        {
            throw Failure("cannot read the identity of " + file.Name, Marshal.GetLastPInvokeError());
        }
        var volume32 = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        var index = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(44)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48));
        return ReadId(file.SafeFileHandle, volume32, index);
    }

    /// <summary>FILE_ID_INFO (64-bit volume serial, 128-bit id); the 64-bit index where that is not available.</summary>
    private static DataObjectId ReadId(SafeFileHandle handle, uint volume32, ulong index)
    {
        var idInfo = new byte[24];
        return GetFileInformationByHandleEx(handle, FileIdInfo, idInfo, (uint)idInfo.Length)
            ? new DataObjectId(BinaryPrimitives.ReadUInt64LittleEndian(idInfo), Convert.ToHexStringLower(idInfo.AsSpan(8, 16)))
            : new DataObjectId(volume32, index.ToString("x16", CultureInfo.InvariantCulture));
    }

    public void WriteAllTextDurably(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(path);
        var temp = path + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp); // left by an interruption; replaced, so it gets this process's owner again
        }
        using (var stream = new FileStream(CreateOwnedFile(temp, CreateNew), FileAccess.Write))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(contents));
            stream.Flush(flushToDisk: true);
        }
        if (!MoveFileExW(temp, path, 0x1 | 0x8)) // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
        {
            throw Failure("cannot replace " + path, Marshal.GetLastPInvokeError());
        }
    }

    public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public IReadOnlyList<string> ListTree(string path) =>
        [.. Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
            .Select(p => Path.GetRelativePath(path, p))];

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    // ------------------------------------------------------------ reading one object

    private static DataObjectState ReadState(SafeFileHandle handle, string fullPath)
    {
        var info = new byte[52]; // BY_HANDLE_FILE_INFORMATION
        if (!GetFileInformationByHandle(handle, info))
        {
            throw Failure("cannot read " + fullPath, Marshal.GetLastPInvokeError());
        }
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(info);
        var lastWrite = BinaryPrimitives.ReadInt64LittleEndian(info.AsSpan(20));
        var volume32 = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        var size = ((long)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(36));
        var links = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(40));
        var index = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(44)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48));

        var id = ReadId(handle, volume32, index);

        uint tag = 0;
        if ((attributes & AttributeReparsePoint) != 0)
        {
            var tagInfo = new byte[8];
            if (GetFileInformationByHandleEx(handle, FileAttributeTagInfo, tagInfo, (uint)tagInfo.Length))
            {
                tag = BinaryPrimitives.ReadUInt32LittleEndian(tagInfo.AsSpan(4));
            }
        }
        var kind = (attributes & AttributeReparsePoint) != 0 ? DataObjectKind.ReparsePoint
            : GetFileType(handle) != FileTypeDisk || (attributes & (AttributeDevice | AttributeOffline | AttributeRecallOnOpen | AttributeRecallOnDataAccess)) != 0 ? DataObjectKind.Other
            : (attributes & AttributeDirectory) != 0 ? DataObjectKind.Directory
            : DataObjectKind.File;

        var state = new DataObjectState
        {
            FullPath = fullPath,
            Kind = kind,
            Attributes = attributes,
            ReparseTag = tag,
            LinkCount = links,
            FinalPath = FinalPath(handle),
            Id = id,
            Size = kind == DataObjectKind.File ? size : 0,
            LastWriteUtc = DateTimeOffset.FromFileTime(lastWrite).ToUniversalTime(),
        };
        return ReadSecurity(handle, state);
    }

    private static DataObjectState ReadSecurity(SafeFileHandle handle, DataObjectState state)
    {
        var buffer = new byte[1024];
        while (true)
        {
            if (GetKernelObjectSecurity(handle, OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation, buffer, (uint)buffer.Length, out var needed))
            {
                break;
            }
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorInsufficientBuffer || needed <= buffer.Length || needed > 1024 * 1024)
            {
                return state; // owner unknown: reported as an untrusted owner
            }
            buffer = new byte[needed];
        }
        var descriptor = new RawSecurityDescriptor(buffer, 0);
        var dacl = descriptor.DiscretionaryAcl;
        var aces = new List<DataAce>();
        if (dacl is not null)
        {
            foreach (GenericAce ace in dacl)
            {
                var deny = ace.AceType is AceType.AccessDenied or AceType.AccessDeniedObject or AceType.AccessDeniedCallback or AceType.AccessDeniedCallbackObject;
                aces.Add(ace is KnownAce known
                    ? new DataAce(known.SecurityIdentifier.Value, !deny, unchecked((uint)known.AccessMask), (int)ace.AceFlags)
                    : new DataAce("(ACE type " + ((int)ace.AceType).ToString(CultureInfo.InvariantCulture) + ")", !deny, 0, (int)ace.AceFlags));
            }
        }
        return state with
        {
            OwnerSid = descriptor.Owner?.Value,
            GroupSid = descriptor.Group?.Value,
            DaclPresent = (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && dacl is not null,
            DaclProtected = (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            Dacl = aces,
            SecurityDescriptor = descriptor.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access),
        };
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }
        var path = new string(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    // ------------------------------------------------------------ descriptors

    private static RawSecurityDescriptor ProtectedRootDescriptor(SecurityIdentifier? owner)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 2);
        acl.InsertAce(0, new CommonAce(AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, unchecked((int)DataDirectoryPolicy.FullControl), SystemSid, false, null));
        acl.InsertAce(1, new CommonAce(AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, unchecked((int)DataDirectoryPolicy.FullControl), AdministratorsSid, false, null));
        return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.SelfRelative,
            owner, null, null, acl);
    }

    /// <summary>What Windows itself would give an object inside the protected root: the root's two entries, marked inherited.</summary>
    private static RawSecurityDescriptor InheritedDescriptor(bool directory)
    {
        var flags = AceFlags.Inherited | (directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None);
        var acl = new RawAcl(GenericAcl.AclRevision, 2);
        acl.InsertAce(0, new CommonAce(flags, AceQualifier.AccessAllowed, unchecked((int)DataDirectoryPolicy.FullControl), SystemSid, false, null));
        acl.InsertAce(1, new CommonAce(flags, AceQualifier.AccessAllowed, unchecked((int)DataDirectoryPolicy.FullControl), AdministratorsSid, false, null));
        return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.SelfRelative,
            AdministratorsSid, null, null, acl);
    }

    private static byte[] Binary(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static DataAccessException Failure(string what, int error) =>
        new($"{what}: {new Win32Exception(error).Message} (error {error.ToString(CultureInfo.InvariantCulture)})", null, notFound: error is 2 or 3);

    private sealed class DataObject(SafeFileHandle handle, DataObjectState state) : IDataObject
    {
        public SafeFileHandle Handle { get; } = handle;

        public DataObjectState State { get; } = state;

        public Stream OpenContent() => State.Kind == DataObjectKind.File
            ? new HandleReadStream(Handle, State.Size)
            : throw new InvalidOperationException("not a file");

        public void SetSecurity(DataSecurity security)
        {
            RawSecurityDescriptor descriptor;
            uint information;
            switch (security)
            {
                case DataSecurity.ProtectedRoot:
                    descriptor = ProtectedRootDescriptor(AdministratorsSid);
                    information = OwnerSecurityInformation | DaclSecurityInformation;
                    break;
                case DataSecurity.InheritedFromRoot inherited:
                    descriptor = InheritedDescriptor(inherited.IsDirectory);
                    information = OwnerSecurityInformation | DaclSecurityInformation;
                    break;
                case DataSecurity.Original original:
                    descriptor = new RawSecurityDescriptor(original.Sddl);
                    information = DaclSecurityInformation
                        | (descriptor.Owner is null ? 0 : OwnerSecurityInformation)
                        | (descriptor.Group is null ? 0 : GroupSecurityInformation);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(security));
            }
            // SetKernelObjectSecurity sets exactly this object's descriptor; unlike SetSecurityInfo it does not walk children by name.
            if (!SetKernelObjectSecurity(Handle, information, Binary(descriptor)))
            {
                throw Failure("cannot set the access of " + State.FullPath, Marshal.GetLastPInvokeError());
            }
        }

        public void Rename(string newFullPath)
        {
            ArgumentNullException.ThrowIfNull(newFullPath);
            // FILE_RENAME_INFO (x64): ReplaceIfExists @0, RootDirectory @8, FileNameLength @16, FileName @20.
            var name = Encoding.Unicode.GetBytes(newFullPath);
            var buffer = new byte[20 + name.Length + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), (uint)name.Length);
            name.CopyTo(buffer, 20);
            if (!SetFileInformationByHandle(Handle, FileRenameInfo, buffer, (uint)buffer.Length))
            {
                throw Failure("cannot rename " + State.FullPath, Marshal.GetLastPInvokeError());
            }
        }

        public void Dispose() => Handle.Dispose();
    }

    /// <summary>Reads a file through the handle that was checked, with positional reads (the handle is not given away).</summary>
    private sealed class HandleReadStream(SafeFileHandle handle, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = RandomAccess.Read(handle, buffer, _position);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(string pathName, ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetFileType(SafeFileHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string replacement, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKernelObjectSecurity(SafeFileHandle handle, uint requested, [Out] byte[] descriptor, uint length, out uint lengthNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
}

/// <summary>Windows facts the repair command checks before it starts: sessions, the service, privileges.</summary>
[SupportedOSPlatform("windows")]
public static partial class DataRepairEnvironment
{
    private static readonly string[] StateNames = ["Active", "Connected", "ConnectQuery", "Shadow", "Disconnected", "Idle", "Listen", "Reset", "Down", "Init"];

    /// <summary>Every session on this PC with its user (WTSEnumerateSessions + WTSQuerySessionInformation).</summary>
    public static IReadOnlyList<InteractiveSession> Sessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var sessions = new List<InteractiveSession>();
        try
        {
            // WTS_SESSION_INFOW: DWORD SessionId; LPWSTR pWinStationName; WTS_CONNECTSTATE_CLASS State (pointer-aligned).
            var size = IntPtr.Size * 3;
            for (var i = 0; i < count; i++)
            {
                var item = list + (i * size);
                var id = Marshal.ReadInt32(item);
                var station = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item + IntPtr.Size));
                var state = Marshal.ReadInt32(item + (IntPtr.Size * 2));
                var name = UserName(id, out var failed);
                sessions.Add(new InteractiveSession(id, state >= 0 && state < StateNames.Length ? StateNames[state] : state.ToString(CultureInfo.InvariantCulture), name, station, failed));
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }
        return sessions;
    }

    /// <summary>The session's user; <paramref name="failed"/> when it cannot be read (the caller refuses: fail closed).</summary>
    private static string? UserName(int sessionId, out bool failed)
    {
        failed = !WTSQuerySessionInformationW(IntPtr.Zero, sessionId, 5, out var buffer, out _); // WTSUserName
        if (failed)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>SERVICE_STATUS dwCurrentState (1 stopped … 4 running), or null when the service is not installed.</summary>
    private const uint ServiceNoChange = 0xFFFFFFFF;

    /// <summary>"auto", "delayed-auto", "demand", "disabled" (or "type-N"); null when the service is not installed. Errors: IOException.</summary>
    public static string? ServiceStartType(string serviceName)
    {
        using var service = OpenService(serviceName, 0x0001); // SERVICE_QUERY_CONFIG
        if (service is null)
        {
            return null;
        }
        QueryServiceConfigW(service.Handle, null, 0, out var needed);
        var buffer = new byte[Math.Max(needed, 64u)];
        if (!QueryServiceConfigW(service.Handle, buffer, (uint)buffer.Length, out _))
        {
            throw ServiceFailure("read the start type of", serviceName);
        }
        var startType = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        var delayed = new byte[4];
        var isDelayed = startType == 2 && QueryServiceConfig2W(service.Handle, 3, delayed, 4, out _) && BinaryPrimitives.ReadInt32LittleEndian(delayed) != 0;
        return startType switch
        {
            2 => isDelayed ? "delayed-auto" : "auto",
            3 => "demand",
            4 => "disabled",
            _ => "type-" + startType.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static void SetServiceStartType(string serviceName, string startType)
    {
        uint type = startType switch
        {
            "auto" or "delayed-auto" => 2,
            "demand" => 3,
            "disabled" => 4,
            _ => throw new IOException("unknown start type " + startType),
        };
        using var service = OpenService(serviceName, 0x0001 | 0x0002) ?? throw new IOException("the Nexus PC service is not installed"); // QUERY_CONFIG | CHANGE_CONFIG
        if (!ChangeServiceConfigW(service.Handle, ServiceNoChange, type, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
        {
            throw ServiceFailure("change the start type of", serviceName);
        }
        if (type == 2)
        {
            var delayed = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(delayed, startType == "delayed-auto" ? 1 : 0);
            if (!ChangeServiceConfig2W(service.Handle, 3, delayed))
            {
                throw ServiceFailure("change the delayed start of", serviceName);
            }
        }
    }

    private static IOException ServiceFailure(string what, string serviceName) =>
        new($"Cannot {what} the service {serviceName}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    private sealed class ServiceHandles(IntPtr scm, IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose()
        {
            CloseServiceHandle(Handle);
            CloseServiceHandle(scm);
        }
    }

    private static ServiceHandles? OpenService(string serviceName, uint access)
    {
        var scm = OpenSCManagerW(null, null, 0x0001);
        if (scm == IntPtr.Zero)
        {
            throw ServiceFailure("open the service manager for", serviceName);
        }
        var service = OpenServiceW(scm, serviceName, access);
        if (service == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            CloseServiceHandle(scm);
            return error == 1060 ? null : throw new IOException($"Cannot open the service {serviceName}: {new Win32Exception(error).Message}");
        }
        return new ServiceHandles(scm, service);
    }

    /// <summary>The process serving a named pipe, if a client handle can be opened (the pipe may be busy or closed to this account).</summary>
    public static int? PipeServerProcessId(string pipeName)
    {
        using var handle = DataDirectoryFileSystem.OpenForQuery(@"\\.\pipe\" + pipeName);
        if (handle is null)
        {
            return null;
        }
        return GetNamedPipeServerProcessId(handle, out var pid) ? (int)pid : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafeFileHandle pipe, out uint serverProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(IntPtr service, [Out] byte[]? config, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2W(IntPtr service, uint infoLevel, [Out] byte[] buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, byte[] info);

    public static int? ServiceState(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, 0x0001);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        try
        {
            var service = OpenServiceW(scm, serviceName, 0x0004);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastPInvokeError();
                return error == 1060 ? null : throw new Win32Exception(error); // ERROR_SERVICE_DOES_NOT_EXIST
            }
            try
            {
                var buffer = new byte[36]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(service, 0, buffer, (uint)buffer.Length, out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public const string BackupPrivilege = "SeBackupPrivilege";
    public const string RestorePrivilege = "SeRestorePrivilege";
    public const string TakeOwnershipPrivilege = "SeTakeOwnershipPrivilege";

    /// <summary>check: reading only.</summary>
    public static readonly IReadOnlyList<string> ReadPrivileges = [BackupPrivilege];

    /// <summary>repair, continue, restore-acl: read objects that shut out Administrators, put back original owners.</summary>
    public static readonly IReadOnlyList<string> RepairPrivileges = [BackupPrivilege, RestorePrivilege, TakeOwnershipPrivilege];

    /// <summary>Enables the privileges for the lifetime of the scope; Dispose puts each back as it was.</summary>
    public static PrivilegeScope EnablePrivileges(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var scope = new PrivilegeScope();
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
        {
            scope.MissingList.AddRange(names);
            return scope;
        }
        scope.Token = token;
        foreach (var name in names)
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
            {
                scope.MissingList.Add(name);
                continue;
            }
            var previous = new byte[16];
            if (!AdjustTokenPrivileges(token, false, Privilege(luid, 2), 16, previous, 16, out _) || Marshal.GetLastPInvokeError() == 1300) // SE_PRIVILEGE_ENABLED; ERROR_NOT_ALL_ASSIGNED
            {
                scope.MissingList.Add(name);
                continue;
            }
            // PreviousState lists the privilege only if its state changed: then it was disabled before.
            if (BinaryPrimitives.ReadInt32LittleEndian(previous) == 1)
            {
                scope.Restore.Add((luid, BinaryPrimitives.ReadInt32LittleEndian(previous.AsSpan(12))));
            }
        }
        return scope;
    }

    private static byte[] Privilege(long luid, int attributes)
    {
        // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES: count, LUID (8), attributes.
        var privileges = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(privileges, 1);
        BinaryPrimitives.WriteInt64LittleEndian(privileges.AsSpan(4), luid);
        BinaryPrimitives.WriteInt32LittleEndian(privileges.AsSpan(12), attributes);
        return privileges;
    }

    public sealed class PrivilegeScope : IDisposable
    {
        internal IntPtr Token { get; set; }
        internal List<string> MissingList { get; } = [];
        internal List<(long Luid, int Attributes)> Restore { get; } = [];

        public IReadOnlyList<string> Missing => MissingList;

        public void Dispose()
        {
            if (Token == IntPtr.Zero)
            {
                return;
            }
            foreach (var (luid, attributes) in Restore)
            {
                AdjustTokenPrivileges(Token, false, Privilege(luid, attributes), 16, null, 0, out _);
            }
            CloseHandle(Token);
            Token = IntPtr.Zero;
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, int infoLevel, [Out] byte[] buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, byte[] newState, uint length, [Out] byte[]? previousState, uint previousLength, out uint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
