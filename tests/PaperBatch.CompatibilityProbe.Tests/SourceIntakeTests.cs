using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;

internal static class SourceIntakeTests
{
    internal static void EnumerationPreservesOrderAndNaturalFolderOrdering()
    {
        using var temporary = new TemporaryDirectory();
        var folder = Path.Combine(temporary.Path, "folder");
        Directory.CreateDirectory(folder);
        var explicitPath = Path.Combine(temporary.Path, "z10.pdf");
        File.WriteAllBytes(explicitPath, PdfBytes());
        File.WriteAllBytes(Path.Combine(folder, "page10.pdf"), PdfBytes());
        File.WriteAllBytes(Path.Combine(folder, "page2.pdf"), PdfBytes());
        File.WriteAllBytes(Path.Combine(folder, "Page1.pdf"), PdfBytes());

        var result = SourceEnumerator.Enumerate(
            [new SourceInput(explicitPath), new SourceInput(folder)],
            new SourceEnumerationOptions { Recursive = false });

        Assert.True(result.Items.Select(item => Path.GetFileName(item.Path)).SequenceEqual(
            ["z10.pdf", "Page1.pdf", "page2.pdf", "page10.pdf"], StringComparer.Ordinal));
    }

    internal static void EnumerationRecursesOnlyWhenRequestedAndSkipsLinks()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "root");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var topFile = Path.Combine(root, "top.pdf");
        var nestedFile = Path.Combine(nested, "nested.pdf");
        File.WriteAllBytes(topFile, PdfBytes());
        File.WriteAllBytes(nestedFile, PdfBytes());

        var shallow = SourceEnumerator.Enumerate([new SourceInput(root)]);
        Assert.Equal(1, shallow.Items.Count(item => item.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)));

        var recursive = SourceEnumerator.Enumerate(
            [new SourceInput(root)],
            new SourceEnumerationOptions { Recursive = true });
        Assert.True(recursive.Items.Any(item => item.Path == Path.GetFullPath(nestedFile)));

        var link = Path.Combine(root, "nested-link");
        try
        {
            Directory.CreateSymbolicLink(link, nested);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var withLink = SourceEnumerator.Enumerate(
            [new SourceInput(root)],
            new SourceEnumerationOptions { Recursive = true });
        Assert.Equal(1, withLink.Items.Count(item => item.Path.EndsWith("nested.pdf", StringComparison.Ordinal)));
        Assert.True(withLink.Items.Any(item => item.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ReparsePointSkipped)));

        var nestedFolder = Path.Combine(nested, "inner");
        Directory.CreateDirectory(nestedFolder);
        var nestedFolderFile = Path.Combine(nestedFolder, "inner.pdf");
        File.WriteAllBytes(nestedFolderFile, PdfBytes());
        var linkedFile = SourceEnumerator.Enumerate([new SourceInput(Path.Combine(link, "nested.pdf"))]);
        Assert.True(linkedFile.Items.Single().Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ReparsePointSkipped));
        var linkedFolder = SourceEnumerator.Enumerate([new SourceInput(Path.Combine(link, "inner"))]);
        Assert.True(linkedFolder.Items.Single().Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ReparsePointSkipped));
    }

    internal static void DuplicateAliasesAndIntentionalCopiesRemainVisible()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "résumé.pdf");
        File.WriteAllBytes(source, PdfBytes());
        var alias = Path.Combine(temporary.Path, "nested", "..", "résumé.pdf");

        var duplicate = SourceEnumerator.Enumerate([new SourceInput(source), new SourceInput(alias)]);
        Assert.Equal(2, duplicate.Items.Length);
        Assert.True(duplicate.Items[1].IsDuplicate);
        Assert.Equal(Path.GetFullPath(source), duplicate.Items[1].DuplicateOfPath!);
        Assert.True(duplicate.Items[1].Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.DuplicatePath));

        var intentional = SourceEnumerator.Enumerate([
            new SourceInput(source),
            new SourceInput(alias, IntentionalCopy: true)
        ]);
        Assert.Equal(2, intentional.Items.Length);
        Assert.True(intentional.Items[1].IsIntentionalCopy);
        Assert.True(!intentional.Items[1].IsDuplicate);
        Assert.True(!intentional.Items[1].Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.DuplicatePath));
        Assert.True(intentional.Diagnostics.All(diagnostic => diagnostic.Code != IntakeDiagnosticCode.DuplicatePath));

        var folder = Path.Combine(temporary.Path, "bundle");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "copy.pdf"), PdfBytes());
        var repeatedFolder = SourceEnumerator.Enumerate([
            new SourceInput(folder),
            new SourceInput(folder, IntentionalCopy: true)
        ], new SourceEnumerationOptions { Recursive = true });
        Assert.Equal(2, repeatedFolder.Items.Length);
        Assert.True(repeatedFolder.Items[1].IsIntentionalCopy);
        Assert.True(!repeatedFolder.Items[1].IsDuplicate);

        var upperCase = Path.Combine(temporary.Path, "Case-Sensitive.pdf");
        var lowerCase = Path.Combine(temporary.Path, "case-sensitive.pdf");
        File.WriteAllBytes(upperCase, PdfBytes());
        if (!OperatingSystem.IsWindows()) File.WriteAllBytes(lowerCase, PdfBytes());
        var caseResult = SourceEnumerator.Enumerate([new SourceInput(upperCase), new SourceInput(lowerCase)]);
        Assert.Equal(OperatingSystem.IsWindows(), caseResult.Items[1].IsDuplicate);
    }

    internal static void EnumerationReportsStableSanitizedFailuresAndCancellation()
    {
        using var temporary = new TemporaryDirectory();
        var missing = Path.Combine(temporary.Path, "private-customer-name.pdf");
        var result = SourceEnumerator.Enumerate([new SourceInput(missing)]);

        Assert.Equal(IntakeDiagnosticCode.InputVanished, result.Items[0].Diagnostics[0].Code);
        Assert.True(!result.Items[0].Diagnostics[0].Message.Contains(missing, StringComparison.Ordinal));

        var unicode = Path.Combine(temporary.Path, "資料-2.pdf");
        File.WriteAllBytes(unicode, PdfBytes());
        var unicodeResult = SourceEnumerator.Enumerate([new SourceInput(unicode)]);
        Assert.True(unicodeResult.Items[0].Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnicodePathSupported));

        var longPath = SourceEnumerator.Enumerate(
            [new SourceInput(unicode)],
            new SourceEnumerationOptions { MaximumPathLength = 1 });
        Assert.Equal(IntakeDiagnosticCode.PathTooLong, longPath.Items[0].Diagnostics[0].Code);

        var secondExplicit = Path.Combine(temporary.Path, "second-explicit.pdf");
        File.WriteAllBytes(secondExplicit, PdfBytes());
        var limitedExplicitSelection = SourceEnumerator.Enumerate(
            [new SourceInput(unicode), new SourceInput(secondExplicit)],
            new SourceEnumerationOptions { MaximumEntries = 1 });
        Assert.Equal(2, limitedExplicitSelection.Items.Length);
        Assert.True(!limitedExplicitSelection.Items[0].HasErrors);
        Assert.Equal(IntakeDiagnosticCode.EnumerationLimitExceeded, limitedExplicitSelection.Items[1].Diagnostics[0].Code);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = SourceEnumerator.Enumerate([new SourceInput(unicode)], cancellationToken: cancellation.Token);
        Assert.True(cancelled.IsCancelled);
        Assert.True(cancelled.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.CancellationRequested));
    }

    internal static void EnumerationCountsInvalidExplicitSelectionsTowardLimit()
    {
        using var temporary = new TemporaryDirectory();
        var missingPaths = Enumerable.Range(0, 4)
            .Select(index => new SourceInput(Path.Combine(temporary.Path, $"missing-{index}.pdf")))
            .ToArray();
        var result = SourceEnumerator.Enumerate(missingPaths, new SourceEnumerationOptions { MaximumEntries = 1 });

        Assert.Equal(2, result.Items.Length);
        Assert.Equal(IntakeDiagnosticCode.InputVanished, result.Items[0].Diagnostics.Single().Code);
        Assert.Equal(IntakeDiagnosticCode.EnumerationLimitExceeded, result.Items[1].Diagnostics.Single().Code);
    }

    internal static void EnumerationRejectsDeviceAttributes()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "device-source.pdf");
        File.WriteAllBytes(source, PdfBytes());

        var result = SourceEnumerator.Enumerate(
            [new SourceInput(source)],
            attributeAccess: new DevicePathAttributeAccess(source));

        Assert.Equal(1, result.Items.Length);
        Assert.True(result.Items[0].HasErrors);
        Assert.Equal(IntakeDiagnosticCode.SpecialFileSkipped, result.Items.Single().Diagnostics.Single().Code);
    }

    internal static void WindowsExtendedLocalPathIsNotTreatedAsNetworkPath()
    {
        Assert.True(SourceEnumerator.IsNetworkPath(@"\\server\share\document.pdf", isWindows: true));
        Assert.True(SourceEnumerator.IsNetworkPath(@"\\?\UNC\server\share\document.pdf", isWindows: true));
        Assert.True(!SourceEnumerator.IsNetworkPath(@"\\?\C:\docs\document.pdf", isWindows: true));
        Assert.True(!SourceEnumerator.IsNetworkPath(@"\\.\PhysicalDrive0", isWindows: true));
        Assert.True(!SourceEnumerator.IsNetworkPath(@"\\server\share\document.pdf", isWindows: false));
    }

    internal static void PreflightRecognizesSupportedFormatFamilies()
    {
        var cases = new (string Extension, SourceFormat Format, byte[] Bytes)[]
        {
            (".pdf", SourceFormat.Pdf, PdfBytes()),
            (".jpg", SourceFormat.Jpeg, JpegBytes()),
            (".png", SourceFormat.Png, PngBytes()),
            (".tiff", SourceFormat.Tiff, TiffMultiPageBytes()),
            (".doc", SourceFormat.Doc, OleBytes("WordDocument")),
            (".docx", SourceFormat.Docx, ZipBytes("[Content_Types].xml", "_rels/.rels", "word/document.xml")),
            (".rtf", SourceFormat.Rtf, Encoding.ASCII.GetBytes("{\\rtf1 test}")),
            (".xls", SourceFormat.Xls, OleBytes("Workbook")),
            (".xlsx", SourceFormat.Xlsx, ZipBytes("[Content_Types].xml", "_rels/.rels", "xl/workbook.xml")),
            (".ppt", SourceFormat.Ppt, OleBytes("PowerPoint Document")),
            (".pptx", SourceFormat.Pptx, ZipBytes("[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml")),
            (".odt", SourceFormat.Odt, OdfBytes("application/vnd.oasis.opendocument.text")),
            (".ods", SourceFormat.Ods, OdfBytes("application/vnd.oasis.opendocument.spreadsheet")),
            (".odp", SourceFormat.Odp, OdfBytes("application/vnd.oasis.opendocument.presentation"))
        };

        foreach (var (extension, expectedFormat, bytes) in cases)
        {
            using var input = new MemoryStream(bytes, writable: false);
            var result = SourceFormatPreflight.Inspect(input, extension);
            Assert.Equal(expectedFormat, result.Format);
            Assert.Equal(SourcePreflightStatus.Ready, result.Status);
        }

        using var bigTiff = new MemoryStream(BigTiffHeaderBytes(), writable: false);
        var bigTiffResult = SourceFormatPreflight.Inspect(bigTiff, ".tif");
        Assert.Equal(SourceFormat.Tiff, bigTiffResult.Format);
        Assert.True(bigTiffResult.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsupportedFormat));
        Assert.True(bigTiffResult.Diagnostics.All(diagnostic => diagnostic.Code != IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightRejectsMismatchMalformedEncryptionAndUnsafePackages()
    {
        using var empty = new MemoryStream([]);
        Assert.True(SourceFormatPreflight.Inspect(empty, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EmptySource));

        using var truncatedPdf = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\ntruncated"));
        Assert.True(SourceFormatPreflight.Inspect(truncatedPdf, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var mismatch = new MemoryStream([0xff, 0xd8, 0xff, 0xd9]);
        Assert.True(SourceFormatPreflight.Inspect(mismatch, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ExtensionSignatureMismatch));

        using var markerOnlyJpeg = new MemoryStream([0xff, 0xd8, 0xff, 0xd9]);
        Assert.True(SourceFormatPreflight.Inspect(markerOnlyJpeg, ".jpg").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var emptyScanJpeg = new MemoryStream(JpegBytes(includeScanData: false));
        Assert.True(SourceFormatPreflight.Inspect(emptyScanJpeg, ".jpg").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var oversizedJpeg = new MemoryStream(JpegBytes(width: 2, height: 2));
        Assert.True(SourceFormatPreflight.Inspect(oversizedJpeg, ".jpg", new SourcePreflightOptions { MaximumRasterPixels = 3 })
            .Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsafePackage));

        using var emptyIfdTiff = new MemoryStream(EmptyIfdTiffBytes());
        Assert.True(SourceFormatPreflight.Inspect(emptyIfdTiff, ".tiff").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var dimensionlessTiff = new MemoryStream(DimensionlessTiffBytes());
        Assert.True(SourceFormatPreflight.Inspect(dimensionlessTiff, ".tiff").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var oversizedTiff = new MemoryStream(TiffBytes(width: 50_000, height: 50_000));
        Assert.True(SourceFormatPreflight.Inspect(oversizedTiff, ".tiff").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsafePackage));

        using var pdfLiteralEncrypt = new MemoryStream(PdfBytes(includeLiteralEncrypt: true));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(pdfLiteralEncrypt, ".pdf").Status);

        using var encryptedPdf = new MemoryStream(PdfBytes(encrypted: true));
        Assert.True(SourceFormatPreflight.Inspect(encryptedPdf, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));

        using var nestedTrailerEncrypt = new MemoryStream(PdfBytes(includeNestedEncrypt: true));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(nestedTrailerEncrypt, ".pdf").Status);

        using var escapedPasswordText = new MemoryStream(Encoding.ASCII.GetBytes("{\\rtf1 text \\\\passwordhash}"));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(escapedPasswordText, ".rtf").Status);

        using var binaryPasswordText = new MemoryStream(Encoding.ASCII.GetBytes("{\\rtf1\\bin13 \\passwordhash}"));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(binaryPasswordText, ".rtf").Status);

        using var truncatedRtfBinary = new MemoryStream(Encoding.ASCII.GetBytes("{\\rtf1\\bin100 xx}"));
        Assert.True(SourceFormatPreflight.Inspect(truncatedRtfBinary, ".rtf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var lateRtfPassword = new MemoryStream(Encoding.ASCII.GetBytes("{\\rtf1 " + new string('x', 5000) + "\\passwordhash1}"));
        Assert.True(SourceFormatPreflight.Inspect(lateRtfPassword, ".rtf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));

        using var invalidXrefOffset = new MemoryStream(PdfBytes(xrefOverride: 0));
        Assert.True(SourceFormatPreflight.Inspect(invalidXrefOffset, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var xrefStreamText = new MemoryStream(PdfXrefStreamBytes(encrypted: false));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(xrefStreamText, ".pdf").Status);

        using var flateXrefStream = new MemoryStream(PdfXrefStreamBytes(encrypted: false, compressWithFlate: true));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(flateXrefStream, ".pdf").Status);

        using var shortDefaultXrefIndex = new MemoryStream(PdfXrefStreamBytes(encrypted: false, includeIndex: false));
        Assert.True(SourceFormatPreflight.Inspect(shortDefaultXrefIndex, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var fullDefaultXrefIndex = new MemoryStream(PdfXrefStreamBytes(encrypted: false, includeIndex: false, completeDefaultIndex: true));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(fullDefaultXrefIndex, ".pdf").Status);

        using var filteredXrefStream = new MemoryStream(PdfXrefStreamBytes(encrypted: false, includeUnsupportedFilter: true));
        Assert.True(SourceFormatPreflight.Inspect(filteredXrefStream, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var xrefStreamMissingFields = new MemoryStream(PdfXrefStreamBytes(encrypted: false, includeRequiredFields: false));
        Assert.True(SourceFormatPreflight.Inspect(xrefStreamMissingFields, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var truncatedXrefStream = new MemoryStream(PdfXrefStreamBytes(encrypted: false, truncateStream: true));
        Assert.True(SourceFormatPreflight.Inspect(truncatedXrefStream, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var encryptedXrefStream = new MemoryStream(PdfXrefStreamBytes(encrypted: true));
        Assert.True(SourceFormatPreflight.Inspect(encryptedXrefStream, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));

        using var legacyOfficeWithPackageExtension = new MemoryStream(OleBytes("WordDocument"));
        Assert.True(SourceFormatPreflight.Inspect(legacyOfficeWithPackageExtension, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ExtensionSignatureMismatch));

        using var encryptedOfficePackage = new MemoryStream(OleBytes("EncryptionInfo", "EncryptedPackage"));
        Assert.True(SourceFormatPreflight.Inspect(encryptedOfficePackage, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));

        using var malformedLegacyOffice = new MemoryStream(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0, 0, 0, 0 });
        Assert.True(SourceFormatPreflight.Inspect(malformedLegacyOffice, ".doc").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var legacyWordWithoutFib = OleBytes("WordDocument");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(legacyWordWithoutFib.AsSpan(1536, 2), 0);
        using var malformedLegacyWord = new MemoryStream(legacyWordWithoutFib);
        Assert.True(SourceFormatPreflight.Inspect(malformedLegacyWord, ".doc").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var brokenFat = OleBytes("WordDocument");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(brokenFat.AsSpan(512, 4), uint.MaxValue);
        using var invalidFatOffice = new MemoryStream(brokenFat);
        Assert.True(SourceFormatPreflight.Inspect(invalidFatOffice, ".doc").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var unrelatedCompoundFile = new MemoryStream(OleBytes("Unrelated Storage"));
        Assert.True(SourceFormatPreflight.Inspect(unrelatedCompoundFile, ".doc").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var twoLargeOleStreams = OleBytes("WordDocument", "OtherLargeStream");
        using var validLargeOleStreams = new MemoryStream(twoLargeOleStreams);
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(validLargeOleStreams, ".doc").Status);

        var overlappingOleChains = twoLargeOleStreams.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(overlappingOleChains.AsSpan(1024 + 256 + 116, 4), 2);
        using var overlappingOleStreams = new MemoryStream(overlappingOleChains);
        Assert.True(SourceFormatPreflight.Inspect(overlappingOleStreams, ".doc").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var unsupported = new MemoryStream([1, 2, 3]);
        Assert.True(SourceFormatPreflight.Inspect(unsupported, ".wps").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsupportedFormat));

        using var bomb = new MemoryStream(ZipBytes(("[Content_Types].xml", new string('x', 64 * 1024)), ("word/document.xml", "x")));
        var limited = SourceFormatPreflight.Inspect(bomb, ".docx", new SourcePreflightOptions { MaximumPackageCompressionRatio = 4 });
        Assert.True(limited.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsafePackage));

        var localEncryptionMismatch = ZipBytes(("[Content_Types].xml", "<root/>"), ("word/document.xml", "<root/>"));
        localEncryptionMismatch[6] |= 1;
        using var mismatchedLocalEncryption = new MemoryStream(localEncryptionMismatch);
        Assert.True(SourceFormatPreflight.Inspect(mismatchedLocalEncryption, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var localMethodMismatch = ZipBytes(("[Content_Types].xml", "<root/>"), ("word/document.xml", "<root/>"));
        localMethodMismatch[8] = 0;
        localMethodMismatch[9] = 0;
        using var mismatchedLocalMethod = new MemoryStream(localMethodMismatch);
        Assert.True(SourceFormatPreflight.Inspect(mismatchedLocalMethod, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var localCrcMismatch = ZipBytes(("[Content_Types].xml", "<root/>"), ("word/document.xml", "<root/>"));
        localCrcMismatch[14] ^= 0x01;
        using var mismatchedLocalCrc = new MemoryStream(localCrcMismatch);
        Assert.True(SourceFormatPreflight.Inspect(mismatchedLocalCrc, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var localCompressedSizeMismatch = ZipBytes(("[Content_Types].xml", "<root/>"), ("word/document.xml", "<root/>"));
        localCompressedSizeMismatch[18] ^= 0x01;
        using var mismatchedLocalCompressedSize = new MemoryStream(localCompressedSizeMismatch);
        Assert.True(SourceFormatPreflight.Inspect(mismatchedLocalCompressedSize, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var localUncompressedSizeMismatch = ZipBytes(("[Content_Types].xml", "<root/>"), ("word/document.xml", "<root/>"));
        localUncompressedSizeMismatch[22] ^= 0x01;
        using var mismatchedLocalUncompressedSize = new MemoryStream(localUncompressedSizeMismatch);
        Assert.True(SourceFormatPreflight.Inspect(mismatchedLocalUncompressedSize, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var validZipDescriptors = new MemoryStream(ZipWithDataDescriptors());
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(validZipDescriptors, ".docx").Status);

        using var invalidZipDescriptor = new MemoryStream(ZipWithDataDescriptors(corruptFirstDescriptor: true));
        Assert.True(SourceFormatPreflight.Inspect(invalidZipDescriptor, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var corruptedZipPayload = ZipWithDataDescriptors();
        corruptedZipPayload[30 + Encoding.ASCII.GetByteCount("[Content_Types].xml")] ^= 0x01;
        using var invalidZipPayload = new MemoryStream(corruptedZipPayload);
        Assert.True(SourceFormatPreflight.Inspect(invalidZipPayload, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        var invalidPngCrc = PngBytes();
        invalidPngCrc[29] ^= 0x01;
        using var corruptedPng = new MemoryStream(invalidPngCrc);
        Assert.True(SourceFormatPreflight.Inspect(corruptedPng, ".png").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var invalidPngHeader = new MemoryStream(PngBytes(bitDepth: 3));
        Assert.True(SourceFormatPreflight.Inspect(invalidPngHeader, ".png").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var invalidLaterTiffStrip = new MemoryStream(TiffMultiStripBytes(invalidSecondOffset: true));
        Assert.True(SourceFormatPreflight.Inspect(invalidLaterTiffStrip, ".tiff").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var mismatchedTiffStripArrays = new MemoryStream(TiffMultiStripBytes(mismatchedCounts: true));
        Assert.True(SourceFormatPreflight.Inspect(mismatchedTiffStripArrays, ".tiff").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var invalidNextTiffDirectory = new MemoryStream(TiffMultiPageBytes(invalidNextDirectory: true));
        Assert.True(SourceFormatPreflight.Inspect(invalidNextTiffDirectory, ".tiff").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightAcceptsPdfXrefStreamPngUpPredictor()
    {
        using var predictedXref = new MemoryStream(PdfXrefStreamBytes(encrypted: false, pngUpPredictor: true));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(predictedXref, ".pdf").Status);

        using var unsupportedPredictor = new MemoryStream(PdfXrefStreamBytes(encrypted: false, pngUpPredictor: true, unsupportedPredictor: true));
        Assert.True(SourceFormatPreflight.Inspect(unsupportedPredictor, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var badPredictedXrefOffset = new MemoryStream(PdfXrefStreamBytes(encrypted: false, pngUpPredictor: true, invalidXrefRecord: true));
        Assert.True(SourceFormatPreflight.Inspect(badPredictedXrefOffset, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var unsupportedXrefEntryType = new MemoryStream(PdfXrefStreamBytes(encrypted: false, pngUpPredictor: true, unsupportedXrefEntryType: true));
        Assert.True(SourceFormatPreflight.Inspect(unsupportedXrefEntryType, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var invalidFreeObjectReference = new MemoryStream(PdfXrefStreamBytes(encrypted: false, pngUpPredictor: true, invalidFreeObjectReference: true));
        Assert.True(SourceFormatPreflight.Inspect(invalidFreeObjectReference, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));

        using var invalidObjectStreamReference = new MemoryStream(PdfXrefStreamBytes(encrypted: false, pngUpPredictor: true, invalidObjectStreamReference: true));
        Assert.True(SourceFormatPreflight.Inspect(invalidObjectStreamReference, ".pdf").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightRejectsPngWithoutIdatPayload()
    {
        using var input = new MemoryStream(PngWithoutImageDataBytes());
        var result = SourceFormatPreflight.Inspect(input, ".png");
        Assert.Equal(SourcePreflightStatus.Rejected, result.Status);
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightDetectsIncrementalPdfEncryption()
    {
        using var input = new MemoryStream(PdfIncrementalEncryptedBytes());
        var result = SourceFormatPreflight.Inspect(input, ".pdf");
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));
    }

    internal static void PreflightRejectsTruncatedDeflateStream()
    {
        using var input = new MemoryStream(ZipWithMissingFinalDeflateBlock());
        var result = SourceFormatPreflight.Inspect(input, ".docx");
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightRejectsTrailingDeflateBytes()
    {
        using var validInput = new MemoryStream(ZipWithCompleteDeflateStream());
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(validInput, ".docx").Status);

        var repetitiveContent = Encoding.ASCII.GetBytes("<root>" + string.Concat(Enumerable.Repeat("alpha beta gamma delta 0123456789 ", 5000)) + "</root>");
        using var largerValidInput = new MemoryStream(ZipWithCompleteDeflateStream(repetitiveContent));
        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(largerValidInput, ".docx").Status);

        using var input = new MemoryStream(ZipWithTrailingDeflateBytes());
        var result = SourceFormatPreflight.Inspect(input, ".docx");
        Assert.Equal(SourcePreflightStatus.Rejected, result.Status);
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightRejectsNonFinitePackageCompressionLimits()
    {
        foreach (var ratio in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            using var input = new MemoryStream(ZipBytes("[Content_Types].xml", "word/document.xml"));
            _ = Capture<ArgumentOutOfRangeException>(() => SourceFormatPreflight.Inspect(
                input,
                ".docx",
                new SourcePreflightOptions { MaximumPackageCompressionRatio = ratio }));
        }
    }

    internal static void PreflightRejectsRepeatedTiffArrayTags()
    {
        using var input = new MemoryStream(TiffRepeatedStripOffsetTagBytes());
        var result = SourceFormatPreflight.Inspect(input, ".tiff");
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
    }

    internal static void PreflightBoundsCumulativeTiffArrayWork()
    {
        using var input = new MemoryStream(TiffMultiPageLargeArraysBytes());
        var result = SourceFormatPreflight.Inspect(input, ".tiff");
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsafePackage));
    }

    internal static void PreflightRecognizesLegacyOfficeWithExtendedDifat()
    {
        using var input = new MemoryStream(OleBytesWithManyFatSectors(512));
        var result = SourceFormatPreflight.Inspect(input, ".doc");
        Assert.Equal(SourcePreflightStatus.Ready, result.Status);
    }

    internal static void PreflightRejectsLegacyOfficeFamilyStreamsInNestedStorage()
    {
        foreach (var (extension, streamName) in new[]
                 {
                     (".doc", "WordDocument"),
                     (".xls", "Workbook"),
                     (".xls", "Book"),
                     (".ppt", "PowerPoint Document")
                 })
        {
            using var input = new MemoryStream(OleBytesWithNestedStorageStream(streamName));
            var result = SourceFormatPreflight.Inspect(input, extension);
            Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
        }
    }

    internal static void PreflightDetectsEncryptedLegacyWord()
    {
        var encryptedLegacyWord = OleBytes("WordDocument");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encryptedLegacyWord.AsSpan(1536, 2), 0xa5ec);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encryptedLegacyWord.AsSpan(1536 + 10, 2), 0x0100);
        using var input = new MemoryStream(encryptedLegacyWord);

        Assert.True(SourceFormatPreflight.Inspect(input, ".doc").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));
    }

    internal static void PreflightDetectsEncryptedLegacyExcel()
    {
        var encryptedLegacyExcel = OleBytes("Workbook");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encryptedLegacyExcel.AsSpan(1536, 2), 0x0809);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encryptedLegacyExcel.AsSpan(1536 + 2, 2), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encryptedLegacyExcel.AsSpan(1536 + 20, 2), 0x002f);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encryptedLegacyExcel.AsSpan(1536 + 22, 2), 2);
        using var input = new MemoryStream(encryptedLegacyExcel);

        Assert.True(SourceFormatPreflight.Inspect(input, ".xls").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));
    }

    internal static void PreflightRejectsOoxmlWithoutRootRelationships()
    {
        using var input = new MemoryStream(ZipBytes("[Content_Types].xml", "word/document.xml"));

        Assert.True(SourceFormatPreflight.Inspect(input, ".docx").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ExtensionSignatureMismatch));
    }

    internal static void PreflightRejectsOpenDocumentWithoutContent()
    {
        using var input = new MemoryStream(OdfBytes("application/vnd.oasis.opendocument.text", includeContent: false));

        Assert.True(SourceFormatPreflight.Inspect(input, ".odt").Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.ExtensionSignatureMismatch));
    }

    internal static void PreflightDetectsEncryptedOpenDocumentManifest()
    {
        var manifest = """<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3"><manifest:file-entry manifest:full-path="/" manifest:media-type="__MIME__"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"><manifest:encryption-data manifest:checksum-type="SHA1/1K" manifest:checksum="AAAAAAAAAAAAAAAAAAAAAA=="><manifest:algorithm manifest:algorithm-name="Blowfish CFB" manifest:initialisation-vector="AAAAAAAAAAAAAAAAAAAAAA=="/><manifest:key-derivation manifest:key-derivation-name="PBKDF2" manifest:iteration-count="1024" manifest:key-size="16" manifest:salt="AAAAAAAAAAAAAAAAAAAAAA=="/></manifest:encryption-data></manifest:file-entry></manifest:manifest>""";
        foreach (var (extension, mediaType) in new[]
        {
            (".odt", "application/vnd.oasis.opendocument.text"),
            (".ods", "application/vnd.oasis.opendocument.spreadsheet"),
            (".odp", "application/vnd.oasis.opendocument.presentation")
        })
        {
            var familyManifest = manifest.Replace("__MIME__", mediaType, StringComparison.Ordinal);
            using var input = new MemoryStream(OdfBytes(mediaType, manifestContent: familyManifest, contentContent: "synthetic encrypted content"));
            var result = SourceFormatPreflight.Inspect(input, extension);

            Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.EncryptedOrPasswordProtected));
        }
    }

    internal static void PreflightRejectsMalformedOpenDocumentManifest()
    {
        using var input = new MemoryStream(OdfBytes("application/vnd.oasis.opendocument.text", manifestContent: "<manifest:manifest>"));
        var result = SourceFormatPreflight.Inspect(input, ".odt");

        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.MalformedContainer));
        Assert.True(result.Diagnostics.All(diagnostic => diagnostic.Code != IntakeDiagnosticCode.EncryptedOrPasswordProtected));
    }

    internal static void PreflightBoundsOpenDocumentManifestRead()
    {
        var manifest = "<manifest>" + new string('x', 5000) + "</manifest>";
        using var input = new MemoryStream(OdfBytes("application/vnd.oasis.opendocument.text", manifestContent: manifest));
        var result = SourceFormatPreflight.Inspect(input, ".odt", new SourcePreflightOptions { MaximumPackageMetadataBytes = 4096 });

        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.UnsafePackage));
    }

    internal static void PreflightAcceptsLegacyOfficeMiniStreamBelowCutoff()
    {
        using var input = new MemoryStream(OleMiniStreamBytes(rootLength: 64, firstMiniSector: 0, streamLength: 64));

        Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(input, ".doc").Status);
    }

    internal static void IntakeIsolatesLegacyOfficeMiniStreamOffsetErrors()
    {
        using var temporary = new TemporaryDirectory();
        var wordPath = Path.Combine(temporary.Path, "mini.doc");
        var pdfPath = Path.Combine(temporary.Path, "valid.pdf");
        File.WriteAllBytes(wordPath, OleMiniStreamBytes(rootLength: 4096, firstMiniSector: 1, streamLength: 128));
        File.WriteAllBytes(pdfPath, PdfBytes());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(temporary.Path, "cache") });
        var service = new SourceIntakeService(cache);

        var result = service.PrepareAsync([new SourceInput(wordPath), new SourceInput(pdfPath)]).GetAwaiter().GetResult();

        Assert.True(result.Items[0].IsReady);
        Assert.True(result.Items[1].IsReady);
    }

    internal static void SnapshotCaptureHashesAndPublishesPathFreeMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var source = Path.Combine(temporary.Path, "資料.pdf");
        var bytes = PdfBytes();
        File.WriteAllBytes(source, bytes);
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root });

        var snapshot = cache.CaptureAsync(source).GetAwaiter().GetResult();
        var second = cache.CaptureAsync(source).GetAwaiter().GetResult();

        Assert.True(snapshot.SnapshotId.StartsWith("snap_", StringComparison.Ordinal));
        Assert.True(snapshot.SnapshotId != second.SnapshotId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), snapshot.Sha256);
        Assert.Equal(bytes.LongLength, snapshot.LengthBytes);
        Assert.Equal(TimeSpan.Zero, snapshot.CapturedAtUtc.Offset);
        using var captured = cache.OpenRead(snapshot.SnapshotId);
        using var copy = new MemoryStream();
        captured.CopyTo(copy);
        Assert.True(bytes.SequenceEqual(copy.ToArray()));

        var metadata = Directory.GetFiles(root, "metadata.json", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Single(text => text.Contains(snapshot.SnapshotId, StringComparison.Ordinal));
        Assert.True(!metadata.Contains(source, StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            var privateBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(0, (int)(File.GetUnixFileMode(root) & privateBits));
            Assert.Equal(0, (int)(File.GetUnixFileMode(Path.Combine(root, snapshot.SnapshotId, "content.bin")) & privateBits));
        }

        var zoneIdentifier = Encoding.UTF8.GetBytes("[ZoneTransfer]\nZoneId=3\n");
        var markedAccess = new ZoneIdentifierSourceAccess(bytes, zoneIdentifier);
        var markedCache = new SourceSnapshotCache(
            new SourceSnapshotCacheOptions { RootPath = Path.Combine(temporary.Path, "marked-cache") }, markedAccess);
        var markedSnapshot = markedCache.CaptureAsync("synthetic-marked.pdf").GetAwaiter().GetResult();
        using var preservedZoneIdentifier = markedCache.OpenZoneIdentifierRead(markedSnapshot.SnapshotId);
        Assert.True(preservedZoneIdentifier is not null);
        using var preservedBytes = new MemoryStream();
        preservedZoneIdentifier!.CopyTo(preservedBytes);
        Assert.True(zoneIdentifier.SequenceEqual(preservedBytes.ToArray()));

        var undercountedCache = new SourceSnapshotCache(new SourceSnapshotCacheOptions
        {
            RootPath = Path.Combine(temporary.Path, "undercounted-cache"),
            MaximumBytes = bytes.LongLength + zoneIdentifier.LongLength
        }, markedAccess);
        var quotaError = Capture<SourceIntakeException>(() =>
            undercountedCache.CaptureAsync("synthetic-marked.pdf").GetAwaiter().GetResult());
        Assert.Equal(IntakeDiagnosticCode.CacheQuotaExceeded, quotaError.Diagnostic.Code);
    }

    internal static void SnapshotCaptureChecksLinuxRuntimeArchitecture()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "source.pdf");
        var root = Path.Combine(temporary.Path, "cache");
        File.WriteAllBytes(source, PdfBytes());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root });
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            Assert.True(cache.CaptureAsync(source).GetAwaiter().GetResult().SnapshotId.StartsWith("snap_", StringComparison.Ordinal));
        }
        else
        {
            var error = Capture<SourceIntakeException>(() => cache.CaptureAsync(source).GetAwaiter().GetResult());
            Assert.Equal(IntakeDiagnosticCode.UnsupportedArchitecture, error.Diagnostic.Code);
            Assert.True(error.Diagnostic.Message.Contains("architecture", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Length);
        }
    }

    internal static void SnapshotCaptureRejectsDeviceAttributeIdentities()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "device-source.pdf");
        File.WriteAllBytes(source, PdfBytes());

        var cases = new[]
        {
            (PathAttributes: FileAttributes.Device, OpenedAttributes: FileAttributes.Device, ExpectedOpenCount: 0),
            (PathAttributes: (FileAttributes?)null, OpenedAttributes: FileAttributes.Device, ExpectedOpenCount: 1)
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var current = cases[index];
            var root = Path.Combine(temporary.Path, $"cache-{index}");
            var access = new DeviceAttributeSourceAccess(
                PdfBytes(), current.PathAttributes, current.OpenedAttributes);
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root }, access);

            var error = Capture<SourceIntakeException>(() => cache.CaptureAsync(source).GetAwaiter().GetResult());

            Assert.Equal(IntakeDiagnosticCode.SpecialFileSkipped, error.Diagnostic.Code);
            Assert.Equal(current.ExpectedOpenCount, access.OpenCount);
            Assert.Equal(0, Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Length);
        }
    }

    internal static void SnapshotCaptureRejectsSourceMutation()
    {
        using var temporary = new TemporaryDirectory();
        foreach (var mutatedField in new[] { "identity", "length", "last-write" })
        {
            var root = Path.Combine(temporary.Path, "cache-" + mutatedField);
            var access = new MutatingSourceAccess(Enumerable.Repeat((byte)0x5a, 128 * 1024).ToArray(), mutatedField);
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root }, access);

            var error = Capture<SourceIntakeException>(() => cache.CaptureAsync("synthetic-source.pdf").GetAwaiter().GetResult());

            Assert.Equal(IntakeDiagnosticCode.SourceChangedDuringCapture, error.Diagnostic.Code);
            Assert.Equal(0, Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Length);
        }
    }

    internal static void SnapshotCaptureFailsClosedWithoutNativeFileIdentity()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return;

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "source.pdf");
        var root = Path.Combine(temporary.Path, "cache");
        File.WriteAllBytes(source, PdfBytes());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root });

        var error = Capture<SourceIntakeException>(() => cache.CaptureAsync(source).GetAwaiter().GetResult());

        Assert.Equal(IntakeDiagnosticCode.SnapshotCaptureFailed, error.Diagnostic.Code);
        Assert.Equal(0, Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Length);
    }

    internal static void SnapshotCancellationCleansOnlyOperationTemporaries()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var source = Path.Combine(temporary.Path, "large.pdf");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)0x4a, 256 * 1024).ToArray());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root });
        File.WriteAllText(Path.Combine(root, "foreign.txt"), "keep");
        using var cancellation = new CancellationTokenSource();

        var error = Capture<SourceIntakeException>(() => cache.CaptureAsync(
            source,
            new CancelAfterFirstReadStreamAccess(cancellation),
            cancellation.Token).GetAwaiter().GetResult());

        Assert.Equal(IntakeDiagnosticCode.CancellationRequested, error.Diagnostic.Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "foreign.txt")));
        Assert.Equal(0, Directory.GetDirectories(root, ".tmp_*", SearchOption.TopDirectoryOnly).Length);
        Assert.Equal(0, Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Length);
    }

    internal static void SnapshotQuotaAndRetentionAreSafe()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var firstSource = Path.Combine(temporary.Path, "first.pdf");
        var secondSource = Path.Combine(temporary.Path, "second.pdf");
        File.WriteAllBytes(firstSource, [1, 2, 3, 4]);
        File.WriteAllBytes(secondSource, [5, 6]);
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root, MaximumBytes = 400 });
        var retained = cache.CaptureAsync(firstSource).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "foreign.txt"), "preserve");

        var quotaError = Capture<SourceIntakeException>(() => cache.CaptureAsync(secondSource).GetAwaiter().GetResult());
        Assert.Equal(IntakeDiagnosticCode.CacheQuotaExceeded, quotaError.Diagnostic.Code);
        Assert.True(Directory.Exists(Path.Combine(root, retained.SnapshotId)));

        cache.Release(retained.SnapshotId);
        cache.Retain(retained.SnapshotId);
        var stillRetainedError = Capture<SourceIntakeException>(() => cache.CaptureAsync(secondSource).GetAwaiter().GetResult());
        Assert.Equal(IntakeDiagnosticCode.CacheQuotaExceeded, stillRetainedError.Diagnostic.Code);
        cache.Release(retained.SnapshotId);
        var next = cache.CaptureAsync(secondSource).GetAwaiter().GetResult();
        Assert.True(!Directory.Exists(Path.Combine(root, retained.SnapshotId)));
        Assert.True(Directory.Exists(Path.Combine(root, next.SnapshotId)));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "foreign.txt")));
    }

    internal static void SnapshotQuotaReclaimsOwnedOrphanTemporaryDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var source = Path.Combine(temporary.Path, "source.pdf");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions
        {
            RootPath = root,
            MaximumBytes = 400,
            MaximumSnapshots = 2
        });
        var orphan = Path.Combine(root, ".tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllBytes(Path.Combine(orphan, "content.bin"), [0]);
        var unownedTemporary = Path.Combine(root, ".tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unownedTemporary);
        File.WriteAllText(Path.Combine(unownedTemporary, "foreign.txt"), "keep");

        var snapshot = cache.CaptureAsync(source).GetAwaiter().GetResult();

        Assert.Equal(1, Directory.GetDirectories(root, ".tmp_*", SearchOption.TopDirectoryOnly).Length);
        Assert.True(Directory.Exists(Path.Combine(root, snapshot.SnapshotId)));
        Assert.True(new byte[] { 1, 2, 3, 4 }.SequenceEqual(File.ReadAllBytes(Path.Combine(root, snapshot.SnapshotId, "content.bin"))));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unownedTemporary, "foreign.txt")));
    }

    internal static void SnapshotQuotaReclaimsOwnedMetadataTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var firstSource = Path.Combine(temporary.Path, "first.pdf");
        var secondSource = Path.Combine(temporary.Path, "second.pdf");
        File.WriteAllBytes(firstSource, [1, 2, 3, 4]);
        File.WriteAllBytes(secondSource, [5, 6, 7, 8, 9, 10]);
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root, MaximumBytes = 400 });
        var released = cache.CaptureAsync(firstSource).GetAwaiter().GetResult();
        var snapshotDirectory = Path.Combine(root, released.SnapshotId);
        var orphanedMetadata = Path.Combine(snapshotDirectory, ".metadata_" + Guid.NewGuid().ToString("N") + ".tmp");
        var foreignFile = Path.Combine(snapshotDirectory, "foreign.txt");
        File.WriteAllText(orphanedMetadata, "partial metadata from an interrupted update");
        File.WriteAllText(foreignFile, "preserve");
        cache.Release(released.SnapshotId);

        var quotaError = Capture<SourceIntakeException>(() => cache.CaptureAsync(secondSource).GetAwaiter().GetResult());

        Assert.Equal(IntakeDiagnosticCode.CacheQuotaExceeded, quotaError.Diagnostic.Code);
        Assert.True(!File.Exists(orphanedMetadata));
        Assert.Equal("preserve", File.ReadAllText(foreignFile));
        Assert.True(Directory.Exists(snapshotDirectory));

        File.Delete(foreignFile);
        var next = cache.CaptureAsync(secondSource).GetAwaiter().GetResult();
        Assert.True(!Directory.Exists(snapshotDirectory));
        Assert.True(Directory.Exists(Path.Combine(root, next.SnapshotId)));
    }

    internal static void SnapshotQuotaIsEnforcedAcrossProcesses()
    {
        if (OperatingSystem.IsMacOS()) return;

        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "shared-cache");
        _ = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root, MaximumBytes = 400 });
        var startPath = Path.Combine(temporary.Path, "start");
        var releasePath = Path.Combine(temporary.Path, "release");
        var workers = Enumerable.Range(0, 2).Select(index =>
        {
            var suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new CacheWorkerProcess(
                Process.Start(CreateCacheWorkerStartInfo(
                    root,
                    startPath,
                    Path.Combine(temporary.Path, "ready-" + suffix),
                    Path.Combine(temporary.Path, "copying-" + suffix),
                    releasePath)) ?? throw new InvalidOperationException("Could not start the cache quota worker."),
                Path.Combine(temporary.Path, "ready-" + suffix),
                Path.Combine(temporary.Path, "copying-" + suffix));
        }).ToArray();

        try
        {
            Assert.True(WaitUntil(() => workers.All(worker => File.Exists(worker.ReadyPath)), TimeSpan.FromSeconds(15)));
            File.WriteAllText(startPath, "go");
            Assert.True(WaitUntil(() => workers.Any(worker => File.Exists(worker.CopyingPath)), TimeSpan.FromSeconds(15)));
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            File.WriteAllText(releasePath, "go");

            foreach (var worker in workers)
            {
                Assert.True(worker.Process.WaitForExit(15_000));
            }

            Assert.Equal(1, workers.Count(worker => worker.Process.ExitCode == 0));
            Assert.True(workers.All(worker => worker.Process.ExitCode is 0 or 2));
            var usedBytes = Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly)
                .Sum(directory => Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length));
            Assert.True(usedBytes <= 400);
        }
        finally
        {
            if (!File.Exists(releasePath)) File.WriteAllText(releasePath, "go");
            foreach (var worker in workers)
            {
                if (!worker.Process.HasExited)
                {
                    worker.Process.Kill(entireProcessTree: true);
                    worker.Process.WaitForExit();
                }

                worker.Process.Dispose();
            }
        }
    }

    internal static int RunCrossProcessCacheWorker(string root, string startPath, string readyPath, string copyingPath, string releasePath)
    {
        try
        {
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root, MaximumBytes = 400 });
            cache.CaptureAsync("synthetic-cache-source", new CrossProcessGateSourceAccess(startPath, readyPath, copyingPath, releasePath))
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (SourceIntakeException exception) when (exception.Diagnostic.Code == IntakeDiagnosticCode.CacheQuotaExceeded)
        {
            return 2;
        }
        catch (SourceIntakeException exception)
        {
            Console.Error.WriteLine(exception.Diagnostic.Code + ": " + exception.InnerException);
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 3;
        }
    }

    private static ProcessStartInfo CreateCacheWorkerStartInfo(string root, string startPath, string readyPath, string copyingPath, string releasePath)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The current process path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("The test assembly path is unavailable."));
        }

        startInfo.ArgumentList.Add("--source-cache-worker");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(startPath);
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(copyingPath);
        startInfo.ArgumentList.Add(releasePath);
        return startInfo;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline) return false;
            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }

        return true;
    }

    internal static void OpenReadVerifiesSnapshotIntegrity()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var source = Path.Combine(temporary.Path, "source.pdf");
        File.WriteAllBytes(source, PdfBytes());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root });
        var snapshot = cache.CaptureAsync(source).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, snapshot.SnapshotId, "content.bin"), "tampered");

        var error = Capture<SourceIntakeException>(() => cache.OpenRead(snapshot.SnapshotId));

        Assert.Equal(IntakeDiagnosticCode.CacheEntryCorrupt, error.Diagnostic.Code);
    }

    internal static void SnapshotCaptureRejectsReparsePointAncestor()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "target");
        Directory.CreateDirectory(target);
        var source = Path.Combine(target, "source.pdf");
        File.WriteAllBytes(source, PdfBytes());
        var linkedDirectory = Path.Combine(temporary.Path, "source-link");
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(temporary.Path, "cache") });
        var error = Capture<SourceIntakeException>(() => cache.CaptureAsync(Path.Combine(linkedDirectory, "source.pdf")).GetAwaiter().GetResult());

        Assert.Equal(IntakeDiagnosticCode.ReparsePointSkipped, error.Diagnostic.Code);
        Assert.Equal(0, Directory.GetDirectories(Path.Combine(temporary.Path, "cache"), "snap_*", SearchOption.TopDirectoryOnly).Length);
    }

    internal static void SnapshotIdentityFailureDoesNotAbortBatchItems()
    {
        using var temporary = new TemporaryDirectory();
        var firstSource = Path.Combine(temporary.Path, "first.pdf");
        var secondSource = Path.Combine(temporary.Path, "second.pdf");
        File.WriteAllBytes(firstSource, PdfBytes());
        File.WriteAllBytes(secondSource, PdfBytes());
        var cache = new SourceSnapshotCache(
            new SourceSnapshotCacheOptions { RootPath = Path.Combine(temporary.Path, "cache") },
            new FailingIdentitySourceAccess("first.pdf", PdfBytes()));
        var service = new SourceIntakeService(cache);

        var result = service.PrepareAsync([new SourceInput(firstSource), new SourceInput(secondSource)]).GetAwaiter().GetResult();

        Assert.Equal(IntakeDiagnosticCode.SnapshotCaptureFailed, result.Items[0].Diagnostics.Single().Code);
        Assert.True(result.Items[1].IsReady);
    }

    internal static void SnapshotPreflightReadFailureDoesNotAbortBatchItems()
    {
        using var temporary = new TemporaryDirectory();
        var firstSource = Path.Combine(temporary.Path, "first.pdf");
        var secondSource = Path.Combine(temporary.Path, "second.pdf");
        var pdfBytes = PdfBytes();
        File.WriteAllBytes(firstSource, pdfBytes);
        File.WriteAllBytes(secondSource, pdfBytes);
        var root = Path.Combine(temporary.Path, "cache");
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions
        {
            RootPath = root,
            MaximumBytes = pdfBytes.LongLength + 1024,
            MaximumSnapshots = 1
        });
        var preflightCalls = 0;
        var service = new SourceIntakeService(cache, (stream, extension, options) =>
        {
            if (Interlocked.Increment(ref preflightCalls) == 1)
            {
                using var throwingStream = new ThrowOnReadStream(stream);
                return SourceFormatPreflight.Inspect(throwingStream, extension, options);
            }

            return SourceFormatPreflight.Inspect(stream, extension, options);
        });

        var result = service.PrepareAsync([new SourceInput(firstSource), new SourceInput(secondSource)]).GetAwaiter().GetResult();

        Assert.Equal(IntakeDiagnosticCode.CacheEntryCorrupt, result.Items[0].Diagnostics.Single().Code);
        Assert.True(!result.Items[0].Diagnostics.Single().Message.Contains(firstSource, StringComparison.Ordinal));
        Assert.True(result.Items[1].IsReady);
        Assert.True(result.Items[1].Snapshot is not null);
        Assert.Equal(Path.Combine(root, result.Items[1].Snapshot!.SnapshotId), Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Single());
    }

    internal static void IntakePreparesSnapshotsAndKeepsPerItemErrors()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "cache");
        var source = Path.Combine(temporary.Path, "good.pdf");
        var missing = Path.Combine(temporary.Path, "secret-missing.pdf");
        File.WriteAllBytes(source, PdfBytes());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = root });
        var service = new SourceIntakeService(cache);

        var result = service.PrepareAsync([new SourceInput(source), new SourceInput(missing)]).GetAwaiter().GetResult();

        Assert.Equal(2, result.Items.Length);
        Assert.True(result.Items[0].IsReady);
        Assert.Equal(SourceFormat.Pdf, result.Items[0].Format!.Value);
        Assert.True(result.Items[0].Snapshot is not null);
        Assert.True(!result.Items[1].IsReady);
        Assert.True(result.Items[1].Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.InputVanished));
        Assert.True(!result.Items[1].Diagnostics.Any(diagnostic => diagnostic.Message.Contains(missing, StringComparison.Ordinal)));

        var corruptingRoot = Path.Combine(temporary.Path, "corrupting-cache");
        var corruptingCache = new SourceSnapshotCache(
            new SourceSnapshotCacheOptions { RootPath = corruptingRoot },
            new CorruptingSourceAccess(corruptingRoot));
        var corruptingService = new SourceIntakeService(corruptingCache);
        var afterCorrupt = corruptingService.PrepareAsync([new SourceInput(source), new SourceInput(missing)]).GetAwaiter().GetResult();
        Assert.Equal(2, afterCorrupt.Items.Length);
        Assert.True(!afterCorrupt.Items[0].IsReady);
        Assert.True(afterCorrupt.Items[0].Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.CacheEntryCorrupt));
        Assert.True(afterCorrupt.Items[1].Diagnostics.Any(diagnostic => diagnostic.Code == IntakeDiagnosticCode.InputVanished));
    }

    private static TException Capture<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static byte[] PdfBytes(bool includeLiteralEncrypt = false, bool encrypted = false, long? xrefOverride = null, bool includeNestedEncrypt = false)
    {
        var content = new StringBuilder("%PDF-1.7\n");
        var objectOffset = Encoding.ASCII.GetByteCount(content.ToString());
        content.Append("1 0 obj\n<< /Type /Catalog");
        if (includeLiteralEncrypt) content.Append(" /Dummy (/Encrypt is text)");
        content.Append(" >>\nendobj\n");
        var xrefOffset = Encoding.ASCII.GetByteCount(content.ToString());
        content.Append("xref\n0 2\n0000000000 65535 f \n");
        content.Append(objectOffset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        content.Append("trailer\n<< /Root 1 0 R");
        if (encrypted) content.Append(" /Encrypt 2 0 R");
        if (includeNestedEncrypt) content.Append(" /Custom << /Encrypt 2 0 R >>");
        content.Append(" >>\nstartxref\n");
        content.Append((xrefOverride ?? xrefOffset).ToString(System.Globalization.CultureInfo.InvariantCulture));
        content.Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(content.ToString());
    }

    private static byte[] PdfIncrementalEncryptedBytes()
    {
        var original = Encoding.ASCII.GetString(PdfBytes(encrypted: true));
        var startXrefIndex = original.LastIndexOf("startxref\n", StringComparison.Ordinal);
        var priorOffsetStart = startXrefIndex + "startxref\n".Length;
        var priorOffsetEnd = original.IndexOf('\n', priorOffsetStart);
        var priorXrefOffset = original[priorOffsetStart..priorOffsetEnd];
        var updated = new StringBuilder(original);
        updated.Append('\n');
        var latestXrefOffset = Encoding.ASCII.GetByteCount(updated.ToString());
        updated.Append("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Root 1 0 R /Prev ")
            .Append(priorXrefOffset)
            .Append(" >>\nstartxref\n")
            .Append(latestXrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(updated.ToString());
    }

    private static byte[] PdfXrefStreamBytes(
        bool encrypted,
        bool includeRequiredFields = true,
        bool truncateStream = false,
        bool includeIndex = true,
        bool completeDefaultIndex = false,
        bool includeUnsupportedFilter = false,
        bool compressWithFlate = false,
        bool pngUpPredictor = false,
        bool unsupportedPredictor = false,
        bool invalidXrefRecord = false,
        bool unsupportedXrefEntryType = false,
        bool invalidFreeObjectReference = false,
        bool invalidObjectStreamReference = false)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        var xrefOffset = output.Length;
        output.Write(Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /XRef "));
        if (encrypted) output.Write(Encoding.ASCII.GetBytes("/Encrypt 2 0 R "));
        if (includeRequiredFields)
        {
            output.Write(Encoding.ASCII.GetBytes(pngUpPredictor
                ? "/Size 2 /W [1 4 3] /Index [0 2] "
                : includeIndex ? "/Size 2 /W [1 4 3] /Index [1 1] " : "/Size 2 /W [1 4 3] "));
        }
        if (includeUnsupportedFilter) output.Write(Encoding.ASCII.GetBytes("/Filter /DCTDecode "));
        byte[] records;
        using (var rawRecords = new MemoryStream())
        {
            Span<byte> xrefRecord = stackalloc byte[8];
            xrefRecord.Clear();
            if (pngUpPredictor)
            {
                if (unsupportedXrefEntryType) xrefRecord[0] = 3;
                if (invalidObjectStreamReference)
                {
                    xrefRecord[0] = 2;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xrefRecord.Slice(1, 4), 2);
                }
                if (invalidFreeObjectReference)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xrefRecord.Slice(1, 4), 2);
                xrefRecord[5] = 0;
                xrefRecord[6] = 0xff;
                xrefRecord[7] = 0xff;
                rawRecords.Write(xrefRecord);
                xrefRecord.Clear();
            }
            else if (!includeIndex && completeDefaultIndex) rawRecords.Write(xrefRecord);
            xrefRecord.Clear();
            xrefRecord[0] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xrefRecord.Slice(1, 4), checked((uint)(invalidXrefRecord ? xrefOffset - 1 : xrefOffset)));
            if (pngUpPredictor)
            {
                var previousRow = rawRecords.ToArray();
                for (var index = 0; index < xrefRecord.Length; index++) xrefRecord[index] -= previousRow[index];
            }
            rawRecords.Write(xrefRecord);
            records = rawRecords.ToArray();
        }

        byte[] streamBytes;
        if (compressWithFlate || pngUpPredictor)
        {
            using var compressed = new MemoryStream();
            using (var compressor = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                compressor.Write(records);
            streamBytes = compressed.ToArray();
            output.Write(Encoding.ASCII.GetBytes("/Filter /FlateDecode "));
            if (pngUpPredictor)
                output.Write(Encoding.ASCII.GetBytes(unsupportedPredictor
                    ? "/DecodeParms << /Predictor 99 /Columns 8 >> "
                    : "/DecodeParms << /Predictor 12 /Columns 8 >> "));
        }
        else
        {
            streamBytes = records;
        }

        var streamLength = streamBytes.Length;
        output.Write(Encoding.ASCII.GetBytes($"/Length {streamLength} >>\nstream\n"));
        if (truncateStream)
        {
            output.Write([1, 2, 3]);
        }
        else
        {
            output.Write(streamBytes);
        }
        output.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\nstartxref\n"));
        output.Write(Encoding.ASCII.GetBytes(xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        output.Write(Encoding.ASCII.GetBytes("\n%%EOF\n"));
        return output.ToArray();
    }

    private static byte[] JpegBytes(ushort width = 1, ushort height = 1, bool includeScanData = true)
    {
        using var output = new MemoryStream();
        output.Write([0xff, 0xd8]);
        output.Write([0xff, 0xc0, 0x00, 0x0b, 0x08]);
        WriteBigEndianUInt16(output, height);
        WriteBigEndianUInt16(output, width);
        output.Write([1, 1, 0x11, 0]);
        output.Write([0xff, 0xda, 0x00, 0x08, 1, 1, 0, 0, 0x3f, 0]);
        output.Write(includeScanData ? [0, 0xff, 0xd9] : [0xff, 0xd9]);
        return output.ToArray();
    }

    private static void WriteBigEndianUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static byte[] OleBytes(params string[] streamNames)
    {
        const uint FreeSector = uint.MaxValue;
        const uint EndOfChain = uint.MaxValue - 1;
        const uint FatSector = uint.MaxValue - 2;
        const int sectorSize = 512;
        var contentSectorCount = 8 * streamNames.Length;
        var bytes = new byte[sectorSize + (2 + contentSectorCount) * sectorSize];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 0xfffe);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30, 2), 9);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 6);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 4096);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), EndOfChain);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68, 4), EndOfChain);
        for (var index = 0; index < 109; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76 + index * 4, 4), index == 0 ? 0u : FreeSector);
        }

        var fatOffset = sectorSize;
        for (var index = 0; index < sectorSize / 4; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + index * 4, 4), FreeSector);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset, 4), FatSector);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + 4, 4), EndOfChain);
        for (var index = 0; index < contentSectorCount; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + (index + 2) * 4, 4),
                index % 8 == 7 ? EndOfChain : (uint)(index + 3));
        }

        var directory = bytes.AsSpan(sectorSize * 2, sectorSize);
        WriteOleDirectoryEntry(directory, 0, "Root Entry", objectType: 5, child: 1, firstSector: EndOfChain, streamLength: 0);
        for (var index = 0; index < streamNames.Length; index++)
        {
            WriteOleDirectoryEntry(directory, 128 * (index + 1), streamNames[index], objectType: 2,
                child: FreeSector, firstSector: (uint)(2 + index * 8), streamLength: 8 * sectorSize,
                right: index + 1 < streamNames.Length ? (uint)(index + 2) : FreeSector);
            if (string.Equals(streamNames[index], "WordDocument", StringComparison.OrdinalIgnoreCase))
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sectorSize * (3 + index * 8), 2), 0xa5ec);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sectorSize * (3 + index * 8) + 2, 2), 0x00c1);
            }
        }
        return bytes;
    }

    private static byte[] OleBytesWithNestedStorageStream(string streamName)
    {
        const uint EndOfChain = uint.MaxValue - 1;
        const uint NoStream = uint.MaxValue;
        var bytes = OleBytes(streamName);
        var directory = bytes.AsSpan(1024, 512);
        WriteOleDirectoryEntry(directory, 128, "Nested Storage", objectType: 1, child: 2,
            firstSector: EndOfChain, streamLength: 0);
        WriteOleDirectoryEntry(directory, 256, streamName, objectType: 2, child: NoStream,
            firstSector: 2, streamLength: 4096);
        return bytes;
    }

    private static byte[] OleBytesWithManyFatSectors(int fatSectorCount)
    {
        const uint FreeSector = uint.MaxValue;
        const uint EndOfChain = uint.MaxValue - 1;
        const uint FatSector = uint.MaxValue - 2;
        const uint DifatSector = uint.MaxValue - 3;
        const int sectorSize = 512;
        const int fatEntriesPerSector = sectorSize / sizeof(uint);
        const int inlineFatEntries = 109;
        const int fatEntriesPerDifatSector = fatEntriesPerSector - 1;
        var difatSectorCount = (fatSectorCount - inlineFatEntries + fatEntriesPerDifatSector - 1) / fatEntriesPerDifatSector;
        var directorySector = fatSectorCount + difatSectorCount;
        var miniFatSector = directorySector + 1;
        var rootMiniStreamSector = directorySector + 2;
        var sectorCount = checked(fatSectorCount * fatEntriesPerSector);
        var bytes = new byte[sectorSize + sectorCount * sectorSize];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 0xfffe);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30, 2), 9);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 6);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), checked((uint)fatSectorCount));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), checked((uint)directorySector));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 4096);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), checked((uint)miniFatSector));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68, 4), checked((uint)fatSectorCount));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72, 4), checked((uint)difatSectorCount));
        for (var index = 0; index < 109; index++)
        {
            var sector = index < fatSectorCount ? (uint)index : FreeSector;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76 + index * 4, 4), sector);
        }

        var fat = Enumerable.Repeat(FreeSector, sectorCount).ToArray();
        for (var index = 0; index < fatSectorCount; index++) fat[index] = FatSector;
        for (var index = 0; index < difatSectorCount; index++) fat[fatSectorCount + index] = DifatSector;
        fat[directorySector] = EndOfChain;
        fat[miniFatSector] = EndOfChain;
        fat[rootMiniStreamSector] = EndOfChain;

        for (var difatIndex = 0; difatIndex < difatSectorCount; difatIndex++)
        {
            var difatSector = fatSectorCount + difatIndex;
            var difatOffset = sectorSize + difatSector * sectorSize;
            for (var entry = 0; entry < fatEntriesPerDifatSector; entry++)
            {
                var fatIndex = inlineFatEntries + difatIndex * fatEntriesPerDifatSector + entry;
                var sector = fatIndex < fatSectorCount ? (uint)fatIndex : FreeSector;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(difatOffset + entry * 4, 4), sector);
            }
            var nextDifat = difatIndex + 1 < difatSectorCount ? (uint)(difatSector + 1) : EndOfChain;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(difatOffset + fatEntriesPerDifatSector * 4, 4), nextDifat);
        }

        for (var fatSector = 0; fatSector < fatSectorCount; fatSector++)
        {
            var fatOffset = sectorSize + fatSector * sectorSize;
            for (var entry = 0; entry < fatEntriesPerSector; entry++)
            {
                var value = fat[fatSector * fatEntriesPerSector + entry];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + entry * 4, 4), value);
            }
        }

        var miniFatOffset = sectorSize + miniFatSector * sectorSize;
        for (var index = 0; index < fatEntriesPerSector; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(miniFatOffset + index * 4, 4), index == 0 ? EndOfChain : FreeSector);
        }
        var directoryOffset = sectorSize + directorySector * sectorSize;
        var directory = bytes.AsSpan(directoryOffset, sectorSize);
        WriteOleDirectoryEntry(directory, 0, "Root Entry", objectType: 5, child: 1, firstSector: (uint)rootMiniStreamSector, streamLength: 64);
        WriteOleDirectoryEntry(directory, 128, "WordDocument", objectType: 2, child: FreeSector, firstSector: 0, streamLength: 64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sectorSize + rootMiniStreamSector * sectorSize, 2), 0xa5ec);
        return bytes;
    }

    private static byte[] OleMiniStreamBytes(long rootLength, uint firstMiniSector, long streamLength)
    {
        const uint FreeSector = uint.MaxValue;
        const uint EndOfChain = uint.MaxValue - 1;
        const uint FatSector = uint.MaxValue - 2;
        const int sectorSize = 512;
        var rootSectorCount = checked((int)((rootLength + sectorSize - 1) / sectorSize));
        var sectorCount = 3 + rootSectorCount;
        var bytes = new byte[sectorSize + sectorCount * sectorSize];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 0xfffe);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30, 2), 9);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 6);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 4096);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68, 4), EndOfChain);
        for (var index = 0; index < 109; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76 + index * 4, 4), index == 0 ? 0u : FreeSector);
        }

        var fatOffset = sectorSize;
        for (var index = 0; index < sectorSize / 4; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + index * 4, 4), FreeSector);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset, 4), FatSector);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + 4, 4), EndOfChain);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + 8, 4), EndOfChain);
        for (var index = 0; index < rootSectorCount; index++)
        {
            var next = index + 1 == rootSectorCount ? EndOfChain : (uint)(4 + index);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + (3 + index) * 4, 4), next);
        }

        var miniFatOffset = sectorSize * 3;
        for (var index = 0; index < sectorSize / 4; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(miniFatOffset + index * 4, 4), FreeSector);
        }
        var streamMiniSectorCount = checked((int)((streamLength + 63) / 64));
        for (var index = 0; index < streamMiniSectorCount; index++)
        {
            var miniSector = checked((int)firstMiniSector + index);
            var next = index + 1 == streamMiniSectorCount ? EndOfChain : (uint)(miniSector + 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(miniFatOffset + miniSector * 4, 4), next);
        }

        var directory = bytes.AsSpan(sectorSize * 2, sectorSize);
        WriteOleDirectoryEntry(directory, 0, "Root Entry", objectType: 5, child: 1, firstSector: 3, streamLength: rootLength);
        WriteOleDirectoryEntry(directory, 128, "WordDocument", objectType: 2, child: FreeSector, firstSector: firstMiniSector, streamLength: streamLength);
        var wordDataOffset = sectorSize * 4 + checked((int)firstMiniSector * 64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(wordDataOffset, 2), 0xa5ec);
        return bytes;
    }

    private static void WriteOleDirectoryEntry(Span<byte> directory, int entryOffset, string name, byte objectType, uint child, uint firstSector, long streamLength, uint right = uint.MaxValue)
    {
        Encoding.Unicode.GetBytes(name + "\0").CopyTo(directory[entryOffset..(entryOffset + 64)]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(directory.Slice(entryOffset + 64, 2), (ushort)((name.Length + 1) * 2));
        directory[entryOffset + 66] = objectType;
        directory[entryOffset + 67] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(directory.Slice(entryOffset + 68, 4), uint.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(directory.Slice(entryOffset + 72, 4), right);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(directory.Slice(entryOffset + 76, 4), child);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(directory.Slice(entryOffset + 116, 4), firstSector);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(directory.Slice(entryOffset + 120, 8), (ulong)streamLength);
    }

    private static byte[] PngBytes(byte bitDepth = 8, byte colorType = 2, byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        WritePngChunk(output, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, bitDepth, colorType, compression, filter, interlace]);
        WritePngChunk(output, "IDAT", [0]);
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] PngWithoutImageDataBytes()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        WritePngChunk(output, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        WritePngChunk(output, "IDAT", []);
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string name, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var type = Encoding.ASCII.GetBytes(name);
        output.Write(type);
        output.Write(data);
        var crcInput = type.Concat(data).ToArray();
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, CalculatePngCrc(crcInput));
        output.Write(crc);
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] TiffBytes(ushort width = 1, ushort height = 1)
    {
        const int firstImageDataOffset = 62;
        var bytes = new byte[firstImageDataOffset + 1];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        bytes[4] = 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 4);
        WriteTiffEntry(bytes, 10, 256, 3, 1, width);
        WriteTiffEntry(bytes, 22, 257, 3, 1, height);
        WriteTiffEntry(bytes, 34, 273, 3, 1, (uint)firstImageDataOffset);
        WriteTiffEntry(bytes, 46, 279, 3, 1, 1);
        return bytes;
    }

    private static byte[] BigTiffHeaderBytes()
    {
        var bytes = new byte[16];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 43;
        bytes[4] = 8;
        bytes[8] = 16;
        return bytes;
    }

    private static byte[] EmptyIfdTiffBytes()
    {
        var bytes = new byte[14];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        bytes[4] = 8;
        return bytes;
    }

    private static byte[] DimensionlessTiffBytes()
    {
        const int firstImageDataOffset = 38;
        var bytes = new byte[firstImageDataOffset + 1];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        bytes[4] = 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 2);
        WriteTiffEntry(bytes, 10, 273, 3, 1, (uint)firstImageDataOffset);
        WriteTiffEntry(bytes, 22, 279, 3, 1, 1);
        return bytes;
    }

    private static byte[] TiffMultiStripBytes(bool invalidSecondOffset = false, bool mismatchedCounts = false)
    {
        const int offsetsArray = 62;
        const int countsArray = 70;
        var bytes = new byte[80];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        bytes[4] = 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 4);
        WriteTiffEntry(bytes, 10, 256, 3, 1, 1);
        WriteTiffEntry(bytes, 22, 257, 3, 1, 1);
        WriteTiffEntry(bytes, 34, 273, 4, 2, offsetsArray);
        WriteTiffEntry(bytes, 46, 279, 4, mismatchedCounts ? 1u : 2u, mismatchedCounts ? 1u : countsArray);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsetsArray, 4), 78);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsetsArray + 4, 4), invalidSecondOffset ? uint.MaxValue : 79);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(countsArray, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(countsArray + 4, 4), 1);
        return bytes;
    }

    private static byte[] TiffRepeatedStripOffsetTagBytes()
    {
        const uint arrayValueCount = 100_000;
        const int duplicateCount = 32;
        var entryCount = 4 + duplicateCount;
        var directoryEnd = 8 + 2 + entryCount * 12 + 4;
        var offsetsArray = directoryEnd;
        var countsArray = offsetsArray + checked((int)arrayValueCount * 4);
        var dataOffset = countsArray + checked((int)arrayValueCount * 4);
        var bytes = new byte[dataOffset + 1];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), checked((ushort)entryCount));
        WriteTiffEntry(bytes, 10, 256, 3, 1, 1);
        WriteTiffEntry(bytes, 22, 257, 3, 1, 1);
        WriteTiffEntry(bytes, 34, 273, 4, arrayValueCount, checked((uint)offsetsArray));
        WriteTiffEntry(bytes, 46, 279, 4, arrayValueCount, checked((uint)countsArray));
        for (var duplicate = 0; duplicate < duplicateCount; duplicate++)
        {
            WriteTiffEntry(bytes, 58 + duplicate * 12, 273, 4, arrayValueCount, checked((uint)offsetsArray));
        }

        for (var index = 0; index < arrayValueCount; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsetsArray + index * 4, 4), checked((uint)dataOffset));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(countsArray + index * 4, 4), 1);
        }

        return bytes;
    }

    private static byte[] TiffMultiPageLargeArraysBytes()
    {
        const int firstDirectoryOffset = 8;
        const int secondDirectoryOffset = 62;
        const int valuesPerArray = 60_000;
        const int arrayLength = valuesPerArray * sizeof(uint);
        const int firstOffsetsArray = 116;
        var firstCountsArray = firstOffsetsArray + arrayLength;
        var secondOffsetsArray = firstCountsArray + arrayLength;
        var secondCountsArray = secondOffsetsArray + arrayLength;
        var dataOffset = secondCountsArray + arrayLength;
        var bytes = new byte[dataOffset + 1];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), firstDirectoryOffset);
        WriteTiffDirectoryWithArrays(bytes, firstDirectoryOffset, firstOffsetsArray, firstCountsArray, checked((uint)secondDirectoryOffset));
        WriteTiffDirectoryWithArrays(bytes, secondDirectoryOffset, secondOffsetsArray, secondCountsArray, 0);
        foreach (var arrayOffset in new[] { firstOffsetsArray, firstCountsArray, secondOffsetsArray, secondCountsArray })
        {
            for (var index = 0; index < valuesPerArray; index++)
            {
                var value = arrayOffset == firstOffsetsArray || arrayOffset == secondOffsetsArray ? checked((uint)dataOffset) : 1u;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(arrayOffset + index * 4, 4), value);
            }
        }

        return bytes;
    }

    private static void WriteTiffDirectoryWithArrays(byte[] bytes, int directoryOffset, int offsetsArray, int countsArray, uint nextDirectoryOffset)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directoryOffset, 2), 4);
        WriteTiffEntry(bytes, directoryOffset + 2, 256, 3, 1, 1);
        WriteTiffEntry(bytes, directoryOffset + 14, 257, 3, 1, 1);
        WriteTiffEntry(bytes, directoryOffset + 26, 273, 4, 60_000, checked((uint)offsetsArray));
        WriteTiffEntry(bytes, directoryOffset + 38, 279, 4, 60_000, checked((uint)countsArray));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directoryOffset + 50, 4), nextDirectoryOffset);
    }

    private static byte[] TiffMultiPageBytes(bool invalidNextDirectory = false)
    {
        const int firstDirectoryOffset = 8;
        const int firstDataOffset = 62;
        const int secondDirectoryOffset = 64;
        const int secondDataOffset = 118;
        var bytes = new byte[secondDataOffset + 1];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), firstDirectoryOffset);
        WriteTiffDirectory(bytes, firstDirectoryOffset, firstDataOffset, invalidNextDirectory ? 500u : secondDirectoryOffset);
        WriteTiffDirectory(bytes, secondDirectoryOffset, secondDataOffset, nextDirectoryOffset: 0);
        return bytes;
    }

    private static void WriteTiffDirectory(byte[] bytes, int directoryOffset, int imageDataOffset, uint nextDirectoryOffset)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directoryOffset, 2), 4);
        WriteTiffEntry(bytes, directoryOffset + 2, 256, 3, 1, 1);
        WriteTiffEntry(bytes, directoryOffset + 14, 257, 3, 1, 1);
        WriteTiffEntry(bytes, directoryOffset + 26, 273, 3, 1, (uint)imageDataOffset);
        WriteTiffEntry(bytes, directoryOffset + 38, 279, 3, 1, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directoryOffset + 50, 4), nextDirectoryOffset);
    }

    private static void WriteTiffEntry(byte[] bytes, int offset, ushort tag, ushort type, uint count, uint value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), tag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), value);
    }

    private static byte[] ZipBytes(params string[] entryNames) => ZipBytes(entryNames.Select(name => (name, "<root/>" )).ToArray());

    private static byte[] ZipBytes(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return output.ToArray();
    }

    private static byte[] ZipWithMissingFinalDeflateBlock()
    {
        var content = Encoding.ASCII.GetBytes("<root/>");
        var truncatedDeflate = new byte[content.Length + 5];
        truncatedDeflate[0] = 0; // Non-final, stored DEFLATE block with no following final block.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(truncatedDeflate.AsSpan(1, 2), checked((ushort)content.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(truncatedDeflate.AsSpan(3, 2), unchecked((ushort)~content.Length));
        content.CopyTo(truncatedDeflate, 5);

        return ZipWithCustomDeflatePayload(content, truncatedDeflate);
    }

    private static byte[] ZipWithTrailingDeflateBytes()
    {
        var (content, compressedPayload) = CreateDeflatePayload();
        var withTrailingBytes = compressedPayload.Concat(new byte[] { 0xde, 0xad, 0xbe, 0xef }).ToArray();
        return ZipWithCustomDeflatePayload(content, withTrailingBytes);
    }

    private static byte[] ZipWithCompleteDeflateStream(byte[]? sourceContent = null)
    {
        var (content, compressedPayload) = CreateDeflatePayload(sourceContent);
        return ZipWithCustomDeflatePayload(content, compressedPayload);
    }

    private static (byte[] Content, byte[] CompressedPayload) CreateDeflatePayload(byte[]? sourceContent = null)
    {
        var content = sourceContent ?? Encoding.ASCII.GetBytes("<root/>");
        using var compressed = new MemoryStream();
        using (var deflater = new System.IO.Compression.DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(content);
        }

        return (content, compressed.ToArray());
    }

    private static byte[] ZipWithCustomDeflatePayload(byte[] content, byte[] deflatePayload)
    {
        var contents = new (string Name, byte[] Data, byte[] Compressed, ushort Method)[]
        {
            ("[Content_Types].xml", content, content, 0),
            ("_rels/.rels", content, content, 0),
            ("word/document.xml", content, deflatePayload, 8)
        };
        var entries = new List<(byte[] Name, byte[] Data, byte[] Compressed, ushort Method, uint Crc, uint Offset)>();
        using var output = new MemoryStream();
        foreach (var entry in contents)
        {
            var name = Encoding.ASCII.GetBytes(entry.Name);
            var crc = CalculatePngCrc(entry.Data);
            var offset = checked((uint)output.Position);
            WriteZipUInt32(output, 0x04034b50);
            WriteZipUInt16(output, 20);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, entry.Method);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt32(output, crc);
            WriteZipUInt32(output, checked((uint)entry.Compressed.Length));
            WriteZipUInt32(output, checked((uint)entry.Data.Length));
            WriteZipUInt16(output, checked((ushort)name.Length));
            WriteZipUInt16(output, 0);
            output.Write(name);
            output.Write(entry.Compressed);
            entries.Add((name, entry.Data, entry.Compressed, entry.Method, crc, offset));
        }

        var centralOffset = checked((uint)output.Position);
        foreach (var entry in entries)
        {
            WriteZipUInt32(output, 0x02014b50);
            WriteZipUInt16(output, 20);
            WriteZipUInt16(output, 20);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, entry.Method);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt32(output, entry.Crc);
            WriteZipUInt32(output, checked((uint)entry.Compressed.Length));
            WriteZipUInt32(output, checked((uint)entry.Data.Length));
            WriteZipUInt16(output, checked((ushort)entry.Name.Length));
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt32(output, 0);
            WriteZipUInt32(output, entry.Offset);
            output.Write(entry.Name);
        }

        var centralSize = checked((uint)output.Position - centralOffset);
        WriteZipUInt32(output, 0x06054b50);
        WriteZipUInt16(output, 0);
        WriteZipUInt16(output, 0);
        WriteZipUInt16(output, checked((ushort)entries.Count));
        WriteZipUInt16(output, checked((ushort)entries.Count));
        WriteZipUInt32(output, centralSize);
        WriteZipUInt32(output, centralOffset);
        WriteZipUInt16(output, 0);
        return output.ToArray();
    }

    private static byte[] ZipWithDataDescriptors(bool corruptFirstDescriptor = false)
    {
        var contents = new (string Name, byte[] Data)[]
        {
            ("[Content_Types].xml", Encoding.ASCII.GetBytes("<root/>")),
            ("_rels/.rels", Encoding.ASCII.GetBytes("<root/>")),
            ("word/document.xml", Encoding.ASCII.GetBytes("<root/>"))
        };
        var entries = new List<(string Name, byte[] NameBytes, byte[] Data, uint Crc, uint Offset)>();
        using var output = new MemoryStream();
        for (var index = 0; index < contents.Length; index++)
        {
            var (name, data) = contents[index];
            var nameBytes = Encoding.ASCII.GetBytes(name);
            var crc = CalculatePngCrc(data);
            var localOffset = checked((uint)output.Position);
            WriteZipUInt32(output, 0x04034b50);
            WriteZipUInt16(output, 20);
            WriteZipUInt16(output, 8);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt32(output, 0);
            WriteZipUInt32(output, 0);
            WriteZipUInt32(output, 0);
            WriteZipUInt16(output, checked((ushort)nameBytes.Length));
            WriteZipUInt16(output, 0);
            output.Write(nameBytes);
            output.Write(data);
            WriteZipUInt32(output, 0x08074b50);
            WriteZipUInt32(output, index == 0 && corruptFirstDescriptor ? crc ^ 1 : crc);
            WriteZipUInt32(output, checked((uint)data.Length));
            WriteZipUInt32(output, checked((uint)data.Length));
            entries.Add((name, nameBytes, data, crc, localOffset));
        }

        var centralOffset = checked((uint)output.Position);
        foreach (var entry in entries)
        {
            WriteZipUInt32(output, 0x02014b50);
            WriteZipUInt16(output, 20);
            WriteZipUInt16(output, 20);
            WriteZipUInt16(output, 8);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt32(output, entry.Crc);
            WriteZipUInt32(output, checked((uint)entry.Data.Length));
            WriteZipUInt32(output, checked((uint)entry.Data.Length));
            WriteZipUInt16(output, checked((ushort)entry.NameBytes.Length));
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt16(output, 0);
            WriteZipUInt32(output, 0);
            WriteZipUInt32(output, entry.Offset);
            output.Write(entry.NameBytes);
        }

        var centralSize = checked((uint)output.Position - centralOffset);
        WriteZipUInt32(output, 0x06054b50);
        WriteZipUInt16(output, 0);
        WriteZipUInt16(output, 0);
        WriteZipUInt16(output, checked((ushort)entries.Count));
        WriteZipUInt16(output, checked((ushort)entries.Count));
        WriteZipUInt32(output, centralSize);
        WriteZipUInt32(output, centralOffset);
        WriteZipUInt16(output, 0);
        return output.ToArray();
    }

    private static void WriteZipUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteZipUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static byte[] OdfBytes(
        string mediaType,
        bool includeContent = true,
        string? manifestContent = null,
        string contentContent = "<document-content/>")
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var mimeType = archive.CreateEntry("mimetype", CompressionLevel.NoCompression).Open())
            {
                mimeType.Write(Encoding.ASCII.GetBytes(mediaType));
            }

            var entries = includeContent
                ? new[] { ("META-INF/manifest.xml", manifestContent ?? "<manifest/>"), ("content.xml", contentContent) }
                : new[] { ("META-INF/manifest.xml", manifestContent ?? "<manifest/>") };
            foreach (var (name, content) in entries)
            {
                using var stream = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), Encoding.UTF8);
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paperbatch-u2-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ThrowOnReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Synthetic cached read failure.");
        public override int Read(Span<byte> buffer) => throw new IOException("Synthetic cached read failure.");
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CacheWorkerProcess(Process Process, string ReadyPath, string CopyingPath);

    private sealed class CrossProcessGateSourceAccess(
        string startPath,
        string readyPath,
        string copyingPath,
        string releasePath) : ISourceFileAccess
    {
        private static readonly byte[] Content = [1, 2, 3, 4, 5, 6, 7, 8];
        private static readonly SourceFileIdentity Identity = new("shared-synthetic-file", Content.LongLength, 1);

        public SourceFileIdentity GetPathIdentity(string path) => Identity;

        public Stream OpenRead(string path) => new CrossProcessGateReadStream(Content, copyingPath, releasePath);

        public SourceFileIdentity GetOpenedFileIdentity(Stream stream)
        {
            File.WriteAllText(readyPath, "ready");
            while (!File.Exists(startPath)) Thread.Sleep(TimeSpan.FromMilliseconds(5));
            return Identity;
        }
    }

    private sealed class ZoneIdentifierSourceAccess(byte[] content, byte[] zoneIdentifier) : ISourceFileAccess
    {
        private readonly SourceFileIdentity _identity = new("zone-marked-source", content.LongLength, 1);

        public SourceFileIdentity GetPathIdentity(string path) => _identity;

        public Stream OpenRead(string path) => new IdentityMemoryStream(content, _identity);

        public SourceFileIdentity GetOpenedFileIdentity(Stream stream) => ((IdentityMemoryStream)stream).Identity;

        public Stream OpenZoneIdentifierRead(string path) => new MemoryStream(zoneIdentifier, writable: false);
    }

    private sealed class FailingIdentitySourceAccess(string failingFileName, byte[] content) : ISourceFileAccess
    {
        private readonly SourceFileIdentity _identity = new("synthetic-source", content.LongLength, 1);

        public SourceFileIdentity GetPathIdentity(string path)
        {
            if (Path.GetFileName(path) == failingFileName) throw new System.ComponentModel.Win32Exception(5);
            return _identity;
        }

        public Stream OpenRead(string path) => new IdentityMemoryStream(content, _identity);

        public SourceFileIdentity GetOpenedFileIdentity(Stream stream) => ((IdentityMemoryStream)stream).Identity;
    }

    private sealed class IdentityMemoryStream(byte[] bytes, SourceFileIdentity identity) : MemoryStream(bytes, writable: false)
    {
        public SourceFileIdentity Identity { get; } = identity;
    }

    private sealed class DevicePathAttributeAccess(string devicePath) : ISourcePathAttributeAccess
    {
        public FileAttributes GetAttributes(string path)
        {
            var attributes = File.GetAttributes(path);
            return Path.GetFullPath(path) == Path.GetFullPath(devicePath)
                ? attributes | FileAttributes.Device
                : attributes;
        }
    }

    private sealed class DeviceAttributeSourceAccess(
        byte[] content,
        FileAttributes? pathAttributes,
        FileAttributes openedAttributes) : ISourceFileAccess
    {
        private readonly SourceFileIdentity _pathIdentity = new(
            "synthetic-device", content.LongLength, 1, pathAttributes);
        private readonly SourceFileIdentity _openedIdentity = new(
            "synthetic-device", content.LongLength, 1, openedAttributes);

        public int OpenCount { get; private set; }

        public SourceFileIdentity GetPathIdentity(string path) => _pathIdentity;

        public Stream OpenRead(string path)
        {
            OpenCount++;
            return new IdentityMemoryStream(content, _openedIdentity);
        }

        public SourceFileIdentity GetOpenedFileIdentity(Stream stream) => ((IdentityMemoryStream)stream).Identity;
    }

    private sealed class CrossProcessGateReadStream(byte[] bytes, string copyingPath, string releasePath) : MemoryStream(bytes, writable: false)
    {
        private bool _waited;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                _waited = true;
                File.WriteAllText(copyingPath, "copying");
                while (!File.Exists(releasePath)) await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class MutatingSourceAccess(byte[] bytes, string mutatedField) : ISourceFileAccess
    {
        private SourceFileIdentity _identity = new("synthetic-file-id", bytes.LongLength, 1);

        public SourceFileIdentity GetPathIdentity(string path) => _identity;
        public Stream OpenRead(string path) => new MutatingReadStream(bytes, Mutate);
        public SourceFileIdentity GetOpenedFileIdentity(Stream stream) => _identity;

        private void Mutate() => _identity = mutatedField switch
        {
            "identity" => _identity with { FileId = "replacement-file-id" },
            "length" => _identity with { LengthBytes = _identity.LengthBytes + 1 },
            _ => _identity with { LastWriteTimeUtcTicks = 2 }
        };
    }

    private sealed class CancelAfterFirstReadStreamAccess(CancellationTokenSource cancellation) : ISourceFileAccess
    {
        public SourceFileIdentity GetPathIdentity(string path) => new("synthetic-file-id", 256 * 1024, 1);
        public Stream OpenRead(string path) => new CancellingReadStream(256 * 1024, cancellation);
        public SourceFileIdentity GetOpenedFileIdentity(Stream stream) => new("synthetic-file-id", 256 * 1024, 1);
    }

    private sealed class CorruptingSourceAccess(string root) : ISourceFileAccess
    {
        private int _corrupted;

        public SourceFileIdentity GetPathIdentity(string path)
        {
            var file = new FileInfo(path);
            return new SourceFileIdentity(Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc.Ticks);
        }

        public Stream OpenRead(string path)
        {
            var identity = GetPathIdentity(path);
            return new CallbackMemoryStream(File.ReadAllBytes(path), identity, () =>
            {
                if (Interlocked.Exchange(ref _corrupted, 1) != 0) return;
                var snapshotDirectory = Directory.GetDirectories(root, "snap_*", SearchOption.TopDirectoryOnly).Single();
                File.WriteAllText(Path.Combine(snapshotDirectory, "metadata.json"), "{");
            });
        }

        public SourceFileIdentity GetOpenedFileIdentity(Stream stream) => ((CallbackMemoryStream)stream).Identity;
    }

    private sealed class CallbackMemoryStream(byte[] bytes, SourceFileIdentity identity, Action onDispose) : MemoryStream(bytes, writable: false)
    {
        private bool _called;

        public SourceFileIdentity Identity { get; } = identity;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_called)
            {
                _called = true;
                onDispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class MutatingReadStream(byte[] bytes, Action afterRead) : MemoryStream(bytes, writable: false)
    {
        private bool _changed;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (read.Result > 0 && !_changed)
            {
                _changed = true;
                afterRead();
            }

            return read;
        }
    }

    private sealed class CancellingReadStream(int length, CancellationTokenSource cancellation) : MemoryStream(new byte[length], writable: false)
    {
        private bool _cancelled;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (read.Result > 0 && !_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }

            return read;
        }
    }
}
