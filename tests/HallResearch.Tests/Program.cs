using System;
using VeteranSoldiers;

internal static class Program
{
    public struct Effect { public int objectId; public float multiplier; }
    public sealed class Research
    {
        public int ownerObjectId = 119;
        public string name = "University research";
        public Effect[] healthEffects = Array.Empty<Effect>();
        public float[] globalEffects = new[] { 1.5f };
    }
    public sealed class UnsafeEffect { public int objectId = 119; }
    public sealed class UnsafeResearch { public UnsafeEffect[] effects = new[] { new UnsafeEffect() }; }
    public struct ChangedEffect { public int GetObjectId() => 119; }
    public sealed class ChangedResearch { public ChangedEffect[] effects = new[] { new ChangedEffect() }; }
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception) { Check(true, message); return; } throw new Exception(message); }
    private static int Main()
    {
        const int hall = 191981074;
        var item = new Research { healthEffects = new[] { new Effect { objectId = 119, multiplier = .25f }, new Effect { objectId = 87, multiplier = .4f } } };
        var source = new[] { item };
        var copy = HallResearchAliases.Copy(source, 119, hall, out int added, out string targets);
        Check(added == 1 && targets == "healthEffects=1" && copy[0].healthEffects.Length == 3, "only actual base-targeted effects gain a Hall alias");
        Check(copy[0].healthEffects[2].objectId == hall && copy[0].healthEffects[2].multiplier == .25f, "alias retains the existing effect magnitude");
        Check(source[0].healthEffects.Length == 2 && source[0].healthEffects[0].objectId == 119 && !ReferenceEquals(copy[0], item), "original asset item and effect array remain unchanged");
        Check(copy[0].ownerObjectId == 119 && ReferenceEquals(copy[0].globalEffects, item.globalEffects), "research ownership and unrelated global effects remain intact");
        copy[0].healthEffects[2].multiplier = .8f;
        Check(source[0].healthEffects[0].multiplier == .25f, "editing an alias cannot mutate its source effect");
        var repeated = HallResearchAliases.Copy(copy, 119, hall, out added, out _);
        Check(ReferenceEquals(repeated, copy) && added == 0, "repeated aliasing cannot duplicate effects");
        var explicitItem = new Research { healthEffects = new[] { new Effect { objectId = 119, multiplier = .2f }, new Effect { objectId = hall, multiplier = .7f } } };
        var explicitSource = new[] { explicitItem };
        Check(ReferenceEquals(HallResearchAliases.Copy(explicitSource, 119, hall, out added, out _), explicitSource) && added == 0,
            "an explicit third-party Hall effect takes priority without overwrite or stacking");
        var unrelated = new[] { new Research { healthEffects = new[] { new Effect { objectId = 87, multiplier = .6f } } } };
        Check(ReferenceEquals(HallResearchAliases.Copy(unrelated, 119, hall, out added, out targets), unrelated) && added == 0 && targets == "none",
            "missions without base-targeted effects do not receive synthetic effects");
        var dynamicSource = new[] { new Research { healthEffects = new[] { new Effect { objectId = 777, multiplier = 2 } } } };
        Check(HallResearchAliases.Copy(dynamicSource, 777, hall, out added, out _)[0].healthEffects[1].objectId == hall && added == 1,
            "alias identity follows discovered native object rather than a hard-coded University ID");
        Reject(() => HallResearchAliases.Copy(source, 119, 119, out _, out _), "identical identities are rejected");
        Reject(() => HallResearchAliases.Copy(new Research[] { null! }, 119, hall, out _, out _), "missing research item fails before publication");
        Reject(() => HallResearchAliases.Copy(new[] { new UnsafeResearch() }, 119, hall, out _, out _), "mutable reference effects are rejected without editing assets");
        Reject(() => HallResearchAliases.Copy(new[] { new ChangedResearch() }, 119, hall, out _, out _), "an altered native object-identity contract fails closed");
        var oversized = new[] { new Research { healthEffects = new Effect[HallResearchAliases.MaximumEffects + 1] } };
        Reject(() => HallResearchAliases.Copy(oversized, 119, hall, out _, out _), "excessive effect arrays fail within bounded parsing");
        Check(item.healthEffects[0].objectId == 119 && item.healthEffects.Length == 2, "rejected calls leave original research data unchanged");
        HallResearchStartupTests.Run(Check);
        Console.WriteLine("PASS: " + checks + " research alias/startup checks; native research execution still requires runtime acceptance.");
        return 0;
    }
}
