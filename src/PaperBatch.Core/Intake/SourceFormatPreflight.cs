using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PaperBatch.Core.Intake;

public enum SourceFormat
{
    Unknown,
    Pdf,
    Jpeg,
    Png,
    Tiff,
    Doc,
    Docx,
    Rtf,
    Xls,
    Xlsx,
    Ppt,
    Pptx,
    Odt,
    Ods,
    Odp
}

public enum SourcePreflightStatus
{
    Ready,
    Rejected
}

public sealed record SourcePreflightOptions
{
    public long MaximumSourceBytes { get; init; } = 512L * 1024 * 1024;
    public int MaximumPackageEntries { get; init; } = 10_000;
    public long MaximumPackageMetadataBytes { get; init; } = 4L * 1024 * 1024;
    public long MaximumUncompressedPackageBytes { get; init; } = 1024L * 1024 * 1024;
    public double MaximumPackageCompressionRatio { get; init; } = 1000;
    public long MaximumRasterPixels { get; init; } = 500_000_000;
}

public sealed record SourcePreflightResult(
    SourceFormat Format,
    string? MediaType,
    SourcePreflightStatus Status,
    ImmutableArray<IntakeDiagnostic> Diagnostics);

/// <summary>Bounded structural intake checks; it never renders or extracts Office package contents.</summary>
public static class SourceFormatPreflight
{
    private const int MaximumPdfHeaderBytes = 1024;
    private const int MaximumPdfTailBytes = 65_557;
    private const int MaximumPdfXrefBytes = 4 * 1024 * 1024;
    private const int MaximumPdfXrefEntries = 100_000;
    private const int MaximumPdfPreviousXrefSections = 64;
    private const int MaximumPdfObjectDepth = 128;
    private const uint ZipLocalHeader = 0x04034b50;
    private const uint ZipCentralHeader = 0x02014b50;
    private const uint ZipEndOfCentralDirectory = 0x06054b50;
    private const int MaximumTiffArrayValues = 100_000;
    private const int MaximumTiffTotalArrayValues = 200_000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SourcePreflightResult Inspect(Stream input, string extension, SourcePreflightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(extension);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException("Preflight requires a readable, seekable snapshot stream.", nameof(input));
        }

        options ??= new SourcePreflightOptions();
        if (options.MaximumSourceBytes <= 0 || options.MaximumPackageEntries <= 0 ||
            options.MaximumPackageMetadataBytes <= 0 || options.MaximumUncompressedPackageBytes <= 0 ||
            !double.IsFinite(options.MaximumPackageCompressionRatio) || options.MaximumPackageCompressionRatio <= 0 ||
            options.MaximumRasterPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preflight limits must be positive.");
        }

