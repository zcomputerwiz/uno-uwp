// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// DataTemplate.SetExtensionInstance / GetExtensionInstance: the application's compiled x:Bind of a DataTemplate hands its
// bindings object to the template's root element. Uno has no such property; the object is only remembered (the application's
// own DataContextChanged handler on the root, which it registers when connected, updates the bindings).
using System.Runtime.CompilerServices;

namespace XamlShim.Abi;

public unsafe partial class UI_Xaml_DataTemplate_Factory
{
    sealed class Holder(IntPtr pointer)
    {
        public readonly IntPtr Pointer = pointer;
        ~Holder() { Ccw.ComRelease(Pointer); }
    }

    static readonly ConditionalWeakTable<object, Holder> extensions = new();

    int IUI_Xaml_IDataTemplateStatics2_Abi.SetExtensionInstance(IntPtr element, IntPtr value)
    {
        if (Boxing.Unbox(element) is not { } target) return unchecked((int)0x80070057);
        extensions.Remove(target);
        if (value != IntPtr.Zero)
        {
            Ccw.ComAddRef(value);
            extensions.Add(target, new Holder(value));
        }
        return 0;
    }

    int IUI_Xaml_IDataTemplateStatics2_Abi.GetExtensionInstance(IntPtr element, IntPtr* result)
    {
        *result = IntPtr.Zero;
        if (Boxing.Unbox(element) is { } target && extensions.TryGetValue(target, out var holder))
        {
            Ccw.ComAddRef(holder.Pointer);
            *result = holder.Pointer;
        }
        return 0;
    }
}

/* XamlBindingHelper.Set/GetDataTemplateComponent: the same for the application's IDataTemplateComponent of a root element */
public unsafe partial class UI_Xaml_Markup_XamlBindingHelper_Factory
{
    sealed class Holder(IntPtr pointer)
    {
        public readonly IntPtr Pointer = pointer;
        ~Holder() { Ccw.ComRelease(Pointer); }
    }

    static readonly ConditionalWeakTable<object, Holder> components = new();

    int IUI_Xaml_Markup_IXamlBindingHelperStatics_Abi.SetDataTemplateComponent(IntPtr element, IntPtr value)
    {
        if (Boxing.Unbox(element) is not { } target) return unchecked((int)0x80070057);
        components.Remove(target);
        if (value != IntPtr.Zero)
        {
            Ccw.ComAddRef(value);
            components.Add(target, new Holder(value));
        }
        return 0;
    }

    int IUI_Xaml_Markup_IXamlBindingHelperStatics_Abi.GetDataTemplateComponent(IntPtr element, IntPtr* result)
    {
        *result = IntPtr.Zero;
        if (Boxing.Unbox(element) is { } target && components.TryGetValue(target, out var holder))
        {
            Ccw.ComAddRef(holder.Pointer);
            *result = holder.Pointer;
        }
        return 0;
    }
}
