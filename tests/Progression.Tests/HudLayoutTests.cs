using System;
using VeteranSoldiers;

internal static class HudLayoutTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var scene in new[] {
            (Width:1280f, Height:720f, Left:147f, Top:538f, MapWidth:190f, MapHeight:180f),
            (Width:1920f, Height:1080f, Left:220f, Top:800f, MapWidth:285f, MapHeight:270f),
            (Width:3440f, Height:1440f, Left:290f, Top:1060f, MapWidth:380f, MapHeight:360f),
            (Width:3840f, Height:2160f, Left:444f, Top:1600f, MapWidth:570f, MapHeight:540f),
            (Width:1280f, Height:720f, Left:20f, Top:538f, MapWidth:190f, MapHeight:180f) })
        {
            var p = VeteranHudLayout.Place(scene.Width, scene.Height, scene.Left, scene.Top, scene.MapWidth, scene.MapHeight);
            string label = scene.Width + "x" + scene.Height + " mapLeft=" + scene.Left;
            check(p.Left >= 0 && p.Top >= 0 && p.Left + p.Width <= scene.Width && p.Top + p.Height <= scene.Height,
                "Veterans access stays inside viewport " + label);
            check(p.Left + p.Width <= scene.Left || p.Top + p.Height <= scene.Top,
                "Veterans access clears the native minimap " + label);
            check(p.Left + p.Width < scene.Width * .5f && p.Top > scene.Height * .35f,
                "Veterans access cannot cover the right-hand alert column or top resources " + label);
            check(p.Height >= 32 && p.Height <= 80, "physical access target remains usable " + label);
        }
        foreach (float invalid in new[] { 0f, float.NaN, float.PositiveInfinity })
        {
            var p = VeteranHudLayout.Place(1280, 720, invalid, invalid, invalid, invalid);
            check(p.Left >= 0 && p.Left + p.Width < 640 && p.Top > 360 && !float.IsNaN(p.Top),
                "missing or invalid native minimap uses a left-side fallback " + invalid);
        }
        var small = VeteranHudLayout.Place(160, 120, 0, 0, 0, 0);
        check(small.Left >= 0 && small.Top >= 0 && small.Left + small.Width <= 160 && small.Top + small.Height <= 120,
            "fallback clamps to a small viewport without a negative rectangle");
    }
}
