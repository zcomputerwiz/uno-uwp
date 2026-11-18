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

/* XamlMarkupHelper.UnloadObject and FindName: x:Load (the Calculator's modes). XamlReader builds every element at once, so
 * an unloaded element is collapsed, and FindName brings it back with the visibility it had */
public unsafe partial class UI_Xaml_Markup_XamlMarkupHelper_Factory
{
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> Unloaded = new();

    int IUI_Xaml_Markup_IXamlMarkupHelperStatics_Abi.UnloadObject(IntPtr element)
    {
        if (Boxing.Unbox(element) is Microsoft.UI.Xaml.UIElement target)
        {
            Unloaded.Remove(target);
            Unloaded.Add(target, target.Visibility);
            target.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            if (Ccw.Calls) Ccw.Log($"UnloadObject: {target.GetType().Name} {(target as Microsoft.UI.Xaml.FrameworkElement)?.Name} collapsed");
        }
        return 0;
    }
}

public unsafe partial class UI_Xaml_FrameworkElement
{
    int IUI_Xaml_IFrameworkElement_Abi.get_DataContext(IntPtr* result)
    {
        var value = Reflect.Get(this, "DataContext");
        *result = Boxing.Box(value);
        if (Ccw.Calls) Ccw.Log($"get_DataContext of {UnoObject?.GetType().Name} {(UnoObject as Microsoft.UI.Xaml.FrameworkElement)?.Name}: {value?.GetType().Name ?? "null"} -> {*result:x}");
        return 0;
    }

    int IUI_Xaml_IFrameworkElement_Abi.FindName(IntPtr name, IntPtr* result)
    {
        var found = Reflect.Call(this, "FindName", new object?[] { HString.Read(name) });
        if (found is Microsoft.UI.Xaml.UIElement element && UI_Xaml_Markup_XamlMarkupHelper_Factory.Unloaded.TryGetValue(element, out var visibility))
        {
            UI_Xaml_Markup_XamlMarkupHelper_Factory.Unloaded.Remove(element);
            element.Visibility = (Microsoft.UI.Xaml.Visibility)visibility;
            if (Ccw.Calls) Ccw.Log($"FindName: {element.GetType().Name} {(element as Microsoft.UI.Xaml.FrameworkElement)?.Name} loaded again, visibility {visibility}");
        }
        *result = Boxing.Box(found);
        return 0;
    }
}

/* the application's Visibility changes, by element (lab trace) */
public unsafe partial class UI_Xaml_UIElement
{
    int IUI_Xaml_IUIElement_Abi.put_Visibility(int value)
    {
        if (Ccw.Calls) Ccw.Log($"put_Visibility {UnoObject?.GetType().Name} {(UnoObject as Microsoft.UI.Xaml.FrameworkElement)?.Name} = {value}");
        Reflect.Set(this, "Visibility", (long)value);
        return 0;
    }
}
