// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The Windows.UI.Xaml members the XAML probe (tests/uwp/xaml/cx-probe) uses, implemented over Uno. Every other member
// of the generated interfaces logs "not implemented" and returns E_NOTIMPL.
using System.Runtime.InteropServices;
using global::Uno.UI.Hosting;
using Mux = Microsoft.UI.Xaml;
using UnoControls = Microsoft.UI.Xaml.Controls;

namespace XamlShim.Abi;

static class Shim
{
    static long tokens;
    public static long Token() => Interlocked.Increment(ref tokens);
    public static readonly List<IntPtr> Kept = new();      /* native objects the framework holds (never released here) */

    /* the shim for a Uno object, made once: the generated class of the same name (Microsoft.UI.Xaml.X is
     * Windows.UI.Xaml.X, class UI_Xaml_X), or that of its nearest base that has one; null when there is none */
    public static ShimObject? TryFor(object uno)
    {
        if (Ccw.Wrapped(uno) is { } existing) return existing;
        var type = ShimType(uno.GetType());
        if (type == null) return null;
        return Ccw.Wrap(uno, () =>
        {
            var s = (ShimObject)Activator.CreateInstance(type)!;
            s.UnoObject = uno;
            return s;
        });
    }

    public static ShimObject For(object uno) =>
        TryFor(uno) ?? throw new NotSupportedException($"no shim class for {uno.GetType().FullName}");

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Type?> shimTypes = new();

    static Type? ShimType(Type unoType) => shimTypes.GetOrAdd(unoType, t =>
    {
        var found = FindShimType(t);
        /* a Uno framework class served by its base's shim lacks its own interfaces (an application's cast to them
         * fails): logged once per class, to find the classes to generate */
        if (found != null && t.FullName is { } n && n.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal) &&
            !found.Name.EndsWith(n[n.LastIndexOf('.')..].Replace('.', '_'), StringComparison.Ordinal))
            Ccw.Log($"shim of base class {found.Name} for {n}");
        return found;
    });

    static Type? FindShimType(Type? t)
    {
        const string prefix = "Microsoft.UI.Xaml.";
        for (; t != null; t = t.BaseType)
        {
            var name = t.FullName ?? "";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || t.IsGenericType) continue;
            /* Uno's classes are both Windows.UI.Xaml's and WinUI 2's (Microsoft.UI.Xaml.Controls.NavigationView):
             * the WinUI 2 shim when there is one, the Windows.UI.Xaml one otherwise */
            var rest = name[prefix.Length..].Replace('.', '_');
            foreach (var candidate in new[] { "XamlShim.Abi.Microsoft_UI_Xaml_" + rest, "XamlShim.Abi.UI_Xaml_" + rest })
            {
                var shim = typeof(Shim).Assembly.GetType(candidate);
                if (shim != null && typeof(ShimObject).IsAssignableFrom(shim) && !shim.IsAbstract &&
                    !typeof(ShimFactory).IsAssignableFrom(shim))
                    return shim;
            }
        }
        return null;
    }

    public static object? UnoOf(IntPtr p) => Ccw.Unwrap(p)?.UnoObject;

    /* invoke a delegate (IUnknown-based: Invoke is slot 3) with two IInspectable arguments */
    public static unsafe int Invoke(IntPtr handler, IntPtr sender, IntPtr args) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)(*(IntPtr**)handler)[3])(handler, sender, args);

    public static void Raise(IntPtr handler, ShimObject sender, ShimObject? args)
    {
        try
        {
            Ccw.Trace($"raise: sender {sender.RuntimeClassName} {Ccw.Describe(sender)}");
            var s = Ccw.Get(sender);
            var a = Ccw.GetDefault(args);
            Ccw.Trace($"raise to {handler:x}: sender {s:x} args {a:x}");
            int hr = Invoke(handler, s, a);
            Ccw.Trace($"handler returned {hr:x8}");
            if (hr < 0) Ccw.Log($"handler returned {hr:x8}");
            Ccw.ComRelease(s);
            if (a != IntPtr.Zero) Ccw.ComRelease(a);
        }
        catch (Exception e)
        {
            Ccw.Log($"raise failed: {e}");
        }
    }

    /* composable activation shared by the factories: new shim around uno, inner of outer when there is one */
    public static unsafe int Compose(ShimObject shim, IntPtr outer, IntPtr* inner, IntPtr* result)
    {
        /* one shim per Uno object: register the factory-made shim so Shim.For finds it */
        if (shim.UnoObject != null) Ccw.Wrap(shim.UnoObject, () => shim);
        var (i, instance) = Ccw.Compose(shim, outer);
        if (inner != null) *inner = i;
        Ccw.Trace($"compose {shim.RuntimeClassName}: {Ccw.Describe(shim)}");
        /* the framework keeps the application's objects alive (Application.Current, elements in the tree): the
         * experiment holds one reference on every outer object for good; lifetime tracking comes later */
        if (outer != IntPtr.Zero) { Ccw.ComAddRef(outer); lock (Kept) Kept.Add(outer); }
        *result = outer == IntPtr.Zero ? Ccw.GetDefault(shim) : instance;
        if (outer == IntPtr.Zero && instance != IntPtr.Zero) Ccw.ComRelease(instance);
        return 0;
    }
}

