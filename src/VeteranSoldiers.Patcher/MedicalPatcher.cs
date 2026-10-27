using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VeteranSoldiers.Patching;

/// <summary>Attribution for the exact native healer request; never infers healing from proximity.</summary>
internal sealed class MedicalPatcher
{
    internal const string SystemName = "Systems.FightSystems.SkillSystems.HealSkillApplySystem";
    private readonly ModuleDefinition module;
    private readonly TypeDefinition job;
    private readonly MethodDefinition body, run, complete;
    private readonly Instruction create, set, schedule;
    private readonly FieldDefinition buffer;
    private readonly PreparedRequestArchetype archetype;

    private MedicalPatcher(ModuleDefinition module, TypeDefinition job, MethodDefinition body,
        MethodDefinition run, MethodDefinition complete, Instruction create, Instruction set,
        Instruction schedule, FieldDefinition buffer, PreparedRequestArchetype archetype)
    {
        this.module = module; this.job = job; this.body = body; this.run = run; this.complete = complete;
        this.create = create; this.set = set; this.schedule = schedule; this.buffer = buffer; this.archetype = archetype;
    }

    internal static MedicalPatcher Prepare(ModuleDefinition module, AssemblyDefinition entities, AssemblyDefinition core)
    {
        var system = module.GetType(SystemName) ?? throw new InvalidOperationException("Native healing system missing.");
        var job = system.NestedTypes.Single(t => t.Name == "HealSkillApplySystem_LambdaJob_0_Job");
        var body = job.Methods.Single(m => m.Name == "OriginalLambdaBody");
        var execute = job.Methods.Single(m => m.Name == "Execute");
        var dispatch = system.Methods.Single(m => m.Name == "HealSkillApplySystem_LambdaJob_0_Execute");
        var initialize = system.Methods.Single(m => m.Name == "OnCreateSimulation");
        CombatPatcher.ValidateFingerprint(body, "9BD2BE3824DAC6C38AD3834938F09476D74103490DEE72124C70B73255DF2CF6");
        CombatPatcher.ValidateFingerprint(execute, "A147CB81DAAD3DD0D9B7686EAD5CCB8FA40DE6BE61821737136EA24507BA3AA9");
        CombatPatcher.ValidateFingerprint(dispatch, "6007FC254A3D37E92F14199F25B8E59AEFE04E40FDC6400D51848DC5746381BC");
        CombatPatcher.ValidateFingerprint(initialize, "ADE8B53520D599C1C4CD79740FF2E2F45DBF2751A6764A0E999A8E03A5937DEE");
        if (job.Methods.Any(m => m.Name == CombatPatcher.WrapperName) || body.IsStatic ||
            body.Parameters[0].ParameterType.FullName != "Unity.Entities.Entity" ||
            body.Parameters[1].ParameterType.FullName != "System.Int32" || body.Body.ExceptionHandlers.Count != 0)
            throw new InvalidOperationException("Native healer identity/dispatch contract changed.");
        var create = body.Body.Instructions.Single(i => i.Operand is MethodReference m &&
            m.Name == "CreateEntity" && m.DeclaringType.FullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter");
        var set = body.Body.Instructions.Single(i => i.Operand is GenericInstanceMethod m && m.Name == "SetComponent" &&
            m.GenericArguments.Single().FullName == "Components.RawComponents.HealthChangeRequest");
        var schedule = dispatch.Body.Instructions.Single(i => i.Operand is GenericInstanceMethod m &&
            m.Name == "ScheduleParallel" && m.DeclaringType.FullName == "Unity.Entities.JobEntityBatchIndexExtensions");
        var scheduled = (GenericInstanceMethod)schedule.Operand;
        if (create.OpCode != OpCodes.Call || set.OpCode != OpCodes.Call || schedule.OpCode != OpCodes.Call ||
            scheduled.GenericArguments.Single().FullName != job.FullName || scheduled.Parameters.Count != 3 ||
            scheduled.ReturnType.FullName != "Unity.Jobs.JobHandle" ||
            body.Body.Instructions.IndexOf(set) <= body.Body.Instructions.IndexOf(create))
            throw new InvalidOperationException("Native healer request contract changed.");
        var run = entities.MainModule.GetType("Unity.Entities.JobEntityBatchIndexExtensions").Methods.Single(m =>
            m.Name == "RunWithoutJobs" && m.GenericParameters.Count == 1 && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.IsByReference);
        var calls = run.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        if (!run.IsPublic || !run.IsStatic || !calls.Any(m => m.Name == "RunWithoutJobsInternal") ||
            calls.Any(m => m.Name.Contains("Schedule") || m.Name.Contains("CreateJobReflectionData")))
            throw new InvalidOperationException("Native healer managed runner contract changed.");
        var complete = core.MainModule.GetType("Unity.Jobs.JobHandle").Methods.Single(m => m.Name == "Complete" && !m.IsStatic && m.Parameters.Count == 0);
        var buffer = job.Fields.Single(f => f.Name == "buffer" && f.FieldType.FullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter");
        return new MedicalPatcher(module, job, body, run, complete, create, set, schedule, buffer,
            PreparedRequestArchetype.Prepare(initialize, "_changeRequestArchetype"));
    }

    internal void Apply(MethodReference tag, TypeReference source)
    {
        // Duplicate the exact deferred entity on the evaluation stack; original request
        // creation, healing value, target and presentation commands remain unchanged.
        var request = new VariableDefinition(module.ImportReference(((MethodReference)create.Operand).ReturnType));
        body.Body.Variables.Add(request); body.Body.InitLocals = true;
        var il = body.Body.GetILProcessor();
        var duplicate = Instruction.Create(OpCodes.Dup);
        il.InsertAfter(create, duplicate);
        il.InsertAfter(duplicate, Instruction.Create(OpCodes.Stloc, request));
        var cursor = set;
        foreach (var instruction in new[] {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldflda, buffer),
            Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Ldloc, request),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, tag)
        }) { il.InsertAfter(cursor, instruction); cursor = instruction; }
        body.Body.MaxStackSize = Math.Max(body.Body.MaxStackSize, 10);
        ExpandShortBranches(body);
        archetype.Apply(source);
        var original = (GenericInstanceMethod)schedule.Operand;
        var handle = module.ImportReference(original.ReturnType);
        var wrapper = new MethodDefinition(CombatPatcher.WrapperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, handle);
        wrapper.Parameters.Add(new ParameterDefinition("job", ParameterAttributes.None, job));
        wrapper.Parameters.Add(new ParameterDefinition("query", ParameterAttributes.None, module.ImportReference(original.Parameters[1].ParameterType)));
        wrapper.Parameters.Add(new ParameterDefinition("dependency", ParameterAttributes.None, handle));
        wrapper.Body.InitLocals = true;
        var result = new VariableDefinition(handle); wrapper.Body.Variables.Add(result);
        var managed = new GenericInstanceMethod(module.ImportReference(run)); managed.GenericArguments.Add(job);
        var runner = wrapper.Body.GetILProcessor();
        runner.Append(Instruction.Create(OpCodes.Ldarga, wrapper.Parameters[2]));
        runner.Append(Instruction.Create(OpCodes.Call, module.ImportReference(complete)));
        runner.Append(Instruction.Create(OpCodes.Ldarga, wrapper.Parameters[0]));
        runner.Append(Instruction.Create(OpCodes.Ldarg_1));
        runner.Append(Instruction.Create(OpCodes.Call, managed));
        runner.Append(Instruction.Create(OpCodes.Ldloca, result));
        runner.Append(Instruction.Create(OpCodes.Initobj, handle));
        runner.Append(Instruction.Create(OpCodes.Ldloc, result));
        runner.Append(Instruction.Create(OpCodes.Ret));
        job.Methods.Add(wrapper); schedule.Operand = wrapper;
    }

    private static void ExpandShortBranches(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
                case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
                case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
                case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
                case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
                case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
                case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
                case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
                case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
                case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
                case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
                case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
                case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
                case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
            }
        }
    }
}
