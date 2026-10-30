using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC Icon Document")]
public sealed class DlcIconDocument : ScriptableObject
{
    public string productId = "";
    public int size = 64;
    public bool pixelArt = true;
    public List<DlcIconLayer> layers = new List<DlcIconLayer>();
    public string assignedPath = "";
    public void Initialize(int dimension)
    {
        if (dimension < 16 || dimension > 512) throw new ArgumentOutOfRangeException(nameof(dimension));
        size = dimension;
        layers = new List<DlcIconLayer> { new DlcIconLayer("Artwork", size) };
    }
}

[Serializable]
public sealed class DlcIconLayer
{
    public string name;
    public bool visible = true;
    public Color32[] pixels;
    public DlcIconLayer(string title, int size) { name = title; pixels = new Color32[size * size]; }
}

public static class DlcIconPixels
{
    public static Color32[] Composite(DlcIconDocument doc)
    {
        if (!doc || doc.size < 16 || doc.size > 512) throw new InvalidOperationException("Invalid icon dimensions.");
        var output = new Color32[doc.size * doc.size];
        foreach (var layer in doc.layers)
        {
            if (layer == null || layer.pixels == null || layer.pixels.Length != output.Length) throw new InvalidOperationException("Invalid icon layer.");
            if (!layer.visible) continue;
            for (int i = 0; i < output.Length; i++)
            {
                Color top = layer.pixels[i], bottom = output[i];
                float alpha = top.a + bottom.a * (1 - top.a);
                output[i] = alpha == 0 ? Color.clear : new Color(
                    (top.r * top.a + bottom.r * bottom.a * (1 - top.a)) / alpha,
                    (top.g * top.a + bottom.g * bottom.a * (1 - top.a)) / alpha,
                    (top.b * top.a + bottom.b * bottom.a * (1 - top.a)) / alpha, alpha);
            }
        }
        return output;
    }
    public static void Set(Color32[] pixels, int size, int x, int y, Color32 color, bool mirror = false)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        pixels[y * size + x] = color;
        if (mirror) pixels[y * size + size - 1 - x] = color;
    }
    public static void Line(Color32[] pixels, int size, Vector2Int a, Vector2Int b, Color32 color, bool mirror)
    {
        int dx = Math.Abs(b.x - a.x), dy = -Math.Abs(b.y - a.y), sx = a.x < b.x ? 1 : -1, sy = a.y < b.y ? 1 : -1, err = dx + dy;
        while (true) { Set(pixels, size, a.x, a.y, color, mirror); if (a == b) break; int e = 2 * err; if (e >= dy) { err += dy; a.x += sx; } if (e <= dx) { err += dx; a.y += sy; } }
    }
    public static void Fill(Color32[] pixels, int size, int x, int y, Color32 color)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        var original = pixels[y * size + x];
        if (original.Equals(color)) return;
        var pending = new Stack<Vector2Int>(); pending.Push(new Vector2Int(x, y));
        while (pending.Count > 0)
        {
            var p = pending.Pop();
            if (p.x < 0 || p.y < 0 || p.x >= size || p.y >= size || !pixels[p.y * size + p.x].Equals(original)) continue;
            pixels[p.y * size + p.x] = color;
            pending.Push(p + Vector2Int.left); pending.Push(p + Vector2Int.right); pending.Push(p + Vector2Int.up); pending.Push(p + Vector2Int.down);
        }
    }
    public static void Move(Color32[] pixels, int size, RectInt selection, Vector2Int delta)
    {
        var before = (Color32[])pixels.Clone();
        for (int y = selection.yMin; y < selection.yMax; y++) for (int x = selection.xMin; x < selection.xMax; x++) Set(pixels, size, x, y, Color.clear);
        for (int y = selection.yMin; y < selection.yMax; y++) for (int x = selection.xMin; x < selection.xMax; x++)
            if (x >= 0 && y >= 0 && x < size && y < size) Set(pixels, size, x + delta.x, y + delta.y, before[y * size + x]);
    }
    public static readonly string[] Templates = { "Arena", "Wrestler", "Outfit", "Taunt", "Prop", "Bundle", "Frame", "Badge" };
    public static Color32[] Template(string name, int size, Color32 color)
    {
        if (Array.IndexOf(Templates, name) < 0) throw new ArgumentException("Unknown template.");
        var pixels = new Color32[size * size];
        // Original low-resolution silhouettes, scaled without filtering.
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int u = x * 16 / size, v = y * 16 / size; bool on = false;
            switch (name)
            {
                case "Arena": on = (u == 2 || u == 13) && v >= 3 && v <= 12 || u >= 2 && u <= 13 && (v == 3 || v == 7 || v == 11); break;
                case "Wrestler": on = (u - 8) * (u - 8) + (v - 10) * (v - 10) <= 10 || v >= 2 && v <= 5 && u >= 3 && u <= 12; break;
                case "Outfit": on = v >= 3 && v <= 12 && u >= 5 && u <= 10 || v >= 9 && v <= 12 && u >= 2 && u <= 13; break;
                case "Taunt": on = u >= 7 && u <= 9 && v >= 4 && v <= 11 || (u + v == 17 || v - u == 1) && v >= 8 && v <= 13 || v <= 4 && v >= 1 && (u + v == 10 || u - v == 6); break;
                case "Prop": on = v >= 2 && v <= 11 && u >= 4 && u <= 11 && (u == 4 || u == 11 || v == 2 || v == 11) || v == 13 && u >= 3 && u <= 12; break;
                case "Bundle": on = u >= 3 && u <= 12 && v >= 3 && v <= 12 && (u == 3 || u == 12 || v == 3 || v == 12 || u == 7 || v == 8); break;
                case "Frame": on = u == 0 || u == 15 || v == 0 || v == 15; break;
                case "Badge": on = u >= 10 && v <= 5 && Math.Abs(u - 12) + Math.Abs(v - 3) <= 3; break;
            }
            if (on) pixels[y * size + x] = color;
        }
        return pixels;
    }
    public static Texture2D Texture(DlcIconDocument doc)
    {
        var texture = new Texture2D(doc.size, doc.size, TextureFormat.RGBA32, false);
        texture.filterMode = doc.pixelArt ? FilterMode.Point : FilterMode.Bilinear;
        texture.SetPixels32(Composite(doc)); texture.Apply(); return texture;
    }
}

public interface IDlcIconAuthoring
{
    string IconAssetPath(string productId);
}
