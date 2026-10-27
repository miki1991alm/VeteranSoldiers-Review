using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VeteranSoldiers.Patching
{
    /// <summary>Exact-build, fail-closed instrumentation; modifies only the supplied in-memory module.</summary>
    public static class CombatPatcher
    {
        public const string SupportedGameSha256 = "098B19C4580FF1C5CE4AA62D78E04BEF0429C2632EC4C36E4299270EE3CA3987";
        public const string SupportedGameMvid = "e2e16580-5ac7-4466-ad7e-12c80ca01ad0";
        public const string MarkerName = "VeteranSoldiers.InstrumentationMarker";
        public const string WrapperName = "VeteranSoldiersRunManaged";
        public const string PreparedTagName = "TagPrepared";
        public const string PreparedDirectTagName = "TagDirectPrepared";

        public static void ValidateFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                string actual = Hex(sha.ComputeHash(stream));
                if (!string.Equals(actual, SupportedGameSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unsupported DNO.Main.dll SHA-256: " + actual + ". Supported build: dno_1.0.187_r / Steam 25478044.");
            }
        }

        public static void Apply(AssemblyDefinition game, AssemblyDefinition entities,
            AssemblyDefinition unityCore, AssemblyDefinition runtime)
        {
            var module = game.MainModule;
            if (game.Name.Name != "DNO.Main" || module.Mvid.ToString() != SupportedGameMvid)
                throw new InvalidOperationException("Unsupported game assembly identity.");
            if (module.GetType(MarkerName) != null)
                throw new InvalidOperationException("Combat instrumentation is already present; refusing duplicate hooks.");

            var melee = FindJob(module, "Systems.FightSystems.DealMeleeDamageSystem", "DealMeleeDamageJob");
            var projectile = FindJob(module, "Systems.FightSystems.ProjectileTrackSystem", "ProjectileFlyTrackJob");
            var direct = FindJob(module, "Systems.UnitDirectControl.UnitControlMeleeAttackSystem", "UnitControlMeleeAttackJob");
            var meleeDamage = FindMethod(melee, "ApplyDamage");
            var projectileDamage = FindMethod(projectile, "DoDamage");
            var directDamage = FindMethod(direct, "ApplyDamage");
            var meleeSchedule = FindMethod(melee, "ScheduleParallel");
            var projectileSchedule = FindMethod(projectile, "ScheduleParallel");
            var directSchedule = FindMethod(direct, "ScheduleJob");
            var directExecute = FindMethod(direct, "Execute");
            var meleeCreate = FindMethod(melee.DeclaringType, "OnCreateSimulation");
            var projectileCreate = FindMethod(projectile.DeclaringType, "OnCreateSimulation");
            var directCreate = FindMethod(direct.DeclaringType, "OnCreateSimulation");

            ValidateFingerprint(meleeDamage, "A26111B121EF4B92BBDCD642C045CABB45629E5F39778E7274C0437982C08FEA");
            ValidateFingerprint(projectileDamage, "1B79F3B3492CAE5E96748F070DC6A632E5D06A5D2AB7B255A20D05D60D67A2F8");
            ValidateFingerprint(meleeSchedule, "2EE8A5998CE45712E01574BA6FA460EAA0FBE609489A4F45EE6AC7E7B02F47F8");
            ValidateFingerprint(projectileSchedule, "05614243DB2BDE4BEF0091943CD3818F846CA598ABA9838CF84A3DEE502C1C13");
            ValidateFingerprint(directDamage, "A22C895B09AF137C29B4CADD09B8D29A3D28A202BE3610A11B1C1EC657A30447");
            ValidateFingerprint(directSchedule, "1CB142F6AB5CB8787FD135E118761DBCC541EF55F417F19F9092A3772CA646E8");
            ValidateFingerprint(directExecute, "C94FE0A4AB24FB6348E351A985C67EA795F02468C889189B8F8289236793F40A");
            ValidateFingerprint(meleeCreate, "4357D6347C3A4446927F88A0456DDE81E6A22C774765AAE05538B4B4EF1DAFE1");
            ValidateFingerprint(projectileCreate, "E5D91D9DB1CC193E3807A779D5CCD0960DE4F61310FCAFC37BC2524E0BA864FC");
            ValidateFingerprint(directCreate, "036BCCB0C97868AAC5C1F1292ED24A1E7A29CD0EDE5340D3F19C6A8DC024EB46");

            var bridge = runtime.MainModule.GetType("VeteranSoldiers.CombatBridge")
                ?? throw new InvalidOperationException("Runtime has no CombatBridge.");
            var tag = FindMethod(bridge, PreparedTagName);
            var tagDirect = FindMethod(bridge, PreparedDirectTagName);
            var sourceComponent = runtime.MainModule.GetType("VeteranSoldiers.CombatSource")
                ?? throw new InvalidOperationException("Runtime has no CombatSource component.");
            if (runtime.Name.Name != "VeteranSoldiers")
                throw new InvalidOperationException("Runtime assembly identity mismatch.");
            ValidateBridge(tag, "Unity.Entities.EntityCommandBuffer/ParallelWriter&", "System.Int32", "Unity.Entities.Entity", "Unity.Entities.Entity");
            ValidateBridge(tagDirect, "Unity.Entities.EntityCommandBuffer&", "Unity.Entities.Entity", "Unity.Entities.Entity");
            ValidateSourceComponent(sourceComponent);

            var extensions = entities.MainModule.GetType("Unity.Entities.JobEntityBatchExtensions")
                ?? throw new InvalidOperationException("Missing JobEntityBatchExtensions.");
            var run = extensions.Methods.Single(method => method.Name == "RunWithoutJobs" && method.GenericParameters.Count == 1
                && method.Parameters.Count == 2 && method.Parameters[0].ParameterType.IsByReference
                && method.Parameters[1].ParameterType.FullName == "Unity.Entities.EntityQuery");
            var jobHandle = unityCore.MainModule.GetType("Unity.Jobs.JobHandle")
                ?? throw new InvalidOperationException("Missing JobHandle.");
            var complete = jobHandle.Methods.Single(method => method.Name == "Complete" && !method.IsStatic && method.Parameters.Count == 0);
            // These checks happen before any mutation, including external assembly imports.
            ValidateManagedRunner(run);
            var medicalPlan = MedicalPatcher.Prepare(module, entities, unityCore);
            var meleePlan = Prepare(melee, meleeDamage, meleeSchedule, "_changeBufferParallel", 0);
            var projectilePlan = Prepare(projectile, projectileDamage, projectileSchedule, "_changeBuffer", 2);
            var directPlan = PrepareDirect(direct, directDamage, directSchedule, directExecute);
            var meleeArchetype = PreparedRequestArchetype.Prepare(meleeCreate, "_changeRequestArchetype");
            var projectileArchetype = PreparedRequestArchetype.Prepare(projectileCreate, "_changeRequestArchetype");
            var directArchetype = PreparedRequestArchetype.Prepare(directCreate, "_meleeAttackRequestArchetype");
            var meleeSource = melee.NestedTypes.Single(type => type.Name == "BatchIterator").Fields.Single(field => field.Name == "Entity" && field.FieldType.FullName == "Utility.ChunkEntity");
            var implicitEntity = module.GetType("Utility.ChunkEntity").Methods.Single(method => method.Name == "op_Implicit" && method.ReturnType.FullName == "Unity.Entities.Entity");
            var projectileSource = projectile.NestedTypes.Single(type => type.Name == "BatchIterator").Fields.Single(field => field.Name == "ProjectileFlyData");
            var sourceMethod = FindMethod(projectile, "GetShooterPosition");
            var getProjectile = sourceMethod.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
                .First(method => method.Name == "get_Value" && method.DeclaringType.FullName == projectileSource.FieldType.FullName);
            var sourceEntity = module.GetType("Components.RawComponents.ProjectileFlyData").Fields.Single(field => field.Name == "sourceEntity" && field.FieldType.FullName == "Unity.Entities.Entity");
            var directSource = direct.NestedTypes.Single(type => type.Name == "UnitInfo").Fields.Single(field => field.Name == "Entity" && field.FieldType.FullName == "Unity.Entities.Entity");

            var importedTag = module.ImportReference(tag);
            InjectTag(meleePlan, importedTag, new[] {
                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldfld, meleeSource),
                Instruction.Create(OpCodes.Call, implicitEntity)
            });
            InjectTag(projectilePlan, importedTag, new[] {
                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldflda, projectileSource),
                Instruction.Create(OpCodes.Call, getProjectile), Instruction.Create(OpCodes.Ldfld, sourceEntity)
            });
            InjectDirectTag(directPlan, module.ImportReference(tagDirect), directSource);
            AddManagedRunner(module, meleePlan, run, complete);
            AddManagedRunner(module, projectilePlan, run, complete);
            AddDirectManagedRunner(module, directPlan, directExecute, complete);
            var importedSourceComponent = module.ImportReference(sourceComponent);
            meleeArchetype.Apply(importedSourceComponent);
            projectileArchetype.Apply(importedSourceComponent);
            directArchetype.Apply(importedSourceComponent);
            medicalPlan.Apply(importedTag, importedSourceComponent);

            var marker = new TypeDefinition("VeteranSoldiers", "InstrumentationMarker",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            marker.Fields.Add(new FieldDefinition("Version", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                module.TypeSystem.String) { Constant = "1" });
            module.Types.Add(marker);
        }

        private static TypeDefinition FindJob(ModuleDefinition module, string systemName, string jobName)
        {
            var system = module.GetType(systemName) ?? throw new InvalidOperationException("Missing system " + systemName);
            return system.NestedTypes.Single(type => type.Name == jobName);
        }

        private static MethodDefinition FindMethod(TypeDefinition type, string name)
        {
            return type.Methods.Single(method => method.Name == name);
        }

        internal static void ValidateFingerprint(MethodDefinition method, string expected)
        {
            // Cecil's instruction text includes offsets, branch targets, locals and full member signatures.
            var originalCulture = CultureInfo.CurrentCulture;
            string serialized;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                serialized = string.Join("\n", method.Body.Instructions.Select(instruction => instruction.ToString()));
            }
            finally { CultureInfo.CurrentCulture = originalCulture; }
            using (var sha = SHA256.Create())
            {
                string actual = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(serialized)));
                if (actual != expected)
                    throw new InvalidOperationException("Method changed or conflicted with another patcher: " + method.FullName + " (" + actual + ").");
            }
        }

        private static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", string.Empty);

        private static void ValidateBridge(MethodDefinition method, params string[] arguments)
        {
            if (!method.IsPublic || !method.IsStatic || method.ReturnType.FullName != "System.Void"
                || !method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(arguments))
                throw new InvalidOperationException("Runtime CombatBridge." + method.Name + " contract mismatch.");
        }

        private static void ValidateSourceComponent(TypeDefinition component)
        {
            if (!component.IsPublic || !component.IsValueType || component.Fields.Count != 2 ||
                !component.Interfaces.Any(item => item.InterfaceType.FullName == "Unity.Entities.IComponentData") ||
                !component.Fields.Any(field => field.Name == "Source" && !field.IsStatic && field.FieldType.FullName == "Unity.Entities.Entity") ||
                !component.Fields.Any(field => field.Name == "Processed" && !field.IsStatic && field.FieldType.FullName == "System.Byte"))
                throw new InvalidOperationException("Runtime CombatSource component layout mismatch.");
        }

        private static void ValidateManagedRunner(MethodDefinition run)
        {
            if (!run.IsPublic || !run.IsStatic || !run.HasBody || run.ReturnType.FullName != "System.Void")
                throw new InvalidOperationException("RunWithoutJobs contract changed.");
            var calledMethods = run.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
            if (!calledMethods.Any(method => method.Name == "RunWithoutJobsInternal")
                || calledMethods.Any(method => method.Name.Contains("Schedule") || method.Name.Contains("CreateJobReflectionData")))
                throw new InvalidOperationException("RunWithoutJobs no longer has the verified direct managed dispatch.");
        }

        private sealed class Plan
        {
            public TypeDefinition Job = null!;
            public MethodDefinition Damage = null!;
            public MethodDefinition Schedule = null!;
            public FieldDefinition Buffer = null!;
            public MethodReference BatchIndex = null!;
            public VariableDefinition Request = null!;
            public Instruction SetRequest = null!;
            public Instruction ScheduleCall = null!;
            public GenericInstanceMethod OriginalSchedule = null!;
        }

        private static Plan Prepare(TypeDefinition job, MethodDefinition damage, MethodDefinition schedule, string bufferName, int requestIndex)
        {
            if (job.Methods.Any(method => method.Name == WrapperName))
                throw new InvalidOperationException("Managed wrapper already exists.");
            var buffer = job.Fields.Single(field => field.Name == bufferName && field.FieldType.FullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter");
            var set = damage.Body.Instructions.Single(instruction => instruction.Operand is GenericInstanceMethod method
                && method.Name == "SetComponent" && method.GenericArguments.Count == 1
                && method.GenericArguments[0].FullName == "Components.RawComponents.HealthChangeRequest");
            if (set.OpCode != OpCodes.Call || set.Next == null || set.Next.OpCode != OpCodes.Ret
                || damage.Body.ExceptionHandlers.Count != 0 || damage.Body.Variables[requestIndex].VariableType.FullName != "Unity.Entities.Entity")
                throw new InvalidOperationException("Health request hook shape changed.");
            var scheduleCall = schedule.Body.Instructions.Single(instruction => instruction.Operand is GenericInstanceMethod method
                && method.DeclaringType.FullName == "Unity.Entities.JobEntityBatchExtensions" && method.Name == "ScheduleParallel"
                && method.GenericArguments.Count == 1 && method.GenericArguments[0].FullName == job.FullName);
            var originalSchedule = (GenericInstanceMethod)scheduleCall.Operand;
            if (originalSchedule.Parameters.Count != 3 || originalSchedule.ReturnType.FullName != "Unity.Jobs.JobHandle"
                || originalSchedule.Parameters[1].ParameterType.FullName != "Unity.Entities.EntityQuery"
                || originalSchedule.Parameters[2].ParameterType.FullName != "Unity.Jobs.JobHandle")
                throw new InvalidOperationException("ScheduleParallel signature changed.");
            return new Plan {
                Job = job, Damage = damage, Schedule = schedule, Buffer = buffer,
                Request = damage.Body.Variables[requestIndex], SetRequest = set,
                BatchIndex = damage.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().First(method => method.Name == "get_BatchIndex"),
                ScheduleCall = scheduleCall, OriginalSchedule = originalSchedule
            };
        }

        private static void InjectTag(Plan plan, MethodReference tag, Instruction[] sourceLoad)
        {
            var processor = plan.Damage.Body.GetILProcessor();
            var sequence = new[] {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldflda, plan.Buffer),
                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, plan.BatchIndex),
                Instruction.Create(OpCodes.Ldloc, plan.Request)
            }.Concat(sourceLoad).Concat(new[] { Instruction.Create(OpCodes.Call, tag) });
            var cursor = plan.SetRequest;
            foreach (var instruction in sequence)
            {
                processor.InsertAfter(cursor, instruction);
                cursor = instruction;
            }
            plan.Damage.Body.MaxStackSize = Math.Max(plan.Damage.Body.MaxStackSize, 8);
        }

        private static Plan PrepareDirect(TypeDefinition job, MethodDefinition damage, MethodDefinition schedule, MethodDefinition execute)
        {
            if (job.Methods.Any(method => method.Name == WrapperName)
                || !job.Interfaces.Any(implementation => implementation.InterfaceType.FullName == "Unity.Jobs.IJob")
                || !execute.IsPublic || execute.IsStatic || execute.Parameters.Count != 0 || execute.ReturnType.FullName != "System.Void")
                throw new InvalidOperationException("Direct-control managed execution contract changed.");
            var buffer = job.Fields.Single(field => field.Name == "_changeECB" && field.FieldType.FullName == "Unity.Entities.EntityCommandBuffer");
            var set = damage.Body.Instructions.Single(instruction => instruction.Operand is GenericInstanceMethod method
                && method.DeclaringType.FullName == "Unity.Entities.EntityCommandBuffer" && method.Name == "SetComponent"
                && method.GenericArguments.Count == 1 && method.GenericArguments[0].FullName == "Components.RawComponents.HealthChangeRequest");
            if (set.OpCode != OpCodes.Call || set.Next == null || set.Next.OpCode != OpCodes.Ret
                || damage.Body.ExceptionHandlers.Count != 0 || damage.Body.Variables[0].VariableType.FullName != "Unity.Entities.Entity"
                || damage.Parameters[0].ParameterType.FullName != job.FullName + "/UnitInfo&")
                throw new InvalidOperationException("Direct-control health request hook shape changed.");
            var scheduleCall = schedule.Body.Instructions.Single(instruction => instruction.Operand is GenericInstanceMethod method
                && method.DeclaringType.FullName == "Unity.Jobs.IJobExtensions" && method.Name == "Schedule"
                && method.GenericArguments.Count == 1 && method.GenericArguments[0].FullName == job.FullName);
            var originalSchedule = (GenericInstanceMethod)scheduleCall.Operand;
            if (scheduleCall.OpCode != OpCodes.Call || originalSchedule.Parameters.Count != 2
                || originalSchedule.ReturnType.FullName != "Unity.Jobs.JobHandle"
                || originalSchedule.Parameters[1].ParameterType.FullName != "Unity.Jobs.JobHandle")
                throw new InvalidOperationException("Direct-control IJob.Schedule signature changed.");
            return new Plan {
                Job = job, Damage = damage, Schedule = schedule, Buffer = buffer,
                Request = damage.Body.Variables[0], SetRequest = set,
                ScheduleCall = scheduleCall, OriginalSchedule = originalSchedule
            };
        }

        private static void InjectDirectTag(Plan plan, MethodReference tag, FieldReference source)
        {
            var processor = plan.Damage.Body.GetILProcessor();
            var sequence = new[] {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldflda, plan.Buffer),
                Instruction.Create(OpCodes.Ldloc, plan.Request), Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Ldfld, source), Instruction.Create(OpCodes.Call, tag)
            };
            var cursor = plan.SetRequest;
            foreach (var instruction in sequence)
            {
                processor.InsertAfter(cursor, instruction);
                cursor = instruction;
            }
            plan.Damage.Body.MaxStackSize = Math.Max(plan.Damage.Body.MaxStackSize, 3);
        }

        private static void AddDirectManagedRunner(ModuleDefinition module, Plan plan, MethodDefinition execute, MethodDefinition complete)
        {
            var handleType = module.ImportReference(plan.OriginalSchedule.ReturnType);
            var wrapper = new MethodDefinition(WrapperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, handleType);
            wrapper.Parameters.Add(new ParameterDefinition("job", ParameterAttributes.None, plan.Job));
            wrapper.Parameters.Add(new ParameterDefinition("dependency", ParameterAttributes.None, handleType));
            wrapper.Body.InitLocals = true;
            var result = new VariableDefinition(handleType);
            wrapper.Body.Variables.Add(result);
            var il = wrapper.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldarga, wrapper.Parameters[1]));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(complete)));
            il.Append(Instruction.Create(OpCodes.Ldarga, wrapper.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Call, execute));
            il.Append(Instruction.Create(OpCodes.Ldloca, result));
            il.Append(Instruction.Create(OpCodes.Initobj, handleType));
            il.Append(Instruction.Create(OpCodes.Ldloc, result));
            il.Append(Instruction.Create(OpCodes.Ret));
            plan.Job.Methods.Add(wrapper);
            plan.ScheduleCall.Operand = wrapper;
        }

        private static void AddManagedRunner(ModuleDefinition module, Plan plan, MethodDefinition run, MethodDefinition complete)
        {
            var handleType = module.ImportReference(plan.OriginalSchedule.ReturnType);
            var wrapper = new MethodDefinition(WrapperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, handleType);
            wrapper.Parameters.Add(new ParameterDefinition("job", ParameterAttributes.None, plan.Job));
            wrapper.Parameters.Add(new ParameterDefinition("query", ParameterAttributes.None, module.ImportReference(plan.OriginalSchedule.Parameters[1].ParameterType)));
            wrapper.Parameters.Add(new ParameterDefinition("dependency", ParameterAttributes.None, handleType));
            wrapper.Body.InitLocals = true;
            var result = new VariableDefinition(handleType);
            wrapper.Body.Variables.Add(result);
            var managedRun = new GenericInstanceMethod(module.ImportReference(run));
            managedRun.GenericArguments.Add(plan.Job);
            var il = wrapper.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldarga, wrapper.Parameters[2]));
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(complete)));
            il.Append(Instruction.Create(OpCodes.Ldarga, wrapper.Parameters[0]));
            il.Append(Instruction.Create(OpCodes.Ldarg_1));
            il.Append(Instruction.Create(OpCodes.Call, managedRun));
            il.Append(Instruction.Create(OpCodes.Ldloca, result));
            il.Append(Instruction.Create(OpCodes.Initobj, handleType));
            il.Append(Instruction.Create(OpCodes.Ldloc, result));
            il.Append(Instruction.Create(OpCodes.Ret));
            plan.Job.Methods.Add(wrapper);
            plan.ScheduleCall.Operand = wrapper;
        }
    }
}