// ---- Application ----

public unsafe partial class UI_Xaml_Application
{
    public static UI_Xaml_Application? Current;

    /* Application.Suspending and Resuming: the application's handlers, raised from CoreApplication's events with
     * their sender and arguments as received (on Windows the sender is not the Application either) */
    static readonly List<IntPtr> suspending = new(), resuming = new();

    int IUI_Xaml_IApplication_Abi.add_Suspending(IntPtr handler, long* token)
    {
        Ccw.ComAddRef(handler);
        lock (suspending) suspending.Add(handler);
        *token = Shim.Token();
        return 0;
    }

    int IUI_Xaml_IApplication_Abi.add_Resuming(IntPtr handler, long* token)
    {
        Ccw.ComAddRef(handler);
        lock (resuming) resuming.Add(handler);
        *token = Shim.Token();
        return 0;
    }

    public static void RaiseSuspending(IntPtr sender, IntPtr args) => Raise(suspending, sender, args, "Suspending");
    public static void RaiseResuming(IntPtr sender, IntPtr args) => Raise(resuming, sender, args, "Resuming");

    static void Raise(List<IntPtr> handlers, IntPtr sender, IntPtr args, string name)
    {
        IntPtr[] list;
        lock (handlers) list = handlers.ToArray();
        Ccw.Log($"{name} to {list.Length} handlers");
        foreach (var h in list)
        {
            int hr = Shim.Invoke(h, sender, args);
            if (hr < 0) Ccw.Log($"{name} handler returned {hr:x8}");
        }
    }

    /* Application.Resources: on Windows the framework loads them from App.xaml after App is constructed and before
     * OnLaunched, and reading them earlier (in App's constructor) fails with E_UNEXPECTED. They are a dictionary of
     * their own, merged into the Uno host's resources so that the framework's lookups find them. */
    Mux.ResourceDictionary? resources;

    public void SetResources(Mux.ResourceDictionary? value)
    {
        var host = Mux.Application.Current.Resources.MergedDictionaries;
        if (resources != null) host.Remove(resources);
        resources = value;
        if (value != null) host.Add(value);
    }

    int IUI_Xaml_IApplication_Abi.get_Resources(IntPtr* result)
    {
        *result = IntPtr.Zero;
        if (resources == null) return unchecked((int)0x8000FFFF);
        *result = Ccw.Get(Shim.For(resources), Iids.UI_Xaml_IResourceDictionary);
        return 0;
    }

    int IUI_Xaml_IApplication_Abi.put_Resources(IntPtr value)
    {
        if (resources == null) return unchecked((int)0x8000FFFF);
        if (Shim.UnoOf(value) is not Mux.ResourceDictionary d) return unchecked((int)0x80070057);
        SetResources(d);
        return 0;
    }

    int IUI_Xaml_IApplication_Abi.Exit()
    {
        Ccw.Log("Application.Exit");
        Mux.Application.Current.Exit();
        CoreWindowHost.QuitCoreDispatcherLoop();
        return 0;
    }
}

public unsafe partial class UI_Xaml_Application_Factory
{
    int IUI_Xaml_IApplicationFactory_Abi.CreateInstance(IntPtr outer, IntPtr* inner, IntPtr* result)
    {
        var app = new UI_Xaml_Application();
        UI_Xaml_Application.Current = app;
        Ccw.Log($"Application created, outer {(outer != IntPtr.Zero ? "yes" : "no")}");
        return Shim.Compose(app, outer, inner, result);
    }

