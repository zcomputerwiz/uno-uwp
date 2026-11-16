// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// Values and collections across the ABI: boxing through the system's Windows.Foundation.PropertyValue (Wine's
// wintypes), and IVector<T>/IIterable<T>/IObservableVector<T> over a Uno collection (Panel.Children: T is UIElement;
// ItemsControl.Items: T is Object). Generic interfaces are not in the generated code: their IIDs come from their
// pinterface signatures and their vtables are written here.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public static unsafe class Boxing
{
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, Guid* iid, IntPtr* factory);

    static IntPtr statics;

    static IntPtr Statics()
    {
        if (statics != IntPtr.Zero) return statics;
        var name = HString.Create("Windows.Foundation.PropertyValue");
        var iid = Iids.Foundation_IPropertyValueStatics;
        IntPtr f;
        int hr = RoGetActivationFactory(name, &iid, &f);
        HString.Delete(name);
        Marshal.ThrowExceptionForHR(hr);
        return statics = f;
    }

    /* an AddRef'd IInspectable for a managed value: shims for framework objects, PropertyValue for the rest */
    public static IntPtr Box(object? value)
    {
        IntPtr result = IntPtr.Zero;
        /* the application's objects carried through Uno: the application's object itself */
        if (value is NativeObject o) { Ccw.ComAddRef(o.Pointer); return o.Pointer; }
        if (value is NativeCommand c) { Ccw.ComAddRef(c.Pointer); return c.Pointer; }
        if (value is NativeList l) { Ccw.ComAddRef(l.Pointer); return l.Pointer; }
        /* a stand-in of an application object that is not an element (a converter): the application's object itself */
        if (value is NativeStandInObject && NativeControls.NativeOf(value) is var native && native != IntPtr.Zero)
        {
            Ccw.ComAddRef(native);
            return native;
        }
        /* framework objects (elements, event arguments, ...) through their shims */
        if (value != null && value is not string && !value.GetType().IsPrimitive && Shim.TryFor(value) is { } shim)
            return Ccw.Get(shim);
        switch (value)
        {
        case null: return IntPtr.Zero;
        case string s:
        {
            var h = HString.Create(s);
            Marshal.ThrowExceptionForHR(Foundation_IPropertyValueStatics_Call.CreateString(Statics(), h, &result));
            HString.Delete(h);
            return result;
        }
        case bool b: Marshal.ThrowExceptionForHR(Foundation_IPropertyValueStatics_Call.CreateBoolean(Statics(), (byte)(b ? 1 : 0), &result)); return result;
        case double v: Marshal.ThrowExceptionForHR(Foundation_IPropertyValueStatics_Call.CreateDouble(Statics(), v, &result)); return result;
        case int i: Marshal.ThrowExceptionForHR(Foundation_IPropertyValueStatics_Call.CreateInt32(Statics(), i, &result)); return result;
        default: throw new NotSupportedException("cannot box " + value.GetType().FullName);
        }
    }

    /* the managed value behind an IInspectable: the Uno object of a shim, or the value of an IPropertyValue */
    public static object? Unbox(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        if (Shim.UnoOf(p) is { } uno) return uno;
        if (Ccw.QueryInterface(p, Iids.Foundation_IPropertyValue, out var pv) < 0)
        {
            /* the application's own objects: a command as .NET's ICommand, anything else kept as it is */
            if (Ccw.QueryInterface(p, Iids.UI_Xaml_Input_ICommand, out var command) >= 0) return new NativeCommand(command);
            if (NativeList.From(p) is { } list) return list;
            return new NativeObject(p);
        }
        try
        {
            int type;
            Marshal.ThrowExceptionForHR(Foundation_IPropertyValue_Call.get_Type(pv, &type));
            switch (type)
            {
            case 4: { int v; Marshal.ThrowExceptionForHR(Foundation_IPropertyValue_Call.GetInt32(pv, &v)); return v; }
            case 9: { double v; Marshal.ThrowExceptionForHR(Foundation_IPropertyValue_Call.GetDouble(pv, &v)); return v; }
            case 11: { byte v; Marshal.ThrowExceptionForHR(Foundation_IPropertyValue_Call.GetBoolean(pv, &v)); return v != 0; }
            case 12:
            {
                IntPtr h;
                Marshal.ThrowExceptionForHR(Foundation_IPropertyValue_Call.GetString(pv, &h));
                var s = HString.Read(h);
                HString.Delete(h);
                return s;
            }
            case 20 when Ccw.QueryInterface(p, ReferenceTypeName, out var reference) >= 0:
            {
                /* a boxed TypeName (Control.DefaultStyleKey): the .NET type standing for it */
                TypeName name;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, TypeName*, int>)(*(IntPtr**)reference)[6])(reference, &name);
                Ccw.ComRelease(reference);
                Marshal.ThrowExceptionForHR(hr);
                var text = HString.Read(name.Name);
                var comma = text.IndexOf(',');
                var full = comma > 0 ? text[..comma].Trim() : text;
                Type? result = name.Kind == 2 ? NativeControls.StandIn(full) : null;
                result ??= NativeTypes.For(name);
                HString.Delete(name.Name);
                return result;
            }
            default: throw new NotSupportedException($"property value of type {type}");
            }
        }
        finally
        {
            Ccw.ComRelease(pv);
        }
    }

    /* IReference<Windows.UI.Xaml.Interop.TypeName> */
    static readonly Guid ReferenceTypeName = PInterface.Iid("pinterface({61c17706-2d65-11e0-9ae8-d48564015472};" +
        "struct(Windows.UI.Xaml.Interop.TypeName;string;enum(Windows.UI.Xaml.Interop.TypeKind;i4)))");

    public static double? UnboxDouble(IntPtr p) => p == IntPtr.Zero ? null : Convert.ToDouble(Unbox(p), System.Globalization.CultureInfo.InvariantCulture);
}

