using System;
using System.Linq;
using HarmonyLib;
using Systems;
using VeteranSoldiers;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
bool Near(double value, double expected) => Math.Abs(value - expected) < .000001;
var target = AccessTools.Method(typeof(HealthTrackSystem), nameof(HealthTrackSystem.OnUpdateSimulation));
var veteran = new Harmony(VeteranRuntime.Guid);
var other = new Harmony("compatibility.fixture.other-mod");

void Clean()
{
    veteran.UnpatchSelf();
    other.UnpatchSelf();
}
HealthTrackSystem Setup(string? prefix = null, string? postfix = null, bool foreignFirst = false, bool lateCancellation = false)
{
    Clean();
    VeteranRuntime.Prepare();
    var system = new HealthTrackSystem();
    void Foreign()
    {
        var pre = prefix == null ? null : new HarmonyMethod(typeof(ForeignMod), prefix);
        if (lateCancellation && pre != null)
        {
            pre.priority = Priority.Last;
            pre.after = new[] { VeteranRuntime.Guid };
        }
        other.Patch(target, pre, postfix == null ? null : new HarmonyMethod(typeof(ForeignMod), postfix));
    }
    if (foreignFirst && (prefix != null || postfix != null)) Foreign();
    // Use the real registration path in Bootstrap.cs, not duplicated test patches.
    VeteranRuntime.Initialize(system.World);
    if (!foreignFirst && (prefix != null || postfix != null)) Foreign();
    return system;
}

try
{
    var system = Setup();
    var patches = Harmony.GetPatchInfo(target);
    Check(patches.Prefixes.Single(p => p.owner == VeteranRuntime.Guid).priority == Priority.Last,
        "production capture prefix is last priority");
    Check(patches.Postfixes.Single(p => p.owner == VeteranRuntime.Guid).priority == Priority.First,
        "production observation postfix is first priority");
    system.OnUpdateSimulation();
    Check(system.OriginalCalls == 1 && VeteranRuntime.Captures == 1 && VeteranRuntime.Observations == 1,
        "one original execution has one valid observation");
    Check(string.Join(",", VeteranRuntime.Trace) == "complete,capture,original,complete,observe",
        "dependencies complete on both sides of the original");

    foreach (bool foreignFirst in new[] { false, true })
    {
        string order = foreignFirst ? "foreign installed first" : "foreign installed last";
        system = Setup(nameof(ForeignMod.RewriteDamage), foreignFirst: foreignFirst);
        system.OnUpdateSimulation();
        Check(Near(VeteranRuntime.CreditA, 12) && Near(VeteranRuntime.CreditB, 0),
            "changed/cancelled source requests are captured after other prefix: " + order);
        Check(Near(system.Health, 88), "rewritten damage reaches original unchanged: " + order);

        system = Setup(nameof(ForeignMod.Cancel), foreignFirst: foreignFirst);
        system.OnUpdateSimulation();
        Check(system.OriginalCalls == 0 && Near(system.Health, 100) && VeteranRuntime.Captures == 0 &&
              VeteranRuntime.Observations == 0 && VeteranRuntime.Completions == 0,
            "cancelled original causes no capture, health work or credit: " + order);

        system = Setup(postfix: nameof(ForeignMod.Heal), foreignFirst: foreignFirst);
        system.OnUpdateSimulation();
        Check(Near(system.Health, 100) && Near(VeteranRuntime.CapturedAfter, 80) &&
              Near(VeteranRuntime.CreditA, 10) && Near(VeteranRuntime.CreditB, 10),
            "foreign postfix healing is outside observed original damage: " + order);
        Check(VeteranRuntime.Trace.IndexOf("observe") < VeteranRuntime.Trace.IndexOf("foreign-heal"),
            "observation runs before foreign postfix: " + order);
    }

    system = Setup(nameof(ForeignMod.Cancel), lateCancellation: true);
    system.OnUpdateSimulation();
    Check(VeteranRuntime.Captures == 1 && VeteranRuntime.Observations == 0 && system.OriginalCalls == 0 &&
          Near(VeteranRuntime.CreditA + VeteranRuntime.CreditB, 0),
        "explicit later cancellation is rejected by postfix runOriginal guard");
    other.UnpatchSelf();
    system.DamageA = 4; system.DamageB = 0;
    system.OnUpdateSimulation();
    Check(Near(VeteranRuntime.CreditA, 4) && Near(VeteranRuntime.CreditB, 0),
        "next call uses a fresh capture after cancelled original");

    system = Setup();
    VeteranRuntime.Settings.Enabled.Value = false;
    system.OnUpdateSimulation();
    Check(system.OriginalCalls == 1 && VeteranRuntime.Completions == 0 && VeteranRuntime.Observations == 0,
        "disabled runtime leaves original execution alone without dependency completion");

    system = Setup();
    VeteranRuntime.ThrowOnCapture = true;
    system.OnUpdateSimulation();
    Check(system.OriginalCalls == 1 && VeteranRuntime.Captures == 1 && VeteranRuntime.Observations == 0 &&
          !VeteranRuntime.Ready && Near(VeteranRuntime.CreditA + VeteranRuntime.CreditB, 0),
        "failed capture is isolated and never produces an observation");

    system = Setup();
    HealthHooks.After(system, true, null);
    Check(VeteranRuntime.Completions == 0 && VeteranRuntime.Observations == 0,
        "postfix without a captured per-call state is inert");
    HealthHooks.Before(system, true, out var state);
    var otherSystem = new HealthTrackSystem();
    HealthHooks.After(otherSystem, true, state);
    Check(VeteranRuntime.Completions == 1 && VeteranRuntime.Observations == 0,
        "state from another world cannot credit this invocation");
    VeteranRuntime.SetWorld(otherSystem.World);
    HealthHooks.After(system, true, state);
    Check(VeteranRuntime.Completions == 1 && VeteranRuntime.Observations == 0,
        "world replaced after capture cannot use an obsolete state");

    Console.WriteLine($"{checks} Harmony compatibility checks passed (managed fixture; no game run).");
}
finally { Clean(); }

public static class ForeignMod
{
    public static void RewriteDamage(HealthTrackSystem __instance)
    {
        VeteranRuntime.Trace.Add("foreign-prefix");
        __instance.DamageA = 12;
        __instance.DamageB = 0;
    }
    public static bool Cancel() => false;
    public static void Heal(HealthTrackSystem __instance)
    {
        VeteranRuntime.Trace.Add("foreign-heal");
        __instance.Health += 20;
    }
}