    int IUI_Xaml_IApplicationStatics_Abi.get_Current(IntPtr* result)
    {
        *result = Ccw.GetDefault(UI_Xaml_Application.Current);
        return 0;
    }

    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, Guid* iid, IntPtr* factory);

    /* as on Windows: CoreApplication.Run with the shim's view source; the view's Run runs the Uno host on the view's
     * thread. Without a CoreApplication (Run refused: not packaged, wrong apartment), the host runs on a thread of its
     * own as before. */
    /* XAMLSHIM_RENDER=software|opengl|vulkan forces Uno's rendering backend (diagnostics); unset, Uno negotiates */
    static void ChooseRenderer(global::Uno.UI.Hosting.Win32HostBuilder b)
    {
        var forced = Environment.GetEnvironmentVariable("XAMLSHIM_RENDER")?.ToLowerInvariant() switch
        {
            "software" => global::Uno.UI.Hosting.Win32RenderingBackend.Software,
            "opengl" => global::Uno.UI.Hosting.Win32RenderingBackend.OpenGL,
            "vulkan" => global::Uno.UI.Hosting.Win32RenderingBackend.Vulkan,
            _ => (global::Uno.UI.Hosting.Win32RenderingBackend?)null,
        };
        if (forced is { } f) { b.ForceRenderingBackend(f); Ccw.Log($"rendering backend forced: {f}"); }
    }

    int IUI_Xaml_IApplicationStatics_Abi.Start(IntPtr callback)
    {
        Ccw.Log("Application.Start");
        Ccw.ComAddRef(callback);
        int status = 0;
        void RunHost()
        {
            try
            {
                CoreWindowHost.MatchDpiAwareness();
                XamlShim.UnoHostApp.Launched = () => Launch(callback);
                var host = global::Uno.UI.Hosting.UnoPlatformHostBuilder.Create()
                    .App(() => new XamlShim.UnoHostApp())
                    .UseWin32(ChooseRenderer)
                    .Build();
                host.Run();
                Ccw.Log("Uno host returned");
            }
            catch (Exception e)
            {
                Ccw.Log($"Uno host failed: {e}");
                status = unchecked((int)0x80004005);
            }
        }

        var name = HString.Create("Windows.ApplicationModel.Core.CoreApplication");
        var iid = Iids.ApplicationModel_Core_ICoreApplication;
        IntPtr core;
        int hr = RoGetActivationFactory(name, &iid, &core);
        HString.Delete(name);
        if (hr >= 0)
        {
            /* CoreApplication.Suspending and Resuming (EventHandler<SuspendingEventArgs>, EventHandler<Object>) to
             * Application.Suspending and Resuming */
            const string eventHandler = "pinterface({9de1c535-6ae1-11e0-84e1-18a905bcc53f};";
            var suspendIid = PInterface.Iid(eventHandler + "rc(Windows.ApplicationModel.SuspendingEventArgs;" +
                                            PInterface.Sig(Iids.ApplicationModel_ISuspendingEventArgs) + "))");
            var resumeIid = PInterface.Iid(eventHandler + "cinterface(IInspectable))");
            var s = Ccw.Get(new NativeDelegate(suspendIid, UI_Xaml_Application.RaiseSuspending), suspendIid);
            var r = Ccw.Get(new NativeDelegate(resumeIid, UI_Xaml_Application.RaiseResuming), resumeIid);
            long token;
            int hs = ApplicationModel_Core_ICoreApplication_Call.add_Suspending(core, s, &token);
            int hres = ApplicationModel_Core_ICoreApplication_Call.add_Resuming(core, r, &token);
            Ccw.Log($"CoreApplication.Suspending {suspendIid} {hs:x8}, Resuming {resumeIid} {hres:x8}");
            Ccw.ComRelease(s);
            Ccw.ComRelease(r);

            var source = Ccw.GetDefault(new FrameworkViewSource(RunHost));
            hr = ApplicationModel_Core_ICoreApplication_Call.Run(core, source);   /* ends the process when the view ends */
            Ccw.Log($"CoreApplication.Run returned {hr:x8}");
            Ccw.ComRelease(source);
            Ccw.ComRelease(core);
            if (hr >= 0) return status;
        }
        Ccw.Log($"no CoreApplication view ({hr:x8}): the Uno host runs on a thread of its own");
        var thread = new Thread(RunHost);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return status;
    }

    /* on the UI thread, once Uno has launched: the application's callback creates App, then OnLaunched with the
     * launch arguments of the CoreApplication view (or a stand-in without one) */
    static void Launch(IntPtr callback)
    {
        CoreWindowHost.EnterCoreDispatcherLoop();   /* queued: runs once this launch has returned */
        int hr = UI_Xaml_ApplicationInitializationCallback_Call.Invoke(callback, IntPtr.Zero);
        Ccw.Log($"initialization callback {hr:x8}");
        var app = UI_Xaml_Application.Current;
        if (app == null) { Ccw.Log("no Application was created"); return; }
        try { app.SetResources(XbfLoader.LoadApplicationResources()); }
        catch (Exception e) { Ccw.Log($"application resources failed: {e}"); app.SetResources(new Mux.ResourceDictionary()); }
        IntPtr a;
        if (FrameworkView.LaunchArgs == IntPtr.Zero ||
            Ccw.QueryInterface(FrameworkView.LaunchArgs, Iids.ApplicationModel_Activation_ILaunchActivatedEventArgs, out a) < 0)
            a = Ccw.GetDefault(new ApplicationModel_Activation_LaunchActivatedEventArgs());
        var overrides = Ccw.Get(app, Iids.UI_Xaml_IApplicationOverrides);
        hr = UI_Xaml_IApplicationOverrides_Call.OnLaunched(overrides, a);
        Ccw.Log($"OnLaunched {hr:x8}");
        Ccw.ComRelease(a);
        Ccw.ComRelease(overrides);
    }

    int IUI_Xaml_IApplicationStatics_Abi.LoadComponent(IntPtr component, IntPtr uri) =>
        XbfLoader.Load(component, uri);

    int IUI_Xaml_IApplicationStatics_Abi.LoadComponentWithResourceLocation(IntPtr component, IntPtr uri, int location) =>
        XbfLoader.Load(component, uri);
}

public unsafe partial class ApplicationModel_Activation_LaunchActivatedEventArgs
{
    int IApplicationModel_Activation_ILaunchActivatedEventArgs_Abi.get_Arguments(IntPtr* result)
    {
        *result = IntPtr.Zero;   /* the empty HSTRING */
        return 0;
    }
}

