using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Watch;

public interface IWatchFolderFileAccess
{
    string GetRootIdentity(string rootPath);
    Stream OpenRead(string rootPath, string expectedRootIdentity, string relativePath);
    SourceFileIdentity GetOpenedIdentity(Stream stream);
}

/// <summary>Opens paths relative to a pinned root without following links in any path component.</summary>
public sealed class PhysicalWatchFolderFileAccess : IWatchFolderFileAccess
{
    private const long LinuxOpenAt2Syscall = 437;
    private const ulong LinuxResolveNoMagicLinks = 0x02;
    private const ulong LinuxResolveNoSymlinks = 0x04;
    private const ulong LinuxResolveBeneath = 0x08;
    private readonly SourceSnapshotCache.PhysicalSourceFileAccess _identities = new();

    public SourceFileIdentity GetOpenedIdentity(Stream stream) => _identities.GetOpenedFileIdentity(stream);

    public string GetRootIdentity(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (OperatingSystem.IsLinux())
        {
            EnsureSupportedLinuxArchitecture();
            using var root = OpenLinuxRoot(rootPath);
            if (FStat(root.DangerousGetHandle().ToInt32(), out var information) != 0) throw NativeOpenError("Could not inspect the watch root");
            if ((information.Mode & 0xf000) != 0x4000) throw new IOException("Watch root is not a directory.");
            return $"{information.Device:x16}:{information.Inode:x16}";
        }
        if (OperatingSystem.IsWindows())
        {
            using var root = OpenWindowsRoot(rootPath);
            var info = GetInformation(root);
            return $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        }
        throw new NotSupportedException("Safe root identity checks are supported only on Linux x64 and Windows.");
    }

