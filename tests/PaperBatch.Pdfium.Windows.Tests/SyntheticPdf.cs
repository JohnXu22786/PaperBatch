using System.Text;

namespace PaperBatch.Pdfium.Windows.Tests;

internal static class SyntheticPdf
{
    public const string ExpectedReleaseSha256 = "3452d11031050ec65e57f180215b57b9e6d465e3fbedafa8a77ad90885c7d23e";

    public static byte[] CreateCompositionSource() => BuildPdf(
        content: "1 0 0 rg 20 20 100 100 re f\n",
        includeTextField: false);

    public static byte[] CreateCompositionInsert() => BuildPdf(
        content: "0 0 1 rg 40 40 100 100 re f\n",
        includeTextField: false);

    public static byte[] CreateVectorPageEditFixture() => BuildPdf(
        content: "1 0 0 rg 20 30 40 50 re f\n0 0 1 rg 120 30 30 30 re f\n",
        includeTextField: false);

    public static byte[] CreateTextFieldFixture() => BuildPdf(
        content: "0.9 0.9 0.9 rg 5 5 190 190 re f\n",
        includeTextField: true);

    private static byte[] BuildPdf(string content, bool includeTextField)
    {
        var streamBytes = Encoding.ASCII.GetBytes(content);
        const string appearanceContent = "q 1 1 1 rg 0 0 140 40 re f BT /Helv 12 Tf 2 13 Td (fixture-value) Tj ET Q\n";
        var appearanceStream = Encoding.ASCII.GetBytes(appearanceContent);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + (includeTextField ? " /AcroForm 5 0 R" : string.Empty) + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /Helv 6 0 R >> >> /Contents 4 0 R" + (includeTextField ? " /Annots [8 0 R]" : string.Empty) + " >>",
            $"<< /Length {streamBytes.Length} >>\nstream\n{content}endstream",
            "<< /Fields [7 0 R] /NeedAppearances false /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /FT /Tx /T (SyntheticField) /V (fixture-value) /Kids [8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /P 3 0 R /Rect [10 20 150 60] /F 4 /AP << /N 9 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 140 40] /Resources << /Font << /Helv 6 0 R >> >> /Length {appearanceStream.Length} >>\nstream\n{appearanceContent}endstream"
        };

        using var output = new MemoryStream();
        Write(output, "%PDF-1.7\n%PaperBatch synthetic fixture\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            Write(output, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = output.Position;
        Write(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            Write(output, $"{offset:D10} 00000 n \n");
        }

        Write(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static void Write(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
    }
}