// ---- Window ----

public unsafe partial class UI_Xaml_Window
{
    public Mux.Window? UnoWindow;

    int IUI_Xaml_IWindow_Abi.put_Content(IntPtr value)
    {
        UnoWindow!.Content = Shim.UnoOf(value) as Mux.UIElement;
        Ccw.Log($"Window.Content = {UnoWindow.Content?.GetType().Name ?? "null"}");
        return 0;
    }

    int IUI_Xaml_IWindow_Abi.get_Content(IntPtr* result)
    {
        var content = UnoWindow?.Content;
        *result = content != null ? Ccw.Get(Shim.For(content), Iids.UI_Xaml_IUIElement) : IntPtr.Zero;
        return 0;
    }

    /* Window.SetTitleBar: the element would be the drag area of a view extended into the title bar; Wine's CoreWindow
     * keeps its standard frame (CoreApplicationViewTitleBar is never extended), so it is only kept */
    public Mux.UIElement? TitleBar;

    int IUI_Xaml_IWindow2_Abi.SetTitleBar(IntPtr value)
    {
        TitleBar = Shim.UnoOf(value) as Mux.UIElement;
        Ccw.Log($"Window.SetTitleBar {TitleBar?.GetType().Name ?? "null"}: kept (the frame is the standard one)");
        return 0;
    }

    /* the view's CoreWindow and its CoreDispatcher (Wine's twinapi.appcore), as on Windows; null without a view */
    int IUI_Xaml_IWindow_Abi.get_CoreWindow(IntPtr* result)
    {
        *result = FrameworkView.CoreWindow;
        if (*result != IntPtr.Zero) Ccw.ComAddRef(*result);
        return 0;
    }

    int IUI_Xaml_IWindow_Abi.get_Dispatcher(IntPtr* result)
    {
        *result = IntPtr.Zero;
        if (FrameworkView.CoreWindow == IntPtr.Zero) return 0;
        return UI_Core_ICoreWindow_Call.get_Dispatcher(FrameworkView.CoreWindow, result);
    }

    /* Activated, Closed, SizeChanged and VisibilityChanged: the CoreWindow's events of the same names (their arguments
     * are the CoreWindow's), raised with this window as the sender; without a CoreWindow they are never raised */
    static readonly Guid activatedHandler = new("26a73b35-a7f9-52db-88d6-15726deb2523");   /* TypedEventHandler<CoreWindow, WindowActivatedEventArgs> */
    static readonly Guid closedHandler = new("d08e4f66-3457-57f2-ba0c-cb347133bd15");      /* TypedEventHandler<CoreWindow, CoreWindowEventArgs> */
    static readonly Guid sizeChangedHandler = new("318dbb67-4089-5690-9040-1d454fb2f686"); /* TypedEventHandler<CoreWindow, WindowSizeChangedEventArgs> */
    static readonly Guid visibilityHandler = new("14b7f692-5583-52a1-aa42-fc1843c0f748");  /* TypedEventHandler<CoreWindow, VisibilityChangedEventArgs> */
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, IntPtr> relayed = new();

    int Relay(IntPtr handler, Guid iid, delegate*<IntPtr, IntPtr, long*, int> add, long* token)
    {
        if (handler == IntPtr.Zero) return unchecked((int)0x80070057);   /* E_INVALIDARG */
        if (FrameworkView.CoreWindow == IntPtr.Zero) { *token = Shim.Token(); return 0; }
        Ccw.ComAddRef(handler);
        var relay = new NativeDelegate(iid, (sender, args) =>
        {
            var self = Ccw.Get(this);
            try { int hr = Shim.Invoke(handler, self, args); if (hr < 0) Ccw.Log($"Window event handler returned {hr:x8}"); }
            finally { Ccw.ComRelease(self); }
        });
        var r = Ccw.Get(relay, iid);
        int result = add(FrameworkView.CoreWindow, r, token);
        Ccw.ComRelease(r);
        if (result < 0) Ccw.ComRelease(handler);
        else relayed[*token] = handler;
        return result;
    }

    int Unrelay(long token, delegate*<IntPtr, long, int> remove)
    {
        if (!relayed.TryRemove(token, out var handler)) return 0;
        Ccw.ComRelease(handler);
        return FrameworkView.CoreWindow == IntPtr.Zero ? 0 : remove(FrameworkView.CoreWindow, token);
    }

