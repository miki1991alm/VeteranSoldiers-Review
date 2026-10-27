using System;
using System.Collections.Generic;
using Components;
using Components.RawComponents;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace VeteranSoldiers;

/// <summary>
/// Bounded, read-only presentation of the native panel's exact selection snapshot.
/// Entity data is read only after a new 5 Hz snapshot; repaint never accesses ECS.
/// </summary>
[DefaultExecutionOrder(200)]
internal sealed class VeteranWorldInsignias : MonoBehaviour
{
    private const int MaximumCandidates = 256;
    private const int MaximumVisible = 64;
    private const float SnapshotLifetime = .5f;
    private const float MotionDuration = .18f;
    private readonly Candidate[] candidates = new Candidate[MaximumCandidates];
    private readonly Rect[] occupied = new Rect[MaximumVisible];
    private readonly Sprite?[] sharedSprites = new Sprite?[5];
    private readonly Rect[] spriteUvs = new Rect[5];
    private Dictionary<Entity, Motion> previousMotion = new Dictionary<Entity, Motion>(MaximumCandidates);
    private Dictionary<Entity, Motion> nextMotion = new Dictionary<Entity, Motion>(MaximumCandidates);
    private World? world;
    private EntityQuery cameraQuery;
    private bool queryCreated, warned;
    private Camera? sceneCamera;
    private int candidateCount, lastRevision = -1;
    private float snapshotTime, retryAfter;

    private struct Candidate
    {
        public Motion Motion;
        public int Level;
        public Specialization Specialization;
        public bool Favorite;
    }

    private struct Motion
    {
        public Vector3 From;
        public Vector3 To;
        public float StartedAt;
    }

    private static bool Visible => VeteranRuntime.Ready && !VeteranNativePanel.Hidden &&
        !VeteranArmyWindow.IsOpen && VeteranRuntime.Settings.ShowVeteranInsignias.Value && VeteranNativePanel.WorldInsigniasAllowed;

