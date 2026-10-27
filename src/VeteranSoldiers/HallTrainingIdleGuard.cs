using System;
using System.Reflection;
using Unity.Entities;

namespace VeteranSoldiers;

/// <summary>An optional native-query shortcut. Any unsupported or invalid boundary
/// retains the ordinary modifier transaction; it never disables native training.</summary>
internal sealed class HallTrainingIdleGuard
{
    private FieldInfo? field;

    internal HallTrainingIdleGuard(Type systemType)
    {
        try
        {
            var candidate = systemType.GetField("_processTrainersJob", BindingFlags.NonPublic | BindingFlags.Instance);
            if (candidate != null && !candidate.IsStatic && candidate.FieldType == typeof(EntityQuery)) field = candidate;
        }
        catch { field = null; }
    }

    internal bool IsEmpty(object system)
    {
        if (field == null) return false;
        try
        {
            return field.GetValue(system) is EntityQuery query && query.IsEmptyIgnoreFilter;
        }
        catch
        {
            // A disposed/changed query or unexpected system instance invalidates only
            // this optimization. Do not retry a failing reflection/native boundary each frame.
            field = null;
            return false;
        }
    }
}
