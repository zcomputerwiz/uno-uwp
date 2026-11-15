// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The generated default property accessors: the Windows.UI.Xaml property of a shim is the Uno object's public
// property of the same name. Values arrive already converted by their ABI kind (string, bool, number, enum as long,
// object as its Uno object or boxed value) and are converted to the Uno property's type here. A property Uno does not
// have (Application.HighContrastAdjustment, ...) is kept on the shim: what was set is read back, the default otherwise,
// and the first use is logged ("stored only"), so that applications setting Windows-only properties go on.
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XamlShim.Abi;

public static class Reflect
{
    static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> properties = new();
    static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, object?>> stored = new();
    static readonly ConcurrentDictionary<string, bool> storedLogged = new();

    /* a property Uno does not have: its value kept on the shim */
    static bool Stored(object shim, string name, out ConcurrentDictionary<string, object?> values)
    {
        var target = ((ShimObject)shim).UnoObject;
        bool missing = target == null || properties.GetOrAdd((target.GetType(), name), key => Lookup(key.Item1, key.Item2)) == null;
        values = stored.GetOrCreateValue(shim);
        if (missing && storedLogged.TryAdd($"{((ShimObject)shim).RuntimeClassName}.{name}", true))
            Ccw.Log($"stored only: {((ShimObject)shim).RuntimeClassName}.{name} (no Uno property)");
        return missing;
    }

    static PropertyInfo? Lookup(Type type, string name)
    {
        /* the most derived public instance property of that name that is not an indexer */
        for (var t = type; t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null && p.GetIndexParameters().Length == 0) return p;
        }
        return null;
    }

    static (object Target, PropertyInfo Property) Find(object shim, string name)
    {
        var target = ((ShimObject)shim).UnoObject;
        var property = target == null ? null : properties.GetOrAdd((target.GetType(), name), key => Lookup(key.Item1, key.Item2));
        if (target == null || property == null)
        {
            Ccw.NotImplemented($"{((ShimObject)shim).RuntimeClassName}.{name} (no Uno property)");
            throw new NotImplementedException();
        }
        return (target, property);
    }

    public static object? Get(object shim, string name)
    {
        /* a statics interface (Popup.IsOpenProperty): the Uno class's static member of that name */
        if (shim is ShimFactory factory)
        {
            const BindingFlags statics = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var type = ShimFactory.UnoTypeOf(factory.ClassName);
            if (type?.GetProperty(name, statics) is { } sp) return sp.GetValue(null);
            if (type?.GetField(name, statics) is { } sf) return sf.GetValue(null);
            Ccw.NotImplemented($"{factory.ClassName}.{name} (no Uno static member)");
            return null;
        }
        if (Stored(shim, name, out var values)) return values.TryGetValue(name, out var v) ? v : null;
        var (target, property) = Find(shim, name);
        return property.GetValue(target);
    }

    public static long GetInteger(object shim, string name) => Convert.ToInt64(Get(shim, name), CultureInfo.InvariantCulture);

    /* struct properties: the ABI struct and Uno's struct of the same shape, member by member (StructConvert) */
    public static T GetStruct<T>(object shim, string name) where T : struct
    {
        if (Stored(shim, name, out var values)) return values.TryGetValue(name, out var v) && v is T t ? t : default;
        var value = Get(shim, name);
        return value == null ? default : (T)StructConvert.ToAbi(value, typeof(T));
    }

    public static void SetStruct<T>(object shim, string name, T value) where T : struct
    {
        if (Stored(shim, name, out var values)) { values[name] = value; return; }
        var (target, property) = Find(shim, name);
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(target, StructConvert.ToUno(value, type));
    }

    public static void Set(object shim, string name, object? value)
    {
        if (Stored(shim, name, out var values)) { values[name] = value; return; }
        var (target, property) = Find(shim, name);
        if (!property.CanWrite)
        {
            Ccw.NotImplemented($"{((ShimObject)shim).RuntimeClassName}.{name} (read-only in Uno)");
            throw new NotImplementedException();
        }
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (value != null && !type.IsInstanceOfType(value))
            value = type.IsEnum ? Enum.ToObject(type, Convert.ToInt64(value, CultureInfo.InvariantCulture))
                                : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        property.SetValue(target, value);
    }
}

/* ABI structs (generated, fields named as in the metadata) to and from Uno's structs of the same names (Thickness,
 * CornerRadius, Color, GridLength, Point, Size, Rect, ...): members matched by name, ignoring case; numbers and enums
 * converted; Windows.Foundation.TimeSpan is ticks; nested structs recursively. Uno structs whose members are read-only
 * are made with the constructor whose parameters have the members' names. */
public static class StructConvert
{
    const BindingFlags Members = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    static object? Member(object value, string name) =>
        value.GetType().GetProperty(name, Members) is { } p && p.GetIndexParameters().Length == 0 ? p.GetValue(value) :
        value.GetType().GetField(name, Members) is { } f ? f.GetValue(value) :
        throw new NotSupportedException($"{value.GetType().FullName} has no member {name}");

    static object? Convert(object? value, Type to)
    {
        if (value == null) return to.IsValueType ? Activator.CreateInstance(to) : null;
        if (to.IsInstanceOfType(value)) return value;
        if (value is TimeSpan span && to == typeof(long)) return span.Ticks;
        if (to == typeof(TimeSpan)) return TimeSpan.FromTicks(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (to.IsEnum) return Enum.ToObject(to, System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (value.GetType().IsEnum) return System.Convert.ChangeType(System.Convert.ToInt64(value, CultureInfo.InvariantCulture), to, CultureInfo.InvariantCulture);
        if (to.IsPrimitive || to == typeof(decimal)) return System.Convert.ChangeType(value, to, CultureInfo.InvariantCulture);
        if (to.IsValueType && value.GetType().IsValueType)
            return to.Namespace == "XamlShim.Abi" ? ToAbi(value, to) : ToUno(value, to);
        throw new NotSupportedException($"cannot convert {value.GetType().FullName} to {to.FullName}");
    }

    public static object ToAbi(object uno, Type abi)
    {
        object result = Activator.CreateInstance(abi)!;
        foreach (var field in abi.GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(result, Convert(Member(uno, field.Name), field.FieldType));
        return result;
    }

    public static object ToUno(object abi, Type uno)
    {
        var fields = abi.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        /* a constructor taking every member by name */
        foreach (var ctor in uno.GetConstructors())
        {
            var ps = ctor.GetParameters();
            if (ps.Length != fields.Length) continue;
            var args = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var field = fields.FirstOrDefault(f => string.Equals(f.Name, ps[i].Name, StringComparison.OrdinalIgnoreCase));
                if (field == null) ok = false;
                else args[i] = Convert(field.GetValue(abi), ps[i].ParameterType);
            }
            if (ok) return ctor.Invoke(args);
        }
        /* or the default value with every member set */
        object result = Activator.CreateInstance(uno)!;
        foreach (var field in fields)
        {
            var value = field.GetValue(abi);
            if (uno.GetProperty(field.Name, Members) is { CanWrite: true } p) p.SetValue(result, Convert(value, p.PropertyType));
            else if (uno.GetField(field.Name, Members) is { } f) f.SetValue(result, Convert(value, f.FieldType));
            else throw new NotSupportedException($"{uno.FullName} has no settable member {field.Name}");
        }
        return result;
    }
}
