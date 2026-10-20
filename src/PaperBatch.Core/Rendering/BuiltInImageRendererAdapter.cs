using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Rendering;

/// <summary>Creates one-page PDFs from bounded baseline JPEG and non-interlaced RGB/gray PNG snapshots.</summary>
public sealed class BuiltInImageRendererAdapter(SourceSnapshotCache snapshots, RenderArtifactStore artifacts) : IRendererAdapter
{
    private const int MaximumImageBytes = 128 * 1024 * 1024;
    private const long MaximumPixels = 100_000_000;

    public ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.ExecutionContext == RendererExecutionContext.BackgroundWorker &&
            request.Format is SourceFormat.Jpeg or SourceFormat.Png);
    }

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (request.Format is not (SourceFormat.Jpeg or SourceFormat.Png))
            return RenderResult.Unsupported("The built-in image renderer supports JPEG and PNG only.");
        try
        {
            using var source = snapshots.OpenRead(request.SnapshotId);
            if (source.Length is <= 0 or > MaximumImageBytes)
                return RenderResult.Unsupported("The image exceeds the built-in renderer size limit.");
            var extension = request.Format == SourceFormat.Jpeg ? ".jpg" : ".png";
            var preflight = SourceFormatPreflight.Inspect(source, extension);
            if (preflight.Status != SourcePreflightStatus.Ready)
                return RenderResult.Failed("The image snapshot failed structural safety checks.");
            source.Position = 0;
            using var input = new MemoryStream(checked((int)source.Length));
            await source.CopyToAsync(input, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var pdf = EncodePdf(input.GetBuffer().AsSpan(0, checked((int)input.Length)), request.Format);
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream(pdf, writable: false);
            var receipt = await artifacts.PersistPdfAsync(output, "paperbatch.builtin-image", "1",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source-format"] = request.Format.ToString(),
                    ["image-resolution"] = "96dpi",
                    ["alpha"] = "unsupported",
                    ["jpeg-orientation"] = "metadata-orientation-1-required"
                }, cancellationToken).ConfigureAwait(false);
            return new RenderResult(RenderStatus.Succeeded,
                new RenderArtifact(receipt.Artifact.ArtifactId, receipt.Artifact.Sha256, receipt.Artifact.LengthBytes, 1), null);
        }
        catch (NotSupportedException exception)
        {
            return RenderResult.Unsupported(exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RenderResult(RenderStatus.Cancelled, null, "Image rendering was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException or ArgumentException)
        {
            return RenderResult.Failed("The image snapshot could not be rendered safely.");
        }
    }

    private static byte[] EncodePdf(ReadOnlySpan<byte> image, SourceFormat format)
    {
        var data = format switch
        {
            SourceFormat.Jpeg => ReadJpeg(image),
            SourceFormat.Png => ReadPng(image),
            _ => throw new NotSupportedException("Only JPEG and PNG image sources can be converted by the built-in renderer.")
        };
        if (data.Width <= 0 || data.Height <= 0 || checked((long)data.Width * data.Height) > MaximumPixels)
            throw new InvalidDataException("The image dimensions exceed the renderer limit.");

        var pageWidth = data.Width * 0.75;
        var pageHeight = data.Height * 0.75;
        if (pageWidth > 14_400 || pageHeight > 14_400)
            throw new NotSupportedException("The image dimensions exceed the supported PDF page size.");
        var content = Encoding.ASCII.GetBytes(FormattableString.Invariant($"q {pageWidth:0.###} 0 0 {pageHeight:0.###} 0 0 cm /Im0 Do Q\n"));
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth:0.###} {pageHeight:0.###}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>")),
            Join(Ascii(data.ImageDictionary), Ascii($"\nstream\n"), data.EncodedBytes, Ascii("\nendstream")),
            Join(Ascii($"<< /Length {content.Length} >>\nstream\n"), content, Ascii("endstream"))
        };
        return BuildPdf(objects);
    }

    private static ImageData ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) throw new InvalidDataException("The JPEG header is invalid.");
        var offset = 2;
        var width = 0;
        var height = 0;
        var components = 0;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset++] != 0xff) continue;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) throw new InvalidDataException("The JPEG segment is truncated.");
            if (marker is 0xc0 or 0xc2)
            {
                if (length < 8) throw new InvalidDataException("The JPEG frame header is invalid.");
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                components = bytes[offset + 7];
                break;
            }
            if (marker == 0xe1 && length >= 16 && bytes.Slice(offset + 2, 6).SequenceEqual("Exif\0\0"u8))
            {
                var orientation = ReadExifOrientation(bytes.Slice(offset + 8, length - 8));
                if (orientation != 1) throw new NotSupportedException("Invalid JPEG EXIF orientation or orientation changes are not supported by the built-in renderer.");
            }
            offset += length;
        }
        if (width <= 0 || height <= 0 || components is not (1 or 3)) throw new NotSupportedException("Only grayscale and RGB baseline JPEG images are supported.");
        var colorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB";
        var dictionary = FormattableString.Invariant($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {bytes.Length} >>");
        return new ImageData(width, height, dictionary, bytes.ToArray());
    }

    private static int ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 0;
        var little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M')) return 0;
        if (ReadTiffUInt16(tiff, 2, little) != 42) return 0;
        var ifd = ReadTiffUInt32(tiff, 4, little);
        if (ifd > int.MaxValue || ifd + 2 > tiff.Length) return 0;
        var start = (int)ifd;
        var count = ReadTiffUInt16(tiff, start, little);
        for (var index = 0; index < count; index++)
        {
            var entry = checked(start + 2 + index * 12);
            if (entry + 12 > tiff.Length) return 0;
            if (ReadTiffUInt16(tiff, entry, little) == 0x0112 && ReadTiffUInt16(tiff, entry + 2, little) == 3 &&
                ReadTiffUInt32(tiff, entry + 4, little) == 1) return ReadTiffUInt16(tiff, entry + 8, little);
        }
        return 1;
    }

    private static ushort ReadTiffUInt16(ReadOnlySpan<byte> data, int index, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(index, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));

    private static uint ReadTiffUInt32(ReadOnlySpan<byte> data, int index, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(index, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(index, 4));

    private static ImageData ReadPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The PNG signature is invalid.");
        var width = 0;
        var height = 0;
        var color = -1;
        var bitDepth = 0;
        var interlace = 0;
        using var idat = new MemoryStream();
        var offset = 8;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > bytes.Length) throw new InvalidDataException("The PNG chunk is truncated.");
            var type = bytes.Slice(offset + 4, 4);
            var payload = bytes.Slice(offset + 8, (int)length);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13) throw new InvalidDataException("The PNG header chunk is invalid.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
                bitDepth = payload[8]; color = payload[9]; interlace = payload[12];
                if (payload[10] != 0 || payload[11] != 0) throw new InvalidDataException("The PNG compression or filter method is unsupported.");
            }
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(payload);
            else if (type.SequenceEqual("IEND"u8)) break;
            offset = checked(offset + 12 + (int)length);
        }
        if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0 || color is not (0 or 2))
            throw new NotSupportedException("The built-in PNG renderer supports non-interlaced 8-bit grayscale and RGB images without alpha.");
        var channels = color == 0 ? 1 : 3;
        var rowBytes = checked(width * channels + 1);
        if (checked((long)rowBytes * height) > MaximumImageBytes) throw new InvalidDataException("The PNG pixel stream exceeds its size limit.");
        var compressed = idat.ToArray();
        ValidatePngRasterData(compressed, height, rowBytes);
        var imageDictionary = FormattableString.Invariant($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {(color == 0 ? "/DeviceGray" : "/DeviceRGB")} /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {channels} /BitsPerComponent 8 /Columns {width} >> /Length {compressed.Length} >>");
        return new ImageData(width, height, imageDictionary, compressed);
    }


    private static void ValidatePngRasterData(byte[] compressed, int height, int rowBytes)
    {
        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var decoder = new ZLibStream(compressedStream, CompressionMode.Decompress);
        var row = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var offset = 0;
            while (offset < row.Length)
            {
                var read = decoder.Read(row, offset, row.Length - offset);
                if (read == 0) throw new InvalidDataException("The PNG compressed raster is truncated.");
                offset += read;
            }
            if (row[0] > 4) throw new InvalidDataException("The PNG raster contains an unsupported row filter.");
        }
        if (decoder.ReadByte() != -1) throw new InvalidDataException("The PNG compressed raster exceeds its declared dimensions.");
    }

    private static byte[] BuildPdf(IReadOnlyList<byte[]> objects)
    {
        using var output = new MemoryStream();
        Write(output, Ascii("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new long[objects.Count + 1];
        for (var index = 0; index < objects.Count; index++)
        {
            offsets[index + 1] = output.Position;
            Write(output, Ascii($"{index + 1} 0 obj\n")); Write(output, objects[index]); Write(output, Ascii("\nendobj\n"));
        }
        var xref = output.Position;
        Write(output, Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        for (var index = 1; index < offsets.Length; index++)
            Write(output, Ascii(offsets[index].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"));
        Write(output, Ascii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    private static byte[] Join(params byte[][] parts)
    {
        using var output = new MemoryStream();
        foreach (var part in parts) output.Write(part);
        return output.ToArray();
    }
    private static void Write(Stream output, byte[] bytes) => output.Write(bytes, 0, bytes.Length);
    private sealed record ImageData(int Width, int Height, string ImageDictionary, byte[] EncodedBytes);
}
