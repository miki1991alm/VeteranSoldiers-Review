using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;

namespace VeteranSoldiers;

/// <summary>A local, explicit snapshot. Never called from a frame callback or a combat hook.</summary>
internal static class CompatibilityDiagnostics
{
    private static int writing;
    private static DateTime nextRequest;
    private const int MaxFiles = 512, MaxDirectories = 256, MaxDepth = 6, MaxTypes = 20000;
    private const long MaxHashBytes = 64L * 1024 * 1024;
    private const long MaxTotalHashBytes = 128L * 1024 * 1024;
    private static readonly string[] ImportantAssemblies =
        { "VeteranSoldiers", "VeteranSoldiers.Patcher", "BepInEx", "BepInEx.Preloader", "0Harmony", "0Harmony20", "DNO.Main", "Unity.Entities" };
    private static readonly string[][] CombatMethods =
    {
        new[] { "Systems.FightSystems.DealMeleeDamageSystem+DealMeleeDamageJob", "ApplyDamage", "ScheduleParallel" },
        new[] { "Systems.FightSystems.ProjectileTrackSystem+ProjectileFlyTrackJob", "DoDamage", "ScheduleParallel" },
        new[] { "Systems.UnitDirectControl.UnitControlMeleeAttackSystem+UnitControlMeleeAttackJob", "ApplyDamage", "ScheduleJob", "Execute" }
    };

    // The caller owns shortcut handling, localization and the success notification.
    // Neither return value contains a path or an exception message.
    internal static bool TryWriteReport(out string fileName, out string failureCode)
    {
        fileName = failureCode = string.Empty;
        if (Interlocked.CompareExchange(ref writing, 1, 0) != 0) { failureCode = "BUSY"; return false; }
        try
        {
            if (DateTime.UtcNow < nextRequest) { failureCode = "COOLDOWN"; return false; }
            nextRequest = DateTime.UtcNow.AddSeconds(5);
            var snapshot = Capture(Paths.BepInExRootPath, AppDomain.CurrentDomain.GetAssemblies());
            return TrySave(Paths.ConfigPath, Render(snapshot), out fileName, out failureCode);
        }
        catch (Exception error) { failureCode = ErrorCode(error); return false; }
        finally { Volatile.Write(ref writing, 0); }
    }

