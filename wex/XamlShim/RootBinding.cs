// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// {Binding ElementName=Root, Path=P} where Root is the x:Name of the compiled XAML's root (NumberPad's
// Style="{Binding ElementName=ControlRoot, Path=ButtonStyle}"). The root is the application's own object, built before
// Uno's reader reads the content, so Uno cannot find it by name (it resolves names once, in what it read) and leaves the
// binding without a source. XbfLoader takes such bindings out of the XAML and writes them as the attached property
// RootBinding.Bindings ("Property=Path|Mode;..."); when Uno's reader sets it, each binding is made with the root as its
// source.
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public static partial class RootBinding
{
    [ThreadStatic] static object? root;

    public static Mux.DependencyProperty BindingsProperty { get; } = Mux.DependencyProperty.RegisterAttached(
        "Bindings", typeof(string), typeof(RootBinding), new Mux.PropertyMetadata(null, OnBindings));

    public static string? GetBindings(Mux.DependencyObject o) => (string?)o.GetValue(BindingsProperty);

    public static void SetBindings(Mux.DependencyObject o, string? value) => o.SetValue(BindingsProperty, value);

    /* the root of the XAML being read, for the bindings made while the returned scope is open */
    public static IDisposable Begin(object? target)
    {
        var previous = root;
        root = target;
        return new Restore(() => root = previous);
    }

    sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }

    static void OnBindings(Mux.DependencyObject o, Mux.DependencyPropertyChangedEventArgs e)
    {
        if (root == null || e.NewValue is not string spec) return;
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2) continue;
            var pathAndMode = parts[1].Split('|');
            var dp = PropertyOf(o.GetType(), parts[0]);
            if (dp == null) { Ccw.Log($"root binding {o.GetType().Name}.{parts[0]}: no such property"); continue; }
            /* the root's dependency property (the application's, registered for the root's type, or a framework one):
             * followed one way, as Uno's binding to the stand-in would read its plain .NET property once */
            if (root is Mux.DependencyObject source && SourceProperty(source, pathAndMode[0]) is { } from)
            {
                o.SetValue(dp, source.GetValue(from));
                var target = new WeakReference<Mux.DependencyObject>(o);
                long token = 0;
                token = source.RegisterPropertyChangedCallback(from, (s, _) =>
                {
                    if (target.TryGetTarget(out var t)) t.SetValue(dp, s.GetValue(from));
                    else s.UnregisterPropertyChangedCallback(from, token);
                });
                if (pathAndMode.Length > 1 && pathAndMode[1] == "TwoWay") Ccw.Log($"root binding {o.GetType().Name}.{parts[0]}: one way only");
                continue;
            }
            var binding = new Mux.Data.Binding { Source = root, Path = new Mux.PropertyPath(pathAndMode[0]) };
            if (pathAndMode.Length > 1 && Enum.TryParse<Mux.Data.BindingMode>(pathAndMode[1], out var mode)) binding.Mode = mode;
            Mux.Data.BindingOperations.SetBinding(o, dp, binding);
        }
    }

    static Mux.DependencyProperty? SourceProperty(Mux.DependencyObject source, string name) =>
        DependencyProperties.Registered.Where(r => r.Key.Name == name && r.Key.Owner.IsInstanceOfType(source)).Select(r => r.Value).FirstOrDefault()
        ?? PropertyOf(source.GetType(), name);

    static Mux.DependencyProperty? PropertyOf(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return type.GetProperty(name + "Property", flags)?.GetValue(null) as Mux.DependencyProperty ??
               type.GetField(name + "Property", flags)?.GetValue(null) as Mux.DependencyProperty;
    }

    /* moves the bindings to the root (ElementName=rootName, with at most Path and Mode) into RootBinding.Bindings */
    public static void Rewrite(XElement content, string rootName, XNamespace shimNamespace)
    {
        foreach (var e in content.DescendantsAndSelf())
        {
            var specs = new List<string>();
            foreach (var a in e.Attributes().Where(a => a.Name.Namespace == XNamespace.None && !a.Name.LocalName.Contains('.')).ToList())
            {
                var m = BindingMarkup().Match(a.Value.Trim());
                if (!m.Success) continue;
                string? path = null, mode = null, element = null;
                var ok = true;
                foreach (var (part, index) in m.Groups[1].Value.Split(',').Select((p, i) => (p.Trim(), i)))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 1 && index == 0) path = kv[0].Trim();
                    else if (kv.Length == 2 && kv[0].Trim() == "Path") path = kv[1].Trim();
                    else if (kv.Length == 2 && kv[0].Trim() == "Mode") mode = kv[1].Trim();
                    else if (kv.Length == 2 && kv[0].Trim() == "ElementName") element = kv[1].Trim();
                    else ok = false;
                }
                if (!ok || element != rootName || string.IsNullOrEmpty(path)) continue;
                specs.Add($"{a.Name.LocalName}={path}" + (mode != null ? "|" + mode : ""));
                a.Remove();
            }
            /* property element form, as XbfKit writes it: <T.Style><Binding ElementName="Root" Path="P"/></T.Style> */
            foreach (var p in e.Elements().Where(c => c.Name.LocalName == e.Name.LocalName + "." + c.Name.LocalName.Split('.').Last() &&
                                                      c.Name.Namespace == e.Name.Namespace).ToList())
            {
                if (p.Elements().Take(2).ToList() is not [var b] || b.Name.LocalName != "Binding" || b.HasElements) continue;
                if (b.Attribute("ElementName")?.Value != rootName || b.Attribute("Path")?.Value is not { Length: > 0 } path) continue;
                if (b.Attributes().Any(a => a.Name.LocalName is not ("ElementName" or "Path" or "Mode"))) continue;
                var mode = b.Attribute("Mode")?.Value;
                specs.Add($"{p.Name.LocalName.Split('.').Last()}={path}" + (mode != null ? "|" + mode : ""));
                p.Remove();
            }
            if (specs.Count > 0)
            {
                e.SetAttributeValue(shimNamespace + "RootBinding.Bindings", string.Join(";", specs));
                Ccw.Log($"root binding {e.Name.LocalName} {e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value}: {string.Join(";", specs)}");
            }
        }
    }

    [GeneratedRegex(@"^\{Binding\s+([^{}]*)\}$")]
    private static partial Regex BindingMarkup();
}
