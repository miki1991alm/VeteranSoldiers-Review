using System;
using Unity.Entities;
using VeteranSoldiers;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    private static Entity Unit(int index, int version = 1, bool enemy = false)
    {
        var entity = new Entity(index, version);
        VeteranRuntime.World.EntityManager.Entities.Add(entity);
        if (enemy) VeteranRuntime.World.EntityManager.Enemies.Add(entity);
        return entity;
    }
    private static void NewWorld()
    {
        VeteranRuntime.World = new World();
        VeteranChronicle.SetWorld(VeteranRuntime.World);
    }
    private static int Main()
    {
        NewWorld(); var first = Unit(7);
        VeteranRuntime.World.EntityManager.JobsCompleted = false;
        Check(VeteranChronicle.SetName(first, "Aldric") && VeteranRuntime.World.EntityManager.CompleteCalls == 1,
            "rename completes pending ECS jobs before health eligibility is read");
        VeteranRuntime.World.EntityManager.JobsCompleted = false;
        Check(VeteranChronicle.ToggleFavorite(first) && VeteranRuntime.World.EntityManager.CompleteCalls == 2,
            "favorite completes pending ECS jobs before health eligibility is read");
        VeteranChronicle.ToggleFavorite(first);
        VeteranChronicle.SetName(first, "Aldric"); VeteranChronicle.ToggleFavorite(first);
        string firstIdentity = VeteranChronicle.GetOrCreate(first).Identity;
        VeteranChronicle.RecordCombat(first, 270, false);
        Check(VeteranChronicle.ConfirmedKills == 0 && VeteranChronicle.EffectiveDamage == 270, "effective contribution and killing credit remain separate");
        VeteranRuntime.World.EntityManager.Entities.Remove(first);
        var recycled = Unit(7, 2); VeteranChronicle.Prune();
        Check(!VeteranChronicle.TryGet(recycled, out _) && VeteranChronicle.Memorial.Count == 0, "recycled entity index inherits no identity and despawn creates no memorial");
        Check(VeteranChronicle.GetOrCreate(recycled).Identity != firstIdentity, "recycled entity receives a new personal identity");
        VeteranChronicle.RecordCombat(recycled, 300, true, true);
        VeteranChronicle.RecordPromotion(recycled, 2);
        var enemy = Unit(50, enemy: true);
        VeteranChronicle.RecordCombat(enemy, 20000, true, true, true); VeteranChronicle.RecordPromotion(enemy, 4);
        Check(VeteranChronicle.EffectiveDamage == 570 && VeteranChronicle.ConfirmedKills == 1 && VeteranChronicle.Promotions == 1,
            "enemy veterancy cannot unlock player campaign milestones");
        Check(!VeteranChronicle.SetName(enemy, "Enemy") && !VeteranChronicle.ToggleFavorite(enemy), "enemy cannot enter player favorites or rename flow");
        VeteranChronicle.RecordDeath(recycled, 68, 2); VeteranChronicle.RecordDeath(recycled, 68, 2);
        Check(VeteranChronicle.Memorial.Count == 1 && VeteranChronicle.ObservedDeaths == 1, "repeated observed death callback cannot duplicate a memorial entry");
        var save = VeteranChronicle.CaptureCampaign();
        VeteranChronicle.Memorial[0].Name = "Later";
        Check(save.Memorial[0].Name == "", "campaign save capture freezes history independently of later changes");
        VeteranChronicle.RestoreCampaign(save);
        VeteranChronicle.RestoreCampaign(save);
        Check(VeteranChronicle.ObservedDeaths == 1 && VeteranChronicle.Memorial.Count == 1, "campaign restore replaces state and never adds milestones twice");
        for (int i = 0; i < 300; i++)
        {
            var unit = Unit(100 + i); VeteranChronicle.GetOrCreate(unit);
            VeteranChronicle.RecordDeath(unit, 68, 0);
        }
        Check(VeteranChronicle.Memorial.Count == 256 && VeteranChronicle.ObservedDeaths == 301, "memorial keeps a bounded roster while preserving total observed deaths");
        NewWorld(); var sameNumbers = Unit(7, 2);
        Check(!VeteranChronicle.TryGet(sameNumbers, out _) && VeteranChronicle.EffectiveDamage == 0 && VeteranChronicle.Memorial.Count == 0,
            "identical entity numbers in a new World cannot inherit campaign history");
        var recruit = Unit(80); VeteranChronicle.SetName(recruit, "Mara"); VeteranChronicle.ToggleFavorite(recruit);
        var recruitSnapshot = VeteranChronicle.GetOrCreate(recruit).Clone();
        VeteranChronicle.ResetSession(); VeteranChronicle.Restore(recruit, recruitSnapshot);
        Check(VeteranChronicle.TryGet(recruit, out var restored) && restored.Name == "Mara" && restored.Favorite && restored.CombatAwards == 0,
            "zero-combat recruit restores chosen identity without fabricated history");
        var previousWorld = VeteranRuntime.World;
        previousWorld.EntityManager.OnComplete = NewWorld;
        Check(!VeteranChronicle.SetName(recruit, "Wrong world") && !VeteranChronicle.TryGet(recruit, out _),
            "mutation refuses a world replaced while pending jobs finish");
        var expired = Unit(81);
        VeteranRuntime.World.EntityManager.OnComplete = () => VeteranRuntime.World.EntityManager.Entities.Remove(expired);
        Check(!VeteranChronicle.ToggleFavorite(expired) && !VeteranChronicle.TryGet(expired, out _),
            "mutation revalidates exact entity existence after completing jobs");
        VeteranRuntime.World.EntityManager.OnComplete = null;
        int completedBeforeDisposal = VeteranRuntime.World.EntityManager.CompleteCalls;
        VeteranRuntime.World.IsCreated = false; VeteranChronicle.Prune();
        Check(!VeteranChronicle.SetName(expired, "Disposed") && !VeteranChronicle.ToggleFavorite(expired) &&
            VeteranRuntime.World.EntityManager.CompleteCalls == completedBeforeDisposal,
            "disposed world rejects button mutations before accessing its job manager");
        Check(!VeteranChronicle.TryGet(recruit, out _) && VeteranChronicle.Memorial.Count == 0, "disposed World releases all chronicle state without creating deaths");
        Console.WriteLine("PASS: " + checks + " chronicle lifecycle checks using an in-memory ECS boundary; not game runtime validation.");
        return 0;
    }
}
