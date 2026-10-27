using System;
using System.Text;
using Components;
using TMPro;
using UI.New;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace VeteranSoldiers;

/// <summary>A single native-canvas promotion notice. Batches are bounded and actions revalidate world/entity identity.</summary>
internal sealed class VeteranPromotionFeed : MonoBehaviour
{
    private static readonly PromotionQueue Queue = new PromotionQueue();
    private static VeteranPromotionFeed? instance;
    private static bool failed;
    private static Entity pendingEntity, visibleEntity;
    private static World? pendingWorld, visibleWorld;
    private static int pendingRank;
    private RectTransform? card;
    private CanvasGroup? group;
    private TMP_Text heading = null!, detail = null!, footer = null!;
    private Image shield = null!;
    private int renderedRevision = -1;
    private readonly Vector3[] corners = new Vector3[4];
    private Canvas[] nativeCanvases = Array.Empty<Canvas>();
    private float nextCanvasSearch;
    internal static void Clear()
    {
        Queue.Clear(); pendingEntity = visibleEntity = Entity.Null; pendingWorld = visibleWorld = null; pendingRank = 0;
        if (instance != null && instance.card != null) instance.card.gameObject.SetActive(false);
    }
    internal static bool HasVisibleCard => Queue.Total > 0 && CanShow;
    // Called before native interaction dispatch. Use only rendered visibility and geometry,
    // not CanShow or input state, so hit testing cannot recurse through the input boundary.
    internal static bool PointerOverVisibleCard => instance != null && instance.card != null &&
        instance.card.gameObject.activeInHierarchy && Queue.Total > 0 &&
        RectTransformUtility.RectangleContainsScreenPoint(instance.card, Input.mousePosition, CanvasCamera(instance.card));
    private static void Dismiss()
    {
        // The card disappears on pointer-up; keep that same gesture out of world selection.
        VeteranUiBridge.Closed();
        Clear();
    }
    internal static void RefreshLanguage()
    {
        Clear();
        if (instance == null) return;
        if (instance.card != null)
        {
            VeteranUiBridge.HideHints(instance.card);
            Destroy(instance.card.gameObject);
            instance.card = null;
            instance.group = null;
        }
        instance.renderedRevision = -1;
    }
    internal static bool Overlaps(Rect area)
    {
        if (!HasVisibleCard || instance == null || instance.card == null) return false;
        instance.card.GetWorldCorners(instance.corners);
        var canvas = instance.card.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, instance.corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, instance.corners[2]);
        return new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y).Overlaps(area);
    }
    private static bool CanShow => !failed && VeteranRuntime.Ready && VeteranRuntime.Settings.ShowPromotionNotifications.Value &&
        !VeteranArmyWindow.IsOpen && VeteranNativePanel.GameplayOverlayAllowed;
    private void Awake() => instance = this;
    internal static void Promoted(Entity entity, int previousRank, int nextRank)
    {
        try
        {
            if (!CanShow || VeteranRuntime.Manager.HasComponent<Enemy>(entity)) return;
            Queue.Add(previousRank, nextRank, Time.unscaledTime);
            if (pendingEntity == Entity.Null || nextRank >= pendingRank)
            { pendingEntity = entity; pendingWorld = VeteranRuntime.ActiveWorld; pendingRank = nextRank; }
        }
        catch (Exception error) { Disable(error); }
    }
    private void Update()
    {
        try
        {
            if (!CanShow) { if (Queue.Total > 0 || Queue.HasPending) Clear(); renderedRevision = -1; return; }
            bool pointerInside = PointerOverVisibleCard;
            if (!Queue.Tick(Time.unscaledTime, pointerInside))
            {
                if (pointerInside) VeteranUiBridge.Closed(); // The hover cap may expire during a pointer gesture.
                if (card != null) card.gameObject.SetActive(false);
                return;
            }
            if (card == null) Build();
            if (card == null || group == null) return;
            card.gameObject.SetActive(true); Fit(); group.alpha = Queue.Opacity(Time.unscaledTime);
            if (renderedRevision == Queue.Revision) return;
            visibleEntity = pendingEntity; visibleWorld = pendingWorld; pendingEntity = Entity.Null; pendingWorld = null; pendingRank = 0;
            shield.sprite = VeteranVisualAssets.RankSprite(Queue.HighestRank);
            heading.text = Queue.Total == 1 ? VeteranText.PromotedTo(VeteranRuntime.Settings.Ranks[Queue.HighestRank].Name) : VeteranText.PromotionCount(Queue.Total);
            var summary = new StringBuilder();
            for (int i = 1; i < 5; i++)
            {
                if (Queue.Count(i) == 0) continue;
                if (summary.Length > 0) summary.Append(" · ");
                summary.Append(VeteranRuntime.Settings.Ranks[i].Name);
                if (Queue.Total > 1) summary.Append(" ×").Append(VeteranText.Number(Queue.Count(i), "N0"));
            }
            detail.text = Queue.Total == 1 && visibleEntity != Entity.Null ? VeteranArmy.UnitName(visibleEntity) : summary.ToString();
            footer.text = VeteranText.Choose("OPEN SERVICE RECORD ›", "ABRIR REGISTRO DE SERVICIO ›");
            renderedRevision = Queue.Revision;
        }
        catch (Exception error) { Disable(error); }
    }
    private void Open()
    {
        Entity entity = visibleEntity; World? world = visibleWorld; bool single = Queue.Total == 1;
        VeteranUiBridge.Closed();
        Clear();
        bool currentWorld = world != null && world.IsCreated && ReferenceEquals(world, VeteranRuntime.ActiveWorld);
        if (currentWorld) world!.EntityManager.CompleteAllJobs();
        currentWorld = world != null && world.IsCreated && ReferenceEquals(world, VeteranRuntime.ActiveWorld);
        if (single && currentWorld && VeteranRuntime.IsEligible(entity))
        { VeteranArmy.SelectOne(world, entity); VeteranArmy.Locate(world, entity); VeteranArmyWindow.OpenSoldier(entity); }
        else VeteranArmyWindow.OpenRoster();
    }
    private void Build()
    {
        if (!UIViewManager.Exist) return;
        VeteranUiBridge.CaptureStyle(); if (VeteranUiBridge.Font == null) return;
        var go = new GameObject("VeteranSoldiersPromotion", typeof(RectTransform)); UIViewManager.AddCustomUIToCanvas(go);
        go.layer = go.transform.parent.gameObject.layer;
        card = (RectTransform)go.transform; card.localScale = Vector3.one; card.localRotation = Quaternion.identity;
        card.anchorMin = card.anchorMax = new Vector2(.5f, 1); card.pivot = new Vector2(.5f, 1);
        card.sizeDelta = new Vector2(536, 116);
        nextCanvasSearch = 0;
        group = go.AddComponent<CanvasGroup>();
        VeteranUiBridge.Fill(card, VeteranUiBridge.Panel, true);
        var click = go.AddComponent<Button>(); click.targetGraphic = go.GetComponent<Image>();
        click.transition = UnityEngine.UI.Selectable.Transition.None; click.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        click.onClick.AddListener(() => { VeteranUiBridge.Click(); Open(); });
        VeteranUiBridge.Fill(VeteranUiBridge.Rect("Rule", card, 0, 0, 536, 2), VeteranUiBridge.Gold);
        shield = VeteranUiBridge.Fill(VeteranUiBridge.Rect("Rank", card, 14, 15, 70, 81), Color.white); shield.preserveAspect = true;
        heading = VeteranUiBridge.Text(card, "Heading", "", 98, 10, 397, 35, 23, VeteranUiBridge.Ivory);
        detail = VeteranUiBridge.Text(card, "Detail", "", 98, 45, 414, 30, 18, VeteranUiBridge.Muted);
        footer = VeteranUiBridge.Text(card, "Action", "", 98, 83, 414, 22, 15, VeteranUiBridge.Gold);
        VeteranUiBridge.Button(card, "Dismiss", "×", 499, 7, 29, 28, Dismiss, out _);
        VeteranUiBridge.Hint(go, VeteranText.Choose("Combat promotion", "Ascenso en combate"), VeteranText.Choose("Open the soldier's record, or the army register for a group of promotions.", "Abre la ficha del soldado, o el registro del ejército para varios ascensos."));
    }
    private static Camera? CanvasCamera(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }
    private Rect ScreenBounds(RectTransform rect)
    {
        rect.GetWorldCorners(corners);
        var camera = CanvasCamera(rect);
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]), max = min;
        for (int i = 1; i < corners.Length; i++)
        { var point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]); min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
        return new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
    }
    private void Fit()
    {
        if (card == null || !(card.parent is RectTransform parent) || parent.rect.width <= 1 || parent.rect.height <= 1) return;
        Rect parentBounds = ScreenBounds(parent);
        float pixelsPerX = parentBounds.width / parent.rect.width, pixelsPerY = parentBounds.height / parent.rect.height;
        if (pixelsPerX <= .01f || pixelsPerY <= .01f) return;
        // Match the HUD/journal's physical scaling instead of inheriting a half/third-size native canvas.
        float height = Mathf.Clamp(Screen.height * (116f / 1080), 96, 232);
        float scale = Mathf.Min(height / (116 * pixelsPerY), Screen.width * .9f / (536 * pixelsPerX));
        card.localScale = Vector3.one * scale;
        float width = 536 * scale * pixelsPerX;
        height = 116 * scale * pixelsPerY;
        float left = (Screen.width - width) * .5f;
        float top = Mathf.Max(12, Screen.height * .10f);
        if (Time.unscaledTime >= nextCanvasSearch)
        { nextCanvasSearch = Time.unscaledTime + 1; nativeCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>(); }
        foreach (var canvas in nativeCanvases)
        {
            if (canvas == null || !canvas.enabled || !canvas.gameObject.activeInHierarchy || canvas.renderMode == RenderMode.WorldSpace ||
                canvas.transform.IsChildOf(card) || !(canvas.transform is RectTransform rect)) continue;
            Rect bounds = ScreenBounds(rect);
            // Native resource/subpanel canvases expose their actual bounds. Ignore full-screen
            // containers and side/bottom HUD surfaces that cannot occlude this centered notice.
            if (bounds.width < 4 || bounds.height < 4 || bounds.width * bounds.height > Screen.width * Screen.height * .7f ||
                bounds.yMax <= 0 || bounds.yMin > Screen.height * .22f || bounds.height > Screen.height * .3f ||
                bounds.xMax <= left || bounds.xMin >= left + width) continue;
            top = Mathf.Max(top, bounds.yMax + 12);
        }
        top = Mathf.Min(top, Screen.height - height - 12);
        float parentCenterX = parentBounds.xMin + parentBounds.width * .5f;
        card.anchoredPosition = new Vector2((Screen.width * .5f - parentCenterX) / pixelsPerX, -(top - parentBounds.yMin) / pixelsPerY);
    }
    private static void Disable(Exception error)
    { if (!failed) VeteranRuntime.Log.LogWarning("Promotion notifications disabled; combat remains active: " + error.Message); failed = true; Clear(); }
    private void OnDestroy() { Clear(); if (card != null) Destroy(card.gameObject); if (ReferenceEquals(instance, this)) instance = null; }
}
