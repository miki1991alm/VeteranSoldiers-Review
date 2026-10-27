using System;
using System.Collections.Generic;

namespace Components { public struct Enemy { } }
namespace Components.RawComponents { internal struct Unused { } }
namespace Unity.Entities
{
    public readonly struct Entity : IEquatable<Entity>
    {
        public readonly int Index, Version;
        public Entity(int index, int version = 1) { Index = index; Version = version; }
        public static Entity Null => default;
        public bool Equals(Entity value) => Index == value.Index && Version == value.Version;
        public override bool Equals(object? value) => value is Entity entity && Equals(entity);
        public override int GetHashCode() => HashCode.Combine(Index, Version);
        public static bool operator ==(Entity left, Entity right) => left.Equals(right);
        public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
    }
    public sealed class World
    {
        public bool IsCreated = true;
        public readonly EntityManager EntityManager = new EntityManager();
    }
    public sealed class EntityManager
    {
        public readonly HashSet<Entity> Entities = new HashSet<Entity>();
        public readonly HashSet<Entity> Dead = new HashSet<Entity>();
        public readonly HashSet<Entity> Enemies = new HashSet<Entity>();
        public bool JobsCompleted = true;
        public int CompleteCalls;
        public Action? OnComplete;
        public void CompleteAllJobs() { CompleteCalls++; JobsCompleted = true; OnComplete?.Invoke(); }
        public bool Exists(Entity value) => Entities.Contains(value);
        public bool HasComponent<T>(Entity value) => typeof(T) == typeof(Components.Enemy) && Enemies.Contains(value);
    }
}
namespace VeteranSoldiers
{
    internal static class VeteranRuntime
    {
        internal static Unity.Entities.World World = new Unity.Entities.World();
        internal static bool IsAlive(Unity.Entities.Entity entity) => World.IsCreated && World.EntityManager.Exists(entity) && !World.EntityManager.Dead.Contains(entity);
        internal static bool IsEligible(Unity.Entities.Entity entity)
        {
            if (!World.EntityManager.JobsCompleted) throw new InvalidOperationException("Eligibility read before pending jobs completed.");
            return IsAlive(entity);
        }
    }
}