    int IUI_Xaml_IWindow_Abi.add_Activated(IntPtr handler, long* token) => Relay(handler, activatedHandler, &UI_Core_ICoreWindow_Call.add_Activated, token);
    int IUI_Xaml_IWindow_Abi.remove_Activated(long token) => Unrelay(token, &UI_Core_ICoreWindow_Call.remove_Activated);
    int IUI_Xaml_IWindow_Abi.add_Closed(IntPtr handler, long* token) => Relay(handler, closedHandler, &UI_Core_ICoreWindow_Call.add_Closed, token);
    int IUI_Xaml_IWindow_Abi.remove_Closed(long token) => Unrelay(token, &UI_Core_ICoreWindow_Call.remove_Closed);
    int IUI_Xaml_IWindow_Abi.add_SizeChanged(IntPtr handler, long* token) => Relay(handler, sizeChangedHandler, &UI_Core_ICoreWindow_Call.add_SizeChanged, token);
    int IUI_Xaml_IWindow_Abi.remove_SizeChanged(long token) => Unrelay(token, &UI_Core_ICoreWindow_Call.remove_SizeChanged);
    int IUI_Xaml_IWindow_Abi.add_VisibilityChanged(IntPtr handler, long* token) => Relay(handler, visibilityHandler, &UI_Core_ICoreWindow_Call.add_VisibilityChanged, token);
    int IUI_Xaml_IWindow_Abi.remove_VisibilityChanged(long token) => Unrelay(token, &UI_Core_ICoreWindow_Call.remove_VisibilityChanged);

    int IUI_Xaml_IWindow_Abi.Activate()
    {
        /* the first layout already at the CoreWindow's size, as on Windows: place Uno's window before it is shown */
        bool placed = FrameworkView.CoreHwnd != IntPtr.Zero && CoreWindowHost.Embed();
        UnoWindow!.Activate();
        Ccw.Log($"Window.Activate (placed before: {placed})");
        /* place Uno's window in the CoreWindow as soon as it exists (a few tries: Uno makes it asynchronously) */
        if (FrameworkView.CoreHwnd != IntPtr.Zero && !placed && !CoreWindowHost.Embed())
        {
            int tries = 0;
            var embed = new Mux.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            embed.Tick += (s, e) =>
            {
                if (CoreWindowHost.Embed() || ++tries == 40) embed.Stop();
                if (tries == 40) Ccw.Log("Uno's window was not found: it stays a window of its own");
            };
            embed.Start();
        }
        if (!Ccw.Tracing) return 0;
        static string FreeMeasure(Microsoft.UI.Xaml.Controls.TextBlock t)
        {
            var copy = new Microsoft.UI.Xaml.Controls.TextBlock { Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight };
            copy.Measure(new Windows.Foundation.Size(1000, 1000));
            return $"{copy.DesiredSize.Width:0.#}x{copy.DesiredSize.Height:0.#}";
        }
        static string FontMetrics(Microsoft.UI.Xaml.Controls.TextBlock t)
        {
            try
            {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var details = typeof(Microsoft.UI.Xaml.Controls.TextBlock).GetMethod("GetDefaultFontDetails", all)!.Invoke(t, null)!;
                var handle = details.GetType().GetProperty("FontHandle", all)!.GetValue(details)!;
                float P(string n) => Convert.ToSingle(handle.GetType().GetProperty(n, all)!.GetValue(handle), System.Globalization.CultureInfo.InvariantCulture);
                return $"{handle.GetType().Name} asc {P("Ascent"):0.#} desc {P("Descent"):0.#} cap {P("CapHeight"):0.#}";
            }
            catch (Exception e) { return "?" + e.GetType().Name; }
        }
        static string Short(object? v) => v switch
        {
            null => "null",
            string s => "'" + (s.Length > 20 ? s[..20] + "..." : s) + "'",
            Mux.Media.SolidColorBrush b => $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" + (b.Opacity < 1 ? $"@{b.Opacity:0.##}" : ""),
            _ => v.GetType().Name is var n && v.ToString() is { } t && t != v.GetType().FullName ? (t.Length > 20 ? t[..20] : t) : v.GetType().Name,
        };
        /* the tree once the application's first view is up (its content is navigated to after OnLaunched) */
        var timer = new Mux.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        var lateDone = false;
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            void Walk(Mux.DependencyObject o, int depth)
            {
                if (o is Mux.FrameworkElement fe)
                    Ccw.Log($"tree {new string(' ', depth * 2)}{fe.GetType().Name}" + (string.IsNullOrEmpty(fe.Name) ? "" : $" {fe.Name}") +
                            $" {fe.ActualWidth:0}x{fe.ActualHeight:0}" +
                            (fe.ActualHeight - Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetAvailableSize(fe).Height > 2 ? $" STALE avail {Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetAvailableSize(fe)} desired {fe.DesiredSize}" : "") + (fe.Visibility != Mux.Visibility.Visible ? " collapsed" : "") +
                            (fe.Opacity < 1 ? $" opacity {fe.Opacity:0.##}" : "") + (fe.IsLoaded ? "" : " not loaded") +
                            (fe is Microsoft.UI.Xaml.Controls.Control ctl && !ctl.IsEnabled ? " disabled" : "") +
                            (fe is Microsoft.UI.Xaml.Controls.TextBlock tb ? $" text '{tb.Text}' size {tb.FontSize} font {tb.FontFamily?.Source} fg {Short(tb.Foreground)} weight {tb.FontWeight.Weight} stretch {tb.FontStretch} bounds {tb.TextLineBounds} lh {tb.LineHeight} lss {tb.LineStackingStrategy} maxl {tb.MaxLines} desired {tb.DesiredSize.Width:0.#}x{tb.DesiredSize.Height:0.#} opacity {tb.Opacity} metrics {FontMetrics(tb)} free {FreeMeasure(tb)} slot {Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(tb)} avail {Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetAvailableSize(tb)} margin {tb.Margin} pad {tb.Padding} va {tb.VerticalAlignment} h {tb.Height} maxh {tb.MaxHeight} wrap {tb.TextWrapping} trim {tb.TextTrimming}" +
                                (tb.GetBindingExpression(Microsoft.UI.Xaml.Controls.TextBlock.TextProperty) is { } be
                                    ? $" bound {be.ParentBinding?.Path?.Path} source {be.DataItem?.GetType().Name ?? "null"}" +
                                      $" relative {be.ParentBinding?.RelativeSource?.Mode}" : "") : "") +
                            /* the values of the properties the application registered for this element's type */
                            string.Concat(DependencyProperties.Registered.Where(r => r.Key.Owner.IsInstanceOfType(fe))
                                .Select(r => $" {r.Key.Name}={Short(fe.GetValue(r.Value))}")) +
                            (fe is Microsoft.UI.Xaml.Controls.ContentControl cc0 ? $" content {cc0.Content?.GetType().Name ?? "null"}" :
                             fe is Microsoft.UI.Xaml.Controls.ContentPresenter cp0 ? $" content {cp0.Content?.GetType().Name ?? "null"} cppad {cp0.Padding} bt {cp0.BorderThickness} margin {cp0.Margin} cr {cp0.CornerRadius} desired {cp0.DesiredSize} avail {Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetAvailableSize(cp0)} cpva {cp0.VerticalContentAlignment} bounds {cp0.TextLineBounds}" : "") +
                            /* what sizes a control that came out flat */
                            (fe is Microsoft.UI.Xaml.Controls.Control c && fe.ActualHeight == 0 && fe.Visibility == Mux.Visibility.Visible
                                ? $" [style {(c.Style != null ? "set" : "none")} template {(c.Template != null ? "set" : "none")} " +
                                  $"h {c.Height} min {c.MinHeight} max {c.MaxHeight} v {c.VerticalAlignment} margin {c.Margin} " +
                                  $"padding {c.Padding} font {c.FontSize} desired {c.DesiredSize}]" : ""));
                if (depth > 40) return;
                int n = Mux.Media.VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++) Walk(Mux.Media.VisualTreeHelper.GetChild(o, i), depth + 1);
            }
            Ccw.Log($"window bounds {UnoWindow.Bounds}");
            for (var p = UnoWindow.Content != null ? Mux.Media.VisualTreeHelper.GetParent(UnoWindow.Content) : null; p != null;
                 p = Mux.Media.VisualTreeHelper.GetParent(p))
                if (p is Mux.FrameworkElement a)
                    Ccw.Log($"ancestor {a.GetType().FullName} {a.ActualWidth:0}x{a.ActualHeight:0} h {a.HorizontalAlignment} " +
                            $"style {(a.Style != null ? "set" : "none")} " +
                            (a is Microsoft.UI.Xaml.Controls.ContentControl cc ? $"hca {cc.HorizontalContentAlignment}" :
                             a is Microsoft.UI.Xaml.Controls.ContentPresenter cp ? $"hca {cp.HorizontalContentAlignment}" : ""));
            if (UnoWindow.Content != null) Walk(UnoWindow.Content, 0);
            /* XAMLSHIM_LATE_TREE=<seconds>: the tree once more that long after the first (with INPUT, to see what typing changed) */
            if (!lateDone && int.TryParse(Environment.GetEnvironmentVariable("XAMLSHIM_LATE_TREE"), out var late) && late > 0)
            {
                lateDone = true;
                Ccw.Log("tree again");
                timer.Interval = TimeSpan.FromSeconds(late);
                timer.Start();
            }
        };
        timer.Start();
        return 0;
    }
}

