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
        var result = property.GetValue(target);
        if (Ccw.Calls && name == "DataContext")
        {
            var parent = (target as Microsoft.UI.Xaml.FrameworkElement)?.Parent;
            Ccw.Log($"get {target.GetType().FullName} {(target as Microsoft.UI.Xaml.FrameworkElement)?.Name}.DataContext: {result?.GetType().FullName ?? "null"}" +
                    $" (parent {parent?.GetType().FullName ?? "none"}, loaded {(target as Microsoft.UI.Xaml.FrameworkElement)?.IsLoaded})");
        }
        return result;
    }

    /* an attached property through a statics interface: the Uno class's static GetX(element) or SetX(element, value) */
    static MethodInfo? Accessor(object shim, string name)
    {
        var factory = (ShimFactory)shim;
        var method = ShimFactory.UnoTypeOf(factory.ClassName)?.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == (name.StartsWith("Get", StringComparison.Ordinal) ? 1 : 2));
        if (method == null) Ccw.NotImplemented($"{factory.ClassName}.{name} (no Uno static method)");
        return method;
    }

    public static object? GetAttached(object shim, string name, IntPtr element)
    {
        var method = Accessor(shim, "Get" + name);
        var target = Boxing.Unbox(element);
        if (method == null || target == null) return null;
        return method.Invoke(null, [target]);
    }

    public static void SetAttached(object shim, string name, IntPtr element, object? value)
    {
        var method = Accessor(shim, "Set" + name) ?? throw new NotImplementedException();
        var target = Boxing.Unbox(element) ?? throw new ArgumentNullException(nameof(element));
        var type = method.GetParameters()[1].ParameterType;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (value != null && !type.IsInstanceOfType(value))
            value = type.IsEnum ? Enum.ToObject(type, Convert.ToInt64(value, CultureInfo.InvariantCulture))
                  : value.GetType().IsValueType && !value.GetType().IsPrimitive && type.IsValueType ? StructConvert.ToUno(value, type)
                  : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        method.Invoke(null, [target, value]);
    }

    /* a method through the ABI (generated default bodies): the Uno class's static method of that name and parameter count
     * for a statics interface, the Uno object's method otherwise; the arguments converted to its parameter types */
    public static object? Call(object shim, string name, object?[] args)
    {
        object? target = null;
        Type? type;
        BindingFlags flags = BindingFlags.Public | BindingFlags.FlattenHierarchy;
        if (shim is ShimFactory factory)
        {
            type = ShimFactory.UnoTypeOf(factory.ClassName);
            flags |= BindingFlags.Static;
        }
        else
        {
            target = ((ShimObject)shim).UnoObject;
            type = target?.GetType();
            flags |= BindingFlags.Instance;
        }
        var candidates = type?.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToList() ?? [];
        var method = candidates.FirstOrDefault(m => m.GetParameters().Zip(args).All(p => p.Second == null || Accepts(p.First.ParameterType, p.Second)))
                     ?? candidates.FirstOrDefault();
        /* a factory's CreateInstance... (not composable): Uno's constructor with those parameters */
        if (method == null && shim is ShimFactory && name.StartsWith("CreateInstance", StringComparison.Ordinal) && type != null)
        {
            var ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Length);
            if (ctor != null)
            {
                var cps = ctor.GetParameters();
                return ctor.Invoke(args.Select((a, i) => ConvertArgument(a, cps[i].ParameterType)).ToArray());
            }
        }
        if (method == null)
        {
            Ccw.NotImplemented($"{(shim as ShimFactory)?.ClassName ?? ((ShimObject)shim).RuntimeClassName}.{name} (no Uno method)");
            throw new NotImplementedException();
        }
        var parameters = method.GetParameters();
        var converted = new object?[args.Length];
        for (int i = 0; i < args.Length; i++) converted[i] = ConvertArgument(args[i], parameters[i].ParameterType);
        if (Ccw.Calls) Ccw.Log($"Reflect.Call {method.DeclaringType?.Name}.{name}");
        return method.Invoke(target, converted);
    }

    static bool Accepts(Type parameter, object value)
    {
        var t = Nullable.GetUnderlyingType(parameter) ?? parameter;
        return t.IsInstanceOfType(value) || (t.IsEnum && value is long) || (t.IsPrimitive && value.GetType().IsPrimitive) ||
               (t.IsValueType && !t.IsPrimitive && value.GetType().IsValueType && !value.GetType().IsPrimitive);
    }

    static object? ConvertArgument(object? value, Type parameter)
    {
        var t = Nullable.GetUnderlyingType(parameter) ?? parameter;
        if (value == null || t.IsInstanceOfType(value)) return value;
        if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (t.IsValueType && !t.IsPrimitive && value.GetType().IsValueType && !value.GetType().IsPrimitive) return StructConvert.ToUno(value, t);
        if (t == typeof(object)) return value;
        return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
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
        if (Ccw.Calls && name == "DataContext") Ccw.Log($"set {target.GetType().FullName} {(target as Microsoft.UI.Xaml.FrameworkElement)?.Name}.DataContext = {value?.GetType().FullName ?? "null"}");
        if (name == "DataContext" && value is NativeObject probed) NativeControls.Probe(probed.Pointer, "DataContext");
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
