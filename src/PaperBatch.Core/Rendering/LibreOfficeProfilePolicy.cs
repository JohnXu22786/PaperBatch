using PaperBatch.Core.Intake;
using System.Runtime.Versioning;

namespace PaperBatch.Core.Rendering;

/// <summary>Creates a private-profile child in an already private, operation-owned directory.</summary>
public static class LibreOfficeProfilePolicy
{
    public static Uri CreatePrivateProfile(string operationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDirectory);
        var operationPath = Path.GetFullPath(operationDirectory);
        if (!Directory.Exists(operationPath))
        {
            throw new DirectoryNotFoundException("The renderer operation directory must already exist.");
        }

        var attributes = File.GetAttributes(operationPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The renderer operation directory cannot be a reparse point.");
        }

        if (OperatingSystem.IsWindows())
        {
            SourceSnapshotCache.ApplyPrivateWindowsPermissions(operationPath);
        }
        else
        {
            EnsureNoReplaceableAncestors(operationPath);
            const UnixFileMode sharedAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(operationPath) & sharedAccess) != 0)
            {
                throw new IOException("The renderer operation directory must be accessible only to its owner.");
            }
        }

        var profilePath = Path.Combine(operationPath, "libreoffice-profile");
        if (Directory.Exists(profilePath) || File.Exists(profilePath))
        {
            throw new IOException("The operation already contains a LibreOffice profile.");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(profilePath);
        }
        else
        {
            Directory.CreateDirectory(profilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if ((File.GetAttributes(profilePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The LibreOffice profile cannot be a reparse point.");
        }

        if (OperatingSystem.IsWindows())
        {
            SourceSnapshotCache.ApplyPrivateWindowsPermissions(profilePath);
        }

        var settingsPath = Path.Combine(profilePath, "registrymodifications.xcu");
        var settings = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var output = new FileStream(settingsPath, settings))
        using (var writer = new StreamWriter(output))
        {
            writer.Write(PrivateSettingsXml);
        }

        return new Uri(profilePath + Path.DirectorySeparatorChar);
    }

    [UnsupportedOSPlatform("windows")]
    private static void EnsureNoReplaceableAncestors(string operationPath)
    {
        for (var current = new DirectoryInfo(operationPath); current is not null; current = current.Parent)
        {
            var mode = File.GetUnixFileMode(current.FullName);
            var sharedWrite = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
            if (sharedWrite && (mode & UnixFileMode.StickyBit) == 0)
            {
                throw new IOException("The renderer operation path traverses a shared writable directory.");
            }
        }
    }

    private static string PrivateSettingsXml => """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:install="http://openoffice.org/2004/installation" xmlns:xml="http://www.w3.org/XML/1998/namespace">
          <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
          <item oor:path="/org.openoffice.Office.Common/Update/Links"><prop oor:name="UpdateMode" oor:op="fuse"><value>0</value></prop></item>
        </oor:items>
        """;
}
