using SkiaSharp;

namespace Bosun.Evals.Fixtures;

/// <summary>Draws a fixture page onto a bitmap (same layout code as the text-layer pages), to be degraded into a "scan".</summary>
internal sealed class SkiaDrawing : IPageDrawing, IDisposable
{
    private readonly SKCanvas canvas;
    private readonly SKTypeface typeface;
    private readonly SKPaint ink = new() { Color = new SKColor(28, 28, 32), IsAntialias = true };
    private readonly SKPaint stroke = new() { Color = new SKColor(40, 40, 44), IsAntialias = true, Style = SKPaintStyle.Stroke };

    public SkiaDrawing(double width, double height, double dpi)
    {
        Width = width;
        Height = height;
        Scale = dpi / 72.0;
        Bitmap = new SKBitmap(new SKImageInfo((int)Math.Round(width * Scale), (int)Math.Round(height * Scale), SKColorType.Rgba8888, SKAlphaType.Opaque));
        canvas = new SKCanvas(Bitmap);
        canvas.Clear(SKColors.White);
        typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
    }

    public SKBitmap Bitmap { get; }

    public double Scale { get; }

    public double Width { get; }

    public double Height { get; }

    private float X(double x) => (float)(x * Scale);

    private float Y(double y) => (float)((Height - y) * Scale);

    public void Text(double x, double y, string text, double size = 8, bool bold = false)
    {
        using var font = new SKFont(bold ? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? typeface : typeface, (float)(size * Scale));
        canvas.DrawText(text, X(x), Y(y), SKTextAlign.Left, font, ink);
    }

    public void TextRight(double right, double y, string text, double size = 8, bool bold = false)
    {
        using var font = new SKFont(bold ? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? typeface : typeface, (float)(size * Scale));
        canvas.DrawText(text, X(right), Y(y), SKTextAlign.Right, font, ink);
    }

    public void TextCentered(double center, double y, string text, double size = 8, bool bold = false)
    {
        using var font = new SKFont(bold ? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? typeface : typeface, (float)(size * Scale));
        canvas.DrawText(text, X(center), Y(y), SKTextAlign.Center, font, ink);
    }

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5)
    {
        stroke.StrokeWidth = (float)Math.Max(1.3, width * Scale);
        canvas.DrawLine(X(x1), Y(y1), X(x2), Y(y2), stroke);
    }

    public void Dispose()
    {
        canvas.Dispose();
        Bitmap.Dispose();
        ink.Dispose();
        stroke.Dispose();
        typeface.Dispose();
    }
}

/// <summary>
/// Turns a clean rendering into something that looks like a flatbed scan: a small rotation, paper tone, uneven lighting, a little blur,
/// Gaussian noise, dust specks and JPEG compression. Everything random comes from the seed: the same seed gives the same bytes.
/// </summary>
internal static class ScanRenderer
{
    public static (byte[] Jpeg, int Width, int Height) Degrade(SKBitmap clean, double angleDegrees, int seed, int jpegQuality = 72)
    {
        var rng = new Random(seed);
        var info = new SKImageInfo(clean.Width, clean.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var scanned = new SKBitmap(info);
        using (var canvas = new SKCanvas(scanned))
        {
            canvas.Clear(new SKColor(238, 236, 229));
            canvas.Translate(clean.Width / 2f, clean.Height / 2f);
            canvas.RotateDegrees((float)angleDegrees);
            canvas.Translate(-clean.Width / 2f, -clean.Height / 2f);
            using var blur = SKImageFilter.CreateBlur(0.75f, 0.75f);
            using var paint = new SKPaint { ImageFilter = blur, IsAntialias = true };
            canvas.DrawBitmap(clean, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        }

        var pixels = scanned.GetPixelSpan();
        var width = scanned.Width;
        var height = scanned.Height;
        for (var y = 0; y < height; y++)
        {
            // uneven lighting: a gentle left-to-right and top-to-bottom gradient
            var shade = 0.95 + (0.05 * (1 - (y / (double)height)));
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var light = shade * (0.94 + (0.06 * (x / (double)width)));
                var noise = Gaussian(rng) * 8.0;
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[i + channel] = (byte)Math.Clamp((pixels[i + channel] * light) + noise, 0, 255);
                }
            }
        }

        // dust and toner specks
        var specks = (int)(width * height * 0.0002);
        for (var s = 0; s < specks; s++)
        {
            var cx = rng.Next(width);
            var cy = rng.Next(height);
            var radius = rng.Next(1, 3);
            var dark = (byte)rng.Next(90, 170);
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var px = cx + dx;
                    var py = cy + dy;
                    if (px < 0 || py < 0 || px >= width || py >= height || (dx * dx) + (dy * dy) > radius * radius)
                    {
                        continue;
                    }

                    var i = ((py * width) + px) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = dark;
                }
            }
        }

        using var image = SKImage.FromBitmap(scanned);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return (data.ToArray(), width, height);
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