    private void LateUpdate()
    {
        if (!Visible)
        {
            candidateCount = 0;
            lastRevision = -1;
            previousMotion.Clear();
            nextMotion.Clear();
            return;
        }

        var active = VeteranNativePanel.SelectionWorld;
        if (active == null || !active.IsCreated || !ReferenceEquals(active, VeteranRuntime.ActiveWorld))
        {
            ReleaseWorld();
            return;
        }
        if (Time.unscaledTime < retryAfter) return;

        try
        {
            if (!ReferenceEquals(world, active))
            {
                ReleaseWorld();
                world = active;
                cameraQuery = active.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<MainCameraRoot>());
                queryCreated = true;
            }
            int revision = VeteranNativePanel.SelectionRevision;
            if (revision == lastRevision) return;
            candidateCount = 0;
            RefreshCandidates(active.EntityManager);
            var reusable = previousMotion;
            previousMotion = nextMotion;
            nextMotion = reusable;
            lastRevision = revision;
            snapshotTime = Time.unscaledTime;
        }
        catch (Exception error)
        {
            candidateCount = 0;
            lastRevision = -1;
            retryAfter = Time.unscaledTime + 1f;
            if (!warned)
            {
                warned = true;
                VeteranRuntime.Log.LogWarning("World insignias unavailable; combat remains active: " + error.Message);
            }
        }
    }

    private void RefreshCandidates(EntityManager manager)
    {
        nextMotion.Clear();
        sceneCamera = null;
        if (!queryCreated || cameraQuery.CalculateEntityCount() != 1) return;
        Entity cameraEntity = cameraQuery.GetSingleton<MainCameraRoot>().camera;
        if (!manager.Exists(cameraEntity)) return;
        // This is the same camera lookup used by the game's ObjectSelectionSystem.
        sceneCamera = manager.GetComponentObject<Camera>(cameraEntity);
        if (sceneCamera == null || !sceneCamera.isActiveAndEnabled) return;

        var selected = VeteranNativePanel.SelectedVeterans;
        // The bounded cache favors the most experienced ranks in crowded selections.
        // Never query all soldiers, prototypes or rendering children to locate an owner.
        for (int level = 4; level >= 1; level--)
        {
            for (int i = 0; i < selected.Count && candidateCount < MaximumCandidates; i++)
            {
                var entry = selected[i];
                if (entry.Level != level) continue;
                Entity entity = entry.Entity;
                if (!manager.Exists(entity) || !manager.HasComponent<Selected>(entity) ||
                    manager.HasComponent<Dead>(entity) || manager.HasComponent<DelayedDestroy>(entity) ||
                    manager.HasComponent<Disabled>(entity) || manager.HasComponent<InternalDisable>(entity) ||
                    !manager.HasComponent<LocalToWorld>(entity)) continue;
                if (manager.HasComponent<HideUnitInFog>(entity) &&
                    manager.GetComponentData<HideUnitInFog>(entity).Value > 0f) continue;
                if (!manager.HasComponent<Health>(entity) || manager.GetComponentData<Health>(entity).currentHealth <= 0f) continue;

                var position = manager.GetComponentData<LocalToWorld>(entity).Position;
                Vector3 anchor = new Vector3(position.x, position.y + 2.2f, position.z);
                if (manager.HasComponent<IndicatorAnchor>(entity))
                {
                    var configuredAnchor = manager.GetComponentData<IndicatorAnchor>(entity).Value;
                    if (configuredAnchor.IsCreated)
                    {
                        var offset = configuredAnchor.Value;
                        anchor = new Vector3(position.x + offset.x, position.y + offset.y, position.z + offset.z);
                    }
                }
                if (!Finite(anchor)) continue;
                Vector3 projected = sceneCamera.WorldToViewportPoint(anchor);
                if (!InView(projected, sceneCamera.nearClipPlane, sceneCamera.farClipPlane)) continue;
                Vector3 from = anchor;
                if (previousMotion.TryGetValue(entity, out var previous) &&
                    (previous.To - anchor).sqrMagnitude < 64f)
                    from = PositionAt(previous, Time.unscaledTime);
                var motion = new Motion { From = from, To = anchor, StartedAt = Time.unscaledTime };
                nextMotion[entity] = motion;
                CacheSprite(level);
                VeteranRuntime.TryGetSnapshot(entity, out var snapshot);
                bool favorite = VeteranChronicle.TryGet(entity, out var service) && service.Favorite;
                candidates[candidateCount++] = new Candidate { Motion = motion, Level = level, Specialization = snapshot.Specialization, Favorite = favorite };
            }
            if (candidateCount == MaximumCandidates) break;
        }
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !Visible || candidateCount == 0 ||
            Time.unscaledTime - snapshotTime > SnapshotLifetime ||
            world == null || !world.IsCreated || !ReferenceEquals(world, VeteranNativePanel.SelectionWorld) ||
            !ReferenceEquals(world, VeteranRuntime.ActiveWorld) ||
            sceneCamera == null || !sceneCamera.isActiveAndEnabled) return;

        Color previousColor = GUI.color;
        Matrix4x4 previousMatrix = GUI.matrix;
        try
        {
            GUI.matrix = Matrix4x4.identity;
            DrawInsignias(sceneCamera);
        }
        catch (Exception error)
        {
            candidateCount = 0;
            lastRevision = -1;
            retryAfter = Time.unscaledTime + 1f;
            if (!warned)
            {
                warned = true;
                VeteranRuntime.Log.LogWarning("World insignia drawing unavailable: " + error.Message);
            }
        }
        finally
        {
            GUI.color = previousColor;
            GUI.matrix = previousMatrix;
        }
    }

    private void DrawInsignias(Camera camera)
    {
        // Fixed screen size: no huge medals when zoomed in, no illegible subpixel art.
        float scale = Mathf.Clamp(Screen.height / 1080f, .85f, 1.5f);
        float size = 24f * scale;
        float gap = 5f * scale;
        int drawn = 0;
        for (int i = 0; i < candidateCount && drawn < MaximumVisible; i++)
        {
            Candidate candidate = candidates[i];
            Vector3 anchor = PositionAt(candidate.Motion, Time.unscaledTime);
            Vector3 viewport = camera.WorldToViewportPoint(anchor);
            if (!InView(viewport, camera.nearClipPlane, camera.farClipPlane)) continue;
            Vector3 screen = camera.WorldToScreenPoint(anchor);
            float x = screen.x - size * .5f;
            float y = Screen.height - screen.y - size - 6f * scale;
            Rect area = new Rect(x - gap, y - gap, size + 2f * gap, size + 2f * gap + 6f * scale);
            if (area.xMin < 0 || area.yMin < 0 || area.xMax > Screen.width || area.yMax > Screen.height) continue;
            if (VeteranNativePanel.OverlapsHud(area) || VeteranPromotionFeed.Overlaps(area)) continue;

            bool overlaps = false;
            for (int j = 0; j < drawn; j++)
            {
                if (!occupied[j].Overlaps(area)) continue;
                overlaps = true;
                break;
            }
            if (overlaps) continue;

            Sprite? sprite = sharedSprites[candidate.Level];
            if (sprite == null) continue;
            Texture2D texture = sprite.texture;
            Rect uv = spriteUvs[candidate.Level];
            Rect icon = new Rect(x, y, size, size);
            GUI.color = new Color(0f, 0f, 0f, .4f);
            GUI.DrawTextureWithTexCoords(new Rect(x + scale, y + 2f * scale, size, size), texture, uv);
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(icon, texture, uv);
            if (candidate.Favorite)
            {
                // A small gold corner tab identifies favorites without changing any unit material.
                GUI.color = new Color(1f, .88f, .46f);
                GUI.DrawTexture(new Rect(x + size - 4f * scale, y, 4f * scale, 7f * scale), Texture2D.whiteTexture);
            }
            if (candidate.Specialization != Specialization.None)
            {
                // The ribbon's color and number of strokes both identify the path; color is not the only cue.
                GUI.color = VeteranVisualAssets.SpecialtyColor(candidate.Specialization);
                int marks = (int)candidate.Specialization;
                float width = marks * 3f * scale + (marks - 1) * 2f * scale;
                for (int mark = 0; mark < marks; mark++)
                    GUI.DrawTexture(new Rect(screen.x - width * .5f + mark * 5f * scale, y + size - 2f * scale, 3f * scale, 3f * scale), Texture2D.whiteTexture);
            }
            GUI.color = VeteranVisualAssets.RankColor(candidate.Level);
            GUI.DrawTexture(new Rect(screen.x - scale, y + size + scale, 2f * scale, 4f * scale), Texture2D.whiteTexture);
            GUI.color = Color.white;
            occupied[drawn++] = area;
        }
    }

    private static bool InView(Vector3 viewport, float nearClip, float farClip) => Finite(viewport) &&
        viewport.z > nearClip && viewport.z < farClip &&
        viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;

    private static Vector3 PositionAt(Motion motion, float now) =>
        Vector3.Lerp(motion.From, motion.To, Mathf.Clamp01((now - motion.StartedAt) / MotionDuration));

    private void CacheSprite(int level)
    {
        if (sharedSprites[level] != null) return;
        // Lazy asset generation occurs at snapshot refresh, never inside GUI repaint.
        Sprite sprite = VeteranVisualAssets.RankSprite(level);
        Rect source = sprite.textureRect;
        Texture2D texture = sprite.texture;
        spriteUvs[level] = new Rect(source.x / texture.width, source.y / texture.height,
            source.width / texture.width, source.height / texture.height);
        sharedSprites[level] = sprite;
    }

    private static bool Finite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private void ReleaseWorld()
    {
        candidateCount = 0;
        lastRevision = -1;
        previousMotion.Clear();
        nextMotion.Clear();
        sceneCamera = null;
        if (queryCreated && world != null && world.IsCreated) cameraQuery.Dispose();
        queryCreated = false;
        world = null;
    }

    private void OnDisable() => ReleaseWorld();
    private void OnDestroy() => ReleaseWorld();
}
