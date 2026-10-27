using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PreparedArchetypeTests
{
    internal static void Verify(MethodDefinition method, Action<bool, string> check)
    {
        var sources = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken &&
            instruction.Operand is TypeReference type && type.FullName == "VeteranSoldiers.CombatSource").ToArray();
        check(sources.Length == 1, "one prepared attribution component in " + method.DeclaringType.Name);
        var source = sources.Single();
        check(source.Previous.OpCode == OpCodes.Ldc_I4_1 && source.Previous.Previous.OpCode == OpCodes.Dup,
            "attribution uses distinct array index one");
        var original = source.Previous.Previous.Previous;
        check(original.OpCode == OpCodes.Stelem_Any && original.Previous.Previous.Previous.OpCode == OpCodes.Ldtoken &&
            ((TypeReference)original.Previous.Previous.Previous.Operand).FullName == "Components.RawComponents.HealthChangeRequest",
            "original health request type remains at array index zero");
        var allocation = original.Previous.Previous.Previous.Previous.Previous.Previous;
        check(allocation.OpCode == OpCodes.Newarr && allocation.Previous.OpCode == OpCodes.Ldc_I4_2,
            "request archetype array contains exactly two components");
        var factory = source.Next.Next.Next.Next;
        check(factory.OpCode == OpCodes.Call && factory.Operand is MethodReference called && called.Name == "CreateArchetype" &&
            factory.Next.OpCode == OpCodes.Stfld && factory.Next.Operand is FieldReference field &&
            (field.Name == "_changeRequestArchetype" || field.Name == "_meleeAttackRequestArchetype"),
            "prepared archetype feeds the original damage-request field");
    }
}
