using System.Globalization;
using System.Text;

namespace Bosun.Evals.Fixtures;

/// <summary>
/// A tiny, deterministic PDF writer for the extraction fixtures: Helvetica text (WinAnsi), lines, rectangles and JPEG images, no
/// compression, no timestamps, no random ids, so the same input always produces the same bytes. It writes exactly what the
/// extraction pipeline must read back, and nothing else (no third-party PDF writer licence to carry).
/// </summary>
public sealed class MiniPdf
{
    private readonly List<MiniPdfPage> pages = [];

    public MiniPdfPage AddPage(double width, double height)
    {
        var page = new MiniPdfPage(width, height);
        pages.Add(page);
        return page;
    }

    public byte[] Build()
    {
        var objects = new List<byte[]>();
        int Add(byte[] body)
        {
            objects.Add(body);
            return objects.Count;
        }

        // 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold are reserved: the page objects need their numbers.
        var catalog = Add([]);
        var pagesObject = Add([]);
        var regular = Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        var bold = Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));

        var pageIds = new List<int>();
        foreach (var page in pages)
        {
            var imageNames = new List<(string Name, int Id)>();
            foreach (var (image, index) in page.Images.Select((image, i) => (image, i)))
            {
                var header = Ascii($"<< /Type /XObject /Subtype /Image /Width {image.PixelWidth} /Height {image.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Jpeg.Length} >>\nstream\n");
                var id = Add([.. header, .. image.Jpeg, .. Ascii("\nendstream")]);
                imageNames.Add(($"Im{index}", id));
            }

            var content = Encoding.Latin1.GetBytes(page.ContentStream());
            var contentId = Add([.. Ascii($"<< /Length {content.Length} >>\nstream\n"), .. content, .. Ascii("\nendstream")]);
            var xObjects = imageNames.Count == 0 ? "" : $" /XObject << {string.Join(' ', imageNames.Select(n => $"/{n.Name} {n.Id} 0 R"))} >>";
            var pageId = Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent {pagesObject} 0 R /MediaBox [0 0 {Num(page.Width)} {Num(page.Height)}] /Resources << /Font << /F1 {regular} 0 R /F2 {bold} 0 R >>{xObjects} >> /Contents {contentId} 0 R >>")));
            pageIds.Add(pageId);
        }

        objects[catalog - 1] = Ascii($"<< /Type /Catalog /Pages {pagesObject} 0 R >>");
        objects[pagesObject - 1] = Ascii($"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] >>");

        using var output = new MemoryStream();
        void Write(byte[] bytes) => output.Write(bytes, 0, bytes.Length);
        Write(Ascii("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write(Ascii($"{i + 1} 0 obj\n"));
            Write(objects[i]);
            Write(Ascii("\nendobj\n"));
        }

        var xref = output.Position;
        Write(Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            Write(Ascii($"{offset:D10} 00000 n \n"));
        }

        Write(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    internal static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed record MiniPdfImage(byte[] Jpeg, int PixelWidth, int PixelHeight, double X, double Y, double Width, double Height);

/// <summary>What the fixture layouts draw on: a PDF page (text layer) or a bitmap that is later degraded into a "scan".</summary>
public interface IPageDrawing
{
    double Width { get; }

    double Height { get; }

    void Text(double x, double y, string text, double size = 8, bool bold = false);

    void TextRight(double right, double y, string text, double size = 8, bool bold = false);

    void TextCentered(double center, double y, string text, double size = 8, bool bold = false);

    void Line(double x1, double y1, double x2, double y2, double width = 0.5);
}

/// <summary>One page being drawn. Coordinates are PDF user space: origin bottom-left, y up, 1 unit = 1/72 inch.</summary>
public sealed class MiniPdfPage(double width, double height) : IPageDrawing
{
    private readonly StringBuilder content = new();

    public double Width { get; } = width;

    public double Height { get; } = height;

    public List<MiniPdfImage> Images { get; } = [];

    public void Text(double x, double y, string text, double size = 8, bool bold = false)
    {
        content.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {MiniPdf.Num(size)} Tf {MiniPdf.Num(x)} {MiniPdf.Num(y)} Td ({Escape(text)}) Tj ET\n");
    }

    /// <summary>Text whose right edge is at <paramref name="right"/>.</summary>
    public void TextRight(double right, double y, string text, double size = 8, bool bold = false) =>
        Text(right - Helvetica.Width(text, size, bold), y, text, size, bold);

    public void TextCentered(double center, double y, string text, double size = 8, bool bold = false) =>
        Text(center - Helvetica.Width(text, size, bold) / 2, y, text, size, bold);

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5) =>
        content.Append(CultureInfo.InvariantCulture, $"{MiniPdf.Num(width)} w {MiniPdf.Num(x1)} {MiniPdf.Num(y1)} m {MiniPdf.Num(x2)} {MiniPdf.Num(y2)} l S\n");

    public void Rectangle(double x, double y, double w, double h, double lineWidth = 0.5) =>
        content.Append(CultureInfo.InvariantCulture, $"{MiniPdf.Num(lineWidth)} w {MiniPdf.Num(x)} {MiniPdf.Num(y)} {MiniPdf.Num(w)} {MiniPdf.Num(h)} re S\n");

    /// <summary>Places a JPEG so that it covers the given rectangle (a scanned page: the whole page).</summary>
    public void Jpeg(byte[] jpeg, int pixelWidth, int pixelHeight, double x, double y, double w, double h)
    {
        Images.Add(new MiniPdfImage(jpeg, pixelWidth, pixelHeight, x, y, w, h));
        content.Append(CultureInfo.InvariantCulture, $"q {MiniPdf.Num(w)} 0 0 {MiniPdf.Num(h)} {MiniPdf.Num(x)} {MiniPdf.Num(y)} cm /Im{Images.Count - 1} Do Q\n");
    }

    internal string ContentStream() => content.ToString();

    /// <summary>WinAnsi bytes as Latin-1 characters: the characters the fixtures use (degree sign, superscripts, dashes, ellipsis) are mapped, the rest is replaced by '?'.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            var mapped = c switch
            {
                '…' => '\u0085',
                '–' => '\u0096',
                '—' => '\u0097',
                '‘' => '\u0091',
                '’' => '\u0092',
                '“' => '\u0093',
                '”' => '\u0094',
                _ => c,
            };
            if (mapped > 0xFF)
            {
                mapped = '?';
            }

            if (mapped is '(' or ')' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(mapped);
        }

        return sb.ToString();
    }
}

/// <summary>Advance widths of Helvetica (the PDF base-14 font), in 1/1000 em, so right-aligned numbers line up exactly.</summary>
public static class Helvetica
{
    private static readonly int[] Ascii =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // space ! " # $ % & ' ( ) * + , - . /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, // 0-9 : ; < = > ?
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, // @ A-O
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, // P-Z [ \ ] ^ _
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, // ` a-o
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, // p-z { | } ~
    ];

    public static double Width(string text, double size, bool bold = false)
    {
        double total = 0;
        foreach (var c in text)
        {
            total += c switch
            {
                >= ' ' and <= '~' => Ascii[c - ' '],
                '°' => 400,
                '²' or '³' => 333,
                'µ' => 556,
                '±' => 584,
                '…' or '—' => 1000,
                '–' => 556,
                _ => 556,
            };
        }

        return total / 1000.0 * size * (bold ? 1.06 : 1.0);
    }
}
