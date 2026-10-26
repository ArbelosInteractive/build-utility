using System;
using UnityEngine;

public static class DlcAppleScreenshot
{
    public const int Width = 1179;
    public const int Height = 2556;

    // Export a separate opaque PNG. The source file is never modified.
    public static byte[] Prepare(byte[] source)
    {
        Texture2D input = null, output = null;
        try
        {
            input = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!ImageConversion.LoadImage(input, source, false))
                throw new InvalidOperationException("Apple review screenshot could not be decoded as an image.");
            var original = input.GetPixels32();
            double scale = Math.Min((double)Width / input.width, (double)Height / input.height);
            int width = Math.Max(1, Math.Min(Width, (int)Math.Round(input.width * scale)));
            int height = Math.Max(1, Math.Min(Height, (int)Math.Round(input.height * scale)));
            int left = (Width - width) / 2, bottom = (Height - height) / 2;
            var pixels = new Color32[Width * Height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);
            for (int y = 0; y < height; y++)
            {
                float sy = Mathf.Clamp((y + 0.5f) * input.height / height - 0.5f, 0, input.height - 1);
                int y0 = (int)sy, y1 = Math.Min(y0 + 1, input.height - 1);
                float fy = sy - y0;
                for (int x = 0; x < width; x++)
                {
                    float sx = Mathf.Clamp((x + 0.5f) * input.width / width - 0.5f, 0, input.width - 1);
                    int x0 = (int)sx, x1 = Math.Min(x0 + 1, input.width - 1);
                    float fx = sx - x0;
                    var a = Color.Lerp(Opaque(original[y0 * input.width + x0]), Opaque(original[y0 * input.width + x1]), fx);
                    var b = Color.Lerp(Opaque(original[y1 * input.width + x0]), Opaque(original[y1 * input.width + x1]), fx);
                    pixels[(bottom + y) * Width + left + x] = Color.Lerp(a, b, fy);
                }
            }
            output = new Texture2D(Width, Height, TextureFormat.RGB24, false, true);
            output.SetPixels32(pixels);
            output.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(output);
            if (png == null || png.Length == 0) throw new InvalidOperationException("Apple screenshot PNG encoding failed.");
            return png;
        }
        finally
        {
            if (output) UnityEngine.Object.DestroyImmediate(output);
            if (input) UnityEngine.Object.DestroyImmediate(input);
        }
    }

    static Color Opaque(Color32 pixel)
    {
        float alpha = pixel.a / 255f;
        return new Color(pixel.r / 255f * alpha, pixel.g / 255f * alpha, pixel.b / 255f * alpha, 1f);
    }
}