/* the list operations the vector needs, over any IList<T> of Uno */
public interface IAbiList
{
    int Count { get; }
    object? this[int index] { get; set; }
    int IndexOf(object? item);
    void Insert(int index, object? item);
    void RemoveAt(int index);
    void Add(object? item);
    void Clear();
}

public sealed class AbiList<T>(IList<T> inner) : IAbiList
{
    public int Count => inner.Count;
    public object? this[int index] { get => inner[index]; set => inner[index] = (T)value!; }
    public int IndexOf(object? item) => item is T t ? inner.IndexOf(t) : -1;
    public void Insert(int index, object? item) => inner.Insert(index, (T)item!);
    public void RemoveAt(int index) => inner.RemoveAt(index);
    public void Add(object? item) => inner.Add((T)item!);
    public void Clear() => inner.Clear();
}

/* IVector<T>, IIterable<T>, IObservableVector<T> over a Uno list; T is a runtime class (its items are shims, passed
 * as its default interface: UIElement, SetterBase) or Object (boxed values too) */
public sealed unsafe class VectorShim : ShimObject
{
    const string IVector = "{913337e9-11a1-4345-a3a2-4e7f956e222d}", IIterable = "{faa585ea-6214-4217-afda-7f46de5869b3}",
                 IObservableVector = "{5917eb53-50b4-4a0d-b309-65862b3f1dbc}";

    readonly IAbiList list;
    readonly Guid? elementIid;    /* the default interface of T when T is a runtime class; null for Object */
    readonly string className;
    readonly (Guid, IntPtr, Func<object, bool>)[] table;

    public const string ObjectSig = "cinterface(IInspectable)";

    public VectorShim(IAbiList list, bool elements, string className)
        : this(list, elements ? ("Windows.UI.Xaml.UIElement", Iids.UI_Xaml_IUIElement) : null, className) { }

    /* element: T's runtime class name and default interface, null for Object; extra: the collection class's own
     * interfaces (SetterBaseCollection's IsSealed) */
    public VectorShim(IAbiList list, (string Class, Guid Iid)? element, string className,
                      params (Guid, IntPtr, Func<object, bool>)[] extra)
    {
        this.list = list;
        elementIid = element?.Iid;
        this.className = className;
        var t = element is { } e ? $"rc({e.Class};{PInterface.Sig(e.Iid)})" : ObjectSig;
        table = [(PInterface.Iid($"pinterface({IVector};{t})"), VectorVtbl, _ => true),
                 (PInterface.Iid($"pinterface({IIterable};{t})"), IterableVtbl, _ => true),
                 (PInterface.Iid($"pinterface({IObservableVector};{t})"), ObservableVtbl, _ => true), .. extra];
    }

    public override string RuntimeClassName => className;
    internal override (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]? CustomInterfaces => table;

    /* the Uno collection, for the collection class's own interfaces */
    public object? Source { get; init; }

    IntPtr ToNative(object? item)
    {
        if (elementIid is not { } iid) return Boxing.Box(item);
        return item is Mux.DependencyObject d ? Ccw.Get(Shim.For(d), iid) : IntPtr.Zero;
    }

    object? FromNative(IntPtr p) => elementIid != null ? Shim.UnoOf(p) : Boxing.Unbox(p);

    static VectorShim Self(IntPtr block) => (VectorShim)Ccw.Target(block);

    static int Run(string what, Func<int> body)
    {
        try { return body(); }
        catch (ArgumentOutOfRangeException) { return unchecked((int)0x8000000B); }   /* E_BOUNDS */
        catch (Exception e) { return Ccw.Fail(what, e); }
    }

    // ---- IVector<T>: GetAt, get_Size, GetView, IndexOf, SetAt, InsertAt, RemoveAt, Append, RemoveAtEnd, Clear,
    //      GetMany, ReplaceAll ----

    static readonly IntPtr VectorVtbl = MakeVector();

