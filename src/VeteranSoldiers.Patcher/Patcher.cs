using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace VeteranSoldiers.Patching
{
    /// <summary>BepInEx preloader contract. Never loads game/runtime types into the CLR.</summary>
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs { get { yield return "DNO.Main.dll"; } }

        public static void Patch(ref AssemblyDefinition assembly)
        {
            var log = Logger.CreateLogSource("VeteranSoldiers.Patcher");
            try
            {
                if (!PatcherConfiguration.IsEnabled(Path.Combine(Paths.ConfigPath, PatcherConfiguration.FileName)))
                {
                    log.LogInfo("General.Enabled=false: combat instrumentation skipped; original Burst scheduling is unchanged.");
                    return;
                }
                string gameFile = Path.Combine(Paths.ManagedPath, "DNO.Main.dll");
                string runtimeFile = Path.Combine(Paths.BepInExRootPath, "ecs_plugins", "VeteranSoldiers.dll");
                if (!File.Exists(runtimeFile))
                    throw new InvalidOperationException("VeteranSoldiers.dll is missing from BepInEx/ecs_plugins; combat instrumentation was not installed.");
                CombatPatcher.ValidateFile(gameFile);
                using (var resolver = new DefaultAssemblyResolver())
                {
                    resolver.AddSearchDirectory(Paths.ManagedPath);
                    resolver.AddSearchDirectory(Path.GetDirectoryName(runtimeFile));
                    var read = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };
                    using (var entities = AssemblyDefinition.ReadAssembly(Path.Combine(Paths.ManagedPath, "Unity.Entities.dll"), read))
                    using (var core = AssemblyDefinition.ReadAssembly(Path.Combine(Paths.ManagedPath, "UnityEngine.CoreModule.dll"), read))
                    using (var runtime = AssemblyDefinition.ReadAssembly(runtimeFile, read))
                    {
                        if (!AssemblyPatchTransaction.TryApply(ref assembly,
                            candidate => CombatPatcher.Apply(candidate, entities, core, runtime), out var error))
                        {
                            log.LogError("Combat instrumentation skipped safely; incoming assembly and other preloader changes retained. " + error);
                            return;
                        }
                    }
                }
                log.LogWarning("Attribution installed in memory: 3 damage routes and exact native healer requests. Melee, projectile, direct-control melee and HealSkillApply jobs run managed on the main thread. Other Burst jobs are unchanged. Performance requires in-game measurement.");
            }
            catch (Exception exception)
            {
                log.LogError("Combat instrumentation skipped safely before commit; incoming assembly retained. " + exception);
                // BepInEx 5 invalidates the entire target if a patcher throws, discarding
                // earlier patches and skipping later ones. Reject only our instrumentation.
                // Runtime remains disabled when its success marker is absent.
            }
        }
    }
}
