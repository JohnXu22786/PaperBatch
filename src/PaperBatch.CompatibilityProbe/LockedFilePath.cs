using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaperBatch.CompatibilityProbe;

internal static class LockedFilePath
{
    private const int InitialPathCapacity = 512;
    private const int LinuxAtEmptyPath = 0x1000;
    private const int LinuxOpenPath = 0x200000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int LinuxNoSuchFileOrDirectory = 2;
    private const int LinuxNotADirectory = 20;
    private const int LinuxOpenReadOnly = 0;
    internal const uint WindowsFileTypeDisk = 1;
    private const uint LinuxStatxBasicStats = 0x07ff;
    private const ushort LinuxRegularFileType = 0x8000;
    private const ushort LinuxFileTypeMask = 0xF000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(16)]
        internal uint LinkCount;

        [FieldOffset(28)]
        internal ushort Mode;

        [FieldOffset(32)]
        internal ulong Inode;

        [FieldOffset(136)]
        internal uint DeviceMajor;

        [FieldOffset(140)]
        internal uint DeviceMinor;
    }

    internal static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0;
        }

        if (!OperatingSystem.IsLinux())
        {
            _ = File.GetAttributes(path);
            throw new IOException("Safe regular-file inspection is supported only on Windows and Linux.");
        }

        using var pathHandle = OpenLinuxPathHandle(path);
        return IsRegularFile(pathHandle);
    }

    internal static string GetFileIdentity(SafeFileHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid)
        {
            throw new IOException("The opened file handle is unavailable.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information)
                || !IsWindowsRegularFileType(GetFileType(handle), (FileAttributes)information.FileAttributes))
            {
                throw new IOException("The opened Windows handle is not a regular file or its identity could not be inspected.");
            }

            if (information.FileIndexHigh == 0 && information.FileIndexLow == 0)
            {
                throw new IOException("The opened Windows file has no stable file index for identity comparison.");
            }

            return $"windows:{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
        }

        if (OperatingSystem.IsLinux())
        {
            var metadata = ReadLinuxStatx(handle);
            if ((metadata.Mode & LinuxFileTypeMask) != LinuxRegularFileType)
            {
                throw new IOException("The opened Linux candidate is not a regular file.");
            }

            if (metadata.Inode == 0)
            {
                throw new IOException("The opened Linux file has no stable inode for identity comparison.");
            }

            return $"linux:{metadata.DeviceMajor:X8}:{metadata.DeviceMinor:X8}:{metadata.Inode:X16}";
        }

        throw new PlatformNotSupportedException("Stable file identity inspection is supported only on Windows and Linux.");
    }

    internal static FileStream OpenReadOnlyRegularFile(string path, FileShare share)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsFile = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            try
            {
                if (!GetFileInformationByHandle(windowsFile.SafeFileHandle, out var information)
                    || !IsWindowsRegularFileType(GetFileType(windowsFile.SafeFileHandle), (FileAttributes)information.FileAttributes))
                {
                    throw new IOException("The opened candidate is not a regular disk file.");
                }

                return windowsFile;
            }
            catch
            {
                windowsFile.Dispose();
                throw;
            }
        }

        if (!OperatingSystem.IsLinux())
        {
            _ = File.GetAttributes(path);
            throw new IOException("Safe regular-file inspection is supported only on Windows and Linux.");
        }

        using var pathHandle = OpenLinuxPathHandle(path);
        if (!IsRegularFile(pathHandle))
        {
            throw new IOException("The executable candidate is not a regular file.");
        }

        var descriptorPath = $"/proc/self/fd/{pathHandle.DangerousGetHandle().ToInt64()}";
        var descriptor = Open(descriptorPath, LinuxOpenReadOnly | LinuxOpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the regular executable candidate for reading.");
        }

        var file = new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.Read);
        try
        {
            if (!IsRegularFile(file.SafeFileHandle))
            {
                throw new IOException("The opened executable candidate is not a regular file.");
            }

            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static string Resolve(string originalPath, SafeFileHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid)
        {
            throw new IOException("The opened file handle is unavailable.");
        }

        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsPath(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
            if (File.Exists(descriptorPath))
            {
                return descriptorPath;
            }
        }

        return Path.GetFullPath(originalPath);
    }

    internal static string ResolveTargetPath(string originalPath, SafeFileHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid)
        {
            throw new IOException("The opened file handle is unavailable.");
        }

        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsPath(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            return ResolveLinuxPath(handle);
        }

        return Path.GetFullPath(originalPath);
    }

    internal static SafeFileHandle OpenLinuxDirectoryForOpenedFile(string openedFilePath, SafeFileHandle openedFile)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Pinned runtime-directory handles are supported only on Linux.");
        }

        var directoryPath = Path.GetDirectoryName(openedFilePath);
        var fileName = Path.GetFileName(openedFilePath);
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(fileName))
        {
            throw new IOException("Could not resolve the opened engine's runtime directory.");
        }

        var directoryHandle = OpenLinuxDirectoryHandle(directoryPath);
        try
        {
            if (ReadLinuxStatx(openedFile).LinkCount != 1)
            {
                throw new IOException("The selected engine has multiple directory entries, so its sibling runtime files are ambiguous.");
            }

            var targetPath = Path.Combine(LinuxDescriptorPath(directoryHandle), fileName);
            using var candidateHandle = OpenLinuxPathHandle(targetPath, noFollow: true);
            if (ReadLinuxStatx(candidateHandle).LinkCount != 1)
            {
                throw new IOException("The selected runtime directory contains an engine hard link with ambiguous sibling runtime files.");
            }

            if (!SameLinuxFile(openedFile, candidateHandle))
            {
                throw new IOException("The opened engine no longer resides in the runtime directory selected for launch.");
            }

            return directoryHandle;
        }
        catch
        {
            directoryHandle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenLinuxDirectoryHandle(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Pinned runtime-directory handles are supported only on Linux.");
        }

        var descriptor = Open(path, LinuxOpenReadOnly | LinuxOpenDirectory | LinuxOpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the LibreOffice runtime directory for a stable launch path.");
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    internal static string LinuxDescriptorPath(SafeFileHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid)
        {
            throw new IOException("The opened Linux descriptor is unavailable.");
        }

        return $"/proc/{Environment.ProcessId}/fd/{handle.DangerousGetHandle().ToInt64()}";
    }

    private static string ResolveWindowsPath(SafeFileHandle handle)
    {
        var capacity = InitialPathCapacity;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the opened file handle to a stable Windows path.");
            }
            if (length < capacity)
            {
                return buffer.ToString();
            }

            capacity = checked((int)length + 1);
        }

        throw new PathTooLongException("The resolved Windows file path exceeds the supported buffer limit.");
    }

    private static string ResolveLinuxPath(SafeFileHandle handle)
    {
        var descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
        var capacity = InitialPathCapacity;
        while (capacity <= 32768)
        {
            var buffer = new byte[capacity];
            var length = ReadLink(descriptorPath, buffer, (nuint)buffer.Length);
            if (length < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resolve the opened Linux file handle to its target path.");
            }

            if (length < buffer.Length)
            {
                string targetPath;
                try
                {
                    targetPath = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                        .GetString(buffer, 0, checked((int)length));
                }
                catch (DecoderFallbackException exception)
                {
                    throw new IOException("The opened Linux file path is not valid UTF-8.", exception);
                }

                if (targetPath.EndsWith(" (deleted)", StringComparison.Ordinal))
                {
                    throw new IOException("The opened Linux file no longer has an installed path for resolving sibling runtime files.");
                }

                return Path.GetFullPath(targetPath);
            }

            capacity = checked(capacity * 2);
        }

        throw new PathTooLongException("The resolved Linux file path exceeds the supported buffer limit.");
    }

    private static SafeFileHandle OpenLinuxPathHandle(string path, bool noFollow = false)
    {
        var flags = LinuxOpenPath | LinuxOpenCloseOnExec | (noFollow ? LinuxOpenNoFollow : 0);
        var descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == LinuxNoSuchFileOrDirectory)
            {
                throw new FileNotFoundException("The executable candidate does not exist.", path);
            }

            if (error == LinuxNotADirectory)
            {
                throw new DirectoryNotFoundException($"A directory in the executable candidate path does not exist: {path}");
            }

            throw new Win32Exception(error, "Could not open the executable candidate for safe type inspection.");
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static bool IsRegularFile(SafeFileHandle handle)
    {
        var metadata = ReadLinuxStatx(handle);
        return (metadata.Mode & LinuxFileTypeMask) == LinuxRegularFileType;
    }

    internal static bool IsWindowsRegularFileType(uint fileType, FileAttributes attributes) =>
        fileType == WindowsFileTypeDisk
        && (attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0;

    private static bool SameLinuxFile(SafeFileHandle first, SafeFileHandle second)
    {
        var firstMetadata = ReadLinuxStatx(first);
        var secondMetadata = ReadLinuxStatx(second);
        return firstMetadata.Inode == secondMetadata.Inode
            && firstMetadata.DeviceMajor == secondMetadata.DeviceMajor
            && firstMetadata.DeviceMinor == secondMetadata.DeviceMinor;
    }

    private static LinuxStatx ReadLinuxStatx(SafeFileHandle handle)
    {
        var descriptor = checked((int)handle.DangerousGetHandle().ToInt64());
        if (Statx(
                descriptor,
                string.Empty,
                LinuxAtEmptyPath,
                LinuxStatxBasicStats,
                out var metadata) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not inspect opened file metadata.");
        }

        return metadata;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsByHandleFileInformation information);

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern nint ReadLink(string path, byte[] buffer, nuint bufferSize);

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(int directoryDescriptor, string path, int flags, uint mask, out LinuxStatx metadata);
}
