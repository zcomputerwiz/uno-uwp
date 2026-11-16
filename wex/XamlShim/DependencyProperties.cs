// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// Dependency properties registered by native code (C++/CX and C++/WinRT applications, WinUI 2) over Uno's property
// system: PropertyMetadata, DependencyProperty.Register/RegisterAttached, DependencyObject.GetValue/SetValue/...,
// property-changed callbacks into native code.
//
// Native owner and property types are TypeNames, not .NET types. Each native type gets a .NET stand-in type of its own
// (emitted, empty) so that Uno's registry, keyed by owner type and name, keeps the properties of different native
// classes apart; primitive and Windows.UI.Xaml types map to their .NET and Uno counterparts. Values cross as IInspectable:
// property values (IPropertyValue) become .NET values, shims their Uno objects, any other native object a NativeObject
// that keeps it alive, and back.
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

/* .NET types for TypeNames */
static class NativeTypes
{
    static readonly ModuleBuilder module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("XamlShim.NativeTypes"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("XamlShim.NativeTypes");
    static readonly ConcurrentDictionary<string, Type> standIns = new();
    static readonly ConcurrentDictionary<Type, string> names = new();

    static readonly Dictionary<string, Type> primitives = new()
    {
        ["Boolean"] = typeof(bool), ["Char16"] = typeof(char), ["UInt8"] = typeof(byte), ["Int16"] = typeof(short),
        ["UInt16"] = typeof(ushort), ["Int32"] = typeof(int), ["UInt32"] = typeof(uint), ["Int64"] = typeof(long),
        ["UInt64"] = typeof(ulong), ["Single"] = typeof(float), ["Double"] = typeof(double), ["String"] = typeof(string),
        ["Object"] = typeof(object), ["Guid"] = typeof(Guid),
    };

    static NativeTypes()
    {
        /* native owner types are stand-ins: the property's owner is never the Uno object's own type */
        global::Uno.UI.FeatureConfiguration.DependencyProperty.ValidatePropertyOwnerOnReadWrite = false;
    }

    public static Type For(TypeName type)
    {
        var name = HString.Read(type.Name);
        if (type.Kind == 0) return primitives.TryGetValue(name, out var p) ? p : typeof(object);
        if (type.Kind == 1 && Known(name) is { } known) return known;
        /* an application control: its stand-in for Uno's XAML reader, so that the properties it registers are found
         * on the type the reader creates (style setters, attributes) */
        var comma = name.IndexOf(',');
        if (type.Kind == 2 && NativeControls.StandIn(comma > 0 ? name[..comma].Trim() : name) is { } control)
        {
            names.TryAdd(control, name);
            return control;
        }
        return StandIn(name);
    }

    public static Type? UnoType(string winrtName) => Known(winrtName);

    /* Windows.UI.Xaml.X is Uno's Microsoft.UI.Xaml.X; the Windows.Foundation and Windows.UI structs are Uno's own */
    static Type? Known(string name)
    {
        string? uno = name.StartsWith("Windows.UI.Xaml.", StringComparison.Ordinal) ? "Microsoft.UI.Xaml." + name["Windows.UI.Xaml.".Length..]
                    : name.StartsWith("Windows.Foundation.", StringComparison.Ordinal) || name.StartsWith("Windows.UI.", StringComparison.Ordinal) ? name
                    : null;
        if (uno == null) return null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (!assembly.IsDynamic && assembly.GetType(uno) is { } t) return t;
        return null;
    }

    public static Type StandIn(string name) => standIns.GetOrAdd(name, n =>
    {
        var clean = new string(n.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_').ToArray());
        lock (module)
        {
            var t = module.DefineType("Native." + clean, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class).CreateType();
            names[t] = n;
            return t;
        }
    });

    /* the native name of a stand-in, or null */
    public static string? NativeName(Type t) => names.TryGetValue(t, out var n) ? n : null;
}

/* a native object that is not a shim: kept alive while Uno holds the value */
/* Uno binds ({Binding Path}) through .NET reflection and through System.Dynamic for the objects that have none: the
 * members of the application's object are its ICustomPropertyProvider's (the C++/CX compiler makes every [Bindable]
 * class one), as XAML on Windows reads them, and its INotifyPropertyChanged is Uno's. */
sealed unsafe class NativeObject : System.Dynamic.DynamicObject, System.ComponentModel.INotifyPropertyChanged
{
    public readonly IntPtr Pointer;
    /* the object's identity (its IUnknown): the same object read twice (a list's item) is equal */
    readonly IntPtr identity;
    IntPtr provider;                                         /* ICustomPropertyProvider, AddRef'd, or zero */
    bool providerKnown;
    readonly Dictionary<string, IntPtr> properties = new();  /* ICustomProperty by name, AddRef'd, or zero */
    System.ComponentModel.PropertyChangedEventHandler? changed;
    bool subscribed;

    public NativeObject(IntPtr p)
    {
        Pointer = p;
        Ccw.ComAddRef(p);
        if (Ccw.QueryInterface(p, Ccw.IID_IUnknown, out var unknown) >= 0) { identity = unknown; Ccw.ComRelease(unknown); }
        else identity = p;
    }
    ~NativeObject()
    {
        Ccw.ComRelease(Pointer);   /* COM objects of these applications are free-threaded or agile */
        if (provider != IntPtr.Zero) Ccw.ComRelease(provider);
        foreach (var p in properties.Values) if (p != IntPtr.Zero) Ccw.ComRelease(p);
    }
    /* one wrapper per native object while it lives: Uno compares objects by reference (NavigationView's SelectedItem), so the same
     * object read twice (a list's item, the selection) must be the same .NET object */
    static readonly Dictionary<IntPtr, WeakReference<NativeObject>> wrappers = new();
    public static NativeObject For(IntPtr p)
    {
        IntPtr id = p;
        if (Ccw.QueryInterface(p, Ccw.IID_IUnknown, out var unknown) >= 0) { id = unknown; Ccw.ComRelease(unknown); }
        lock (wrappers)
        {
            if (wrappers.TryGetValue(id, out var weak) && weak.TryGetTarget(out var known)) return known;
            var created = new NativeObject(p);
            wrappers[id] = new WeakReference<NativeObject>(created);
            if (wrappers.Count > 4096) foreach (var dead in wrappers.Where(w => !w.Value.TryGetTarget(out _)).Select(w => w.Key).ToList()) wrappers.Remove(dead);
            return created;
        }
    }
    public override bool Equals(object? obj) => obj is NativeObject o && o.identity == identity;
    public override int GetHashCode() => identity.GetHashCode();

    IntPtr Property(string name)
    {
        lock (properties)
        {
            if (properties.TryGetValue(name, out var known)) return known;
            if (!providerKnown)
            {
                providerKnown = true;
                if (Ccw.QueryInterface(Pointer, Iids.UI_Xaml_Data_ICustomPropertyProvider, out var p) >= 0) provider = p;
            }
            IntPtr property = IntPtr.Zero;
            if (provider != IntPtr.Zero)
            {
                var h = HString.Create(name);
                if (UI_Xaml_Data_ICustomPropertyProvider_Call.GetCustomProperty(provider, h, &property) < 0) property = IntPtr.Zero;
                HString.Delete(h);
            }
            if (Ccw.Tracing) Ccw.Log($"native object {Pointer:x} property {name}: provider {provider:x}, property {property:x}");
            return properties[name] = property;
        }
    }

    /* the application's XAML metadata (its IXamlMetadataProvider) knows every [Bindable] type, the component's too: the
     * member by the object's runtime class name, AddRef'd, or zero */
    readonly Dictionary<string, IntPtr> members = new();
    string? className;

    IntPtr Member(string name)
    {
        lock (members)
        {
            if (members.TryGetValue(name, out var known)) return known;
            if (className == null)
            {
                IntPtr h;
                className = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)Pointer)[4])(Pointer, &h) >= 0
                    ? HString.Read(h) : "";
                if (h != IntPtr.Zero) HString.Delete(h);
            }
            var member = className.Length > 0 ? NativeControls.XamlMember(className, name) : IntPtr.Zero;
            if (Ccw.Tracing) Ccw.Log($"native object {Pointer:x} ({className}) member {name}: {member:x}");
            return members[name] = member;
        }
    }

    public override bool TryGetMember(System.Dynamic.GetMemberBinder binder, out object? result)
    {
        result = null;
        if (Member(binder.Name) is var member && member != IntPtr.Zero)
        {
            IntPtr got;
            int status = UI_Xaml_Markup_IXamlMember_Call.GetValue(member, Pointer, &got);
            if (status < 0) { Ccw.Log($"binding {binder.Name}: GetValue {status:x8}"); return false; }
            result = Boxing.Unbox(got);
            if (got != IntPtr.Zero) Ccw.ComRelease(got);
            return true;
        }
        var property = Property(binder.Name);
        if (property == IntPtr.Zero) return false;
        IntPtr value;
        int hr = UI_Xaml_Data_ICustomProperty_Call.GetValue(property, Pointer, &value);
        if (hr < 0) { Ccw.Log($"binding {binder.Name}: GetValue {hr:x8}"); return false; }
        result = Boxing.Unbox(value);
        if (value != IntPtr.Zero) Ccw.ComRelease(value);
        return true;
    }

    public override bool TrySetMember(System.Dynamic.SetMemberBinder binder, object? value)
    {
        if (Member(binder.Name) is var member && member != IntPtr.Zero)
        {
            var b = Boxing.Box(value);
            int status = UI_Xaml_Markup_IXamlMember_Call.SetValue(member, Pointer, b);
            if (b != IntPtr.Zero) Ccw.ComRelease(b);
            if (status < 0) { Ccw.Log($"binding {binder.Name}: SetValue {status:x8}"); return false; }
            return true;
        }
        var property = Property(binder.Name);
        if (property == IntPtr.Zero) return false;
        var boxed = Boxing.Box(value);
        int hr = UI_Xaml_Data_ICustomProperty_Call.SetValue(property, Pointer, boxed);
        if (boxed != IntPtr.Zero) Ccw.ComRelease(boxed);
        if (hr < 0) { Ccw.Log($"binding {binder.Name}: SetValue {hr:x8}"); return false; }
        return true;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            changed += value;
            if (subscribed) return;
            subscribed = true;
            if (Ccw.QueryInterface(Pointer, Iids.UI_Xaml_Data_INotifyPropertyChanged, out var notify) < 0) return;
            var iid = Iids.UI_Xaml_Data_PropertyChangedEventHandler;
            var handler = Ccw.Get(new NativeDelegate(iid, (_, args) =>
            {
                string name = "";
                if (args != IntPtr.Zero && Ccw.QueryInterface(args, Iids.UI_Xaml_Data_IPropertyChangedEventArgs, out var e) >= 0)
                {
                    IntPtr h;
                    if (UI_Xaml_Data_IPropertyChangedEventArgs_Call.get_PropertyName(e, &h) >= 0) { name = HString.Read(h); HString.Delete(h); }
                    Ccw.ComRelease(e);
                }
                changed?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }), iid);
            long token;
            int hr = UI_Xaml_Data_INotifyPropertyChanged_Call.add_PropertyChanged(notify, handler, &token);
            if (hr < 0) Ccw.Log($"INotifyPropertyChanged.PropertyChanged: {hr:x8}");
            Ccw.ComRelease(handler);
            Ccw.ComRelease(notify);
        }
        remove { changed -= value; }
    }
}

