using System;
using UnityEngine;

namespace VeteranSoldiers;

/// <summary>Five shared, antialiased heraldic seals. No per-soldier texture or material.</summary>
internal static class VeteranVisualAssets
{
    private static readonly Sprite?[] sprites = new Sprite?[5];
    private static readonly Color[] metals = {
        new Color(.65f, .65f, .60f), new Color(.81f, .53f, .30f),
        new Color(.77f, .84f, .87f), new Color(1f, .77f, .34f),
        new Color(1f, .93f, .69f)
    };
    private static readonly Vector2[] shield = {
        new Vector2(.13f,.90f), new Vector2(.87f,.90f), new Vector2(.87f,.44f),
        new Vector2(.76f,.25f), new Vector2(.50f,.065f), new Vector2(.24f,.25f), new Vector2(.13f,.44f)
    };
    private static readonly Vector2[] inner = Scale(shield, .86f);
    private static readonly Vector2[] inset = Scale(shield, .79f);

    public static Color RankColor(int level) => metals[Mathf.Clamp(level, 0, 4)];
    public static Color SpecialtyColor(Specialization value) => value == Specialization.Assault ? new Color(.93f, .43f, .28f) :
        value == Specialization.Guardian ? new Color(.43f, .72f, .90f) : new Color(.77f, .82f, .45f);

    public static Sprite RankSprite(int level)
    {
        level = Mathf.Clamp(level, 0, 4);
        if (sprites[level] != null) return sprites[level]!;
        const int size = 128, samples = 3;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            Color premultiplied = Color.clear;
            for (int sy = 0; sy < samples; sy++)
            for (int sx = 0; sx < samples; sx++)
            {
                Color sample = Pixel((x + (sx + .5f) / samples) / size,
                    (y + (sy + .5f) / samples) / size, level);
                premultiplied.r += sample.r * sample.a;
                premultiplied.g += sample.g * sample.a;
                premultiplied.b += sample.b * sample.a;
                premultiplied.a += sample.a;
            }
            float coverage = premultiplied.a;
            pixels[y * size + x] = coverage > 0
                ? new Color(premultiplied.r / coverage, premultiplied.g / coverage,
                    premultiplied.b / coverage, coverage / (samples * samples)) : Color.clear;
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Veteran Soldiers / heraldry / " + level,
            filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), size);
        sprite.name = texture.name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        sprites[level] = sprite;
        return sprite;
    }

    private static Color Pixel(float x, float y, int level)
    {
        // Drop shadow, double metal rim and an enamel face keep the seal legible at 24 px.
        if (!Inside(shield, x, y))
            return Inside(shield, x - .014f, y + .022f) ? new Color(.03f,.025f,.02f,.65f) : Color.clear;
        Color metal = RankColor(level);
        float light = Mathf.Clamp01(.30f + .55f * y + .15f * (1 - x));
        Color rim = Color.Lerp(metal * .43f, Color.Lerp(metal, Color.white, .36f), light);
        rim.a = 1;
        if (!Inside(inner, x, y)) return rim;
        if (!Inside(inset, x, y)) return new Color(.20f,.155f,.10f,1);
        Color enamel = Color.Lerp(new Color(.13f,.12f,.105f), new Color(.29f,.265f,.22f), y);
        if (x < .50f) enamel = Color.Lerp(enamel, metal, .045f);
        Color ink = Color.Lerp(metal, Color.white, .12f + .15f * y);
        ink.a = 1;
        bool mark = false;
        if (level == 0)
        {
            mark = Diamond(x, y, .50f, .53f, .085f, .13f);
        }
        else if (level < 4)
        {
            float first = level == 1 ? .60f : level == 2 ? .68f : .74f;
            for (int i = 0; i < level; i++)
            {
                float peak = first - i * .165f;
                float line = peak - Mathf.Abs(x - .50f) * .62f;
                if (Mathf.Abs(x - .50f) < .21f && y <= line && y >= line - .072f) mark = true;
            }
        }
        else
        {
            float dx = x - .50f, dy = y - .58f;
            float angle = Mathf.Atan2(dx, dy);
            float sector = Mathf.Repeat(angle + Mathf.PI / 5, 2 * Mathf.PI / 5) - Mathf.PI / 5;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float radius = Mathf.Lerp(.095f, .215f, 1 - Mathf.Abs(sector) / (Mathf.PI / 5));
            mark = r < radius;
            for (int side = -1; side <= 1; side += 2)
            for (int leaf = 0; leaf < 4; leaf++)
            {
                float cx = .50f + side * (.155f + .028f * leaf);
                float cy = .31f + .088f * leaf;
                float lx = (x - cx) * side, ly = y - cy;
                mark |= Mathf.Abs(lx * .80f + ly * .60f) / .026f +
                    Mathf.Abs(-lx * .60f + ly * .80f) / .057f < 1;
            }
        }
        // Small rivets and central foot diamond echo the game's angular ornament.
        mark |= Diamond(x, y, .265f, .79f, .018f, .018f) || Diamond(x, y, .735f, .79f, .018f, .018f);
        if (level >= 2) mark |= Diamond(x, y, .50f, .235f, .023f, .027f);
        return mark ? ink : enamel;
    }

    private static bool Diamond(float x, float y, float cx, float cy, float rx, float ry) =>
        Mathf.Abs(x - cx) / rx + Mathf.Abs(y - cy) / ry <= 1;

    private static Vector2[] Scale(Vector2[] polygon, float factor)
    {
        var result = new Vector2[polygon.Length];
        var center = new Vector2(.5f, .52f);
        for (int i = 0; i < polygon.Length; i++) result[i] = center + (polygon[i] - center) * factor;
        return result;
    }

    private static bool Inside(Vector2[] polygon, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }
}
