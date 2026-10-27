using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VeteranSoldiers.Patching;

internal static class Program
{
    private static int assertions;

    private static int Main(string[] arguments)
    {
        try { Run(arguments); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Run(string[] arguments)
    {
        VerifyConfiguration();
        BridgeProtocolTests.Run(Check);
        string managed = arguments.Length > 0 ? arguments[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Diplomacy is Not an Option\Diplomacy is Not an Option_Data\Managed";
        string gameFile = Path.Combine(managed, "DNO.Main.dll");
        string beforeFileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameFile)));
        CombatPatcher.ValidateFile(gameFile);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var reader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };
        using var entities = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Unity.Entities.dll"), reader);
        using var core = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "UnityEngine.CoreModule.dll"), reader);
        using var runtime = arguments.Length > 1 ? AssemblyDefinition.ReadAssembly(arguments[1], reader) : BuildContract(entities);
        using (var game = AssemblyDefinition.ReadAssembly(gameFile, reader))
        {
            var originalBodies = Methods(game.MainModule).Where(method => method.HasBody).ToDictionary(method => method.MetadataToken.ToInt32(), BodyText);
            int originalBurst = BurstAttributeCount(game.MainModule);
            CombatPatcher.Apply(game, entities, core, runtime);
            Check(game.MainModule.GetType(CombatPatcher.MarkerName).Fields.Single(field => field.Name == "Version").Constant.Equals("1"), "success marker version");
            var changed = Methods(game.MainModule).Where(method => originalBodies.TryGetValue(method.MetadataToken.ToInt32(), out var body) && BodyText(method) != body).ToArray();
            Check(changed.Length == 12, "exactly twelve existing methods change");
            Check(changed.All(method => method.Name == "ApplyDamage" || method.Name == "DoDamage" || method.Name == "ScheduleParallel" || method.Name == "ScheduleJob" || method.Name == "OnCreateSimulation" || method.Name == "OriginalLambdaBody" || method.Name == "HealSkillApplySystem_LambdaJob_0_Execute"), "only four emitters, schedulers and request initializers change");
            Check(BurstAttributeCount(game.MainModule) == originalBurst, "all Burst attributes retained");
            var wrappers = Methods(game.MainModule).Where(method => method.Name == CombatPatcher.WrapperName).ToArray();
            Check(wrappers.Length == 4, "four managed runners including exact medical attribution");
            foreach (var wrapper in wrappers)
            {
                bool direct = wrapper.DeclaringType.Name == "UnitControlMeleeAttackJob";
                var calls = wrapper.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Call).Select(instruction => (MethodReference)instruction.Operand).ToArray();
                Check(calls.Length == 2 && calls[0].Name == "Complete" && calls[1].Name == (direct ? "Execute" : "RunWithoutJobs"), "complete dependency before direct managed execution");
                Check(direct ? calls[1].DeclaringType.FullName == wrapper.DeclaringType.FullName
                    : ((GenericInstanceMethod)calls[1]).GenericArguments.Single().FullName == wrapper.DeclaringType.FullName, "runner uses exact job type");
                Check(!calls.Any(method => method.Name.Contains("Schedule") || method.Name.Contains("ReflectionData")), "runner cannot resolve native Burst schedule");
                Check(wrapper.Body.Instructions.Last().OpCode == OpCodes.Ret, "runner returns a completed/default handle");
                VerifyStack(wrapper);
            }
            foreach (var method in changed.Where(method => method.Name == "ApplyDamage" || method.Name == "DoDamage"))
            {
                VerifyHook(method);
                VerifyStack(method);
            }
            foreach (var method in changed.Where(method => method.Name == "OnCreateSimulation"))
            {
                PreparedArchetypeTests.Verify(method, Check);
                VerifyStack(method);
            }
            var medicalBody = changed.Single(method => method.Name == "OriginalLambdaBody");
            VerifyMedicalHook(medicalBody);
            VerifyStack(medicalBody);
            using var bytes = new MemoryStream();
            game.Write(bytes);
            bytes.Position = 0;
            using var restored = AssemblyDefinition.ReadAssembly(bytes, reader);
            Check(restored.MainModule.GetType(CombatPatcher.MarkerName) != null, "Cecil round-trip preserves marker");
            Check(Methods(restored.MainModule).Count(method => method.Name == CombatPatcher.WrapperName) == 4, "Cecil round-trip preserves all four runners");
            var restoredMedical = Methods(restored.MainModule).Single(method => method.Name == "OriginalLambdaBody" && method.DeclaringType.Name == "HealSkillApplySystem_LambdaJob_0_Job");
            VerifyMedicalHook(restoredMedical); VerifyStack(restoredMedical);
            foreach (var method in Methods(restored.MainModule).Where(method => method.Name == CombatPatcher.WrapperName || (method.Name == "ApplyDamage" && (method.DeclaringType.Name == "DealMeleeDamageJob" || method.DeclaringType.Name == "UnitControlMeleeAttackJob")) || (method.Name == "DoDamage" && method.DeclaringType.Name == "ProjectileFlyTrackJob")))
                VerifyStack(method);
            foreach (var method in Methods(restored.MainModule).Where(method => method.Name == "OnCreateSimulation" &&
                (method.DeclaringType.Name == "DealMeleeDamageSystem" || method.DeclaringType.Name == "ProjectileTrackSystem" || method.DeclaringType.Name == "UnitControlMeleeAttackSystem")))
            {
                PreparedArchetypeTests.Verify(method, Check);
                VerifyStack(method);
            }
            ExpectRejectedWithoutMutation(game, entities, core, runtime, "duplicate patch rejected");
        }
        using (var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader))
        {
            changedGame.MainModule.Mvid = Guid.NewGuid();
            ExpectRejectedWithoutMutation(changedGame, entities, core, runtime, "unknown game identity rejected");
        }
        using (var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader))
        {
            var method = Methods(changedGame.MainModule).Single(candidate => candidate.Name == "ApplyDamage" && candidate.DeclaringType.Name == "DealMeleeDamageJob");
            method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            ExpectRejectedWithoutMutation(changedGame, entities, core, runtime, "conflicting emitter patch rejected before mutation");
        }
        using (var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader))
        {
            var method = Methods(changedGame.MainModule).Single(candidate => candidate.Name == "Execute" && candidate.DeclaringType.Name == "UnitControlMeleeAttackJob");
            method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            ExpectRejectedWithoutMutation(changedGame, entities, core, runtime, "conflicting direct-control execution rejected before mutation");
        }
        using (var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader))
        using (var badRuntime = BuildContract(entities))
        {
            badRuntime.MainModule.GetType("VeteranSoldiers.CombatBridge").Methods.Single(method => method.Name == CombatPatcher.PreparedTagName).Parameters.RemoveAt(3);
            ExpectRejectedWithoutMutation(changedGame, entities, core, badRuntime, "wrong runtime contract rejected before mutation");
        }
        using (var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader))
        using (var badRuntime = BuildContract(entities))
        {
            badRuntime.MainModule.GetType("VeteranSoldiers.CombatBridge").Methods.Single(method => method.Name == CombatPatcher.PreparedDirectTagName).Parameters.RemoveAt(2);
            ExpectRejectedWithoutMutation(changedGame, entities, core, badRuntime, "wrong direct-control runtime contract rejected before mutation");
        }
        foreach (string system in new[] { "Systems.FightSystems.DealMeleeDamageSystem", "Systems.FightSystems.ProjectileTrackSystem", "Systems.UnitDirectControl.UnitControlMeleeAttackSystem", "Systems.FightSystems.SkillSystems.HealSkillApplySystem" })
        {
            using var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader);
            var method = changedGame.MainModule.GetType(system).Methods.Single(candidate => candidate.Name == "OnCreateSimulation");
            method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            ExpectRejectedWithoutMutation(changedGame, entities, core, runtime, "conflicting request initializer rejected: " + system);
        }
        foreach (string methodName in new[] { "OriginalLambdaBody", "Execute", "HealSkillApplySystem_LambdaJob_0_Execute" })
        {
            using var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader);
            var method = Methods(changedGame.MainModule).Single(candidate => candidate.Name == methodName && candidate.DeclaringType.Name.StartsWith("HealSkillApplySystem", StringComparison.Ordinal));
            method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            ExpectRejectedWithoutMutation(changedGame, entities, core, runtime, "conflicting medical method rejected before any mutation: " + methodName);
        }
        foreach (string defect in new[] { "old-runtime", "source-layout" })
        {
            using var changedGame = AssemblyDefinition.ReadAssembly(gameFile, reader);
            using var badRuntime = BuildContract(entities);
            if (defect == "old-runtime")
                badRuntime.MainModule.GetType("VeteranSoldiers.CombatBridge").Methods.Single(method => method.Name == CombatPatcher.PreparedTagName).Name = "Tag";
            else badRuntime.MainModule.GetType("VeteranSoldiers.CombatSource").Fields.Single(field => field.Name == "Processed").FieldType = badRuntime.MainModule.TypeSystem.Int32;
            ExpectRejectedWithoutMutation(changedGame, entities, core, badRuntime, defect + " rejected before mutation");
        }
        TransactionTests.Run(gameFile, reader, entities, core, runtime, Check);
        Check(beforeFileHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(gameFile))), "original game assembly byte-for-byte unchanged");
        Console.WriteLine($"PASS: {assertions} patcher assertions; exact installed assembly, in-memory only. No Unity runtime or performance claim.");
    }

    private static IEnumerable<MethodDefinition> Methods(ModuleDefinition module) => Types(module.Types).SelectMany(type => type.Methods);
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes)) yield return nested;
        }
    }
    private static string BodyText(MethodDefinition method) => method.HasBody ? string.Join("\n", method.Body.Instructions.Select(instruction => instruction.ToString())) : string.Empty;
    private static int BurstAttributeCount(ModuleDefinition module) => Types(module.Types).Sum(type => type.CustomAttributes.Count(attribute => attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute")
        + type.Methods.Sum(method => method.CustomAttributes.Count(attribute => attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute")));
    private static void Check(bool success, string description)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + description);
        assertions++;
    }
    private static string Snapshot(AssemblyDefinition assembly) => string.Join("\n", Methods(assembly.MainModule).Select(method => method.FullName + "\n" + BodyText(method)))
        + string.Join("\n", assembly.MainModule.AssemblyReferences.Select(reference => reference.FullName))
        + string.Join("\n", Types(assembly.MainModule.Types).Select(type => type.FullName));
    private static void ExpectRejectedWithoutMutation(AssemblyDefinition game, AssemblyDefinition entities, AssemblyDefinition core, AssemblyDefinition runtime, string description)
    {
        string before = Snapshot(game);
        bool rejected = false;
        try { CombatPatcher.Apply(game, entities, core, runtime); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, description);
        Check(before == Snapshot(game), description + " atomically");
    }

    private static AssemblyDefinition BuildContract(AssemblyDefinition entities)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("VeteranSoldiers", new Version(1, 0, 0, 0)), "VeteranSoldiers", ModuleKind.Dll);
        var module = assembly.MainModule;
        var bridge = new TypeDefinition("VeteranSoldiers", "CombatBridge", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract, module.TypeSystem.Object);
        module.Types.Add(bridge);
        var tag = new MethodDefinition(CombatPatcher.PreparedTagName, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        bridge.Methods.Add(tag);
        var writer = entities.MainModule.GetType("Unity.Entities.EntityCommandBuffer").NestedTypes.Single(type => type.Name == "ParallelWriter");
        var entity = entities.MainModule.GetType("Unity.Entities.Entity");
        tag.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.ImportReference(writer))));
        tag.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        tag.Parameters.Add(new ParameterDefinition(module.ImportReference(entity)));
        tag.Parameters.Add(new ParameterDefinition(module.ImportReference(entity)));
        tag.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var tagDirect = new MethodDefinition(CombatPatcher.PreparedDirectTagName, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        bridge.Methods.Add(tagDirect);
        tagDirect.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.ImportReference(entities.MainModule.GetType("Unity.Entities.EntityCommandBuffer")))));
        tagDirect.Parameters.Add(new ParameterDefinition(module.ImportReference(entity)));
        tagDirect.Parameters.Add(new ParameterDefinition(module.ImportReference(entity)));
        tagDirect.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var source = new TypeDefinition("VeteranSoldiers", "CombatSource", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.ImportReference(entity.BaseType));
        source.Interfaces.Add(new InterfaceImplementation(module.ImportReference(entities.MainModule.GetType("Unity.Entities.IComponentData"))));
        source.Fields.Add(new FieldDefinition("Source", FieldAttributes.Public, module.ImportReference(entity)));
        source.Fields.Add(new FieldDefinition("Processed", FieldAttributes.Public, module.TypeSystem.Byte));
        module.Types.Add(source);
        return assembly;
    }

    private static void VerifyHook(MethodDefinition method)
    {
        bool direct = method.DeclaringType.Name == "UnitControlMeleeAttackJob";
        string hook = direct ? CombatPatcher.PreparedDirectTagName : CombatPatcher.PreparedTagName;
        var instructions = method.Body.Instructions;
        int setIndex = instructions.Select((instruction, index) => (instruction, index)).Single(pair => pair.instruction.Operand is GenericInstanceMethod reference
            && reference.Name == "SetComponent" && reference.GenericArguments.Single().FullName == "Components.RawComponents.HealthChangeRequest").index;
        int tagIndex = instructions.Select((instruction, index) => (instruction, index)).Single(pair => pair.instruction.Operand is MethodReference reference
            && reference.DeclaringType.FullName == "VeteranSoldiers.CombatBridge" && reference.Name == hook).index;
        Check(tagIndex > setIndex && instructions[tagIndex + 1].OpCode == OpCodes.Ret, "tag occurs after the request is populated, before return");
        Check(instructions[setIndex + (direct ? 3 : 5)].Operand is VariableDefinition variable && variable.Index == (method.Name == "ApplyDamage" ? 0 : 2), "tag uses exact deferred request entity local");
        Check(instructions.Skip(setIndex + 1).Take(tagIndex - setIndex).Any(instruction => instruction.Operand is MemberReference reference
            && (direct ? reference.Name == "Entity" && reference.DeclaringType.Name == "UnitInfo" : reference.Name == "sourceEntity" || reference.Name == "op_Implicit")), "tag obtains actual attacker identity");
        Check(instructions.Count(instruction => instruction.Operand is MethodReference reference && reference.Name == hook) == 1, "one tag per emitted health request");
    }

    private static void VerifyMedicalHook(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        var create = instructions.Single(i => i.Operand is MethodReference m && m.Name == "CreateEntity");
        Check(create.Next.OpCode == OpCodes.Dup && create.Next.Next.OpCode == OpCodes.Stloc &&
            create.Next.Next.Operand is VariableDefinition v && v.VariableType.FullName == "Unity.Entities.Entity",
            "medical attribution captures exact deferred request entity without replacing original stack value");
        var stored = (VariableDefinition)create.Next.Next.Operand;
        var set = instructions.Single(i => i.Operand is GenericInstanceMethod m && m.Name == "SetComponent" &&
            m.GenericArguments.Single().FullName == "Components.RawComponents.HealthChangeRequest");
        int index = instructions.IndexOf(set);
        Check(instructions[index + 1].OpCode == OpCodes.Ldarg_0 && instructions[index + 2].Operand is FieldReference buffer && buffer.Name == "buffer" &&
            instructions[index + 3].OpCode == OpCodes.Ldarg_2 && instructions[index + 4].Operand == stored &&
            instructions[index + 5].OpCode == OpCodes.Ldarg_1 && instructions[index + 6].Operand is MethodReference tag && tag.Name == "TagPrepared",
            "medical tag uses original healer parameter, original sort key and exact request after request population");
        Check(instructions.Count(i => i.Operand is MethodReference m && m.Name == "TagPrepared") == 1,
            "exactly one medical attribution write per emitted native heal request");
        Check(instructions.Count(i => i.Operand is MethodReference m && m.Name == "ApplyWithoutSource") == 1,
            "medical request factory remains intact; no extra healing request is generated");
    }

    private static void VerifyConfiguration()
    {
        var cases = new (string text, bool enabled)[] {
            ("", true), ("[General]\nEnabled = false", false), ("[General]\nEnabled = TRUE", true),
            (" # comment\n [General] \n Enabled = False \n", false),
            ("[Other]\nEnabled = false", true), ("[General]\nOther = false", true),
            ("[general]\nEnabled = false", true), ("[General]\nenabled = false", true),
            ("[General]\n# Enabled = false", true), ("[General]\nEnabled = false\nEnabled = true", true),
            ("[General]\nEnabled = true\nEnabled = false", false),
            ("[General]\nEnabled = false\n[Other]\nEnabled = true", false),
            ("[General]\nEnabled = invalid", true), ("[General]\nEnabled = false\nEnabled = invalid", true)
        };
        foreach (var item in cases)
            using (var reader = new StringReader(item.text))
                Check(PatcherConfiguration.ReadEnabled(reader) == item.enabled, "preloader config matches BepInEx General.Enabled semantics");
        string missing = Path.Combine(AppContext.BaseDirectory, "nonexistent-veteran-test-" + Guid.NewGuid() + ".cfg");
        Check(PatcherConfiguration.IsEnabled(missing), "missing config enables by default");
        Check(!File.Exists(missing), "preloader never creates configuration");
        using var patcher = AssemblyDefinition.ReadAssembly(typeof(CombatPatcher).Assembly.Location);
        var entry = patcher.MainModule.GetType("VeteranSoldiers.Patching.Patcher").Methods.Single(method => method.Name == "Patch");
        var instructions = entry.Body.Instructions;
        Check(entry.Parameters.Single().ParameterType.IsByReference, "BepInEx entry uses transactional ref assembly contract");
        var rejectionHandler = entry.Body.ExceptionHandlers.Single(handler => handler.CatchType?.FullName == "System.Exception");
        int protectedStart = instructions.IndexOf(rejectionHandler.TryStart);
        int protectedEnd = instructions.IndexOf(rejectionHandler.TryEnd);
        Check(!instructions.Any(instruction => instruction.OpCode == OpCodes.Rethrow)
            && instructions.Where(instruction => instruction.OpCode == OpCodes.Throw).All(instruction =>
                instructions.IndexOf(instruction) >= protectedStart && instructions.IndexOf(instruction) < protectedEnd),
            "BepInEx entry catches explicit rejection instead of invalidating other mods");
        int guard = instructions.Select((instruction, index) => (instruction, index)).Single(pair => pair.instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == typeof(PatcherConfiguration).FullName && method.Name == "IsEnabled").index;
        int apply = instructions.Select((instruction, index) => (instruction, index)).Single(pair => pair.instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == typeof(AssemblyPatchTransaction).FullName && method.Name == "TryApply").index;
        Check(guard < apply && instructions[guard + 1].OpCode.FlowControl == FlowControl.Cond_Branch, "disabled config guarded before patch application");
        int branchTarget = instructions.IndexOf((Instruction)instructions[guard + 1].Operand);
        Check(instructions.Skip(guard + 2).Take(branchTarget - guard - 2).Any(instruction => instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S || instruction.OpCode == OpCodes.Ret), "disabled config exits preloader before loading or modifying assemblies");
    }

    // Control-flow stack-height verifier over the complete patched emitters and added runners.
    // Checks every branch, merge and return rather than only counting inserted instructions.
    private static void VerifyStack(MethodDefinition method)
    {
        var heights = new Dictionary<Instruction, int>();
        var pending = new Queue<(Instruction instruction, int height)>();
        pending.Enqueue((method.Body.Instructions[0], 0));
        while (pending.Count > 0)
        {
            var (instruction, height) = pending.Dequeue();
            if (heights.TryGetValue(instruction, out int prior))
            {
                Check(prior == height, "consistent IL stack at branch merge");
                continue;
            }
            heights.Add(instruction, height);
            int pop = PopCount(instruction, method);
            Check(height >= pop, "no IL stack underflow at " + instruction);
            int after = height - pop + PushCount(instruction);
            if (instruction.OpCode == OpCodes.Ret)
            {
                Check(after == 0, "empty IL stack at return");
                continue;
            }
            if (instruction.Operand is Instruction target)
            {
                pending.Enqueue((target, after));
                if (instruction.OpCode.FlowControl == FlowControl.Branch) continue;
            }
            if (instruction.Next != null) pending.Enqueue((instruction.Next, after));
        }
    }
    private static int PopCount(Instruction instruction, MethodDefinition enclosing)
    {
        if (instruction.OpCode == OpCodes.Ret) return enclosing.ReturnType.FullName == "System.Void" ? 0 : 1;
        if (instruction.Operand is MethodReference method && (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj))
            return method.Parameters.Count + ((method.HasThis && instruction.OpCode != OpCodes.Newobj) ? 1 : 0);
        return instruction.OpCode.StackBehaviourPop switch {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            _ => throw new InvalidOperationException("Unsupported verifier pop: " + instruction)
        };
    }
    private static int PushCount(Instruction instruction)
    {
        if (instruction.OpCode == OpCodes.Newobj) return 1;
        if (instruction.Operand is MethodReference method && (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt))
            return method.ReturnType.FullName == "System.Void" ? 0 : 1;
        return instruction.OpCode.StackBehaviourPush switch {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => throw new InvalidOperationException("Unsupported verifier push: " + instruction)
        };
    }
}
