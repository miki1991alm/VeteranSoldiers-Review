using System;
using System.Collections.Generic;
using System.Text;
using Components;
using Components.RawComponents;
using Components.SingletonComponents;
using Components.Skills;
using HarmonyLib;
using Systems.GameStateSystems;
using TMPro;
using UI.New;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Utility;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

internal readonly struct VeteranSelectionEntry
{
    public readonly Entity Entity;
    public readonly int Level;
    public readonly double Experience;
    public readonly float MaximumHealth;
    public readonly double NativeDamage, Damage;
    public readonly bool DamageKnown;
    public VeteranSelectionEntry(Entity entity, int level, double experience, float maximumHealth, double nativeDamage, double damage, bool damageKnown)
    {
        Entity = entity; Level = level; Experience = experience; MaximumHealth = maximumHealth;
        NativeDamage = nativeDamage; Damage = damage; DamageKnown = damageKnown;
    }
}

/// <summary>Native selection view and shared, main-thread selection snapshot. Never changes combat data.</summary>
internal static class VeteranNativePanel
{
    private static readonly List<VeteranSelectionEntry> selected = new List<VeteranSelectionEntry>();
    private static readonly int[] rankCounts = new int[5];
    private static NativeView? view;
    private static bool initialized, failed;
    private static float nextRefresh, nextSearch;
    private static int lastInputFrame = -1;
    private static readonly List<Rect> hudAreas = new List<Rect>();
    private static Canvas[] hudCanvases = Array.Empty<Canvas>();
    private static float nextCanvasSearch;
    private static readonly Vector3[] hudCorners = new Vector3[4];
    private static World? skillsWorld;
    private static EntityQuery skillsQuery;
    private static bool skillsQueryCreated;
    public static IReadOnlyList<VeteranSelectionEntry> SelectedVeterans => selected;
    public static World? SelectionWorld { get; private set; }
    public static int SelectionRevision { get; private set; }
    public static bool Hidden { get; private set; }
    public static bool WorldInsigniasAllowed => !failed && SelectionWorld != null && SelectionWorld.IsCreated && GameplayOverlayAllowed;
    internal static bool GameplayOverlayAllowed
    {
        get
        {
            if (Hidden || !VeteranRuntime.Ready || VeteranRuntime.ActiveWorld == null || !VeteranRuntime.ActiveWorld.IsCreated) return false;
            try
            {
                var ui = GameState.UIState;
                return !GameState.IsDirectControl && !ui.mainMenuOpened && !ui.winLosePanelOpened && !ui.confirmOpened && !ui.cutsceneIsPlaying &&
                    !ui.photoModActive && !ui.researchWindowOpened && !ui.marketWindowOpened && !ui.missionPanelOpened &&
                    !ui.statisticPanelOpened && !ui.sandboxWavePanelOpened && !ui.formationOrderPanelOpened;
            }
            catch { return false; }
        }
    }

    public static bool OverlapsHud(Rect guiScreenRect)
    {
        if (VeteranArmyWindow.Overlaps(guiScreenRect)) return true;
        for (int i = 0; i < hudAreas.Count; i++) if (hudAreas[i].Overlaps(guiScreenRect)) return true;
        return false;
    }

    public static void Initialize(Harmony harmony)
    {
        if (initialized || failed) return;
        try
        {
            Patch(harmony, "StartInit", nameof(OnInitialized));
            Patch(harmony, "GameStateUpdateConsume", nameof(OnNativeUpdate), nameof(BeforeNativeUpdate));
            Patch(harmony, "OnLocalizationUpdate", nameof(OnNativeUpdate), nameof(BeforeNativeUpdate));
            Patch(harmony, "Hide", nameof(OnHidden));
            Patch(harmony, "OnDestroy", nameof(OnDestroyed));
            initialized = true;
        }
        catch (Exception error) { Disable(error); }
    }

