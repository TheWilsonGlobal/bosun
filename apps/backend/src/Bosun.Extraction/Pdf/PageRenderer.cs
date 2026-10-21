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

/// <summary>A page cannot be rendered within the pixel budget.</summary>
public sealed class PageTooLargeException(string message) : Exception(message);

// PDFtoImage ships native PDFium binaries for Windows, Linux and macOS (the platforms Bosun targets); the analyzer only sees that the
// project is "any platform".
#pragma warning disable CA1416
/// <summary>PDFium (via PDFtoImage) page rendering. PDFium is not thread-safe, so rendering is serialised.</summary>
public sealed class PdfiumPageRenderer : IPageRenderer
{
    public const int MaxSidePixels = 6000;

    /// <summary>Below this resolution a page is not legible to a model: the page is refused instead of being rendered tiny.</summary>
    public const int MinUsableDpi = 20;

    private static readonly object Gate = new();

    public PdfiumPageRenderer(long maxPixels = 36_000_000)
    {
        MaxPixels = maxPixels;
    }

    /// <summary>A rendered bitmap never has more pixels than this (width x height).</summary>
    public long MaxPixels { get; }

    /// <summary>Resolution to render a page of the given size (points) at: the requested dpi, lowered so neither the side nor the pixel count passes its cap. Null = even <see cref="MinUsableDpi"/> would be too big.</summary>
    public static int? EffectiveDpi(double widthPoints, double heightPoints, int requestedDpi, long maxPixels)
    {
        var w = Math.Max(widthPoints, 1) / 72.0;
        var h = Math.Max(heightPoints, 1) / 72.0;
        var bySide = MaxSidePixels / Math.Max(w, h);
        var byPixels = Math.Sqrt(maxPixels / (w * h));
        var dpi = (int)Math.Floor(Math.Min(Math.Min(requestedDpi, 600), Math.Min(bySide, byPixels)));
        return dpi < MinUsableDpi ? null : dpi;
    }

    /// <summary>Largest side of any page (points) and the 1-based number of that page, read by PDFium without rendering. (0, 0) when PDFium cannot say.</summary>
    internal static (double Side, int Page) LargestPageSide(byte[] pdf)
    {
        lock (Gate)
        {
            try
            {
                var sizes = Conversion.GetPageSizes(pdf);
                float best = 0;
                var page = 0;
                for (var i = 0; i < sizes.Count; i++)
                {
                    var side = Math.Max(sizes[i].Width, sizes[i].Height);
                    if (double.IsNaN(side))
                    {
                        side = float.PositiveInfinity;
                    }

                    if (side > best)
                    {
                        best = side;
                        page = i + 1;
                    }
                }

                return (best, page);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (0, 0);
            }
        }
    }

    public byte[] RenderPng(byte[] pdf, int page, int dpi, double[]? crop = null)
    {
        lock (Gate)
        {
            using var stream = new MemoryStream(pdf, writable: false);
            var size = Conversion.GetPageSize(stream, page - 1, leaveOpen: true);
            var effectiveDpi = EffectiveDpi(size.Width, size.Height, dpi, MaxPixels)
                ?? throw new PageTooLargeException($"Page {page} is {size.Width:0} x {size.Height:0} points: too large to render legibly within {MaxPixels:N0} pixels.");

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
