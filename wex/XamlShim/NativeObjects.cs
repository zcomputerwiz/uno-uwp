// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The application's own objects that are not XAML elements (a converter in <Page.Resources>): their stand-in derives
// from NativeStandInObject, which forwards the interfaces Uno calls on such objects to the application's object.
// Implemented: IValueConverter (Convert, ConvertBack).
using System.Runtime.InteropServices;

namespace XamlShim.Abi;

public class NativeStandInObject : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language) =>
        NativeObjects.Convert(this, value, targetType, parameter, language, back: false);

    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        NativeObjects.Convert(this, value, targetType, parameter, language, back: true);
}

/* the application's Windows.UI.Xaml.Input.ICommand as the .NET ICommand Uno's Command properties take;
 * CanExecuteChanged is relayed from the application's event once something listens */
public sealed unsafe class NativeCommand : System.Windows.Input.ICommand
{
    public readonly IntPtr Pointer;       /* the application's ICommand, AddRef'd */
    EventHandler? changed;
    bool subscribed;

    public NativeCommand(IntPtr command) { Pointer = command; }
    ~NativeCommand() { Ccw.ComRelease(Pointer); }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            changed += value;
            if (subscribed) return;
            subscribed = true;
            /* EventHandler<Object> */
            var iid = PInterface.Iid("pinterface({9de1c535-6ae1-11e0-84e1-18a905bcc53f};cinterface(IInspectable))");
            var handler = Ccw.Get(new NativeDelegate(iid, (_, _) => changed?.Invoke(this, EventArgs.Empty)), iid);
            long token;
            int hr = UI_Xaml_Input_ICommand_Call.add_CanExecuteChanged(Pointer, handler, &token);
            if (hr < 0) Ccw.Log($"ICommand.CanExecuteChanged: {hr:x8}");
            Ccw.ComRelease(handler);
        }
        remove { changed -= value; }
    }

    public bool CanExecute(object? parameter)
    {
        var p = Boxing.Box(parameter);
        byte result;
        int hr = UI_Xaml_Input_ICommand_Call.CanExecute(Pointer, p, &result);
        if (p != IntPtr.Zero) Ccw.ComRelease(p);
        if (Ccw.Tracing) Ccw.Log($"ICommand.CanExecute({parameter?.GetType().Name ?? "null"}): {hr:x8}, {result}");
        return hr >= 0 && result != 0;
    }

    public void Execute(object? parameter)
    {
        var p = Boxing.Box(parameter);
        int hr = UI_Xaml_Input_ICommand_Call.Execute(Pointer, p);
        if (p != IntPtr.Zero) Ccw.ComRelease(p);
        if (hr < 0 || Ccw.Tracing) Ccw.Log($"ICommand.Execute({parameter?.GetType().Name ?? "null"}): {hr:x8}");
    }
}

public static unsafe class NativeObjects
{
    static readonly Dictionary<Type, string> primitives = new()
    {
        [typeof(bool)] = "Boolean", [typeof(char)] = "Char16", [typeof(byte)] = "UInt8", [typeof(short)] = "Int16",
        [typeof(ushort)] = "UInt16", [typeof(int)] = "Int32", [typeof(uint)] = "UInt32", [typeof(long)] = "Int64",
        [typeof(ulong)] = "UInt64", [typeof(float)] = "Single", [typeof(double)] = "Double", [typeof(string)] = "String",
        [typeof(object)] = "Object", [typeof(Guid)] = "Guid",
    };

    /* the TypeName of a type as the application knows it (HSTRING owned by the caller) */
    public static TypeName ToTypeName(Type? t)
    {
        if (t == null) return new TypeName { Name = HString.Create("Object"), Kind = 0 };
        if (primitives.TryGetValue(t, out var p)) return new TypeName { Name = HString.Create(p), Kind = 0 };
        if (NativeTypes.NativeName(t) is { } native) return new TypeName { Name = HString.Create(native), Kind = 2 };
        var full = t.FullName ?? t.Name;
        if (full.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal) && t.Assembly == typeof(Microsoft.UI.Xaml.UIElement).Assembly)
            return new TypeName { Name = HString.Create("Windows.UI.Xaml." + full["Microsoft.UI.Xaml.".Length..]), Kind = 1 };
        return new TypeName { Name = HString.Create(full), Kind = t.Assembly.IsDynamic ? 2 : 1 };
    }

    public static object? Convert(object self, object? value, Type targetType, object? parameter, string language, bool back)
    {
        var native = NativeControls.NativeOf(self);
        if (native == IntPtr.Zero || Ccw.QueryInterface(native, Iids.UI_Xaml_Data_IValueConverter, out var converter) < 0)
        {
            Ccw.Log($"{self.GetType().FullName}: not a value converter");
            return value;
        }
        IntPtr v = IntPtr.Zero, prm = IntPtr.Zero, lang = IntPtr.Zero, result = IntPtr.Zero;
        var type = ToTypeName(targetType);
        try
        {
            v = Boxing.Box(value);
            prm = Boxing.Box(parameter);
            lang = HString.Create(language ?? "");
            int hr = back ? UI_Xaml_Data_IValueConverter_Call.ConvertBack(converter, v, type, prm, lang, &result)
                          : UI_Xaml_Data_IValueConverter_Call.Convert(converter, v, type, prm, lang, &result);
            Marshal.ThrowExceptionForHR(hr);
            var converted = Boxing.Unbox(result);
            /* an enumeration (Visibility) comes boxed as its underlying integer */
            var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (converted != null && target.IsEnum && converted.GetType() != target) converted = Enum.ToObject(target, converted);
            return converted;
        }
        catch (Exception e)
        {
            Ccw.Log($"{self.GetType().FullName}.{(back ? "ConvertBack" : "Convert")}: {e.Message}");
            return null;
        }
        finally
        {
            if (v != IntPtr.Zero) Ccw.ComRelease(v);
            if (prm != IntPtr.Zero) Ccw.ComRelease(prm);
            if (result != IntPtr.Zero) Ccw.ComRelease(result);
            HString.Delete(lang);
            HString.Delete(type.Name);
            Ccw.ComRelease(converter);
        }
    }
}
