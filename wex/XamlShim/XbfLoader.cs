// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// Application.LoadComponent for compiled XAML: the page's XBF is turned back into XAML text by XbfKit (MIT,
// github.com/0x5bfa/XbfKit), connection IDs are noted and removed, Uno's XamlReader builds the content, and the
// application's generated IComponentConnector.Connect receives each connected element (x:Name fields, event handlers).
// The XBF comes from the package's resources.pri (ms-appx:///Dir/Page.xaml is the resource Files/Dir/Page.xbf, embedded
// data or a path), read with PriFormat (Apache-2.0, ../PriFormat); a loose <package root>\Dir\Page.xbf is the fallback.
// Limits: the first candidate is taken (no qualifier matching; XAML pages normally have one); only the root's content
// is loaded (the root's own properties and resources are logged and skipped).
using System.Xml.Linq;
using Mux = Microsoft.UI.Xaml;
using UnoControls = Microsoft.UI.Xaml.Controls;

namespace XamlShim.Abi;

public static unsafe class XbfLoader
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static int Load(IntPtr component, IntPtr uri)
    {
        var location = UriText(uri);
        var target = Ccw.Unwrap(component);
        Ccw.Log($"LoadComponent {location} into {target?.RuntimeClassName ?? "an unknown object"}");
        const string scheme = "ms-appx:///";
        if (!location.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { Ccw.Log($"unsupported location {location}"); return unchecked((int)0x80070057); }
        var relative = Path.ChangeExtension(location[scheme.Length..], ".xbf");
        var root = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var xbf = FromPri(root, relative);
        if (xbf == null)
        {
            var path = Path.Combine(root, relative.Replace('/', '\\'));
            if (!File.Exists(path)) { Ccw.Log($"no Files/{relative} in resources.pri and no {path}"); return unchecked((int)0x80070002); }
            Ccw.Log($"XBF from {path}");
            xbf = File.ReadAllBytes(path);
        }

        string xaml;
        using (var stream = new MemoryStream(xbf))
            xaml = XbfKit.XbfDecompiler.Decompile(stream, XbfKit.XbfDialect.WUX, new XbfKit.XbfDecompilationOptions { IncludeConnectionIds = true });
        var doc = XDocument.Parse(xaml);
        var top = doc.Root!;
        foreach (var a in top.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != X + "Class"))
            Ccw.Log($"root attribute {a.Name.LocalName} skipped");

        /* connection IDs: named elements keep their names, others get one */
        var connections = new List<(string Name, int Id)>();
        foreach (var e in top.Descendants())
        {
            var id = e.Attribute(X + "ConnectionId");
            if (id == null) continue;
            var n = e.Attribute(X + "Name")?.Value;
            if (n == null) { n = "__connection" + id.Value; e.SetAttributeValue(X + "Name", n); }
            connections.Add((n, int.Parse(id.Value, System.Globalization.CultureInfo.InvariantCulture)));
            id.Remove();
        }
        if (top.Attribute(X + "ConnectionId") is XAttribute rootId) { Ccw.Log($"root connection {rootId.Value} skipped"); rootId.Remove(); }

        var content = top.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
        if (target is not UI_Xaml_DependencyObject shim || content.Count == 0)
        {
            Ccw.Log($"nothing loaded ({content.Count} content elements)");
            return 0;
        }
        if (content.Count > 1) Ccw.Log($"{content.Count} content elements: only the first is loaded");
        var element = new XElement(content[0]);
        foreach (var ns in top.Attributes().Where(a => a.IsNamespaceDeclaration)) element.SetAttributeValue(ns.Name, ns.Value);
        var built = (Mux.UIElement)Mux.Markup.XamlReader.Load(element.ToString());

        switch (shim.UnoObject)
        {
        case UnoControls.UserControl user: user.Content = built; break;
        case UnoControls.ContentControl control: control.Content = built; break;
        default: Ccw.Log($"cannot set the content of {shim.UnoObject?.GetType().Name}"); return 0;
        }

        if (connections.Count == 0) return 0;
        if (Ccw.QueryInterface(component, Iids.UI_Xaml_Markup_IComponentConnector, out var connector) < 0)
        {
            Ccw.Log("the component has no IComponentConnector");
            return 0;
        }
        foreach (var (n, id) in connections)
        {
            var found = (built as Mux.FrameworkElement)?.FindName(n);
            if (found == null) { Ccw.Log($"connection {id}: {n} not found"); continue; }
            var wrapper = Shim.For(found);
            var p = Ccw.Get(wrapper);
            int hr = UI_Xaml_Markup_IComponentConnector_Call.Connect(connector, id, p);
            Ccw.Log($"Connect {id} {n} ({wrapper.RuntimeClassName}): {hr:x8}");
            Ccw.ComRelease(p);
        }
        Ccw.ComRelease(connector);
        return 0;
    }

    /* the bytes of the resource Files/<relative> in <root>\resources.pri, or null */
    static byte[]? FromPri(string root, string relative)
    {
        var pri = Path.Combine(root, "resources.pri");
        if (!File.Exists(pri)) return null;
        var want = "\\Files\\" + relative.Replace('/', '\\');
        using var stream = File.OpenRead(pri);
        var file = PriFormat.PriFile.Parse(stream);
        foreach (var map in file.Sections.OfType<PriFormat.ResourceMapSection>())
        {
            foreach (var set in map.CandidateSets.Values)
            {
                var item = file.GetResourceMapItemByRef(set.ResourceMapItem);
                if (!item.FullName.EndsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var candidate in set.Candidates)
                {
                    var span = candidate.Data ?? (candidate.DataItem is { } r ? file.GetDataItemByRef(r) : null);
                    if (span is not { } s) continue;
                    var bytes = new byte[s.Length];
                    stream.Position = s.Offset;
                    stream.ReadExactly(bytes);
                    switch (candidate.Type)
                    {
                    case PriFormat.ResourceValueType.EmbeddedData:
                        Ccw.Log($"XBF {item.FullName}: {bytes.Length} bytes embedded in resources.pri");
                        return bytes;
                    case PriFormat.ResourceValueType.Path:
                    case PriFormat.ResourceValueType.AsciiPath:
                    case PriFormat.ResourceValueType.Utf8Path:
                        var text = candidate.Type == PriFormat.ResourceValueType.Path
                            ? System.Text.Encoding.Unicode.GetString(bytes) : System.Text.Encoding.UTF8.GetString(bytes);
                        var path = Path.Combine(root, text.TrimEnd('\0'));
                        Ccw.Log($"XBF {item.FullName}: file {path}");
                        return File.Exists(path) ? File.ReadAllBytes(path) : null;
                    }
                }
            }
        }
        return null;
    }

    static string UriText(IntPtr uri)
    {
        if (Ccw.QueryInterface(uri, Iids.Foundation_IUriRuntimeClass, out var p) < 0) return "";
        IntPtr h;
        Foundation_IUriRuntimeClass_Call.get_AbsoluteUri(p, &h);
        Ccw.ComRelease(p);
        var s = HString.Read(h);
        HString.Delete(h);
        return s;
    }
}