        var diagnostics = ImmutableArray.CreateBuilder<IntakeDiagnostic>();
        var length = input.Length;
        if (length == 0)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.EmptySource, "The selected source is empty."));
            return Result(SourceFormatForExtension(extension), SourcePreflightStatus.Rejected, diagnostics);
        }

        if (length > options.MaximumSourceBytes)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The source exceeds the configured preflight size limit."));
            return Result(SourceFormatForExtension(extension), SourcePreflightStatus.Rejected, diagnostics);
        }

        var extensionToken = extension.Trim().StartsWith(".", StringComparison.Ordinal)
            ? extension.Trim()
            : Path.GetExtension(extension);
        var expected = SourceFormatForExtension(extensionToken);
        if (expected == SourceFormat.Unknown)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.UnsupportedFormat, "The source extension is not in the supported intake inventory."));
            return Result(SourceFormat.Unknown, SourcePreflightStatus.Rejected, diagnostics);
        }

        var detected = DetectSignature(input, length);
        if (detected == SourceFormat.Doc && expected is SourceFormat.Xls or SourceFormat.Ppt)
        {
            // The legacy formats share the OLE compound-file signature; the selected extension distinguishes the family.
            detected = expected;
        }
        else if (detected == SourceFormat.Docx && expected is SourceFormat.Docx or SourceFormat.Xlsx or SourceFormat.Pptx or SourceFormat.Odt or SourceFormat.Ods or SourceFormat.Odp)
        {
            // OOXML and OpenDocument containers share the ZIP signature; bounded package entries validate the family below.
            detected = expected;
        }
        if (detected == SourceFormat.Unknown)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The source does not have a recognized bounded signature or container."));
            return Result(expected, SourcePreflightStatus.Rejected, diagnostics);
        }

        if (expected != detected)
        {
            if (detected == SourceFormat.Doc && expected is (SourceFormat.Docx or SourceFormat.Xlsx or SourceFormat.Pptx) &&
                LegacyOfficeContainerInspector.HasEncryptedPackageStreams(input, length, options.MaximumPackageMetadataBytes))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.EncryptedOrPasswordProtected,
                    "The Office compound file contains both encrypted-package metadata streams."));
            }
            else
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.ExtensionSignatureMismatch,
                    "The source extension does not match its detected file signature."));
            }

            return Result(detected, SourcePreflightStatus.Rejected, diagnostics);
        }

        var valid = expected switch
        {
            SourceFormat.Pdf => ValidatePdf(input, length, diagnostics),
            SourceFormat.Jpeg => ValidateJpeg(input, length, options, diagnostics),
            SourceFormat.Png => ValidatePng(input, length, options, diagnostics),
            SourceFormat.Tiff => ValidateTiff(input, length, options, diagnostics),
            SourceFormat.Rtf => ValidateRtf(input, length, diagnostics),
            SourceFormat.Doc or SourceFormat.Xls or SourceFormat.Ppt => ValidateLegacyOffice(input, length, expected, options, diagnostics),
            SourceFormat.Docx or SourceFormat.Xlsx or SourceFormat.Pptx or SourceFormat.Odt or SourceFormat.Ods or SourceFormat.Odp =>
                ValidatePackage(input, length, expected, options, diagnostics),
            _ => false
        };

        return Result(expected, valid && diagnostics.All(diagnostic => diagnostic.Severity != IntakeSeverity.Error)
            ? SourcePreflightStatus.Ready
            : SourcePreflightStatus.Rejected, diagnostics);
    }

    private static SourceFormat DetectSignature(Stream input, long length)
    {
        var header = ReadAtMost(input, 0, (int)Math.Min(length, MaximumPdfHeaderBytes));
        if (header.AsSpan().IndexOf("%PDF-"u8) >= 0) return SourceFormat.Pdf;
        if (header.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 })) return SourceFormat.Jpeg;
        if (header.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return SourceFormat.Png;
        if (header.Length >= 4 &&
            ((header[0] == (byte)'I' && header[1] == (byte)'I') || (header[0] == (byte)'M' && header[1] == (byte)'M')) &&
            ReadTiffMagic(header) is 42 or 43)
        {
            return SourceFormat.Tiff;
        }

        if (header.AsSpan().StartsWith("{\\rtf"u8)) return SourceFormat.Rtf;
        if (header.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) return SourceFormat.Doc;
        if (header.AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 0x03, 0x04 }) || header.AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 0x05, 0x06 }))
        {
            return SourceFormat.Docx;
        }

        return SourceFormat.Unknown;
    }

    private static bool ValidatePdf(Stream input, long length, ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        var header = ReadAtMost(input, 0, (int)Math.Min(length, MaximumPdfHeaderBytes));
        if (header.AsSpan().IndexOf("%PDF-"u8) < 0)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PDF header is missing."));
            return false;
        }

        var tail = ReadTail(input, length, MaximumPdfTailBytes);
        var tailText = Encoding.Latin1.GetString(tail);
        var eof = tailText.LastIndexOf("%%EOF", StringComparison.Ordinal);
        var startXref = tailText.LastIndexOf("startxref", StringComparison.Ordinal);
        if (eof < 0 || startXref < 0 || startXref > eof)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PDF trailer or terminal EOF marker is missing."));
            return false;
        }

        var valueIndex = startXref + "startxref".Length;
        while (valueIndex < eof && char.IsWhiteSpace(tailText[valueIndex])) valueIndex++;
        var valueEnd = valueIndex;
        while (valueEnd < eof && char.IsAsciiDigit(tailText[valueEnd])) valueEnd++;
        if (valueEnd == valueIndex || (valueEnd < eof && !IsPdfDelimiter((byte)tailText[valueEnd])) ||
            !long.TryParse(tailText.AsSpan(valueIndex, valueEnd - valueIndex), out var xrefOffset) || xrefOffset < 0 || xrefOffset >= length)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PDF startxref value is missing or outside the file."));
            return false;
        }

        var visitedXrefOffsets = new HashSet<long>();
        for (var sectionIndex = 0; sectionIndex < MaximumPdfPreviousXrefSections; sectionIndex++)
        {
            if (!visitedXrefOffsets.Add(xrefOffset) ||
                !TryValidatePdfCrossReference(input, xrefOffset, length, out var declaresEncryption, out var previousXrefOffset))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PDF startxref or previous cross-reference offset does not identify a bounded cross-reference table or stream."));
                return false;
            }

            if (declaresEncryption)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.EncryptedOrPasswordProtected, "The PDF cross-reference or trailer dictionary declares encryption."));
                return false;
            }

            if (previousXrefOffset is null) return true;
            if (previousXrefOffset >= xrefOffset || previousXrefOffset >= length)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PDF previous cross-reference offset is invalid."));
                return false;
            }

            xrefOffset = previousXrefOffset.Value;
        }

        diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The PDF previous cross-reference chain exceeds the inspection limit."));
        return false;
    }

    private static bool TryValidatePdfCrossReference(
        Stream input,
        long xrefOffset,
        long fileLength,
        out bool declaresEncryption,
        out long? previousXrefOffset)
    {
        declaresEncryption = false;
        previousXrefOffset = null;
        if (xrefOffset < 0 || xrefOffset >= fileLength) return false;
        var bytes = ReadAtMost(input, xrefOffset, (int)Math.Min(fileLength - xrefOffset, MaximumPdfXrefBytes));
        var cursor = 0;
        SkipPdfTrivia(bytes, ref cursor);
        if (cursor + 4 <= bytes.Length && bytes.AsSpan(cursor, 4).SequenceEqual("xref"u8) &&
            (cursor + 4 == bytes.Length || IsPdfDelimiter(bytes[cursor + 4])))
        {
            return TryValidateClassicPdfXref(bytes, cursor + 4, fileLength, out declaresEncryption, out previousXrefOffset);
        }

        return TryValidatePdfXrefStream(bytes, xrefOffset, fileLength, out declaresEncryption, out previousXrefOffset);
    }

    private static bool TryValidateClassicPdfXref(
        ReadOnlySpan<byte> bytes,
        int cursor,
        long fileLength,
        out bool declaresEncryption,
        out long? previousXrefOffset)
    {
        declaresEncryption = false;
        previousXrefOffset = null;
        var totalEntries = 0;
        while (cursor < bytes.Length)
        {
            SkipPdfTrivia(bytes, ref cursor);
            if (IsPdfKeywordAt(bytes, cursor, "trailer"))
            {
                if (totalEntries == 0) return false;
                cursor += "trailer".Length;
                SkipPdfTrivia(bytes, ref cursor);
                if (cursor + 1 >= bytes.Length || bytes[cursor] != (byte)'<' || bytes[cursor + 1] != (byte)'<') return false;
                return TryReadPdfTrailerDictionary(bytes, ref cursor, out declaresEncryption, out previousXrefOffset);
            }

            if (!TryReadPdfToken(bytes, ref cursor, out var firstObjectToken) ||
                !TryReadPdfToken(bytes, ref cursor, out var entryCountToken) ||
                !long.TryParse(firstObjectToken, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var firstObject) ||
                !int.TryParse(entryCountToken, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var entryCount) ||
                firstObject < 0 || entryCount <= 0 || entryCount > MaximumPdfXrefEntries - totalEntries ||
                firstObject > long.MaxValue - entryCount)
            {
                return false;
            }

            totalEntries += entryCount;
            for (var index = 0; index < entryCount; index++)
            {
                if (!TryReadPdfToken(bytes, ref cursor, out var offsetToken) || offsetToken.Length != 10 ||
                    !TryReadPdfToken(bytes, ref cursor, out var generationToken) || generationToken.Length != 5 ||
                    !TryReadPdfToken(bytes, ref cursor, out var statusToken) || statusToken.Length != 1 ||
                    !offsetToken.All(char.IsAsciiDigit) || !generationToken.All(char.IsAsciiDigit) || statusToken is not "n" and not "f" ||
                    !long.TryParse(offsetToken, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var entryOffset) ||
                    !int.TryParse(generationToken, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var entryGeneration) || entryGeneration > 65535 ||
                    statusToken == "n" && (entryOffset < 0 || entryOffset >= fileLength))
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static bool TryValidatePdfXrefStream(
        ReadOnlySpan<byte> bytes,
        long xrefOffset,
        long fileLength,
        out bool declaresEncryption,
        out long? previousXrefOffset)
    {
        declaresEncryption = false;
        previousXrefOffset = null;
        var cursor = 0;
        if (!TryReadPdfToken(bytes, ref cursor, out var objectNumber) ||
            !TryReadPdfToken(bytes, ref cursor, out var generation) ||
            !TryReadPdfToken(bytes, ref cursor, out var objectKeyword) || objectKeyword != "obj" ||
            !long.TryParse(objectNumber, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var objectId) || objectId < 0 ||
            !int.TryParse(generation, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generationId) || generationId is < 0 or > 65535)
        {
            return false;
        }

        SkipPdfTrivia(bytes, ref cursor);
        if (cursor + 1 >= bytes.Length || bytes[cursor] != (byte)'<' || bytes[cursor + 1] != (byte)'<') return false;
        cursor += 2;
        var hasType = false;
        var hasSize = false;
        var hasWidths = false;
        var hasLength = false;
        var hasIndex = false;
        var hasFilter = false;
        var hasPreviousXref = false;
        var hasDecodeParameters = false;
        long size = 0;
        long streamLength = 0;
        var fieldWidths = Array.Empty<int>();
        var recordWidth = 0;
        long[]? indexRanges = null;
        var decodeParameters = PdfXrefDecodeParameters.Default;
        while (cursor < bytes.Length)
        {
            SkipPdfTrivia(bytes, ref cursor);
            if (cursor + 1 < bytes.Length && bytes[cursor] == (byte)'>' && bytes[cursor + 1] == (byte)'>')
            {
                cursor += 2;
                break;
            }

            if (cursor >= bytes.Length || bytes[cursor] != (byte)'/' || !TryReadPdfName(bytes, ref cursor, out var key)) return false;
            switch (key)
            {
                case "Type":
                    if (hasType || !TryReadPdfNameValue(bytes, ref cursor, "XRef")) return false;
                    hasType = true;
                    break;
                case "Size":
                    if (hasSize || !TryReadPdfNonNegativeInteger(bytes, ref cursor, out size) || size is <= 0 or > MaximumPdfXrefEntries)
                        return false;
                    hasSize = true;
                    break;
                case "W":
                    if (hasWidths || !TryReadPdfXrefWidths(bytes, ref cursor, out fieldWidths, out recordWidth)) return false;
                    hasWidths = true;
                    break;
                case "Index":
                    if (hasIndex || !TryReadPdfXrefIndex(bytes, ref cursor, out indexRanges)) return false;
                    hasIndex = true;
                    break;
                case "Length":
                    if (hasLength || !TryReadPdfNonNegativeInteger(bytes, ref cursor, out streamLength) || streamLength > MaximumPdfXrefBytes)
                        return false;
                    hasLength = true;
                    break;
                case "Encrypt":
                    declaresEncryption = true;
                    if (!SkipPdfValue(bytes, ref cursor, depth: 1)) return false;
                    break;
                case "Prev":
                    if (hasPreviousXref || !TryReadPdfNonNegativeInteger(bytes, ref cursor, out var previousOffset)) return false;
                    previousXrefOffset = previousOffset;
                    hasPreviousXref = true;
                    break;
                case "Filter":
                    if (hasFilter || !TryReadPdfNameValue(bytes, ref cursor, "FlateDecode")) return false;
                    hasFilter = true;
                    break;
                case "DecodeParms":
                    if (hasDecodeParameters || !TryReadPdfXrefDecodeParameters(bytes, ref cursor, out decodeParameters)) return false;
                    hasDecodeParameters = true;
                    break;
                default:
                    if (!SkipPdfValue(bytes, ref cursor, depth: 1)) return false;
                    break;
            }
        }

        if (!hasType || !hasSize || !hasWidths || !hasLength || recordWidth <= 0 || objectId >= size ||
            hasDecodeParameters && !hasFilter)
            return false;

        long recordCount = 0;
        if (indexRanges is null)
        {
            recordCount = size;
        }
        else
        {
            for (var index = 0; index < indexRanges.Length; index += 2)
            {
                var first = indexRanges[index];
                var count = indexRanges[index + 1];
                if (first > size || count > size - first || count > MaximumPdfXrefEntries - recordCount) return false;
                recordCount += count;
            }
        }

        if (recordCount <= 0 || recordCount > MaximumPdfXrefEntries ||
            recordCount > MaximumPdfXrefBytes / recordWidth ||
            (!hasFilter && streamLength != recordCount * recordWidth))
            return false;
        if (cursor >= bytes.Length || !TryReadPdfToken(bytes, ref cursor, out var streamKeyword) || streamKeyword != "stream" ||
            cursor >= bytes.Length || bytes[cursor] is not 10 and not 13) return false;

        if (bytes[cursor++] == 13 && cursor < bytes.Length && bytes[cursor] == 10) cursor++;
        if (streamLength > bytes.Length - cursor) return false;
        var streamBytes = bytes.Slice(cursor, checked((int)streamLength));
        var expectedRecordBytes = checked((int)(recordCount * recordWidth));
        byte[] decodedRecords;
        if (hasFilter)
        {
            if (!TryDecodePdfXrefFlate(streamBytes, expectedRecordBytes, decodeParameters, out decodedRecords)) return false;
        }
        else
        {
            if (decodeParameters.Predictor != 1) return false;
            decodedRecords = streamBytes.ToArray();
        }

        if (decodedRecords.Length != expectedRecordBytes ||
            !TryValidatePdfXrefStreamRecords(decodedRecords, fieldWidths, recordWidth, indexRanges, size, objectId, generationId, xrefOffset, fileLength)) return false;
        cursor += checked((int)streamLength);
        if (!TryReadPdfToken(bytes, ref cursor, out var endStreamKeyword) || endStreamKeyword != "endstream" ||
            !TryReadPdfToken(bytes, ref cursor, out var endObjectKeyword) || endObjectKeyword != "endobj") return false;
        return true;
    }

    private static bool TryDecodePdfXrefFlate(
        ReadOnlySpan<byte> compressed,
        int expectedLength,
        PdfXrefDecodeParameters parameters,
        out byte[] decodedRecords)
    {
        decodedRecords = [];
        if (expectedLength <= 0 || expectedLength > MaximumPdfXrefBytes || parameters.Columns <= 0) return false;
        if (parameters.Predictor != 1 && expectedLength % parameters.Columns != 0) return false;

        var rowCount = parameters.Predictor == 1 ? 1 : expectedLength / parameters.Columns;
        var encodedLength = parameters.Predictor == 15 ? expectedLength + rowCount : expectedLength;
        if (encodedLength > MaximumPdfXrefBytes) return false;
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var decoder = new ZLibStream(input, CompressionMode.Decompress);
            var encoded = new byte[encodedLength + 1];
            var total = 0;
            while (total < encoded.Length)
            {
                var count = decoder.Read(encoded, total, encoded.Length - total);
                if (count == 0) break;
                total += count;
            }

            if (total != encodedLength || decoder.ReadByte() != -1) return false;
            return TryApplyPdfXrefPredictor(encoded.AsSpan(0, encodedLength), expectedLength, parameters, rowCount, out decodedRecords);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryApplyPdfXrefPredictor(
        ReadOnlySpan<byte> encoded,
        int expectedLength,
        PdfXrefDecodeParameters parameters,
        int rowCount,
        out byte[] decoded)
    {
        decoded = new byte[expectedLength];
        var columns = parameters.Columns;
        switch (parameters.Predictor)
        {
            case 1:
                encoded.CopyTo(decoded);
                return true;
            case 2:
                for (var row = 0; row < rowCount; row++)
                {
                    var rowStart = row * columns;
                    for (var column = 0; column < columns; column++)
                    {
                        var left = column == 0 ? 0 : decoded[rowStart + column - 1];
                        decoded[rowStart + column] = unchecked((byte)(encoded[rowStart + column] + left));
                    }
                }
                return true;
            case >= 10 and <= 14:
            case 15:
                var encodedCursor = 0;
                for (var row = 0; row < rowCount; row++)
                {
                    var filter = parameters.Predictor == 15 ? encoded[encodedCursor++] : (byte)(parameters.Predictor - 10);
                    if (filter > 4) return false;
                    var rowStart = row * columns;
                    for (var column = 0; column < columns; column++)
                    {
                        var raw = encoded[encodedCursor++];
                        var left = column == 0 ? 0 : decoded[rowStart + column - 1];
                        var up = row == 0 ? 0 : decoded[rowStart - columns + column];
                        var upperLeft = row == 0 || column == 0 ? 0 : decoded[rowStart - columns + column - 1];
                        var predictor = filter switch
                        {
                            0 => 0,
                            1 => left,
                            2 => up,
                            3 => (left + up) / 2,
                            4 => PaethPredictor(left, up, upperLeft),
                            _ => 0
                        };
                        decoded[rowStart + column] = unchecked((byte)(raw + predictor));
                    }
                }
                return encodedCursor == encoded.Length;
            default:
                return false;
        }
    }

    private static int PaethPredictor(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left
            : upDistance <= upperLeftDistance ? up : upperLeft;
    }

    private static bool TryReadPdfXrefDecodeParameters(
        ReadOnlySpan<byte> bytes,
        ref int cursor,
        out PdfXrefDecodeParameters parameters)
    {
        parameters = PdfXrefDecodeParameters.Default;
        SkipPdfTrivia(bytes, ref cursor);
        if (cursor + 4 <= bytes.Length && bytes.Slice(cursor, 4).SequenceEqual("null"u8) &&
            (cursor + 4 == bytes.Length || IsPdfDelimiter(bytes[cursor + 4])))
        {
            cursor += 4;
            return true;
        }
        if (cursor + 1 >= bytes.Length || bytes[cursor] != (byte)'<' || bytes[cursor + 1] != (byte)'<') return false;
        cursor += 2;
        var predictor = 1;
        var colors = 1;
        var bitsPerComponent = 8;
        var columns = 1;
        var hasPredictor = false;
        var hasColors = false;
        var hasBitsPerComponent = false;
        var hasColumns = false;
        while (cursor < bytes.Length)
        {
            SkipPdfTrivia(bytes, ref cursor);
            if (cursor + 1 < bytes.Length && bytes[cursor] == (byte)'>' && bytes[cursor + 1] == (byte)'>')
            {
                cursor += 2;
                break;
            }

            if (cursor >= bytes.Length || bytes[cursor] != (byte)'/' || !TryReadPdfName(bytes, ref cursor, out var key) ||
                !TryReadPdfNonNegativeInteger(bytes, ref cursor, out var value) || value > int.MaxValue)
                return false;

            switch (key)
            {
                case "Predictor" when !hasPredictor:
                    predictor = (int)value;
                    hasPredictor = true;
                    break;
                case "Colors" when !hasColors:
                    colors = (int)value;
                    hasColors = true;
                    break;
                case "BitsPerComponent" when !hasBitsPerComponent:
                    bitsPerComponent = (int)value;
                    hasBitsPerComponent = true;
                    break;
                case "Columns" when !hasColumns:
                    columns = (int)value;
                    hasColumns = true;
                    break;
                default:
                    return false;
            }
        }

        if (columns <= 0 || columns > MaximumPdfXrefBytes || colors != 1 || bitsPerComponent != 8 ||
            predictor is not 1 and not 2 and not (>= 10 and <= 15))
            return false;

        parameters = new PdfXrefDecodeParameters(predictor, columns);
        return true;
    }

    private static bool TryValidatePdfXrefStreamRecords(
        ReadOnlySpan<byte> records,
        int[] fieldWidths,
        int recordWidth,
        long[]? indexRanges,
        long size,
        long streamObjectId,
        int streamGeneration,
        long xrefOffset,
        long fileLength)
    {
        var ranges = indexRanges ?? [0, records.Length / recordWidth];
        var recordIndex = 0;
        var foundStreamObject = false;
        for (var range = 0; range < ranges.Length; range += 2)
        {
            var first = ranges[range];
            var count = ranges[range + 1];
            for (long item = 0; item < count; item++, recordIndex++)
            {
                var record = records.Slice(recordIndex * recordWidth, recordWidth);
                if (!TryReadPdfXrefField(record, 0, fieldWidths[0], defaultValue: 1, out var entryType) ||
                    !TryReadPdfXrefField(record, fieldWidths[0], fieldWidths[1], defaultValue: 0, out var field2) ||
                    !TryReadPdfXrefField(record, fieldWidths[0] + fieldWidths[1], fieldWidths[2], defaultValue: 0, out var field3))
                    return false;

                var objectId = first + item;
                switch (entryType)
                {
                    case 0 when field2 < (ulong)size && field3 <= 65535:
                        break;
                    case 1 when field2 < (ulong)fileLength && field3 <= 65535:
                        break;
                    case 2 when field2 > 0 && field2 < (ulong)size && field3 < (ulong)size:
                        break;
                    default:
                        return false;
                }
                if (objectId != streamObjectId) continue;
                if (foundStreamObject || entryType != 1 || field2 != (ulong)xrefOffset || field3 != (ulong)streamGeneration)
                    return false;
                foundStreamObject = true;
            }
        }

        return recordIndex * recordWidth == records.Length && foundStreamObject;
    }

    private static bool TryReadPdfXrefField(ReadOnlySpan<byte> record, int offset, int width, ulong defaultValue, out ulong value)
    {
        value = defaultValue;
        if (width == 0) return true;
        if (width > 8 || offset < 0 || offset > record.Length - width) return false;
        value = 0;
        for (var index = 0; index < width; index++) value = (value << 8) | record[offset + index];
        return true;
    }

    private readonly record struct PdfXrefDecodeParameters(int Predictor, int Columns)
    {
        public static PdfXrefDecodeParameters Default => new(1, 1);
    }

    private static bool TryReadPdfNameValue(ReadOnlySpan<byte> bytes, ref int cursor, string expectedValue)
    {
        SkipPdfTrivia(bytes, ref cursor);
        return cursor < bytes.Length && bytes[cursor] == (byte)'/' && TryReadPdfName(bytes, ref cursor, out var value) &&
               string.Equals(value, expectedValue, StringComparison.Ordinal);
    }

    private static bool TryReadPdfXrefWidths(ReadOnlySpan<byte> bytes, ref int cursor, out int[] fieldWidths, out int totalWidth)
    {
        fieldWidths = [];
        totalWidth = 0;
        SkipPdfTrivia(bytes, ref cursor);
        if (cursor >= bytes.Length || bytes[cursor++] != (byte)'[') return false;
        var widths = new int[3];
        for (var index = 0; index < 3; index++)
        {
            if (!TryReadPdfNonNegativeInteger(bytes, ref cursor, out var width) || width > 8) return false;
            widths[index] = (int)width;
            totalWidth += widths[index];
        }

        SkipPdfTrivia(bytes, ref cursor);
        if (cursor >= bytes.Length || bytes[cursor++] != (byte)']' || totalWidth <= 0) return false;
        fieldWidths = widths;
        return true;
    }

    private static bool TryReadPdfXrefIndex(ReadOnlySpan<byte> bytes, ref int cursor, out long[]? ranges)
    {
        ranges = null;
        SkipPdfTrivia(bytes, ref cursor);
        if (cursor >= bytes.Length || bytes[cursor++] != (byte)'[') return false;
        var values = new List<long>(4);
        while (cursor < bytes.Length)
        {
            SkipPdfTrivia(bytes, ref cursor);
            if (cursor < bytes.Length && bytes[cursor] == (byte)']')
            {
                cursor++;
                break;
            }

            if (values.Count >= MaximumPdfXrefEntries || !TryReadPdfNonNegativeInteger(bytes, ref cursor, out var value)) return false;
            values.Add(value);
        }

        if (values.Count == 0 || values.Count % 2 != 0) return false;
        ranges = values.ToArray();
        return true;
    }

    private static bool TryReadPdfNonNegativeInteger(ReadOnlySpan<byte> bytes, ref int cursor, out long value)
    {
        value = 0;
        return TryReadPdfToken(bytes, ref cursor, out var token) &&
               long.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static bool TryReadPdfTrailerDictionary(
        ReadOnlySpan<byte> bytes,
        ref int cursor,
        out bool declaresEncryption,
        out long? previousXrefOffset,
        int depth = 0)
    {
        declaresEncryption = false;
        previousXrefOffset = null;
        if (depth > MaximumPdfObjectDepth || cursor + 1 >= bytes.Length || bytes[cursor] != (byte)'<' || bytes[cursor + 1] != (byte)'<') return false;
        cursor += 2;
        var hasPreviousXref = false;
        while (cursor < bytes.Length)
        {
            SkipPdfTrivia(bytes, ref cursor);
            if (cursor + 1 < bytes.Length && bytes[cursor] == (byte)'>' && bytes[cursor + 1] == (byte)'>')
            {
                cursor += 2;
                return true;
            }

            if (cursor >= bytes.Length || bytes[cursor] != (byte)'/' || !TryReadPdfName(bytes, ref cursor, out var key)) return false;
            if (string.Equals(key, "Encrypt", StringComparison.Ordinal))
            {
                declaresEncryption = true;
                if (!SkipPdfValue(bytes, ref cursor, depth + 1)) return false;
            }
            else if (string.Equals(key, "Prev", StringComparison.Ordinal))
            {
                if (hasPreviousXref || !TryReadPdfNonNegativeInteger(bytes, ref cursor, out var previousOffset)) return false;
                previousXrefOffset = previousOffset;
                hasPreviousXref = true;
            }
            else if (!SkipPdfValue(bytes, ref cursor, depth + 1))
            {
                return false;
            }
        }

        return false;
    }

    private static bool SkipPdfDictionary(ReadOnlySpan<byte> bytes, ref int cursor, int depth)
    {
        if (depth > MaximumPdfObjectDepth || cursor + 1 >= bytes.Length || bytes[cursor] != (byte)'<' || bytes[cursor + 1] != (byte)'<') return false;
        cursor += 2;
        while (cursor < bytes.Length)
        {
            SkipPdfTrivia(bytes, ref cursor);
            if (cursor + 1 < bytes.Length && bytes[cursor] == (byte)'>' && bytes[cursor + 1] == (byte)'>')
            {
                cursor += 2;
                return true;
            }

            if (cursor >= bytes.Length || bytes[cursor] != (byte)'/' || !TryReadPdfName(bytes, ref cursor, out _)) return false;
            if (!SkipPdfValue(bytes, ref cursor, depth + 1)) return false;
        }

        return false;
    }

    private static bool SkipPdfValue(ReadOnlySpan<byte> bytes, ref int cursor, int depth)
    {
        if (depth > MaximumPdfObjectDepth) return false;
        SkipPdfTrivia(bytes, ref cursor);
        if (cursor >= bytes.Length) return false;
        switch (bytes[cursor])
        {
            case (byte)'/':
                return TryReadPdfName(bytes, ref cursor, out _);
            case (byte)'(':
                return SkipPdfLiteralString(bytes, ref cursor);
            case (byte)'[':
                cursor++;
                while (cursor < bytes.Length)
                {
                    SkipPdfTrivia(bytes, ref cursor);
                    if (cursor >= bytes.Length) return false;
                    if (bytes[cursor] == (byte)']')
                    {
                        cursor++;
                        return true;
                    }

                    if (!SkipPdfValue(bytes, ref cursor, depth + 1)) return false;
                }

                return false;
            case (byte)'<':
                if (cursor + 1 < bytes.Length && bytes[cursor + 1] == (byte)'<')
                {
                    return SkipPdfDictionary(bytes, ref cursor, depth + 1);
                }

                cursor++;
                while (cursor < bytes.Length && bytes[cursor] != (byte)'>') cursor++;
                if (cursor >= bytes.Length) return false;
                cursor++;
                return true;
            default:
                var valueStart = cursor;
                while (cursor < bytes.Length && !IsPdfDelimiter(bytes[cursor])) cursor++;
                if (cursor == valueStart) return false;
                var firstEnd = cursor;
                if (IsPdfInteger(bytes[valueStart..firstEnd]))
                {
                    var probe = cursor;
                    SkipPdfTrivia(bytes, ref probe);
                    var secondStart = probe;
                    while (probe < bytes.Length && !IsPdfDelimiter(bytes[probe])) probe++;
                    if (probe > secondStart && IsPdfInteger(bytes[secondStart..probe]))
                    {
                        SkipPdfTrivia(bytes, ref probe);
                        var referenceStart = probe;
                        while (probe < bytes.Length && !IsPdfDelimiter(bytes[probe])) probe++;
                        if (bytes[referenceStart..probe].SequenceEqual("R"u8)) cursor = probe;
                    }
                }

                return true;
        }
    }

    private static bool SkipPdfLiteralString(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        if (bytes[cursor] != (byte)'(') return false;
        cursor++;
        var nesting = 1;
        while (cursor < bytes.Length)
        {
            var current = bytes[cursor++];
            if (current == (byte)'\\')
            {
                if (cursor < bytes.Length) cursor++;
            }
            else if (current == (byte)'(')
            {
                nesting++;
            }
            else if (current == (byte)')' && --nesting == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadPdfName(ReadOnlySpan<byte> bytes, ref int cursor, out string name)
    {
        name = string.Empty;
        if (cursor >= bytes.Length || bytes[cursor] != (byte)'/') return false;
        cursor++;
        var decoded = new StringBuilder();
        while (cursor < bytes.Length && !IsPdfDelimiter(bytes[cursor]))
        {
            if (bytes[cursor] == (byte)'#' && cursor + 2 < bytes.Length &&
                TryParseHexNibble(bytes[cursor + 1], out var high) && TryParseHexNibble(bytes[cursor + 2], out var low))
            {
                decoded.Append((char)((high << 4) | low));
                cursor += 3;
            }
            else
            {
                decoded.Append((char)bytes[cursor++]);
            }
        }

        name = decoded.ToString();
        return true;
    }

    private static bool TryParseHexNibble(byte value, out int nibble)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            nibble = value - (byte)'0';
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            nibble = value - (byte)'a' + 10;
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            nibble = value - (byte)'A' + 10;
            return true;
        }

        nibble = 0;
        return false;
    }

    private static void SkipPdfTrivia(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        while (cursor < bytes.Length)
        {
            if (IsPdfWhitespace(bytes[cursor]))
            {
                cursor++;
                continue;
            }

            if (bytes[cursor] != (byte)'%') return;
            while (cursor < bytes.Length && bytes[cursor] is not 10 and not 13) cursor++;
        }
    }

    private static bool TryReadPdfToken(ReadOnlySpan<byte> bytes, ref int cursor, out string token)
    {
        SkipPdfTrivia(bytes, ref cursor);
        var start = cursor;
        while (cursor < bytes.Length && !IsPdfDelimiter(bytes[cursor])) cursor++;
        token = cursor == start ? string.Empty : Encoding.ASCII.GetString(bytes[start..cursor]);
        return cursor > start;
    }

    private static bool IsPdfKeywordAt(ReadOnlySpan<byte> bytes, int cursor, string keyword)
    {
        if (cursor < 0 || cursor + keyword.Length > bytes.Length ||
            !bytes.Slice(cursor, keyword.Length).SequenceEqual(Encoding.ASCII.GetBytes(keyword))) return false;
        var after = cursor + keyword.Length;
        return after == bytes.Length || IsPdfDelimiter(bytes[after]);
    }

    private static bool IsPdfInteger(ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty) return false;
        var index = token[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        if (index == token.Length) return false;
        for (; index < token.Length; index++)
        {
            if (token[index] is < (byte)'0' or > (byte)'9') return false;
        }

        return true;
    }

    private static bool IsPdfWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsPdfDelimiter(byte value) => IsPdfWhitespace(value) || value is
        (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsJpegStartOfFrame(byte marker) => marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or
        >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf;

    private static bool ValidateJpeg(
        Stream input,
        long length,
        SourcePreflightOptions options,
        ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        var header = ReadAtMost(input, 0, (int)Math.Min(length, 2));
        if (header.Length != 2 || header[0] != 0xff || header[1] != 0xd8)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The JPEG start marker is missing."));
            return false;
        }

        var offset = 2L;
        var hasFrame = false;
        var hasScan = false;
        var markerCount = 0;
        long scanOffset = -1;
        Span<byte> markerByte = stackalloc byte[1];
        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> frameHeader = stackalloc byte[6];
        Span<byte> scanComponents = stackalloc byte[1];
        while (offset < length && ++markerCount <= 4096)
        {
            if (!ReadExactlyAt(input, offset, markerByte) || markerByte[0] != 0xff)
            {
                break;
            }

            offset++;
            do
            {
                if (!ReadExactlyAt(input, offset, markerByte))
                {
                    offset = length;
                    break;
                }

                offset++;
            } while (markerByte[0] == 0xff);

            var marker = markerByte[0];
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (marker == 0xd9)
            {
                break;
            }

            var segmentLengthOffset = offset;
            if (!ReadExactlyAt(input, segmentLengthOffset, lengthBytes))
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2 || segmentLength > length - segmentLengthOffset)
            {
                break;
            }

            var segmentDataOffset = segmentLengthOffset + 2;
            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 11 || !ReadExactlyAt(input, segmentDataOffset, frameHeader))
                {
                    break;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(frameHeader[1..3]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(frameHeader[3..5]);
                var componentCount = frameHeader[5];
                if (componentCount == 0 || segmentLength != 8 + componentCount * 3)
                {
                    break;
                }

                if (width == 0 || height == 0 || (long)width * height > options.MaximumRasterPixels)
                {
                    diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The JPEG dimensions exceed the configured raster limit."));
                    return false;
                }

                hasFrame = true;
            }

            offset = segmentLengthOffset + segmentLength;
            if (marker == 0xda)
            {
                hasScan = hasFrame && segmentLength >= 8 && ReadExactlyAt(input, segmentDataOffset, scanComponents) &&
                          scanComponents[0] > 0 && segmentLength == 6 + scanComponents[0] * 2;
                scanOffset = offset;
                break;
            }
        }

        if (!hasFrame || !hasScan || markerCount > 4096 || !HasJpegScanDataAndEndMarker(input, scanOffset, length))
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The JPEG frame, scan, or terminal end marker is missing or malformed."));
            return false;
        }

        return true;
    }

    private static bool HasJpegScanDataAndEndMarker(Stream input, long scanOffset, long length)
    {
        if (scanOffset < 0 || scanOffset >= length || length < 2 ||
            !ReadExactlyAt(input, length - 2, stackalloc byte[] { 0xff, 0xd9 })) return false;

        var buffer = new byte[8192];
        var sawScanData = false;
        var sawMarkerPrefix = false;
        input.Position = scanOffset;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) return false;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (!sawMarkerPrefix)
                {
                    if (value == 0xff)
                    {
                        sawMarkerPrefix = true;
                    }
                    else
                    {
                        sawScanData = true;
                    }

                    continue;
                }

                if (value == 0xff) continue;
                sawMarkerPrefix = false;
                if (value == 0x00)
                {
                    sawScanData = true;
                }
                else if (value == 0xd9)
                {
                    return sawScanData;
                }
                else if (!sawScanData)
                {
                    return false;
                }
            }
        }
    }

    private static bool ValidatePng(Stream input, long length, SourcePreflightOptions options, ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        Span<byte> signature = stackalloc byte[8];
        if (!ReadExactlyAt(input, 0, signature) || !signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PNG signature is incomplete."));
            return false;
        }

        long offset = 8;
        var chunkCount = 0;
        var hasHeader = false;
        long imageDataLength = 0;
        var hasEnd = false;
        var chunkHeader = new byte[8];
        var headerData = new byte[13];
        while (offset < length && ++chunkCount <= 4096)
        {
            if (!ReadExactlyAt(input, offset, chunkHeader)) break;
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(0, 4));
            var chunkType = Encoding.ASCII.GetString(chunkHeader.AsSpan(4, 4));
            offset += 8;
            if (chunkLength > length - offset - 4)
            {
                break;
            }

            if (!PngChunkCrcMatches(input, offset - 4, chunkLength, offset + chunkLength))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "A PNG chunk CRC does not match its bounded type and data bytes."));
                return false;
            }

            if (!hasHeader)
            {
                if (chunkType != "IHDR" || chunkLength != 13)
                {
                    break;
                }

                if (!ReadExactlyAt(input, offset, headerData)) break;
                var width = BinaryPrimitives.ReadUInt32BigEndian(headerData.AsSpan(0, 4));
                var height = BinaryPrimitives.ReadUInt32BigEndian(headerData.AsSpan(4, 4));
                if (width == 0 || height == 0 || (ulong)width * height > (ulong)options.MaximumRasterPixels)
                {
                    diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The PNG dimensions exceed the configured raster limit."));
                    return false;
                }

                if (!IsValidPngBitDepth(headerData[8], headerData[9]) || headerData[10] != 0 || headerData[11] != 0 || headerData[12] > 1)
                {
                    diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PNG IHDR bit depth, color type, compression, filter, or interlace method is invalid."));
                    return false;
                }

                hasHeader = true;
            }
            else if (chunkType == "IHDR")
            {
                break;
            }

            if (chunkType == "IDAT") imageDataLength += chunkLength;
            if (chunkType == "IEND")
            {
                if (chunkLength != 0) break;
                hasEnd = true;
                break;
            }

            offset += chunkLength + 4; // chunk data and CRC
        }

        if (!hasHeader || imageDataLength == 0 || !hasEnd || chunkCount > 4096)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The PNG has incomplete or malformed image chunks."));
            return false;
        }

        return true;
    }

    private static bool IsValidPngBitDepth(byte bitDepth, byte colorType) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        4 or 6 => bitDepth is 8 or 16,
        _ => false
    };

    private static bool PngChunkCrcMatches(Stream input, long typeOffset, uint chunkLength, long crcOffset)
    {
        Span<byte> type = stackalloc byte[4];
        Span<byte> crcBytes = stackalloc byte[4];
        if (!ReadExactlyAt(input, typeOffset, type)) return false;
        var crc = UpdatePngCrc(uint.MaxValue, type);
        var buffer = new byte[8192];
        var remaining = (long)chunkLength;
        var offset = typeOffset + 4;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            if (!ReadExactlyAt(input, offset, buffer.AsSpan(0, count))) return false;
            crc = UpdatePngCrc(crc, buffer.AsSpan(0, count));
            offset += count;
            remaining -= count;
        }

        return ReadExactlyAt(input, crcOffset, crcBytes) && BinaryPrimitives.ReadUInt32BigEndian(crcBytes) == ~crc;
    }

    private static uint UpdatePngCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return crc;
    }

    private static bool ValidateTiff(
        Stream input,
        long length,
        SourcePreflightOptions options,
        ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        Span<byte> header = stackalloc byte[8];
        if (length < header.Length || !ReadExactlyAt(input, 0, header))
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF header is truncated."));
            return false;
        }

        var littleEndian = header[0] == (byte)'I' && header[1] == (byte)'I';
        var magic = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..4])
            : BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);
        if (magic == 43)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.UnsupportedFormat, "BigTIFF is recognized but is not supported by the bounded TIFF preflight."));
            return false;
        }

        if (magic != 42)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF byte order or classic TIFF marker is invalid."));
            return false;
        }

        var offset = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..8])
            : BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        if (offset < 8 || offset > length - 2)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF first image-directory offset is outside the file."));
            return false;
        }

        Span<byte> countBytes = stackalloc byte[2];
        Span<byte> entry = stackalloc byte[12];
        Span<byte> nextDirectoryBytes = stackalloc byte[4];
        var currentDirectoryOffset = offset;
        var visitedDirectories = new HashSet<uint>();
        var totalArrayValues = 0L;
        for (var pageIndex = 0; currentDirectoryOffset != 0; pageIndex++)
        {
            if (pageIndex >= 4096 || !visitedDirectories.Add(currentDirectoryOffset) || currentDirectoryOffset < 8 || currentDirectoryOffset > length - 2)
            {
                diagnostics.Add(Error(pageIndex >= 4096 ? IntakeDiagnosticCode.UnsafePackage : IntakeDiagnosticCode.MalformedContainer,
                    "The TIFF image-directory chain is looping, oversized, or outside the file."));
                return false;
            }

            if (!ReadExactlyAt(input, currentDirectoryOffset, countBytes))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF image-directory entry count is truncated."));
                return false;
            }

            var count = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(countBytes)
                : BinaryPrimitives.ReadUInt16BigEndian(countBytes);
            var directoryLength = 2L + count * 12L + 4;
            if (count == 0 || count > 4096 || directoryLength > length - currentDirectoryOffset)
            {
                diagnostics.Add(Error(count > 4096 ? IntakeDiagnosticCode.UnsafePackage : IntakeDiagnosticCode.MalformedContainer,
                    "The TIFF image directory is empty, oversized, or extends beyond the file."));
                return false;
            }

            uint width = 0;
            uint height = 0;
            uint[]? stripOffsets = null;
            uint[]? stripByteCounts = null;
            uint[]? tileOffsets = null;
            uint[]? tileByteCounts = null;
            var directoryTags = new HashSet<ushort>();
            for (var index = 0; index < count; index++)
            {
                var entryOffset = (long)currentDirectoryOffset + 2 + index * 12L;
                if (!ReadExactlyAt(input, entryOffset, entry))
                {
                    diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF image-directory entries are truncated."));
                    return false;
                }

                var tag = ReadTiffUInt16(entry[..2], littleEndian);
                var type = ReadTiffUInt16(entry[2..4], littleEndian);
                var valueCount = ReadTiffUInt32(entry[4..8], littleEndian);
                var valueField = entry[8..12];
                if (!directoryTags.Add(tag))
                {
                    diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF image directory contains a duplicate tag."));
                    return false;
                }

                if (tag is 256 or 257)
                {
                    if (valueCount != 1 || type is not 3 and not 4)
                    {
                        diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF width or height field is not a single SHORT or LONG value."));
                        return false;
                    }

                    var value = type == 3 ? ReadTiffUInt16(valueField[..2], littleEndian) : ReadTiffUInt32(valueField, littleEndian);
                    if (tag == 256) width = value;
                    else height = value;
                }
                else if (tag is 273 or 279 or 324 or 325)
                {
                    if (valueCount > MaximumTiffTotalArrayValues - totalArrayValues)
                    {
                        diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The TIFF strip or tile arrays exceed the aggregate inspection limit."));
                        return false;
                    }

                    if (!TryReadTiffValues(input, valueField, valueCount, type, littleEndian, length, out var values))
                    {
                        diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                            "The TIFF strip or tile references are empty, oversized, malformed, or outside the source."));
                        return false;
                    }

                    totalArrayValues += valueCount;

                    if (tag == 273) stripOffsets = values;
                    if (tag == 279) stripByteCounts = values;
                    if (tag == 324) tileOffsets = values;
                    if (tag == 325) tileByteCounts = values;
                }
            }

            if (width == 0 || height == 0)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF image dimensions are missing or zero."));
                return false;
            }

            if ((ulong)width * height > (ulong)options.MaximumRasterPixels)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The TIFF dimensions exceed the configured raster limit."));
                return false;
            }

            if ((stripOffsets is null) != (stripByteCounts is null) || (tileOffsets is null) != (tileByteCounts is null))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF strip or tile offset and byte-count arrays are incomplete."));
                return false;
            }

            var hasStrips = stripOffsets is not null && stripByteCounts is not null;
            var hasTiles = tileOffsets is not null && tileByteCounts is not null;
            if (!hasStrips && !hasTiles)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF image has no strip or tile data references."));
                return false;
            }

            if ((hasStrips && !TiffDataRangesAreValid(stripOffsets!, stripByteCounts!, length)) ||
                (hasTiles && !TiffDataRangesAreValid(tileOffsets!, tileByteCounts!, length)))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "A TIFF image strip or tile data range is empty or outside the source."));
                return false;
            }

            if (!ReadExactlyAt(input, (long)currentDirectoryOffset + directoryLength - 4, nextDirectoryBytes))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The TIFF image-directory link is truncated."));
                return false;
            }

            currentDirectoryOffset = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(nextDirectoryBytes)
                : BinaryPrimitives.ReadUInt32BigEndian(nextDirectoryBytes);
        }

        return true;
    }

    private static bool ValidateRtf(Stream input, long length, ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        var prefix = ReadAtMost(input, 0, (int)Math.Min(length, 4096));
        var suffix = ReadTail(input, length, 4096);
        var prefixText = Encoding.Latin1.GetString(prefix);
        var suffixText = Encoding.Latin1.GetString(suffix);
        if (!prefixText.StartsWith("{\\rtf", StringComparison.Ordinal) || !suffixText.TrimEnd().EndsWith('}'))
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The RTF header or closing group delimiter is missing."));
            return false;
        }

        var controlWordResult = ScanRtfControlWords(input, length, "passwordhash");
        if (controlWordResult == RtfControlScanResult.MalformedBinarySegment)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "An RTF binary segment is truncated or has no byte count."));
            return false;
        }

        if (controlWordResult == RtfControlScanResult.PasswordProtection)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.EncryptedOrPasswordProtected, "The RTF contains an explicit password protection marker."));
            return false;
        }

        return true;
    }

    private static RtfControlScanResult ScanRtfControlWords(Stream input, long length, string expectedWord)
    {
        input.Position = 0;
        var reader = new RtfByteReader(input, length);
        while (reader.TryRead(out var current))
        {
            if (current != (byte)'\\' || !reader.TryRead(out var next)) continue;
            if (next == (byte)'\'')
            {
                _ = reader.TryRead(out _);
                _ = reader.TryRead(out _);
                continue;
            }

            if (!IsRtfAsciiLetter(next)) continue;
            var wordLength = 0;
            var matchesExpected = true;
            var matchesBinary = true;
            var hasNext = true;
            while (IsRtfAsciiLetter(next))
            {
                var lower = (byte)(next is >= (byte)'A' and <= (byte)'Z' ? next + ('a' - 'A') : next);
                if (wordLength >= expectedWord.Length || lower != (byte)expectedWord[wordLength]) matchesExpected = false;
                if (wordLength >= 3 || lower != "bin"[wordLength]) matchesBinary = false;
                wordLength++;
                if (!reader.TryRead(out next))
                {
                    hasNext = false;
                    break;
                }
            }

            if (matchesExpected && wordLength == expectedWord.Length) return RtfControlScanResult.PasswordProtection;

            long binaryLength = 0;
            var hasBinaryLength = false;
            if (hasNext && next is >= (byte)'0' and <= (byte)'9')
            {
                hasBinaryLength = true;
                do
                {
                    var digit = next - (byte)'0';
                    binaryLength = binaryLength > (long.MaxValue - digit) / 10
                        ? long.MaxValue
                        : binaryLength * 10 + digit;
                    if (!reader.TryRead(out next))
                    {
                        hasNext = false;
                        break;
                    }
                } while (next is >= (byte)'0' and <= (byte)'9');
            }

            if (hasNext && next != (byte)' ')
            {
                reader.PushBack(next);
            }

            if (matchesBinary && wordLength == 3)
            {
                if (!hasBinaryLength || !reader.Skip(binaryLength)) return RtfControlScanResult.MalformedBinarySegment;
            }
        }

        return RtfControlScanResult.NoPasswordProtection;
    }

    private enum RtfControlScanResult
    {
        NoPasswordProtection,
        PasswordProtection,
        MalformedBinarySegment
    }

    private static bool IsRtfAsciiLetter(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool ValidateLegacyOffice(
        Stream input,
        long length,
        SourceFormat expected,
        SourcePreflightOptions options,
        ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        if (!LegacyOfficeContainerInspector.IsValidOfficeDocument(input, length, expected, options.MaximumPackageMetadataBytes, out var isEncrypted))
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                "The legacy Office compound file is malformed or does not contain the expected document stream."));
            return false;
        }

        if (isEncrypted)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.EncryptedOrPasswordProtected,
                "The legacy Office document contains an encryption marker."));
            return false;
        }

        return true;
    }

    private static bool ValidatePackage(
        Stream input,
        long length,
        SourceFormat expected,
        SourcePreflightOptions options,
        ImmutableArray<IntakeDiagnostic>.Builder diagnostics)
    {
        if (!TryReadZipDirectory(input, length, options, diagnostics, out var entries))
        {
            return false;
        }

        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var required = expected switch
        {
            SourceFormat.Docx => new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml" },
            SourceFormat.Xlsx => new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml" },
            SourceFormat.Pptx => new[] { "[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml" },
            SourceFormat.Odt or SourceFormat.Ods or SourceFormat.Odp => new[] { "mimetype", "META-INF/manifest.xml", "content.xml" },
            _ => []
        };

        if (required.Any(name => !names.Contains(name)))
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.ExtensionSignatureMismatch,
                "The package entries do not match the document family named by the extension."));
            return false;
        }

        if (expected is SourceFormat.Odt or SourceFormat.Ods or SourceFormat.Odp)
        {
            var mimeEntry = entries.Single(entry => entry.Name == "mimetype");
            var expectedMime = expected switch
            {
                SourceFormat.Odt => "application/vnd.oasis.opendocument.text",
                SourceFormat.Ods => "application/vnd.oasis.opendocument.spreadsheet",
                _ => "application/vnd.oasis.opendocument.presentation"
            };
            if (mimeEntry.Method != 0 || mimeEntry.Length > 128 || mimeEntry.LocalOffset != 0)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The OpenDocument mimetype entry is not in the required bounded form."));
                return false;
            }

            var dataOffset = mimeEntry.LocalOffset + 30 + mimeEntry.LocalNameLength + mimeEntry.LocalExtraLength;
            var data = ReadAtMost(input, dataOffset, checked((int)mimeEntry.Length));
            if (!string.Equals(Encoding.ASCII.GetString(data), expectedMime, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.ExtensionSignatureMismatch,
                    "The OpenDocument mimetype does not match the document family named by the extension."));
                return false;
            }

            var manifestEntry = entries.Single(entry => entry.Name == "META-INF/manifest.xml");
            if (manifestEntry.Length > options.MaximumPackageMetadataBytes || manifestEntry.Length > int.MaxValue)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage,
                    "The OpenDocument manifest exceeds the configured metadata limit."));
                return false;
            }

            if (!TryReadZipEntry(input, manifestEntry, (int)Math.Min(options.MaximumPackageMetadataBytes, int.MaxValue), out var manifestBytes))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                    "The OpenDocument manifest could not be read within its validated bounds."));
                return false;
            }

            var manifestSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = options.MaximumPackageMetadataBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            try
            {
                using var manifestStream = new MemoryStream(manifestBytes, writable: false);
                using var reader = XmlReader.Create(manifestStream, manifestSettings);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element &&
                        reader.LocalName == "encryption-data" &&
                        reader.NamespaceURI == "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0")
                    {
                        diagnostics.Add(Error(IntakeDiagnosticCode.EncryptedOrPasswordProtected,
                            "The OpenDocument manifest declares encrypted content."));
                        return false;
                    }
                }
            }
            catch (XmlException)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                    "The OpenDocument manifest is not well-formed bounded XML."));
                return false;
            }
        }

        return true;
    }

    private static bool TryReadZipEntry(Stream input, ZipEntryMetadata entry, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (entry.Length > maximumBytes) return false;

        var content = new byte[(int)entry.Length];
        using var compressed = new BoundedReadStream(input, entry.DataOffset, entry.CompressedLength, rejectUnexpectedEnd: entry.Method == 8);
        using var inflater = entry.Method == 8 ? new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true) : null;
        var payload = (Stream?)inflater ?? compressed;
        var offset = 0;
        try
        {
            while (offset < content.Length)
            {
                var read = payload.Read(content, offset, content.Length - offset);
                if (read == 0) return false;
                offset += read;
            }

            Span<byte> extra = stackalloc byte[1];
            if (payload.Read(extra) != 0 || compressed.Remaining != 0) return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        bytes = content;
        return true;
    }

    private static bool TryReadZipDirectory(
        Stream input,
        long length,
        SourcePreflightOptions options,
        ImmutableArray<IntakeDiagnostic>.Builder diagnostics,
        out List<ZipEntryMetadata> entries)
    {
        entries = [];
        var tail = ReadTail(input, length, MaximumPdfTailBytes);
        var eocdIndex = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == ZipEndOfCentralDirectory)
            {
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));
                if (index + 22 + commentLength == tail.Length)
                {
                    eocdIndex = index;
                    break;
                }
            }
        }

        if (eocdIndex < 0)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The package has no bounded ZIP end-of-directory record."));
            return false;
        }

        var eocd = tail.AsSpan(eocdIndex);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..6]);
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..8]);
        var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..10]);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..12]);
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..16]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..20]);
        var eocdOffset = length - tail.Length + eocdIndex;

        if (diskNumber != 0 || centralDisk != 0 || diskEntries != entryCount || entryCount == ushort.MaxValue ||
            centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "Multi-disk or ZIP64 metadata is outside the bounded intake profile."));
            return false;
        }

        if (entryCount == 0 || entryCount > options.MaximumPackageEntries || centralSize > options.MaximumPackageMetadataBytes ||
            (long)centralOffset + centralSize > eocdOffset)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The package directory exceeds the configured metadata limits."));
            return false;
        }

        var centralEnd = (long)centralOffset + centralSize;
        var cursor = (long)centralOffset;
        long totalUncompressed = 0;
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        Span<byte> header = stackalloc byte[46];
        for (var index = 0; index < entryCount; index++)
        {
            if (cursor + header.Length > centralEnd || !ReadExactlyAt(input, cursor, header) ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != ZipCentralHeader)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The package central directory is truncated or malformed."));
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
            var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..32]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..46]);
            var recordLength = 46L + nameLength + extraLength + commentLength;
            if (recordLength > centralEnd - cursor || uncompressed == uint.MaxValue || compressed == uint.MaxValue || localOffset == uint.MaxValue)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The package entry metadata is truncated or requires unsupported ZIP64 fields."));
                return false;
            }

            if ((flags & 0x41) != 0)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.EncryptedOrPasswordProtected, "The package contains an encrypted entry."));
                return false;
            }

            if (method is not 0 and not 8)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsupportedFormat, "The package uses an unsupported compression method."));
                return false;
            }

            var nameBytes = ReadAtMost(input, cursor + 46, nameLength);
            string name;
            try
            {
                name = (flags & (1 << 11)) != 0 ? StrictUtf8.GetString(nameBytes) : Encoding.ASCII.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The package contains an invalid UTF-8 entry name."));
                return false;
            }

            if (!IsSafePackageEntryName(name) || !uniqueNames.Add(name))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The package contains an unsafe or duplicate entry name."));
                return false;
            }

            var localHeader = new byte[30];
            if ((long)localOffset + localHeader.Length > centralOffset || !ReadExactlyAt(input, localOffset, localHeader) ||
                BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(0, 4)) != ZipLocalHeader)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "A package entry has an invalid local header."));
                return false;
            }

            var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(26, 2));
            var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(28, 2));
            var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(6, 2));
            var localMethod = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(8, 2));
            var localCrc = BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(14, 4));
            var localCompressed = BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(18, 4));
            var localUncompressed = BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(22, 4));
            var localName = ReadAtMost(input, (long)localOffset + 30, localNameLength);
            var dataOffset = (long)localOffset + 30 + localNameLength + localExtraLength;
            var usesDescriptor = (flags & 0x08) != 0;
            var localMetadataMatches = usesDescriptor
                ? (localCrc == 0 || localCrc == crc) && (localCompressed == 0 || localCompressed == compressed) &&
                  (localUncompressed == 0 || localUncompressed == uncompressed)
                : localCrc == crc && localCompressed == compressed && localUncompressed == uncompressed;
            if (localFlags != flags || localMethod != method || !localName.AsSpan().SequenceEqual(nameBytes) ||
                !localMetadataMatches || dataOffset > centralOffset || compressed > centralOffset - dataOffset)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                    "A package local header's name, flags, method, CRC, or sizes do not match its bounded central-directory entry."));
                return false;
            }

            var descriptorLength = 0;
            if (usesDescriptor && !ZipDataDescriptorMatches(input, dataOffset + compressed, centralOffset, crc, compressed, uncompressed, out descriptorLength))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                    "A package data descriptor does not match its bounded central-directory entry."));
                return false;
            }

            totalUncompressed = checked(totalUncompressed + uncompressed);
            if (totalUncompressed > options.MaximumUncompressedPackageBytes ||
                (compressed == 0 && uncompressed > 0) ||
                (compressed > 0 && (double)uncompressed / compressed > options.MaximumPackageCompressionRatio))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.UnsafePackage, "The package declares excessive uncompressed size or compression ratio."));
                return false;
            }

            if (!ZipEntryPayloadMatches(input, dataOffset, method, crc, compressed, uncompressed))
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer,
                    "A package entry's bounded payload does not match its declared CRC and uncompressed length."));
                return false;
            }

            entries.Add(new ZipEntryMetadata(name, flags, method, crc, compressed, uncompressed, localOffset, nameLength, localExtraLength,
                dataOffset, dataOffset + compressed, descriptorLength));
            cursor += recordLength;
        }

        if (cursor != centralEnd)
        {
            diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "The package central-directory size does not match its entries."));
            return false;
        }

        var orderedEntries = entries.OrderBy(entry => entry.LocalOffset).ToArray();
        for (var index = 0; index < orderedEntries.Length; index++)
        {
            var entry = orderedEntries[index];
            var nextLocalOffset = index + 1 < orderedEntries.Length ? orderedEntries[index + 1].LocalOffset : (uint)centralOffset;
            if (entry.LocalOffset >= nextLocalOffset || entry.DataEnd + entry.DescriptorLength > nextLocalOffset)
            {
                diagnostics.Add(Error(IntakeDiagnosticCode.MalformedContainer, "Package local entries overlap or extend into another entry."));
                return false;
            }
        }

        return true;
    }

    private static bool ZipEntryPayloadMatches(Stream input, long dataOffset, ushort method, uint crc32, uint compressedLength, uint expectedLength)
    {
        if (method == 0 && compressedLength != expectedLength) return false;
        using var compressed = new BoundedReadStream(input, dataOffset, compressedLength, rejectUnexpectedEnd: method == 8);
        using var inflater = method == 8 ? new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true) : null;
        var payload = (Stream?)inflater ?? compressed;
        var buffer = new byte[8192];
        var crc = uint.MaxValue;
        long uncompressedLength = 0;
        try
        {
            while (true)
            {
                var read = payload.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                uncompressedLength += read;
                if (uncompressedLength > expectedLength) return false;
                crc = UpdatePngCrc(crc, buffer.AsSpan(0, read));
            }

            return uncompressedLength == expectedLength && ~crc == crc32 && compressed.Remaining == 0 &&
                   (method != 8 || DeflateBoundaryScanner.ConsumesExactly(input, dataOffset, compressedLength));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Finds the final DEFLATE block boundary without trusting inflater read-ahead.</summary>
    private sealed class DeflateBoundaryScanner
    {
        private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

        private static readonly int[] LengthExtraBits =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
        ];

        private readonly DeflateBitReader _bits;

        private DeflateBoundaryScanner(Stream source, long offset, long length) => _bits = new DeflateBitReader(source, offset, length);

        public static bool ConsumesExactly(Stream source, long offset, long length) =>
            new DeflateBoundaryScanner(source, offset, length).Scan();

        private bool Scan()
        {
            while (true)
            {
                if (!_bits.TryReadBits(1, out var finalBlock) || !_bits.TryReadBits(2, out var blockType)) return false;
                if (blockType == 3) return false;

                if (blockType == 0)
                {
                    if (!ScanStoredBlock()) return false;
                }
                else
                {
                    if (blockType == 1)
                    {
                        CreateFixedTrees(out var literals, out var distances);
                        if (!ScanCompressedBlock(literals, distances)) return false;
                    }
                    else
                    {
                        if (!TryCreateDynamicTrees(out var literals, out var distances) || !ScanCompressedBlock(literals, distances)) return false;
                    }
                }

                if (finalBlock != 0)
                {
                    return _bits.BytesConsumed == _bits.Length;
                }
            }
        }

        private bool ScanStoredBlock()
        {
            _bits.AlignToByte();
            if (!_bits.TryReadBits(16, out var length) || !_bits.TryReadBits(16, out var complement) ||
                (ushort)length != unchecked((ushort)~(ushort)complement))
            {
                return false;
            }

            for (var index = 0; index < length; index++)
            {
                if (!_bits.TryReadBits(8, out _)) return false;
            }

            return true;
        }

        private bool ScanCompressedBlock(DeflateHuffman literals, DeflateHuffman distances)
        {
            while (literals.TryDecode(_bits, out var symbol))
            {
                if (symbol < 256) continue;
                if (symbol == 256) return true;
                if (symbol is < 257 or > 285) return false;

                var lengthExtraBits = LengthExtraBits[symbol - 257];
                if (!_bits.TryReadBits(lengthExtraBits, out _)) return false;
                if (!distances.TryDecode(_bits, out var distanceSymbol) || distanceSymbol > 29) return false;
                var distanceExtraBits = distanceSymbol < 4 ? 0 : distanceSymbol / 2 - 1;
                if (!_bits.TryReadBits(distanceExtraBits, out _)) return false;
            }

            return false;
        }

        private bool TryCreateDynamicTrees(out DeflateHuffman literals, out DeflateHuffman distances)
        {
            literals = DeflateHuffman.Empty;
            distances = DeflateHuffman.Empty;
            if (!_bits.TryReadBits(5, out var literalCount) || !_bits.TryReadBits(5, out var distanceCount) ||
                !_bits.TryReadBits(4, out var codeLengthCount))
            {
                return false;
            }

            var literalCodeCount = checked((int)literalCount + 257);
            var distanceCodeCount = checked((int)distanceCount + 1);
            var codeLengthCodeCount = checked((int)codeLengthCount + 4);
            Span<byte> codeLengthLengths = stackalloc byte[19];
            codeLengthLengths.Clear();
            for (var index = 0; index < codeLengthCodeCount; index++)
            {
                if (!_bits.TryReadBits(3, out var length)) return false;
                codeLengthLengths[CodeLengthOrder[index]] = (byte)length;
            }

            var codeLengthTree = new DeflateHuffman(codeLengthLengths);
            if (!codeLengthTree.IsValid) return false;

            var totalCodeCount = literalCodeCount + distanceCodeCount;
            var codeLengths = new byte[totalCodeCount];
            var cursor = 0;
            while (cursor < codeLengths.Length)
            {
                if (!codeLengthTree.TryDecode(_bits, out var symbol)) return false;
                if (symbol <= 15)
                {
                    codeLengths[cursor++] = (byte)symbol;
                    continue;
                }

                int repeatCount;
                byte repeatedLength;
                if (symbol == 16)
                {
                    if (cursor == 0 || !_bits.TryReadBits(2, out var repeatExtra)) return false;
                    repeatCount = checked((int)repeatExtra + 3);
                    repeatedLength = codeLengths[cursor - 1];
                }
                else if (symbol == 17)
                {
                    if (!_bits.TryReadBits(3, out var repeatExtra)) return false;
                    repeatCount = checked((int)repeatExtra + 3);
                    repeatedLength = 0;
                }
                else if (symbol == 18)
                {
                    if (!_bits.TryReadBits(7, out var repeatExtra)) return false;
                    repeatCount = checked((int)repeatExtra + 11);
                    repeatedLength = 0;
                }
                else
                {
                    return false;
                }

                if (repeatCount > codeLengths.Length - cursor) return false;
                codeLengths.AsSpan(cursor, repeatCount).Fill(repeatedLength);
                cursor += repeatCount;
            }

            var literalLengths = codeLengths.AsSpan(0, literalCodeCount);
            if (literalLengths[256] == 0) return false;
            literals = new DeflateHuffman(literalLengths);
            distances = new DeflateHuffman(codeLengths.AsSpan(literalCodeCount, distanceCodeCount));
            return literals.IsValid && distances.IsValid;
        }

        private static void CreateFixedTrees(out DeflateHuffman literals, out DeflateHuffman distances)
        {
            Span<byte> literalLengths = stackalloc byte[288];
            for (var symbol = 0; symbol < literalLengths.Length; symbol++)
            {
                literalLengths[symbol] = (byte)(symbol <= 143 ? 8 : symbol <= 255 ? 9 : symbol <= 279 ? 7 : 8);
            }

            Span<byte> distanceLengths = stackalloc byte[32];
            distanceLengths.Fill(5);
            literals = new DeflateHuffman(literalLengths);
            distances = new DeflateHuffman(distanceLengths);
        }
    }

    private sealed class DeflateHuffman
    {
        private readonly int[] _counts = new int[16];
        private readonly int[] _firstCodes = new int[16];
        private readonly int[] _firstSymbols = new int[16];
        private readonly int[] _symbols;
        private readonly int _maximumLength;

        public static DeflateHuffman Empty { get; } = new(ReadOnlySpan<byte>.Empty);

        public DeflateHuffman(ReadOnlySpan<byte> codeLengths)
        {
            var maximumLength = 0;
            foreach (var length in codeLengths)
            {
                if (length > 15)
                {
                    IsValid = false;
                    _symbols = [];
                    return;
                }

                if (length == 0) continue;
                _counts[length]++;
                maximumLength = Math.Max(maximumLength, length);
            }

            _maximumLength = maximumLength;
            var symbolCount = 0;
            var code = 0;
            for (var length = 1; length <= 15; length++)
            {
                code = (code + _counts[length - 1]) << 1;
                if (code + _counts[length] > (1 << length))
                {
                    IsValid = false;
                    _symbols = [];
                    return;
                }

                _firstCodes[length] = code;
                _firstSymbols[length] = symbolCount;
                symbolCount += _counts[length];
            }

            _symbols = new int[symbolCount];
            var offsets = (int[])_firstSymbols.Clone();
            for (var symbol = 0; symbol < codeLengths.Length; symbol++)
            {
                var length = codeLengths[symbol];
                if (length != 0) _symbols[offsets[length]++] = symbol;
            }

            IsValid = true;
        }

        public bool IsValid { get; }

        public bool TryDecode(DeflateBitReader bits, out int symbol)
        {
            var code = 0;
            for (var length = 1; length <= _maximumLength; length++)
            {
                if (!bits.TryReadBits(1, out var bit)) break;
                code = (code << 1) | (int)bit;
                var offset = code - _firstCodes[length];
                if (offset < 0 || offset >= _counts[length]) continue;
                symbol = _symbols[_firstSymbols[length] + offset];
                return true;
            }

            symbol = 0;
            return false;
        }
    }

    private sealed class DeflateBitReader(Stream source, long start, long length)
    {
        private readonly byte[] _buffer = new byte[8192];
        private ulong _bitBuffer;
        private int _bufferedBits;
        private int _bufferOffset;
        private int _bufferCount;
        private long _sourceBytesLoaded;
        private long _bytesConsumed;

        public long Length { get; } = length;
        public long BytesConsumed => _bytesConsumed;

        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            if (count is < 0 or > 16) return false;
            while (_bufferedBits < count)
            {
                if (!TryReadByte(out var next)) return false;
                _bitBuffer |= (ulong)next << _bufferedBits;
                _bufferedBits += 8;
            }

            var mask = count == 0 ? 0UL : (1UL << count) - 1;
            value = (uint)(_bitBuffer & mask);
            _bitBuffer >>= count;
            _bufferedBits -= count;
            return true;
        }

        public void AlignToByte()
        {
            var discard = _bufferedBits % 8;
            _bitBuffer >>= discard;
            _bufferedBits -= discard;
        }

        private bool TryReadByte(out byte value)
        {
            if (_bufferOffset >= _bufferCount)
            {
                var remaining = Length - _sourceBytesLoaded;
                if (remaining <= 0)
                {
                    value = 0;
                    return false;
                }

                source.Position = start + _sourceBytesLoaded;
                _bufferOffset = 0;
                _bufferCount = source.Read(_buffer, 0, (int)Math.Min(_buffer.Length, remaining));
                _sourceBytesLoaded += _bufferCount;
                if (_bufferCount == 0)
                {
                    value = 0;
                    return false;
                }
            }

            value = _buffer[_bufferOffset++];
            _bytesConsumed++;
            return true;
        }
    }

    private static bool ZipDataDescriptorMatches(
        Stream input,
        long offset,
        long centralOffset,
        uint crc,
        uint compressed,
        uint uncompressed,
        out int descriptorLength)
    {
        descriptorLength = 0;
        Span<byte> first = stackalloc byte[4];
        if (offset > centralOffset - 12 || !ReadExactlyAt(input, offset, first)) return false;
        Span<byte> values = stackalloc byte[12];
        if (ReadExactlyAt(input, offset, values) &&
            BinaryPrimitives.ReadUInt32LittleEndian(values[..4]) == crc &&
            BinaryPrimitives.ReadUInt32LittleEndian(values[4..8]) == compressed &&
            BinaryPrimitives.ReadUInt32LittleEndian(values[8..12]) == uncompressed)
        {
            descriptorLength = 12;
            return true;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(first) != 0x08074b50 || offset > centralOffset - 16 ||
            !ReadExactlyAt(input, offset + 4, values) ||
            BinaryPrimitives.ReadUInt32LittleEndian(values[..4]) != crc ||
            BinaryPrimitives.ReadUInt32LittleEndian(values[4..8]) != compressed ||
            BinaryPrimitives.ReadUInt32LittleEndian(values[8..12]) != uncompressed) return false;

        descriptorLength = 16;
        return true;
    }

    private static bool IsSafePackageEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '/' || name.Contains('\\') || name.Contains('\0') || name.Contains(':'))
        {
            return false;
        }

        return !name.Split('/').Any(segment => segment is ".." or ".");
    }

    private static int ReadTiffMagic(ReadOnlySpan<byte> header) => header[0] == (byte)'I'
        ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..4])
        : BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);

    private static ushort ReadTiffUInt16(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(value)
        : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint ReadTiffUInt32(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(value)
        : BinaryPrimitives.ReadUInt32BigEndian(value);

    private static bool TryReadTiffValues(
        Stream input,
        ReadOnlySpan<byte> valueField,
        uint valueCount,
        ushort type,
        bool littleEndian,
        long fileLength,
        out uint[] values)
    {
        values = [];
        if (valueCount == 0 || valueCount > MaximumTiffArrayValues || type is not 3 and not 4) return false;
        var elementSize = type == 3 ? 2 : 4;
        var encodedLength = (long)valueCount * elementSize;
        byte[] encodedValues;
        if (encodedLength <= valueField.Length)
        {
            encodedValues = valueField[..(int)encodedLength].ToArray();
        }
        else
        {
            var valuesOffset = ReadTiffUInt32(valueField, littleEndian);
            if (valuesOffset < 8 || valuesOffset > fileLength || encodedLength > fileLength - valuesOffset) return false;
            encodedValues = new byte[(int)encodedLength];
            if (!ReadExactlyAt(input, valuesOffset, encodedValues)) return false;
        }

        values = new uint[(int)valueCount];
        for (var index = 0; index < values.Length; index++)
        {
            var value = encodedValues.AsSpan(index * elementSize, elementSize);
            values[index] = type == 3 ? ReadTiffUInt16(value, littleEndian) : ReadTiffUInt32(value, littleEndian);
        }

        return true;
    }

    private static bool TiffDataRangesAreValid(uint[] offsets, uint[] byteCounts, long fileLength)
    {
        if (offsets.Length == 0 || offsets.Length != byteCounts.Length) return false;
        for (var index = 0; index < offsets.Length; index++)
        {
            var offset = offsets[index];
            var byteCount = byteCounts[index];
            if (offset < 8 || offset >= fileLength || byteCount == 0 || byteCount > fileLength - offset) return false;
        }

        return true;
    }

    private static SourceFormat SourceFormatForExtension(string extension) => extension.Trim().ToLowerInvariant() switch
    {
        ".pdf" => SourceFormat.Pdf,
        ".jpg" or ".jpeg" => SourceFormat.Jpeg,
        ".png" => SourceFormat.Png,
        ".tif" or ".tiff" => SourceFormat.Tiff,
        ".doc" => SourceFormat.Doc,
        ".docx" => SourceFormat.Docx,
        ".rtf" => SourceFormat.Rtf,
        ".xls" => SourceFormat.Xls,
        ".xlsx" => SourceFormat.Xlsx,
        ".ppt" => SourceFormat.Ppt,
        ".pptx" => SourceFormat.Pptx,
        ".odt" => SourceFormat.Odt,
        ".ods" => SourceFormat.Ods,
        ".odp" => SourceFormat.Odp,
        _ => SourceFormat.Unknown
    };

    private static string? MediaTypeFor(SourceFormat format) => format switch
    {
        SourceFormat.Pdf => "application/pdf",
        SourceFormat.Jpeg => "image/jpeg",
        SourceFormat.Png => "image/png",
        SourceFormat.Tiff => "image/tiff",
        SourceFormat.Doc => "application/msword",
        SourceFormat.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        SourceFormat.Rtf => "text/rtf",
        SourceFormat.Xls => "application/vnd.ms-excel",
        SourceFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        SourceFormat.Ppt => "application/vnd.ms-powerpoint",
        SourceFormat.Pptx => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        SourceFormat.Odt => "application/vnd.oasis.opendocument.text",
        SourceFormat.Ods => "application/vnd.oasis.opendocument.spreadsheet",
        SourceFormat.Odp => "application/vnd.oasis.opendocument.presentation",
        _ => null
    };

    private static IntakeDiagnostic Error(IntakeDiagnosticCode code, string message) => new(IntakeSeverity.Error, code, message);

    private static SourcePreflightResult Result(
        SourceFormat format,
        SourcePreflightStatus status,
        ImmutableArray<IntakeDiagnostic>.Builder diagnostics) =>
        new(format, MediaTypeFor(format), status, diagnostics.ToImmutable());

    private static byte[] ReadAtMost(Stream input, long offset, int count)
    {
        input.Position = offset;
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var amount = input.Read(bytes, read, count - read);
            if (amount == 0) break;
            read += amount;
        }

        if (read == bytes.Length) return bytes;
        Array.Resize(ref bytes, read);
        return bytes;
    }

    private static byte[] ReadTail(Stream input, long length, int maximumBytes) =>
        ReadAtMost(input, Math.Max(0, length - maximumBytes), (int)Math.Min(length, maximumBytes));

    private static bool ReadExactlyAt(Stream input, long offset, Span<byte> destination)
    {
        input.Position = offset;
        var total = 0;
        while (total < destination.Length)
        {
            var read = input.Read(destination[total..]);
            if (read == 0) return false;
            total += read;
        }

        return true;
    }

    private sealed record ZipEntryMetadata(
        string Name,
        ushort Flags,
        ushort Method,
        uint Crc32,
        uint CompressedLength,
        uint Length,
        uint LocalOffset,
        ushort LocalNameLength,
        ushort LocalExtraLength,
        long DataOffset,
        long DataEnd,
        int DescriptorLength);

    private sealed class BoundedReadStream(Stream source, long start, long length, bool rejectUnexpectedEnd = false) : Stream
    {
        private long _position;

        public long Remaining => length - _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Remaining);
            if (count == 0)
            {
                if (rejectUnexpectedEnd) throw new InvalidDataException("The compressed entry ended before the DEFLATE final block.");
                return 0;
            }

            source.Position = start + _position;
            var read = source.Read(buffer[..count]);
            if (read == 0 && rejectUnexpectedEnd)
            {
                throw new InvalidDataException("The compressed entry ended before the DEFLATE final block.");
            }

            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RtfByteReader(Stream source, long length)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _offset;
        private int _count;
        private long _sourcePosition;
        private bool _hasPending;
        private byte _pending;

        public bool TryRead(out byte value)
        {
            if (_hasPending)
            {
                _hasPending = false;
                value = _pending;
                return true;
            }

            if (_offset >= _count)
            {
                var remaining = length - _sourcePosition;
                if (remaining <= 0)
                {
                    value = 0;
                    return false;
                }

                _offset = 0;
                _count = source.Read(_buffer, 0, (int)Math.Min(_buffer.Length, remaining));
                _sourcePosition += _count;
                if (_count == 0)
                {
                    value = 0;
                    return false;
                }
            }

            value = _buffer[_offset++];
            return true;
        }

        public void PushBack(byte value)
        {
            if (_hasPending) throw new InvalidOperationException("Only one RTF byte of lookahead is supported.");
            _hasPending = true;
            _pending = value;
        }

        public bool Skip(long count)
        {
            while (count-- > 0)
            {
                if (!TryRead(out _)) return false;
            }

            return true;
        }
    }
}
