using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics;

namespace Bosun.Extraction;

/// <summary>A word with its box in PDF user space (origin bottom-left, y up).</summary>
public sealed record PageWord(string Text, double X0, double Y0, double X1, double Y1)
{
    public double Height => Y1 - Y0;

    public double YCenter => (Y0 + Y1) / 2;
}

/// <summary>A straight drawn line (a table border) in PDF user space.</summary>
public sealed record PageLine(double X0, double Y0, double X1, double Y1);

/// <summary>
/// What the text layer of one page offers, reduced to plain data: this is the input of triage and of the table reconstruction, so
/// both can be unit-tested without a PDF (and the PDF reader stays the only code that touches PdfPig).
/// </summary>
public sealed record PageContent(
    int Number,
    double Width,
    double Height,
    IReadOnlyList<PageWord> Words,
    IReadOnlyList<PageLine> HorizontalLines,
    IReadOnlyList<PageLine> VerticalLines,
    int CharCount,
    int ImageCount,
    double ImageAreaRatio,
    double GarbageRatio)
{
    /// <summary>All words in reading order, one text line per visual row (used for grounding checks and the injection scan).</summary>
    public string Text => TableReconstructor.PlainText(this);
}

/// <summary>The document cannot be read as a PDF (corrupt, truncated, or password protected).</summary>
public sealed class PdfUnreadableException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record PdfInfo(int PageCount);

public static class PdfPages
{
    public const string CodeUnreadable = "pdf_unreadable";
    public const string CodeEncrypted = "pdf_encrypted";

    private static ParsingOptions Options => new() { UseLenientParsing = true, SkipMissingFonts = true };

    /// <summary>Opens the document just far enough to count its pages. Throws <see cref="PdfUnreadableException"/> for a file that is not a usable PDF.</summary>
    public static PdfInfo Inspect(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf, Options);
            return new PdfInfo(document.NumberOfPages);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new PdfUnreadableException(CodeEncrypted, "The PDF is password protected. Upload an unprotected copy.", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PdfUnreadableException(CodeUnreadable, "The file could not be read as a PDF (it may be corrupt or truncated).", ex);
        }
    }

    public static IReadOnlyList<PageContent> Read(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf, Options);
            var pages = new List<PageContent>(document.NumberOfPages);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                pages.Add(ReadPage(document.GetPage(number)));
            }

            return pages;
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new PdfUnreadableException(CodeEncrypted, "The PDF is password protected. Upload an unprotected copy.", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not PdfUnreadableException)
        {
            throw new PdfUnreadableException(CodeUnreadable, "The file could not be read as a PDF (it may be corrupt or truncated).", ex);
        }
    }

    private static PageContent ReadPage(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Select(w => new PageWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top))
            .ToList();

        var letters = page.Letters;
        var charCount = 0;
        var bad = 0;
        foreach (var letter in letters)
        {
            foreach (var c in letter.Value)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                charCount++;
                if (IsSuspectChar(c))
                {
                    bad++;
                }
            }
        }

        var (horizontal, vertical) = Lines(page);
        var area = Math.Max(1.0, page.Width * page.Height);
        var imageArea = 0.0;
        var imageCount = 0;
        foreach (var image in page.GetImages())
        {
            imageCount++;
            imageArea += Math.Max(0, image.BoundingBox.Width) * Math.Max(0, image.BoundingBox.Height);
        }

        return new PageContent(page.Number, page.Width, page.Height, words, horizontal, vertical, charCount, imageCount,
            Math.Min(1.0, imageArea / area), charCount == 0 ? 0 : (double)bad / charCount);
    }

    /// <summary>Replacement characters, private-use glyphs and control characters: what a broken font mapping produces.</summary>
    public static bool IsSuspectChar(char c) =>
        c == '�' || c is >= '' and <= '' || char.IsControl(c) || c is >= '\u0080' and <= '\u009F';

    private static (List<PageLine> Horizontal, List<PageLine> Vertical) Lines(Page page)
    {
        var horizontal = new List<PageLine>();
        var vertical = new List<PageLine>();

        void Add(PdfPoint a, PdfPoint b)
        {
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            if (dy <= 0.75 && dx >= 3)
            {
                horizontal.Add(new PageLine(Math.Min(a.X, b.X), (a.Y + b.Y) / 2, Math.Max(a.X, b.X), (a.Y + b.Y) / 2));
            }
            else if (dx <= 0.75 && dy >= 3)
            {
                vertical.Add(new PageLine((a.X + b.X) / 2, Math.Min(a.Y, b.Y), (a.X + b.X) / 2, Math.Max(a.Y, b.Y)));
            }
        }

        try
        {
            foreach (var path in page.Paths)
            {
                foreach (var subpath in path)
                {
                    PdfPoint? start = null;
                    PdfPoint? current = null;
                    foreach (var command in subpath.Commands)
                    {
                        switch (command)
                        {
                            case PdfSubpath.Move move:
                                start = current = move.Location;
                                break;
                            case PdfSubpath.Line line:
                                Add(line.From, line.To);
                                current = line.To;
                                break;
                            case PdfSubpath.Close when start is not null && current is not null:
                                Add(current.Value, start.Value);
                                current = start;
                                break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unusual vector content must not stop the text path: the lines are only a hint.
            horizontal.Clear();
            vertical.Clear();
        }

        return (horizontal, vertical);
    }
}
