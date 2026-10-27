using System;

namespace VeteranSoldiers;

internal readonly struct VeteranHudPlacement
{
    internal readonly float Left, Top, Width, Height;
    internal VeteranHudPlacement(float left, float top, float width, float height)
    { Left = left; Top = top; Width = width; Height = height; }
}

/// <summary>Screen-pixel layout beside the native minimap, outside the right-hand alert column.</summary>
internal static class VeteranHudLayout
{
    internal const float DesignWidth = 160, DesignHeight = 44;
    internal static VeteranHudPlacement Place(float width, float height, float mapLeft, float mapTop, float mapWidth, float mapHeight)
    {
        float margin = Math.Max(8, height * .008f);
        float buttonHeight = Math.Max(32, Math.Min(80, height / 27));
        float buttonWidth = DesignWidth * buttonHeight / DesignHeight;
        float left = margin, top = height * .75f - buttonHeight * .5f;
        if (Finite(mapLeft) && Finite(mapTop) && Finite(mapWidth) && Finite(mapHeight) && mapWidth > 1 && mapHeight > 1)
        {
            if (mapLeft >= buttonWidth + margin * 2)
            {
                left = (mapLeft - buttonWidth) * .5f;
                top = mapTop + mapHeight * .5f - buttonHeight * .5f;
            }
            else
            {
                left = mapLeft + (mapWidth - buttonWidth) * .5f;
                top = mapTop - buttonHeight - margin;
            }
        }
        buttonWidth = Math.Min(buttonWidth, Math.Max(1, width - margin * 2));
        buttonHeight = Math.Min(buttonHeight, Math.Max(1, height - margin * 2));
        left = Math.Max(margin, Math.Min(left, width - buttonWidth - margin));
        top = Math.Max(margin, Math.Min(top, height - buttonHeight - margin));
        return new VeteranHudPlacement(left, top, buttonWidth, buttonHeight);
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
