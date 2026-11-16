// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The objects of a compiled XAML page's connection IDs. FindName finds only elements of the visual tree, not visual
// states, row definitions, flyouts or state triggers, which the application's IComponentConnector.Connect also takes.
// XbfLoader writes each connection ID as the attached property XamlConnection.Id; Uno's reader sets it on the object
// as it builds it, and the object is recorded for the page being loaded.
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public static unsafe class XamlConnection
{
    [ThreadStatic] static Dictionary<int, object>? current;

    public static Mux.DependencyProperty IdProperty { get; } = Mux.DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(XamlConnection), new Mux.PropertyMetadata(null, OnId));

    public static string? GetId(Mux.DependencyObject o) => (string?)o.GetValue(IdProperty);

    public static void SetId(Mux.DependencyObject o, string? value) => o.SetValue(IdProperty, value);

    /* the file an element's connection belongs to: set before Id, so that a template's element, built after the page's load,
     * can be connected to the application's component of that file */
    public static Mux.DependencyProperty HostProperty { get; } = Mux.DependencyProperty.RegisterAttached(
        "Host", typeof(string), typeof(XamlConnection), new Mux.PropertyMetadata(null));

    public static string? GetHost(Mux.DependencyObject o) => (string?)o.GetValue(HostProperty);

    public static void SetHost(Mux.DependencyObject o, string? value) => o.SetValue(HostProperty, value);

    static readonly Dictionary<string, IntPtr> hosts = new();

    /* the application's component (AddRef'd) of a loaded file; a later load of the same file replaces it */
    public static void Register(string host, IntPtr component)
    {
        Ccw.ComAddRef(component);
        lock (hosts)
        {
            if (hosts.TryGetValue(host, out var old)) Ccw.ComRelease(old);
            hosts[host] = component;
        }
    }

    /* an element of a template, built after the load: the binding connector of its id (x:Bind of the template), and the
     * application's Connect, as for the page's own elements */
    static void ConnectLate(string host, int id, Mux.DependencyObject element)
    {
        IntPtr component;
        lock (hosts) { if (!hosts.TryGetValue(host, out component)) return; }
        if (Ccw.QueryInterface(component, Iids.UI_Xaml_Markup_IComponentConnector, out var connector) < 0) return;
        var wrapper = Shim.For(element);
        var p = Ccw.Get(wrapper);
        IntPtr bindings = IntPtr.Zero;
        try
        {
            if (Ccw.QueryInterface(component, Iids.UI_Xaml_Markup_IComponentConnector2, out var connector2) >= 0)
            {
                int hb = UI_Xaml_Markup_IComponentConnector2_Call.GetBindingConnector(connector2, id, p, &bindings);
                Ccw.ComRelease(connector2);
                if (hb < 0) bindings = IntPtr.Zero;
            }
            int hr = UI_Xaml_Markup_IComponentConnector_Call.Connect(connector, id, p);
            int hc = bindings != IntPtr.Zero ? UI_Xaml_Markup_IComponentConnector_Call.Connect(bindings, id, p) : 0;
            Ccw.Log($"Connect {id} {element.GetType().Name} of a template in {host}: {hr:x8}" + (bindings != IntPtr.Zero ? $", bindings {hc:x8}" : ""));
        }
        finally
        {
            if (bindings != IntPtr.Zero) Ccw.ComRelease(bindings);
            Ccw.ComRelease(p);
            Ccw.ComRelease(connector);
        }
    }

    static void OnId(Mux.DependencyObject o, Mux.DependencyPropertyChangedEventArgs e)
    {
        if (current != null && e.NewValue is string s &&
            int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            current[id] = o;
        else if (current == null && e.NewValue is string late && GetHost(o) is { } host &&
                 int.TryParse(late, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var lateId))
            ConnectLate(host, lateId, o);
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
