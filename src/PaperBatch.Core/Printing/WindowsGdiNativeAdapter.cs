using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaperBatch.Core.Printing;

/// <summary>Production GDI/Winspool boundary. It never selects or changes a default printer.</summary>
public sealed class WindowsGdiNativeAdapter : IWindowsGdiNativeAdapter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CancelGates = new(StringComparer.Ordinal);
    private const uint DcPaperIds = 2;
    private const uint DcBinIds = 6;
    private const uint DcDuplex = 7;
    private const uint DcCopies = 18;
    private const int MaximumDeviceValues = 4096;
    private const int DmInBuffer = 8;
    private const int DmOutBuffer = 2;
    private const uint DmOrientation = 0x00000001;
    private const uint DmPaperSize = 0x00000002;
    private const uint DmDefaultSource = 0x00000200;
    private const uint DmCopies = 0x00000100;
    private const uint DmColor = 0x00000800;
    private const uint DmDuplex = 0x00001000;
    private const int Portrait = 1;
    private const int Landscape = 2;
    private const int Color = 2;
    private const int Monochrome = 1;
    private const int Simplex = 1;
    private const int LongEdge = 2;
    private const int ShortEdge = 3;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint SrcCopy = 0x00CC0020;
    private const int GdiError = -1;
    private const uint JobStatusDeleted = 0x00000100;
    private const uint JobStatusCancelled = 0x00008000;

    public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GDI/Winspool printing requires Windows.");
        var baseSnapshot = WindowsPrintCapabilityEnumerator.GetCapabilities(queueIdentity);
        return GetCapabilitiesAsync(baseSnapshot, cancellationToken);
    }

    public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(PrinterCapabilitySnapshot baseSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GDI/Winspool printing requires Windows.");
        var options = baseSnapshot.Options.ToBuilder();
        var paperIds = MapIds(baseSnapshot, PrintOption.Media, DcPaperIds, 2);
        var trayIds = MapIds(baseSnapshot, PrintOption.Tray, DcBinIds, 2);
        DisableIfUnmapped(options, baseSnapshot, PrintOption.Media, paperIds);
        DisableIfUnmapped(options, baseSnapshot, PrintOption.Tray, trayIds);
        var duplex = DeviceCapabilities(baseSnapshot.DisplayName, baseSnapshot.PortName, DcDuplex, IntPtr.Zero, IntPtr.Zero);
        var copies = DeviceCapabilities(baseSnapshot.DisplayName, baseSnapshot.PortName, DcCopies, IntPtr.Zero, IntPtr.Zero);
        options[PrintOption.Duplex] = MapDuplexCapability(duplex);
        if (copies <= 0) options[PrintOption.Copies] = new(CapabilityState.Unavailable, [], "The driver did not report a copy limit.");
        else options[PrintOption.Copies] = new(CapabilityState.Supported, [$"1..{copies}"], null);

        var nativeTokens = System.Collections.Immutable.ImmutableDictionary<PrintOption, System.Collections.Immutable.ImmutableDictionary<string, string>>.Empty
            .Add(PrintOption.Media, paperIds)
            .Add(PrintOption.Tray, trayIds)
            .Add(PrintOption.Copies, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("Maximum", Math.Min(copies, 999).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var tokenText = string.Join('\n', nativeTokens.SelectMany(option => option.Value.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => $"{option.Key}:{value.Key}:{value.Value}")));
        var finalOptions = options.ToImmutable();
        var optionText = string.Join("\n", finalOptions.OrderBy(pair => pair.Key).Select(pair =>
            $"{pair.Key}:{pair.Value.State}:{string.Join(',', pair.Value.Values)}:{pair.Value.Detail}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{baseSnapshot.QueueIdentity}\0{baseSnapshot.DriverName}\0{baseSnapshot.PortName}\n{optionText}\n{tokenText}\nduplex:{duplex};copies:{copies}"));
        var fingerprint = Convert.ToHexStringLower(hash);
        return Task.FromResult(baseSnapshot with { Options = finalOptions, Fingerprint = fingerprint, NativeTokens = nativeTokens });
    }

    public Task<IWindowsGdiDocument> StartDocAsync(WindowsPrintSubmission submission,
        IReadOnlyList<WindowsPrintRasterPage> pages, CancellationToken cancellationToken = default)
    {
        ThrowIfCancellationRequestedBeforeStartDoc(cancellationToken);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GDI/Winspool printing requires Windows.");
        var snapshot = GetCapabilitiesBeforeStartDoc(submission.Options.QueueIdentity, cancellationToken);
        if (!string.Equals(snapshot.Fingerprint, submission.Options.CapabilityFingerprint, StringComparison.Ordinal))
            throw new PrintSubmissionException("Queue capabilities changed at the native StartDoc boundary.", false);

        var nativeSettings = ResolveSettings(snapshot, submission, pages);
        if (!OpenPrinter(snapshot.DisplayName, out var printer, IntPtr.Zero))
            throw NativeFailure("The selected printer queue could not be opened.", false);
        IntPtr modeMemory = IntPtr.Zero;
        IntPtr dc = IntPtr.Zero;
        try
        {
            var openedQueue = GetCapabilitiesBeforeStartDoc(submission.Options.QueueIdentity, cancellationToken);
            if (!string.Equals(openedQueue.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
                throw new PrintSubmissionException("The selected queue changed while the native printer handle was being opened.", false);
            var modeSize = DocumentProperties(IntPtr.Zero, printer, snapshot.DisplayName, IntPtr.Zero, IntPtr.Zero, 0);
            if (modeSize <= 0 || modeSize > 1024 * 1024) throw new PrintSubmissionException("The driver returned an invalid DEVMODE size.", false);
            modeMemory = Marshal.AllocHGlobal(modeSize);
            if (DocumentProperties(IntPtr.Zero, printer, snapshot.DisplayName, modeMemory, IntPtr.Zero, DmOutBuffer) != 1)
                throw NativeFailure("The printer driver could not provide its current DEVMODE.", false);
            var mode = Marshal.PtrToStructure<DevMode>(modeMemory);
            if (mode.Size + mode.DriverExtra > modeSize) throw new PrintSubmissionException("The driver DEVMODE exceeds its allocated bounds.", false);
            mode.Fields |= DmOrientation | DmPaperSize | DmCopies | DmColor | DmDuplex;
            mode.Orientation = checked((short)nativeSettings.Orientation);
            mode.PaperSize = nativeSettings.Paper;
            if (nativeSettings.Tray is { } selectedTray)
            {
                mode.Fields |= DmDefaultSource;
                mode.DefaultSource = selectedTray;
            }
            mode.Copies = nativeSettings.Copies;
            mode.Color = checked((short)nativeSettings.Color);
            mode.Duplex = checked((short)nativeSettings.Duplex);
            Marshal.StructureToPtr(mode, modeMemory, fDeleteOld: false);
            if (DocumentProperties(IntPtr.Zero, printer, snapshot.DisplayName, modeMemory, modeMemory, DmInBuffer | DmOutBuffer) != 1)
                throw new PrintSubmissionException("The selected settings were rejected by the printer driver before StartDoc.", false);
            mode = Marshal.PtrToStructure<DevMode>(modeMemory);
            var requiredFields = DmOrientation | DmPaperSize | DmCopies | DmColor | DmDuplex |
                (nativeSettings.Tray is null ? 0u : DmDefaultSource);
            if (mode.Size + mode.DriverExtra > modeSize || !HasRequiredDevModeFields(mode.Fields, requiredFields) ||
                mode.Orientation != nativeSettings.Orientation || mode.PaperSize != nativeSettings.Paper ||
                (nativeSettings.Tray is { } expectedTray && mode.DefaultSource != expectedTray) || mode.Copies != nativeSettings.Copies ||
                mode.Color != nativeSettings.Color || mode.Duplex != nativeSettings.Duplex)
                throw new PrintSubmissionException("The printer driver did not preserve the requested DEVMODE settings.", false);

            dc = CreateDC("WINSPOOL", snapshot.DisplayName, null, modeMemory);
            if (dc == IntPtr.Zero) throw NativeFailure("The selected printer driver could not create a GDI device context.", false);
            var documentName = Marshal.StringToHGlobalUni(DocumentName(submission.Options.QueueIdentity, submission.RunNonce));
            try
            {
                var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), DocumentName = documentName };
                ThrowIfCancellationRequestedBeforeStartDoc(cancellationToken);
                var jobId = StartDoc(dc, ref info);
                if (jobId <= 0) throw NativeFailure("GDI StartDoc failed; the OS outcome is uncertain.", true);
                return Task.FromResult<IWindowsGdiDocument>(new GdiDocument(dc, printer, modeMemory, jobId, pages, submission.Segment.PrintSettings.ScalingMode));
            }
            finally { Marshal.FreeHGlobal(documentName); }
        }
        catch
        {
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (modeMemory != IntPtr.Zero) Marshal.FreeHGlobal(modeMemory);
            printer.Dispose();
            throw;
        }
    }

    private PrinterCapabilitySnapshot GetCapabilitiesBeforeStartDoc(string queueIdentity, CancellationToken cancellationToken)
    {
        try { return GetCapabilitiesAsync(queueIdentity, cancellationToken).GetAwaiter().GetResult(); }
        catch (OperationCanceledException error)
        {
            throw new PrintSubmissionException("GDI submission was canceled before StartDoc.", mayHaveReachedOs: false, error);
        }
    }

    private static void ThrowIfCancellationRequestedBeforeStartDoc(CancellationToken cancellationToken)
    {
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException error)
        {
            throw new PrintSubmissionException("GDI submission was canceled before StartDoc.", mayHaveReachedOs: false, error);
        }
    }

    public async Task<PrintBackendJobStatus> GetStatusAsync(PaperBatch.Core.Persistence.OsJobReceipt receipt, CancellationToken cancellationToken = default)
    {
        var (snapshot, printer) = await OpenReceiptQueueAsync(receipt, cancellationToken).ConfigureAwait(false);
        _ = snapshot;
        using (printer)
        {
            if (!uint.TryParse(receipt.OsJobId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var jobId))
                return PrintBackendJobStatus.Unknown;
            var job = ReadJob(printer, jobId);
            if (job is null || !string.Equals(job.Value.DocumentName, DocumentName(receipt.QueueIdentity, receipt.RunNonce), StringComparison.Ordinal)) return PrintBackendJobStatus.Unknown;
            return MapWinspoolJobStatus(job.Value.Status);
        }
    }

    public async Task<bool> CancelAsync(PaperBatch.Core.Persistence.OsJobReceipt receipt, CancellationToken cancellationToken = default)
    {
        var gate = CancelGates.GetOrAdd(receipt.QueueIdentity, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, printer) = await OpenReceiptQueueAsync(receipt, cancellationToken).ConfigureAwait(false);
            using (printer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!uint.TryParse(receipt.OsJobId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var jobId)) return false;
                // Serialize our queue-scoped callers and make the nonce check the last Winspool
                // read before SetJob. Winspool has no compare-and-cancel primitive: a spooler-side
                // job removal/ID reuse between these calls remains an unavoidable TOCTOU window.
                var job = ReadJob(printer, jobId);
                if (job is null || !string.Equals(job.Value.DocumentName, DocumentName(receipt.QueueIdentity, receipt.RunNonce), StringComparison.Ordinal)) return false;
                if (!SetJob(printer, jobId, 0, IntPtr.Zero, 3))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 87 || error == 1168) return false;
                    throw new Win32Exception(error, "The owned print job could not be cancelled.");
                }
                return true;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<(PrinterCapabilitySnapshot Snapshot, SafePrinterHandle Printer)> OpenReceiptQueueAsync(
        PaperBatch.Core.Persistence.OsJobReceipt receipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await GetCapabilitiesAsync(receipt.QueueIdentity, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.QueueIdentity, receipt.QueueIdentity, StringComparison.Ordinal) ||
            (receipt.DriverName is not null && !string.Equals(receipt.DriverName, snapshot.DriverName, StringComparison.Ordinal)) ||
            (receipt.PortName is not null && !string.Equals(receipt.PortName, snapshot.PortName, StringComparison.Ordinal)))
            throw new InvalidOperationException("The receipt no longer resolves to the same local queue/driver/port identity.");
        if (!OpenPrinter(snapshot.DisplayName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The receipt's exact printer queue could not be opened.");
        try
        {
            var openedQueue = await GetCapabilitiesAsync(receipt.QueueIdentity, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(openedQueue.QueueIdentity, snapshot.QueueIdentity, StringComparison.Ordinal) ||
                !string.Equals(openedQueue.DriverName, snapshot.DriverName, StringComparison.Ordinal) ||
                !string.Equals(openedQueue.PortName, snapshot.PortName, StringComparison.Ordinal))
                throw new InvalidOperationException("The printer queue changed while its native handle was being opened.");
            return (openedQueue, printer);
        }
        catch { printer.Dispose(); throw; }
    }

    private static NativeSettings ResolveSettings(PrinterCapabilitySnapshot snapshot, WindowsPrintSubmission submission,
        IReadOnlyList<WindowsPrintRasterPage> pages)
    {
        var settings = submission.Segment.PrintSettings;
        if (!snapshot.NativeTokens.TryGetValue(PrintOption.Media, out var media) || !media.TryGetValue(settings.Media, out var mediaIdText) ||
            !short.TryParse(mediaIdText, out var paper))
            throw new PrintSubmissionException("The selected media has no current DEVMODE paper identifier.", false);
        short? tray = null;
        if (settings.TrayId is { } trayName &&
            (!snapshot.NativeTokens.TryGetValue(PrintOption.Tray, out var trays) || !trays.TryGetValue(trayName, out var trayText) || !short.TryParse(trayText, out var parsedTray)))
            throw new PrintSubmissionException("The selected tray has no current DEVMODE bin identifier.", false);
        else if (settings.TrayId is not null) tray = short.Parse(snapshot.NativeTokens[PrintOption.Tray][settings.TrayId], System.Globalization.CultureInfo.InvariantCulture);
        var color = submission.Options.Color == "Color" ? Color : Monochrome;
        var duplex = submission.Options.Duplex switch
        {
            "Simplex" => Simplex,
            "LongEdge" => LongEdge,
            "ShortEdge" => ShortEdge,
            _ => throw new PrintSubmissionException("The selected duplex mode has no DEVMODE mapping.", false)
        };
        var copies = settings.Copies;
        if (!snapshot.NativeTokens.TryGetValue(PrintOption.Copies, out var copyTokens) ||
            !copyTokens.TryGetValue("Maximum", out var maximumText) || !int.TryParse(maximumText, out var maximum) ||
            copies < 1 || copies > Math.Min(maximum, 999))
            throw new PrintSubmissionException("The selected copy count is not supported by the current driver DEVMODE.", false);
        var orientation = settings.Orientation switch
        {
            Domain.Orientation.Portrait => Portrait,
            Domain.Orientation.Landscape => Landscape,
            _ => pages[0].PdfSize.WidthPoints > pages[0].PdfSize.HeightPoints ? Landscape : Portrait
        };
        return new NativeSettings(orientation, paper, tray, checked((short)copies), color, duplex);
    }

    private static System.Collections.Immutable.ImmutableDictionary<string, string> MapIds(PrinterCapabilitySnapshot snapshot, PrintOption option, uint flag, int elementBytes)
    {
        var capability = snapshot.Options.GetValueOrDefault(option);
        if (capability is null || capability.State != CapabilityState.Supported) return System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
        var count = DeviceCapabilities(snapshot.DisplayName, snapshot.PortName, flag, IntPtr.Zero, IntPtr.Zero);
        if (count is <= 0 or > MaximumDeviceValues || count != capability.Values.Length) return System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
        var ids = Marshal.AllocHGlobal(checked(count * elementBytes));
        try
        {
            if (DeviceCapabilities(snapshot.DisplayName, snapshot.PortName, flag, ids, IntPtr.Zero) != count)
                return System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
            var result = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var value = snapshot.Options[option].Values[index];
                if (result.ContainsKey(value)) return System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
                result.Add(value, Marshal.ReadInt16(ids, checked(index * elementBytes)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return result.ToImmutable();
        }
        finally { Marshal.FreeHGlobal(ids); }
    }

    private static void DisableIfUnmapped(
        System.Collections.Immutable.ImmutableDictionary<PrintOption, PrintOptionCapability>.Builder options,
        PrinterCapabilitySnapshot snapshot,
        PrintOption option,
        System.Collections.Immutable.ImmutableDictionary<string, string> nativeIds)
    {
        if (snapshot.Options.TryGetValue(option, out var capability) && capability.State == CapabilityState.Supported &&
            !HasExactNativeMapping(capability.Values, nativeIds))
            options[option] = new(CapabilityState.Unavailable, [], $"The driver reported {option} names but its native identifiers could not be mapped exactly.");
    }

    internal static bool HasRequiredDevModeFields(uint actualFields, uint requiredFields) =>
        (actualFields & requiredFields) == requiredFields;

    internal static PrintBackendJobStatus MapWinspoolJobStatus(uint status)
    {
        if ((status & JobStatusDeleted) != 0) return PrintBackendJobStatus.Unknown;
        if ((status & (0x00000002u | 0x00000040u)) != 0) return PrintBackendJobStatus.Failed;
        if ((status & JobStatusCancelled) != 0) return PrintBackendJobStatus.Cancelled;
        if ((status & (0x00000004u | 0x00000010u | 0x00000020u | 0x00000008u)) != 0) return PrintBackendJobStatus.Processing;
        if ((status & (0x00000080u | 0x00001000u)) != 0) return PrintBackendJobStatus.ReportedComplete;
        return PrintBackendJobStatus.Accepted;
    }

    internal static bool HasExactNativeMapping(
        IReadOnlyCollection<string> capabilityValues,
        System.Collections.Immutable.ImmutableDictionary<string, string> nativeIds) =>
        capabilityValues.Count == nativeIds.Count && capabilityValues.All(nativeIds.ContainsKey);

    internal static PrintOptionCapability MapDuplexCapability(int deviceCapabilitiesResult) => deviceCapabilitiesResult switch
    {
        < 0 => new(CapabilityState.Unavailable, [], "The driver did not report duplex capability."),
        0 => new(CapabilityState.Supported, ["Simplex"], null),
        _ => new(CapabilityState.Supported, ["Simplex", "LongEdge", "ShortEdge"], null)
    };

    private static (uint Status, string? DocumentName)? ReadJob(SafePrinterHandle printer, uint jobId)
    {
        _ = GetJob(printer, jobId, 1, IntPtr.Zero, 0, out var needed);
        if (needed == 0) return null;
        if (needed > 1024 * 1024) throw new InvalidDataException("Winspool returned an oversized job record.");
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!GetJob(printer, jobId, 1, buffer, needed, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87 || error == 1168) return null;
                throw new Win32Exception(error, "The owned print job status could not be read.");
            }
            var job = Marshal.PtrToStructure<JobInfo1>(buffer);
            return (job.Status, job.DocumentName == IntPtr.Zero ? null : Marshal.PtrToStringUni(job.DocumentName));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static PrintSubmissionException NativeFailure(string message, bool mayHaveReachedOs) =>
        new(message, mayHaveReachedOs, new Win32Exception(Marshal.GetLastWin32Error()));

    private static string DocumentName(string queueIdentity, string runNonce)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(queueIdentity + "\0" + runNonce));
        return "PB-" + Convert.ToHexString(digest.AsSpan(0, 20));
    }

    internal static bool IsSelectedPage(IReadOnlyList<WindowsPrintRasterPage> pages, WindowsPrintRasterPage page) =>
        pages.Any(selectedPage => ReferenceEquals(selectedPage, page));

    private sealed record NativeSettings(int Orientation, short Paper, short? Tray, short Copies, int Color, int Duplex);

    private sealed class GdiDocument(IntPtr dc, SafePrinterHandle printer, IntPtr mode, int jobId,
        IReadOnlyList<WindowsPrintRasterPage> pages, Domain.ScalingMode scalingMode) : IWindowsGdiDocument
    {
        private bool _disposed;
        private bool _ended;
        public string OsJobId => jobId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public Task StartPageAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartPage(dc) <= 0) throw NativeFailure("GDI StartPage failed after StartDoc.", true);
            return Task.CompletedTask;
        }
        public Task WritePageAsync(WindowsPrintRasterPage page, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Number <= 0 || !IsSelectedPage(pages, page))
                throw new PrintSubmissionException("The page does not belong to this exact GDI document.", true);
            var width = GetDeviceCaps(dc, 8);
            var height = GetDeviceCaps(dc, 10);
            if (width <= 0 || height <= 0) throw NativeFailure("The printer returned invalid printable geometry after StartDoc.", true);
            var dpiX = GetDeviceCaps(dc, 88);
            var dpiY = GetDeviceCaps(dc, 90);
            if (dpiX <= 0 || dpiY <= 0) throw NativeFailure("The printer did not report a usable GDI resolution.", true);
            var naturalWidth = page.PdfSize.WidthPoints / 72d * dpiX;
            var naturalHeight = page.PdfSize.HeightPoints / 72d * dpiY;
            if (!double.IsFinite(naturalWidth) || !double.IsFinite(naturalHeight) || naturalWidth <= 0 || naturalHeight <= 0 ||
                naturalWidth > int.MaxValue || naturalHeight > int.MaxValue)
                throw new PrintSubmissionException("The final PDF page exceeds the bounded GDI destination size.", true);
            var scale = scalingMode == Domain.ScalingMode.ShrinkToFit
                ? Math.Min(1d, Math.Min((double)width / naturalWidth, (double)height / naturalHeight))
                : 1d;
            var destWidth = Math.Max(1, (int)Math.Round(naturalWidth * scale));
            var destHeight = Math.Max(1, (int)Math.Round(naturalHeight * scale));
            var destX = (width - destWidth) / 2;
            var destY = (height - destHeight) / 2;
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = page.Raster.Width,
                Height = -page.Raster.Height, Planes = 1, BitCount = 32, Compression = BiRgb,
                SizeImage = checked((uint)page.Raster.BgraPixels.Length)
            };
            var info = new BitmapInfo { Header = header, Colors = 0 };
            var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BitmapInfo>());
            var pixels = GCHandle.Alloc(page.Raster.BgraPixels, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                var result = StretchDIBits(dc, destX, destY, destWidth, destHeight, 0, 0, page.Raster.Width, page.Raster.Height,
                    pixels.AddrOfPinnedObject(), infoPtr, DibRgbColors, SrcCopy);
                if (result == GdiError || result == 0) throw NativeFailure("GDI could not rasterize a page to the selected queue.", true);
            }
            finally { pixels.Free(); Marshal.FreeHGlobal(infoPtr); }
            return Task.CompletedTask;
        }
        public Task EndPageAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EndPage(dc) <= 0) throw NativeFailure("GDI EndPage failed after StartDoc.", true);
            return Task.CompletedTask;
        }
        public Task EndDocAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EndDoc(dc) <= 0) throw NativeFailure("GDI EndDoc failed; the spool outcome is unknown.", true);
            _ended = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            var abortFailed = !_ended && AbortDoc(dc) <= 0;
            var deleteFailed = !DeleteDC(dc);
            if (mode != IntPtr.Zero) Marshal.FreeHGlobal(mode);
            printer.Dispose();
            if (abortFailed || deleteFailed) throw NativeFailure("GDI document cleanup failed after StartDoc.", true);
            return ValueTask.CompletedTask;
        }
    }

    private static bool OpenPrinter(string name, out SafePrinterHandle printer, IntPtr defaults)
    {
        if (OpenPrinterNative(name, out var handle, defaults)) { printer = new SafePrinterHandle(handle); return true; }
        printer = new SafePrinterHandle(IntPtr.Zero);
        return false;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinterNative(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int DocumentProperties(IntPtr hwnd, SafePrinterHandle printer, string name, IntPtr output, IntPtr input, int mode);
    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int DeviceCapabilities(string name, string port, uint capability, IntPtr output, IntPtr devMode);
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initData);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDoc(IntPtr dc, ref DocInfo info);
    [DllImport("gdi32.dll", EntryPoint = "StartPage", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "EndPage", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "EndDoc", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "AbortDoc", SetLastError = true)] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetDeviceCaps", SetLastError = true)] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll", EntryPoint = "StretchDIBits", SetLastError = true)] private static extern int StretchDIBits(IntPtr dc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc, int srcWidth, int srcHeight, IntPtr bits, IntPtr info, uint usage, uint rop);
    [DllImport("winspool.drv", EntryPoint = "GetJobW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetJob(SafePrinterHandle printer, uint jobId, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("winspool.drv", EntryPoint = "SetJobW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetJob(SafePrinterHandle printer, uint jobId, uint level, IntPtr info, uint command);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DocInfo { public int Size; public IntPtr DocumentName; public IntPtr Output; public IntPtr DataType; public uint Type; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public short Orientation, PaperSize, PaperLength, PaperWidth, Scale, Copies, DefaultSource, PrintQuality, Color, Duplex;
        public short YResolution, TrueTypeOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct JobInfo1 { public uint JobId; public IntPtr PrinterName, MachineName, UserName, DocumentName, DataType, StatusText; public uint Status, Priority, Position, TotalPages, PagesPrinted; public SystemTime Submitted; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    private sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePrinterHandle(IntPtr handle) : base(true) => SetHandle(handle);
        public SafePrinterHandle() : this(IntPtr.Zero) { }
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }
}
