using System;
using System.Collections.Generic;
using System.Reflection;

namespace VeteranSoldiers;

/// <summary>Copies actual native object-targeted effects; never edits research assets or base-object entries.</summary>
internal static class HallResearchAliases
{
    internal const int MaximumItems = 10000;
    internal const int MaximumEffects = 200000;

    internal static T[] Copy<T>(T[] source, int baseId, int hallId, out int added, out string targets) where T : class, new()
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (baseId <= 0 || hallId <= 0 || baseId == hallId) throw new ArgumentException("Research aliases require two distinct positive object IDs.");
        if (source.Length > MaximumItems) throw new InvalidOperationException("Research item limit exceeded.");
        added = 0;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        T[] result = source;
        FieldInfo[] fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public);
        int total = 0;
        for (int index = 0; index < source.Length; index++)
        {
            T item = source[index] ?? throw new InvalidOperationException("A research item is missing.");
            T? copy = null;
            foreach (FieldInfo field in fields)
            {
                if (!field.FieldType.IsArray || !(field.GetValue(item) is Array values)) continue;
                if (values.Rank != 1) throw new InvalidOperationException("Unsupported research array rank: " + field.Name);
                if (values.Length > MaximumEffects - total) throw new InvalidOperationException("Research effect limit exceeded.");
                total += values.Length;
                Type elementType = field.FieldType.GetElementType()!;
                FieldInfo? identity = elementType.GetField("objectId", BindingFlags.Instance | BindingFlags.Public);
                if (identity == null)
                {
                    if (elementType.GetMethod("GetObjectId", BindingFlags.Instance | BindingFlags.Public) != null)
                        throw new InvalidOperationException("An object-targeted research has no writable objectId field: " + field.Name);
                    continue; // Global effects have no object identity and need no alias.
                }
                if (identity.FieldType != typeof(int) || identity.IsInitOnly || !elementType.IsValueType)
                    throw new InvalidOperationException("Unsupported object-targeted research contract: " + field.Name);
                int matches = 0;
                bool explicitHall = false;
                for (int i = 0; i < values.Length; i++)
                {
                    int id = (int)identity.GetValue(values.GetValue(i))!;
                    if (id == baseId) matches++;
                    else if (id == hallId) explicitHall = true;
                }
                if (matches == 0) continue;
                counts.TryGetValue(field.Name, out int count);
                counts[field.Name] = count + matches;
                // A third-party explicit Hall effect is authoritative for this research/effect family.
                if (explicitHall) continue;
                if (field.IsInitOnly) throw new InvalidOperationException("Read-only research array: " + field.Name);
                if (matches > MaximumEffects - total) throw new InvalidOperationException("Research alias limit exceeded.");
                total += matches;
                Array aliases = Array.CreateInstance(elementType, values.Length + matches);
                Array.Copy(values, aliases, values.Length);
                int offset = values.Length;
                for (int i = 0; i < values.Length; i++)
                {
                    object effect = values.GetValue(i)!; // A fresh boxed value; changing it cannot mutate the source array.
                    if ((int)identity.GetValue(effect)! != baseId) continue;
                    identity.SetValue(effect, hallId);
                    aliases.SetValue(effect, offset++);
                }
                if (copy == null)
                {
                    copy = new T();
                    foreach (FieldInfo itemField in fields)
                    {
                        if (itemField.IsInitOnly) throw new InvalidOperationException("Read-only research item contract: " + itemField.Name);
                        itemField.SetValue(copy, itemField.GetValue(item));
                    }
                }
                field.SetValue(copy, aliases);
                added += matches;
            }
            if (copy != null)
            {
                if (ReferenceEquals(result, source)) result = (T[])source.Clone();
                result[index] = copy;
            }
        }
        var descriptions = new List<string>();
        foreach (var pair in counts) descriptions.Add(pair.Key + "=" + pair.Value);
        targets = descriptions.Count == 0 ? "none" : string.Join(",", descriptions);
        return result;
    }
}
