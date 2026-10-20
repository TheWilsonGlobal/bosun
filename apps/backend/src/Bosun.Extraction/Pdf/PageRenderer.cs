using PDFtoImage;
using SkiaSharp;

namespace Bosun.Extraction;

/// <summary>Renders a PDF page to a PNG. Behind an interface so tests and evals can see exactly what the vision path was given.</summary>
public interface IPageRenderer
{
    /// <param name="pdf">The document bytes.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="dpi">Requested resolution; lowered for oversized pages so the bitmap stays below <see cref="PdfiumPageRenderer.MaxSidePixels"/>.</param>
    /// <param name="crop">Optional region as fractions of the page (x0, y0, x1, y1; origin top-left).</param>
    byte[] RenderPng(byte[] pdf, int page, int dpi, double[]? crop = null);
}

// PDFtoImage ships native PDFium binaries for Windows, Linux and macOS (the platforms Bosun targets); the analyzer only sees that the
// project is "any platform".
#pragma warning disable CA1416
/// <summary>PDFium (via PDFtoImage) page rendering. PDFium is not thread-safe, so rendering is serialised.</summary>
public sealed class PdfiumPageRenderer : IPageRenderer
{
    public const int MaxSidePixels = 6000;

    private static readonly object Gate = new();

    public byte[] RenderPng(byte[] pdf, int page, int dpi, double[]? crop = null)
    {
        lock (Gate)
        {
            using var stream = new MemoryStream(pdf, writable: false);
            var size = Conversion.GetPageSize(stream, page - 1, leaveOpen: true);
            var side = Math.Max(size.Width, size.Height) / 72.0;
            var effectiveDpi = (int)Math.Clamp(Math.Min(dpi, MaxSidePixels / Math.Max(side, 0.1)), 36, 600);

            stream.Position = 0;
            using var bitmap = Conversion.ToImage(stream, page - 1, leaveOpen: true, options: new RenderOptions(Dpi: effectiveDpi));
            if (crop is { Length: 4 })
            {
                var x0 = (int)Math.Clamp(crop[0] * bitmap.Width, 0, bitmap.Width - 1);
                var y0 = (int)Math.Clamp(crop[1] * bitmap.Height, 0, bitmap.Height - 1);
                var x1 = (int)Math.Clamp(crop[2] * bitmap.Width, x0 + 1, bitmap.Width);
                var y1 = (int)Math.Clamp(crop[3] * bitmap.Height, y0 + 1, bitmap.Height);
                using var piece = new SKBitmap();
                if (bitmap.ExtractSubset(piece, new SKRectI(x0, y0, x1, y1)))
                {
                    using var pieceData = piece.Encode(SKEncodedImageFormat.Png, 100);
                    return pieceData.ToArray();
                }
            }

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
#pragma warning restore CA1416
