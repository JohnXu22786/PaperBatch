using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace PaperBatch.CompatibilityProbe;

public sealed record PrintQueueMeasurement(string Name, string DriverName, string PortName, uint Status, uint CurrentJobCount);

public sealed record PrintProbeResult(
    ProbeEvidence Backend,
    ProbeEvidence Queues,
    ProbeEvidence Drivers,
    ProbeEvidence GdiQueueDriverRoute,
    ProbeEvidence GdiJobId,
    ProbeEvidence PhysicalDeviceOutput);

public static class PrintProbe
{
    public static PrintProbeResult Collect(bool includeLocalQueues)
    {
        if (!OperatingSystem.IsWindows())
        {
            var backend = ProbeEvidence.Unavailable(EvidenceSource.Native, "platform check", "The Windows GDI/Winspool backend is unavailable on this operating system.");
            var queues = ProbeEvidence.Unavailable(EvidenceSource.Native, "platform check", "This probe does not enumerate non-Windows print queues.");
            return new PrintProbeResult(
                backend,
                queues,
                queues,
                ProbeEvidence.Unavailable(EvidenceSource.Native, "validate a Windows GDI queue/driver/port route", "The Windows GDI/Winspool backend is unavailable on this operating system."),
                ProbeEvidence.Unavailable(EvidenceSource.Native, "GDI test-job submission and job-ID correlation", "The Windows GDI/Winspool backend is unavailable on this operating system."),
                ProbeEvidence.Unavailable(EvidenceSource.Native, "physical-device output confirmation", "The Windows print backend and physical printer are unavailable on this operating system."));
        }

        return WindowsPrintProbe.Collect(includeLocalQueues);
    }
}

