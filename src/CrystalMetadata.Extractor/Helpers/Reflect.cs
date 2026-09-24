using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using CrystalMetadata.Core.Json;

namespace CrystalMetadata.Extractor.Helpers
{
    /// <summary>
    /// Safe reflection helpers. The Crystal SDK mixes managed classes (Engine API), structs with public fields
    /// (PageMargins) and COM RCWs (RAS API). Getters throw frequently on RAS objects; everything here swallows and
    /// returns null so a missing property becomes a JSON null rather than a failed extraction.
    /// </summary>
    internal static class Reflect
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        /// <summary>Reads a public property or field by name. For RCWs, pass the interop interface types to look through.</summary>
        public static object? Get(object? target, string name, params Type[] interfaceTypes)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            try
            {
                if (interfaceTypes != null && interfaceTypes.Length > 0)
                {
                    foreach (var t in interfaceTypes)
                    {
                        if (t == null || !t.IsInstanceOfType(target))
                        {
                            continue;
                        }

                        var pi = t.GetProperty(name, Flags);
                        if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0)
                        {
                            return pi.GetValue(target, null);
                        }
                    }
                }

                var type = target.GetType();
                var prop = type.GetProperty(name, Flags);
                if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    return prop.GetValue(target, null);
                }

                var field = type.GetField(name, Flags);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                if (Marshal_IsComObject(target))
                {
                    return GetViaDynamic(target, name);
                }
            }
            catch
            {
                // swallowed by design: unavailable property -> null
            }

            return null;
        }

        private static bool Marshal_IsComObject(object o)
        {
            try
            {
                return System.Runtime.InteropServices.Marshal.IsComObject(o);
            }
            catch
            {
                return false;
            }
        }

        private static object? GetViaDynamic(object target, string name)
        {
            try
            {
                var binder = Microsoft.CSharp.RuntimeBinder.Binder.GetMember(
                    Microsoft.CSharp.RuntimeBinder.CSharpBinderFlags.None,
                    name,
                    typeof(Reflect),
                    new[] { Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags.None, null) });
                var site = System.Runtime.CompilerServices.CallSite<Func<System.Runtime.CompilerServices.CallSite, object, object>>.Create(binder);
                return site.Target(site, target);
            }
            catch
            {
                return null;
            }
        }

        public static string? GetString(object? target, string name, params Type[] interfaceTypes)
        {
            return ToText(Get(target, name, interfaceTypes));
        }

        public static bool? GetBool(object? target, string name, params Type[] interfaceTypes)
        {
            var v = Get(target, name, interfaceTypes);
            if (v == null) return null;
            if (v is bool b) return b;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static int? GetInt(object? target, string name, params Type[] interfaceTypes)
        {
            var v = Get(target, name, interfaceTypes);
            if (v == null) return null;
            if (v is int i) return i;
            if (v is Enum) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static double? GetDouble(object? target, string name, params Type[] interfaceTypes)
        {
            var v = Get(target, name, interfaceTypes);
            if (v == null) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        /// <summary>Text form of a scalar: enum name, invariant number, ISO date, colour hex; null for null/empty colours.</summary>
        public static string? ToText(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return s;
                case Enum e:
                    return e.ToString();
                case Color c:
                    return c.IsEmpty ? null : MetadataJson.FormatColor(c.R, c.G, c.B);
                case DateTime dt:
                    return dt.ToString("o", CultureInfo.InvariantCulture);
                case IFormattable f:
                    return f.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString();
            }
        }

        /// <summary>Whether the value can be stored in a raw bag as-is.</summary>
        public static bool IsScalar(object? value)
        {
            if (value == null) return true;
            var t = value.GetType();
            return t.IsPrimitive || t.IsEnum || value is string || value is decimal || value is DateTime || value is Color;
        }

        public static object? ToJsonScalar(object? value)
        {
            if (value == null) return null;
            if (value is Enum || value is Color || value is DateTime) return ToText(value);
            return value;
        }

        /// <summary>
        /// Copies every readable public scalar property of <paramref name="target"/> into a dictionary, skipping names in
        /// <paramref name="exclude"/>. For RCWs supply the interop interface types; for managed objects the runtime type is used.
        /// </summary>
        public static Dictionary<string, object?> ScalarBag(object? target, ISet<string>? exclude, params Type[] interfaceTypes)
        {
            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (target == null)
            {
                return bag;
            }

            var types = new List<Type>();
            if (interfaceTypes != null)
            {
                foreach (var t in interfaceTypes)
                {
                    if (t != null && t.IsInstanceOfType(target))
                    {
                        types.Add(t);
                    }
                }
            }

            if (types.Count == 0)
            {
                types.Add(target.GetType());
            }

            foreach (var type in types)
            {
                PropertyInfo[] props;
                try
                {
                    props = type.GetProperties(Flags);
                }
                catch
                {
                    continue;
                }

                foreach (var p in props)
                {
                    if (!p.CanRead || p.GetIndexParameters().Length != 0 || bag.ContainsKey(p.Name))
                    {
                        continue;
                    }

                    if (exclude != null && exclude.Contains(p.Name))
                    {
                        continue;
                    }

                    object? value;
                    try
                    {
                        value = p.GetValue(target, null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (IsScalar(value))
                    {
                        bag[p.Name] = ToJsonScalar(value);
                    }
                }
            }

            return bag;
        }

        /// <summary>Enumerates a COM/managed collection defensively.</summary>
        public static IEnumerable<object> Items(object? collection)
        {
            if (collection == null)
            {
                yield break;
            }

            if (collection is IEnumerable en)
            {
                IEnumerator it;
                try
                {
                    it = en.GetEnumerator();
                }
                catch
                {
                    yield break;
                }

                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = it.MoveNext();
                    }
                    catch
                    {
                        yield break;
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    object? current;
                    try
                    {
                        current = it.Current;
                    }
                    catch
                    {
                        continue;
                    }

                    if (current != null)
                    {
                        yield return current;
                    }
                }
            }
            else
            {
                // RAS collections expose Count + Item(index)
                var count = GetInt(collection, "Count");
                if (count == null)
                {
                    yield break;
                }

                var indexer = collection.GetType().GetMethod("get_Item") ?? collection.GetType().GetMethod("Item");
                if (indexer == null)
                {
                    yield break;
                }

                for (var i = 0; i < count.Value; i++)
                {
                    object? item = null;
                    try
                    {
                        item = indexer.Invoke(collection, new object[] { i });
                    }
                    catch
                    {
                        // skip
                    }

                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }
    }
}
