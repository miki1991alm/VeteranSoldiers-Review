using System;
using System.Collections.Generic;
using System.Text;
using Components;
using Systems.GameStateSystems;
using TMPro;
using UI;
using UI.HelpersStorage;
using UI.New;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

/// <summary>Native-canvas army journal. Bounded rows and one snapshot per second while visible.</summary>
internal sealed class VeteranArmyWindow : MonoBehaviour
{
    private const int PageSize = 8;
    private static VeteranArmyWindow? instance;
    private RectTransform? root, panel, hud, minimap;
    private RectTransform[] pages = Array.Empty<RectTransform>();
    private readonly List<VeteranRosterEntry> filtered = new List<VeteranRosterEntry>();
    private readonly List<UnitType> types = new List<UnitType>();
    private readonly List<Entity> selectedEntities = new List<Entity>();
    private readonly TMP_Text[] rankCounts = new TMP_Text[5], rowNames = new TMP_Text[PageSize], rowDetails = new TMP_Text[PageSize];
    private readonly Image[] rowBadges = new Image[PageSize];
    private readonly Image[] favoriteMarkers = new Image[PageSize];
    private readonly Button[] rowButtons = new Button[PageSize], specialtyButtons = new Button[3], doctrineButtons = new Button[3];
    private readonly TMP_Text[] specialtyLabels = new TMP_Text[3], doctrineStates = new TMP_Text[3], doctrineActions = new TMP_Text[3];
    private readonly HintableElement[] doctrineHints = new HintableElement[3];
    private readonly RectTransform[] doctrineProgress = new RectTransform[3];
    private readonly TMP_Text[] memorialNames = new TMP_Text[6], memorialDetails = new TMP_Text[6];
    private readonly TMP_Text[] tabLabels = new TMP_Text[4];
    private TMP_Text totals = null!, footer = null!, pageLabel = null!, empty = null!, rankFilterLabel = null!, typeFilterLabel = null!, specialtyFilterLabel = null!, favoriteFilterLabel = null!;
    private TMP_Text detailName = null!, detailRank = null!, detailStats = null!, detailHistory = null!, detailMedals = null!, favoriteLabel = null!, specialtyHint = null!;
    private TMP_Text hallStatus = null!, hallDetail = null!, memorialSummary = null!, memorialPage = null!, settingsSummary = null!, buildHallLabel = null!;
    private TMP_Text historyPageLabel = null!;
    private Button historyPrevious = null!, historyNext = null!;
    private HintableElement medalsHint = null!;
    private TMP_InputField nameInput = null!;
    private Image detailBadge = null!, xpFill = null!;
    private Button previous = null!, next = null!, selectFilter = null!, favorite = null!, locate = null!, saveName = null!, memorialPrevious = null!, memorialNext = null!, buildHall = null!;
    private bool opened, failed, confirming, onlyFavorites, nameDirty;
    private int currentTab, pageIndex, memorialIndex, historyPage, rankFilter = -1, typeFilter = -1, specialtyFilter = -1;
    private float nextRefresh, nextHud, statusUntil;
    private World? shownWorld;
    private Entity shownUnit;
    private readonly Vector3[] corners = new Vector3[4];
    private GameObject? previousFocus;
    private ConfirmWindow? ownedConfirmation;
    private GameObject[] detailControls = Array.Empty<GameObject>();
    private GameObject detailEmpty = null!;
    private static Color Gold => VeteranUiBridge.Gold;
    private static Color Ivory => VeteranUiBridge.Ivory;
    private static Color Muted => VeteranUiBridge.Muted;
    internal static bool IsOpen => instance != null && instance.opened;
    internal static bool IsConfirming => instance != null && instance.confirming;
    internal static bool PointerOverAccessButton => instance != null && instance.hud != null && instance.hud.gameObject.activeInHierarchy &&
        instance.Bounds(instance.hud).Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
    private bool CurrentWorld => shownWorld != null && shownWorld.IsCreated && ReferenceEquals(shownWorld, VeteranRuntime.ActiveWorld);
    internal static string DebugLayout()
    {
        if (instance == null) return "Journal layout: not built.";
        try
        {
            string access = "Veterans access: " + (instance.hud == null ? "not built" : instance.Bounds(instance.hud).ToString()) +
                " minimap=" + (instance.minimap == null ? "unavailable" : instance.Bounds(instance.minimap).ToString());
            if (instance.root == null || instance.panel == null) return access + "\nJournal layout: not built.";
            var result = new StringBuilder("Journal layout: screen=" + Screen.width + "x" + Screen.height + " rootRect=" + instance.root.rect +
                " rootPixels=" + instance.Bounds(instance.root) + " panelPixels=" + instance.Bounds(instance.panel) +
                " panelScale=" + instance.panel.localScale + " active=" + instance.root.gameObject.activeInHierarchy + "\n" + access);
            if (instance.root.gameObject.activeInHierarchy)
                foreach (var label in instance.root.GetComponentsInChildren<TMP_Text>())
                    if (label.isTextTruncated || !string.IsNullOrWhiteSpace(label.text) && label.textBounds.size.x <= 0)
                        result.Append("\nJournal text fit: ").Append(label.transform.parent.name).Append('/').Append(label.name)
                            .Append(" truncated=").Append(label.isTextTruncated).Append(" glyphBounds=").Append(label.textBounds.size)
                            .Append(" rect=").Append(label.rectTransform.rect.size).Append(" fontSize=").Append(label.fontSize);
            return result.ToString();
        }
        catch (Exception error) { return "Journal layout unavailable: " + error.Message; }
    }
    internal static void Toggle() { if (instance != null) instance.SetOpen(!instance.opened); }
    internal static void OpenRoster() { if (instance != null) { instance.SetOpen(true); if (instance.opened && instance.root != null) instance.Tab(0); } }
    internal static void OpenHall() { if (instance != null) { instance.SetOpen(true); if (instance.opened && instance.root != null) instance.Tab(1); } }
    internal static void OpenSoldier(Entity entity)
    {
        if (instance == null) return;
        instance.SetOpen(true);
        if (!instance.opened || instance.root == null) return;
        if (instance.shownUnit != entity) instance.InspectEntity(entity);
        instance.Tab(0); instance.Refresh();
    }
    internal static void OpenSelected()
    {
        if (VeteranHall.IsSelectedHall()) { OpenHall(); return; }
        if (GameState.SelectedEntities.IsCreated && GameState.SelectedEntities.Length == 1) OpenSoldier(GameState.SelectedEntities[0]);
        else OpenRoster();
    }
    internal static void Diagnose() { if (instance != null) instance.WriteDiagnostics(); }
    internal static bool Overlaps(Rect screenRect)
    {
        if (instance == null) return false;
        return (instance.opened && instance.panel != null && instance.Bounds(instance.panel).Overlaps(screenRect)) ||
            (instance.hud != null && instance.hud.gameObject.activeInHierarchy && instance.Bounds(instance.hud).Overlaps(screenRect));
    }
    private Rect Bounds(RectTransform rect)
    {
        rect.GetWorldCorners(corners);
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera? camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]), max = min;
        for (int i = 1; i < 4; i++) { var point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]); min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
        return new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
    }
    private void Awake() => instance = this;
    private void Update()
    {
        try
        {
            if (failed || !VeteranRuntime.Ready) { SetOpen(false); if (hud != null) hud.gameObject.SetActive(false); return; }
            bool gameplay = VeteranNativePanel.GameplayOverlayAllowed;
            if (Time.unscaledTime >= nextHud) { nextHud = Time.unscaledTime + 1; EnsureHud(); FitHud(); }
            if (hud != null) hud.gameObject.SetActive(gameplay && !opened);
            if (opened && (root == null || !ReferenceEquals(shownWorld, VeteranRuntime.ActiveWorld) || (!gameplay && !confirming))) SetOpen(false);
            if (confirming)
            {
                // The native interaction job is suspended, but the native dialog still owns
                // submit/cancel. Route these keys to its handlers, never to a world action.
                // ConfirmWindow guards duplicate submits; Hide clears callbacks immediately.
                var dialog = ownedConfirmation;
                if (dialog != null && dialog.Active && EventSystem.current != null)
                {
                    if (Input.GetKeyDown(KeyCode.Escape)) dialog.OnCancel(new BaseEventData(EventSystem.current));
                    else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) dialog.OnSubmit(new BaseEventData(EventSystem.current));
                }
                if (dialog == null || !dialog.Active)
                { confirming = false; ownedConfirmation = null; if (opened && root != null) { root.gameObject.SetActive(true); Refresh(); } }
                return;
            }
            if (opened && Input.GetKeyDown(KeyCode.Escape)) { SetOpen(false); return; }
            if (gameplay && !VeteranNativePanel.IsEditingText())
            {
                if (VeteranRuntime.Settings.ArmyShortcut.Value.IsDown()) SetOpen(!opened);
                if (VeteranRuntime.Settings.DiagnosticsShortcut.Value.IsDown()) WriteDiagnostics();
            }
            if (!opened) return;
            Fit();
            if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 1; Refresh(); }
        }
        catch (Exception error) { Disable(error); }
    }
    private void SetOpen(bool value)
    {
        if (value && (failed || !VeteranNativePanel.GameplayOverlayAllowed || !VeteranUiBridge.InputAvailable)) return;
        if (value && root == null) Build();
        bool changed = opened != value;
        opened = value;
        if (root != null) root.gameObject.SetActive(value);
        if (!value)
        {
            if (changed) VeteranUiBridge.Closed();
            if (confirming)
            {
                confirming = false;
                var dialog = ownedConfirmation; ownedConfirmation = null;
                if (dialog != null && dialog.Active && EventSystem.current != null) dialog.HideWithoutCallback();
            }
            if (root != null) VeteranUiBridge.HideHints(root);
            ReleaseFocus(true);
            previousFocus = null;
            return;
        }
        if (changed)
        {
            previousFocus = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            shownWorld = VeteranRuntime.ActiveWorld; shownUnit = Entity.Null; nameDirty = false; historyPage = 0;
            if (CurrentWorld) shownWorld!.EntityManager.CompleteAllJobs();
            if (CurrentWorld && GameState.SelectedEntities.IsCreated && GameState.SelectedEntities.Length == 1 && VeteranRuntime.IsEligible(GameState.SelectedEntities[0])) shownUnit = GameState.SelectedEntities[0];
            VeteranPromotionFeed.Clear(); pageIndex = memorialIndex = 0;
        }
        nextRefresh = 0; Fit(); Refresh();
    }
    private void Tab(int value)
    {
        ReleaseFocus(false);
        currentTab = value;
        for (int i = 0; i < pages.Length; i++) { pages[i].gameObject.SetActive(i == value); tabLabels[i].color = i == value ? Gold : Ivory; }
        nextRefresh = 0;
    }
    private void ReleaseFocus(bool restorePrevious)
    {
        if (nameInput != null) nameInput.DeactivateInputField();
        var events = EventSystem.current;
        if (events == null) return;
        var selected = events.currentSelectedGameObject;
        bool owned = selected != null && root != null && selected.transform.IsChildOf(root);
        // DeactivateInputField alone does not clear EventSystem selection. Native input
        // checks the selected object's component and would keep ignoring world shortcuts.
        if (owned) events.SetSelectedGameObject(null);
        if (restorePrevious && VeteranNativePanel.GameplayOverlayAllowed && previousFocus != null && previousFocus.activeInHierarchy &&
            (root == null || !previousFocus.transform.IsChildOf(root))) events.SetSelectedGameObject(previousFocus);
    }
    private void Status(string message, float seconds = 7)
    { if (footer != null) footer.text = message; statusUntil = Time.unscaledTime + seconds; }
    private void WriteDiagnostics()
    {
        bool success = CompatibilityDiagnostics.TryWriteReport(out string fileName, out string error);
        string message = success ? VeteranText.Choose("Report saved: ", "Informe guardado: ") + fileName : VeteranText.Choose("Could not save report: ", "No se pudo guardar: ") + error;
        VeteranRuntime.Log.LogInfo(message);
        SetOpen(true); if (opened && root != null) { Tab(3); Status(message, 20); }
    }
    private void Refresh()
    {
        if (!opened || panel == null) return;
        VeteranArmy.Refresh();
        var summary = VeteranArmy.Summary;
        totals.text = VeteranText.Soldiers(summary.Total) + "   ·   " + VeteranText.Number(summary.TotalExperience, "N0") + " XP   ·   " +
            VeteranPersistence.Status;
        for (int i = 0; i < 5; i++) rankCounts[i].text = VeteranRuntime.Settings.Ranks[i].Name + "  " + VeteranText.Number(summary.Counts[i], "N0");
        if (currentTab == 0) RefreshRoster();
        if (currentTab == 1) RefreshHall();
        if (currentTab == 2) RefreshMemorial();
        if (currentTab == 3) RefreshSettings();
        if (Time.unscaledTime > statusUntil) footer.text = VeteranText.Choose("Esc closes · Select and locate never issue movement orders · Save the game to preserve changes", "Esc cierra · Seleccionar y localizar no emiten órdenes de movimiento · Guarda la partida para conservar cambios");
    }
    private void RefreshRoster()
    {
        types.Clear();
        foreach (var entry in VeteranArmy.Entries) if (!types.Contains(entry.Type)) types.Add(entry.Type);
        types.Sort();
        filtered.Clear();
        foreach (var entry in VeteranArmy.Entries)
        {
            if (rankFilter >= 0 && entry.Level != rankFilter || typeFilter >= 0 && (int)entry.Type != typeFilter ||
                specialtyFilter >= 0 && (int)entry.Snapshot.Specialization != specialtyFilter) continue;
            if (onlyFavorites && (!VeteranChronicle.TryGet(entry.Entity, out var record) || !record.Favorite)) continue;
            filtered.Add(entry);
        }
        int maxPage = Math.Max(0, (filtered.Count - 1) / PageSize); pageIndex = Math.Min(pageIndex, maxPage);
        rankFilterLabel.text = rankFilter < 0 ? VeteranText.Choose("All ranks", "Todos los rangos") : VeteranRuntime.Settings.Ranks[rankFilter].Name;
        typeFilterLabel.text = typeFilter < 0 ? VeteranText.Choose("All types", "Todos los tipos") : TypeName((UnitType)typeFilter);
        specialtyFilterLabel.text = specialtyFilter < 0 ? VeteranText.Choose("All paths", "Todas las sendas") : VeteranText.SpecialtyName((Specialization)specialtyFilter);
        favoriteFilterLabel.text = onlyFavorites ? VeteranText.Choose("Favorites", "Favoritos") : VeteranText.Choose("All soldiers", "Todos");
        for (int i = 0; i < PageSize; i++)
        {
            int index = pageIndex * PageSize + i; bool exists = index < filtered.Count;
            rowButtons[i].gameObject.SetActive(exists); if (!exists) continue;
            var entry = filtered[index];
            bool has = VeteranChronicle.TryGet(entry.Entity, out var record);
            rowNames[i].text = has && !string.IsNullOrEmpty(record.Name) ? record.Name : entry.UnitName;
            favoriteMarkers[i].enabled = has && record.Favorite;
            rowNames[i].color = entry.Entity == shownUnit ? Gold : Ivory;
            rowDetails[i].text = entry.UnitName + "  ·  " + VeteranRuntime.Settings.Ranks[entry.Level].Name + "  ·  " + VeteranText.Number(entry.Snapshot.Experience, "N0") + " XP" +
                (entry.Snapshot.Specialization == Specialization.None ? "" : "  ·  " + VeteranText.SpecialtyName(entry.Snapshot.Specialization)) +
                (entry.Selectable ? "" : VeteranText.Choose("  ·  Garrisoned", "  ·  Guarnición"));
            rowBadges[i].sprite = VeteranVisualAssets.RankSprite(entry.Level);
        }
        empty.gameObject.SetActive(filtered.Count == 0);
        empty.text = VeteranText.Choose("No soldiers match these filters.\nRecruit troops or change the filters.", "Ningún soldado coincide con estos filtros.\nRecluta tropas o cambia los filtros.");
        previous.interactable = pageIndex > 0; next.interactable = pageIndex < maxPage;
        pageLabel.text = VeteranText.Soldiers(filtered.Count) + "  ·  " + (pageIndex + 1) + " / " + (maxPage + 1);
        selectFilter.interactable = filtered.Count > 0;
        RefreshDetail();
    }
    private string TypeName(UnitType type)
    { foreach (var entry in VeteranArmy.Entries) if (entry.Type == type) return entry.UnitName; return type.ToString(); }
    private void InspectRow(int row)
    {
        int index = pageIndex * PageSize + row; if (index >= filtered.Count) return;
        InspectEntity(filtered[index].Entity);
        RefreshDetail(); nextRefresh = 0;
    }
    private void InspectEntity(Entity entity)
    {
        ReleaseFocus(false);
        if (shownUnit == entity) return;
        shownUnit = entity; historyPage = 0;
        nameInput.SetTextWithoutNotify(""); nameDirty = false;
    }
    private void RefreshDetail()
    {
        if (CurrentWorld) shownWorld!.EntityManager.CompleteAllJobs();
        bool valid = shownUnit != Entity.Null && CurrentWorld && VeteranRuntime.TryGetSnapshot(shownUnit, out _) && !VeteranRuntime.Manager.HasComponent<Enemy>(shownUnit);
        if (!valid && shownUnit != Entity.Null)
        {
            ReleaseFocus(false);
            shownUnit = Entity.Null; nameDirty = false;
            nameInput.SetTextWithoutNotify("");
        }
        foreach (var control in detailControls) control.SetActive(valid);
        detailEmpty.SetActive(!valid);
        favorite.interactable = locate.interactable = saveName.interactable = nameInput.interactable = valid;
        if (!valid)
        {
            detailName.text = VeteranText.Choose("Soldier dossier", "Ficha del soldado");
            detailRank.text = VeteranText.Choose("Choose a soldier from the register", "Elige un soldado del registro");
            detailBadge.sprite = VeteranVisualAssets.RankSprite(0); xpFill.rectTransform.anchorMax = new Vector2(0, 1);
            return;
        }
        VeteranRuntime.TryGetSnapshot(shownUnit, out var snapshot);
        int level = Progression.Level(snapshot.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value);
        bool has = VeteranChronicle.TryGet(shownUnit, out var record);
        string name = has && !string.IsNullOrEmpty(record.Name) ? record.Name : VeteranArmy.UnitName(shownUnit);
        detailName.text = name; detailRank.text = VeteranRuntime.Settings.Ranks[level].Name.ToUpperInvariant() + "  ·  " + VeteranText.Number(snapshot.Experience, "N1") + " XP";
        detailBadge.sprite = VeteranVisualAssets.RankSprite(level);
        xpFill.color = VeteranVisualAssets.RankColor(level); xpFill.rectTransform.anchorMax = new Vector2((float)Progression.Fraction(snapshot.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value), 1);
        bool maximum = level + 1 >= VeteranRuntime.Settings.MaxLevel.Value;
        // Both values use the same ceiling rule. Reconstructing the maximum can differ
        // by float epsilon at a half-point; independent N0 rounding displayed 72 / 71.
        detailStats.text = VeteranText.Health + "  " + DisplayHealth(snapshot.CurrentHealth) + " / " + DisplayHealth(snapshot.VanillaMaxHealth * snapshot.AppliedHealthFactor) +
            "    " + VeteranText.Damage + " +" + VeteranText.Number(VeteranRuntime.DamageBonus(shownUnit, level) * 100, "0.#") + "%\n" +
            (maximum ? VeteranText.MaximumRank : VeteranText.Choose("Next rank in ", "Próximo rango en ") + VeteranText.Number(VeteranRuntime.Settings.Ranks[level + 1].Threshold - snapshot.Experience, "N1") + " XP");
        favoriteLabel.text = has && record.Favorite ? VeteranText.Choose("Favorite: ON", "Favorito: SÍ") : VeteranText.Choose("Mark favorite", "Marcar favorito");
        if (!nameInput.isFocused && !nameDirty) nameInput.SetTextWithoutNotify(has ? record.Name : "");
        detailMedals.text = has ? VeteranText.Medals(record.Medals) : VeteranText.Choose("No decorations yet", "Sin condecoraciones todavía");
        medalsHint.Init(new HintData { type = HintType.Mouse, Tittle = VeteranText.Choose("Decorations", "Condecoraciones"), Description = detailMedals.text });
        var history = new StringBuilder();
        int maxHistoryPage = has ? Math.Max(0, (record.History.Count - 1) / 3) : 0;
        historyPage = Math.Max(0, Math.Min(historyPage, maxHistoryPage));
        historyPrevious.interactable = historyPage > 0; historyNext.interactable = historyPage < maxHistoryPage;
        historyPageLabel.text = VeteranText.Choose("SERVICE", "SERVICIO") + "  " + (historyPage + 1) + "/" + (maxHistoryPage + 1);
        if (has)
        {
            history.Append(VeteranText.Choose("Combat contribution: ", "Contribución en combate: ")).Append(VeteranText.Number(record.EffectiveDamage, "N0")).Append(VeteranText.Choose(" effective damage", " de daño efectivo"));
            int end = record.History.Count - 1 - historyPage * 3, start = Math.Max(0, end - 2);
            for (int i = end; i >= start; i--) history.Append('\n').Append("• ").Append(VeteranText.ChronicleEvent(record.History[i]));
        }
        else history.Append(VeteranText.Choose("The chronicle begins with this soldier's next achievement.", "La crónica comienza con el próximo logro de este soldado."));
        detailHistory.text = history.ToString();
        bool canChoose = VeteranRuntime.Settings.EnableSpecializations.Value && level >= Specializations.UnlockLevel && snapshot.Specialization == Specialization.None;
        specialtyHint.text = snapshot.Specialization != Specialization.None ? VeteranText.SpecialtyName(snapshot.Specialization) + " · " + VeteranText.SpecialtyEffect(snapshot.Specialization) :
            !VeteranRuntime.Settings.EnableSpecializations.Value ? VeteranText.Choose("Specializations disabled in configuration", "Especializaciones desactivadas en configuración") :
            level < Specializations.UnlockLevel ? VeteranText.Choose("Unlocks at Veteran rank", "Se desbloquea en Veterano") : VeteranText.Choose("Choose one permanent path", "Elige una senda permanente");
        for (int i = 0; i < 3; i++) { specialtyButtons[i].interactable = canChoose; specialtyLabels[i].text = VeteranText.SpecialtyName((Specialization)(i + 1)); specialtyLabels[i].color = snapshot.Specialization == (Specialization)(i + 1) ? Gold : Ivory; }
    }
    private static string DisplayHealth(float value) => VeteranText.Number(value <= 0 ? 0 : Math.Max(1, Math.Ceiling(value - .0001)), "N0");
    private void Choose(Specialization choice)
    {
        Entity target = shownUnit; World? targetWorld = shownWorld;
        Confirm(VeteranText.Choose("Choose specialization", "Elegir especialización"), VeteranText.SpecialtyName(choice) + "\n" + VeteranText.SpecialtyEffect(choice) + "\n\n" + VeteranText.Choose("This choice is permanent for this soldier. Maximum-health bonuses do not heal.", "La elección es permanente para este soldado. Las bonificaciones de vida máxima no curan."), () =>
        {
            if (targetWorld != null && ReferenceEquals(targetWorld, VeteranRuntime.ActiveWorld) && VeteranRuntime.ChooseSpecialization(targetWorld, target, choice))
                Status(VeteranText.Choose("Specialization chosen. Save your game to preserve it.", "Especialización elegida. Guarda la partida para conservarla."));
            else Status(VeteranText.Choose("The soldier is no longer eligible.", "El soldado ya no cumple los requisitos."));
        });
    }
    private void Confirm(string title, string message, Action action)
    {
        var dialog = UIViewManager.Exist ? UIViewManager.ConfirmWindow : null;
        if (dialog == null || dialog.Active) { Status(VeteranText.Choose("Close the current confirmation first.", "Cierra primero la confirmación actual.")); return; }
        ownedConfirmation = dialog;
        ReleaseFocus(false);
        confirming = true; if (root != null) { VeteranUiBridge.HideHints(root); root.gameObject.SetActive(false); }
        dialog.ShowTwoButtons(title, message, () => { if (ReferenceEquals(shownWorld, VeteranRuntime.ActiveWorld)) action(); }, () => { });
    }
    private void RefreshHall()
    {
        hallStatus.text = VeteranText.Choose("HALL OF VETERANS", "SALÓN DE LOS VETERANOS");
        hallDetail.text = VeteranHall.Status + "\n" + VeteranText.Choose("One University or Hall per settlement. Native research is retained.", "Una Universidad o un Salón por asentamiento. Conserva la investigación nativa.") +
            "\n" + VeteranText.Choose("Campaign combat XP: ", "XP de combate de la campaña: ") + VeteranText.Number(VeteranHall.CombatExperience, "N0");
        bool dedicate = VeteranHall.CanDedicate(out _);
        bool universityPending = VeteranHall.OrdinaryUniversityCount > 0 && !dedicate;
        buildHall.interactable = VeteranHall.Available && (dedicate || VeteranHall.HallCount == 0 && !universityPending);
        buildHallLabel.text = dedicate ? VeteranText.Choose("Dedicate University · FREE", "Dedicar Universidad · GRATIS") :
            VeteranHall.HallCount > 0 ? VeteranHall.IsOperational ? VeteranText.Choose("Hall operational", "Salón operativo") : VeteranText.Choose("Hall under construction", "Salón en construcción") :
            universityPending ? VeteranHall.OrdinaryUniversityCount > 1 ? VeteranText.Choose("Check your Universities", "Comprueba tus Universidades") : VeteranText.Choose("Finish University first", "Termina primero la Universidad") : VeteranText.Choose("Build a hall", "Construir un salón");
        for (int i = 0; i < 3; i++)
        {
            var doctrine = (HallDoctrine)(i + 1);
            bool active = VeteranHall.ActiveDoctrine == doctrine, pending = VeteranHall.PendingDoctrine == doctrine;
            bool can = VeteranHall.CanStartDoctrine(doctrine, out string reason);
            string requirement = VeteranHall.DoctrineRequirement(doctrine);
            string state = active ? VeteranText.Choose("ACTIVE", "ACTIVA") + "\n" + (VeteranHall.IsOperational ? VeteranText.Choose("The hall is operational", "El salón está operativo") : VeteranText.Choose("Suspended: rebuild the hall", "Suspendida: reconstruye el salón")) :
                pending ? VeteranText.Number(VeteranHall.ResearchProgress01 * 100, "0") + "% · " + VeteranHall.PreparationStatus :
                requirement + "\n" + (can ? VeteranText.Choose("Ready to adopt", "Lista para adoptar") : VeteranHall.DoctrineBlockLabel(doctrine));
            doctrineStates[i].text = state + "\n" + VeteranHall.DoctrineCostText(doctrine);
            doctrineStates[i].color = can || active && VeteranHall.IsOperational ? Ivory : Muted;
            float progress = pending ? VeteranHall.ResearchProgress01 : (float)Math.Min(1, VeteranHall.CombatExperience / HallRules.ExperienceThreshold(doctrine));
            doctrineProgress[i].sizeDelta = new Vector2(332 * Mathf.Clamp01(progress), 4);
            // The blocking state is visible on the card; its full explanation remains available by click and hint.
            string extraReason = !can && !active && !pending && !string.Equals(reason, requirement, StringComparison.Ordinal) ? reason : "";
            doctrineHints[i].Init(new HintData { type = HintType.Mouse, Tittle = VeteranHall.DoctrineName(doctrine),
                Description = doctrineStates[i].text + (string.IsNullOrEmpty(extraReason) ? "" : "\n\n" + extraReason) });
            doctrineButtons[i].interactable = !active && !pending;
            doctrineActions[i].text = active ? VeteranText.Choose("Active", "Activa") : pending ? VeteranText.Choose("Preparing…", "Preparando…") :
                can ? VeteranText.Choose("Adopt doctrine", "Adoptar doctrina") : VeteranText.Choose("View requirements", "Ver requisitos");
            doctrineActions[i].color = can ? Ivory : Gold;
        }
    }
    private void Adopt(HallDoctrine doctrine)
    {
        if (!VeteranHall.CanStartDoctrine(doctrine, out string reason))
        {
            Status(reason, 12);
            if (VeteranHall.DoctrineBlock(doctrine) == DoctrineBlocker.Resources)
                UIViewManager.ShowPriceWarnings(VeteranHall.DoctrinePrice(doctrine));
            return;
        }
        Confirm(VeteranHall.DoctrineName(doctrine), VeteranHall.DoctrineDescription(doctrine) + "\n\n" + VeteranHall.DoctrineCostText(doctrine) + "\n" + VeteranText.Choose("Only one doctrine can be active. Adoption uses the hall's own preparation queue.", "Solo puede haber una doctrina activa. El salón tiene su propia cola de preparación."), () =>
        { bool result = VeteranHall.TryStartDoctrine(doctrine, out string startReason); Status(result ? VeteranText.Choose("Doctrine preparation started.", "Preparación de doctrina iniciada.") : startReason); });
    }
    private void RefreshMemorial()
    {
        var fallen = VeteranChronicle.Memorial;
        int maxPage = Math.Max(0, (fallen.Count - 1) / 6); memorialIndex = Math.Min(memorialIndex, maxPage);
        memorialSummary.text = fallen.Count == 0 ? VeteranText.Choose("No recorded fallen soldiers. Their names will be remembered here.", "No hay soldados caídos registrados. Sus nombres se recordarán aquí.") :
            VeteranText.Number(fallen.Count, "N0") + VeteranText.Choose(" names remembered in this campaign", " nombres recordados en esta partida");
        for (int i = 0; i < 6; i++)
        {
            int index = fallen.Count - 1 - (memorialIndex * 6 + i); bool exists = index >= 0;
            memorialNames[i].transform.parent.gameObject.SetActive(exists); if (!exists) continue;
            var record = fallen[index];
            memorialNames[i].text = string.IsNullOrEmpty(record.Name) ? VeteranText.Choose("Unnamed soldier", "Soldado sin nombre") : record.Name;
            memorialDetails[i].text = VeteranRuntime.Settings.Ranks[Math.Min(4, Math.Max(0, record.HighestLevel))].Name + "   ·   " + VeteranText.Medals(record.Medals) + "\n" +
                VeteranText.Number(record.EffectiveDamage, "N0") + VeteranText.Choose(" effective combat damage", " de daño efectivo en combate");
        }
        memorialPrevious.interactable = memorialIndex > 0; memorialNext.interactable = memorialIndex < maxPage;
        memorialPage.text = (memorialIndex + 1) + " / " + (maxPage + 1);
    }
    private void RefreshSettings()
    {
        settingsSummary.text = "Veteran Soldiers " + VeteranRuntime.Version + "  ·  Kirapp\n" +
            VeteranText.Choose("Presentation options apply immediately. Combat balance remains in the configuration file.\nThe diagnostic report lists patch owners; it does not certify other mods as compatible.", "Las opciones visuales se aplican al instante. El equilibrio de combate permanece en el archivo de configuración.\nEl informe identifica los propietarios de parches; no certifica la compatibilidad con otros mods.");
        foreach (var control in settingControls) control.Refresh();
    }
    private readonly List<SettingControl> settingControls = new List<SettingControl>();
    private sealed class SettingControl
    {
        internal readonly TMP_Text Text; internal readonly string Label; internal readonly Func<bool> Read;
        internal SettingControl(TMP_Text text, string label, Func<bool> read) { Text = text; Label = label; Read = read; }
        internal void Refresh() => Text.text = Label + "   " + (Read() ? VeteranText.Choose("ON", "SÍ") : VeteranText.Choose("OFF", "NO"));
    }
    private void Build()
    {
        settingControls.Clear();
        VeteranUiBridge.CaptureStyle();
        if (VeteranUiBridge.Font == null || !UIViewManager.Exist) throw new InvalidOperationException("Native interface is not ready.");
        root = NativeRoot("VeteranSoldiersJournal");
        VeteranUiBridge.Fill(root, new Color(0, 0, 0, .42f), true);
        panel = VeteranUiBridge.Rect("Journal", root, 0, 0, 1200, 820);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, .5f); panel.anchoredPosition = Vector2.zero;
        VeteranUiBridge.Fill(panel, VeteranUiBridge.Panel, true);
        VeteranUiBridge.Frame(panel);
        Rule(panel, 0, 0, 1200, 3, Gold); Rule(panel, 0, 817, 1200, 3, Gold);
        Rule(panel, 0, 0, 2, 820, Gold); Rule(panel, 1198, 0, 2, 820, Gold);
        // The native font's ascender/descender metrics need more height than its nominal
        // point size. Too-short boxes make TMP ellipsize the entire first line away.
        VeteranUiBridge.Heading(Label(panel, "Title", VeteranText.Choose("VETERANS", "VETERANOS"), 26, 7, 900, 50, 35, Ivory));
        Label(panel, "Subtitle", VeteranText.Choose("THE ARMY'S CHRONICLE", "LA CRÓNICA DEL EJÉRCITO"), 28, 56, 900, 24, 16, Gold);
        Btn(panel, "Close", "×", 1136, 18, 42, 42, () => SetOpen(false), out _, VeteranText.Choose("Close the journal · Escape", "Cerrar el registro · Escape"));
        string[] names = { VeteranText.Choose("Army register", "Registro del ejército"), VeteranText.Choose("Hall & doctrines", "Salón y doctrinas"), VeteranText.Choose("Memorial", "Memorial"), VeteranText.Choose("Settings", "Ajustes") };
        for (int i = 0; i < 4; i++) { int value = i; Btn(panel, "Tab" + i, names[i], 24 + i * 288, 91, 276, 40, () => { Tab(value); Refresh(); }, out tabLabels[i]); }
        pages = new RectTransform[4];
        for (int i = 0; i < 4; i++) pages[i] = VeteranUiBridge.Rect("Page" + i, panel, 24, 147, 1152, 575);
        BuildRoster(pages[0]); BuildHall(pages[1]); BuildMemorial(pages[2]); BuildSettings(pages[3]);
        Rule(panel, 24, 739, 1152, 1, new Color(.53f, .45f, .33f));
        totals = Label(panel, "Totals", "", 28, 750, 1140, 24, 17, Ivory);
        footer = Label(panel, "Footer", "", 28, 780, 1140, 24, 15, Muted);
        Tab(currentTab); root.gameObject.SetActive(false);
    }
    private void BuildRoster(RectTransform page)
    {
        Btn(page, "RankFilter", "", 0, 0, 168, 36, () => { rankFilter = rankFilter >= 4 ? -1 : rankFilter + 1; pageIndex = 0; Refresh(); }, out rankFilterLabel, VeteranText.Choose("Click to cycle ranks. The rank counters below are direct filters.", "Pulsa para recorrer los rangos. Los contadores inferiores son filtros directos."));
        Btn(page, "TypeFilter", "", 177, 0, 168, 36, () => { int index = types.IndexOf((UnitType)typeFilter) + 1; typeFilter = index >= types.Count ? -1 : (int)types[index]; pageIndex = 0; Refresh(); }, out typeFilterLabel, VeteranText.Choose("Click to cycle the unit types in your army.", "Pulsa para recorrer los tipos de unidad de tu ejército."));
        Btn(page, "PathFilter", "", 354, 0, 168, 36, () => { specialtyFilter = specialtyFilter >= 3 ? -1 : specialtyFilter + 1; pageIndex = 0; Refresh(); }, out specialtyFilterLabel, VeteranText.Choose("Click to cycle specializations, including unassigned soldiers.", "Pulsa para recorrer las especializaciones, incluidos los soldados sin senda."));
        Btn(page, "FavoriteFilter", "", 531, 0, 168, 36, () => { onlyFavorites = !onlyFavorites; pageIndex = 0; Refresh(); }, out favoriteFilterLabel, VeteranText.Choose("Show only soldiers you have marked as favorites.", "Muestra solo los soldados que has marcado como favoritos."));
        for (int i = 0; i < 5; i++)
        {
            int rank = i;
            Btn(page, "RankSummary" + i, "", i * 140, 44, 135, 29, () => { rankFilter = rankFilter == rank ? -1 : rank; pageIndex = 0; Refresh(); }, out rankCounts[i]);
            rankCounts[i].fontSize = 15; rankCounts[i].color = VeteranVisualAssets.RankColor(i);
        }
        for (int i = 0; i < PageSize; i++)
        {
            int row = i; rowButtons[i] = Btn(page, "SoldierRow" + i, "", 0, 84 + i * 52, 699, 48, () => InspectRow(row), out _);
            var rect = (RectTransform)rowButtons[i].transform;
            rowBadges[i] = VeteranUiBridge.Fill(VeteranUiBridge.Rect("Badge", rect, 9, 4, 35, 39), Color.white); rowBadges[i].preserveAspect = true;
            favoriteMarkers[i] = VeteranUiBridge.Fill(VeteranUiBridge.Rect("FavoriteMark", rect, 666, 10, 6, 26), Gold); favoriteMarkers[i].enabled = false;
            rowNames[i] = Label(rect, "Name", "", 57, 0, 600, 28, 20, Ivory);
            rowDetails[i] = Label(rect, "Detail", "", 57, 25, 600, 21, 14, Muted);
        }
        empty = Label(page, "Empty", "", 24, 160, 650, 140, 24, Muted, true); empty.alignment = TextAlignmentOptions.Center;
        previous = Btn(page, "Previous", "‹", 0, 513, 44, 36, () => { pageIndex--; Refresh(); }, out _);
        next = Btn(page, "Next", "›", 50, 513, 44, 36, () => { pageIndex++; Refresh(); }, out _);
        pageLabel = Label(page, "Pagination", "", 106, 513, 331, 36, 16, Muted);
        selectFilter = Btn(page, "SelectFiltered", VeteranText.Choose("Select filtered", "Seleccionar filtrados"), 474, 513, 225, 36, () =>
        { if (!CurrentWorld) return; selectedEntities.Clear(); foreach (var entry in filtered) selectedEntities.Add(entry.Entity); Status(VeteranText.Selected(VeteranArmy.SelectEntities(selectedEntities))); }, out _);
        var detail = VeteranUiBridge.Rect("Dossier", page, 723, 0, 429, 575);
        VeteranUiBridge.Fill(detail, VeteranUiBridge.Recess); Rule(detail, 0, 0, 429, 2, Gold);
        detailBadge = VeteranUiBridge.Fill(VeteranUiBridge.Rect("RankInsignia", detail, 14, 16, 58, 64), Color.white); detailBadge.preserveAspect = true;
        detailName = Label(detail, "SoldierName", "", 86, 13, 329, 32, 25, Ivory);
        detailRank = Label(detail, "Rank", "", 86, 47, 329, 29, 17, Gold);
        var track = VeteranUiBridge.Rect("ExperienceTrack", detail, 16, 91, 397, 5); VeteranUiBridge.Fill(track, new Color(.08f, .075f, .065f));
        var fill = VeteranUiBridge.Rect("ExperienceFill", track, 0, 0, 0, 0); fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.offsetMin = fill.offsetMax = Vector2.zero; xpFill = VeteranUiBridge.Fill(fill, Gold);
        detailStats = Label(detail, "Stats", "", 16, 105, 397, 53, 17, Ivory, true);
        favorite = Btn(detail, "Favorite", "", 16, 170, 190, 33, () => { if (CurrentWorld && shownUnit != Entity.Null) { VeteranChronicle.ToggleFavorite(shownUnit); Refresh(); } }, out favoriteLabel);
        locate = Btn(detail, "Locate", VeteranText.Choose("Select & locate", "Seleccionar y localizar"), 216, 170, 197, 33, () =>
        { bool located = VeteranArmy.Locate(shownWorld, shownUnit); VeteranArmy.SelectOne(shownWorld, shownUnit); if (located) SetOpen(false); }, out _);
        var inputRect = VeteranUiBridge.Rect("Nickname", detail, 16, 215, 296, 34); VeteranUiBridge.Fill(inputRect, new Color(.08f, .075f, .065f), true);
        nameInput = inputRect.gameObject.AddComponent<TMP_InputField>(); nameInput.lineType = TMP_InputField.LineType.SingleLine; nameInput.characterLimit = ChronicleRules.MaximumNameLength;
        var viewport = VeteranUiBridge.Rect("Viewport", inputRect, 7, 1, 282, 32); viewport.gameObject.AddComponent<RectMask2D>(); nameInput.textViewport = viewport;
        var inputText = Label(viewport, "Text", "", 0, 0, 282, 32, 18, Ivory);
        var placeholder = Label(viewport, "Placeholder", VeteranText.Choose("Give this soldier a name", "Pon nombre al soldado"), 0, 0, 282, 32, 16, Muted);
        nameInput.textComponent = inputText; nameInput.placeholder = placeholder; nameInput.richText = false; nameInput.selectionColor = new Color(.75f, .57f, .28f, .5f);
        nameInput.onValueChanged.AddListener(_ => nameDirty = true);
        saveName = Btn(detail, "SaveName", VeteranText.Choose("Name", "Nombrar"), 321, 215, 92, 34, () =>
        { if (CurrentWorld && shownUnit != Entity.Null && VeteranChronicle.SetName(shownUnit, nameInput.text)) { nameDirty = false; ReleaseFocus(false); Status(VeteranText.Choose("Name updated. Save the game to preserve it.", "Nombre actualizado. Guarda la partida para conservarlo.")); Refresh(); } }, out _);
        detailMedals = Label(detail, "Honors", "", 16, 262, 397, 43, 17, Gold, true);
        detailMedals.raycastTarget = true; medalsHint = VeteranUiBridge.Hint(detailMedals.gameObject, VeteranText.Choose("Decorations", "Condecoraciones"), "");
        historyPageLabel = Label(detail, "HistoryPage", "", 16, 306, 298, 24, 16, Gold);
        historyPrevious = Btn(detail, "PreviousHistory", "‹", 341, 306, 31, 24, () => { historyPage--; RefreshDetail(); }, out _);
        historyNext = Btn(detail, "NextHistory", "›", 382, 306, 31, 24, () => { historyPage++; RefreshDetail(); }, out _);
        detailHistory = Label(detail, "ServiceRecord", "", 16, 339, 397, 104, 16, Muted, true); detailHistory.alignment = TextAlignmentOptions.TopLeft;
        var specialtyDivider = VeteranUiBridge.Rect("SpecializationDivider", detail, 16, 452, 397, 1);
        VeteranUiBridge.Fill(specialtyDivider, new Color(.40f, .35f, .27f));
        specialtyHint = Label(detail, "SpecializationHint", "", 16, 464, 397, 42, 16, Ivory, true);
        for (int i = 0; i < 3; i++)
        {
            var choice = (Specialization)(i + 1);
            specialtyButtons[i] = Btn(detail, "Specialize" + i, "", 16 + i * 134, 517, 129, 36, () => Choose(choice), out specialtyLabels[i], VeteranText.SpecialtyEffect(choice)); specialtyLabels[i].fontSize = 16;
        }
        detailControls = new[] { track.gameObject, detailStats.gameObject, favorite.gameObject, locate.gameObject, nameInput.gameObject,
            saveName.gameObject, detailMedals.gameObject, historyPageLabel.gameObject, historyPrevious.gameObject, historyNext.gameObject,
            detailHistory.gameObject, specialtyDivider.gameObject, specialtyHint.gameObject, specialtyButtons[0].gameObject,
            specialtyButtons[1].gameObject, specialtyButtons[2].gameObject };
        var emptyDetail = VeteranUiBridge.Rect("ChooseSoldier", detail, 16, 105, 397, 450);
        detailEmpty = emptyDetail.gameObject;
        var introduction = Label(emptyDetail, "Introduction", VeteranText.Choose(
            "Select a soldier on the left to open its service record.", "Selecciona un soldado de la izquierda para abrir su ficha de servicio."), 16, 22, 365, 112, 23, Ivory, true);
        introduction.alignment = TextAlignmentOptions.Center;
        Rule(emptyDetail, 66, 160, 265, 1, new Color(.51f, .43f, .31f));
        for (int rank = 0; rank < 5; rank++)
        {
            var seal = VeteranUiBridge.Fill(VeteranUiBridge.Rect("Rank" + rank, emptyDetail, 36 + rank * 67, 192, 56, 62), Color.white);
            seal.sprite = VeteranVisualAssets.RankSprite(rank); seal.preserveAspect = true;
        }
        var invitation = Label(emptyDetail, "Invitation", VeteranText.Choose(
            "Give them a name. Follow every promotion. Choose their path and remember their achievements.",
            "Dales un nombre. Sigue cada ascenso. Elige su senda y recuerda sus hazañas."), 16, 288, 365, 131, 20, Muted, true);
        invitation.alignment = TextAlignmentOptions.Center;
    }
    private void BuildHall(RectTransform page)
    {
        hallStatus = Label(page, "HallTitle", "", 0, 0, 850, 38, 28, Gold);
        hallDetail = Label(page, "HallState", "", 0, 45, 800, 104, 19, Ivory, true);
        buildHall = Btn(page, "BuildHall", VeteranText.Choose("Build a hall", "Construir un salón"), 880, 4, 272, 42, BuildOrDedicate, out buildHallLabel);
        buildHallLabel.fontSize = 17;
        Btn(page, "Legends", VeteranText.Choose("Living legends", "Leyendas vivas"), 880, 56, 272, 36, () => { rankFilter = 4; typeFilter = specialtyFilter = -1; onlyFavorites = false; pageIndex = 0; Tab(0); Refresh(); }, out _);
        Btn(page, "Memorial", VeteranText.Choose("Visit the memorial", "Visitar el memorial"), 880, 102, 272, 36, () => { Tab(2); Refresh(); }, out _);
        for (int i = 0; i < 3; i++)
        {
            var doctrine = (HallDoctrine)(i + 1);
            var card = VeteranUiBridge.Rect("Doctrine" + i, page, i * 390, 174, 372, 365);
            VeteranUiBridge.Fill(card, VeteranUiBridge.Recess); Rule(card, 0, 0, 372, 2, Gold);
            var icon = VeteranUiBridge.Fill(VeteranUiBridge.Rect("Seal", card, 20, 23, 54, 59), Color.white); icon.sprite = VeteranVisualAssets.RankSprite(i + 2); icon.preserveAspect = true;
            Label(card, "Title", VeteranHall.DoctrineName(doctrine), 89, 24, 263, 56, 23, Ivory, true);
            Label(card, "Description", VeteranHall.DoctrineDescription(doctrine), 20, 103, 332, 102, 19, Ivory, true);
            Rule(card, 20, 203, 332, 4, VeteranUiBridge.Panel);
            doctrineProgress[i] = VeteranUiBridge.Rect("UnlockProgress", card, 20, 203, 0, 4);
            VeteranUiBridge.Fill(doctrineProgress[i], Gold);
            doctrineStates[i] = Label(card, "Requirements", "", 20, 217, 332, 86, 16, Muted, true);
            doctrineButtons[i] = Btn(card, "Adopt", "", 20, 311, 332, 36, () => Adopt(doctrine), out doctrineActions[i]);
            doctrineHints[i] = VeteranUiBridge.Hint(doctrineButtons[i].gameObject, VeteranHall.DoctrineName(doctrine), "");
        }
        Label(page, "HallPrinciple", VeteranText.Choose("Experience and specialization remain available without a hall. Doctrine benefits require a completed hall.", "La experiencia y las especializaciones siguen disponibles sin salón. Las doctrinas requieren un salón terminado."), 0, 546, 1152, 28, 16, Muted);
    }
    private void BuildMemorial(RectTransform page)
    {
        Label(page, "Heading", VeteranText.Choose("THE FALLEN", "LOS CAÍDOS"), 0, 0, 1152, 41, 30, Gold);
        memorialSummary = Label(page, "Summary", "", 0, 45, 1152, 39, 20, Ivory);
        for (int i = 0; i < 6; i++)
        {
            var card = VeteranUiBridge.Rect("Fallen" + i, page, (i % 2) * 586, 101 + (i / 2) * 128, 566, 112);
            VeteranUiBridge.Fill(card, VeteranUiBridge.Recess); Rule(card, 0, 0, 2, 112, Gold);
            memorialNames[i] = Label(card, "Name", "", 20, 10, 526, 32, 24, Ivory);
            memorialDetails[i] = Label(card, "Service", "", 20, 48, 526, 53, 17, Muted, true);
        }
        memorialPrevious = Btn(page, "Previous", "‹", 0, 505, 48, 38, () => { memorialIndex--; Refresh(); }, out _);
        memorialNext = Btn(page, "Next", "›", 58, 505, 48, 38, () => { memorialIndex++; Refresh(); }, out _);
        memorialPage = Label(page, "Page", "", 123, 505, 120, 38, 18, Muted);
        Label(page, "Verified", VeteranText.Choose("Only confirmed deaths enter the memorial. Records begin when the chronicle is installed.", "Solo las muertes confirmadas entran en el memorial. El registro comienza al instalar la crónica."), 0, 551, 1152, 24, 16, Muted);
    }
    private void BuildSettings(RectTransform page)
    {
        Label(page, "Heading", VeteranText.Choose("YOUR VETERAN SOLDIERS", "TUS VETERAN SOLDIERS"), 0, 0, 1152, 39, 28, Gold);
        settingsSummary = Label(page, "SettingsInfo", "", 0, 51, 1152, 102, 19, Ivory, true);
        var settings = VeteranRuntime.Settings;
        Setting(page, VeteranText.Choose("Selection panel", "Panel de selección"), 0, 175, () => settings.ShowPanel.Value, value => settings.ShowPanel.Value = value);
        Setting(page, VeteranText.Choose("Experience bar", "Barra de experiencia"), 586, 175, () => settings.ShowExperienceBar.Value, value => settings.ShowExperienceBar.Value = value);
        Setting(page, VeteranText.Choose("Rank insignias", "Insignias de rango"), 0, 235, () => settings.ShowVeteranInsignias.Value, value => settings.ShowVeteranInsignias.Value = value);
        Setting(page, VeteranText.Choose("Promotion notices", "Avisos de ascenso"), 586, 235, () => settings.ShowPromotionNotifications.Value, value => settings.ShowPromotionNotifications.Value = value);
        Label(page, "ScaleTitle", VeteranText.Choose("Selection-panel scale", "Escala del panel de selección"), 0, 316, 550, 33, 21, Ivory);
        Btn(page, "Smaller", "−", 586, 312, 70, 40, () => { settings.PanelScale.Value = Mathf.Max(.75f, settings.PanelScale.Value - .1f); settings.File.Save(); Status(VeteranText.Choose("Panel scale: ", "Escala del panel: ") + VeteranText.Number(settings.PanelScale.Value, "0.0")); }, out _);
        Btn(page, "DefaultScale", "100 %", 668, 312, 112, 40, () => { settings.PanelScale.Value = 1; settings.File.Save(); }, out _);
        Btn(page, "Larger", "+", 792, 312, 70, 40, () => { settings.PanelScale.Value = Mathf.Min(1.5f, settings.PanelScale.Value + .1f); settings.File.Save(); Status(VeteranText.Choose("Panel scale: ", "Escala del panel: ") + VeteranText.Number(settings.PanelScale.Value, "0.0")); }, out _);
        Btn(page, "Language", VeteranText.Choose("Language: English", "Idioma: Español"), 904, 312, 248, 40,
            () => ChangeLanguage(settings.Language.Value == "Spanish" ? "English" : "Spanish"), out var language,
            VeteranText.Choose("Click to switch this mod between English and Spanish. Does not change the game's language or your custom rank names.",
                "Pulsa para cambiar el mod entre inglés y español. No cambia el idioma del juego ni los nombres personalizados de rango."));
        language.fontSize = 17;
        Rule(page, 0, 384, 1152, 1, new Color(.44f, .39f, .31f));
        Label(page, "ReportExplanation", VeteranText.Choose("Having a problem? Create a local compatibility report. It contains assembly versions and patch owners, without save data or your account name.", "¿Hay algún problema? Crea un informe local de compatibilidad. Incluye versiones y propietarios de parches, sin partidas ni tu nombre de cuenta."), 0, 409, 770, 82, 19, Muted, true);
        Btn(page, "Diagnostics", VeteranText.Choose("Create diagnostic report", "Crear informe de diagnóstico"), 810, 416, 342, 46, WriteDiagnostics, out _);
        Label(page, "Shortcuts", VeteranText.Choose("Optional shortcuts", "Atajos opcionales") + ": " + settings.ArmyShortcut.Value + "  /  " + settings.DiagnosticsShortcut.Value + "\n" +
            VeteranText.Choose("All features remain accessible through the interface buttons.", "Todas las funciones siguen accesibles desde los botones de la interfaz."), 0, 506, 1152, 65, 18, Muted, true);
    }
    private void Setting(RectTransform page, string label, float x, float y, Func<bool> read, Action<bool> write)
    {
        Btn(page, label, "", x, y, 566, 46, () => { write(!read()); VeteranRuntime.Settings.File.Save(); RefreshSettings(); }, out var text);
        settingControls.Add(new SettingControl(text, label, read));
    }
    private void ChangeLanguage(string language)
    {
        if (!opened || confirming || currentTab != 3 || !CurrentWorld) return;
        string draftName = nameInput.text;
        bool draftDirty = nameDirty;
        ReleaseFocus(false);
        VeteranRuntime.Settings.ApplyInterfaceLanguage(language);
        // Rebuild only our presentation. World/entity identity, filters, pages, selection
        // and unsaved nickname input stay in this component; no gameplay data is recreated.
        if (root != null)
        {
            VeteranUiBridge.HideHints(root);
            root.gameObject.SetActive(false);
            Destroy(root.gameObject); root = null; panel = null;
        }
        if (hud != null)
        {
            VeteranUiBridge.HideHints(hud);
            hud.gameObject.SetActive(false);
            Destroy(hud.gameObject); hud = null;
        }
        Build();
        nameInput.SetTextWithoutNotify(draftName); nameDirty = draftDirty;
        root!.gameObject.SetActive(true);
        EnsureHud(); if (hud != null) hud.gameObject.SetActive(false);
        VeteranPromotionFeed.RefreshLanguage();
        VeteranNativePanel.RefreshLanguage();
        Fit(); Refresh();
        Status(VeteranText.Choose("Mod language updated. The game's language is unchanged.", "Idioma del mod actualizado. El idioma del juego se mantiene."));
    }
    private void EnsureHud()
    {
        if (hud != null || !UIViewManager.Exist || !VeteranUiBridge.InputAvailable) return;
        VeteranUiBridge.CaptureStyle(); if (VeteranUiBridge.Font == null) return;
        hud = NativeRoot("VeteranSoldiersHud");
        hud.anchorMin = hud.anchorMax = new Vector2(0, 1); hud.pivot = new Vector2(0, 1);
        hud.sizeDelta = new Vector2(160, 44);
        var button = Btn(hud, "Veterans", VeteranText.Choose("VETERANS", "VETERANOS"), 0, 0, 160, 44, OpenRoster, out var text,
            VeteranText.Choose("Army register, soldier stories, the Hall of Veterans and settings.", "Registro del ejército, historias de soldados, Salón de los Veteranos y ajustes."));
        text.rectTransform.anchoredPosition = new Vector2(40, 0); text.rectTransform.sizeDelta = new Vector2(114, 44);
        text.fontSize = 17; VeteranUiBridge.Heading(text);
        var icon = VeteranUiBridge.Fill(VeteranUiBridge.Rect("VeteranSeal", button.transform, 9, 7, 26, 30), Color.white);
        icon.sprite = VeteranVisualAssets.RankSprite(4); icon.preserveAspect = true;
        var colors = button.colors; colors.normalColor = new Color(.32f, .28f, .23f); colors.selectedColor = colors.normalColor; button.colors = colors;
        Rule(button.transform, 0, 0, 160, 2, Gold);
        FitHud();
    }
    private void FitHud()
    {
        if (hud == null || !(hud.parent is RectTransform parent) || parent.rect.width <= 1 || parent.rect.height <= 1) return;
        Rect bounds = Bounds(parent);
        float pixelsPerX = bounds.width / parent.rect.width, pixelsPerY = bounds.height / parent.rect.height;
        if (pixelsPerX <= .01f || pixelsPerY <= .01f) return;
        if (minimap == null)
        {
            var nativeMap = UnityEngine.Object.FindObjectOfType<Minimap>();
            if (nativeMap != null) minimap = nativeMap.transform as RectTransform;
        }
        Rect map = minimap == null ? default : Bounds(minimap);
        var position = VeteranHudLayout.Place(Screen.width, Screen.height, map.x, map.y, map.width, map.height);
        hud.localScale = new Vector3(position.Width / (160 * pixelsPerX), position.Height / (44 * pixelsPerY), 1);
        hud.anchoredPosition = new Vector2((position.Left - bounds.xMin) / pixelsPerX, -(position.Top - bounds.yMin) / pixelsPerY);
    }
    private static RectTransform NativeRoot(string name)
    {
        var go = new GameObject(name, typeof(RectTransform)); UIViewManager.AddCustomUIToCanvas(go);
        go.layer = go.transform.parent.gameObject.layer;
        var rect = (RectTransform)go.transform; rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; return rect;
    }
    private void Fit()
    {
        if (root == null || panel == null || root.rect.width <= 1 || root.rect.height <= 1) return;
        // Fit the real viewport, not an arbitrary maximum of one native canvas unit.
        // The game can scale its canvas down on high-resolution displays; allow upscaling.
        Rect bounds = Bounds(root);
        float pixelsPerX = bounds.width / Mathf.Max(1, root.rect.width);
        float pixelsPerY = bounds.height / Mathf.Max(1, root.rect.height);
        float scale = Mathf.Min(Screen.width * .94f / (1200 * Mathf.Max(.01f, pixelsPerX)), Screen.height * .88f / (820 * Mathf.Max(.01f, pixelsPerY)));
        panel.localScale = Vector3.one * Mathf.Max(.25f, scale);
    }
    private void BuildOrDedicate()
    {
        if (!CurrentWorld) return;
        if (VeteranHall.CanDedicate(out _))
        {
            World? expected = shownWorld;
            Confirm(VeteranText.Choose("Dedicate the University", "Dedicar la Universidad"),
                VeteranText.Choose("Dedicate your University as the Hall of Veterans for free. Existing research, workers and health are retained.\n\nThe Hall occupies the University's building slot; you do not need both buildings.", "Dedica tu Universidad como Salón de los Veteranos sin coste. Conserva la investigación, los trabajadores y la vida actuales.\n\nEl Salón ocupa el lugar de la Universidad; no necesitas los dos edificios."), () =>
                {
                    if (!CurrentWorld || !ReferenceEquals(expected, VeteranRuntime.ActiveWorld)) return;
                    bool result = VeteranHall.TryDedicate(out string reason);
                    Status(result ? VeteranText.Choose("The University is now your Hall of Veterans.", "La Universidad es ahora tu Salón de los Veteranos.") : reason);
                });
            return;
        }
        if (VeteranHall.BeginConstruction(out string constructionReason)) SetOpen(false); else Status(constructionReason);
    }
    private static TMP_Text Label(Transform parent, string name, string text, float x, float y, float w, float h, float size, Color color, bool wrap = false) => VeteranUiBridge.Text(parent, name, text, x, y, w, h, size, color, wrap);
    private static Button Btn(Transform parent, string name, string text, float x, float y, float w, float h, Action action, out TMP_Text label, string hint = "") => VeteranUiBridge.Button(parent, name, text, x, y, w, h, action, out label, hint);
    private static void Rule(Transform parent, float x, float y, float w, float h, Color color) => VeteranUiBridge.Fill(VeteranUiBridge.Rect("Rule", parent, x, y, w, h), color);
    private void Disable(Exception error)
    { if (!failed) VeteranRuntime.Log.LogWarning("Veteran journal disabled: " + error); failed = true; SetOpen(false); if (hud != null) hud.gameObject.SetActive(false); }
    private void OnDestroy()
    { SetOpen(false); if (root != null) Destroy(root.gameObject); if (hud != null) Destroy(hud.gameObject); if (ReferenceEquals(instance, this)) instance = null; }
}
