namespace Ashok.Core.Overlay;

/// <summary>
/// Draws the overlay ring as premultiplied 32-bit BGRA pixels (one <see cref="int"/> per pixel, top row
/// first), ready for a layered window. Pure math, no Windows APIs, so it is tested on any OS.
/// The ring's outer edge is the bitmap's edge: on every straight side the outermost row or column is
/// fully opaque, and the band is exactly <c>thickness</c> pixels deep. Corners are rounded and
/// anti-aliased.
/// </summary>
public static class RingRaster
{
    public static int[] Render(int width, int height, int thickness, int cornerRadius, int rgb)
    {
        if (width <= 0 || height <= 0) return [];

        var pixels = new int[width * height];
        double halfW = width / 2.0, halfH = height / 2.0;
        var radius = Math.Clamp((double)cornerRadius, 0.0, Math.Min(halfW, halfH));
        double innerHalfW = halfW - radius, innerHalfH = halfH - radius;
        int red = (rgb >> 16) & 0xFF, green = (rgb >> 8) & 0xFF, blue = rgb & 0xFF;

        for (var y = 0; y < height; y++)
        {
            var qy = Math.Abs(y + 0.5 - halfH) - innerHalfH;
            for (var x = 0; x < width; x++)
            {
                var qx = Math.Abs(x + 0.5 - halfW) - innerHalfW;

                // Signed distance to the outer rounded edge: negative inside the ring's outer box.
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                var distance = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;

                var coverage = Math.Clamp(0.5 - distance, 0, 1) * Math.Clamp(distance + thickness + 0.5, 0, 1);
                if (coverage <= 0) continue;

                var a = (int)Math.Round(coverage * 255);
                pixels[y * width + x] = (a << 24) | ((red * a / 255) << 16) | ((green * a / 255) << 8) | (blue * a / 255);
            }
        }
        return pixels;
    }
}
