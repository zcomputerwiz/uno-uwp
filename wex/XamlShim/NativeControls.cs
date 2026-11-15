// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The application's own controls inside Uno's XAML reader. Uno finds a XAML type by its .NET name (using:CalculatorApp
// and TitleBar is CalculatorApp.TitleBar) in the loaded assemblies. Before a page is read, each element of an
// application namespace gets a stand-in .NET type of that name, derived from the Uno class of the control's nearest
// Windows.UI.Xaml base (from the application's IXamlMetadataProvider). The stand-in's constructor has the application
// create the native control (IXamlType.ActivateInstance); the native constructor composes over its base class, and the
// base class's factory takes the stand-in itself as the Uno object, so the element in Uno's tree is the inner object
// of the application's control. Uno sets members through .NET reflection only, so the stand-in also gets a property
// for each member the package's XAML uses on that type and its Uno base lacks (found by reading every compiled XAML file
// of the package once): it forwards to the application's IXamlMember. A markup extension's stand-in forwards
// ProvideValue to the application's IMarkupExtensionOverrides.
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XamlShim.Abi;

public static unsafe partial class NativeControls
{
    static readonly ModuleBuilder module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("XamlShim.NativeControls"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("XamlShim.NativeControls");
    static readonly ConcurrentDictionary<string, Type?> standIns = new();
    static readonly MethodInfo construct = typeof(NativeControls).GetMethod(nameof(Construct))!;
    static readonly MethodInfo getMember = typeof(NativeControls).GetMethod(nameof(GetMember))!;
    static readonly MethodInfo setMember = typeof(NativeControls).GetMethod(nameof(SetMember))!;
    static readonly MethodInfo provideValue = typeof(NativeControls).GetMethod(nameof(ProvideValue))!;
    static readonly MethodInfo findProperty = typeof(NativeControls).GetMethod(nameof(FindProperty))!;
    static readonly HashSet<string> initialized = new();

    /* the dependency property an application type registered under a name (it or a base), or null; asking the
     * application's metadata for the member first has the type registered its properties */
    public static Microsoft.UI.Xaml.DependencyProperty? FindProperty(string type, string member)
    {
        Microsoft.UI.Xaml.DependencyProperty? Look()
        {
            for (var t = standIns.TryGetValue(type, out var s) ? s : null; t != null; t = t.BaseType)
                if (DependencyProperties.Registered.TryGetValue((t, member), out var dp)) return dp;
            return null;
        }
        if (Look() is { } found) return found;
        var m = XamlMember(type, member);
        if (m != IntPtr.Zero) Ccw.ComRelease(m);
        /* an application class registers its properties in its static constructor, which IXamlType.RunInitializer runs
         * (as the XAML framework has it run before it looks a type's properties up) */
        if (Look() == null && initialized.Add(type) && XamlType(type) is var xamlType && xamlType != IntPtr.Zero)
        {
            int hr = UI_Xaml_Markup_IXamlType_Call.RunInitializer(xamlType);
            Ccw.ComRelease(xamlType);
            Ccw.Log($"{type}: RunInitializer {hr:x8}");
        }
        var after = Look();
        if (after == null) Ccw.Log($"{type}.{member}Property: no dependency property registered");
        return after;
    }

    /* the application's object behind each stand-in */
    sealed class NativeRef(IntPtr pointer) { public readonly IntPtr Pointer = pointer; }
    static readonly ConditionalWeakTable<object, NativeRef> natives = new();

    /* the members the package's XAML uses on each application type, from all its compiled XAML */
    static Dictionary<string, HashSet<string>>? usedMembers;

    [GeneratedRegex(@"\{(\w+):(\w+)\s*([^{}]*)\}")]
    private static partial Regex MarkupUse();
    [GeneratedRegex(@"(\w+)\s*=")]
    private static partial Regex MemberAssignment();

    static string? ApplicationType(XNamespace ns, string localName)
    {
        var n = ns.NamespaceName;
        if (!n.StartsWith("using:", StringComparison.Ordinal) || localName.Contains('.')) return null;
        var clr = n["using:".Length..];
        if (clr.StartsWith("Windows.", StringComparison.Ordinal) || clr.StartsWith("Microsoft.", StringComparison.Ordinal)) return null;
        return clr + "." + localName;
    }

    static Dictionary<string, HashSet<string>> UsedMembers()
    {
        if (usedMembers != null) return usedMembers;
        var used = new Dictionary<string, HashSet<string>>();
        void Add(string type, string member)
        {
            if (!used.TryGetValue(type, out var set)) used[type] = set = new HashSet<string>();
            set.Add(member);
        }
        int files = 0;
        var dump = Ccw.Calls ? Path.Combine(Path.GetTempPath(), "xamlshim-xaml") : null;
        if (dump != null) Directory.CreateDirectory(dump);
        foreach (var (name, doc) in XbfLoader.AllXbf())
        {
            files++;
            if (dump != null)
                File.WriteAllText(Path.Combine(dump, name.Replace('\\', '_').Trim('_') + ".xaml"), doc.ToString());
            foreach (var e in doc.Root!.DescendantsAndSelf())
            {
                if (ApplicationType(e.Name.Namespace, e.Name.LocalName) is { } type)
                    foreach (var a in e.Attributes())
                        if (!a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None && !a.Name.LocalName.Contains('.'))
                            Add(type, a.Name.LocalName);
                var dot = e.Name.LocalName.IndexOf('.');
                if (dot > 0 && ApplicationType(e.Name.Namespace, e.Name.LocalName[..dot]) is { } owner)
                    Add(owner, e.Name.LocalName[(dot + 1)..]);
                /* a style's setters on an application type (TargetType="CalculatorButton" or "local:CalculatorButton") */
                if (e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value is { } setter && !setter.Contains('.') &&
                    !setter.Contains('(') && e.Ancestors().FirstOrDefault(p => p.Name.LocalName == "Style")?.Attribute("TargetType")?.Value is { } target)
                {
                    var colon = target.IndexOf(':');
                    if (colon > 0)
                    {
                        if (e.GetNamespaceOfPrefix(target[..colon]) is { } ns && ApplicationType(ns, target[(colon + 1)..]) is { } styled)
                            Add(styled, setter);
                    }
                    else
                        foreach (var d in e.AncestorsAndSelf().SelectMany(p => p.Attributes()).Where(d => d.IsNamespaceDeclaration))
                            if (ApplicationType(d.Value, target) is { } candidate) Add(candidate, setter);
                }
                foreach (var a in e.Attributes())
                    foreach (Match m in MarkupUse().Matches(a.Value))
                    {
                        var ns = e.GetNamespaceOfPrefix(m.Groups[1].Value);
                        if (ns == null || ApplicationType(ns, m.Groups[2].Value) is not { } extension) continue;
                        foreach (Match assignment in MemberAssignment().Matches(m.Groups[3].Value))
                            Add(extension, assignment.Groups[1].Value);
                    }
            }
        }
        Ccw.Log($"application XAML: {files} files, {used.Count} application types with members");
        return usedMembers = used;
    }

    /* the stand-in under construction, taken by the base class's factory */
    [ThreadStatic] static object? pending;

    /* the stand-in types of the application's elements in a XAML document (namespaces using:X outside Windows and
     * Microsoft), defined before Uno reads it */
    public static void Prepare(XElement root)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            if (ApplicationType(e.Name.Namespace, e.Name.LocalName) is { } type) StandIn(type);
            /* markup extensions used in attribute values */
            foreach (var a in e.Attributes())
                foreach (Match m in MarkupUse().Matches(a.Value))
                    if (e.GetNamespaceOfPrefix(m.Groups[1].Value) is { } ns && ApplicationType(ns, m.Groups[2].Value) is { } extension)
                        StandIn(extension);
        }
    }

    /* the stand-in of an application control, or null (not a XAML control of the application, or no application yet:
     * then nothing is remembered) */
    public static Type? StandIn(string fullName)
    {
        if (standIns.TryGetValue(fullName, out var known)) return known;
        if (UI_Xaml_Application.Current == null) return null;
        return standIns.GetOrAdd(fullName, Define);
    }

    static Type? Define(string fullName)
    {
        var xamlType = XamlType(fullName);
        if (xamlType == IntPtr.Zero) { Ccw.Log($"native type {fullName}: not in the application's metadata"); return null; }
        string? baseName = null;
        try { baseName = WindowsBase(xamlType); }
        finally { Ccw.ComRelease(xamlType); }
        var unoBase = baseName != null ? NativeTypes.UnoType(baseName) : null;
        var baseCtor = unoBase?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        if (unoBase == null || unoBase.IsSealed || baseCtor == null || !(baseCtor.IsPublic || baseCtor.IsFamily || baseCtor.IsFamilyOrAssembly))
        {
            Ccw.Log($"native type {fullName}: base {baseName ?? "none"} has no Uno class to stand in for it");
            return null;
        }
        UsedMembers().TryGetValue(fullName, out var members);
        lock (module)
        {
            var builder = module.DefineType(fullName, TypeAttributes.Public | TypeAttributes.Class, unoBase);
            var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtor);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, fullName);
            il.Emit(OpCodes.Call, construct);
            il.Emit(OpCodes.Ret);

            /* the application's own members: object-typed properties forwarding to its IXamlMember */
            var forwarded = new List<string>();
            foreach (var member in members ?? [])
            {
                if (unoBase.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy) != null) continue;
                var property = builder.DefineProperty(member, PropertyAttributes.None, typeof(object), null);
                var get = builder.DefineMethod("get_" + member, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                                               typeof(object), Type.EmptyTypes);
                var g = get.GetILGenerator();
                g.Emit(OpCodes.Ldarg_0);
                g.Emit(OpCodes.Ldstr, fullName);
                g.Emit(OpCodes.Ldstr, member);
                g.Emit(OpCodes.Call, getMember);
                g.Emit(OpCodes.Ret);
                var set = builder.DefineMethod("set_" + member, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                                               null, [typeof(object)]);
                var s = set.GetILGenerator();
                s.Emit(OpCodes.Ldarg_0);
                s.Emit(OpCodes.Ldstr, fullName);
                s.Emit(OpCodes.Ldstr, member);
                s.Emit(OpCodes.Ldarg_1);
                s.Emit(OpCodes.Call, setMember);
                s.Emit(OpCodes.Ret);
                property.SetGetMethod(get);
                property.SetSetMethod(set);
                forwarded.Add(member);
            }

            /* the application's dependency properties: Uno's reader finds a property through a static XProperty
             * member (style setters, attributes), these return the property the application registered for this type */
            foreach (var member in members ?? [])
            {
                var name = member + "Property";
                if (unoBase.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy) != null ||
                    unoBase.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy) != null) continue;
                var property = builder.DefineProperty(name, PropertyAttributes.None, typeof(Microsoft.UI.Xaml.DependencyProperty), null);
                var get = builder.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName |
                                               MethodAttributes.HideBySig, typeof(Microsoft.UI.Xaml.DependencyProperty), Type.EmptyTypes);
                var g = get.GetILGenerator();
                g.Emit(OpCodes.Ldstr, fullName);
                g.Emit(OpCodes.Ldstr, member);
                g.Emit(OpCodes.Call, findProperty);
                g.Emit(OpCodes.Ret);
                property.SetGetMethod(get);
            }

            /* a markup extension's value comes from the application's ProvideValue */
            if (typeof(Microsoft.UI.Xaml.Markup.MarkupExtension).IsAssignableFrom(unoBase))
            {
                var provide = builder.DefineMethod("ProvideValue", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                                                   typeof(object), Type.EmptyTypes);
                var p = provide.GetILGenerator();
                p.Emit(OpCodes.Ldarg_0);
                p.Emit(OpCodes.Call, provideValue);
                p.Emit(OpCodes.Ret);
            }
            var type = builder.CreateType();
            Ccw.Log($"native type {fullName}: stand-in over {unoBase.FullName}" +
                    (forwarded.Count > 0 ? $", members {string.Join(" ", forwarded)}" : ""));
            return type;
        }
    }

    static IntPtr Native(object self) => natives.TryGetValue(self, out var n) ? n.Pointer : IntPtr.Zero;

    /* the IXamlMember of an application type's member (AddRef'd), or zero */
    static IntPtr XamlMember(string type, string member)
    {
        var xamlType = XamlType(type);
        if (xamlType == IntPtr.Zero) return IntPtr.Zero;
        var h = HString.Create(member);
        IntPtr result = IntPtr.Zero;
        int hr = UI_Xaml_Markup_IXamlType_Call.GetMember(xamlType, h, &result);
        HString.Delete(h);
        Ccw.ComRelease(xamlType);
        if (hr < 0 || result == IntPtr.Zero) Ccw.Log($"{type}.{member}: no member ({hr:x8})");
        return hr < 0 ? IntPtr.Zero : result;
    }

    public static object? GetMember(object self, string type, string member)
    {
        var native = Native(self);
        var m = XamlMember(type, member);
        if (native == IntPtr.Zero || m == IntPtr.Zero) return null;
        try
        {
            IntPtr value;
            Marshal.ThrowExceptionForHR(UI_Xaml_Markup_IXamlMember_Call.GetValue(m, native, &value));
            return Boxing.Unbox(value);
        }
        catch (Exception e) { Ccw.Log($"get {type}.{member}: {e.Message}"); return null; }
        finally { Ccw.ComRelease(m); }
    }

    public static void SetMember(object self, string type, string member, object? value)
    {
        var native = Native(self);
        var m = XamlMember(type, member);
        if (native == IntPtr.Zero || m == IntPtr.Zero) { Ccw.Log($"set {type}.{member}: no native object or member"); return; }
        IntPtr boxed = IntPtr.Zero;
        try
        {
            boxed = value is string text ? FromString(m, text) : Boxing.Box(value);
            int hr = UI_Xaml_Markup_IXamlMember_Call.SetValue(m, native, boxed);
            if (Ccw.Calls || hr < 0) Ccw.Log($"set {type}.{member} = {value}: {hr:x8}");
        }
        catch (Exception e) { Ccw.Log($"set {type}.{member} = {value}: {e.Message}"); }
        finally
        {
            if (boxed != IntPtr.Zero) Ccw.ComRelease(boxed);
            Ccw.ComRelease(m);
        }
    }

    /* XAML text as the member's type: strings and primitives here, other types through the type's CreateFromString */
    static IntPtr FromString(IntPtr member, string text)
    {
        IntPtr type, h;
        Marshal.ThrowExceptionForHR(UI_Xaml_Markup_IXamlMember_Call.get_Type(member, &type));
        try
        {
            UI_Xaml_Markup_IXamlType_Call.get_FullName(type, &h);
            var name = HString.Read(h);
            HString.Delete(h);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            switch (name)
            {
            case "String": case "Object": return Boxing.Box(text);
            case "Boolean": return Boxing.Box(bool.Parse(text));
            case "Double": return Boxing.Box(double.Parse(text, c));
            case "Int32": return Boxing.Box(int.Parse(text, c));
            }
            var s = HString.Create(text);
            IntPtr result;
            int hr = UI_Xaml_Markup_IXamlType_Call.CreateFromString(type, s, &result);
            HString.Delete(s);
            Marshal.ThrowExceptionForHR(hr);
            return result;
        }
        finally { Ccw.ComRelease(type); }
    }

    /* a markup extension stand-in's value: the application's IMarkupExtensionOverrides.ProvideValue */
    public static object? ProvideValue(object self)
    {
        var native = Native(self);
        if (native == IntPtr.Zero || Ccw.QueryInterface(native, Iids.UI_Xaml_Markup_IMarkupExtensionOverrides, out var overrides) < 0)
        {
            Ccw.Log("ProvideValue: no application markup extension");
            return null;
        }
        try
        {
            IntPtr value;
            Marshal.ThrowExceptionForHR(UI_Xaml_Markup_IMarkupExtensionOverrides_Call.ProvideValue(overrides, &value));
            return Boxing.Unbox(value);
        }
        catch (Exception e) { Ccw.Log($"ProvideValue: {e.Message}"); return null; }
        finally { Ccw.ComRelease(overrides); }
    }

    /* called by a stand-in's constructor: the application creates its control over this object */
    public static void Construct(object self, string fullName)
    {
        var xamlType = XamlType(fullName);
        if (xamlType == IntPtr.Zero) return;
        IntPtr instance;
        var previous = pending;
        pending = self;
        int hr;
        try { hr = UI_Xaml_Markup_IXamlType_Call.ActivateInstance(xamlType, &instance); }
        finally { pending = previous; Ccw.ComRelease(xamlType); }
        if (hr < 0) { Ccw.Log($"ActivateInstance {fullName}: {hr:x8}"); return; }
        lock (Shim.Kept) Shim.Kept.Add(instance);
        natives.AddOrUpdate(self, new NativeRef(instance));
        if (Shim.UnoOf(instance) != self) Ccw.Log($"native {fullName}: the application's control is not over its stand-in");
    }

    /* the stand-in waiting for its base class's factory, when it is one of that class */
    public static object? TakePending(Type unoType)
    {
        if (pending == null || !unoType.IsInstanceOfType(pending)) return null;
        var p = pending;
        pending = null;
        return p;
    }

    /* the application's IXamlType of a type (AddRef'd), or zero */
    static IntPtr XamlType(string fullName)
    {
        var app = UI_Xaml_Application.Current;
        if (app == null) return IntPtr.Zero;
        var provider = Ccw.Get(app, Iids.UI_Xaml_Markup_IXamlMetadataProvider);
        if (provider == IntPtr.Zero) return IntPtr.Zero;
        /* by full name (.NET Native providers key GetXamlType by assembly-qualified name), then as a TypeName */
        var h = HString.Create(fullName);
        IntPtr result = IntPtr.Zero;
        int hr = UI_Xaml_Markup_IXamlMetadataProvider_Call.GetXamlTypeByFullName(provider, h, &result);
        if (hr < 0 || result == IntPtr.Zero)
        {
            var name = new TypeName { Name = h, Kind = 2 };
            result = IntPtr.Zero;
            hr = UI_Xaml_Markup_IXamlMetadataProvider_Call.GetXamlType(provider, name, &result);
        }
        HString.Delete(h);
        Ccw.ComRelease(provider);
        return hr < 0 ? IntPtr.Zero : result;
    }

    /* the full name of the nearest Windows.UI.Xaml base of a XAML type, or null */
    static string? WindowsBase(IntPtr xamlType)
    {
        Ccw.ComAddRef(xamlType);
        for (var t = xamlType; t != IntPtr.Zero;)
        {
            IntPtr h, next = IntPtr.Zero;
            UI_Xaml_Markup_IXamlType_Call.get_FullName(t, &h);
            var name = HString.Read(h);
            HString.Delete(h);
            if (name.StartsWith("Windows.UI.Xaml.", StringComparison.Ordinal)) { Ccw.ComRelease(t); return name; }
            if (UI_Xaml_Markup_IXamlType_Call.get_BaseType(t, &next) < 0) next = IntPtr.Zero;
            Ccw.ComRelease(t);
            t = next;
        }
        return null;
    }
}