    static IntPtr MakeVector()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(VectorShim), sizeof(IntPtr) * 18);
        Ccw.FillInspectable(v);
        v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&GetAt;
        v[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetSize;
        v[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetView;
        v[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, byte*, int>)&IndexOf;
        v[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)&SetAt;
        v[11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)&InsertAt;
        v[12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&RemoveAt;
        v[13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&Append;
        v[14] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&RemoveAtEnd;
        v[15] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Clear;
        v[16] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, int>)&GetMany;
        v[17] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&ReplaceAll;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetAt(IntPtr b, uint index, IntPtr* item) => Run("IVector.GetAt", () => { var s = Self(b); *item = s.ToNative(s.list[(int)index]); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetSize(IntPtr b, uint* size) => Run("IVector.get_Size", () => { *size = (uint)Self(b).list.Count; return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetView(IntPtr b, IntPtr* view) { Ccw.NotImplemented("IVector.GetView"); *view = IntPtr.Zero; return unchecked((int)0x80004001); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int IndexOf(IntPtr b, IntPtr item, uint* index, byte* found) => Run("IVector.IndexOf", () =>
    {
        var s = Self(b);
        int i = s.list.IndexOf(s.FromNative(item));
        *found = (byte)(i >= 0 ? 1 : 0);
        *index = (uint)Math.Max(i, 0);
        return 0;
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int SetAt(IntPtr b, uint index, IntPtr item) => Run("IVector.SetAt", () => { var s = Self(b); s.list[(int)index] = s.FromNative(item); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int InsertAt(IntPtr b, uint index, IntPtr item) => Run("IVector.InsertAt", () => { var s = Self(b); s.list.Insert((int)index, s.FromNative(item)); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int RemoveAt(IntPtr b, uint index) => Run("IVector.RemoveAt", () => { Self(b).list.RemoveAt((int)index); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Append(IntPtr b, IntPtr item) => Run("IVector.Append", () => { var s = Self(b); s.list.Add(s.FromNative(item)); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int RemoveAtEnd(IntPtr b) => Run("IVector.RemoveAtEnd", () => { var s = Self(b); s.list.RemoveAt(s.list.Count - 1); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Clear(IntPtr b) => Run("IVector.Clear", () => { Self(b).list.Clear(); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetMany(IntPtr b, uint start, uint capacity, IntPtr* items, uint* actual) => Run("IVector.GetMany", () =>
    {
        var s = Self(b);
        uint n = 0;
        for (int i = (int)start; i < s.list.Count && n < capacity; i++) items[n++] = s.ToNative(s.list[i]);
        *actual = n;
        return 0;
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int ReplaceAll(IntPtr b, uint count, IntPtr* items) => Run("IVector.ReplaceAll", () =>
    {
        var s = Self(b);
        s.list.Clear();
        for (uint i = 0; i < count; i++) s.list.Add(s.FromNative(items[i]));
        return 0;
    });

    // ---- IIterable<T>.First, IObservableVector<T> (VectorChanged: accepted, not raised yet) ----

    static readonly IntPtr IterableVtbl = MakeIterable();
    static readonly IntPtr ObservableVtbl = MakeObservable();

    static IntPtr MakeIterable()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(VectorShim), sizeof(IntPtr) * 7);
        Ccw.FillInspectable(v);
        v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&First;
        return (IntPtr)v;
    }

    static IntPtr MakeObservable()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(VectorShim), sizeof(IntPtr) * 8);
        Ccw.FillInspectable(v);
        v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)&AddVectorChanged;
        v[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long, int>)&RemoveVectorChanged;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int First(IntPtr b, IntPtr* iterator) { Ccw.NotImplemented("IIterable.First"); *iterator = IntPtr.Zero; return unchecked((int)0x80004001); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int AddVectorChanged(IntPtr b, IntPtr handler, long* token) { Ccw.NotImplemented("IObservableVector.add_VectorChanged (accepted, not raised)"); *token = Shim.Token(); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int RemoveVectorChanged(IntPtr b, long token) => 0;
}

/* Style.Setters: a SetterBaseCollection, IVector<SetterBase> over Uno's, with its IsSealed */
public unsafe partial class UI_Xaml_Style
{
    static readonly Guid ISetterBaseCollection = new("03c40ca8-909e-4117-811c-a4529496bdf1");
    static readonly IntPtr SetterBaseCollectionVtbl = MakeSetterBaseCollection();
    VectorShim? setters;

    static IntPtr MakeSetterBaseCollection()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(UI_Xaml_Style), sizeof(IntPtr) * 7);
        Ccw.FillInspectable(v);
        v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)&GetIsSealed;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetIsSealed(IntPtr b, byte* result)
    {
        *result = ((VectorShim)Ccw.Target(b)).Source is Mux.SetterBaseCollection c && c.IsSealed ? (byte)1 : (byte)0;
        return 0;
    }

    int IUI_Xaml_IStyle_Abi.get_Setters(IntPtr* result)
    {
        var c = ((Mux.Style)UnoObject!).Setters;
        setters ??= new VectorShim(new AbiList<Mux.SetterBase>(c), ("Windows.UI.Xaml.SetterBase", Iids.UI_Xaml_ISetterBase),
                                   "Windows.UI.Xaml.SetterBaseCollection", (ISetterBaseCollection, SetterBaseCollectionVtbl, _ => true))
                   { Source = c };
        *result = Ccw.GetDefault(setters);
        return 0;
    }
}