public unsafe partial class UI_Xaml_Window_Factory
{
    UI_Xaml_Window? current;

    int IUI_Xaml_IWindowStatics_Abi.get_Current(IntPtr* result)
    {
        current ??= new UI_Xaml_Window { UnoWindow = XamlShim.UnoHostApp.MainWindow };
        *result = Ccw.GetDefault(current);
        return 0;
    }
}

// ---- elements ----

public unsafe partial class UI_Xaml_UIElement
{
    public Mux.UIElement UnoElement => (Mux.UIElement)UnoObject!;

    int IUI_Xaml_IUIElement_Abi.TransformToVisual(IntPtr visual, IntPtr* result)
    {
        var target = Shim.UnoOf(visual) as Mux.UIElement;
        var t = UnoElement.TransformToVisual(target);
        *result = Ccw.GetDefault(new UI_Xaml_Media_GeneralTransform { UnoObject = t });
        return 0;
    }
}

public unsafe partial class UI_Xaml_FrameworkElement
{
    Mux.FrameworkElement Fe => (Mux.FrameworkElement)UnoObject!;

    int IUI_Xaml_IFrameworkElement_Abi.get_ActualWidth(double* result) { *result = Fe.ActualWidth; return 0; }
    int IUI_Xaml_IFrameworkElement_Abi.get_ActualHeight(double* result) { *result = Fe.ActualHeight; return 0; }

