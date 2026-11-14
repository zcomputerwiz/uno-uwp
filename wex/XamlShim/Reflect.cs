// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The generated default property accessors: the Windows.UI.Xaml property of a shim is the Uno object's public
// property of the same name. Values arrive already converted by their ABI kind (string, bool, number, enum as long,
// object as its Uno object or boxed value) and are converted to the Uno property's type here. A shim without a Uno
// object, or a Uno type without the property, logs "not implemented" and gives E_NOTIMPL.
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace XamlShim.Abi;

public static class Reflect
{
    static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> properties = new();

    static (object Target, PropertyInfo Property) Find(object shim, string name)
    {
        var target = ((ShimObject)shim).UnoObject;
        var property = target == null ? null : properties.GetOrAdd((target.GetType(), name), key =>
        {
            /* the most derived public instance property of that name that is not an indexer */
            for (var t = key.Item1; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            return null;
        });
        if (target == null || property == null)
        {
            Ccw.NotImplemented($"{((ShimObject)shim).RuntimeClassName}.{name} (no Uno property)");
            throw new NotImplementedException();
        }
        return (target, property);
    }

    public static object? Get(object shim, string name)
    {
        var (target, property) = Find(shim, name);
        return property.GetValue(target);
    }

    public static long GetInteger(object shim, string name) => Convert.ToInt64(Get(shim, name), CultureInfo.InvariantCulture);

    /* struct properties: the ABI struct and Uno's struct of the same shape, member by member (StructConvert) */
    public static T GetStruct<T>(object shim, string name) where T : struct
    {
        var value = Get(shim, name);
        return value == null ? default : (T)StructConvert.ToAbi(value, typeof(T));
    }

    public static void SetStruct<T>(object shim, string name, T value) where T : struct
    {
        var (target, property) = Find(shim, name);
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(target, StructConvert.ToUno(value, type));
    }

    public static void Set(object shim, string name, object? value)
    {
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
