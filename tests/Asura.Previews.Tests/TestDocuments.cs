using System.Text;

namespace Asura.Previews.Tests;

/// <summary>
/// Documents built by hand rather than checked in, so a fixture is exactly what
/// the test says it is — here, a real two-page PDF with visible text on each
/// page and a correct cross-reference table.
/// </summary>
internal static class TestDocuments
{
    public static string TwoPagePdf(double pageWidth = 612, double pageHeight = 792)
    {
        const string firstPage =
            "BT /F1 24 Tf 72 700 Td (Asura preview) Tj ET\n"
            + "BT /F1 12 Tf 72 660 Td (Page one of two) Tj ET";
        const string secondPage = "BT /F1 24 Tf 72 700 Td (Second page) Tj ET";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            FormattableString.Invariant(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] ")
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
            FormattableString.Invariant(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] ")
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(builder.Length);
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        AppendStream(builder, offsets, 6, firstPage);
        AppendStream(builder, offsets, 7, secondPage);

        var startXref = builder.Length;
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\n"
            + $"startxref\n{startXref}\n%%EOF\n");
        return builder.ToString();
    }

    private static void AppendStream(
        StringBuilder builder,
        List<int> offsets,
        int number,
        string content)
    {
        offsets.Add(builder.Length);
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
    }
}
