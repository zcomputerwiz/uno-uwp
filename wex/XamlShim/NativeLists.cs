// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The application's own collections as .NET lists for Uno (ItemsSource, MenuItemsSource): a .NET Native collection
// is an IBindableVector (and IBindableObservableVector), a C++/CX one an IVector<Object> (and IObservableVector<Object>);
// both have the same vtable for what is read here (GetAt 6, get_Size 7, IndexOf 9, SetAt 10, InsertAt 11, RemoveAt 12,
// Append 13, Clear 15). Changes are reported to Uno as a reset.
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace XamlShim.Abi;

public sealed unsafe class NativeList : IList, INotifyCollectionChanged
{
    public static readonly Guid IID_IBindableVector = new("393de7de-6fd0-4c0d-bb71-47244a113e93");
    static readonly Guid IID_IBindableObservableVector = new("fe1eb536-7e7f-4f90-ac9a-474984aae512");
    static readonly Guid IID_BindableVectorChangedEventHandler = new("624cd4e1-d007-43b1-9c03-af4d3e6258c4");
    public static readonly Guid IID_IVectorObject = PInterface.Iid("pinterface({913337e9-11a1-4345-a3a2-4e7f956e222d};cinterface(IInspectable))");
    static readonly Guid IID_IObservableVectorObject = PInterface.Iid("pinterface({5917eb53-50b4-4a0d-b309-65862b3f1dbc};cinterface(IInspectable))");
    static readonly Guid IID_VectorChangedObject = PInterface.Iid("pinterface({0c051752-9fbf-4c70-aa0c-0e4c82d9a761};cinterface(IInspectable))");

    public readonly IntPtr Pointer;      /* the vector interface, AddRef'd */
    readonly bool bindable;
    NotifyCollectionChangedEventHandler? changed;
    bool subscribed;

    NativeList(IntPtr vector, bool bindable) { Pointer = vector; this.bindable = bindable; }
    ~NativeList() { Ccw.ComRelease(Pointer); }

    /* a list over p when it is one of the application's vectors, or null */
    public static NativeList? From(IntPtr p)
    {
        if (Ccw.QueryInterface(p, IID_IBindableVector, out var v) >= 0) return new NativeList(v, true);
        if (Ccw.QueryInterface(p, IID_IVectorObject, out v) >= 0) return new NativeList(v, false);
        return null;
    }

    IntPtr* Vtbl => *(IntPtr**)Pointer;

    static void Check(int hr)
    {
        if (hr == unchecked((int)0x8000000B)) throw new ArgumentOutOfRangeException();
        Marshal.ThrowExceptionForHR(hr);
    }

    public int Count
    {
        get
        {
            uint size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vtbl[7])(Pointer, &size));
            return (int)size;
        }
    }

    public object? this[int index]
    {
        get
        {
            IntPtr item;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl[6])(Pointer, (uint)index, &item));
            try { return Boxing.Unbox(item); }
            finally { if (item != IntPtr.Zero) Ccw.ComRelease(item); }
        }
        set => WithItem(value, item => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Vtbl[10])(Pointer, (uint)index, item));
    }

    void WithItem(object? value, Func<IntPtr, int> call)
    {
        var item = Boxing.Box(value);
        try { Check(call(item)); }
        finally { if (item != IntPtr.Zero) Ccw.ComRelease(item); }
    }

    public int IndexOf(object? value)
    {
        var item = Boxing.Box(value);
        try
        {
            uint index;
            byte found;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, byte*, int>)Vtbl[9])(Pointer, item, &index, &found));
            return found != 0 ? (int)index : -1;
        }
        finally { if (item != IntPtr.Zero) Ccw.ComRelease(item); }
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;
    public void Insert(int index, object? value) =>
        WithItem(value, item => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Vtbl[11])(Pointer, (uint)index, item));
    public void RemoveAt(int index) => Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl[12])(Pointer, (uint)index));
    public int Add(object? value)
    {
        WithItem(value, item => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl[13])(Pointer, item));
        return Count - 1;
    }
    public void Remove(object? value) { if (IndexOf(value) is var i and >= 0) RemoveAt(i); }
    public void Clear() => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[15])(Pointer));

    public bool IsFixedSize => false;
    public bool IsReadOnly => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public IEnumerator GetEnumerator() { for (int i = 0, n = Count; i < n; i++) yield return this[i]; }

    /* the application's VectorChanged, relayed as a reset once something listens */
    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add
        {
            changed += value;
            if (subscribed) return;
            subscribed = true;
            var (observable, handlerIid) = bindable ? (IID_IBindableObservableVector, IID_BindableVectorChangedEventHandler)
                                                    : (IID_IObservableVectorObject, IID_VectorChangedObject);
            if (Ccw.QueryInterface(Pointer, observable, out var o) < 0) return;
            var handler = Ccw.Get(new NativeDelegate(handlerIid, (_, _) =>
                changed?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset))), handlerIid);
            long token;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)(*(IntPtr**)o)[6])(o, handler, &token);
            if (hr < 0) Ccw.Log($"VectorChanged: {hr:x8}");
            Ccw.ComRelease(handler);
            Ccw.ComRelease(o);
        }
        remove { changed -= value; }
    }
}