    int IUI_Xaml_IFrameworkElement_Abi.SetBinding(IntPtr dp, IntPtr binding)
    {
        if (Shim.UnoOf(binding) is not Mux.Data.BindingBase b) return unchecked((int)0x80070057);   /* E_INVALIDARG */
        Fe.SetBinding(DependencyProperties.Of(dp).Uno, b);
        return 0;
    }
}

/* a state trigger of the application (derived from StateTriggerBase) sets its state through the protected SetActive */
public unsafe partial class UI_Xaml_StateTriggerBase
{
    static readonly System.Reflection.MethodInfo setActive = typeof(Mux.StateTriggerBase).GetMethod("SetActive",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(bool)])!;

    int IUI_Xaml_IStateTriggerBaseProtected_Abi.SetActive(byte isActive)
    {
        setActive.Invoke(UnoObject, [isActive != 0]);
        return 0;
    }
}

public unsafe partial class UI_Xaml_Media_GeneralTransform
{
    int IUI_Xaml_Media_IGeneralTransform_Abi.TransformPoint(Foundation_Point point, Foundation_Point* result)
    {
        var p = ((Mux.Media.GeneralTransform)UnoObject!).TransformPoint(new Windows.Foundation.Point(point.X, point.Y));
        result->X = (float)p.X;
        result->Y = (float)p.Y;
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_ContentControl
{
    /* any content: elements, values (the Calculator's digit buttons get a Char16) and the application's objects */
    int IUI_Xaml_Controls_IContentControl_Abi.put_Content(IntPtr value)
    {
        ((UnoControls.ContentControl)UnoObject!).Content = Boxing.Unbox(value);
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_UserControl
{
    int IUI_Xaml_Controls_IUserControl_Abi.put_Content(IntPtr value)
    {
        ((UnoControls.UserControl)UnoObject!).Content = Shim.UnoOf(value) as Mux.UIElement;
        return 0;
    }
}


// ---- Frame and Page ----

/* the arguments of a navigation (Frame.Navigate to a new page): the page, the parameter, the page's type; no Uno object */
public unsafe partial class UI_Xaml_Navigation_NavigationEventArgs
{
    readonly IntPtr content, parameter;
    readonly string pageType;
    readonly int pageKind;

    public UI_Xaml_Navigation_NavigationEventArgs() : this(IntPtr.Zero, IntPtr.Zero, "", 0) { }

    internal UI_Xaml_Navigation_NavigationEventArgs(IntPtr content, IntPtr parameter, string pageType, int pageKind)
    {
        this.content = content;
        this.parameter = parameter;
        this.pageType = pageType;
        this.pageKind = pageKind;
        if (content != IntPtr.Zero) Ccw.ComAddRef(content);
        if (parameter != IntPtr.Zero) Ccw.ComAddRef(parameter);
    }

    ~UI_Xaml_Navigation_NavigationEventArgs()
    {
        if (content != IntPtr.Zero) Ccw.ComRelease(content);
        if (parameter != IntPtr.Zero) Ccw.ComRelease(parameter);
    }

    static IntPtr AddRefed(IntPtr p) { if (p != IntPtr.Zero) Ccw.ComAddRef(p); return p; }

    int IUI_Xaml_Navigation_INavigationEventArgs_Abi.get_Content(IntPtr* result) { *result = AddRefed(content); return 0; }
    int IUI_Xaml_Navigation_INavigationEventArgs_Abi.get_Parameter(IntPtr* result) { *result = AddRefed(parameter); return 0; }
    int IUI_Xaml_Navigation_INavigationEventArgs_Abi.get_SourcePageType(TypeName* result)
    {
        result->Name = HString.Create(pageType);
        result->Kind = pageKind;
        return 0;
    }
    int IUI_Xaml_Navigation_INavigationEventArgs_Abi.get_NavigationMode(int* result) { *result = 0; return 0; }   /* New */
    int IUI_Xaml_Navigation_INavigationEventArgs_Abi.get_Uri(IntPtr* result) { *result = IntPtr.Zero; return 0; }
    int IUI_Xaml_Navigation_INavigationEventArgs_Abi.put_Uri(IntPtr value) => 0;
    int IUI_Xaml_Navigation_INavigationEventArgs2_Abi.get_NavigationTransitionInfo(IntPtr* result) { *result = IntPtr.Zero; return 0; }
}

public unsafe partial class UI_Xaml_Controls_Frame
{
    /* Navigate(TypeName, parameter): the page comes from the application's own metadata provider */
    int IUI_Xaml_Controls_IFrame_Abi.Navigate(TypeName type, IntPtr parameter, byte* result)
    {
        *result = 0;
        Ccw.Trace($"Navigate: name {type.Name:x} kind {type.Kind}");
        var name = HString.Read(type.Name);
        Ccw.Log($"Navigate {name}");
        var app = UI_Xaml_Application.Current ?? throw new InvalidOperationException("no Application");
        var provider = Ccw.Get(app, Iids.UI_Xaml_Markup_IXamlMetadataProvider);
        Ccw.Trace($"Navigate: provider {provider:x}");
        if (provider == IntPtr.Zero) { Ccw.Log("the application has no IXamlMetadataProvider"); return unchecked((int)0x80004002); }
        IntPtr xamlType, instance;
        int hr = UI_Xaml_Markup_IXamlMetadataProvider_Call.GetXamlType(provider, type, &xamlType);
        Ccw.ComRelease(provider);
        Ccw.Trace($"Navigate: GetXamlType {hr:x8} {xamlType:x}");
        if (hr < 0 || xamlType == IntPtr.Zero) { Ccw.Log($"GetXamlType {name}: {hr:x8}"); return hr < 0 ? hr : unchecked((int)0x80004005); }
        hr = UI_Xaml_Markup_IXamlType_Call.ActivateInstance(xamlType, &instance);
        Ccw.ComRelease(xamlType);
        if (hr < 0) { Ccw.Log($"ActivateInstance {name}: {hr:x8}"); return hr; }
        Shim.Kept.Add(instance);
        var page = Shim.UnoOf(instance);
        Ccw.Log($"Navigate {name}: {page?.GetType().Name ?? "not a shim object"}");
        var frame = (UnoControls.ContentControl)UnoObject!;
        if (frame.Content != null) backStackDepth++;        /* the back stack keeps the page type; navigation back is not done */
        frame.Content = page;

        /* the page's OnNavigatedTo (its IPageOverrides), after it became the frame's content, as on Windows */
        if (Ccw.QueryInterface(instance, Iids.UI_Xaml_Controls_IPageOverrides, out var overrides) >= 0)
        {
            var args = new UI_Xaml_Navigation_NavigationEventArgs(instance, parameter, name, type.Kind);
            var a = Ccw.Get(args, Iids.UI_Xaml_Navigation_INavigationEventArgs);
            int nhr = UI_Xaml_Controls_IPageOverrides_Call.OnNavigatedTo(overrides, a);
            Ccw.Log($"OnNavigatedTo {name}: {nhr:x8}");
            Ccw.ComRelease(a);
            Ccw.ComRelease(overrides);
        }
        *result = 1;
        return 0;
    }

    int backStackDepth;

    int IUI_Xaml_Controls_IFrame_Abi.get_BackStackDepth(int* result) { *result = backStackDepth; return 0; }
}

public unsafe partial class UI_Xaml_Controls_Page
{
    /* the Frame showing this page: the nearest Uno Frame among its ancestors */
    int IUI_Xaml_Controls_IPage_Abi.get_Frame(IntPtr* result)
    {
        *result = IntPtr.Zero;
        for (var o = Mux.Media.VisualTreeHelper.GetParent((Mux.DependencyObject)UnoObject!); o != null; o = Mux.Media.VisualTreeHelper.GetParent(o))
        {
            if (o is UnoControls.Frame f)
            {
                *result = Ccw.GetDefault(Shim.For(f));
                return 0;
            }
        }
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_Frame_Factory
{
    int IUI_Xaml_Controls_IFrameFactory_Abi.CreateInstance(IntPtr outer, IntPtr* inner, IntPtr* result) =>
        Shim.Compose(new UI_Xaml_Controls_Frame { UnoObject = new UnoControls.Frame() }, outer, inner, result);
}

public unsafe partial class UI_Xaml_Controls_Page_Factory
{
    int IUI_Xaml_Controls_IPageFactory_Abi.CreateInstance(IntPtr outer, IntPtr* inner, IntPtr* result)
    {
        var page = new UI_Xaml_Controls_Page { UnoObject = new UnoControls.Page() };
        return Shim.Compose(page, outer, inner, result);
    }
}

// ---- DispatcherTimer ----

public unsafe partial class UI_Xaml_DispatcherTimer
{
    readonly Mux.DispatcherTimer timer = new();

    int IUI_Xaml_IDispatcherTimer_Abi.put_Interval(long ticks) { timer.Interval = TimeSpan.FromTicks(ticks); return 0; }
    int IUI_Xaml_IDispatcherTimer_Abi.Start() { timer.Start(); return 0; }
    int IUI_Xaml_IDispatcherTimer_Abi.Stop() { timer.Stop(); return 0; }

    int IUI_Xaml_IDispatcherTimer_Abi.add_Tick(IntPtr handler, long* token)
    {
        Ccw.ComAddRef(handler);
        timer.Tick += (s, e) => Shim.Raise(handler, this, null);
        *token = Shim.Token();
        return 0;
    }
}

public unsafe partial class UI_Xaml_DispatcherTimer_Factory
{
    int IUI_Xaml_IDispatcherTimerFactory_Abi.CreateInstance(IntPtr outer, IntPtr* inner, IntPtr* result) =>
        Shim.Compose(new UI_Xaml_DispatcherTimer(), outer, inner, result);
}
