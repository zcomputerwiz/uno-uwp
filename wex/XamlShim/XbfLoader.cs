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
        var xbf = ReadXbf(relative);
        if (xbf == null) return unchecked((int)0x80070002);
        var top = Decompile(xbf).Root!;

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
        int? rootConnection = null;
        if (top.Attribute(X + "ConnectionId") is XAttribute rootId)
        {
            rootConnection = int.Parse(rootId.Value, System.Globalization.CultureInfo.InvariantCulture);
            rootId.Remove();
        }

        var content = top.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
        if (target is not UI_Xaml_DependencyObject shim || content.Count == 0)
        {
            Ccw.Log($"nothing loaded ({content.Count} content elements)");
            return 0;
        }
        if (content.Count > 1) Ccw.Log($"{content.Count} content elements: only the first is loaded");
        ApplyRootAttributes(top, shim.UnoObject);

        /* the root's own resources (<Page.Resources>): the element's Resources, before its content is read, so that
         * the content's resources and the application's code (x:Bind converters) find them */
        if (top.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) is { } resources &&
            shim.UnoObject is Mux.FrameworkElement owner)
        {
            var entries = resources.Elements().Select(e => new XElement(e)).ToList();
            if (entries.Count > 0) owner.Resources = BuildDictionary(top, entries, Path.GetFileNameWithoutExtension(relative));
        }

        var element = new XElement(content[0]);
        foreach (var ns in top.Attributes().Where(a => a.IsNamespaceDeclaration)) element.SetAttributeValue(ns.Name, ns.Value);
        ResolveConditionalNamespaces(element);
        NativeControls.Prepare(element);
        QualifyTargetTypes(element);
        QualifyAttachedSetters(element);
        var built = (Mux.UIElement)Mux.Markup.XamlReader.Load(element.ToString());

        switch (shim.UnoObject)
        {
        case UnoControls.UserControl user: user.Content = built; break;
        case UnoControls.ContentControl control: control.Content = built; break;
        default: Ccw.Log($"cannot set the content of {shim.UnoObject?.GetType().Name}"); return 0;
        }

        if (connections.Count == 0 && rootConnection == null) return 0;
        if (Ccw.QueryInterface(component, Iids.UI_Xaml_Markup_IComponentConnector, out var connector) < 0)
        {
            Ccw.Log("the component has no IComponentConnector");
            return 0;
        }

        /* x:Bind: the root's binding connector (IComponentConnector2.GetBindingConnector) also receives every
         * connected element; the root itself is connected too (its own event handlers, the bindings' Loading) */
        IntPtr bindings = IntPtr.Zero;
        if (rootConnection is { } root)
        {
            if (Ccw.QueryInterface(component, Iids.UI_Xaml_Markup_IComponentConnector2, out var connector2) >= 0)
            {
                int hb = UI_Xaml_Markup_IComponentConnector2_Call.GetBindingConnector(connector2, root, component, &bindings);
                Ccw.ComRelease(connector2);
                Ccw.Log($"GetBindingConnector {root}: {hb:x8}, {(bindings != IntPtr.Zero ? "bindings" : "none")}");
                if (hb < 0) bindings = IntPtr.Zero;
            }
            int hr = UI_Xaml_Markup_IComponentConnector_Call.Connect(connector, root, component);
            Ccw.Log($"Connect {root} (root): {hr:x8}");
        }
        foreach (var (n, id) in connections)
        {
            var found = (built as Mux.FrameworkElement)?.FindName(n);
            if (found == null) { Ccw.Log($"connection {id}: {n} not found"); continue; }
            var wrapper = Shim.For(found);
            var p = Ccw.Get(wrapper);
            int hr = UI_Xaml_Markup_IComponentConnector_Call.Connect(connector, id, p);
            int hb = bindings != IntPtr.Zero ? UI_Xaml_Markup_IComponentConnector_Call.Connect(bindings, id, p) : 0;
            Ccw.Log($"Connect {id} {n} ({wrapper.RuntimeClassName}): {hr:x8}" + (bindings != IntPtr.Zero ? $", bindings {hb:x8}" : ""));
            Ccw.ComRelease(p);
        }
        if (bindings != IntPtr.Zero) Ccw.ComRelease(bindings);
        Ccw.ComRelease(connector);
        return 0;
    }

    /* the root element's own properties (<UserControl Height="32">): set on the component's Uno object, the text
     * converted by Uno's XamlBindingHelper; {StaticResource X} and {ThemeResource X} from the resources; other
     * markup extensions and attached properties are logged and left out */
    static void ApplyRootAttributes(XElement top, object? target)
    {
        if (target == null) return;
        foreach (var a in top.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
        {
            var name = a.Name.LocalName;
            var property = name.Contains('.') ? null :
                target.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (property == null || !property.CanWrite) { Ccw.Log($"root attribute {name}: left out"); continue; }
            try
            {
                object? value;
                var text = a.Value.Trim();
                if (text.StartsWith('{'))
                {
                    var parts = text.Trim('{', '}').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || (parts[0] != "StaticResource" && parts[0] != "ThemeResource"))
                    { Ccw.Log($"root attribute {name}={text}: left out"); continue; }
                    value = Find((Mux.ResourceDictionary?)(target as Mux.FrameworkElement)?.Resources ?? new Mux.ResourceDictionary(), parts[1].Trim());
                }
                else value = Mux.Markup.XamlBindingHelper.ConvertValue(property.PropertyType, text);
                property.SetValue(target, value);
            }
            catch (Exception e) { Ccw.Log($"root attribute {name}={a.Value}: {FirstLine(e)}"); }
        }
    }

    /* the compiled XAML <relative> (Dir/Page.xbf) of the package: from resources.pri, else a loose file; null if none */
    static byte[]? ReadXbf(string relative)
    {
        var root = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        if (FromPri(root, relative) is { } xbf) return xbf;
        var path = Path.Combine(root, relative.Replace('/', '\\'));
        if (!File.Exists(path)) { Ccw.Log($"no Files/{relative} in resources.pri and no {path}"); return null; }
        Ccw.Log($"XBF from {path}");
        return File.ReadAllBytes(path);
    }

    /* every compiled XAML file of the package's resources.pri (first candidate of each), as decompiled documents */
    public static IEnumerable<(string Name, XDocument Document)> AllXbf()
    {
        var root = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var pri = Path.Combine(root, "resources.pri");
        if (!File.Exists(pri)) yield break;
        var found = new List<(string, byte[])>();
        using (var stream = File.OpenRead(pri))
        {
            var file = PriFormat.PriFile.Parse(stream);
            foreach (var map in file.Sections.OfType<PriFormat.ResourceMapSection>())
                foreach (var set in map.CandidateSets.Values)
                {
                    var item = file.GetResourceMapItemByRef(set.ResourceMapItem);
                    if (!item.FullName.Contains("\\Files\\", StringComparison.OrdinalIgnoreCase) ||
                        !item.FullName.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase)) continue;
                    var candidate = set.Candidates.Count > 0 ? set.Candidates[0] : null;
                    if (candidate == null || candidate.Type != PriFormat.ResourceValueType.EmbeddedData) continue;
                    var span = candidate.Data ?? (candidate.DataItem is { } r ? file.GetDataItemByRef(r) : null);
                    if (span is not { } s) continue;
                    var bytes = new byte[s.Length];
                    stream.Position = s.Offset;
                    stream.ReadExactly(bytes);
                    found.Add((item.FullName, bytes));
                }
        }
        foreach (var (name, bytes) in found)
        {
            XDocument? doc = null;
            try { doc = Decompile(bytes); }
            catch (Exception e) { Ccw.Log($"XBF {name}: {FirstLine(e)}"); }
            if (doc != null) yield return (name, doc);
        }
    }

    internal static XDocument Decompile(byte[] xbf)
    {
        using var stream = new MemoryStream(xbf);
        return XDocument.Parse(XbfKit.XbfDecompiler.Decompile(stream, XbfKit.XbfDialect.WUX,
                                                              new XbfKit.XbfDecompilationOptions { IncludeConnectionIds = true }));
    }

    /* Application.Resources of the package's App.xaml (App.xbf), which the framework loads after App is constructed and
     * before OnLaunched. Whole if Uno's XamlReader takes it; otherwise entry by entry (merged dictionaries, theme
     * dictionaries, keyed resources), each one that fails logged and left out. */
    public static Mux.ResourceDictionary LoadApplicationResources()
    {
        var xbf = ReadXbf("App.xbf");
        if (xbf == null) return new Mux.ResourceDictionary();
        var top = Decompile(xbf).Root!;
        if (Ccw.Calls) File.WriteAllText(Path.Combine(Path.GetTempPath(), "xamlshim-App.xaml"), top.ToString());
        /* XbfKit writes Application.Resources as the root's content (no property element); either form is taken */
        var property = top.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
        var children = (property ?? top).Elements().Where(e => property != null || !e.Name.LocalName.Contains('.')).ToList();
        if (children.Count == 0) { Ccw.Log("App.xaml has no Application.Resources"); return new Mux.ResourceDictionary(); }
        return BuildDictionary(top, children, "application");
    }

    /* a resource dictionary from the entries of a compiled XAML file's root (its X.Resources) */
    static Mux.ResourceDictionary BuildDictionary(XElement top, List<XElement> children, string what)
    {
        foreach (var e in children.SelectMany(c => c.DescendantsAndSelf())) e.Attribute(X + "ConnectionId")?.Remove();

        var dict = children.Count == 1 && children[0].Name.LocalName == "ResourceDictionary"
            ? new XElement(children[0])
            : new XElement(top.Name.Namespace + "ResourceDictionary", children.Select(c => new XElement(c)));
        var namespaces = top.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
        foreach (var ns in namespaces) dict.SetAttributeValue(ns.Name, ns.Value);
        ResolveConditionalNamespaces(dict);
        NativeControls.Prepare(dict);
        QualifyTargetTypes(dict);
        QualifyAttachedSetters(dict);
        var deferred = TakeDeferred(dict);
        try
        {
            var whole = (Mux.ResourceDictionary)Mux.Markup.XamlReader.Load(dict.ToString());
            ApplyDeferred(whole, deferred);
            Ccw.Log($"{what} resources: {whole.Count} entries, {whole.MergedDictionaries.Count} merged, {whole.ThemeDictionaries.Count} themes");
            return whole;
        }
        catch (Exception e) { Ccw.Log($"{what} resources as a whole: {FirstLine(e)}; loading them one by one"); }

        var result = new Mux.ResourceDictionary();
        Mux.ResourceDictionary LoadDictionary(XElement element)
        {
            var copy = new XElement(element);
            copy.Attribute(X + "Key")?.Remove();
            foreach (var ns in namespaces) copy.SetAttributeValue(ns.Name, ns.Value);
            if (copy.Name.LocalName != "ResourceDictionary")
                copy = new XElement(dict.Name, namespaces.Select(a => new XAttribute(a)), new XElement(element));
            return (Mux.ResourceDictionary)Mux.Markup.XamlReader.Load(copy.ToString());
        }
        void Try(string entry, Action load)
        {
            try { load(); Ccw.Log($"{what} resource {entry}: loaded"); }
            catch (Exception e) { Ccw.Log($"{what} resource {entry}: {(Ccw.Calls ? e.ToString() : FirstLine(e))}"); }
        }
        foreach (var child in dict.Elements())
        {
            var local = child.Name.LocalName;
            if (local == "ResourceDictionary.MergedDictionaries")
                foreach (var m in child.Elements())
                    Try($"merged {m.Name.LocalName} {m.Attribute("Source")?.Value}", () => result.MergedDictionaries.Add(LoadDictionary(m)));
            else if (local == "ResourceDictionary.ThemeDictionaries")
                foreach (var t in child.Elements())
                {
                    var key = t.Attribute(X + "Key")?.Value ?? "";
                    Try($"theme {key}", () => result.ThemeDictionaries[key] = LoadDictionary(t));
                }
            else
            {
                var key = child.Attribute(X + "Key")?.Value ?? child.Attribute("TargetType")?.Value ?? "";
                Try($"{local} {key}", () =>
                {
                    foreach (var kv in LoadDictionary(child)) result[kv.Key] = kv.Value;
                });
            }
        }
        ApplyDeferred(result, deferred);
        Ccw.Log($"{what} resources: {result.Count} entries, {result.MergedDictionaries.Count} merged, {result.ThemeDictionaries.Count} themes");
        return result;
    }

    static string FirstLine(Exception e) => e.GetType().Name + ": " + e.Message.Split('\n')[0];

    // ---- what Uno's XAML reader does not take as Windows writes it ----

    static readonly string[] unoNamespaces =
        ["Microsoft.UI.Xaml", "Microsoft.UI.Xaml.Controls", "Microsoft.UI.Xaml.Controls.Primitives", "Microsoft.UI.Xaml.Media",
         "Microsoft.UI.Xaml.Shapes", "Microsoft.UI.Xaml.Documents", "Microsoft.UI.Xaml.Media.Animation", "Microsoft.UI.Xaml.Input",
         "Microsoft.UI.Xaml.Automation"];

    /* the Uno type a presentation-namespace name stands for, or null */
    static Type? UnoType(string name)
    {
        foreach (var ns in unoNamespaces)
            if (NativeTypes.UnoType("Windows.UI.Xaml" + ns["Microsoft.UI.Xaml".Length..] + "." + name) is { } t) return t;
        return null;
    }

    /* conditional XAML namespaces (xmlns:c="...presentation?IsApiContractPresent(Windows.Foundation.UniversalApiContract,7)"):
     * what they hold is kept on the plain namespace when the condition holds, removed when it does not. Every
     * UniversalApiContract version of Windows 11 (up to 19) is present; other contracts and queries are taken as
     * absent. */
    internal static void ResolveConditionalNamespaces(XElement root)
    {
        static bool? Holds(string ns)
        {
            var q = ns.IndexOf('?');
            if (q < 0) return null;
            var query = ns[(q + 1)..].Replace(" ", "");
            bool present = query.StartsWith("IsApiContractPresent(", StringComparison.Ordinal);
            if (!present && !query.StartsWith("IsApiContractNotPresent(", StringComparison.Ordinal)) return false;
            var args = query[(query.IndexOf('(') + 1)..].TrimEnd(')').Split(',');
            bool holds = args.Length >= 2 && args[0] == "Windows.Foundation.UniversalApiContract" &&
                         int.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var v) && v <= 19;
            return present ? holds : !holds;
        }
        static XNamespace Plain(XNamespace ns) => ns.NamespaceName[..ns.NamespaceName.IndexOf('?')];

        foreach (var e in root.DescendantsAndSelf().Reverse().ToList())
        {
            if (Holds(e.Name.NamespaceName) is { } keep)
            {
                if (!keep) { e.Remove(); continue; }
                e.Name = Plain(e.Name.Namespace) + e.Name.LocalName;
            }
            foreach (var a in e.Attributes().ToList())
            {
                if (a.IsNamespaceDeclaration)
                {
                    if (a.Value.Contains('?')) a.Remove();
                    continue;
                }
                if (Holds(a.Name.NamespaceName) is not { } keepAttribute) continue;
                a.Remove();
                if (keepAttribute) e.SetAttributeValue(a.Name.LocalName, a.Value);
            }
        }
    }

    /* TargetType="CalculatorButton": the compiled XAML names an application type without its prefix; the prefix of
     * the application namespace that has it */
    internal static void QualifyTargetTypes(XElement root)
    {
        /* an application type of that name wins over Uno's (the Calculator's TitleBar, Uno's Controls.TitleBar) */
        foreach (var e in root.DescendantsAndSelf())
        {
            var a = e.Attribute("TargetType");
            if (a == null || a.Value.Contains(':')) continue;
            foreach (var ns in e.Attributes().Concat(e.Ancestors().SelectMany(p => p.Attributes())).Where(d => d.IsNamespaceDeclaration))
            {
                if (!ns.Value.StartsWith("using:", StringComparison.Ordinal)) continue;
                if (NativeControls.StandIn(ns.Value["using:".Length..] + "." + a.Value) == null) continue;
                a.Value = ns.Name.LocalName + ":" + a.Value;
                break;
            }
        }
    }

    /* the decompiled XAML writes an attached property in a style setter without its owner (Property="AccessibilityView"
     * for AutomationProperties.AccessibilityView): when the style's target type has no property of that name, the Uno
     * class with an attached property of that name (static SetX and XProperty) owns it */
    static readonly string[] preferredOwners = ["ScrollViewer", "AutomationProperties", "ToolTipService", "Grid", "Canvas", "Typography"];
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> attachedOwners = new();

    static string? AttachedOwner(string property) => attachedOwners.GetOrAdd(property, p =>
    {
        bool Owns(Type t) =>
            t.GetMethod("Set" + p, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) is { } set &&
            set.GetParameters().Length == 2 &&
            (t.GetProperty(p + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null ||
             t.GetField(p + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null);
        foreach (var name in preferredOwners)
            if (UnoType(name) is { } t && Owns(t)) return name;
        foreach (var t in typeof(Mux.UIElement).Assembly.GetExportedTypes())
            if (unoNamespaces.Contains(t.Namespace) && Owns(t)) return t.Name;
        return null;
    });

    static bool HasProperty(Type t, string p) =>
        t.GetProperty(p, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy) != null ||
        t.GetProperty(p + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy) != null ||
        t.GetField(p + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy) != null;

    internal static void QualifyAttachedSetters(XElement root)
    {
        foreach (var setter in root.Descendants().Where(e => e.Name.LocalName == "Setter").ToList())
        {
            var a = setter.Attribute("Property");
            if (a == null || a.Value.Contains('.') || a.Value.Contains('(')) continue;
            var target = setter.Ancestors().FirstOrDefault(p => p.Name.LocalName == "Style")?.Attribute("TargetType")?.Value;
            if (target == null) continue;
            Type? type = null;
            bool has;
            var colon = target.IndexOf(':');
            if (colon < 0)
            {
                if ((type = UnoType(target)) == null) continue;
                has = HasProperty(type, a.Value);
            }
            else if (setter.GetNamespaceOfPrefix(target[..colon]) is { } ns && ns.NamespaceName.StartsWith("using:", StringComparison.Ordinal))
            {
                /* an application type: its Uno base's properties, and those the application registers (not the
                 * stand-in's own forwarding members, made for every setter name) */
                var full = ns.NamespaceName["using:".Length..] + "." + target[(colon + 1)..];
                if ((type = NativeControls.StandIn(full)) == null) continue;
                has = HasProperty(type.BaseType!, a.Value) || NativeControls.FindProperty(full, a.Value) != null;
            }
            else continue;
            if (has) continue;
            if (AttachedOwner(a.Value) is { } owner) a.Value = owner + "." + a.Value;
            else
            {
                /* a property Uno does not have (RichEditBox.ContentLinkForegroundColor): the setter is left out */
                Ccw.Log($"setter {target}.{a.Value}: no such property in Uno, left out");
                setter.Remove();
            }
        }
    }

    /* entries Uno's reader rejects in a resource dictionary, taken out and added after it is loaded:
     * <StaticResource x:Key ResourceKey/> (another resource under a second key) and enumeration values written as
     * element text (<ApplicationTheme x:Key>Dark</ApplicationTheme>) */
    /* Value gets the dictionary the entry goes into and the root */
    sealed record Deferred(string? Theme, string Key, Func<Mux.ResourceDictionary, Mux.ResourceDictionary, object?> Value);

    static List<Deferred> TakeDeferred(XElement dict)
    {
        var deferred = new List<Deferred>();
        void Scan(XElement d, string? theme)
        {
            foreach (var e in d.Elements().ToList())
            {
                var key = e.Attribute(X + "Key")?.Value;
                var local = e.Name.LocalName;
                if (local == "ResourceDictionary.ThemeDictionaries")
                    foreach (var t in e.Elements()) Scan(t, t.Attribute(X + "Key")?.Value);
                else if (local == "ResourceDictionary.MergedDictionaries")
                    foreach (var m in e.Elements()) Scan(m, theme);
                else if (local == "StaticResource" && key != null && e.Attribute("ResourceKey") is { } target)
                {
                    e.Remove();
                    deferred.Add(new(theme, key, (into, root) => Find(into, target.Value) ?? Find(root, target.Value)));
                }
                else if (key != null && !e.HasElements && e.Value.Trim().Length > 0 && UnoType(local) is { IsEnum: true } type)
                {
                    e.Remove();
                    var text = e.Value.Trim();
                    deferred.Add(new(theme, key, (_, _) => Enum.Parse(type, text)));
                }
            }
        }
        Scan(dict, null);
        return deferred;
    }

    static object? Find(Mux.ResourceDictionary root, string key) =>
        root.TryGetValue(key, out var v) ? v : Mux.Application.Current.Resources.TryGetValue(key, out v) ? v : null;

    static void ApplyDeferred(Mux.ResourceDictionary root, List<Deferred> deferred)
    {
        foreach (var d in deferred)
        {
            var into = root;
            if (d.Theme != null)
            {
                if (!root.ThemeDictionaries.TryGetValue(d.Theme, out var t) || t is not Mux.ResourceDictionary theme)
                    root.ThemeDictionaries[d.Theme] = theme = new Mux.ResourceDictionary();
                into = theme;
            }
            var value = d.Value(into, root);
            if (value == null) { Ccw.Log($"application resource {d.Key}: not found as resource"); continue; }
            into[d.Key] = value;
        }
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
