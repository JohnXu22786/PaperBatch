using PaperBatch.Pdfium.Windows.Tests;

var failures = 0;
foreach (var (name, run) in new (string Name, Action Run)[]
{
    ("synthetic PDF fixtures are deterministic", FixturesAreDeterministic),
    ("composition fixture has two distinct render targets", CompositionFixtureHasDistinctTargets),
    ("form fixture identifies one bounded text widget", FormFixtureIsBounded),
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

    if (args.Length != 7 || args[1] != "--pdfium-path" || args[3] != "--archive-path" || args[5] != "--output")
    {
        Console.Error.WriteLine("Usage: smoke --pdfium-path <verified-pdfium.dll> --archive-path <verified-asset.tgz> --output <report.json>");
        return 2;
    }

    var result = PdfiumSmoke.Run(args[2], args[4]);
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

static void ReportKeepsG1Open()
{
    var report = PdfiumSmoke.CreateUnavailableReport("binary hash mismatch", SyntheticPdf.ExpectedReleaseSha256);
    using var json = System.Text.Json.JsonDocument.Parse(report);
    var root = json.RootElement;
    Assert.Equal("blocked", root.GetProperty("status").GetString());
    Assert.Equal("open", root.GetProperty("g1").GetString());
    Assert.Equal(false, root.GetProperty("productDistributionValidated").GetBoolean());
    Assert.Contains("physical", string.Join(' ', root.GetProperty("notValidated").EnumerateArray().Select(x => x.GetString())));
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