/* the IInspectable that stands for DependencyProperty.UnsetValue */
sealed class UnsetValueObject : ShimObject
{
    public static readonly UnsetValueObject Instance = new();
    public override string RuntimeClassName => "Windows.UI.Xaml.DependencyProperty.UnsetValue";
}

static unsafe class Values
{
    /* the value of an IInspectable for a property of type target */
    public static object? ToUno(IntPtr p, Type target)
    {
        if (p == IntPtr.Zero) return null;
        if (Ccw.Unwrap(p) is UnsetValueObject) return Mux.DependencyProperty.UnsetValue;
        if (Shim.UnoOf(p) is { } uno) return uno;
        object? value;
        if (Ccw.QueryInterface(p, Iids.Foundation_IPropertyValue, out var pv) >= 0)
        {
            Ccw.ComRelease(pv);
            try { value = Boxing.Unbox(p); }
            catch (NotSupportedException) when (target.IsEnum)
            {
                /* an IReference<enum> box: get_Value is the first method after IInspectable's */
                int v;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)p)[6])(p, &v));
                value = v;
            }
            catch (NotSupportedException)
            {
                /* another box (an enum or struct of the application, OtherType): kept as it is, given back unchanged */
                return NativeObject.For(p);
            }
        }
        else if (target.IsEnum)
        {
            int v;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)p)[6])(p, &v));
            value = v;
        }
        else return NativeObject.For(p);
        return Convert(value, target);
    }

    static object? Convert(object? value, Type target)
    {
        if (value == null || target == typeof(object) || target.IsInstanceOfType(value)) return value;
        if (target.IsEnum && value is IConvertible) return Enum.ToObject(target, System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        if (target.IsPrimitive && value is IConvertible) return System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        return value;
    }

    /* an AddRef'd IInspectable for a value */
    public static IntPtr ToAbi(object? value)
    {
        switch (value)
        {
        case null: return IntPtr.Zero;
        case NativeObject n: Ccw.ComAddRef(n.Pointer); return n.Pointer;
        case Enum e: return Boxing.Box(System.Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (ReferenceEquals(value, Mux.DependencyProperty.UnsetValue)) return Ccw.Get(UnsetValueObject.Instance);
        return Boxing.Box(value);
    }
}

// ---- PropertyMetadata ----

public unsafe partial class UI_Xaml_PropertyMetadata
{
    /* as given by native code; the Uno metadata is made when the property is registered (its type known then) */
    internal IntPtr NativeDefault, NativeChanged, NativeCreateDefault;
    readonly ConcurrentDictionary<Type, Mux.PropertyMetadata> forType = new();

    internal void Set(IntPtr defaultValue, IntPtr changed, IntPtr createDefault)
    {
        foreach (var p in new[] { defaultValue, changed, createDefault }) if (p != IntPtr.Zero) Ccw.ComAddRef(p);
        NativeDefault = defaultValue; NativeChanged = changed; NativeCreateDefault = createDefault;
    }

    /* the Uno metadata of a property of type t registered with this metadata; property is filled in once known */
    internal Mux.PropertyMetadata ForUno(Type t, Func<UI_Xaml_DependencyProperty?> property)
    {
        if (UnoObject is Mux.PropertyMetadata existing && NativeDefault == IntPtr.Zero && NativeChanged == IntPtr.Zero &&
            NativeCreateDefault == IntPtr.Zero)
            return existing;
        return forType.GetOrAdd(t, type =>
        {
            Mux.PropertyChangedCallback? changed = NativeChanged == IntPtr.Zero ? null :
                (d, e) => DependencyProperties.RaiseChanged(NativeChanged, d, property(), e.OldValue, e.NewValue);
            Mux.PropertyMetadata m;
            if (NativeCreateDefault != IntPtr.Zero)
            {
                var create = NativeCreateDefault;
                m = Mux.PropertyMetadata.Create(() =>
                {
                    IntPtr v;
                    Marshal.ThrowExceptionForHR(UI_Xaml_CreateDefaultValueCallback_Call.Invoke(create, &v));
                    var value = Values.ToUno(v, type);
                    if (v != IntPtr.Zero) Ccw.ComRelease(v);
                    return value;
                }, changed);
            }
            else
            {
                var value = NativeDefault != IntPtr.Zero ? Values.ToUno(NativeDefault, type)
                          : type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
                m = changed != null ? new Mux.PropertyMetadata(value, changed) : new Mux.PropertyMetadata(value);
            }
            UnoObject ??= m;
            return m;
        });
    }

    int IUI_Xaml_IPropertyMetadata_Abi.get_DefaultValue(IntPtr* result)
    {
        if (NativeDefault != IntPtr.Zero) { Ccw.ComAddRef(NativeDefault); *result = NativeDefault; return 0; }
        *result = Values.ToAbi((UnoObject as Mux.PropertyMetadata)?.DefaultValue);
        return 0;
    }

    int IUI_Xaml_IPropertyMetadata_Abi.get_CreateDefaultValueCallback(IntPtr* result)
    {
        if (NativeCreateDefault != IntPtr.Zero) Ccw.ComAddRef(NativeCreateDefault);
        *result = NativeCreateDefault;
        return 0;
    }
}

public unsafe partial class UI_Xaml_PropertyMetadata_Factory
{
    static int Make(IntPtr defaultValue, IntPtr changed, IntPtr createDefault, IntPtr outer, IntPtr* inner, IntPtr* result)
    {
        var m = new UI_Xaml_PropertyMetadata();
        m.Set(defaultValue, changed, createDefault);
        return Shim.Compose(m, outer, inner, result);
    }

    int IUI_Xaml_IPropertyMetadataFactory_Abi.CreateInstanceWithDefaultValue(IntPtr defaultValue, IntPtr outer, IntPtr* inner, IntPtr* result) =>
        Make(defaultValue, IntPtr.Zero, IntPtr.Zero, outer, inner, result);
    int IUI_Xaml_IPropertyMetadataFactory_Abi.CreateInstanceWithDefaultValueAndCallback(IntPtr defaultValue, IntPtr changed, IntPtr outer, IntPtr* inner, IntPtr* result) =>
        Make(defaultValue, changed, IntPtr.Zero, outer, inner, result);
    int IUI_Xaml_IPropertyMetadataStatics_Abi.CreateWithDefaultValue(IntPtr defaultValue, IntPtr* result) =>
        Make(defaultValue, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, result);
    int IUI_Xaml_IPropertyMetadataStatics_Abi.CreateWithDefaultValueAndCallback(IntPtr defaultValue, IntPtr changed, IntPtr* result) =>
        Make(defaultValue, changed, IntPtr.Zero, IntPtr.Zero, null, result);
    int IUI_Xaml_IPropertyMetadataStatics_Abi.CreateWithFactory(IntPtr create, IntPtr* result) =>
        Make(IntPtr.Zero, IntPtr.Zero, create, IntPtr.Zero, null, result);
    int IUI_Xaml_IPropertyMetadataStatics_Abi.CreateWithFactoryAndCallback(IntPtr create, IntPtr changed, IntPtr* result) =>
        Make(IntPtr.Zero, changed, create, IntPtr.Zero, null, result);
}

/* new FontFamily(name): Uno's FontFamily of that name (composable) */
public unsafe partial class UI_Xaml_Media_FontFamily_Factory
{
    int IUI_Xaml_Media_IFontFamilyFactory_Abi.CreateInstanceWithName(IntPtr familyName, IntPtr outer, IntPtr* inner, IntPtr* result) =>
        Shim.Compose(new UI_Xaml_Media_FontFamily { UnoObject = new Mux.Media.FontFamily(PackageUri.Map(HString.Read(familyName)) ?? HString.Read(familyName)) },
                     outer, inner, result);
}

/* new PropertyPath(path): Uno's PropertyPath of that path */
public unsafe partial class UI_Xaml_PropertyPath_Factory
{
    int IUI_Xaml_IPropertyPathFactory_Abi.CreateInstance(IntPtr path, IntPtr* result)
    {
        *result = Ccw.Get(Shim.For(new Mux.PropertyPath(HString.Read(path))), Iids.UI_Xaml_IPropertyPath);
        return 0;
    }
}

/* Style.TargetType: the type as the application names it */
public unsafe partial class UI_Xaml_Style
{
    int IUI_Xaml_IStyle_Abi.get_TargetType(TypeName* result)
    {
        *result = NativeObjects.ToTypeName(((Mux.Style)UnoObject!).TargetType);
        return 0;
    }
    int IUI_Xaml_IStyle_Abi.put_TargetType(TypeName value)
    {
        ((Mux.Style)UnoObject!).TargetType = NativeTypes.For(value);
        return 0;
    }
}

/* new PropertyChangedEventArgs(name): what an application's INotifyPropertyChanged raises (C++/CX view models make
 * one for every property they change); composable */
public unsafe partial class UI_Xaml_Data_PropertyChangedEventArgs
{
    public string PropertyName { get; init; } = "";
}

public unsafe partial class UI_Xaml_Data_PropertyChangedEventArgs_Factory
{
    int IUI_Xaml_Data_IPropertyChangedEventArgsFactory_Abi.CreateInstance(IntPtr name, IntPtr outer, IntPtr* inner, IntPtr* result) =>
        Shim.Compose(new UI_Xaml_Data_PropertyChangedEventArgs { PropertyName = HString.Read(name) ?? "" }, outer, inner, result);
}

// ---- DependencyProperty ----

public unsafe partial class UI_Xaml_DependencyProperty
{
    internal Type PropertyType = typeof(object);
    internal UI_Xaml_PropertyMetadata? NativeMetadata;
    internal Mux.DependencyProperty Uno => (Mux.DependencyProperty)UnoObject!;

    int IUI_Xaml_IDependencyProperty_Abi.GetMetadata(TypeName forType, IntPtr* result)
    {
        if (NativeMetadata != null) { *result = Ccw.Get(NativeMetadata, Iids.UI_Xaml_IPropertyMetadata); return 0; }
        var m = Uno.GetMetadata(NativeTypes.For(forType));
        *result = m != null && Shim.TryFor(m) is { } shim ? Ccw.Get(shim, Iids.UI_Xaml_IPropertyMetadata) : IntPtr.Zero;
        return 0;
    }
}

public unsafe partial class UI_Xaml_DependencyProperty_Factory
{
    int IUI_Xaml_IDependencyPropertyStatics_Abi.get_UnsetValue(IntPtr* result)
    {
        *result = Ccw.Get(UnsetValueObject.Instance);
        return 0;
    }

    int IUI_Xaml_IDependencyPropertyStatics_Abi.Register(IntPtr name, TypeName propertyType, TypeName ownerType, IntPtr metadata, IntPtr* result) =>
        DependencyProperties.Register(name, propertyType, ownerType, metadata, false, result);

    int IUI_Xaml_IDependencyPropertyStatics_Abi.RegisterAttached(IntPtr name, TypeName propertyType, TypeName ownerType, IntPtr metadata, IntPtr* result) =>
        DependencyProperties.Register(name, propertyType, ownerType, metadata, true, result);
}

public unsafe partial class UI_Xaml_DependencyPropertyChangedEventArgs
{
    internal UI_Xaml_DependencyProperty? Property;
    internal object? OldValue, NewValue;

    int IUI_Xaml_IDependencyPropertyChangedEventArgs_Abi.get_Property(IntPtr* result)
    {
        *result = Property != null ? Ccw.Get(Property, Iids.UI_Xaml_IDependencyProperty) : IntPtr.Zero;
        return 0;
    }
    int IUI_Xaml_IDependencyPropertyChangedEventArgs_Abi.get_OldValue(IntPtr* result) { *result = Values.ToAbi(OldValue); return 0; }
    int IUI_Xaml_IDependencyPropertyChangedEventArgs_Abi.get_NewValue(IntPtr* result) { *result = Values.ToAbi(NewValue); return 0; }
}

static unsafe class DependencyProperties
{
    /* the properties registered here, by owner type and name (the stand-in of an application control finds its
     * properties here for Uno's XAML reader) */
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Owner, string Name), Mux.DependencyProperty> Registered = new();

    public static int Register(IntPtr name, TypeName propertyType, TypeName ownerType, IntPtr metadata, bool attached, IntPtr* result)
    {
        var n = HString.Read(name);
        var pt = NativeTypes.For(propertyType);
        var ot = NativeTypes.For(ownerType);
        UI_Xaml_PropertyMetadata? md = null;
        if (metadata != IntPtr.Zero && (md = Ccw.Unwrap(metadata) as UI_Xaml_PropertyMetadata) == null)
            return unchecked((int)0x80070057);   /* E_INVALIDARG: metadata of another implementation */
        UI_Xaml_DependencyProperty? shim = null;
        Mux.DependencyProperty dp;
        try
        {
            var unoMetadata = md?.ForUno(pt, () => shim);
            dp = attached ? Mux.DependencyProperty.RegisterAttached(n, pt, ot, unoMetadata)
                          : Mux.DependencyProperty.Register(n, pt, ot, unoMetadata);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"{HString.Read(ownerType.Name)}.{n} ({HString.Read(propertyType.Name)}, " +
                                                $"kind {propertyType.Kind}): {e.Message}", e);
        }
        shim = new UI_Xaml_DependencyProperty { UnoObject = dp, PropertyType = pt, NativeMetadata = md };
        Ccw.Wrap(dp, () => shim);
        Registered[(ot, n)] = dp;
        Ccw.Trace($"{(attached ? "attached " : "")}property {HString.Read(ownerType.Name)}.{n} ({HString.Read(propertyType.Name)})");
        *result = Ccw.Get(shim, Iids.UI_Xaml_IDependencyProperty);
        return 0;
    }

    /* the shim of a DependencyProperty: one registered here, or one of Uno's (type from its registration) */
    public static UI_Xaml_DependencyProperty Of(IntPtr dp)
    {
        var s = Ccw.Unwrap(dp) as UI_Xaml_DependencyProperty ?? throw new ArgumentException("not a dependency property");
        if (s.PropertyType == typeof(object) && s.UnoObject is Mux.DependencyProperty p &&
            typeof(Mux.DependencyProperty).GetProperty("Type", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(p) is Type t)
            s.PropertyType = t;
        return s;
    }

    /* PropertyChangedCallback(d, e) into native code: d is the native object (the application's own when composed) */
    public static void RaiseChanged(IntPtr callback, Mux.DependencyObject d, UI_Xaml_DependencyProperty? property, object? oldValue, object? newValue)
    {
        try
        {
            if (Shim.TryFor(d) is not { } target) { Ccw.Log($"property changed on {d.GetType().Name}: no shim"); return; }
            var o = Ccw.Get(target, Iids.UI_Xaml_IDependencyObject);
            var args = new UI_Xaml_DependencyPropertyChangedEventArgs { Property = property, OldValue = oldValue, NewValue = newValue };
            var a = Ccw.Get(args, Iids.UI_Xaml_IDependencyPropertyChangedEventArgs);
            int hr = UI_Xaml_PropertyChangedCallback_Call.Invoke(callback, o, a);
            if (hr < 0) Ccw.Log($"property changed callback returned {hr:x8}");
            Ccw.ComRelease(a);
            Ccw.ComRelease(o);
        }
        catch (Exception e) { Ccw.Log($"property changed callback failed: {e}"); }
    }
}

// ---- DependencyObject ----

public unsafe partial class UI_Xaml_DependencyObject
{
    Mux.DependencyObject Do => (Mux.DependencyObject)UnoObject!;

    /* the CoreDispatcher of the view's thread (Wine's, as Window.Dispatcher gives): every element lives on that thread;
     * null without a view */
    int IUI_Xaml_IDependencyObject_Abi.get_Dispatcher(IntPtr* result)
    {
        *result = IntPtr.Zero;
        if (FrameworkView.CoreWindow == IntPtr.Zero) return 0;
        return UI_Core_ICoreWindow_Call.get_Dispatcher(FrameworkView.CoreWindow, result);
    }

    int IUI_Xaml_IDependencyObject_Abi.GetValue(IntPtr dp, IntPtr* result)
    {
        *result = Values.ToAbi(Do.GetValue(DependencyProperties.Of(dp).Uno));
        return 0;
    }

    int IUI_Xaml_IDependencyObject_Abi.SetValue(IntPtr dp, IntPtr value)
    {
        var p = DependencyProperties.Of(dp);
        Do.SetValue(p.Uno, Values.ToUno(value, p.PropertyType));
        return 0;
    }

    int IUI_Xaml_IDependencyObject_Abi.ClearValue(IntPtr dp) { Do.ClearValue(DependencyProperties.Of(dp).Uno); return 0; }

    int IUI_Xaml_IDependencyObject_Abi.ReadLocalValue(IntPtr dp, IntPtr* result)
    {
        *result = Values.ToAbi(Do.ReadLocalValue(DependencyProperties.Of(dp).Uno));
        return 0;
    }

    int IUI_Xaml_IDependencyObject_Abi.GetAnimationBaseValue(IntPtr dp, IntPtr* result)
    {
        *result = Values.ToAbi(Do.GetAnimationBaseValue(DependencyProperties.Of(dp).Uno));
        return 0;
    }

    int IUI_Xaml_IDependencyObject2_Abi.RegisterPropertyChangedCallback(IntPtr dp, IntPtr callback, long* result)
    {
        var p = DependencyProperties.Of(dp);
        Ccw.ComAddRef(callback);
        *result = Do.RegisterPropertyChangedCallback(p.Uno, (sender, property) =>
        {
            if (Shim.TryFor(sender) is not { } s) return;
            var o = Ccw.Get(s, Iids.UI_Xaml_IDependencyObject);
            var d = Ccw.Get(p, Iids.UI_Xaml_IDependencyProperty);
            int hr = UI_Xaml_DependencyPropertyChangedCallback_Call.Invoke(callback, o, d);
            if (hr < 0) Ccw.Log($"property changed callback returned {hr:x8}");
            Ccw.ComRelease(d);
            Ccw.ComRelease(o);
        });
        return 0;
    }

    int IUI_Xaml_IDependencyObject2_Abi.UnregisterPropertyChangedCallback(IntPtr dp, long token)
    {
        Do.UnregisterPropertyChangedCallback(DependencyProperties.Of(dp).Uno, token);
        return 0;
    }
}
