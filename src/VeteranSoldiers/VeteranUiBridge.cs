using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Audio;
using HarmonyLib;
using Systems.InputSystems;
using Systems.InputSystems.Interactions;
using Systems.InputSystems.Raw;
using TMPro;
using UI;
using UI.HelpersStorage;
using UI.New;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace VeteranSoldiers;

/// <summary>Native presentation and a scoped input boundary. Never mutates the game's UIState.</summary>
internal static class VeteranUiBridge
{
    private static Action<PlayerInputSystem>? runInteractions;
    private static readonly FieldInfo? Triggers = AccessTools.Field(typeof(PlayerInputSystem), "_interactionTriggers");
    private static readonly FieldInfo? Drag = AccessTools.Field(typeof(PlayerInputSystem), "_lockDragAction");
    private static bool wasBlocked;
    private static int blockThrough = -1;
    internal static bool InputAvailable { get; private set; }
    internal static bool InputBlocked => VeteranArmyWindow.IsOpen || VeteranArmyWindow.PointerOverAccessButton ||
        VeteranPromotionFeed.PointerOverVisibleCard || Time.frameCount <= blockThrough;
    internal static readonly Color Gold = new Color(.85f, .68f, .41f);
    internal static readonly Color Ivory = new Color(1f, .95f, .82f);
    internal static readonly Color Muted = new Color(.84f, .80f, .71f);
    internal static readonly Color Panel = new Color(.30f, .27f, .235f, .995f);
    internal static readonly Color Recess = new Color(.235f, .215f, .19f, .98f);
    internal static TMP_FontAsset? Font;
    internal static Material? FontMaterial;
    private static TMP_FontAsset? headingFont;
    private static Material? headingMaterial;
    private static Image? buttonTemplate;
    private static Sprite? frameSprite;
    private static Color frameColor = Color.white;

    internal static void Initialize(Harmony harmony)
    {
        try
        {
            Type job = AccessTools.Inner(typeof(PlayerInputSystem), "InteractionJob") ?? throw new MissingMemberException("InteractionJob");
            var run = AccessTools.Method(job, "Run") ?? throw new MissingMethodException("InteractionJob.Run");
            if (Triggers == null || Drag == null) throw new MissingFieldException("PlayerInputSystem interaction state");
            if (Triggers.FieldType != typeof(NativeParallelHashMap<RawKeyBinding, InteractionTriggers>) || Drag.FieldType != typeof(NativeReference<RawKeyBinding>))
                throw new InvalidOperationException("PlayerInputSystem interaction containers have changed.");
            runInteractions = (Action<PlayerInputSystem>)Delegate.CreateDelegate(typeof(Action<PlayerInputSystem>), run);
            harmony.Patch(AccessTools.Method(typeof(PlayerInputSystem), "OnUpdate"), transpiler: new HarmonyMethod(typeof(VeteranUiBridge), nameof(InputBoundary)));
            InputAvailable = true;
        }
        catch (Exception error) { VeteranRuntime.Log.LogWarning("Veteran windows disabled: native input boundary unavailable. " + error); }
    }

    private static IEnumerable<CodeInstruction> InputBoundary(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
                method.Name == "Run" && method.DeclaringType?.Name == "InteractionJob" && method.DeclaringType.DeclaringType == typeof(PlayerInputSystem))
            {
                instruction.operand = AccessTools.Method(typeof(VeteranUiBridge), nameof(DispatchInteractions));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Expected exactly one native interaction dispatch; found " + replaced);
    }

    private static void DispatchInteractions(PlayerInputSystem system)
    {
        bool blocked = InputBlocked;
        // Raw keys still update normally, and actions queued by native UI or another mod are untouched.
        // Clear drag/double-click state on both boundaries so a held world gesture cannot leak through.
        if (blocked != wasBlocked)
        {
            var triggers = (NativeParallelHashMap<RawKeyBinding, InteractionTriggers>)Triggers!.GetValue(system);
            using var keys = triggers.GetKeyArray(Allocator.Temp);
            foreach (var key in keys) triggers[key] = default;
            var drag = (NativeReference<RawKeyBinding>)Drag!.GetValue(system);
            drag.Value = default;
            wasBlocked = blocked;
        }
        if (!blocked) runInteractions!(system);
    }

