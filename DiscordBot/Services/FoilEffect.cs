using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>Gives a card face a holographic foil look: a rainbow sheen with diagonal bands, screen-blended over the photo.</summary>
    public static class FoilEffect
    {
        private const float _maxStrength = 0.45f; //how strongly the rainbow covers the photo at its brightest band
        private const float _bandsPerImage = 2.5f; //how many sheen bands cross the card diagonally
        private const int _jpegQuality = 90;

        /// <summary>Takes JPEG (or any ImageSharp-readable) bytes and returns a foil JPEG of the same size.</summary>
        public static byte[] Apply(byte[] imageBytes)
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            int width = image.Width;
            int height = image.Height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    //position along the diagonal, 0 to 1
                    double t = (x + y) / (double)(width + height);

                    //hue sweeps through the rainbow across the card
                    double hue = (t * 360.0 * 1.5) % 360.0;
                    var (r, g, b) = HsvToRgb(hue, 0.55, 1.0);

                    //sheen: a smooth band wave, so some stripes are brighter than others
                    double wave = 0.5 + 0.5 * Math.Sin(t * _bandsPerImage * 2 * Math.PI);
                    float strength = _maxStrength * (float)(0.35 + 0.65 * wave);

                    Rgba32 original = image[x, y];
                    image[x, y] = new Rgba32(
                        Blend(original.R, r, strength),
                        Blend(original.G, g, strength),
                        Blend(original.B, b, strength),
                        original.A);
                }
            }

            using var stream = new MemoryStream();
            image.SaveAsJpeg(stream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = _jpegQuality });
            return stream.ToArray();
        }

        /// <summary>Screen-blends the rainbow colour over the photo, then mixes by strength.</summary>
        private static byte Blend(byte photo, byte rainbow, float strength)
        {
            float screened = 255f - (255f - photo) * (255f - rainbow) / 255f;
            float mixed = photo + (screened - photo) * strength;
            return (byte)Math.Clamp(mixed, 0f, 255f);
        }

        private static (byte r, byte g, byte b) HsvToRgb(double hue, double saturation, double value)
        {
            double c = value * saturation;
            double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
            double m = value - c;

            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
