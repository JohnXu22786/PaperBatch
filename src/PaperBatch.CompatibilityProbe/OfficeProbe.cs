using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using System.ComponentModel;

namespace PaperBatch.CompatibilityProbe;

public sealed record OfficeSuiteMeasurement(
    string Suite,
    string Component,
    string RequestedExecutablePath,
    string? ResolvedExecutablePath,
    ProbeEvidence Version,
    ProbeEvidence Bitness,
    ProbeEvidence BinarySha256);

public sealed record OfficeProbeResult(
    ProbeEvidence InstalledSuites,
    OfficeExportEvidence Exports,
    OfficeCoexistenceEvidence Coexistence);

public sealed record OfficeExportEvidence(
    ProbeEvidence MicrosoftOffice,
    ProbeEvidence WpsOffice,
    ProbeEvidence LibreOffice);

public sealed record OfficeCoexistenceEvidence(
    ProbeEvidence MicrosoftOffice,
    ProbeEvidence WpsOffice,
    ProbeEvidence LibreOffice);

internal sealed record LibreOfficeVersionEvidenceValue(
    string Version,
    string EngineSha256,
    string ExpectedSha256,
    string RuntimeComponentsSha256);

internal readonly record struct BoundedVersionProcessResult(
    bool Exited,
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed record OfficeBinaryInspection(string Sha256, string? Architecture, long SizeBytes);

public static class OfficeProbe
{
    internal const int MaximumVersionOutputCharacters = 4096;

    private static readonly string[] ExecutableNames =
    [
        "soffice",
        "soffice.exe",
        "libreoffice",
        "libreoffice.exe",
        "WINWORD.EXE",
        "EXCEL.EXE",
        "POWERPNT.EXE",
        "wps.exe",
        "et.exe",
        "wpp.exe"
    ];

    public static OfficeProbeResult Collect(
        string? expectedLibreOfficeEngineSha256 = null,
        string? libreOfficeEnginePath = null)
        => Collect(
            expectedLibreOfficeEngineSha256,
            libreOfficeEnginePath,
            PinnedEngineSnapshot.DefaultMaximumSnapshotBytes);

    internal static OfficeProbeResult Collect(
        string? expectedLibreOfficeEngineSha256,
        string? libreOfficeEnginePath,
        long maximumBinaryBytes)
    {
        if (maximumBinaryBytes < 0
            || maximumBinaryBytes > PinnedEngineSnapshot.DefaultMaximumSnapshotBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBinaryBytes),
                $"Binary inspection limits must be between 0 and {PinnedEngineSnapshot.DefaultMaximumSnapshotBytes} bytes.");
        }

        var candidates = DiscoverExecutables(out var discoveryBlocked);
        var libreOfficePathInspectionBlocked = false;
        if (!string.IsNullOrWhiteSpace(libreOfficeEnginePath))
        {
            candidates.RemoveAll(item => item.Suite == "LibreOffice");
            try
            {
                var fullPath = Path.GetFullPath(libreOfficeEnginePath);
                if (!LockedFilePath.IsRegularFile(fullPath))
                {
                    libreOfficePathInspectionBlocked = true;
                }
                else
                {
                    candidates.Add(new OfficeExecutable(
                        "LibreOffice",
                        "Direct engine binary",
                        fullPath,
                        IsEngineBinary: true));
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            {
                libreOfficePathInspectionBlocked = true;
            }
        }

        ProbeEvidence suitesEvidence;
        if (candidates.Count == 0)
        {
            if (discoveryBlocked || libreOfficePathInspectionBlocked)
            {
                suitesEvidence = ProbeEvidence.Blocked(
                    EvidenceSource.Environment,
                    "PATH and supported platform installation-location inspection",
                    "Access or I/O errors prevented complete inspection of supported installation locations.");
            }
            else
            {
                suitesEvidence = ProbeEvidence.Unavailable(
                    EvidenceSource.Environment,
                    "PATH and supported platform installation-location inspection",
                    "No supported Office, WPS, or LibreOffice executable was found in the inspected locations.");
            }

            return new OfficeProbeResult(
                suitesEvidence,
                new OfficeExportEvidence(
                    ExportEvidence("Microsoft Office", available: false, "Run a scoped fixture on a licensed Windows Office image.", discoveryBlocked: discoveryBlocked),
                    ExportEvidence("WPS Office", available: false, "Run a scoped fixture on a licensed Windows WPS image.", discoveryBlocked: discoveryBlocked),
                    ExportEvidence("LibreOffice", available: false, "Run tools/probe_libreoffice_export.py; version detection does not export a document.", discoveryBlocked: discoveryBlocked || libreOfficePathInspectionBlocked)),
                CoexistenceEvidence(
                    microsoftOfficeInstalled: false,
                    wpsOfficeInstalled: false,
                    libreOfficeInstalled: false,
                    microsoftOfficeDiscoveryBlocked: discoveryBlocked,
                    wpsOfficeDiscoveryBlocked: discoveryBlocked,
                    libreOfficeDiscoveryBlocked: discoveryBlocked || libreOfficePathInspectionBlocked));
        }

        var measurements = candidates
            .Select(item => Measure(item, expectedLibreOfficeEngineSha256, maximumBinaryBytes))
            .ToArray();
        var hasMicrosoftOffice = measurements.Any(item => item.Suite == "Microsoft Office");
        var hasWpsOffice = measurements.Any(item => item.Suite == "WPS Office");
        var hasLibreOfficeCandidate = candidates.Any(item => item.Suite == "LibreOffice");
        var hasLibreOffice = measurements.Any(item => item.Suite == "LibreOffice" && item.Version.Status == EvidenceStatus.Verified);
        var microsoftOfficeCandidateBlocked = candidates.Any(item => item.Suite == "Microsoft Office") && !hasMicrosoftOffice;
        var wpsOfficeCandidateBlocked = candidates.Any(item => item.Suite == "WPS Office") && !hasWpsOffice;
        var libreOfficeCandidateBlocked = hasLibreOfficeCandidate && !hasLibreOffice;
        var inventoryMethod = "read-only executable-candidate inventory; suite candidates require matching product identity";
        suitesEvidence = discoveryBlocked || libreOfficePathInspectionBlocked
            ? new ProbeEvidence(
                EvidenceStatus.Blocked,
                EvidenceSource.Environment,
                inventoryMethod,
                measurements,
                "Access or I/O errors prevented complete inspection of supported installation locations.")
            : ProbeEvidence.Verified(EvidenceSource.Environment, inventoryMethod, measurements);

        return new OfficeProbeResult(
            suitesEvidence,
            new OfficeExportEvidence(
                ExportEvidence("Microsoft Office", hasMicrosoftOffice, "Run a scoped fixture on a licensed Windows Office image.", microsoftOfficeCandidateBlocked, discoveryBlocked && !candidates.Any(item => item.Suite == "Microsoft Office")),
                ExportEvidence("WPS Office", hasWpsOffice, "Run a scoped fixture on a licensed Windows WPS image.", wpsOfficeCandidateBlocked, discoveryBlocked && !candidates.Any(item => item.Suite == "WPS Office")),
                ExportEvidence("LibreOffice", hasLibreOffice, "Run tools/probe_libreoffice_export.py; version detection does not export a document.", libreOfficeCandidateBlocked, (discoveryBlocked && !hasLibreOfficeCandidate) || libreOfficePathInspectionBlocked)),
            CoexistenceEvidence(
                hasMicrosoftOffice,
                hasWpsOffice,
                hasLibreOffice,
                libreOfficeCandidateBlocked,
                microsoftOfficeCandidateBlocked,
                wpsOfficeCandidateBlocked,
                discoveryBlocked && !candidates.Any(item => item.Suite == "Microsoft Office"),
                discoveryBlocked && !candidates.Any(item => item.Suite == "WPS Office"),
                (discoveryBlocked && !hasLibreOfficeCandidate) || libreOfficePathInspectionBlocked));
    }

    private static ProbeEvidence ExportEvidence(
        string suite,
        bool available,
        string nextStep,
        bool candidateBlocked = false,
        bool discoveryBlocked = false) =>
        available
            ? ProbeEvidence.NotRun(EvidenceSource.Native, $"{suite} document export", nextStep)
            : candidateBlocked
                ? ProbeEvidence.Blocked(EvidenceSource.Native, $"{suite} document export", "An executable candidate was found but did not identify as the required suite.")
                : discoveryBlocked
                    ? ProbeEvidence.Blocked(EvidenceSource.Native, $"{suite} document export", "Access or I/O errors prevented confirming that this suite is absent.")
                    : ProbeEvidence.Unavailable(EvidenceSource.Native, $"{suite} document export", "No executable for this suite was found in the inspected locations.");

    private static OfficeCoexistenceEvidence CoexistenceEvidence(
        bool microsoftOfficeInstalled,
        bool wpsOfficeInstalled,
        bool libreOfficeInstalled,
        bool libreOfficeCandidateBlocked = false,
        bool microsoftOfficeCandidateBlocked = false,
        bool wpsOfficeCandidateBlocked = false,
        bool microsoftOfficeDiscoveryBlocked = false,
        bool wpsOfficeDiscoveryBlocked = false,
        bool libreOfficeDiscoveryBlocked = false) =>
        new(
            CoexistenceEvidence("Microsoft Office", microsoftOfficeInstalled, "The collector does not attach to or terminate a user's Office process.", candidateBlocked: microsoftOfficeCandidateBlocked, discoveryBlocked: microsoftOfficeDiscoveryBlocked),
            CoexistenceEvidence("WPS Office", wpsOfficeInstalled, "The collector does not attach to or terminate a user's WPS process.", candidateBlocked: wpsOfficeCandidateBlocked, discoveryBlocked: wpsOfficeDiscoveryBlocked),
            CoexistenceEvidence("LibreOffice", libreOfficeInstalled, "The isolated version check is not a coexistence test.", libreOfficeCandidateBlocked, libreOfficeDiscoveryBlocked));

    private static ProbeEvidence CoexistenceEvidence(
        string suite,
        bool suiteInstalled,
        string safetyNote,
        bool candidateBlocked = false,
        bool discoveryBlocked = false) =>
        suiteInstalled
            ? ProbeEvidence.NotRun(EvidenceSource.Native, $"{suite} coexistence", $"No controlled coexistence test session was supplied; {safetyNote}")
            : candidateBlocked
                ? ProbeEvidence.Blocked(EvidenceSource.Native, $"{suite} coexistence", "An executable candidate was found but did not identify as the required suite.")
                : discoveryBlocked
                    ? ProbeEvidence.Blocked(EvidenceSource.Native, $"{suite} coexistence", "Access or I/O errors prevented confirming that this suite is absent.")
                    : ProbeEvidence.Unavailable(EvidenceSource.Native, $"{suite} coexistence", $"No {suite} executable was found in the inspected locations.");

    private static List<OfficeExecutable> DiscoverExecutables(out bool inspectionBlocked)
    {
        inspectionBlocked = false;
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var found = new Dictionary<string, OfficeExecutable>(pathComparer);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in ExecutableNames)
            {
                AddCandidate(found, Path.Combine(directory.Trim(), name), ref inspectionBlocked);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            AddStandardWindowsPaths(found, ref inspectionBlocked);
            AddWindowsUninstallLocations(found, ref inspectionBlocked);
        }

        return found.Values.OrderBy(item => item.Suite, StringComparer.Ordinal).ThenBy(item => item.Component, StringComparer.Ordinal).ToList();
    }

    private static void AddStandardWindowsPaths(IDictionary<string, OfficeExecutable> found, ref bool inspectionBlocked)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var officeFolder in new[] { "Microsoft Office\\root\\Office16", "Microsoft Office\\Office16" })
            {
                AddCandidate(found, Path.Combine(root, officeFolder, "WINWORD.EXE"), ref inspectionBlocked);
                AddCandidate(found, Path.Combine(root, officeFolder, "EXCEL.EXE"), ref inspectionBlocked);
                AddCandidate(found, Path.Combine(root, officeFolder, "POWERPNT.EXE"), ref inspectionBlocked);
            }

            AddCandidate(found, Path.Combine(root, "LibreOffice", "program", "soffice.exe"), ref inspectionBlocked);
            AddCandidate(found, Path.Combine(root, "Kingsoft", "WPS Office", "office6", "wps.exe"), ref inspectionBlocked);
            AddCandidate(found, Path.Combine(root, "Kingsoft", "WPS Office", "office6", "et.exe"), ref inspectionBlocked);
            AddCandidate(found, Path.Combine(root, "Kingsoft", "WPS Office", "office6", "wpp.exe"), ref inspectionBlocked);
            AddWpsVersionDirectories(found, Path.Combine(root, "Kingsoft", "WPS Office"), ref inspectionBlocked);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsUninstallLocations(IDictionary<string, OfficeExecutable> found, ref bool inspectionBlocked)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", writable: false);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subkeyName in uninstall.GetSubKeyNames())
                {
                    using var subkey = uninstall.OpenSubKey(subkeyName, writable: false);
                    var displayName = subkey?.GetValue("DisplayName") as string;
                    var installLocation = subkey?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
                    {
                        continue;
                    }

                    var executableNames = displayName.Contains("WPS", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "wps.exe", "et.exe", "wpp.exe" }
                        : displayName.Contains("LibreOffice", StringComparison.OrdinalIgnoreCase)
                            ? new[] { "soffice.exe", "libreoffice.exe" }
                            : displayName.Contains("Office", StringComparison.OrdinalIgnoreCase)
                                ? new[] { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE" }
                                : Array.Empty<string>();
                    if (executableNames.Length > 0)
                    {
                        foreach (var name in executableNames)
                        {
                            AddCandidate(found, Path.Combine(installLocation, name), ref inspectionBlocked);
                            AddCandidate(found, Path.Combine(installLocation, "program", name), ref inspectionBlocked);
                            AddCandidate(found, Path.Combine(installLocation, "office6", name), ref inspectionBlocked);
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                inspectionBlocked = true;
            }
            catch (System.Security.SecurityException)
            {
                inspectionBlocked = true;
            }
            catch (IOException)
            {
                inspectionBlocked = true;
            }
        }
    }

    private static void AddWpsVersionDirectories(IDictionary<string, OfficeExecutable> found, string root, ref bool inspectionBlocked)
    {
        try
        {
            var attributes = File.GetAttributes(root);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(root))
            {
                foreach (var name in new[] { "wps.exe", "et.exe", "wpp.exe" })
                {
                    AddCandidate(found, Path.Combine(versionDirectory, "office6", name), ref inspectionBlocked);
                }
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            inspectionBlocked = true;
        }
        catch (IOException)
        {
            inspectionBlocked = true;
        }
        catch (System.Security.SecurityException)
        {
            inspectionBlocked = true;
        }
    }

    private static void AddCandidate(IDictionary<string, OfficeExecutable> found, string path, ref bool inspectionBlocked)
    {
        try
        {
            if (!LockedFilePath.IsRegularFile(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            var executable = DescribeExecutable(fullPath);
            found.TryAdd(fullPath, executable);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            inspectionBlocked = true;
        }
    }

    private static OfficeExecutable DescribeExecutable(string path)
    {
        var name = Path.GetFileName(path);
        var lower = name.ToLowerInvariant();
        if (lower is "winword.exe" or "excel.exe" or "powerpnt.exe")
        {
            return new OfficeExecutable("Microsoft Office", Path.GetFileNameWithoutExtension(name), path);
        }

        if (lower is "wps.exe" or "et.exe" or "wpp.exe")
        {
            var component = lower switch { "wps.exe" => "Writer", "et.exe" => "Spreadsheets", _ => "Presentations" };
            return new OfficeExecutable("WPS Office", component, path);
        }

        return new OfficeExecutable("LibreOffice", "Writer/Calc/Impress suite", path);
    }

    private static OfficeSuiteMeasurement Measure(
        OfficeExecutable executable,
        string? expectedLibreOfficeEngineSha256,
        long maximumBinaryBytes)
    {
        ProbeEvidence version;
        ProbeEvidence bitness;
        ProbeEvidence identity;
        string? resolvedExecutablePath = null;
        string? pinnedEngineSha256 = null;
        try
        {
            using var executableFile = LockedFilePath.OpenReadOnlyRegularFile(executable.Path, FileShare.Read);
            resolvedExecutablePath = LockedFilePath.ResolveTargetPath(executable.Path, executableFile.SafeFileHandle);
            if (executable.Suite == "LibreOffice")
            {
                version = ReadLibreOfficeVersion(
                    executable,
                    expectedLibreOfficeEngineSha256,
                    executableFile,
                    maximumBinaryBytes);
                if (version.Value is LibreOfficeVersionEvidenceValue measuredVersion)
                {
                    pinnedEngineSha256 = measuredVersion.EngineSha256;
                }
            }
            else
            {
                version = ReadOfficeVersionWithinLimit(
                    executableFile,
                    maximumBinaryBytes,
                    () => ReadOfficeVersion(executable, executableFile));
            }

            var binaryPath = ResolveBinaryMetadataPath(executable);
            using var metadataFile = PathEquals(binaryPath, executable.Path)
                ? null
                : LockedFilePath.OpenReadOnlyRegularFile(binaryPath, FileShare.Read);
            var binaryFile = metadataFile ?? executableFile;
            var resolvedBinaryPath = LockedFilePath.ResolveTargetPath(binaryPath, binaryFile.SafeFileHandle);
            string? observedSha256 = null;
            string? binaryLimitFailure = null;
            OfficeBinaryInspection? binaryInspection = null;
            try
            {
                binaryInspection = InspectBinaryWithinLimit(binaryFile, maximumBinaryBytes);
                observedSha256 = binaryInspection.Sha256;
            }
            catch (InvalidDataException exception)
            {
                binaryLimitFailure = exception.Message;
            }

            if (binaryLimitFailure is not null)
            {
                const string step = "read executable binary within the bounded inspection limit";
                bitness = ProbeEvidence.Blocked(EvidenceSource.Native, step, binaryLimitFailure);
                identity = ProbeEvidence.Blocked(EvidenceSource.Native, step, binaryLimitFailure);
            }
            else if (pinnedEngineSha256 is not null
                && !string.Equals(pinnedEngineSha256, observedSha256, StringComparison.OrdinalIgnoreCase))
            {
                const string changed = "The opened engine source changed after the version command snapshot was pinned, so its identity and bitness cannot be attributed to the launched image.";
                bitness = ProbeEvidence.Blocked(EvidenceSource.Native, "compare source binary with the pinned version-command image", changed);
                identity = ProbeEvidence.Blocked(EvidenceSource.Native, "compare source binary SHA-256 with the pinned version-command image", changed);
            }
            else
            {
                var architecture = binaryInspection!.Architecture;
                bitness = architecture is null
                    ? ProbeEvidence.Blocked(EvidenceSource.Native, "read PE/ELF machine header", "The executable header format or architecture is not supported by this probe.")
                    : ProbeEvidence.Verified(EvidenceSource.Native, "read executable or engine-binary header without launching the suite", architecture);

                identity = ProbeEvidence.Verified(
                    EvidenceSource.Native,
                    "read locked executable length and SHA-256 without loading the binary",
                    new { requestedPath = binaryPath, resolvedPath = resolvedBinaryPath, sizeBytes = binaryInspection.SizeBytes, sha256 = observedSha256 });
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            version = ProbeEvidence.Blocked(EvidenceSource.Native, "read executable metadata", exception.Message);
            bitness = version;
            identity = version;
        }
        catch (IOException exception)
        {
            version = ProbeEvidence.Blocked(EvidenceSource.Native, "read executable metadata", exception.Message);
            bitness = version;
            identity = version;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            version = ProbeEvidence.Blocked(EvidenceSource.Native, "read executable metadata", exception.Message);
            bitness = version;
            identity = version;
        }
        catch (System.Security.SecurityException exception)
        {
            version = ProbeEvidence.Blocked(EvidenceSource.Native, "read executable metadata", exception.Message);
            bitness = version;
            identity = version;
        }

        var suite = version.Status != EvidenceStatus.Verified
            ? "Unverified executable candidate"
            : executable.Suite;
        var component = suite == executable.Suite ? executable.Component : Path.GetFileName(executable.Path);
        return new OfficeSuiteMeasurement(suite, component, executable.Path, resolvedExecutablePath, version, bitness, identity);
    }

    private static ProbeEvidence ReadOfficeVersion(OfficeExecutable executable, FileStream lockedFile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "verify Office suite Authenticode publisher",
                "Windows trust verification is unavailable on this operating system; version-resource strings alone are not accepted as suite identity.");
        }

        var identity = ReadOfficeIdentityWithLockedFile(
            executable.Path,
            lockedFile,
            VerifyAuthenticodePublisher,
            (path, _) => FileVersionInfo.GetVersionInfo(path));
        var signer = identity.Identity;
        if (!signer.Trusted)
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "verify Office suite Authenticode publisher",
                signer.Limitation ?? "The executable signature or signer identity could not be verified.");
        }

        var versionInfo = identity.Metadata;
        var productName = versionInfo.ProductName?.Trim();
        var companyName = versionInfo.CompanyName?.Trim();
        var fileVersion = versionInfo.FileVersion?.Trim();
        if (string.IsNullOrWhiteSpace(fileVersion))
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "read executable version and product identity resources",
                "The executable candidate has no readable version resource to confirm suite identity.");
        }

        if (!HasExpectedOfficeIdentity(executable.Suite, productName, companyName, signer.Organization, signer.Trusted))
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "verify signed publisher and Office suite product identity",
                "The trusted signer or executable version resource does not identify the required Office suite.");
        }

        return ProbeEvidence.Verified(
            EvidenceSource.Native,
            "WinVerifyTrust with whole-chain revocation checks, signed publisher certificate, and executable version identity",
            new { fileVersion, productName, companyName, signerOrganization = signer.Organization, authenticodeTrusted = signer.Trusted });
    }

    internal static ProbeEvidence ReadOfficeVersionWithinLimit(
        FileStream lockedFile,
        long maximumBytes,
        Func<ProbeEvidence> readOfficeVersion)
    {
        if (lockedFile.Length > maximumBytes)
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "check executable size before Office trust verification",
                $"The executable exceeds the {maximumBytes / (1024 * 1024)} MiB inspection limit.");
        }

        return readOfficeVersion();
    }

    internal static bool HasExpectedOfficeIdentity(
        string suite,
        string? productName,
        string? companyName,
        string? signerOrganization,
        bool authenticodeTrusted)
    {
        if (!authenticodeTrusted
            || !IsExpectedPublisher(suite, signerOrganization)
            || string.IsNullOrWhiteSpace(productName)
            || string.IsNullOrWhiteSpace(companyName))
        {
            return false;
        }

        if (suite == "Microsoft Office")
        {
            var productMatches = HasToken(productName, "Office")
                || HasToken(productName, "Word")
                || HasToken(productName, "Excel")
                || HasToken(productName, "PowerPoint");
            return HasToken(companyName, "Microsoft") && productMatches;
        }

        return suite == "WPS Office"
            && (HasToken(companyName, "Kingsoft") || HasToken(companyName, "WPS"))
            && (HasToken(productName, "WPS") || HasAdjacentTokens(productName, "Kingsoft", "Office"));
    }

    private static bool IsExpectedPublisher(string suite, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return false;
        }

        var normalizedPublisher = NormalizePublisher(publisher);
        return suite switch
        {
            "Microsoft Office" => normalizedPublisher == "MICROSOFTCORPORATION",
            "WPS Office" => normalizedPublisher == "ZHUHAIKINGSOFTOFFICESOFTWARECOLTD",
            _ => false
        };
    }

    private static string NormalizePublisher(string publisher) =>
        new(publisher.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static (TIdentity Identity, TMetadata Metadata) ReadOfficeIdentityWithLockedFile<TIdentity, TMetadata>(
        string path,
        Func<string, SafeFileHandle, TIdentity> identityReader,
        Func<string, SafeFileHandle, TMetadata> metadataReader)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadOfficeIdentityWithLockedFile(path, file, identityReader, metadataReader);
    }

    private static (TIdentity Identity, TMetadata Metadata) ReadOfficeIdentityWithLockedFile<TIdentity, TMetadata>(
        string path,
        FileStream lockedFile,
        Func<string, SafeFileHandle, TIdentity> identityReader,
        Func<string, SafeFileHandle, TMetadata> metadataReader)
    {
        var fileHandle = lockedFile.SafeFileHandle;
        var stablePath = LockedFilePath.Resolve(path, fileHandle);
        var identity = identityReader(stablePath, fileHandle);
        var metadata = metadataReader(stablePath, fileHandle);
        return (identity, metadata);
    }

    private static AuthenticodePublisherResult VerifyAuthenticodePublisher(string path, SafeFileHandle fileHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AuthenticodePublisherResult(false, null, "Windows Authenticode verification is unavailable on this operating system.");
        }

        IntPtr filePath = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        var trustData = new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = WinTrustUiNone,
            RevocationChecks = WinTrustRevokeWholeChain,
            UnionChoice = WinTrustChoiceFile,
            StateAction = WinTrustStateVerify
        };
        var action = GenericVerifyV2;
        var addedHandleReference = false;
        try
        {
            fileHandle.DangerousAddRef(ref addedHandleReference);
            if (fileHandle.IsInvalid || fileHandle.IsClosed)
            {
                return new AuthenticodePublisherResult(false, null, "The locked executable file handle is unavailable.");
            }

            filePath = Marshal.StringToHGlobalUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath,
                FileHandle = fileHandle.DangerousGetHandle()
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            trustData.FileInfo = fileInfoPointer;

            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            if (status != 0)
            {
                return new AuthenticodePublisherResult(
                    false,
                    null,
                    $"WinVerifyTrust rejected the executable signature or trust chain (0x{status:X8}).");
            }

            #pragma warning disable SYSLIB0057 // WinVerifyTrust verifies the file; this API extracts its embedded signer certificate.
            using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
            #pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signedCertificate.Export(X509ContentType.Cert));
            var organization = ReadSignerOrganization(certificate.SubjectName);
            if (organization is null)
            {
                return new AuthenticodePublisherResult(
                    false,
                    null,
                    "The trusted signer certificate does not contain exactly one publisher organization value.");
            }

            return new AuthenticodePublisherResult(true, organization, null);
        }
        catch (Exception exception) when (exception is CryptographicException
            or AsnContentException
            or DllNotFoundException
            or EntryPointNotFoundException
            or MarshalDirectiveException
            or InvalidOperationException)
        {
            return new AuthenticodePublisherResult(false, null, $"Authenticode publisher verification failed: {exception.Message}");
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = WinTrustStateClose;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            if (filePath != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(filePath);
            }
            if (addedHandleReference)
            {
                fileHandle.DangerousRelease();
            }
        }
    }

    private sealed record AuthenticodePublisherResult(bool Trusted, string? Organization, string? Limitation);

    internal static string? ReadSignerOrganization(X500DistinguishedName subjectName)
    {
        try
        {
            var organizations = new List<string>();
            foreach (var relativeName in subjectName.EnumerateRelativeDistinguishedNames())
            {
                var setReader = new AsnReader(relativeName.RawData, AsnEncodingRules.DER).ReadSetOf();
                while (setReader.HasData)
                {
                    var attribute = setReader.ReadSequence();
                    var oid = attribute.ReadObjectIdentifier();
                    if (oid == OrganizationNameOid)
                    {
                        var tag = attribute.PeekTag();
                        if (tag.TagClass != TagClass.Universal || !IsDirectoryStringTag(tag.TagValue))
                        {
                            return null;
                        }

                        var value = attribute.ReadCharacterString((UniversalTagNumber)tag.TagValue).Trim();
                        if (value.Length == 0 || attribute.HasData)
                        {
                            return null;
                        }
                        organizations.Add(value);
                    }
                    else
                    {
                        _ = attribute.ReadEncodedValue();
                        if (attribute.HasData)
                        {
                            return null;
                        }
                    }
                }
            }

            return organizations.Count == 1 ? organizations[0] : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static bool IsDirectoryStringTag(int tagValue) =>
        tagValue == (int)UniversalTagNumber.UTF8String
        || tagValue == (int)UniversalTagNumber.PrintableString
        || tagValue == (int)UniversalTagNumber.TeletexString
        || tagValue == (int)UniversalTagNumber.UniversalString
        || tagValue == (int)UniversalTagNumber.BMPString;

    private const string OrganizationNameOid = "2.5.4.10";
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeWholeChain = 1;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateVerify = 1;
    private const uint WinTrustStateClose = 2;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    private static bool HasToken(string value, string expectedToken) =>
        System.Text.RegularExpressions.Regex.Split(value, "[^A-Za-z0-9]+")
            .Any(token => string.Equals(token, expectedToken, StringComparison.OrdinalIgnoreCase));

    private static bool HasAdjacentTokens(string value, string first, string second)
    {
        var tokens = System.Text.RegularExpressions.Regex.Split(value, "[^A-Za-z0-9]+");
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (string.Equals(tokens[index], first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(tokens[index + 1], second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ProbeEvidence ReadLibreOfficeVersion(
        OfficeExecutable executable,
        string? expectedSha256,
        FileStream lockedFile,
        long maximumEngineBytes)
    {
        if (!executable.IsEngineBinary)
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "inspect LibreOffice launcher without executing it",
                "Supply --libreoffice-engine-path and its trusted SHA-256; the discovered launcher is never used for native evidence.");
        }

        if (expectedSha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(expectedSha256, "^[0-9a-fA-F]{64}$"))
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "compare direct engine SHA-256 with --libreoffice-engine-sha256 trusted pin",
                "A 64-character SHA-256 from an independent trusted test-image manifest is required before launching the engine binary.");
        }

        var profile = Path.Combine(Path.GetTempPath(), "paperbatch-lo-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(profile);
            using var snapshot = PinnedEngineSnapshot.Create(
                lockedFile,
                executable.Path,
                expectedSha256,
                maximumEngineBytes);
            var engineSha256 = snapshot.Sha256;
            var startInfo = new ProcessStartInfo(snapshot.LaunchPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = snapshot.RuntimeDirectoryPath
            };
            if (OperatingSystem.IsLinux())
            {
                PinnedEngineSnapshot.ConfigureLinuxLaunchEnvironment(startInfo.Environment, snapshot.RuntimeDirectoryPath);
            }
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--nodefault");
            startInfo.ArgumentList.Add("--nofirststartwizard");
            startInfo.ArgumentList.Add("-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri);
            startInfo.ArgumentList.Add("--version");

            snapshot.VerifyRuntimeComponentsUnchanged();
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ProbeEvidence.Blocked(EvidenceSource.Native, "isolated LibreOffice --version process", "The process could not be started.");
            }

            var result = RunVersionProcessWithBoundedOutput(process, TimeSpan.FromMilliseconds(15_000));
            if (!result.Exited)
            {
                snapshot.VerifyRuntimeComponentsUnchanged();
                return ProbeEvidence.Blocked(
                    EvidenceSource.Native,
                    "isolated LibreOffice --version process",
                    "The probe exceeded the 15 second timeout; complete process-tree termination could not be confirmed.");
            }

            snapshot.VerifyRuntimeComponentsUnchanged();
            var output = (result.StandardOutput + "\n" + result.StandardError).Trim();
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                var diagnostic = output.Length > 500 ? output[..500] : output;
                return ProbeEvidence.Blocked(
                    EvidenceSource.Native,
                    "isolated LibreOffice --version process",
                    $"The isolated version command exited with code {result.ExitCode}. {diagnostic}");
            }

            if (!IsLibreOfficeVersionOutput(output))
            {
                return ProbeEvidence.Blocked(
                    EvidenceSource.Native,
                    "isolated LibreOffice --version process",
                    "The executable's version output did not identify LibreOffice or LibreOfficeDev.");
            }

            return ProbeEvidence.Verified(
                EvidenceSource.Native,
                "match the directly invoked engine against --libreoffice-engine-sha256, fingerprint the sibling runtime tree, then run --version with a unique temporary UserInstallation profile and confirm the runtime tree is unchanged",
                new LibreOfficeVersionEvidenceValue(
                    output,
                    engineSha256,
                    expectedSha256.ToLowerInvariant(),
                    snapshot.RuntimeComponentsSha256));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return ProbeEvidence.Blocked(EvidenceSource.Native, "isolated LibreOffice --version process", exception.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(profile))
                {
                    Directory.Delete(profile, recursive: true);
                }
            }
            catch (IOException)
            {
                // The report remains valid; a temporary-profile cleanup failure is not a native pass.
            }
            catch (UnauthorizedAccessException)
            {
                // The report remains valid; a temporary-profile cleanup failure is not a native pass.
            }
        }
    }

    internal static BoundedVersionProcessResult RunVersionProcessWithBoundedOutput(
        Process process,
        TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var processTree = VersionProcessTree.Create(process);
        using var cancellation = new CancellationTokenSource();
        var standardOutput = ReadBoundedVersionOutputAsync(process.StandardOutput, cancellation.Token);
        var standardError = ReadBoundedVersionOutputAsync(process.StandardError, cancellation.Token);
        var completion = Task.WhenAll(process.WaitForExitAsync(), standardOutput, standardError);
        var timer = Stopwatch.StartNew();
        var cleanupBudget = TimeSpan.FromMilliseconds(Math.Min(1000, timeout.TotalMilliseconds / 4));
        try
        {
            completion.WaitAsync(timeout - cleanupBudget).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            try
            {
                processTree.Terminate(process, timeout - timer.Elapsed);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
            {
            }

            cancellation.Cancel();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            ObserveDrainCompletion(standardOutput);
            ObserveDrainCompletion(standardError);
            return new BoundedVersionProcessResult(false, -1, string.Empty, string.Empty);
        }

        return new BoundedVersionProcessResult(
            true,
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static async Task<string> ReadBoundedVersionOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var captured = new StringBuilder(MaximumVersionOutputCharacters);
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                var remaining = MaximumVersionOutputCharacters - captured.Length;
                if (remaining > 0)
                    captured.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return captured.ToString();
    }

    private static void ObserveDrainCompletion(Task<string> drain)
    {
        _ = drain.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class VersionProcessTree : IDisposable
    {
        private readonly SafeFileHandle? _job;

        private VersionProcessTree(SafeFileHandle? job)
        {
            _job = job;
        }

        public static VersionProcessTree Create(Process process)
        {
            if (!OperatingSystem.IsWindows()) return new VersionProcessTree(null);

            var job = CreateJobObject(IntPtr.Zero, null);
            if (!job.IsInvalid && AssignProcessToJobObject(job, process.Handle))
                return new VersionProcessTree(job);
            job.Dispose();
            return new VersionProcessTree(null);
        }

        public void Terminate(Process process, TimeSpan waitBudget)
        {
            if (_job is { IsInvalid: false, IsClosed: false })
                _ = TerminateJobObject(_job, 1);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (waitBudget > TimeSpan.Zero)
                    process.WaitForExit((int)Math.Min(int.MaxValue, waitBudget.TotalMilliseconds));
            }
        }

        public void Dispose() => _job?.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    }

    private static bool IsLibreOfficeVersionOutput(string output)
    {
        var value = output.TrimStart();
        return value.StartsWith("LibreOffice ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("LibreOfficeDev ", StringComparison.OrdinalIgnoreCase);
    }

    internal static OfficeBinaryInspection InspectBinaryWithinLimit(Stream stream, long maximumBytes)
    {
        if (!stream.CanSeek || maximumBytes < 0 || maximumBytes > PinnedEngineSnapshot.DefaultMaximumSnapshotBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Binary inspection requires a seekable stream and a limit within the supported range.");
        }

        stream.Position = 0;
        var initialLength = stream.Length;
        if (initialLength < 0 || initialLength > maximumBytes)
        {
            throw new InvalidDataException($"The executable exceeds the {maximumBytes / (1024 * 1024)} MiB inspection limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var prefix = new byte[64];
        var prefixBytes = 0;
        var peHeader = new byte[26];
        var peHeaderBytes = 0;
        int? peOffset = null;
        long bytesHashed = 0;

        while (true)
        {
            var remainingWithSentinel = maximumBytes - bytesHashed + 1;
            var readLimit = (int)Math.Min(buffer.Length, remainingWithSentinel);
            var bytesRead = stream.Read(buffer, 0, readLimit);
            if (bytesRead == 0)
            {
                break;
            }

            if (bytesHashed + bytesRead > maximumBytes)
            {
                throw new InvalidDataException($"The executable exceeds the {maximumBytes / (1024 * 1024)} MiB inspection limit.");
            }

            hash.AppendData(buffer, 0, bytesRead);
            var chunkStart = bytesHashed;
            var chunkEnd = chunkStart + bytesRead;
            if (prefixBytes < prefix.Length)
            {
                var prefixCount = Math.Min(bytesRead, prefix.Length - prefixBytes);
                buffer.AsSpan(0, prefixCount).CopyTo(prefix.AsSpan(prefixBytes));
                prefixBytes += prefixCount;
            }

            if (peOffset is null
                && prefixBytes == prefix.Length
                && prefix[0] == (byte)'M'
                && prefix[1] == (byte)'Z')
            {
                var candidateOffset = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(0x3C, 4));
                if (candidateOffset >= 0x40 && candidateOffset <= initialLength - peHeader.Length)
                {
                    peOffset = candidateOffset;
                }
            }

            if (peOffset is int offset)
            {
                var overlapStart = Math.Max(chunkStart, offset);
                var overlapEnd = Math.Min(chunkEnd, (long)offset + peHeader.Length);
                if (overlapStart < overlapEnd)
                {
                    var overlapLength = checked((int)(overlapEnd - overlapStart));
                    var sourceOffset = checked((int)(overlapStart - chunkStart));
                    var destinationOffset = checked((int)(overlapStart - offset));
                    buffer.AsSpan(sourceOffset, overlapLength).CopyTo(peHeader.AsSpan(destinationOffset));
                    peHeaderBytes += overlapLength;
                }
            }

            bytesHashed = chunkEnd;
        }

        if (bytesHashed != initialLength || stream.Length != initialLength)
        {
            throw new InvalidDataException("The executable length changed while its digest and architecture were being inspected.");
        }

        var architecture = ReadArchitecture(prefix.AsSpan(0, prefixBytes), peHeader.AsSpan(0, peHeaderBytes), initialLength);
        return new OfficeBinaryInspection(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            architecture,
            initialLength);
    }

    private static string? ReadArchitecture(ReadOnlySpan<byte> header, ReadOnlySpan<byte> peHeader, long streamLength)
    {
        if (header.Length >= 20
            && header[0] == 0x7F
            && header[1] == (byte)'E'
            && header[2] == (byte)'L'
            && header[3] == (byte)'F')
        {
            if (header[5] != 1)
            {
                return null;
            }

            var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(18, 2));
            return (header[4], machine) switch
            {
                (1, 3) => "x86 (32-bit)",
                (2, 62) => "x64 (64-bit)",
                (2, 183) => "arm64 (64-bit)",
                _ => null
            };
        }

        if (header.Length < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            return null;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0x3C, 4));
        if (peOffset < 0x40 || peOffset > streamLength - peHeader.Length || peHeader.Length != 26
            || peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E' || peHeader[2] != 0 || peHeader[3] != 0)
        {
            return null;
        }

        var machineType = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.Slice(4, 2));
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.Slice(20, 2));
        if (optionalHeaderSize < 2 || (long)peOffset + 24 + optionalHeaderSize > streamLength)
        {
            return null;
        }

        var optionalHeaderMagic = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.Slice(24, 2));
        return (machineType, optionalHeaderMagic) switch
        {
            (0x014C, 0x010B) => "x86 (32-bit)",
            (0x8664, 0x020B) => "x64 (64-bit)",
            (0xAA64, 0x020B) => "arm64 (64-bit)",
            _ => null
        };
    }

    private static string ResolveBinaryMetadataPath(OfficeExecutable executable)
    {
        if (executable.Suite != "LibreOffice" || executable.IsEngineBinary)
        {
            return executable.Path;
        }

        var directory = Path.GetDirectoryName(executable.Path);
        if (string.IsNullOrEmpty(directory))
        {
            return executable.Path;
        }

        var engineBinary = Path.Combine(directory, "soffice.bin");
        return File.Exists(engineBinary) ? engineBinary : executable.Path;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record OfficeExecutable(string Suite, string Component, string Path, bool IsEngineBinary = false);
}