    public Stream OpenRead(string rootPath, string expectedRootIdentity, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRootIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var components = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(component => component is "." or ".."))
            throw new IOException("Watch source path is outside its configured root.");
        if (OperatingSystem.IsLinux()) return OpenLinux(rootPath, expectedRootIdentity, components);
        if (OperatingSystem.IsWindows()) return OpenWindows(rootPath, expectedRootIdentity, components);
        throw new NotSupportedException("Safe root-relative source opens are supported only on Linux and Windows.");
    }

    private static Stream OpenLinux(string rootPath, string expectedRootIdentity, string[] components)
    {
        using var root = OpenLinuxRoot(rootPath);
        if (FStat(root.DangerousGetHandle().ToInt32(), out var rootInfo) != 0) throw NativeOpenError("Could not inspect the watch root");
        if ((rootInfo.Mode & 0xf000) != 0x4000 || $"{rootInfo.Device:x16}:{rootInfo.Inode:x16}" != expectedRootIdentity)
            throw new IOException("Watch root was replaced after the scanner was created.");
        var relative = string.Join('/', components);
        var how = new OpenHow { Flags = 0x00080000 | 0x00000800, Resolve = LinuxResolveBeneath | LinuxResolveNoSymlinks | LinuxResolveNoMagicLinks };
        using var relativePath = new Utf8String(relative);
        var fileDescriptor = OpenAt2(LinuxOpenAt2Syscall, root.DangerousGetHandle().ToInt32(), relativePath.Pointer, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fileDescriptor < 0) throw NativeOpenError("Could not safely open a watch source");
        var openedFile = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
        try
        {
            if (FStat(fileDescriptor, out var fileInfo) != 0) throw NativeOpenError("Could not inspect an opened watch source");
            if ((fileInfo.Mode & 0xf000) != 0x8000) throw new IOException("Only regular files can be opened as watch sources.");
            return new FileStream(openedFile, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            openedFile.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenLinuxRoot(string rootPath)
    {
        EnsureSupportedLinuxArchitecture();
        var rootHow = new OpenHow { Flags = 0x00080000 | 0x00010000 | 0x00020000, Resolve = LinuxResolveNoSymlinks | LinuxResolveNoMagicLinks };
        using (var path = new Utf8String(Path.GetFullPath(rootPath)))
        {
            var descriptor = OpenAt2(LinuxOpenAt2Syscall, -100, path.Pointer, ref rootHow, (nuint)Marshal.SizeOf<OpenHow>());
            if (descriptor < 0) throw NativeOpenError("Could not safely open the watch root");
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
    }

    private static Stream OpenWindows(string rootPath, string expectedRootIdentity, string[] components)
    {
        using var root = OpenWindowsRoot(rootPath);
        var rootInfo = GetInformation(root);
        var actualIdentity = $"{rootInfo.VolumeSerialNumber:x8}:{rootInfo.FileIndexHigh:x8}{rootInfo.FileIndexLow:x8}";
        if (!string.Equals(actualIdentity, expectedRootIdentity, StringComparison.Ordinal)) throw new IOException("Watch root was replaced after the scanner was created.");

        SafeFileHandle parent = root;
        var ownsParent = false;
        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                var final = index == components.Length - 1;
                var child = OpenRelativeWindows(parent, components[index], directory: !final);
                if (ownsParent) parent.Dispose();
                parent = child;
                ownsParent = true;
                var info = GetInformation(parent);
                if ((info.Attributes & 0x00000400) != 0 || (final && (info.Attributes & (0x00000010 | 0x00000040)) != 0) ||
                    (!final && (info.Attributes & 0x00000010) == 0))
                    throw new IOException("Watch source path contains a link or special file.");
            }

            var file = parent;
            ownsParent = false;
            return new FileStream(file, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        finally
        {
            if (ownsParent) parent.Dispose();
        }
    }

    private static SafeFileHandle OpenWindowsRoot(string rootPath)
    {
        var root = CreateFileW(Path.GetFullPath(rootPath), 0x00000001 | 0x00000020 | 0x00000080, 0x00000007, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (root.IsInvalid) { root.Dispose(); throw NativeOpenError("Could not open the watch root"); }
        var info = GetInformation(root);
        if ((info.Attributes & 0x00000010) == 0 || (info.Attributes & 0x00000400) != 0)
        { root.Dispose(); throw new IOException("Watch root is not a regular directory path."); }
        var actualRoot = NormalizeWindowsPath(FinalPath(root));
        var requestedRoot = NormalizeWindowsPath(Path.GetFullPath(rootPath));
        if (!string.Equals(actualRoot, requestedRoot, StringComparison.OrdinalIgnoreCase))
        { root.Dispose(); throw new IOException("Watch root resolved through a linked path."); }
        return root;
    }

    private static void EnsureSupportedLinuxArchitecture()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NotSupportedException($"Linux watch-folder access currently supports x64 only; {RuntimeInformation.ProcessArchitecture} is not supported.");
    }

    private static SafeFileHandle OpenRelativeWindows(SafeFileHandle parent, string name, bool directory)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString
        {
            Length = checked((ushort)(name.Length * sizeof(char))),
            MaximumLength = checked((ushort)(name.Length * sizeof(char))),
            Buffer = nameBuffer
        };
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        Marshal.StructureToPtr(unicode, unicodeBuffer, false);
        try
        {
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeBuffer,
                Attributes = 0x40
            };
            var status = NtCreateFile(out var handle,
                directory ? 0x00000001 | 0x00000020 | 0x00000080 | 0x00100000 : 0x80000000 | 0x00000080 | 0x00100000,
                ref attributes,
                out _,
                IntPtr.Zero,
                0,
                0x00000007,
                1,
                (uint)(0x00200000 | 0x00000020 | (directory ? 0x00000001 : 0x00000040)),
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                throw new IOException($"Could not safely open a watch path component (NTSTATUS 0x{status:x8}).");
            }
            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeOpenError("Could not inspect an opened watch path component");
        return info;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw NativeOpenError("Could not resolve the opened watch root");
        return new string(buffer, 0, (int)length);
    }

    private static string NormalizeWindowsPath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) path = "\\\\" + path[8..];
        else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static IOException NativeOpenError(string message) => new($"{message} (native error {Marshal.GetLastPInvokeError()}).", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation { public uint Attributes; public FileTime CreationTime; public FileTime LastAccessTime; public FileTime LastWriteTime; public uint VolumeSerialNumber; public uint SizeHigh; public uint SizeLow; public uint NumberOfLinks; public uint FileIndexHigh; public uint FileIndexLow; }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern int OpenAt2(long syscallNumber, int dirfd, IntPtr path, ref OpenHow how, nuint size);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, out SourceSnapshotCache.PhysicalSourceFileAccess.LinuxStat information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint pathLength, uint flags);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    private sealed class Utf8String : IDisposable
    {
        public Utf8String(string value) => Pointer = Marshal.StringToCoTaskMemUTF8(value);
        public IntPtr Pointer { get; }
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
    }
}