    internal static DiagnosticSnapshot Capture(string loaderRoot, IEnumerable<Assembly> loadedAssemblies,
        IEnumerable<MethodBase>? extraWatchedMethods = null)
    {
        var result = new DiagnosticSnapshot();
        var timer = Stopwatch.StartNew();
        InspectFiles(loaderRoot, result, timer);
        var assemblies = loadedAssemblies.Distinct().ToArray();
        var interesting = new HashSet<string>(ImportantAssemblies, StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files) interesting.Add(file.Name);
        var watched = new HashSet<MethodBase>();
        try
        {
            foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info != null && info.Owners.Count > 0 && (info.Owners.Contains(VeteranRuntime.Guid) ||
                    method.DeclaringType?.Assembly == typeof(VeteranRuntime).Assembly)) watched.Add(method);
            }
        }
        catch (Exception error) { result.Issues.Add("HARMONY_ENUMERATION_" + ErrorCode(error)); }
        if (extraWatchedMethods != null) foreach (var method in extraWatchedMethods) watched.Add(method);

        foreach (var assembly in assemblies)
        {
            if (timer.Elapsed.TotalSeconds > 5) { result.Issues.Add("TIME_LIMIT"); break; }
            try
            {
                var name = assembly.GetName();
                if (!interesting.Contains(name.Name ?? string.Empty)) continue;
                result.Loaded.Add(new DiagnosticAssembly("loaded#" + (result.Loaded.Count + 1),
                    name.Name ?? "unknown", name.Version?.ToString() ?? "unknown")
                    { Mvid = assembly.ManifestModule.ModuleVersionId.ToString("D") });
                if (name.Name == "DNO.Main")
                {
                    result.InstrumentationPresent = assembly.GetType("VeteranSoldiers.InstrumentationMarker", false) != null;
                    foreach (var group in CombatMethods)
                    {
                        var type = assembly.GetType(group[0], false);
                        if (type == null) { result.Issues.Add("COMBAT_TYPE_UNAVAILABLE"); continue; }
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (group.Skip(1).Contains(method.Name)) watched.Add(method);
                    }
                }
                // Attribute data is inspected on assemblies already loaded by the game.
                // This does not instantiate attributes, plugins or their static constructors.
                foreach (var type in ReadTypes(assembly, result).Take(MaxTypes))
                {
                    if (timer.Elapsed.TotalSeconds > 5) { result.Issues.Add("TIME_LIMIT"); break; }
                    try
                    {
                        foreach (var attribute in CustomAttributeData.GetCustomAttributes(type))
                        {
                            if (attribute.AttributeType.FullName != "BepInEx.BepInPlugin" || attribute.ConstructorArguments.Count < 3) continue;
                            result.Plugins.Add(new DiagnosticPlugin(name.Name ?? "unknown",
                                attribute.ConstructorArguments[0].Value as string ?? "unknown",
                                attribute.ConstructorArguments[2].Value as string ?? "unknown"));
                        }
                    }
                    catch (Exception error) { result.Issues.Add("PLUGIN_METADATA_" + ErrorCode(error)); }
                }
            }
            catch (Exception error) { result.Issues.Add("LOADED_METADATA_" + ErrorCode(error)); }
        }
        foreach (var method in watched.OrderBy(MethodLabel, StringComparer.Ordinal))
        {
            var target = new DiagnosticTarget(MethodLabel(method));
            result.Targets.Add(target);
            try
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                AddPatches(target, "prefix", info.Prefixes);
                AddPatches(target, "postfix", info.Postfixes);
                AddPatches(target, "transpiler", info.Transpilers);
                AddPatches(target, "finalizer", info.Finalizers);
            }
            catch (Exception error) { result.Issues.Add("PATCH_METADATA_" + ErrorCode(error)); }
        }
        return result;
    }

    private static IEnumerable<Type> ReadTypes(Assembly assembly, DiagnosticSnapshot result)
    {
        try
        {
            var types = assembly.GetTypes();
            if (types.Length > MaxTypes) result.Issues.Add("TYPE_SCAN_LIMIT");
            return types;
        }
        catch (ReflectionTypeLoadException error)
        {
            result.Issues.Add("PARTIAL_TYPE_METADATA");
            return error.Types.Where(type => type != null).Cast<Type>();
        }
        catch (Exception error) { result.Issues.Add("TYPE_METADATA_" + ErrorCode(error)); return Array.Empty<Type>(); }
    }

    private static void AddPatches(DiagnosticTarget target, string kind, IEnumerable<Patch> patches)
    {
        foreach (var patch in patches)
            target.Patches.Add(new DiagnosticPatch(kind, patch.owner, patch.priority, patch.index,
                patch.before ?? Array.Empty<string>(), patch.after ?? Array.Empty<string>(),
                patch.PatchMethod?.DeclaringType?.Assembly.GetName().Name ?? "unknown"));
    }

    private static string MethodLabel(MethodBase method) => (method.DeclaringType?.FullName ?? "unknown") + "." + method.Name +
        "(" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")";

    private static void InspectFiles(string loaderRoot, DiagnosticSnapshot result, Stopwatch timer)
    {
        int directories = 0;
        long hashedBytes = 0;
        foreach (string area in new[] { "ecs_plugins", "plugins", "patchers" })
        {
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(Path.Combine(loaderRoot, area), 0));
            while (queue.Count > 0)
            {
                if (timer.Elapsed.TotalSeconds > 5) { result.Issues.Add("TIME_LIMIT"); return; }
                if (result.Files.Count >= MaxFiles || directories >= MaxDirectories)
                { result.Issues.Add("FILE_SCAN_LIMIT"); return; }
                var current = queue.Dequeue();
                if (!Directory.Exists(current.Key)) continue;
                directories++;
                try
                {
                    if (IsReparsePoint(current.Key)) { result.Issues.Add("LINK_SKIPPED"); continue; }
                    foreach (var file in Directory.EnumerateFiles(current.Key, "*.dll").Take(MaxFiles + 1).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        if (timer.Elapsed.TotalSeconds > 5) { result.Issues.Add("TIME_LIMIT"); return; }
                        if (result.Files.Count >= MaxFiles) { result.Issues.Add("FILE_SCAN_LIMIT"); return; }
                        if (IsReparsePoint(file)) { result.Issues.Add("LINK_SKIPPED"); continue; }
                        var entry = new DiagnosticAssembly(area + "#" + (result.Files.Count + 1), "unreadable", "unknown");
                        result.Files.Add(entry);
                        try
                        {
                            // Metadata only: never Assembly.Load/LoadFrom, nor plugin execution.
                            var name = AssemblyName.GetAssemblyName(file);
                            entry.Name = name.Name ?? "unknown";
                            entry.Version = name.Version?.ToString() ?? "unknown";
                            long size = new FileInfo(file).Length;
                            if (size > MaxHashBytes || hashedBytes + size > MaxTotalHashBytes) result.Issues.Add("HASH_SIZE_LIMIT");
                            else
                            {
                                hashedBytes += size;
                                using (var stream = File.OpenRead(file))
                                using (var hash = SHA256.Create())
                                    entry.Hash = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty);
                            }
                        }
                        catch (Exception error) { result.Issues.Add("FILE_METADATA_" + ErrorCode(error)); }
                    }
                    foreach (var directory in Directory.EnumerateDirectories(current.Key).Take(MaxDirectories + 1).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        if (current.Value >= MaxDepth) { result.Issues.Add("DIRECTORY_DEPTH_LIMIT"); break; }
                        if (queue.Count + directories >= MaxDirectories) { result.Issues.Add("DIRECTORY_COUNT_LIMIT"); break; }
                        queue.Enqueue(new KeyValuePair<string, int>(directory, current.Value + 1));
                    }
                }
                catch (Exception error) { result.Issues.Add("DIRECTORY_SCAN_" + ErrorCode(error)); }
            }
        }
    }

    internal static string Render(DiagnosticSnapshot snapshot, params string[] additionalPrivateTokens)
    {
        var privateTokens = new[] { Environment.UserName, Environment.MachineName }.Concat(additionalPrivateTokens).ToArray();
        string Safe(string? value) => SafeLabel(value, privateTokens);
        var text = new StringBuilder();
        text.AppendLine("Veteran Soldiers compatibility snapshot v1");
        text.AppendLine("UTC: " + snapshot.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        text.AppendLine("Mod version: " + Safe(VeteranRuntime.Version));
        text.AppendLine("Scope: explicit local request; no network, configuration values, save data, file paths or exception messages.");
        text.AppendLine("Installed DLL metadata and loaded plugin attributes are distinct; neither proves a plugin initialized correctly.");
        text.AppendLine("No warnings does NOT mean compatible. Preloader history, native detours, other Harmony versions and runtime stat writes may be invisible.");
        text.AppendLine("Priority and before/after values are declarations, not a guaranteed effective execution order.");
        text.AppendLine("Instrumentation marker: " + (snapshot.InstrumentationPresent ? "present (not a post-patch integrity check)" : "not observed"));
        text.AppendLine("Scan bounds: 512 DLLs; 256 directories; depth 6; 64 MiB hash per file / 128 MiB total; 20000 types per relevant loaded assembly; soft 5-second inspection budget.");
        text.AppendLine();
        text.AppendLine("INSTALLED DLL METADATA (logical IDs, not file locations)");
        foreach (var entry in snapshot.Files)
            text.AppendLine(Safe(entry.Id) + " | " + Safe(entry.Name) + " | " + Safe(entry.Version) + " | SHA256 " + Safe(entry.Hash));
        text.AppendLine();
        text.AppendLine("RELEVANT LOADED ASSEMBLIES");
        foreach (var entry in snapshot.Loaded)
            text.AppendLine(Safe(entry.Id) + " | " + Safe(entry.Name) + " | " + Safe(entry.Version) + " | MVID " + Safe(entry.Mvid));
        text.AppendLine();
        text.AppendLine("PLUGIN ATTRIBUTE METADATA IN LOADED ASSEMBLIES (includes ECS; not an initialization result)");
        foreach (var plugin in snapshot.Plugins.OrderBy(value => value.Guid, StringComparer.Ordinal))
            text.AppendLine(Safe(plugin.Guid) + " | version " + Safe(plugin.Version) + " | assembly " + Safe(plugin.Assembly));
        text.AppendLine();
        text.AppendLine("WATCHED METHODS (our registered Harmony targets, patched mod methods and known combat emitters/schedulers)");
        foreach (var target in snapshot.Targets)
        {
            text.AppendLine(Safe(target.Name));
            if (target.Patches.Count == 0) text.AppendLine("  No Harmony patches visible through this Harmony library.");
            foreach (var patch in target.Patches.OrderBy(value => value.Kind, StringComparer.Ordinal).ThenByDescending(value => value.Priority).ThenBy(value => value.Index))
                text.AppendLine("  " + Safe(patch.Kind) + " owner=" + Safe(patch.Owner) + " priority=" + patch.Priority.ToString(CultureInfo.InvariantCulture) +
                    " index=" + patch.Index.ToString(CultureInfo.InvariantCulture) + " assembly=" + Safe(patch.Assembly) +
                    " before=[" + string.Join(",", patch.Before.Select(Safe)) + "] after=[" + string.Join(",", patch.After.Select(Safe)) + "]");
        }
        text.AppendLine();
        text.AppendLine("REVIEW WARNINGS (potential overlap, not a compatibility verdict)");
        int warningCount = 0;
        void Warning(string code, string detail) { warningCount++; text.AppendLine(code + " | " + detail); }
        foreach (var group in snapshot.Files.Where(value => value.Name != "unreadable").GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            Warning("DUPLICATE_INSTALLED_ASSEMBLY", Safe(group.Key) + " | " + string.Join(",", group.Select(value => Safe(value.Id))) + " | copies=" + group.Count());
        foreach (var group in snapshot.Loaded.GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            Warning("DUPLICATE_LOADED_ASSEMBLY", Safe(group.Key) + " | instances=" + group.Count());
        foreach (var group in snapshot.Plugins.GroupBy(value => value.Guid, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            Warning("DUPLICATE_PLUGIN_GUID", Safe(group.Key) + " | declarations=" + group.Count());
        foreach (var target in snapshot.Targets)
            foreach (var patch in target.Patches.Where(value => value.Owner != VeteranRuntime.Guid))
                Warning(patch.Kind == "transpiler" ? "FOREIGN_TRANSPILER" : "SHARED_METHOD", Safe(target.Name) + " | " + Safe(patch.Kind) + " | " + Safe(patch.Owner));
        if (snapshot.Targets.Count == 0) Warning("NO_TARGETS_OBSERVED", "Hook inspection was empty; initialization may be incomplete or disabled.");
        if (!snapshot.InstrumentationPresent) Warning("MARKER_NOT_OBSERVED", "Combat instrumentation was not confirmed in this snapshot.");
        foreach (var issue in snapshot.Issues.OrderBy(value => value, StringComparer.Ordinal)) Warning("PARTIAL_INSPECTION", Safe(issue));
        if (warningCount == 0) text.AppendLine("No listed overlaps observed. This is not a compatibility guarantee.");
        text.AppendLine("Review duplicate DLLs with the game closed. Preserve saves/configuration; do not remove shared loader files.");
        return text.ToString();
    }

    internal static string SafeLabel(string? value, params string[] privateTokens)
    {
        if (value == null || value.Length == 0) return "none";
        if (value.Length > 240 || value.Any(character => !char.IsLetterOrDigit(character) && " ._-+[]()<>*&,$`#".IndexOf(character) < 0)) return "[redacted]";
        foreach (var token in privateTokens)
            if (!string.IsNullOrEmpty(token) && token.Length >= 3 && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return "[redacted]";
        foreach (string token in new[] { "password", "secret", "token", "bearer", "sk-", "ghp_", "github_pat_", "eyJ" })
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return "[redacted]";
        return value;
    }

    internal static bool TrySave(string configRoot, string report, out string fileName, out string failureCode)
    {
        fileName = failureCode = string.Empty;
        string? temporary = null;
        try
        {
            var folder = Path.GetFullPath(configRoot);
            foreach (string part in new[] { string.Empty, "VeteranSoldiers", "diagnostics" })
            {
                if (part.Length > 0) folder = Path.Combine(folder, part);
                if (Directory.Exists(folder) && IsReparsePoint(folder)) { failureCode = "LINKED_OUTPUT_DIRECTORY"; return false; }
                Directory.CreateDirectory(folder);
            }
            var name = "compatibility-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
            temporary = Path.Combine(folder, name + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(report);
            File.Move(temporary, Path.Combine(folder, name));
            temporary = null;
            fileName = name;
            return true;
        }
        catch (Exception error) { failureCode = ErrorCode(error); return false; }
        finally { if (temporary != null) try { File.Delete(temporary); } catch { /* Best-effort removal of our own incomplete file only. */ } }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string ErrorCode(Exception error) => error is UnauthorizedAccessException ? "ACCESS_DENIED" :
        error is IOException ? "IO_FAILURE" : error is BadImageFormatException ? "NOT_MANAGED_DLL" : "INSPECTION_FAILED";
}

internal sealed class DiagnosticSnapshot
{
    internal DateTime CreatedUtc = DateTime.UtcNow;
    internal bool InstrumentationPresent;
    internal readonly List<DiagnosticAssembly> Files = new List<DiagnosticAssembly>(), Loaded = new List<DiagnosticAssembly>();
    internal readonly List<DiagnosticPlugin> Plugins = new List<DiagnosticPlugin>();
    internal readonly List<DiagnosticTarget> Targets = new List<DiagnosticTarget>();
    internal readonly HashSet<string> Issues = new HashSet<string>(StringComparer.Ordinal);
}

internal sealed class DiagnosticAssembly
{
    internal string Id, Name, Version, Hash = "not recorded", Mvid = "not recorded";
    internal DiagnosticAssembly(string id, string name, string version) { Id = id; Name = name; Version = version; }
}

internal sealed class DiagnosticPlugin
{
    internal string Assembly, Guid, Version;
    internal DiagnosticPlugin(string assembly, string guid, string version) { Assembly = assembly; Guid = guid; Version = version; }
}

internal sealed class DiagnosticTarget
{
    internal string Name;
    internal readonly List<DiagnosticPatch> Patches = new List<DiagnosticPatch>();
    internal DiagnosticTarget(string name) { Name = name; }
}

internal sealed class DiagnosticPatch
{
    internal string Kind, Owner, Assembly;
    internal int Priority, Index;
    internal string[] Before, After;
    internal DiagnosticPatch(string kind, string owner, int priority, int index, string[] before, string[] after, string assembly)
    { Kind = kind; Owner = owner; Priority = priority; Index = index; Before = before; After = after; Assembly = assembly; }
}
