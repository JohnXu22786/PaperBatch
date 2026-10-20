using PaperBatch.Pdfium.Windows.Tests;

var failures = 0;
foreach (var (name, run) in new (string Name, Action Run)[]
{
    ("synthetic PDF fixtures are deterministic", FixturesAreDeterministic),
    ("composition fixture has two distinct render targets", CompositionFixtureHasDistinctTargets),
    ("form fixture identifies one bounded text widget", FormFixtureIsBounded),
    ("missing form exports retain per-API execution status", MissingFormExportsAreReported),
    ("form API failure status survives report formatting", FormApiFailureStatusIsReported),
    ("blocked form fixture preserves exports without native calls", BlockedFormFixturePreservesExportStatus),
    ("proof report cannot imply product pinning or close G1", ReportKeepsG1Open)
})
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

if (args.Length > 0 && args[0] == "smoke")
{
    if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
    {
        Console.Error.WriteLine("The native PDFium smoke requires a 64-bit Windows runner.");
        return 2;
    }

    if (args.Length != 9 || args[1] != "--pdfium-path" || args[3] != "--archive-path" || args[5] != "--output" || args[7] != "--checkpoint")
    {
        Console.Error.WriteLine("Usage: smoke --pdfium-path <verified-pdfium.dll> --archive-path <verified-asset.tgz> --output <report.json> --checkpoint <checkpoint.json>");
        return 2;
    }

    var result = PdfiumSmoke.Run(args[2], args[4], args[8]);
    File.WriteAllText(args[6], result.Json);
    Console.WriteLine(result.Summary);
    if (!result.CompositionPassed || result.FormStatus == "failed")
    {
        return 1;
    }
}

return failures == 0 ? 0 : 1;

static void FixturesAreDeterministic()
{
    Assert.BytesEqual(SyntheticPdf.CreateCompositionSource(), SyntheticPdf.CreateCompositionSource());
    Assert.BytesEqual(SyntheticPdf.CreateCompositionInsert(), SyntheticPdf.CreateCompositionInsert());
    Assert.BytesEqual(SyntheticPdf.CreateTextFieldFixture(), SyntheticPdf.CreateTextFieldFixture());
}

static void CompositionFixtureHasDistinctTargets()
{
    var source = SyntheticPdf.CreateCompositionSource();
    var insert = SyntheticPdf.CreateCompositionInsert();
    Assert.Contains("1 0 0 rg", System.Text.Encoding.ASCII.GetString(source));
    Assert.Contains("0 0 1 rg", System.Text.Encoding.ASCII.GetString(insert));
}

static void FormFixtureIsBounded()
{
    var fixture = System.Text.Encoding.ASCII.GetString(SyntheticPdf.CreateTextFieldFixture());
    Assert.Contains("/AcroForm", fixture);
    Assert.Contains("/Subtype /Widget", fixture);
    Assert.Contains("/FT /Tx", fixture);
    Assert.Contains("SyntheticField", fixture);
    Assert.Contains("fixture-value", fixture);
}

static void MissingFormExportsAreReported()
{
    var results = PdfiumSmoke.CreateFormExportResults(new HashSet<string>(StringComparer.Ordinal)
    {
        "FPDF_GetFormType",
        "FPDFPage_GetAnnotCount"
    });
    Assert.Equal(PdfiumSmoke.BoundedFormApiNames.Count, results.Length);
    using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(results));
    var entries = json.RootElement.EnumerateArray().ToArray();
    Assert.Equal("available", entries.Single(x => x.GetProperty("name").GetString() == "FPDF_GetFormType").GetProperty("exportStatus").GetString());
    Assert.Equal("not-run", entries.Single(x => x.GetProperty("name").GetString() == "FPDF_GetFormType").GetProperty("executionStatus").GetString());
    Assert.Equal("unavailable", entries.Single(x => x.GetProperty("name").GetString() == "FPDFPage_CloseAnnot").GetProperty("exportStatus").GetString());
    Assert.Equal("unavailable", entries.Single(x => x.GetProperty("name").GetString() == "FPDFPage_CloseAnnot").GetProperty("executionStatus").GetString());
}

static void FormApiFailureStatusIsReported()
{
    var exports = new HashSet<string>(PdfiumSmoke.BoundedFormApiNames, StringComparer.Ordinal);
    var statuses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FPDF_GetFormType"] = "failed"
    };
    using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(PdfiumSmoke.CreateFormExportResults(exports, statuses)));
    var formType = json.RootElement.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "FPDF_GetFormType");
    Assert.Equal("available", formType.GetProperty("exportStatus").GetString());
    Assert.Equal("failed", formType.GetProperty("executionStatus").GetString());
    var laterApi = json.RootElement.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "FPDFPage_HasFormFieldAtPoint");
    Assert.Equal("not-run", laterApi.GetProperty("executionStatus").GetString());
}

static void BlockedFormFixturePreservesExportStatus()
{
    var exports = new HashSet<string>(PdfiumSmoke.BoundedFormApiNames, StringComparer.Ordinal);
    using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
        PdfiumSmoke.CreateBlockedFormFixture("prior native access violation", exports)));
    var root = json.RootElement;
    Assert.Equal("blocked", root.GetProperty("status").GetString());
    var results = root.GetProperty("apiResults").EnumerateArray().ToArray();
    Assert.True(results.All(x => x.GetProperty("exportStatus").GetString() == "available"));
    Assert.True(results.All(x => x.GetProperty("executionStatus").GetString() == "not-run"));
}

static void ReportKeepsG1Open()
{
    var report = PdfiumSmoke.CreateUnavailableReport("binary hash mismatch", SyntheticPdf.ExpectedReleaseSha256);
    using var json = System.Text.Json.JsonDocument.Parse(report);
    var root = json.RootElement;
    Assert.Equal("blocked", root.GetProperty("status").GetString());
    Assert.Equal("open", root.GetProperty("g1").GetString());
    Assert.Equal(false, root.GetProperty("productDistributionValidated").GetBoolean());
    Assert.Contains("physical", string.Join(' ', root.GetProperty("notValidated").EnumerateArray().Select(x => x.GetString())));
    var formApis = root.GetProperty("formFixture").GetProperty("apiResults").EnumerateArray().ToArray();
    Assert.Equal(PdfiumSmoke.BoundedFormApiNames.Count, formApis.Length);
    Assert.True(formApis.All(x => x.GetProperty("exportStatus").GetString() == "unchecked"));
    Assert.True(formApis.All(x => x.GetProperty("executionStatus").GetString() == "not-run"));
    Assert.Equal("blocked", root.GetProperty("composition").GetProperty("status").GetString());
    Assert.Equal("not-run", root.GetProperty("formFixture").GetProperty("status").GetString());
}

internal static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void Equal<T>(T expected, T? actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    public static void BytesEqual(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException("Expected byte sequences to match.");
    }
}