    private static void Patch(Harmony harmony, string nativeMethod, string postfix, string? prefix = null)
    {
        var method = AccessTools.Method(typeof(InfoPanel), nativeMethod)
            ?? throw new MissingMethodException(typeof(InfoPanel).FullName, nativeMethod);
        harmony.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(VeteranNativePanel), prefix),
            postfix: new HarmonyMethod(typeof(VeteranNativePanel), postfix));
    }

    private static void OnInitialized(InfoPanel __instance)
    {
        if (failed) return;
        try
        {
            if (view != null && view.Owner == __instance) return;
            view?.Dispose();
            view = new NativeView(__instance);
            nextRefresh = 0;
            if (VeteranRuntime.Settings.DebugLogging.Value) VeteranRuntime.Log.LogInfo(DebugDump());
        }
        catch (Exception error) { Disable(error); }
    }

    private static void OnNativeUpdate(InfoPanel __instance)
    {
        if (failed || view == null || view.Owner != __instance) return;
        try
        {
            // Native code may restore its grid cell size or hide the canvas between our 5 Hz ticks.
            view.NativeChanged();
            if (!GameState.SelectionCommonState.HasSelection) { view.Hide(); ClearSnapshot(); }
        }
        catch (Exception error) { Disable(error); }
    }

    private static void BeforeNativeUpdate(InfoPanel __instance)
    {
        if (failed || view == null || view.Owner != __instance) return;
        try { view.BeforeNativeUpdate(); }
        catch (Exception error) { Disable(error); }
    }

    private static void OnHidden(InfoPanel __instance)
    {
        if (view == null || view.Owner != __instance) return;
        try { view.Hide(); ClearSnapshot(); }
        catch (Exception error) { Disable(error); }
    }

    private static void OnDestroyed(InfoPanel __instance)
    {
        if (view == null || view.Owner != __instance) return;
        try { view.Dispose(); view = null; ClearSnapshot(); }
        catch (Exception error) { Disable(error); }
    }

    public static void Tick()
    {
        try
        {
            if (lastInputFrame != Time.frameCount)
            {
                lastInputFrame = Time.frameCount;
                if (VeteranRuntime.Ready && !IsEditingText() && VeteranRuntime.Settings.ToggleInterfaceShortcut.Value.IsDown())
                {
                    Hidden = !Hidden;
                    VeteranPromotionFeed.Clear();
                    nextRefresh = 0;
                }
            }
            if (!initialized || failed) return;
            if (!VeteranRuntime.Ready || Hidden)
            { view?.Hide(); ClearSnapshot(); return; }
            if (!VeteranRuntime.Settings.ShowPanel.Value && !VeteranRuntime.Settings.ShowVeteranInsignias.Value)
            { view?.Hide(); ClearSnapshot(); return; }
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .2f;
            if (view != null && view.Owner == null) { view = null; ClearSnapshot(); }
            if (view == null && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 3;
                var panel = UnityEngine.Object.FindObjectOfType<InfoPanel>();
                if (panel != null) OnInitialized(panel);
            }
            Refresh();
        }
        catch (Exception error) { Disable(error); }
    }

    internal static void RefreshLanguage()
    {
        nextRefresh = 0;
        var world = VeteranRuntime.ActiveWorld;
        if (!VeteranRuntime.Ready || world == null || !world.IsCreated) return;
        world.EntityManager.CompleteAllJobs();
        view?.NativeChanged();
        // Ask the native panel to resolve its selection name/hints again. The game's
        // own locale is untouched; mod-owned Hall strings are resolved dynamically.
        GameStateUpdateTasksUtility.UpdateGameState(world.EntityManager, GameStateUpdateTaskType.Selection);
    }

    private static void Refresh()
    {
        var world = VeteranRuntime.ActiveWorld;
        if (world == null || !world.IsCreated || view == null || !view.Owner.gameObject.activeInHierarchy ||
            !GameState.SelectedEntities.IsCreated || GameState.SelectedEntities.Length == 0)
        { view?.Hide(); ClearSnapshot(); return; }

        // Shared with world insignias: no second selection query or job barrier is needed there.
        world.EntityManager.CompleteAllJobs();
        if (VeteranHall.IsSelectedHall() && VeteranRuntime.Settings.ShowPanel.Value)
        { selected.Clear(); SelectionWorld = world; SelectionRevision++; view.ShowHall(); RefreshHudAreas(); return; }
        selected.Clear();
        Array.Clear(rankCounts, 0, rankCounts.Length);
        SelectionWorld = world;
        int totalSelection = GameState.SelectedEntities.Length;
        SkillsContainer? skills = VeteranRuntime.Settings.ShowPanel.Value ? ReadSkills(world) : null;
        double totalExperience = 0;
        VeteranSnapshot single = default;
        for (int i = 0; i < totalSelection; i++)
        {
            Entity entity = GameState.SelectedEntities[i];
            if (!VeteranRuntime.TryGetSnapshot(entity, out var snapshot)) continue;
            int level = Progression.Level(snapshot.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value);
            bool known = TryNativeDamage(entity, skills, out double baseline);
            double boosted = BoostDamage(baseline, entity, level);
            selected.Add(new VeteranSelectionEntry(entity, level, snapshot.Experience,
                snapshot.VanillaMaxHealth * snapshot.AppliedHealthFactor, baseline, boosted, known));
            rankCounts[level]++;
            totalExperience += snapshot.Experience;
            single = snapshot;
        }
        SelectionRevision++;
        if (selected.Count == 0 || !VeteranRuntime.Settings.ShowPanel.Value)
        { view.Hide(); RefreshHudAreas(); return; }
        bool individual = GameState.SelectionCommonState.SingleSelection && totalSelection == 1 && selected.Count == 1;
        view.Show(individual, selected, single, rankCounts, totalSelection, totalExperience);
        RefreshHudAreas();
    }

    private static void ClearSnapshot()
    {
        if (selected.Count == 0 && SelectionWorld == null) return;
        selected.Clear();
        SelectionWorld = null;
        SelectionRevision++;
    }

    internal static bool IsEditingText()
    {
        var focused = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
        return focused != null && (focused.GetComponent<TMP_InputField>() != null || focused.GetComponent<InputField>() != null);
    }

    private static double BoostDamage(double damage, Entity entity, int level) => Math.Min(damage * (1 + VeteranRuntime.DamageBonus(entity, level)),
        Math.Max(damage, VeteranRuntime.Settings.MaxDamage.Value));

    private static SkillsContainer? ReadSkills(World world)
    {
        try
        {
            if (!ReferenceEquals(skillsWorld, world))
            {
                if (skillsQueryCreated && skillsWorld != null && skillsWorld.IsCreated) skillsQuery.Dispose();
                skillsQueryCreated = false;
                skillsWorld = world;
                skillsQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SkillsContainerSingleton>());
                skillsQueryCreated = true;
            }
            if (skillsQuery.CalculateEntityCount() != 1) return null;
            return world.EntityManager.GetComponentObject<SkillsContainerSingleton>(skillsQuery.GetSingletonEntity()).Container;
        }
        catch { return null; } // A missing/changing container must leave the native statistic untouched.
    }

    private static bool TryNativeDamage(Entity entity, SkillsContainer? skills, out double damage)
    {
        damage = 0;
        try
        {
            var manager = VeteranRuntime.Manager;
            double basis = VeteranRuntime.BaseDamage(entity); // Includes ResearchUnitAffectData.
            damage = basis;
            bool berserk = manager.HasComponent<BerserkSkillData>(entity) && manager.GetComponentData<BerserkSkillData>(entity).IsInActiveState();
            bool lastStand = manager.HasComponent<LastStandSkillData>(entity);
            if (berserk || lastStand)
            {
                if (!skills.HasValue || !manager.HasComponent<ObjectId>(entity)) return false;
                int id = manager.GetComponentData<ObjectId>(entity).value;
                SkillsContainer container = skills.Value;
                if (berserk && container.HasInEnabledSkills(id, UnitSkillType.Berserk))
                {
                    container.GetSkillData(id, out BerserkSkill skill);
                    damage = basis * skill.damageMultiplier;
                }
                if (lastStand && container.HasInEnabledSkills(id, UnitSkillType.LastStand))
                {
                    container.GetSkillData(id, out LastStandSkill skill);
                    damage = basis * skill.damageMultiplier;
                }
            }
            // Match UpdateSelectedUnitsJob's HUD formula exactly. LastStand replaces Berserk there;
            // this is a displayed weapon statistic, not a prediction of target armor/critical/splash damage.
            if (manager.HasComponent<PinataComponent>(entity)) damage += manager.GetComponentData<PinataComponent>(entity).increaseValues.x;
            return damage >= 0 && Progression.Finite(damage);
        }
        catch { return false; }
    }

    private static void Disable(Exception error)
    {
        if (!failed) VeteranRuntime.Log.LogWarning("Native veterancy panel disabled; combat remains active: " + error);
        failed = true;
        ClearSnapshot();
        try { if (skillsQueryCreated && skillsWorld != null && skillsWorld.IsCreated) skillsQuery.Dispose(); }
        catch { /* Only an owned presentation query is being released. */ }
        skillsQueryCreated = false;
        skillsWorld = null;
        try { view?.Dispose(); } catch { /* Presentation cleanup must not disable combat. */ }
        view = null;
    }

    /// <summary>Read-only diagnostic. Emits hierarchy/rects for sandbox layout review.</summary>
    public static string DebugDump()
    {
        try
        {
            var ui = GameState.UIState;
            var focus = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            var output = new StringBuilder("Veteran UI state: InputAvailable=").Append(VeteranUiBridge.InputAvailable)
                .Append(" JournalIsOpen=").Append(VeteranArmyWindow.IsOpen)
                .Append(" JournalConfirming=").Append(VeteranArmyWindow.IsConfirming)
                .Append(" InputBlocked=").Append(VeteranUiBridge.InputBlocked)
                .Append(" GameplayOverlayAllowed=").Append(GameplayOverlayAllowed)
                .Append(" Hidden=").Append(Hidden).Append(" TimeScale=").Append(Time.timeScale)
                .Append(" Paused=").Append(Time.timeScale <= 0)
                .Append(" Focus=").Append(focus == null ? "<none>" : focus.name)
                .Append(" EditingText=").Append(IsEditingText())
                .Append(" NativeConfirmActive=").Append(UIViewManager.Exist && UIViewManager.ConfirmWindow != null && UIViewManager.ConfirmWindow.Active)
                .Append("\nNative UI flags: mainMenu=").Append(ui.mainMenuOpened)
                .Append(" winLose=").Append(ui.winLosePanelOpened).Append(" research=").Append(ui.researchWindowOpened)
                .Append(" market=").Append(ui.marketWindowOpened).Append(" mission=").Append(ui.missionPanelOpened)
                .Append(" statistics=").Append(ui.statisticPanelOpened).Append(" sandboxWave=").Append(ui.sandboxWavePanelOpened)
                .Append(" cutscene=").Append(ui.cutsceneIsPlaying).Append(" photo=").Append(ui.photoModActive)
                .Append(" confirm=").Append(ui.confirmOpened).Append(" formation=").Append(ui.formationOrderPanelOpened)
                .Append(" directControl=").Append(GameState.IsDirectControl)
                .Append(" sandboxEditing=").Append(ui.sandboxSideEditing).Append(" mapEditorEditing=").Append(ui.mapEditorAnyFieldEditing)
                .Append('\n').Append(VeteranArmyWindow.DebugLayout())
                .Append('\n').Append(view == null ? "Native veteran panel: not initialized." : view.Dump());
            return output.ToString();
        }
        catch (Exception error) { return "Native panel diagnostic: " + error.Message; }
    }

    private static void RefreshHudAreas()
    {
        if (Time.unscaledTime >= nextCanvasSearch)
        {
            nextCanvasSearch = Time.unscaledTime + 3;
            hudCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        }
        hudAreas.Clear();
        foreach (var canvas in hudCanvases)
        {
            if (canvas == null || !canvas.enabled || !canvas.gameObject.activeInHierarchy || canvas.renderMode == RenderMode.WorldSpace) continue;
            var rect = canvas.transform as RectTransform;
            if (rect == null) continue;
            rect.GetWorldCorners(hudCorners);
            Camera? camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, hudCorners[0]);
            Vector2 max = min;
            for (int i = 1; i < 4; i++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, hudCorners[i]);
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
            }
            // Full-screen canvases are containers, not occluding HUD surfaces. Native panel canvases
            // and subordinate minimap/action/resource canvases provide their actual screen rectangles.
            float width = max.x - min.x, height = max.y - min.y;
            if (width < 4 || height < 4 || width * height > Screen.width * Screen.height * .7f) continue;
            hudAreas.Add(new Rect(min.x, Screen.height - max.y, width, height));
        }
        view?.AddHudArea(hudAreas);
    }

    private sealed class NativeView : IDisposable
    {
        private static readonly Color Ivory = new Color(.94f, .89f, .76f, 1);
        private static readonly Color Muted = new Color(.76f, .71f, .62f, 1);
        private static readonly Color Bronze = new Color(.65f, .48f, .29f, 1);
        private static readonly Color Ink = new Color(.28f, .26f, .23f, .32f);
        private readonly Canvas common, different;
        private readonly GridLayoutGroup grid;
        private readonly List<ObjectInfoParameter> parameters;
        private readonly List<ObjectInfoStatus> statuses;
        private readonly List<SelectedObjectElement> portraits;
        private readonly TMP_Text template;
        private readonly TMP_Text? nativeDamage;
        private readonly List<Entity> decoratedSelection = new List<Entity>();
        private string savedDamage = "", savedHealth = "", decoratedDamage = "", decoratedHealth = "";
        private Color savedDamageColor, decoratedDamageColor;
        private bool damageDecorated, healthDecorated;
        private readonly RectTransform root, singleRoot, groupRoot;
        private readonly TMP_Text rank, xp, next, bonuses, total, armyLink;
        private readonly TMP_Text[] counts = new TMP_Text[5];
        private readonly TMP_Text[] names = new TMP_Text[5];
        private readonly Image badge, xpFill;
        private readonly Image[] cardBadges = new Image[5];
        private readonly Image[] cardBackgrounds = new Image[5];
        private readonly RectTransform bar;
        private readonly Vector3[] corners = new Vector3[4];
        private Vector2 savedGridCell, appliedGridCell;
        private bool gridAdjusted, layoutDirty = true, shown, individual;
        private int screenWidth, screenHeight;
        private float lastScale;
        public readonly InfoPanel Owner;

        public NativeView(InfoPanel owner)
        {
            Owner = owner;
            common = Field<Canvas>(owner, "_commonCanvas");
            different = Field<Canvas>(owner, "_multipleDifferentCanvas");
            grid = Field<GridLayoutGroup>(owner, "_multipleDifferentGridLayout");
            parameters = Field<List<ObjectInfoParameter>>(owner, "_parameterElements");
            statuses = Field<List<ObjectInfoStatus>>(owner, "_statusElements");
            portraits = Field<List<SelectedObjectElement>>(owner, "_selectedObjectsElements");
            template = Field<TMP_Text>(owner, "_healthText");
            nativeDamage = parameters.Count == 0 ? null : AccessTools.Field(typeof(ObjectInfoParameter), "_text")?.GetValue(parameters[0]) as TMP_Text;
            root = Node("VeteranSoldiersNative", common.transform);
            var interaction = root.gameObject.AddComponent<CanvasGroup>();
            interaction.interactable = true;
            interaction.blocksRaycasts = true;
            ImageAt("Parchment", root, Ink, 0, 0, 1, 1);
            ImageAt("UpperRule", root, Bronze, 0, 1, 1, 1, 0, -1, 0, 0);
            ImageAt("LowerRule", root, new Color(.65f, .48f, .29f, .45f), 0, 0, 1, 0, 0, 0, 0, 1);
            singleRoot = Node("Individual", root);
            Stretch(singleRoot);
            groupRoot = Node("SelectionRanks", root);
            Stretch(groupRoot);

            badge = ImageAt("RankInsignia", singleRoot, Color.white, 0, 0, .10f, 1, 7, 8, -3, -8);
            badge.preserveAspect = true;
            armyLink = TextAt("Eyebrow", singleRoot, VeteranText.Choose("SOLDIER RECORD ›", "FICHA DEL SOLDADO ›"),
                .11f, .64f, .35f, .94f, 18, Muted);
            MakeLink(armyLink, VeteranArmyWindow.OpenSelected);
            rank = TextAt("Rank", singleRoot, "", .11f, .23f, .35f, .66f, 32, Ivory);
            xp = TextAt("Experience", singleRoot, "", .37f, .57f, .71f, .93f, 24, Ivory);
            var track = ImageAt("ExperienceTrack", singleRoot, new Color(.05f, .05f, .045f, .8f), .37f, .43f, .71f, .49f);
            bar = track.rectTransform;
            xpFill = ImageAt("ExperienceFill", bar, Bronze, 0, 0, 0, 1);
            next = TextAt("NextRank", singleRoot, "", .37f, .025f, .73f, .40f, 18, Muted);
            ImageAt("BonusDivider", singleRoot, new Color(.65f, .48f, .29f, .4f), .745f, .17f, .745f, .83f, 0, 0, 1, 0);
            bonuses = TextAt("Bonuses", singleRoot, "", .77f, .08f, .985f, .90f, 20, Ivory);

            for (int i = 0; i < 5; i++)
            {
                float left = i / 5f, right = (i + 1) / 5f;
                var card = Node("Rank" + (i + 1), groupRoot);
                Anchors(card, left, .30f, right, .94f, 4, 0, -4, 0);
                cardBackgrounds[i] = ImageAt("Wash", card, new Color(.27f, .23f, .17f, .35f), 0, 0, 1, 1);
                cardBadges[i] = ImageAt("Insignia", card, Color.white, .07f, .35f, .43f, .96f);
                cardBadges[i].preserveAspect = true;
                names[i] = TextAt("Name", card, VeteranRuntime.Settings.Ranks[i].Name, .025f, .015f, .975f, .36f, 22, Muted);
                names[i].alignment = TextAlignmentOptions.Center;
                counts[i] = TextAt("Count", card, "0", .48f, .36f, .96f, .96f, 34, Ivory);
                counts[i].alignment = TextAlignmentOptions.Center;
                int rankFilter = i;
                cardBackgrounds[i].raycastTarget = true;
                var filterButton = card.gameObject.AddComponent<Button>();
                filterButton.targetGraphic = cardBackgrounds[i];
                filterButton.transition = UnityEngine.UI.Selectable.Transition.None;
                filterButton.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
                filterButton.onClick.AddListener(() =>
                {
                    try { VeteranArmy.SelectRank(rankFilter, true); nextRefresh = 0; }
                    catch (Exception error) { VeteranRuntime.Log.LogWarning("Selection rank filter skipped: " + error.Message); }
                });
            }
            total = TextAt("SelectionTotal", groupRoot, "", .01f, .015f, .99f, .28f, 18, Muted);
            total.alignment = TextAlignmentOptions.Center;
            MakeLink(total, VeteranArmyWindow.Toggle);
            root.gameObject.SetActive(false);
        }

        public void NativeChanged()
        {
            layoutDirty = true;
            if (shown && !Hidden && VeteranRuntime.Ready && VeteranRuntime.Settings.ShowPanel.Value && MatchesDecoratedSelection())
                DecorateNativeFigures(selected, GameState.SelectionCommonState.selectionType == SelectionType.MultipleDifferent);
            else { damageDecorated = false; healthDecorated = false; decoratedSelection.Clear(); }
        }

        public void BeforeNativeUpdate() => RestoreNativeFigures(false);

        public void ShowHall()
        {
            if (!common.enabled || !common.gameObject.activeInHierarchy) { Hide(); return; }
            RestoreNativeFigures(); RestoreGrid();
            if (root.parent != common.transform) root.SetParent(common.transform, false);
            individual = true; singleRoot.gameObject.SetActive(true); groupRoot.gameObject.SetActive(false);
            root.gameObject.SetActive(true); shown = true;
            Layout(false, VeteranRuntime.Settings.PanelScale.Value);
            armyLink.text = VeteranText.Choose("OPEN THE HALL ›", "ABRIR EL SALÓN ›");
            badge.sprite = VeteranVisualAssets.RankSprite(4); badge.enabled = true;
            rank.text = VeteranText.Choose("VETERANS", "VETERANOS"); rank.color = Bronze;
            xp.text = VeteranHall.ActiveDoctrine == HallDoctrine.None ? VeteranText.Choose("No active doctrine", "Sin doctrina activa") : VeteranHall.DoctrineName(VeteranHall.ActiveDoctrine);
            next.text = VeteranHall.PendingDoctrine != HallDoctrine.None ? VeteranText.Choose("Preparing: ", "Preparando: ") + VeteranHall.DoctrineName(VeteranHall.PendingDoctrine) :
                VeteranText.Choose("Legends · Memorial · Doctrines", "Leyendas · Memorial · Doctrinas");
            bonuses.text = VeteranHall.IsOperational ? VeteranText.Choose("HALL\nOPERATIONAL", "SALÓN\nOPERATIVO") : VeteranText.Choose("HALL\nINCOMPLETE", "SALÓN\nINCOMPLETO");
            bar.gameObject.SetActive(VeteranHall.PendingDoctrine != HallDoctrine.None);
            xpFill.color = Bronze; xpFill.rectTransform.anchorMax = new Vector2(VeteranHall.ResearchProgress01, 1);
            layoutDirty = true;
        }

        public void Show(bool isIndividual, IReadOnlyList<VeteranSelectionEntry> entries, VeteranSnapshot snapshot,
            int[] rankTotals, int selectedTotal, double totalExperience)
        {
            bool differentTypes = GameState.SelectionCommonState.selectionType == SelectionType.MultipleDifferent;
            Canvas canvas = differentTypes ? different : common;
            if (!canvas.enabled || !canvas.gameObject.activeInHierarchy) { Hide(); return; }
            if (root.parent != canvas.transform)
            { RestoreGrid(); root.SetParent(canvas.transform, false); layoutDirty = true; }
            if (individual != isIndividual) { individual = isIndividual; layoutDirty = true; }
            singleRoot.gameObject.SetActive(individual);
            groupRoot.gameObject.SetActive(!individual);
            root.gameObject.SetActive(true);
            shown = true;
            float scale = VeteranRuntime.Settings.PanelScale.Value;
            if (screenWidth != Screen.width || screenHeight != Screen.height || lastScale != scale) layoutDirty = true;
            if (layoutDirty)
            {
                Layout(differentTypes, scale);
                screenWidth = Screen.width; screenHeight = Screen.height; lastScale = scale; layoutDirty = false;
            }
            bool showInsignias = VeteranRuntime.Settings.ShowVeteranInsignias.Value;
            var ranks = VeteranRuntime.Settings.Ranks;
            if (individual)
            {
                var entry = entries[0];
                armyLink.text = VeteranText.Choose("SOLDIER RECORD ›", "FICHA DEL SOLDADO ›");
                int level = entry.Level;
                bool maximum = level + 1 >= VeteranRuntime.Settings.MaxLevel.Value;
                Color color = VeteranVisualAssets.RankColor(level);
                badge.sprite = VeteranVisualAssets.RankSprite(level);
                badge.enabled = showInsignias;
                rank.text = ranks[level].Name.ToUpperInvariant();
                rank.color = color;
                xp.text = VeteranText.Number(entry.Experience, "N1") + (maximum ? " XP · " + VeteranText.Maximum : " / " + VeteranText.Number(ranks[level + 1].Threshold, "N0") + " XP");
                next.text = maximum ? VeteranText.MaximumRank : VeteranText.NextRank(ranks[level + 1].Name, Math.Max(0, ranks[level + 1].Threshold - entry.Experience));
                if (snapshot.Specialization != Specialization.None)
                    next.text = VeteranText.SpecialtyName(snapshot.Specialization).ToUpperInvariant() + "\n" +
                        (Specializations.Active(snapshot.Specialization, level, VeteranRuntime.Settings.EnableSpecializations.Value) ? VeteranText.SpecialtyEffect(snapshot.Specialization) :
                            VeteranText.Choose("Specialization inactive", "Especialización inactiva"));
                bar.gameObject.SetActive(VeteranRuntime.Settings.ShowExperienceBar.Value);
                xpFill.color = color;
                xpFill.rectTransform.anchorMax = new Vector2((float)Progression.Fraction(entry.Experience, ranks, VeteranRuntime.Settings.MaxLevel.Value), 1);
                double baseline = GameState.SelectedUnitsState.sameSelectionBaseData.damage;
                double boosted = BoostDamage(baseline, entry.Entity, level);
                double damageBonus = baseline > 0 ? (boosted / baseline - 1) * 100 : 0;
                bonuses.text = VeteranText.Damage + " " + VeteranText.Number(boosted, "0.##") + " · +" + VeteranText.Number(damageBonus, "0.#") + "%\n" + VeteranText.Health + " " +
                    VeteranText.Number(snapshot.VanillaMaxHealth * snapshot.AppliedHealthFactor, "0.##") + " · +" + VeteranText.Number((snapshot.AppliedHealthFactor - 1) * 100, "0.#") + "%";
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    cardBadges[i].sprite = VeteranVisualAssets.RankSprite(i);
                    cardBadges[i].enabled = showInsignias;
                    counts[i].text = VeteranText.Number(rankTotals[i], "N0");
                    names[i].text = ranks[i].Name;
                    names[i].color = rankTotals[i] == 0 ? Muted : VeteranVisualAssets.RankColor(i);
                    cardBackgrounds[i].color = rankTotals[i] == 0 ? new Color(.13f, .12f, .10f, .2f) : new Color(.31f, .25f, .17f, .45f);
                }
                double minDamage = double.MaxValue, maxDamage = 0, minHealth = double.MaxValue, maxHealth = 0;
                bool damageKnown = true;
                foreach (var entry in entries)
                {
                    damageKnown &= entry.DamageKnown;
                    minDamage = Math.Min(minDamage, entry.Damage); maxDamage = Math.Max(maxDamage, entry.Damage);
                    minHealth = Math.Min(minHealth, entry.MaximumHealth); maxHealth = Math.Max(maxHealth, entry.MaximumHealth);
                }
                total.text = VeteranText.Soldiers(entries.Count) +
                    (entries.Count != selectedTotal ? " · " + VeteranText.Selected(selectedTotal) : "") +
                    " · " + VeteranText.Number(totalExperience, "N0") + " XP";
                if (differentTypes) total.text += "\n" + (damageKnown ? VeteranText.Damage + " " + Range(minDamage, maxDamage) + "   ·   " : "") + VeteranText.MaximumHealth + " " + Range(minHealth, maxHealth);
                else total.text += " · " + VeteranText.Choose("Army", "Ejército") + " [" + VeteranRuntime.Settings.ArmyShortcut.Value + "]";
            }
            DecorateNativeFigures(entries, differentTypes);
        }

        private void Layout(bool differentTypes, float scale)
        {
            var parent = root.parent as RectTransform ?? throw new InvalidOperationException("Native InfoPanel has no RectTransform.");
            Rect bounds = parent.rect;
            const float em = 1;
            float desiredHeight = (differentTypes ? 160 : 124) * scale;
            float inset = 8 * em;
            float left = bounds.xMin + inset, right = bounds.xMax - inset;
            float top;
            float bottomLimit = bounds.yMin + inset;
            if (!differentTypes && TryParameterBounds(parent, out Rect parameterBounds))
            {
                // The native card leaves a wide band below the stat row and above its status icons.
                left = Math.Max(left, parameterBounds.xMin);
                right = Math.Min(right, left + Math.Max(parameterBounds.width + 100 * em, 720 * em));
                top = parameterBounds.yMin - 7 * em;
                float statusRight = left;
                float aboveStatuses = bottomLimit;
                foreach (var status in statuses)
                {
                    if (status == null || !status.gameObject.activeInHierarchy) continue;
                    Rect statusBounds = Bounds(parent, (RectTransform)status.transform);
                    if (statusBounds.yMax < top && statusBounds.xMax > left && statusBounds.xMin < right)
                    {
                        aboveStatuses = Math.Max(aboveStatuses, statusBounds.yMax + 5 * em);
                        statusRight = Math.Max(statusRight, statusBounds.xMax + 12 * em);
                    }
                }
                // Use the empty space to the right of native status icons, leaving HP and abilities intact.
                // If many statuses occupy that row, use the remaining band above them without moving vanilla UI.
                if (right - statusRight >= 560 * em) left = statusRight;
                else bottomLimit = aboveStatuses;
            }
            else
            {
                // Heterogeneous selection keeps the native type portraits above a rank summary strip.
                top = bottomLimit + desiredHeight;
                ReserveGridSpace(parent, desiredHeight + inset * 2);
            }
            float available = top - bottomLimit;
            float height = Math.Min(desiredHeight, available);
            if (height < 38 * em || right - left < 250 * em)
                throw new InvalidOperationException("Native selection layout has no safe veterancy band. " + Dump());
            root.anchorMin = root.anchorMax = Vector2.zero;
            root.pivot = new Vector2(0, 1);
            root.anchoredPosition = new Vector2(left - bounds.xMin, top - bounds.yMin);
            root.sizeDelta = new Vector2(right - left, height);
            foreach (var label in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                label.enableAutoSizing = true;
                label.fontSizeMin = 12;
                label.fontSizeMax = (label.name == "Rank" ? 32 : label.name == "Count" ? 34 :
                    label.name == "Experience" ? 24 : label.name == "Name" ? (differentTypes ? 26 : 22) : label.name == "Bonuses" ? 20 : 18) * scale;
            }
        }

        private bool TryParameterBounds(RectTransform parent, out Rect result)
        {
            result = default;
            bool has = false;
            foreach (var parameter in parameters)
            {
                if (parameter == null || !parameter.gameObject.activeInHierarchy) continue;
                Union(ref result, ref has, Bounds(parent, (RectTransform)parameter.transform));
                foreach (var label in parameter.GetComponentsInChildren<TMP_Text>())
                    if (label.gameObject.activeInHierarchy) Union(ref result, ref has, Bounds(parent, label.rectTransform));
            }
            return has;
        }

        private void ReserveGridSpace(RectTransform parent, float reserved)
        {
            int active = 0;
            foreach (var portrait in portraits) if (portrait != null && portrait.gameObject.activeInHierarchy) active++;
            if (active == 0) return;
            if (!gridAdjusted || (grid.cellSize - appliedGridCell).sqrMagnitude > .01f) savedGridCell = grid.cellSize;
            var gridRect = (RectTransform)grid.transform;
            float width = gridRect.rect.width - grid.padding.horizontal;
            int columns = grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount ? Math.Max(1, grid.constraintCount) :
                Math.Max(1, Mathf.FloorToInt((width + grid.spacing.x) / (savedGridCell.x + grid.spacing.x)));
            int rows = Mathf.CeilToInt((float)active / columns);
            float available = Math.Min(parent.rect.height, gridRect.rect.height) - reserved - grid.padding.vertical;
            float cellHeight = Math.Max(16, (available - (rows - 1) * grid.spacing.y) / rows);
            float factor = Math.Min(1, cellHeight / savedGridCell.y);
            appliedGridCell = savedGridCell * factor;
            grid.cellSize = appliedGridCell;
            gridAdjusted = true;
            LayoutRebuilder.ForceRebuildLayoutImmediate(gridRect);
        }

        private void RestoreGrid()
        {
            if (!gridAdjusted || grid == null) return;
            if ((grid.cellSize - appliedGridCell).sqrMagnitude < .01f) grid.cellSize = savedGridCell;
            gridAdjusted = false;
        }

        public void Hide()
        {
            if (!shown) return;
            shown = false;
            RestoreNativeFigures();
            if (root != null) root.gameObject.SetActive(false);
            RestoreGrid();
        }

        public void Dispose()
        {
            Hide();
            RestoreGrid();
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
        }

        public string Dump()
        {
            var output = new StringBuilder("Native veterancy panel layout: screen=").Append(Screen.width).Append('x').Append(Screen.height);
            foreach (var rect in Owner.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.GetComponent<TMP_Text>() == null && rect.GetComponent<Canvas>() == null && rect.GetComponent<Button>() == null &&
                    rect.GetComponent<ObjectInfoParameter>() == null && rect.GetComponent<ObjectInfoStatus>() == null &&
                    rect.GetComponent<GridLayoutGroup>() == null && rect != root) continue;
                rect.GetWorldCorners(corners);
                output.Append("\n").Append(Path(rect)).Append(" active=").Append(rect.gameObject.activeInHierarchy)
                    .Append(" rect=").Append(rect.rect).Append(" anchor=").Append(rect.anchorMin).Append("..").Append(rect.anchorMax)
                    .Append(" world=").Append(corners[0]).Append("..").Append(corners[2]);
                var text = rect.GetComponent<TMP_Text>();
                if (text != null) output.Append(" font=").Append(text.fontSize).Append(" glyphs=").Append(text.textBounds).Append(" text=").Append(text.text.Replace('\n', '|'));
            }
            return output.ToString();
        }

        public void AddHudArea(List<Rect> areas)
        {
            if (!shown || root == null || !root.gameObject.activeInHierarchy) return;
            var canvas = root.GetComponentInParent<Canvas>();
            Camera? camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            root.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 max = min;
            for (int i = 1; i < 4; i++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
            }
            areas.Add(new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y));
        }

        private static string Range(double min, double max) => Math.Abs(max - min) < .01 ? VeteranText.Number(min, "0.##") : VeteranText.Number(min, "0.##") + "–" + VeteranText.Number(max, "0.##");

        private void DecorateNativeFigures(IReadOnlyList<VeteranSelectionEntry> entries, bool differentTypes)
        {
            RestoreNativeFigures();
            // Verified contract in this build's InfoPanel.UpdateParametersAndStatuses: for a units-only
            // Single/MultipleSame selection with positive damage, parameter 0 is always weapon damage.
            if (differentTypes || entries.Count != GameState.SelectedEntities.Length ||
                GameState.SelectionCommonState.buildingsSelectedCount != 0 || GameState.SelectionCommonState.otherSelectedCount != 0 ||
                GameState.SelectionCommonState.unitsSelectedCount == 0 || GameState.SelectedUnitsState.sameSelectionBaseData.damage <= 0) return;
            for (int i = 0; i < GameState.SelectedEntities.Length; i++) decoratedSelection.Add(GameState.SelectedEntities[i]);
            double minDamage = double.MaxValue, maxDamage = 0, minHealth = double.MaxValue, maxHealth = 0;
            bool damageKnown = true, hasDamageBonus = false;
            foreach (var entry in entries)
            {
                damageKnown &= entry.DamageKnown;
                hasDamageBonus |= entry.DamageKnown && entry.Damage > entry.NativeDamage;
                minDamage = Math.Min(minDamage, entry.Damage); maxDamage = Math.Max(maxDamage, entry.Damage);
                minHealth = Math.Min(minHealth, entry.MaximumHealth); maxHealth = Math.Max(maxHealth, entry.MaximumHealth);
            }
            if (individual)
            {
                // Preserve native research/active-skill display effects for the one actual selected entity.
                double nativeBase = GameState.SelectedUnitsState.sameSelectionBaseData.damage;
                minDamage = maxDamage = BoostDamage(nativeBase, entries[0].Entity, entries[0].Level);
                damageKnown = true;
                hasDamageBonus = maxDamage > nativeBase;
            }
            decoratedDamage = Range(minDamage, maxDamage);
            damageDecorated = nativeDamage != null && damageKnown;
            if (nativeDamage != null) { savedDamage = nativeDamage.text; savedDamageColor = nativeDamage.color; }
            decoratedDamageColor = hasDamageBonus ?
                GameState.Settings.globalSettings.data.uiSettings.textColor3 : savedDamageColor;
            healthDecorated = !individual;
            savedHealth = template.text;
            decoratedHealth = "-/" + Range(minHealth, maxHealth);
            ApplyNativeFigures();
        }

        private bool MatchesDecoratedSelection()
        {
            if (decoratedSelection.Count == 0 || !GameState.SelectedEntities.IsCreated || decoratedSelection.Count != GameState.SelectedEntities.Length) return false;
            for (int i = 0; i < decoratedSelection.Count; i++) if (decoratedSelection[i] != GameState.SelectedEntities[i]) return false;
            return true;
        }

        private void ApplyNativeFigures()
        {
            if (damageDecorated && nativeDamage != null)
            {
                nativeDamage.text = decoratedDamage; nativeDamage.color = decoratedDamageColor;
            }
            if (healthDecorated)
            {
                template.text = decoratedHealth;
            }
        }

        private void RestoreNativeFigures(bool clearSelection = true)
        {
            // Restore before the game's writer runs. Its new baseline is captured after it finishes,
            // even when the newly written string happens to equal our previous decorated value.
            if (damageDecorated && nativeDamage != null)
            { nativeDamage.text = savedDamage; nativeDamage.color = savedDamageColor; }
            if (healthDecorated) template.text = savedHealth;
            damageDecorated = false; healthDecorated = false;
            if (clearSelection) decoratedSelection.Clear();
        }

        private Rect Bounds(RectTransform parent, RectTransform child)
        {
            child.GetWorldCorners(corners);
            Vector3 min = parent.InverseTransformPoint(corners[0]);
            Vector3 max = min;
            for (int i = 1; i < 4; i++)
            {
                Vector3 point = parent.InverseTransformPoint(corners[i]);
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static void Union(ref Rect into, ref bool has, Rect value)
        {
            into = has ? Rect.MinMaxRect(Math.Min(into.xMin, value.xMin), Math.Min(into.yMin, value.yMin),
                Math.Max(into.xMax, value.xMax), Math.Max(into.yMax, value.yMax)) : value;
            has = true;
        }

        private static T Field<T>(InfoPanel panel, string name) where T : class =>
            AccessTools.Field(typeof(InfoPanel), name)?.GetValue(panel) as T ?? throw new MissingFieldException(typeof(InfoPanel).FullName, name);

        private static void MakeLink(TMP_Text label, Action action)
        {
            label.raycastTarget = true;
            var button = label.gameObject.AddComponent<Button>(); button.targetGraphic = label;
            button.transition = UnityEngine.UI.Selectable.Transition.None;
            button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            button.onClick.AddListener(() => { try { VeteranUiBridge.Click(); action(); } catch (Exception error) { VeteranRuntime.Log.LogWarning("Army link unavailable: " + error.Message); } });
        }

        private static RectTransform Node(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.layer = parent.gameObject.layer;
            var rect = (RectTransform)gameObject.transform;
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static void Stretch(RectTransform rect) => Anchors(rect, 0, 0, 1, 1);
        private static void Anchors(RectTransform rect, float x0, float y0, float x1, float y1,
            float insetLeft = 0, float insetBottom = 0, float insetRight = 0, float insetTop = 0)
        {
            rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = new Vector2(insetLeft, insetBottom); rect.offsetMax = new Vector2(insetRight, insetTop);
        }

        private static Image ImageAt(string name, Transform parent, Color color, float x0, float y0, float x1, float y1,
            float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            var rect = Node(name, parent);
            Anchors(rect, x0, y0, x1, y1, left, bottom, right, top);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color; image.raycastTarget = false;
            return image;
        }

        private TMP_Text TextAt(string name, Transform parent, string text, float x0, float y0, float x1, float y1, float size, Color color)
        {
            var rect = Node(name, parent);
            Anchors(rect, x0, y0, x1, y1);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = template.font;
            label.fontSharedMaterial = template.fontSharedMaterial;
            label.fontSize = size;
            label.text = text;
            label.color = color;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            return label;
        }

        private static string Path(Transform transform)
        {
            string result = transform.name;
            for (Transform parent = transform.parent; parent != null; parent = parent.parent) result = parent.name + "/" + result;
            return result;
        }
    }
}
