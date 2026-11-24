using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace LirikMelayang;

static class Cover
{
    const int Size = 32;

    // Null when the cover is missing, unreadable, or has no distinct color.
    public static async Task<Color?> ColorAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var source = await thumbnail.OpenReadAsync();
            using var buffer = new MemoryStream();
            await source.AsStreamForRead().CopyToAsync(buffer);
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = image.DecodePixelHeight = Size;
            image.StreamSource = buffer;
            image.EndInit();

            var pixels = new byte[Size * Size * 4];
            new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0).CopyPixels(pixels, Size * 4, 0);
            return Pick(pixels);
        }
        catch (Exception) { return null; }
    }

    // Strongest hue of a Bgr32 image, brightened until it reads on dark backgrounds.
    public static Color? Pick(byte[] pixels)
    {
        var hues = new (double Weight, double R, double G, double B)[12];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            double max = Math.Max(r, Math.Max(g, b)), range = max - Math.Min(r, Math.Min(g, b));
            if (max < 40 || range / max < 0.25) continue;

            var hue = max == r ? (g - b) / range : max == g ? 2 + (b - r) / range : 4 + (r - g) / range;
            // Offset by half a bin so that red, which wraps around 0, stays in one bin.
            ref var bin = ref hues[((int)Math.Floor(hue * 2 + 0.5) + 12) % 12];
            var weight = range / 255;
            bin.Weight += weight;
            bin.R += r * weight;
            bin.G += g * weight;
            bin.B += b * weight;
        }

        var best = hues.MaxBy(h => h.Weight);
        if (best.Weight < pixels.Length / 4 * 0.01) return null;

        var top = Math.Max(best.R, Math.Max(best.G, best.B));
        double red = best.R / top, green = best.G / top, blue = best.B / top;
        while (Luminance(red, green, blue) < 0.4)
        {
            red += (1 - red) * 0.1;
            green += (1 - green) * 0.1;
            blue += (1 - blue) * 0.1;
        }
        return Color.FromRgb((byte)(red * 255), (byte)(green * 255), (byte)(blue * 255));
    }

    static double Luminance(double r, double g, double b)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    public static bool SelfTest()
    {
        static byte[] Fill(params (byte R, byte G, byte B, int Count)[] parts) =>
            parts.SelectMany(p => Enumerable.Repeat(new byte[] { p.B, p.G, p.R, 0 }, p.Count)).SelectMany(x => x).ToArray();

        var navy = Pick(Fill((0, 0, 90, 900), (128, 128, 128, 124)));
        var red = Pick(Fill((200, 10, 20, 500), (200, 20, 10, 500), (20, 120, 20, 24)));
        return Pick(Fill((128, 128, 128, 1024))) is null
            && Pick(Fill((0, 0, 0, 1024))) is null
            && navy is { } n && n.B > n.R && Luminance(n.R / 255.0, n.G / 255.0, n.B / 255.0) > 0.38
            && red is { } r && r.R > r.G && r.R > r.B;
    }
}
