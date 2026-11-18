// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// <Grid.Visibility><Binding Path="P" Converter="{StaticResource K}"/></Grid.Visibility> (XbfKit's form of a binding with a
// converter). Uno's reader resolves {StaticResource K} while it reads, when the element is not yet in the tree and the
// page's resources are out of reach, and leaves Converter unset: the property then keeps the unconverted value (the
// Calculator's date page showed both of its lazy sub-layouts). XbfLoader writes such a binding as the attached property
// ConverterBinding.Bindings ("Property=Path|Key|Mode|Parameter;..."); when Uno's reader sets it, the binding is made with a
// converter that looks the key up when the binding is evaluated: in the resources of the element and its parents, then
// the application's.
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public static class ConverterBinding
{
    public static Mux.DependencyProperty BindingsProperty { get; } = Mux.DependencyProperty.RegisterAttached(
        "Bindings", typeof(string), typeof(ConverterBinding), new Mux.PropertyMetadata(null, OnBindings));

    public static string? GetBindings(Mux.DependencyObject o) => (string?)o.GetValue(BindingsProperty);

    public static void SetBindings(Mux.DependencyObject o, string? value) => o.SetValue(BindingsProperty, value);

    static void OnBindings(Mux.DependencyObject o, Mux.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not string spec) return;
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2) continue;
            var f = parts[1].Split('|');
            var dp = RootBinding.PropertyOf(o.GetType(), parts[0]);
            if (dp == null) { Ccw.Log($"converter binding {o.GetType().Name}.{parts[0]}: no such property"); continue; }
            var binding = new Mux.Data.Binding { Path = new Mux.PropertyPath(f[0]), Converter = new LazyConverter(o, f[1]) };
            if (f.Length > 2 && f[2].Length > 0 && Enum.TryParse<Mux.Data.BindingMode>(f[2], out var mode)) binding.Mode = mode;
            if (f.Length > 3 && f[3].Length > 0) binding.ConverterParameter = f[3];
            Mux.Data.BindingOperations.SetBinding(o, dp, binding);
        }
    }

    sealed class LazyConverter(Mux.DependencyObject owner, string key) : Mux.Data.IValueConverter
    {
        readonly WeakReference<Mux.DependencyObject> owner = new(owner);
        Mux.Data.IValueConverter? converter;

        Mux.Data.IValueConverter? Find()
        {
            if (converter != null) return converter;
            object? found = null;
            owner.TryGetTarget(out var o);
            for (var d = o; d != null && found == null; d = Mux.Media.VisualTreeHelper.GetParent(d))
                if (d is Mux.FrameworkElement fe && fe.Resources.TryGetValue(key, out var v)) found = v;
            if (found == null && Mux.Application.Current?.Resources.TryGetValue(key, out var a) == true) found = a;
            return converter = found as Mux.Data.IValueConverter;
        }

        public object? Convert(object? value, Type targetType, object? parameter, string language) =>
            Find() is { } c ? c.Convert(value, targetType, parameter, language) : Mux.DependencyProperty.UnsetValue;

        public object? ConvertBack(object? value, Type targetType, object? parameter, string language) =>
            Find() is { } c ? c.ConvertBack(value, targetType, parameter, language) : Mux.DependencyProperty.UnsetValue;
    }
}