internal static class WindowsPrintProbe
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;

    public static PrintProbeResult Collect(bool includeLocalQueues)
    {
        var spoolerPath = Path.Combine(Environment.SystemDirectory, "winspool.drv");
        var pathEvidence = InspectWinspoolPath(spoolerPath, File.GetAttributes);
        if (pathEvidence.Status != EvidenceStatus.Verified)
        {
            return NotRunResult(pathEvidence, includeLocalQueues);
        }

        if (!NativeLibrary.TryLoad(spoolerPath, out var library))
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "load the absolute system winspool.drv path", "The Windows print-spooler library could not be loaded.");
            return NotRunResult(blocked, includeLocalQueues);
        }

        try
        {
            var apiNames = new[] { "EnumPrintersW", "StartDocPrinterW", "GetJobW", "EnumJobsW" };
            var available = apiNames.Where(name => NativeLibrary.TryGetExport(library, name, out _)).ToArray();
            var backend = available.Length == apiNames.Length
                ? ProbeEvidence.Verified(EvidenceSource.Native, "inspect Winspool entry points; no queue was opened and no job was submitted", new { libraryPath = spoolerPath, entryPoints = available })
                : ProbeEvidence.Unavailable(EvidenceSource.Native, "inspect Winspool entry points", $"Required exports were missing from winspool.drv; available: {string.Join(", ", available)}.");

            ProbeEvidence queues;
            ProbeEvidence drivers;
            if (!includeLocalQueues)
            {
                queues = ProbeEvidence.NotRun(EvidenceSource.Native, "local queue enumeration", "Pass --include-local-queues to read local queue names and driver labels; no printer settings will be changed.");
                drivers = ProbeEvidence.NotRun(EvidenceSource.Native, "local driver enumeration", "Local queues were not enumerated.");
            }
            else if (!available.Contains("EnumPrintersW", StringComparer.Ordinal))
            {
                queues = ProbeEvidence.Unavailable(EvidenceSource.Native, "local queue enumeration", "EnumPrintersW is not exported by the system print library.");
                drivers = ProbeEvidence.Unavailable(EvidenceSource.Native, "local driver enumeration", "The queue enumeration API is unavailable.");
            }
            else
            {
                try
                {
                    var enumerate = Marshal.GetDelegateForFunctionPointer<EnumPrintersDelegate>(NativeLibrary.GetExport(library, "EnumPrintersW"));
                    var localQueues = EnumerateLocalQueues(enumerate);
                    queues = localQueues.Count == 0
                        ? ProbeEvidence.Unavailable(EvidenceSource.Native, "EnumPrintersW(PRINTER_ENUM_LOCAL)", "Windows returned no local print queues.")
                        : ProbeEvidence.Verified(EvidenceSource.Native, "EnumPrintersW(PRINTER_ENUM_LOCAL); no jobs or documents were opened", localQueues);
                    drivers = localQueues.Count == 0
                        ? ProbeEvidence.Unavailable(EvidenceSource.Native, "local queue driver labels", "No local queue driver labels were returned.")
                        : ProbeEvidence.Verified(EvidenceSource.Native, "read driver labels from local PRINTER_INFO_2 records", localQueues.Select(queue => new { queue.Name, queue.DriverName }).ToArray());
                }
                catch (UnauthorizedAccessException exception)
                {
                    queues = ProbeEvidence.Blocked(EvidenceSource.Native, "local queue enumeration", exception.Message);
                    drivers = queues;
                }
                catch (Win32Exception exception)
                {
                    queues = ProbeEvidence.Blocked(EvidenceSource.Native, "local queue enumeration", $"Winspool returned error {exception.NativeErrorCode}.");
                    drivers = queues;
                }
            }

            return new PrintProbeResult(
                backend,
                queues,
                drivers,
                RouteEvidence(queues),
                NoSafeQueueSubmissionEvidence(),
                ProbeEvidence.NotRun(EvidenceSource.Native, "physical-device output confirmation", "No scoped test page was submitted and no operator-verified paper output was recorded."));
        }
        catch (SecurityException exception)
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "Winspool system-library inspection", exception.Message);
            return NotRunResult(blocked, includeLocalQueues);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    internal static ProbeEvidence NoSafeQueueSubmissionEvidence() => ProbeEvidence.Unavailable(
        EvidenceSource.Native,
        "GDI test-job submission and job-ID correlation",
        "No test page was submitted: this probe has no verified noninteractive, local-only virtual queue policy. Windows built-in file-output queues can show a save dialog, and other queue routes may contact an external printer or service.");

    internal static ProbeEvidence InspectWinspoolPath(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        try
        {
            var attributes = getAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return ProbeEvidence.Blocked(
                    EvidenceSource.Native,
                    "Windows system-library inspection",
                    "winspool.drv resolved to a directory instead of a file.");
            }

            return ProbeEvidence.Verified(
                EvidenceSource.Native,
                "inspect Windows system-library file attributes",
                new { path, attributes });
        }
        catch (FileNotFoundException)
        {
            return ProbeEvidence.Unavailable(
                EvidenceSource.Native,
                "Windows system-library inspection",
                "winspool.drv was not found in the system directory.");
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeEvidence.Unavailable(
                EvidenceSource.Native,
                "Windows system-library inspection",
                "The Windows system directory was not found.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException or Win32Exception)
        {
            return ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "Windows system-library inspection",
                exception.Message);
        }
    }

    private static List<PrintQueueMeasurement> EnumerateLocalQueues(EnumPrintersDelegate enumerate)
    {
        _ = enumerate(PrinterEnumLocal, null, 2, IntPtr.Zero, 0, out var bytesNeeded, out var returned);
        var firstError = Marshal.GetLastWin32Error();
        if (bytesNeeded == 0 && returned == 0)
        {
            if (firstError is 0 or ErrorInsufficientBuffer)
            {
                return [];
            }

            throw new Win32Exception(firstError);
        }

        if (firstError != ErrorInsufficientBuffer && bytesNeeded == 0)
        {
            throw new Win32Exception(firstError);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!enumerate(PrinterEnumLocal, null, 2, buffer, bytesNeeded, out _, out returned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var results = new List<PrintQueueMeasurement>(checked((int)returned));
            var entrySize = Marshal.SizeOf<PrinterInfo2>();
            for (var index = 0; index < returned; index++)
            {
                var address = IntPtr.Add(buffer, checked((int)index * entrySize));
                var info = Marshal.PtrToStructure<PrinterInfo2>(address);
                results.Add(new PrintQueueMeasurement(
                    Marshal.PtrToStringUni(info.PrinterName) ?? string.Empty,
                    Marshal.PtrToStringUni(info.DriverName) ?? string.Empty,
                    Marshal.PtrToStringUni(info.PortName) ?? string.Empty,
                    info.Status,
                    info.JobCount));
            }

            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PrintProbeResult NotRunResult(ProbeEvidence backend, bool includeLocalQueues)
    {
        var inaccessibleStatus = backend.Status switch
        {
            EvidenceStatus.Blocked => EvidenceStatus.Blocked,
            EvidenceStatus.Unavailable => EvidenceStatus.Unavailable,
            _ => EvidenceStatus.NotRun
        };
        var queueStatus = inaccessibleStatus == EvidenceStatus.NotRun && !includeLocalQueues
            ? ProbeEvidence.NotRun(EvidenceSource.Native, "local queue enumeration", "Pass --include-local-queues to read local queue names.")
            : EvidenceForStatus(inaccessibleStatus, "local queue enumeration", backend.Limitation ?? "The print-spooler library could not be inspected.");
        var gdiJobId = inaccessibleStatus switch
        {
            EvidenceStatus.Blocked => ProbeEvidence.Blocked(EvidenceSource.Native, "GDI test-job submission and job-ID correlation", backend.Limitation ?? "The print-spooler library could not be inspected."),
            EvidenceStatus.Unavailable => ProbeEvidence.Unavailable(EvidenceSource.Native, "GDI test-job submission and job-ID correlation", backend.Limitation ?? "The print-spooler library is unavailable."),
            _ => ProbeEvidence.NotRun(EvidenceSource.Native, "GDI test-job submission and job-ID correlation", "No test page or printer job was submitted.")
        };
        var physicalDeviceOutput = inaccessibleStatus switch
        {
            EvidenceStatus.Blocked => ProbeEvidence.Blocked(EvidenceSource.Native, "physical-device output confirmation", backend.Limitation ?? "The print-spooler library could not be inspected."),
            EvidenceStatus.Unavailable => ProbeEvidence.Unavailable(EvidenceSource.Native, "physical-device output confirmation", backend.Limitation ?? "The Windows print-spooler library is unavailable."),
            _ => ProbeEvidence.NotRun(EvidenceSource.Native, "physical-device output confirmation", "No scoped test page was submitted and no operator-verified paper output was recorded.")
        };
        return new PrintProbeResult(
            backend,
            queueStatus,
            inaccessibleStatus == EvidenceStatus.NotRun
                ? ProbeEvidence.NotRun(EvidenceSource.Native, "local driver enumeration", "Local queues were not enumerated.")
                : EvidenceForStatus(inaccessibleStatus, "local driver enumeration", backend.Limitation ?? "The print-spooler library could not be inspected."),
            EvidenceForStatus(
                inaccessibleStatus == EvidenceStatus.NotRun && !includeLocalQueues
                    ? EvidenceStatus.NotRun
                    : inaccessibleStatus,
                "validate a Windows GDI queue/driver/port route",
                backend.Limitation ?? "Queue and driver labels do not verify a usable route; no scoped print job was submitted."),
            gdiJobId,
            physicalDeviceOutput);
    }

    private static ProbeEvidence RouteEvidence(ProbeEvidence queueInventory)
    {
        return queueInventory.Status switch
        {
            EvidenceStatus.Verified => ProbeEvidence.NotRun(
                EvidenceSource.Native,
                "validate a Windows GDI queue/driver/port route",
                "Queue and driver labels were inventoried, but no scoped test print verified that the route can reach a device."),
            EvidenceStatus.Unavailable => ProbeEvidence.Unavailable(
                EvidenceSource.Native,
                "validate a Windows GDI queue/driver/port route",
                queueInventory.Limitation ?? "No local queue was available for route validation."),
            EvidenceStatus.Blocked => ProbeEvidence.Blocked(
                EvidenceSource.Native,
                "validate a Windows GDI queue/driver/port route",
                queueInventory.Limitation ?? "Queue inspection was blocked before route validation."),
            _ => ProbeEvidence.NotRun(
                EvidenceSource.Native,
                "validate a Windows GDI queue/driver/port route",
                "Local queues were not enumerated and no scoped print job was submitted.")
        };
    }

    private static ProbeEvidence EvidenceForStatus(EvidenceStatus status, string method, string limitation) => status switch
    {
        EvidenceStatus.Blocked => ProbeEvidence.Blocked(EvidenceSource.Native, method, limitation),
        EvidenceStatus.Unavailable => ProbeEvidence.Unavailable(EvidenceSource.Native, method, limitation),
        _ => ProbeEvidence.NotRun(EvidenceSource.Native, method, limitation)
    };

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate bool EnumPrintersDelegate(uint flags, string? name, uint level, IntPtr buffer, uint bufferSize, out uint bytesNeeded, out uint returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName;
        public IntPtr PrinterName;
        public IntPtr ShareName;
        public IntPtr PortName;
        public IntPtr DriverName;
        public IntPtr Comment;
        public IntPtr Location;
        public IntPtr DevMode;
        public IntPtr SeparatorFile;
        public IntPtr PrintProcessor;
        public IntPtr DataType;
        public IntPtr Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint JobCount;
        public uint AveragePagesPerMinute;
    }
}
