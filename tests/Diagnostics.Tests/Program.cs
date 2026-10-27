using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using VeteranSoldiers;

int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
var fixture = Path.GetFullPath(Path.Combine("logs", "diagnostics-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
var own = new Harmony(VeteranRuntime.Guid);
var foreign = new Harmony("diagnostics.fixture.other");
var target = AccessTools.Method(typeof(Target), nameof(Target.Run));
try
{
    string installed = Path.Combine(fixture, "ecs_plugins");
    Directory.CreateDirectory(Path.Combine(installed, "nested"));
    string executable = Assembly.GetExecutingAssembly().Location;
    File.Copy(executable, Path.Combine(installed, "first.dll"));
    File.Copy(executable, Path.Combine(installed, "nested", "renamed-copy.dll"));
    File.WriteAllText(Path.Combine(installed, "broken.dll"), "not a managed dll");
    File.WriteAllText(Path.Combine(fixture, "personal.cfg"), "secret=never inspect this sentinel");
    string before = File.ReadAllText(Path.Combine(fixture, "personal.cfg"));
    int loadedBefore = AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == Assembly.GetExecutingAssembly().GetName().Name);
    own.Patch(target, new HarmonyMethod(typeof(Hooks), nameof(Hooks.Before)) { priority = Priority.Last });
    foreign.Patch(target, new HarmonyMethod(typeof(Hooks), nameof(Hooks.Before)) { priority = Priority.Normal, before = new[] { VeteranRuntime.Guid } },
        transpiler: new HarmonyMethod(typeof(Hooks), nameof(Hooks.PassThrough)));
    var snapshot = CompatibilityDiagnostics.Capture(fixture, new[] { Assembly.GetExecutingAssembly() });
    string report = CompatibilityDiagnostics.Render(snapshot);
    Check(snapshot.Files.Count == 3, "all three installed DLL candidates inspected, including malformed input");
    Check(snapshot.Files.Count(entry => entry.Hash.Length == 64) == 2, "valid DLL metadata includes content hashes");
    Check(snapshot.Loaded.Count == 1 && snapshot.Plugins.Count == 2, "loaded BepInPlugin metadata includes two declarations");
    Check(snapshot.Loaded[0].Mvid == Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("D"), "loaded assembly identity records its real MVID");
    Check(snapshot.Issues.Contains("FILE_METADATA_NOT_MANAGED_DLL"), "bad DLL becomes a partial-inspection code without aborting");
    Check(ConstructorTracker.Count == 0, "reflection never instantiates attribute or plugin constructors");
    Check(loadedBefore == AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == Assembly.GetExecutingAssembly().GetName().Name), "disk inspection never loads the renamed DLL copies");
    Check(snapshot.Targets.Count == 1 && snapshot.Targets[0].Patches.Count == 3, "actual Harmony registrations are discovered from our owner");
    Check(snapshot.Targets[0].Patches.Any(patch => patch.Owner == VeteranRuntime.Guid && patch.Priority == Priority.Last), "actual production owner and declared priority retained");
    Check(snapshot.Targets[0].Patches.Any(patch => patch.Owner == "diagnostics.fixture.other" && patch.Before.Contains(VeteranRuntime.Guid)), "foreign before relationship retained");
    Check(report.Contains("DUPLICATE_INSTALLED_ASSEMBLY"), "renamed same-assembly DLL copies produce an overlap warning");
    Check(report.Contains("DUPLICATE_PLUGIN_GUID"), "duplicate plugin GUID declarations produce a warning");
    Check(report.Contains("SHARED_METHOD") && report.Contains("FOREIGN_TRANSPILER"), "foreign hook and transpiler are distinguished");
    Check(report.Contains("No warnings does NOT mean compatible"), "report states observation cannot certify compatibility");
    Check(!report.Contains(fixture) && !report.Contains("first.dll") && !report.Contains("renamed-copy.dll"), "report omits paths and user-selected filenames");
    Check(!report.Contains("never inspect this sentinel") && File.ReadAllText(Path.Combine(fixture, "personal.cfg")) == before, "configuration is neither read into output nor changed");
    Check(Target.Run(8) == 8, "diagnostic inspection does not change live fixture behavior");
    Check(Harmony.GetPatchInfo(target).Owners.Contains("diagnostics.fixture.other"), "inspection preserves other owners' registrations");

    var malicious = new DiagnosticSnapshot { InstrumentationPresent = true };
    malicious.Loaded.Add(new DiagnosticAssembly("loaded#1", "Example", "1.0"));
    malicious.Loaded.Add(new DiagnosticAssembly("loaded#2", "Example", "2.0"));
    malicious.Files.Add(new DiagnosticAssembly("C:\\Users\\PrivatePerson", "https://host/key", "password=hunter2"));
    malicious.Plugins.Add(new DiagnosticPlugin("PrivatePerson.mod", "person@example.com", "sk-private-value"));
    var sensitiveTarget = new DiagnosticTarget("Fake.Target");
    sensitiveTarget.Patches.Add(new DiagnosticPatch("prefix", "\nSECRET=hidden", 400, 1, new[] { "Bearer value" }, new[] { "github_pat_value" }, "PrivateMachine.plugin"));
    malicious.Targets.Add(sensitiveTarget);
    string safe = CompatibilityDiagnostics.Render(malicious, "PrivatePerson", "PrivateMachine");
    Check(safe.Contains("DUPLICATE_LOADED_ASSEMBLY"), "multiple loaded versions also produce a warning");
    Check(!safe.Contains("PrivatePerson") && !safe.Contains("PrivateMachine"), "known user and host tokens are removed from untrusted metadata");
    Check(!safe.Contains("hunter2") && !safe.Contains("example.com") && !safe.Contains("https://") && !safe.Contains("Bearer value") && !safe.Contains("github_pat_value"), "path email and credential-shaped metadata are redacted");
    Check(!safe.Contains("SECRET=hidden"), "untrusted metadata cannot inject additional report lines");
    Check(CompatibilityDiagnostics.SafeLabel("mod\u2028extra") == "[redacted]" &&
        CompatibilityDiagnostics.SafeLabel("mod\u202Ehidden") == "[redacted]", "Unicode line separators and direction controls are rejected");

    string config = Path.Combine(fixture, "config");
    Check(CompatibilityDiagnostics.TrySave(config, report, out string name, out string error) && error.Length == 0, "report saved below supplied configuration root");
    Check(Path.GetFileName(name) == name && name.EndsWith(".txt", StringComparison.Ordinal), "success returns a basename only");
    string saved = Path.Combine(config, "VeteranSoldiers", "diagnostics", name);
    Check(File.ReadAllText(saved) == report, "written report matches the reviewed snapshot");
    Check(!Directory.GetFiles(Path.GetDirectoryName(saved)!, "*.tmp").Any(), "completed report leaves no temporary file");
    Check(CompatibilityDiagnostics.TrySave(config, report, out string second, out _) && second != name, "second explicit report preserves the previous report");
    string blocked = Path.Combine(fixture, "not-a-directory");
    File.WriteAllText(blocked, "keep");
    Check(!CompatibilityDiagnostics.TrySave(blocked, report, out string failedName, out string failure) && failedName.Length == 0 && failure == "IO_FAILURE", "write failure returns a fixed code without leaking a path");
    Check(File.ReadAllText(blocked) == "keep", "failed write preserves unrelated existing files");

    string many = Path.Combine(fixture, "bounded", "plugins");
    Directory.CreateDirectory(many);
    for (int i = 0; i < 520; i++) File.WriteAllText(Path.Combine(many, "fixture-" + i.ToString("D3") + ".dll"), "bad DLL");
    var bounded = CompatibilityDiagnostics.Capture(Path.GetDirectoryName(many)!, Array.Empty<Assembly>());
    Check(bounded.Files.Count <= 512 && (bounded.Issues.Contains("FILE_SCAN_LIMIT") || bounded.Issues.Contains("TIME_LIMIT")), "large installed-file set is bounded and reported incomplete");

    own.UnpatchSelf();
    var foreignOnly = CompatibilityDiagnostics.Capture(Path.Combine(fixture, "absent"), Array.Empty<Assembly>());
    Check(foreignOnly.Targets.Any(value => value.Patches.Any(patch => patch.Owner == "diagnostics.fixture.other")), "foreign patches to the mod assembly remain visible without our own Harmony owner on that method");
    foreign.UnpatchSelf();
    var empty = CompatibilityDiagnostics.Capture(Path.Combine(fixture, "absent"), Array.Empty<Assembly>());
    string emptyReport = CompatibilityDiagnostics.Render(empty);
    Check(emptyReport.Contains("NO_TARGETS_OBSERVED") && emptyReport.Contains("MARKER_NOT_OBSERVED"), "empty or disabled inspection is explicitly inconclusive");
    var clean = new DiagnosticSnapshot { InstrumentationPresent = true };
    clean.Targets.Add(new DiagnosticTarget("Fake.Target"));
    Check(CompatibilityDiagnostics.Render(clean).Contains("not a compatibility guarantee"), "clean fixture never emits an automatic compatible verdict");
    Console.WriteLine(checks + " diagnostics checks passed. File/reflection/Harmony fixtures only; no game run.");
}
finally { own.UnpatchSelf(); foreign.UnpatchSelf(); }

static class Target
{
    [MethodImpl(MethodImplOptions.NoInlining)] internal static int Run(int value) => value;
}
static class Hooks
{
    internal static void Before() { }
    internal static IEnumerable<CodeInstruction> PassThrough(IEnumerable<CodeInstruction> code) => code;
}
static class ConstructorTracker { internal static int Count; }
[Probe, BepInPlugin("diagnostics.fixture.plugin", "Fixture One", "1.0.0")]
sealed class PluginOne { static PluginOne() { ConstructorTracker.Count++; } }
[BepInPlugin("diagnostics.fixture.plugin", "Fixture Two", "2.0.0")]
sealed class PluginTwo { static PluginTwo() { ConstructorTracker.Count++; } }
sealed class ProbeAttribute : Attribute { public ProbeAttribute() { ConstructorTracker.Count++; } }

namespace VeteranSoldiers
{
    internal static class VeteranRuntime
    {
        internal const string Guid = "com.veteransoldiers.mod";
        internal const string Version = "fixture";
    }
}
