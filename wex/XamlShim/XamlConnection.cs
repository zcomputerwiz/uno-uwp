// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The objects of a compiled XAML page's connection IDs. FindName finds only elements of the visual tree, not visual
// states, row definitions, flyouts or state triggers, which the application's IComponentConnector.Connect also takes.
// XbfLoader writes each connection ID as the attached property XamlConnection.Id; Uno's reader sets it on the object
// as it builds it, and the object is recorded for the page being loaded.
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public static class XamlConnection
{
    [ThreadStatic] static Dictionary<int, object>? current;

    public static Mux.DependencyProperty IdProperty { get; } = Mux.DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(XamlConnection), new Mux.PropertyMetadata(null, OnId));

    public static string? GetId(Mux.DependencyObject o) => (string?)o.GetValue(IdProperty);

    public static void SetId(Mux.DependencyObject o, string? value) => o.SetValue(IdProperty, value);

    static void OnId(Mux.DependencyObject o, Mux.DependencyPropertyChangedEventArgs e)
    {
        if (current != null && e.NewValue is string s &&
            int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            current[id] = o;
    }

    /* records the connected objects built while the returned scope is open (nested pages have their own) */
    public static Scope Begin() => new(current);

    public sealed class Scope : IDisposable
    {
        readonly Dictionary<int, object>? previous;
        public Dictionary<int, object> Objects { get; } = new();

        internal Scope(Dictionary<int, object>? previous)
        {
            this.previous = previous;
            current = Objects;
        }

        public void Dispose() => current = previous;
    }
}
