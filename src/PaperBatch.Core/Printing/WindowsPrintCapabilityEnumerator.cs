using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PaperBatch.Core.Printing;

/// <summary>Read-only local Winspool inventory. It never opens a job or changes queue defaults.</summary>
public static class WindowsPrintCapabilityEnumerator
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const int MaximumEnumeratedValues = 4096;
    private const ushort DcPapers = 2;
    private const ushort DcBins = 6;
    private const ushort DcColorDevice = 32;
    private const ushort DcPaperNames = 16;
    private const ushort DcBinNames = 12;
    private const ushort DcEnumResolutions = 13;

    public static ImmutableArray<PrinterCapabilitySnapshot> EnumerateLocalQueues()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local print capability enumeration requires Windows.");

        _ = EnumPrinters(PrinterEnumLocal, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(PrinterEnumLocal, null, 2, buffer, needed, out _, out var returned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Local print queue enumeration failed.");
            var size = Marshal.SizeOf<PrinterInfo2>();
            var result = ImmutableArray.CreateBuilder<PrinterCapabilitySnapshot>(checked((int)returned));
            for (var index = 0; index < returned; index++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buffer, checked((int)index * size)));
                var name = ReadString(info.PrinterName);
                var driver = ReadString(info.DriverName);
                var port = ReadString(info.PortName);
                if (name is null || driver is null || port is null) continue;
                result.Add(Query(name, driver, port));
            }
            return result.ToImmutable();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static PrinterCapabilitySnapshot GetCapabilities(string queueIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueIdentity);
        return EnumerateLocalQueues().FirstOrDefault(queue =>
            string.Equals(queue.QueueIdentity, queueIdentity, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("The selected local printer queue is no longer present.");
    }

    private static PrinterCapabilitySnapshot Query(string name, string driver, string port)
    {
        var options = ImmutableDictionary.CreateBuilder<PrintOption, PrintOptionCapability>();
        options[PrintOption.Media] = ReadNames(name, port, DcPapers, DcPaperNames, 64, "media types");
        options[PrintOption.Tray] = ReadNames(name, port, DcBins, DcBinNames, 24, "trays");
        options[PrintOption.Color] = QueryColor(name, port);
        options[PrintOption.Duplex] = new(CapabilityState.Unknown, [], "Winspool's legacy duplex flag does not identify the supported edge modes.");
        options[PrintOption.Resolution] = ReadResolutions(name, port);
        options[PrintOption.Finishing] = new(CapabilityState.Unknown, [], "This driver did not provide a verified finishing-option inventory.");
        var identity = Digest($"{name}\0{driver}\0{port}");
        var fingerprintData = string.Join("\n", options.OrderBy(pair => pair.Key).Select(pair =>
            $"{pair.Key}:{pair.Value.State}:{string.Join(',', pair.Value.Values)}:{pair.Value.Detail}"));
        var fingerprint = Digest($"{identity}\n{fingerprintData}");
        return new PrinterCapabilitySnapshot(identity, name, driver, port, fingerprint, options.ToImmutable());
    }

    private static PrintOptionCapability ReadNames(string name, string port, ushort countFlag, ushort namesFlag, int width, string label)
    {
        var count = DeviceCapabilities(name, port, countFlag, IntPtr.Zero, IntPtr.Zero);
        if (count < 0) return new(CapabilityState.Unavailable, [], $"The driver did not return {label}.");
        if (count == 0) return new(CapabilityState.Unsupported, [], $"The driver reports no {label}.");
        if (count > MaximumEnumeratedValues) return new(CapabilityState.Unknown, [], $"The driver returned too many {label} to inspect safely.");
        var bytes = checked((int)count * width * sizeof(char));
        var memory = Marshal.AllocHGlobal(bytes);
        try
        {
            if (DeviceCapabilities(name, port, namesFlag, memory, IntPtr.Zero) < 0)
                return new(CapabilityState.Unavailable, [], $"The driver could not enumerate {label}.");
            var values = Enumerable.Range(0, count)
                .Select(index => Marshal.PtrToStringUni(IntPtr.Add(memory, checked(index * width * sizeof(char))))?.TrimEnd('\0'))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            return values.IsEmpty
                ? new(CapabilityState.Unknown, [], $"The driver returned empty {label} names.")
                : new(CapabilityState.Supported, values, null);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static PrintOptionCapability QueryColor(string name, string port)
    {
        var result = DeviceCapabilities(name, port, DcColorDevice, IntPtr.Zero, IntPtr.Zero);
        return result switch
        {
            1 => new(CapabilityState.Supported, ["Color"], "Color mode was reported; monochrome support is not inferred."),
            0 => new(CapabilityState.Supported, ["Monochrome"], null),
            _ => new(CapabilityState.Unavailable, [], "The driver did not report color-device capability.")
        };
    }

    private static PrintOptionCapability ReadResolutions(string name, string port)
    {
        var count = DeviceCapabilities(name, port, DcEnumResolutions, IntPtr.Zero, IntPtr.Zero);
        if (count < 0) return new(CapabilityState.Unavailable, [], "The driver did not report resolutions.");
        if (count == 0) return new(CapabilityState.Unsupported, [], "The driver reports no discrete resolutions.");
        if (count > MaximumEnumeratedValues) return new(CapabilityState.Unknown, [], "The driver returned too many resolutions to inspect safely.");
        var memory = Marshal.AllocHGlobal(checked((int)count * 2 * sizeof(int)));
        try
        {
            if (DeviceCapabilities(name, port, DcEnumResolutions, memory, IntPtr.Zero) < 0)
                return new(CapabilityState.Unavailable, [], "The driver could not enumerate resolutions.");
            var values = ImmutableArray.CreateBuilder<string>(count);
            for (var index = 0; index < count; index++)
            {
                var x = Marshal.ReadInt32(memory, checked(index * 2 * sizeof(int)));
                var y = Marshal.ReadInt32(memory, checked((index * 2 + 1) * sizeof(int)));
                if (x > 0 && y > 0) values.Add($"{x}x{y}");
            }
            return values.Count == 0
                ? new(CapabilityState.Unknown, [], "The driver returned invalid resolution values.")
                : new(CapabilityState.Supported, values.Distinct(StringComparer.Ordinal).ToImmutableArray(), null);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string? ReadString(IntPtr pointer) => pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint bufferSize, out uint needed, out uint returned);

    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int DeviceCapabilities(string deviceName, string portName, ushort capability, IntPtr output, IntPtr devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
        public uint Jobs;
        public uint AveragePpm;
    }
}
