using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VeteranSoldiers.Patching;

internal static class TransactionTests
{
    public static void Run(string gameFile, ReaderParameters reader, AssemblyDefinition entities,
        AssemblyDefinition core, AssemblyDefinition runtime, Action<bool, string> check)
    {
        using (var incoming = AssemblyDefinition.ReadAssembly(gameFile, reader))
        {
            AddOtherModMember(incoming, "BeforeVeterans");
            string before = Hash(incoming);
            var current = incoming;
            bool accepted = AssemblyPatchTransaction.TryApply(ref current,
                candidate => CombatPatcher.Apply(candidate, entities, core, runtime), out var error);
            check(accepted && error == null, "transaction accepts supported assembly: " + error);
            try
            {
                check(!ReferenceEquals(incoming, current), "successful transaction replaces only the assembly reference");
                check(before == Hash(incoming), "successful transaction leaves incoming assembly semantics unchanged");
                check(current.MainModule.GetType("OtherMod.BeforeVeterans") != null, "earlier unrelated preloader change retained");
                check(current.MainModule.GetType(CombatPatcher.MarkerName) != null, "committed assembly has success marker");
                AddOtherModMember(current, "AfterVeterans");
                check(current.MainModule.GetType("OtherMod.AfterVeterans") != null, "subsequent preloader can modify committed assembly");
                string committed = Hash(current);
                var same = current;
                check(!AssemblyPatchTransaction.TryApply(ref current,
                    candidate => CombatPatcher.Apply(candidate, entities, core, runtime), out error), "duplicate transaction rejected without escaping exception");
                check(error is InvalidOperationException && ReferenceEquals(same, current) && committed == Hash(current),
                    "duplicate rejection preserves existing instrumentation and other mods");
            }
            finally { if (!ReferenceEquals(incoming, current)) current.Dispose(); }
        }

        foreach (string conflict in new[] { "emitter", "scheduler", "identity" })
        {
            using var incoming = AssemblyDefinition.ReadAssembly(gameFile, reader);
            AddOtherModMember(incoming, "BeforeVeterans");
            if (conflict == "identity") incoming.MainModule.Mvid = Guid.NewGuid();
            else
            {
                var job = incoming.MainModule.GetType("Systems.FightSystems.DealMeleeDamageSystem")
                    .NestedTypes.Single(type => type.Name == "DealMeleeDamageJob");
                var method = job.Methods.Single(item => item.Name == (conflict == "emitter" ? "ApplyDamage" : "ScheduleParallel"));
                method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            }
            string before = Hash(incoming);
            var current = incoming;
            // BepInEx invalidates all patches for this target only when the delegate throws.
            // This deliberately follows that boundary without loading game code into the CLR.
            bool loaderInvalidatedTarget = false;
            bool subsequentPatcherRan = false;
            bool accepted = true;
            Exception? rejection = null;
            try
            {
                accepted = AssemblyPatchTransaction.TryApply(ref current,
                    candidate => CombatPatcher.Apply(candidate, entities, core, runtime), out rejection);
            }
            catch { loaderInvalidatedTarget = true; }
            check(!accepted && rejection is InvalidOperationException, conflict + " rejected without relaxing exact-build checks");
            check(ReferenceEquals(current, incoming) && before == Hash(current), conflict + " rejection preserves incoming reference and serialized content");
            check(current.MainModule.GetType(CombatPatcher.MarkerName) == null, conflict + " rejection does not enable runtime");
            if (!loaderInvalidatedTarget)
            {
                AddOtherModMember(current, "AfterVeterans");
                subsequentPatcherRan = true;
            }
            check(subsequentPatcherRan && current.MainModule.GetType("OtherMod.BeforeVeterans") != null,
                conflict + " rejection preserves earlier mod and permits subsequent preloader");
        }

        foreach (bool invalidOutput in new[] { false, true })
        {
            using var incoming = AssemblyDefinition.ReadAssembly(gameFile, reader);
            AddOtherModMember(incoming, "BeforeVeterans");
            string before = Hash(incoming);
            var current = incoming;
            bool accepted = AssemblyPatchTransaction.TryApply(ref current, candidate => {
                CombatPatcher.Apply(candidate, entities, core, runtime);
                if (!invalidOutput) throw new InvalidOperationException("Injected failure after all candidate mutations.");
                var bridgeCall = candidate.MainModule.GetType("Systems.FightSystems.DealMeleeDamageSystem")
                    .NestedTypes.Single(type => type.Name == "DealMeleeDamageJob")
                    .Methods.Single(method => method.Name == "ApplyDamage").Body.Instructions
                    .Single(instruction => instruction.Operand is MethodReference method && method.Name == CombatPatcher.PreparedTagName);
                bridgeCall.Operand = "invalid call operand"; // Must fail Cecil serialization before commit.
            }, out var error);
            string stage = invalidOutput ? "serialization failure" : "late patch failure";
            check(!accepted && error != null, stage + " is contained");
            check(ReferenceEquals(current, incoming) && before == Hash(current), stage + " rolls back all candidate changes");
            check(current.MainModule.GetType(CombatPatcher.MarkerName) == null
                && !current.MainModule.AssemblyReferences.Any(reference => reference.Name == "VeteranSoldiers"),
                stage + " leaks neither marker nor runtime reference");
        }
    }

    private static void AddOtherModMember(AssemblyDefinition assembly, string name)
    {
        var module = assembly.MainModule;
        var type = new TypeDefinition("OtherMod", name,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        var method = new MethodDefinition("GetSentinel", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 731));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        module.Types.Add(type);
    }

    private static string Hash(AssemblyDefinition assembly)
    {
        using var bytes = new MemoryStream();
        assembly.Write(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