    internal static void Closed() => blockThrough = Time.frameCount + 2;
    internal static void Click() { try { AudioManager.PlayUISound(UIAudioEvent.Click); } catch { } }
    internal static void CaptureStyle()
    {
        var native = UnityEngine.Object.FindObjectOfType<InfoPanel>();
        if (native != null)
        {
            foreach (var text in native.GetComponentsInChildren<TMP_Text>(true))
                if (text.font != null) { Font = text.font; FontMaterial = text.fontSharedMaterial; break; }
            var nativeTitle = native.transform.Find("CommonSingleInfo/Name")?.GetComponent<TMP_Text>();
            if (nativeTitle != null && nativeTitle.font != null)
            { headingFont = nativeTitle.font; headingMaterial = nativeTitle.fontSharedMaterial; }
            float largest = 0;
            foreach (var image in native.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null || image.type != Image.Type.Sliced || image.sprite.border.sqrMagnitude < 1) continue;
                float area = image.rectTransform.rect.width * image.rectTransform.rect.height;
                if (area <= largest) continue;
                largest = area; frameSprite = image.sprite; frameColor = image.color;
            }
        }
        if (Font == null) Font = TMP_Settings.defaultFontAsset;
        var buttons = UnityEngine.Object.FindObjectOfType<MainButtonsManager>();
        var research = buttons == null ? null : AccessTools.Field(typeof(MainButtonsManager), "_researchButton")?.GetValue(buttons) as Button;
        buttonTemplate = research == null ? null : research.targetGraphic as Image;
    }
    internal static void Heading(TMP_Text label)
    {
        if (headingFont == null) return;
        label.font = headingFont;
        if (headingMaterial != null) label.fontSharedMaterial = headingMaterial;
    }

    internal static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }
    internal static Image Fill(RectTransform rect, Color color, bool raycast = false)
    {
        var image = rect.gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = raycast; return image;
    }
    internal static TMP_Text Text(Transform parent, string name, string text, float x, float y, float width, float height, float size, Color color, bool wrap = false)
    {
        var label = Rect(name, parent, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = Font; if (FontMaterial != null) label.fontSharedMaterial = FontMaterial;
        label.text = text; label.fontSize = size; label.color = color; label.richText = false;
        label.raycastTarget = false; label.enableWordWrapping = wrap; label.overflowMode = TextOverflowModes.Ellipsis;
        label.alignment = TextAlignmentOptions.MidlineLeft; return label;
    }
    internal static Button Button(Transform parent, string name, string text, float x, float y, float width, float height, Action action, out TMP_Text label, string hint = "")
    {
        var rect = Rect(name, parent, x, y, width, height);
        var fill = Fill(rect, Color.white, true);
        // A research icon is not a stretchable button background. Reuse only an actual nine-slice.
        if (buttonTemplate != null && buttonTemplate.sprite != null && buttonTemplate.type == Image.Type.Sliced && buttonTemplate.sprite.border.sqrMagnitude > 0)
        { fill.sprite = buttonTemplate.sprite; fill.type = Image.Type.Sliced; }
        else
        {
            Fill(Rect("TopRim", rect, 0, 0, width, 1), new Color(.84f, .74f, .54f, .55f));
            Fill(Rect("BottomShadow", rect, 0, height - 2, width, 2), new Color(.06f, .05f, .04f, .55f));
            Fill(Rect("LeftRim", rect, 0, 0, 1, height), new Color(.84f, .74f, .54f, .3f));
            Fill(Rect("RightShadow", rect, width - 1, 0, 1, height), new Color(.06f, .05f, .04f, .4f));
        }
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = fill;
        var colors = button.colors; colors.normalColor = new Color(.49f, .43f, .35f);
        colors.highlightedColor = new Color(.72f, .60f, .40f); colors.pressedColor = Gold;
        colors.selectedColor = colors.normalColor; colors.disabledColor = new Color(.28f, .27f, .25f, .65f);
        colors.fadeDuration = .08f; button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() =>
        {
            try { Click(); action(); }
            catch (Exception error) { VeteranRuntime.Log.LogWarning("Veteran interface action could not complete: " + error); }
        });
        label = Text(rect, "Label", text, 7, 0, width - 14, height, 19, Ivory); label.alignment = TextAlignmentOptions.Center;
        if (!string.IsNullOrEmpty(hint)) Hint(rect.gameObject, text, hint);
        return button;
    }
    internal static HintableElement Hint(GameObject owner, string title, string description)
    {
        var hint = owner.AddComponent<HintableElement>(); hint._constValue = false;
        hint.Init(new HintData { type = HintType.Mouse, Tittle = title, Description = description }); return hint;
    }
    internal static void HideHints(Transform parent)
    {
        foreach (var hint in parent.GetComponentsInChildren<HintableElement>(true)) hint.HintCleanup();
    }
    internal static void Frame(RectTransform panel)
    {
        float width = panel.rect.width, height = panel.rect.height;
        if (frameSprite != null)
        {
            var nativeFrame = Fill(Rect("NativeFrame", panel, 0, 0, width, height), frameColor);
            nativeFrame.sprite = frameSprite; nativeFrame.type = Image.Type.Sliced;
            // Native panel sprites include an opaque center. Retain their edge ornament
            // without allowing that center to replace the journal's dark reading surface.
            nativeFrame.fillCenter = false;
        }
        // Angular brass cornerwork mirrors the native panel's geometric ornaments.
        foreach (float x in new[] { 8f, width - 8f })
        foreach (float y in new[] { 8f, height - 8f })
        {
            var diamond = Rect("CornerDiamond", panel, x, y, 12, 12);
            diamond.pivot = new Vector2(.5f, .5f); diamond.localRotation = Quaternion.Euler(0, 0, 45);
            Fill(diamond, Gold);
            var center = Rect("CornerInset", diamond, 3, 3, 6, 6); Fill(center, Recess);
        }
        Fill(Rect("FrameTopInset", panel, 18, 7, width - 36, 1), Gold * .75f);
        Fill(Rect("FrameBottomInset", panel, 18, height - 8, width - 36, 1), Gold * .75f);
        Fill(Rect("FrameLeftInset", panel, 7, 18, 1, height - 36), Gold * .75f);
        Fill(Rect("FrameRightInset", panel, width - 8, 18, 1, height - 36), Gold * .75f);
    }
}
