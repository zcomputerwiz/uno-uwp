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
        var target = (shim as UI_Xaml_DependencyObject)?.UnoObject;
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
