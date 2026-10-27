using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VeteranSoldiers.Patching
{
    /// <summary>Extends only an exact, fingerprint-checked damage-request archetype initializer.</summary>
    internal sealed class PreparedRequestArchetype
    {
        private readonly MethodDefinition method;
        private readonly Instruction arrayLength, createCall;
        private readonly MethodReference typeFromHandle, implicitComponent;
        private readonly TypeReference componentType;

        private PreparedRequestArchetype(MethodDefinition method, Instruction arrayLength, Instruction createCall,
            MethodReference typeFromHandle, MethodReference implicitComponent, TypeReference componentType)
        {
            this.method = method;
            this.arrayLength = arrayLength;
            this.createCall = createCall;
            this.typeFromHandle = typeFromHandle;
            this.implicitComponent = implicitComponent;
            this.componentType = componentType;
        }

        internal static PreparedRequestArchetype Prepare(MethodDefinition method, string fieldName)
        {
            var store = method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld &&
                instruction.Operand is FieldReference field && field.DeclaringType.FullName == method.DeclaringType.FullName &&
                field.Name == fieldName && field.FieldType.FullName == "Unity.Entities.EntityArchetype");
            var create = store.Previous;
            if (create == null || create.OpCode != OpCodes.Call || !(create.Operand is MethodReference factory) ||
                factory.FullName != "Unity.Entities.EntityArchetype Unity.Entities.EntityManager::CreateArchetype(Unity.Entities.ComponentType[])")
                throw new InvalidOperationException("Damage-request archetype factory changed.");

            var sequence = new Instruction[8];
            Instruction? cursor = create.Previous;
            for (int i = sequence.Length - 1; i >= 0; i--)
            {
                if (cursor == null) throw new InvalidOperationException("Damage-request archetype initializer is incomplete.");
                sequence[i] = cursor;
                cursor = cursor.Previous;
            }
            if (sequence[0].OpCode != OpCodes.Ldc_I4_1 || sequence[1].OpCode != OpCodes.Newarr ||
                !(sequence[1].Operand is TypeReference component) || component.FullName != "Unity.Entities.ComponentType" ||
                sequence[2].OpCode != OpCodes.Dup || sequence[3].OpCode != OpCodes.Ldc_I4_0 ||
                sequence[4].OpCode != OpCodes.Ldtoken || !(sequence[4].Operand is TypeReference request) ||
                request.FullName != "Components.RawComponents.HealthChangeRequest" ||
                sequence[5].OpCode != OpCodes.Call || !(sequence[5].Operand is MethodReference fromHandle) ||
                fromHandle.FullName != "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)" ||
                sequence[6].OpCode != OpCodes.Call || !(sequence[6].Operand is MethodReference conversion) ||
                conversion.FullName != "Unity.Entities.ComponentType Unity.Entities.ComponentType::op_Implicit(System.Type)" ||
                sequence[7].OpCode != OpCodes.Stelem_Any ||
                !(sequence[7].Operand is TypeReference element) || element.FullName != component.FullName)
                throw new InvalidOperationException("Damage-request archetype must contain exactly HealthChangeRequest.");
            return new PreparedRequestArchetype(method, sequence[0], create, fromHandle, conversion, component);
        }

        internal void Apply(TypeReference sourceComponent)
        {
            // Preserve the original HealthChangeRequest at index zero. Every request is
            // born in its final archetype, so assigning attribution is an ordinary write.
            arrayLength.OpCode = OpCodes.Ldc_I4_2;
            var il = method.Body.GetILProcessor();
            foreach (var instruction in new[] {
                Instruction.Create(OpCodes.Dup),
                Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.Ldtoken, sourceComponent),
                Instruction.Create(OpCodes.Call, typeFromHandle),
                Instruction.Create(OpCodes.Call, implicitComponent),
                Instruction.Create(OpCodes.Stelem_Any, componentType)
            }) il.InsertBefore(createCall, instruction);
        }
    }
}
