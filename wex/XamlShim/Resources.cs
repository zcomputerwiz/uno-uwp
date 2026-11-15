// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// ResourceDictionary across the ABI: its generated interfaces plus IMap<Object, Object> (Lookup, Size, HasKey, Insert,
// Remove, Clear) over the Uno dictionary. Lookup and HasKey search merged and theme dictionaries too, as Uno's
// dictionary does (and Windows' Lookup); Size counts the dictionary's own entries.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public sealed unsafe partial class UI_Xaml_ResourceDictionary
{
    const string IMap = "{3c2925fe-8519-45c1-aa79-197b6718c1c1}", IIterable = "{faa585ea-6214-4217-afda-7f46de5869b3}",
                 IKeyValuePair = "{02b51929-c1c4-4a7e-8940-0312b5c18500}", Object = "cinterface(IInspectable)";

    /* after the vtables: static fields are initialized in the order written */
    static readonly IntPtr MapVtbl = MakeMap();
    static readonly IntPtr IterableVtbl = MakeIterable();

    static readonly (Guid, IntPtr, Func<object, bool>)[] table =
        [(PInterface.Iid($"pinterface({IMap};{Object};{Object})"), MapVtbl, _ => true),
         (PInterface.Iid($"pinterface({IIterable};pinterface({IKeyValuePair};{Object};{Object}))"), IterableVtbl, _ => true)];

    internal override (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]? CustomInterfaces => table;

    static Mux.ResourceDictionary Dict(IntPtr block) => (Mux.ResourceDictionary)((ShimObject)Ccw.Target(block)).UnoObject!;

    static int Run(string what, Func<int> body)
    {
        if (Ccw.Calls) Ccw.Log("call ResourceDictionary " + what);
        try { return body(); }
        catch (Exception e) { return Ccw.Fail(what, e); }
    }

    // ---- IMap<K,V>: Lookup, get_Size, HasKey, GetView, Insert, Remove, Clear ----

    static IntPtr MakeMap()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(UI_Xaml_ResourceDictionary), sizeof(IntPtr) * 13);
        Ccw.FillInspectable(v);
        v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)&Lookup;
        v[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetSize;
        v[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)&HasKey;
        v[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetView;
        v[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, byte*, int>)&Insert;
        v[11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&Remove;
        v[12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Clear;
        return (IntPtr)v;
    }

    static IntPtr MakeIterable()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(UI_Xaml_ResourceDictionary), sizeof(IntPtr) * 7);
        Ccw.FillInspectable(v);
        v[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&First;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Lookup(IntPtr b, IntPtr key, IntPtr* value) => Run("IMap.Lookup", () =>
    {
        *value = IntPtr.Zero;
        var k = Boxing.Unbox(key);
        if (k == null || !Dict(b).TryGetValue(k, out var v))
        {
            Ccw.Log($"ResourceDictionary.Lookup {k}: not found");
            return unchecked((int)0x8000000B);   /* E_BOUNDS */
        }
        *value = Boxing.Box(v);
        return 0;
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetSize(IntPtr b, uint* size) => Run("IMap.get_Size", () => { *size = (uint)Dict(b).Count; return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int HasKey(IntPtr b, IntPtr key, byte* found) => Run("IMap.HasKey", () =>
    {
        var k = Boxing.Unbox(key);
        *found = (byte)(k != null && Dict(b).TryGetValue(k, out _) ? 1 : 0);
        return 0;
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetView(IntPtr b, IntPtr* view) { Ccw.NotImplemented("IMap.GetView"); *view = IntPtr.Zero; return unchecked((int)0x80004001); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Insert(IntPtr b, IntPtr key, IntPtr value, byte* replaced) => Run("IMap.Insert", () =>
    {
        var d = Dict(b);
        var k = Boxing.Unbox(key) ?? throw new ArgumentNullException(nameof(key));
        *replaced = (byte)(d.ContainsKey(k) ? 1 : 0);
        d[k] = Boxing.Unbox(value);
        return 0;
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Remove(IntPtr b, IntPtr key) => Run("IMap.Remove", () =>
    {
        var k = Boxing.Unbox(key);
        return k != null && Dict(b).Remove(k) ? 0 : unchecked((int)0x8000000B);
    });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Clear(IntPtr b) => Run("IMap.Clear", () => { Dict(b).Clear(); return 0; });
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int First(IntPtr b, IntPtr* iterator) { Ccw.NotImplemented("IIterable<IKeyValuePair>.First"); *iterator = IntPtr.Zero; return unchecked((int)0x80004001); }
}
