// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// Start-up the way Windows.UI.Xaml does it: Application.Start runs CoreApplication.Run with the shim's own view source.
// The system then makes the view's thread and its CoreWindow, calls Initialize, SetWindow, Load, raises the launch
// activation (the real arguments) and calls Run, in which the Uno host runs on that thread. Uno's window becomes a
// child that fills the CoreWindow, so the CoreWindow's size, visibility and lifecycle (suspend and resume) apply.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XamlShim.Abi;

/* the IID of a parameterized WinRT interface or delegate (pinterface signature, SHA-1 name-based GUID) */
public static class PInterface
{
    static readonly byte[] Namespace = [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];

    public static Guid Iid(string signature)
    {
        var data = Namespace.Concat(Encoding.UTF8.GetBytes(signature)).ToArray();
        var h = SHA1.HashData(data);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid((uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]), (ushort)(h[4] << 8 | h[5]), (ushort)(h[6] << 8 | h[7]),
                        h[8], h[9], h[10], h[11], h[12], h[13], h[14], h[15]);
    }

    public static string Sig(Guid g) => "{" + g.ToString("D") + "}";
}

/* a native-callable delegate (IUnknown and Invoke(sender, args), slot 3) for one delegate IID */
public sealed unsafe class NativeDelegate : ShimObject
{
    readonly Action<IntPtr, IntPtr> invoke;
    readonly (Guid, IntPtr, Func<object, bool>)[] table;
    static readonly IntPtr vtbl = MakeVtbl();

    public NativeDelegate(Guid iid, Action<IntPtr, IntPtr> invoke)
    {
        this.invoke = invoke;
        table = [(iid, vtbl, _ => true)];
    }

    public override string RuntimeClassName => "NativeDelegate";
    internal override (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]? CustomInterfaces => table;

    static IntPtr MakeVtbl()
    {
        var v = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(NativeDelegate), sizeof(IntPtr) * 4);
        var inspectable = stackalloc IntPtr[6];
        Ccw.FillInspectable(inspectable);
        v[0] = inspectable[0];
        v[1] = inspectable[1];
        v[2] = inspectable[2];
        v[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&Invoke;
        return (IntPtr)v;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Invoke(IntPtr block, IntPtr sender, IntPtr args)
    {
        try { ((NativeDelegate)Ccw.Target(block)).invoke(sender, args); return 0; }
        catch (Exception e) { return Ccw.Fail("NativeDelegate.Invoke", e); }
    }
}

public sealed unsafe class FrameworkViewSource : ShimObject, IApplicationModel_Core_IFrameworkViewSource_Abi
{
    public override string RuntimeClassName => "XamlShim.FrameworkViewSource";
    internal override (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]? CustomInterfaces =>
        [(ApplicationModel_Core_IFrameworkViewSource_Vtbl.Iid, ApplicationModel_Core_IFrameworkViewSource_Vtbl.Vtbl, _ => true)];

    readonly Action run;
    public FrameworkViewSource(Action run) { this.run = run; }

    int IApplicationModel_Core_IFrameworkViewSource_Abi.CreateView(IntPtr* result)
    {
        *result = Ccw.GetDefault(new FrameworkView(run));
        return 0;
    }
}

public sealed unsafe class FrameworkView : ShimObject, IApplicationModel_Core_IFrameworkView_Abi
{
    public override string RuntimeClassName => "XamlShim.FrameworkView";
    internal override (Guid Iid, IntPtr Vtbl, Func<object, bool> Implements)[]? CustomInterfaces =>
        [(ApplicationModel_Core_IFrameworkView_Vtbl.Iid, ApplicationModel_Core_IFrameworkView_Vtbl.Vtbl, _ => true)];

    static readonly Guid IID_ICoreWindowInterop = new("45D64A29-A63E-4CB6-B498-5781D298CB4F");
    static readonly Guid IID_ICoreApplicationView = new("638BB2DB-451D-4661-B099-414F34FFB9F1");

    /* the activation arguments of the launch (IActivatedEventArgs, AddRef'd), and the CoreWindow and its native window */
    public static IntPtr LaunchArgs;
    public static IntPtr CoreWindow;
    public static IntPtr CoreHwnd;

    readonly Action run;
    public FrameworkView(Action run) { this.run = run; }

    int IApplicationModel_Core_IFrameworkView_Abi.Initialize(IntPtr view)
    {
        /* TypedEventHandler<CoreApplicationView, IActivatedEventArgs> */
        var iid = PInterface.Iid("pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.ApplicationModel.Core.CoreApplicationView;" +
                                 PInterface.Sig(IID_ICoreApplicationView) + ");" + PInterface.Sig(Iids.ApplicationModel_Activation_IActivatedEventArgs) + ")");
        var handler = new NativeDelegate(iid, (sender, args) =>
        {
            if (args == IntPtr.Zero || LaunchArgs != IntPtr.Zero) return;
            Ccw.ComAddRef(args);
            LaunchArgs = args;
            int kind;
            ApplicationModel_Activation_IActivatedEventArgs_Call.get_Kind(args, &kind);
            Ccw.Log($"Activated, kind {kind}");
        });
        var h = Ccw.Get(handler, iid);
        long token;
        int hr = ApplicationModel_Core_ICoreApplicationView_Call.add_Activated(view, h, &token);
        Ccw.ComRelease(h);
        Ccw.Log($"view Initialize: Activated handler {iid} {hr:x8}");
        return 0;
    }

    int IApplicationModel_Core_IFrameworkView_Abi.SetWindow(IntPtr window)
    {
        Ccw.ComAddRef(window);
        CoreWindow = window;
        if (Ccw.QueryInterface(window, IID_ICoreWindowInterop, out var interop) >= 0)
        {
            IntPtr hwnd;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[3])(interop, &hwnd);
            CoreHwnd = hwnd;
            Ccw.ComRelease(interop);
        }
        Ccw.Log($"view SetWindow: native window {CoreHwnd:x}");
        return 0;
    }

    int IApplicationModel_Core_IFrameworkView_Abi.Load(IntPtr entryPoint)
    {
        Ccw.Log($"view Load {HString.Read(entryPoint)}");
        return 0;
    }

    int IApplicationModel_Core_IFrameworkView_Abi.Run()
    {
        Ccw.Log("view Run");
        run();
        return 0;
    }

    int IApplicationModel_Core_IFrameworkView_Abi.Uninitialize() => 0;
}

/* Uno's top-level window as a child that fills the CoreWindow */
public static unsafe class CoreWindowHost
{
    [DllImport("user32.dll")] static extern int EnumThreadWindows(uint thread, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> proc, IntPtr param);
    [DllImport("user32.dll")] static extern int IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern int GetClientRect(IntPtr hwnd, int* rect);
    [DllImport("user32.dll")] static extern int MoveWindow(IntPtr hwnd, int x, int y, int w, int h, int repaint);
    [DllImport("user32.dll")] static extern int ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const int GWL_STYLE = -16, GWLP_WNDPROC = -4;
    const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000,
               WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_VISIBLE = 0x10000000;
    const uint WM_SIZE = 0x0005, WM_SETFOCUS = 0x0007;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    [DllImport("user32.dll")] static extern int SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    static IntPtr child, oldProc;
    static readonly List<IntPtr> found = new();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, char* name, int count);

    static string ClassName(IntPtr hwnd)
    {
        char* name = stackalloc char[256];
        int n = GetClassNameW(hwnd, name, 256);
        return new string(name, 0, Math.Max(0, n));
    }

    /* top-level windows of the thread but the CoreWindow and IME windows, hidden ones too (Uno makes its window
     * when the Window object is created and shows it in Activate) */
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Collect(IntPtr hwnd, IntPtr param)
    {
        var cls = ClassName(hwnd);
        if (hwnd != FrameworkView.CoreHwnd && !cls.Contains("IME", StringComparison.Ordinal)) found.Add(hwnd);
        return 1;
    }

    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();

    /* SetParent only takes windows of compatible DPI awareness (ERROR_INVALID_STATE_TRANSITION, 5023, as on Windows):
     * the thread creates Uno's window with the CoreWindow's awareness context */
    public static void MatchDpiAwareness()
    {
        var core = FrameworkView.CoreHwnd;
        if (core == IntPtr.Zero) return;
        var want = GetWindowDpiAwarenessContext(core);
        var had = GetThreadDpiAwarenessContext();
        var previous = SetThreadDpiAwarenessContext(want);
        Ccw.Log($"DPI awareness: CoreWindow {want:x}, thread had {had:x}, set (previous {previous:x})");
    }

    /* true when Uno's window was found and placed */
    public static bool Embed()
    {
        var core = FrameworkView.CoreHwnd;
        if (core == IntPtr.Zero || child != IntPtr.Zero) return child != IntPtr.Zero;
        found.Clear();
        _ = EnumThreadWindows(GetCurrentThreadId(), &Collect, IntPtr.Zero);
        if (found.Count == 0) return false;
        Ccw.Log("thread windows: " + string.Join(", ", found.Select(h => $"{h:x} {ClassName(h)} visible {IsWindowVisible(h)}")));
        /* Uno's main window class (Uno.UI.Runtime.Skia.Win32), else the first candidate */
        child = found.FirstOrDefault(h => ClassName(h).StartsWith("UnoPlatform", StringComparison.Ordinal), found[0]);
        var style = (long)GetWindowLongPtrW(child, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtrW(child, GWL_STYLE, (IntPtr)style);
        /* the frame is gone: the client area is the whole window (else its origin stays inset by the old caption and borders) */
        _ = SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        var previousParent = SetParent(child, core);
        Ccw.Log($"SetParent({child:x}, {core:x}): previous parent {previousParent:x}, error {Marshal.GetLastWin32Error()}, now {GetParent(child):x}; " +
                $"DPI awareness of Uno's window {GetWindowDpiAwarenessContext(child):x}, of the CoreWindow {GetWindowDpiAwarenessContext(core):x}");
        Fit();
        oldProc = SetWindowLongPtrW(core, GWLP_WNDPROC, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&CoreProc);
        childProc = SetWindowLongPtrW(child, GWLP_WNDPROC, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ChildProc);
        _ = ShowWindow(child, 5);
        SetFocus(child);
        int hr = UI_Core_ICoreWindow_Call.Activate(FrameworkView.CoreWindow);
        Ccw.Log($"Uno window {child:x} placed in the CoreWindow {core:x} ({found.Count} candidates), CoreWindow.Activate {hr:x8}");
        ScheduleDrawCheck();
        return true;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

    /* diagnostics, once, 5 s after the window is placed: the windows' sizes, the root element's layout and what the
     * child window's pixels hold (read back through GDI: an X screenshot does not show every surface) */
    static void ScheduleDrawCheck()
    {
        if (Environment.GetEnvironmentVariable("XAMLSHIM_TRACE") != "1") return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var timer = queue?.CreateTimer();
        if (timer == null) { Ccw.Log("draw check: no dispatcher queue"); return; }
        /* pointer input that reaches Uno's tree, for the lab's input tests */
        if (Microsoft.UI.Xaml.Window.Current?.Content is { } top)
        {
            void Log(string what, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            {
                Microsoft.UI.Xaml.DependencyObject? o = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
                while (o != null && o is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) o = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(o);
                var button = o as Microsoft.UI.Xaml.Controls.Primitives.ButtonBase;
                Ccw.Log($"pointer {what}: {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.GetType().Name} " +
                        $"{(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.Name} at {e.GetCurrentPoint(top).Position} handled {e.Handled}; " +
                        (button == null ? "no button" : $"button {button.Name} enabled {button.IsEnabled} pressed {button.IsPressed} over {button.IsPointerOver} " +
                         $"mode {button.ClickMode} command {button.Command?.GetType().Name ?? "null"} parameter {button.CommandParameter?.GetType().Name ?? "null"} " +
                         $"data context {(button as Microsoft.UI.Xaml.FrameworkElement)?.DataContext?.GetType().Name ?? "null"} " +
                         $"binding {(button.GetBindingExpression(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase.CommandProperty) is { } cb ? "set, item " + (cb.DataItem?.GetType().Name ?? "null") + " path " + cb.ParentBinding?.Path?.Path : "none")}") +
                        $"; pointer type {e.Pointer.PointerDeviceType} captured {e.Pointer.IsInContact}");
            }
            top.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => Log("pressed", e)), true);
            top.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => Log("released", e)), true);
            top.AddHandler(Microsoft.UI.Xaml.UIElement.TappedEvent, new Microsoft.UI.Xaml.Input.TappedEventHandler((_, e) =>
                Ccw.Log($"tapped: {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.GetType().Name} {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.Name}")), true);
            top.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
                Ccw.Log($"key down: {e.Key} from {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.GetType().Name}")), true);
        }
        timer.Interval = TimeSpan.FromSeconds(8);
        timer.IsRepeating = true;
        /* XAMLSHIM_HITTEST="x,y;x,y": what Uno's hit test finds at these points of the window, at every tick (lab diagnostics) */
        var hitPoints = Environment.GetEnvironmentVariable("XAMLSHIM_HITTEST");
        var watchedPopups = new HashSet<object>();
        /* XAMLSHIM_THEMEKEYS="Key,Key": where each theme resource is defined (top level or a theme dictionary, through the merged dictionaries) */
        var themeKeys = Environment.GetEnvironmentVariable("XAMLSHIM_THEMEKEYS");
        bool themeKeysDone = false;
        void LogThemeKeys()
        {
            if (themeKeysDone || string.IsNullOrEmpty(themeKeys)) return;
            themeKeysDone = true;
            static string Show(object? v) => v switch
            {
                Microsoft.UI.Xaml.Media.SolidColorBrush b => $"SolidColorBrush {b.Color}",
                Windows.UI.Color c => $"Color {c}",
                null => "null",
                _ => v.GetType().Name
            };
            void Search(Microsoft.UI.Xaml.ResourceDictionary d, string key, string path, int depth)
            {
                if (depth > 12) return;
                if (d.ContainsKey(key)) Ccw.Log($"  theme key {key}: {path} top level = {Show(d[key])}");
                foreach (var td in d.ThemeDictionaries)
                    if (td.Value is Microsoft.UI.Xaml.ResourceDictionary t)
                    {
                        if (t.ContainsKey(key)) Ccw.Log($"  theme key {key}: {path} theme {td.Key} = {Show(t[key])}");
                        foreach (var m in t.MergedDictionaries) Search(m, key, $"{path} theme {td.Key} merged", depth + 1);
                    }
                int i = 0;
                foreach (var m in d.MergedDictionaries) Search(m, key, $"{path} merged[{i++}]", depth + 1);
            }
            foreach (var key in themeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                Ccw.Log($"theme key {key}: application theme {Microsoft.UI.Xaml.Application.Current.RequestedTheme}");
                Search(Microsoft.UI.Xaml.Application.Current.Resources, key, "application", 0);
            }
        }
        timer.Tick += (_, _) =>
        {
            LogThemeKeys();
            /* the content of the open popups: pointer input there does not reach the window's content element */
            if (!string.IsNullOrEmpty(hitPoints) && Microsoft.UI.Xaml.Window.Current?.Content is { } popupRoot)
                foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(popupRoot.XamlRoot))
                {
                    if (popup.Child is not { } pc) continue;
                    Ccw.Log($"open popup {popup.GetType().Name} child {pc.GetType().Name} {(pc as Microsoft.UI.Xaml.FrameworkElement)?.ActualWidth}x{(pc as Microsoft.UI.Xaml.FrameworkElement)?.ActualHeight}");
                    var firstSight = watchedPopups.Add(pc);
                    Walk(pc, 0);
                    if (!firstSight) continue;
                    pc.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
                        Ccw.Log($"popup pointer pressed: {e.OriginalSource?.GetType().Name} {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.Name} at {e.GetCurrentPoint(pc).Position} handled {e.Handled}")), true);
                    pc.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
                        Ccw.Log($"popup pointer released: {e.OriginalSource?.GetType().Name} {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.Name} handled {e.Handled}")), true);
                    pc.AddHandler(Microsoft.UI.Xaml.UIElement.TappedEvent, new Microsoft.UI.Xaml.Input.TappedEventHandler((_, e) =>
                        Ccw.Log($"popup tapped: {e.OriginalSource?.GetType().Name} {(e.OriginalSource as Microsoft.UI.Xaml.FrameworkElement)?.Name}")), true);
                    /* the expanders and the toggle buttons of the popup: what changes when they are clicked */
                    void Walk(Microsoft.UI.Xaml.DependencyObject d, int depth)
                    {
                        if (d is Microsoft.UI.Xaml.Controls.Expander ex && watchedPopups.Add(ex))
                        {
                            Ccw.Log($"expander {ex.Name} header {(ex.Header as Microsoft.UI.Xaml.FrameworkElement)?.GetType().Name} expanded {ex.IsExpanded} enabled {ex.IsEnabled} {ex.ActualWidth}x{ex.ActualHeight}");
                            ex.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Expander.IsExpandedProperty, (s, p) =>
                            {
                                Ccw.Log($"expander {ex.Name} IsExpanded -> {ex.IsExpanded}; content {ex.Content?.GetType().Name} {(ex.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualWidth}x{(ex.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualHeight}");
                                void Dump(Microsoft.UI.Xaml.DependencyObject o, int level)
                                {
                                    var f = o as Microsoft.UI.Xaml.FrameworkElement;
                                    Ccw.Log($"  {new string(' ', level)}{o.GetType().Name} {f?.Name} {f?.ActualWidth}x{f?.ActualHeight} vis {f?.Visibility}" +
                                            (o is Microsoft.UI.Xaml.Controls.ItemsControl ic ? $" items {ic.Items.Count} source {ic.ItemsSource?.GetType().Name}" : "") +
                                            (o is Microsoft.UI.Xaml.Controls.ItemsRepeater r ? $" repeater source {r.ItemsSource?.GetType().FullName} count {r.ItemsSourceView?.Count} template {r.ItemTemplate?.GetType().Name} layout {r.Layout?.GetType().Name}" : ""));
                                    if (level > 14) return;
                                    int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o);
                                    for (int i = 0; i < n; i++) Dump(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i), level + 1);
                                }
                                if (ex.IsExpanded) Dump(ex, 0);
                            });
                            ex.Expanding += (s, a) => Ccw.Log($"expander {ex.Name} Expanding");
                        }
                        else if (d is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb && watchedPopups.Add(tb))
                        {
                            Ccw.Log($"toggle button {tb.Name} checked {tb.IsChecked} enabled {tb.IsEnabled} mode {tb.ClickMode} {tb.ActualWidth}x{tb.ActualHeight}");
                            tb.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton.IsCheckedProperty, (s, p) => Ccw.Log($"toggle button {tb.Name} IsChecked -> {tb.IsChecked}"));
                            tb.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase.IsPressedProperty, (s, p) => Ccw.Log($"toggle button {tb.Name} IsPressed -> {tb.IsPressed}"));
                            tb.Click += (s, a) => Ccw.Log($"toggle button {tb.Name} Click");
                            tb.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase.IsPointerOverProperty, (s, p) => Ccw.Log($"toggle button {tb.Name} IsPointerOver -> {tb.IsPointerOver}"));
                            tb.AddHandler(Microsoft.UI.Xaml.UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) => Ccw.Log($"toggle button {tb.Name} PointerEntered over {tb.IsPointerOver}")), true);
                            tb.AddHandler(Microsoft.UI.Xaml.UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) => Ccw.Log($"toggle button {tb.Name} PointerExited")), true);
                            tb.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) => Ccw.Log($"toggle button {tb.Name} PointerPressed source {a.OriginalSource?.GetType().Name} handled {a.Handled} pressed {tb.IsPressed} over {tb.IsPointerOver} enabled {tb.IsEnabled} hit {tb.IsHitTestVisible}")), true);
                            tb.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) => Ccw.Log($"toggle button {tb.Name} PointerReleased handled {a.Handled} pressed {tb.IsPressed} over {tb.IsPointerOver}")), true);
                            /* where on the way up from the source the event turns handled */
                            void Chain(Microsoft.UI.Xaml.DependencyObject e, int level)
                            {
                                if (e is Microsoft.UI.Xaml.UIElement u)
                                    u.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) =>
                                        Ccw.Log($"  press passes {u.GetType().Name} {(u as Microsoft.UI.Xaml.FrameworkElement)?.Name}: handled {a.Handled}")), true);
                                if (level > 12) return;
                                int cc = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(e);
                                for (int i = 0; i < cc; i++) Chain(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(e, i), level + 1);
                            }
                            Chain(tb, 0);
                            tb.AddHandler(Microsoft.UI.Xaml.UIElement.PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) => Ccw.Log($"toggle button {tb.Name} PointerCanceled")), true);
                            tb.AddHandler(Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, a) => Ccw.Log($"toggle button {tb.Name} PointerCaptureLost")), true);
                        }
                        if (depth > 60) return;
                        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
                        for (int i = 0; i < count; i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i), depth + 1);
                    }
                    foreach (var pair in hitPoints.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var xy = pair.Split(',');
                        if (xy.Length != 2 || !double.TryParse(xy[0], out var px) || !double.TryParse(xy[1], out var py)) continue;
                        var inside = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(px, py), pc).ToList();
                        Ccw.Log($"popup hit test at {px},{py}: {inside.Count} elements: " + string.Join(" > ", inside.Take(8).Select(x => x.GetType().Name + " " + (x as Microsoft.UI.Xaml.FrameworkElement)?.Name)));
                    }
                }
            if (!string.IsNullOrEmpty(hitPoints) && Microsoft.UI.Xaml.Window.Current?.Content is { } root)
                foreach (var pair in hitPoints.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var xy = pair.Split(',');
                    if (xy.Length != 2 || !double.TryParse(xy[0], out var hx) || !double.TryParse(xy[1], out var hy)) continue;
                    try
                    {
                        var found = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(hx, hy), root).ToList();
                        Ccw.Log($"hit test at {hx},{hy}: {found.Count} elements");
                        foreach (var element in found.Take(6))
                        {
                            var chain = new StringBuilder();
                            Microsoft.UI.Xaml.DependencyObject? o = element;
                            for (int depth = 0; o != null && depth < 40; depth++, o = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(o))
                                if (o is Microsoft.UI.Xaml.UIElement ue && (!ue.IsHitTestVisible || ue.Visibility != Microsoft.UI.Xaml.Visibility.Visible || ue.Opacity == 0))
                                    chain.Append($" [{o.GetType().Name} {(o as Microsoft.UI.Xaml.FrameworkElement)?.Name} hit {ue.IsHitTestVisible} vis {ue.Visibility} opacity {ue.Opacity}]");
                            Ccw.Log($"  {element.GetType().Name} {(element as Microsoft.UI.Xaml.FrameworkElement)?.Name} actual {(element as Microsoft.UI.Xaml.FrameworkElement)?.ActualWidth}x{(element as Microsoft.UI.Xaml.FrameworkElement)?.ActualHeight}{chain}");
                        }
                    }
                    catch (Exception ex) { Ccw.Log($"hit test at {hx},{hy}: {ex.GetType().Name} {ex.Message}"); }
                    /* below the deepest hit: its children, then the first child's, ... (why the hit test stops there) */
                    if (hx == 150 && hy == 160)
                    {
                        /* the topmost child whose layout bounds hold the point, level by level */
                        Microsoft.UI.Xaml.DependencyObject? o = root;
                        for (int depth = 0; o != null && depth < 40; depth++)
                        {
                            Microsoft.UI.Xaml.DependencyObject? next = null;
                            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o);
                            for (int i = n - 1; i >= 0 && next == null; i--)
                            {
                                var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i);
                                if (c is not Microsoft.UI.Xaml.FrameworkElement fe || fe.Visibility != Microsoft.UI.Xaml.Visibility.Visible) continue;
                                try
                                {
                                    var rect = fe.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                                    if (rect.Contains(new Windows.Foundation.Point(hx, hy))) next = c;
                                }
                                catch { }
                            }
                            if (next == null) { Ccw.Log($"  layout path ends at {o.GetType().Name} ({n} children)"); break; }
                            var f = (Microsoft.UI.Xaml.FrameworkElement)next;
                            Ccw.Log($"  layout path {depth}: {next.GetType().Name} {f.Name} {f.ActualWidth}x{f.ActualHeight} hit {f.IsHitTestVisible} op {f.Opacity} rt {f.RenderTransform?.GetType().Name} clip {f.Clip != null}" +
                                    (f is Microsoft.UI.Xaml.Controls.Panel pn ? $" background {(pn.Background == null ? "null" : pn.Background.GetType().Name)}" : ""));
                            o = next;
                        }
                    }
                }
            if (DrawCheck() >= (int.TryParse(Environment.GetEnvironmentVariable("XAMLSHIM_CHECKS"), out var maxChecks) ? maxChecks : 8)) timer.Stop();
        };
        timer.Start();
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, byte* info, uint usage, void** bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern int BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern int DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int DeleteDC(IntPtr dc);

    /* the window's pixels as a 32-bit top-down BMP file */
    static void SaveBitmap(IntPtr dc, int w, int h, string path)
    {
        if (w <= 0 || h <= 0) return;
        var info = new byte[40];
        BitConverter.TryWriteBytes(info.AsSpan(0), 40);
        BitConverter.TryWriteBytes(info.AsSpan(4), w);
        BitConverter.TryWriteBytes(info.AsSpan(8), -h);
        BitConverter.TryWriteBytes(info.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(info.AsSpan(14), (short)32);
        void* bits;
        IntPtr mem = CreateCompatibleDC(dc), dib;
        fixed (byte* p = info) dib = CreateDIBSection(dc, p, 0, &bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { _ = DeleteDC(mem); return; }
        var old = SelectObject(mem, dib);
        _ = BitBlt(mem, 0, 0, w, h, dc, 0, 0, 0x00CC0020 /* SRCCOPY */);
        var size = w * h * 4;
        using (var f = File.Create(path))
        {
            var header = new byte[14];
            header[0] = (byte)'B'; header[1] = (byte)'M';
            BitConverter.TryWriteBytes(header.AsSpan(2), 54 + size);
            BitConverter.TryWriteBytes(header.AsSpan(10), 54);
            f.Write(header);
            f.Write(info);
            f.Write(new ReadOnlySpan<byte>(bits, size));
        }
        _ = SelectObject(mem, old);
        _ = DeleteObject(dib);
        _ = DeleteDC(mem);
        Ccw.Log($"draw check: window pixels saved to {path}");
    }

    static int drawChecks;

    [DllImport("user32.dll")] static extern int GetWindowRect(IntPtr hwnd, int* rect);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);

    static string Rects()
    {
        int* a = stackalloc int[4];
        int* b = stackalloc int[4];
        _ = GetWindowRect(child, a);
        _ = GetWindowRect(FrameworkView.CoreHwnd, b);
        return $"window rects (screen): Uno {a[0]},{a[1]},{a[2]},{a[3]} CoreWindow {b[0]},{b[1]},{b[2]},{b[3]}; " +
               $"parent of Uno {GetParent(child):x} (CoreWindow {FrameworkView.CoreHwnd:x}), style {(long)GetWindowLongPtrW(child, GWL_STYLE):x}";
    }

    [DllImport("user32.dll")] static extern int ClientToScreen(IntPtr hwnd, int* point);
    [DllImport("user32.dll")] static extern int ScreenToClient(IntPtr hwnd, int* point);

    /* where a window's client origin is on the screen, and what a screen point (100,100) is in its client area */
    static string Origin(IntPtr hwnd)
    {
        int* p = stackalloc int[2];
        p[0] = 0; p[1] = 0;
        _ = ClientToScreen(hwnd, p);
        var origin = $"{p[0]},{p[1]}";
        p[0] = 100; p[1] = 100;
        _ = ScreenToClient(hwnd, p);
        return $"{origin} (screen 100,100 is {p[0]},{p[1]})";
    }

    /* the first text of the visual tree named NormalOutput (the Calculator's display) */
    static string DisplayText()
    {
        string? found = null;
        void Walk(Microsoft.UI.Xaml.DependencyObject o)
        {
            if (found != null) return;
            if (o is Microsoft.UI.Xaml.Controls.TextBlock { Name: "NormalOutput" } tb) { found = tb.Text; return; }
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i));
        }
        if (Microsoft.UI.Xaml.Window.Current?.Content is { } content) Walk(content);
        return found ?? "(none)";
    }

    /* runs every 8 s, the window's pixels saved as xamlshim-window-N.bmp and (the latest) xamlshim-window.bmp; the
     * number of checks so far */
    static int DrawCheck()
    {
        var n = ++drawChecks;
        int* r = stackalloc int[4];
        _ = GetClientRect(FrameworkView.CoreHwnd, r);
        var core = $"{r[2] - r[0]}x{r[3] - r[1]}";
        _ = GetClientRect(child, r);
        int w = r[2] - r[0], h = r[3] - r[1];
        var counts = new Dictionary<uint, int>();
        var dc = GetDC(child);
        for (int y = 0; y < h; y += Math.Max(1, h / 20))
            for (int x = 0; x < w; x += Math.Max(1, w / 20))
            {
                var p = GetPixel(dc, x, y);
                counts[p] = counts.GetValueOrDefault(p) + 1;
            }
        SaveBitmap(dc, w, h, Path.Combine(Path.GetTempPath(), $"xamlshim-window-{n}.bmp"));
        SaveBitmap(dc, w, h, Path.Combine(Path.GetTempPath(), "xamlshim-window.bmp"));
        _ = ReleaseDC(child, dc);
        var colors = string.Join(", ", counts.OrderByDescending(c => c.Value).Take(6).Select(c => $"{c.Key:x6} x{c.Value}"));
        var content = Microsoft.UI.Xaml.Window.Current?.Content as Microsoft.UI.Xaml.FrameworkElement;
        var root = content?.XamlRoot;
        Ccw.Log($"draw check {n}: CoreWindow client {core}, Uno window {w}x{h} visible {IsWindowVisible(child)}; content " +
                $"{content?.GetType().Name ?? "null"} {content?.ActualWidth}x{content?.ActualHeight} visibility {content?.Visibility}; " +
                $"XamlRoot {root?.Size.Width}x{root?.Size.Height}; display '{DisplayText()}'; origins: Uno window {Origin(child)}, " +
                $"CoreWindow {Origin(FrameworkView.CoreHwnd)}; {Rects()}; pixels {colors}");
        return n;
    }

    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);

    static bool coreLoop;

    /* The CoreWindow raises its events (visibility, hence suspend and resume; activation; size) from its dispatcher's
     * ProcessEvents, which Windows' XAML uses as its message loop. Uno's host runs a GetMessage loop of its own; its
     * dispatcher only posts messages to a window of its own, which any loop serves. So once the application has
     * launched, a Uno action enters CoreDispatcher.ProcessEvents(ProcessUntilQuit): from then on Wine's loop dispatches
     * every message of the thread (Uno's included) and wakes for the CoreWindow's events. Application.Exit posts WM_QUIT,
     * which ends that loop; Uno's loop then finds its windows closed and returns. */
    public static void EnterCoreDispatcherLoop()
    {
        if (FrameworkView.CoreWindow == IntPtr.Zero) return;
        IntPtr found;
        if (UI_Core_ICoreWindow_Call.get_Dispatcher(FrameworkView.CoreWindow, &found) < 0) found = IntPtr.Zero;
        var dispatcher = found;
        if (dispatcher == IntPtr.Zero)
        {
            Ccw.Log("the CoreWindow has no dispatcher");
            return;
        }
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            const int ProcessUntilQuit = 2;
            coreLoop = true;
            Ccw.Log("CoreDispatcher.ProcessEvents(ProcessUntilQuit) runs the message loop");
            int hr = UI_Core_ICoreDispatcher_Call.ProcessEvents(dispatcher, ProcessUntilQuit);
            coreLoop = false;
            Ccw.Log($"CoreDispatcher loop returned {hr:x8}");
        });
    }

    /* Application.Exit: end the CoreDispatcher loop as well */
    public static void QuitCoreDispatcherLoop()
    {
        if (coreLoop) PostQuitMessage(0);
    }

    /* the CoreWindow's client size, or null without a CoreWindow */
    public static (int Width, int Height)? ClientSize()
    {
        if (FrameworkView.CoreHwnd == IntPtr.Zero) return null;
        int* r = stackalloc int[4];
        if (GetClientRect(FrameworkView.CoreHwnd, r) == 0) return null;
        return (r[2] - r[0], r[3] - r[1]);
    }

    static void Fit()
    {
        int* r = stackalloc int[4];
        _ = GetClientRect(FrameworkView.CoreHwnd, r);
        _ = MoveWindow(child, 0, 0, r[2] - r[0], r[3] - r[1], 1);
        if (Ccw.Tracing) Ccw.Log($"Fit: client {r[0]},{r[1]},{r[2]},{r[3]}; {Rects()}; origin {Origin(child)}");
    }

    static IntPtr childProc;
    const uint WM_WINDOWPOSCHANGING = 0x0046;

    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    /* Keyboard input goes to the focused window, Uno's, but an application's shortcuts are CoreWindow.KeyDown,
     * KeyUp and CharacterReceived (the Calculator's keys). Key messages are sent to the CoreWindow first, which raises
     * them (Wine's windows.ui); an application that marks the event handled keeps it from Uno's tree, as on Windows. */
    static bool keyHandled, keyHooked;

    static void HookKeyEvents()
    {
        if (keyHooked || FrameworkView.CoreWindow == IntPtr.Zero) return;
        keyHooked = true;
        /* added after the application's own handlers (it added them when it started), so that they have run */
        var keyHandler = new Guid("a3ec0774-55ac-5d61-8232-b35c5d35c93c");         /* TypedEventHandler<CoreWindow, KeyEventArgs> */
        var characterHandler = new Guid("73f846a8-0af6-5872-8fb8-ae2f56d8553e");   /* ... <CoreWindow, CharacterReceivedEventArgs> */
        for (int which = 0; which < 3; which++)
        {
            var index = which;
            var iid = which == 2 ? characterHandler : keyHandler;
            var observer = new NativeDelegate(iid, (_, args) =>
            {
                if (args == IntPtr.Zero || Ccw.QueryInterface(args, Iids.UI_Core_ICoreWindowEventArgs, out var e) < 0) return;
                byte handled;
                if (UI_Core_ICoreWindowEventArgs_Call.get_Handled(e, &handled) >= 0 && handled != 0) keyHandled = true;
                if (Ccw.Tracing) Ccw.Log($"CoreWindow key event {index}: handled {handled}");
                Ccw.ComRelease(e);
            });
            var r = Ccw.Get(observer, iid);
            long token;
            _ = which switch
            {
                0 => UI_Core_ICoreWindow_Call.add_KeyDown(FrameworkView.CoreWindow, r, &token),
                1 => UI_Core_ICoreWindow_Call.add_KeyUp(FrameworkView.CoreWindow, r, &token),
                _ => UI_Core_ICoreWindow_Call.add_CharacterReceived(FrameworkView.CoreWindow, r, &token),
            };
            Ccw.ComRelease(r);
        }
    }

    static bool IsKeyMessage(uint msg) => msg is 0x0100 or 0x0101 or 0x0104 or 0x0105;   /* WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP */
    static int pendingCharacters;
    [DllImport("user32.dll")] static extern int PeekMessageW(byte* msg, IntPtr hwnd, uint min, uint max, uint remove);   /* WM_KEYDOWN..WM_CHAR, WM_SYSKEYDOWN..WM_SYSCHAR */

    /* Uno's window keeps its own idea of where it is (its position as a top-level window, taken as a position in the
     * CoreWindow): every move or resize it asks for is made the whole client area of the CoreWindow */
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static IntPtr ChildProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (IsKeyMessage(msg) && FrameworkView.CoreHwnd != IntPtr.Zero)
        {
            HookKeyEvents();
            keyHandled = false;
            SendMessageW(FrameworkView.CoreHwnd, msg, w, l);
            if (Ccw.Tracing) Ccw.Log($"key message {msg:x} {(long)w:x} sent to the CoreWindow: handled {keyHandled}");
            if (keyHandled) return IntPtr.Zero;
            if (msg is 0x0100 or 0x0104)
            {
                /* TranslateMessage has posted the character of this key before the key message was dispatched; Uno takes
                 * it from the queue itself, so the CoreWindow gets it now (CharacterReceived: the Calculator's digits) */
                var m = stackalloc byte[48];
                if (PeekMessageW(m, hwnd, 0x0102, 0x0102, 0) != 0)
                {
                    pendingCharacters++;
                    SendMessageW(FrameworkView.CoreHwnd, 0x0102, *(IntPtr*)(m + 16), *(IntPtr*)(m + 24));
                    if (Ccw.Tracing) Ccw.Log($"character {(long)*(IntPtr*)(m + 16):x} sent to the CoreWindow");
                }
            }
        }
        else if (msg is 0x0102 or 0x0106 && FrameworkView.CoreHwnd != IntPtr.Zero)
        {
            /* a character that reached this window: the CoreWindow's, unless it was sent with its key */
            if (pendingCharacters > 0) pendingCharacters--;
            else SendMessageW(FrameworkView.CoreHwnd, msg, w, l);
        }
        if (msg == WM_WINDOWPOSCHANGING && l != IntPtr.Zero)
        {
            /* WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags */
            var wp = (int*)((byte*)l + 2 * sizeof(IntPtr));
            int* r = stackalloc int[4];
            if (GetClientRect(FrameworkView.CoreHwnd, r) != 0)
            {
                wp[0] = 0; wp[1] = 0; wp[2] = r[2] - r[0]; wp[3] = r[3] - r[1];
                wp[4] &= ~(int)(SWP_NOMOVE | SWP_NOSIZE);
            }
        }
        return CallWindowProcW(childProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static IntPtr CoreProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        var result = CallWindowProcW(oldProc, hwnd, msg, w, l);
        if (msg == WM_SIZE && child != IntPtr.Zero) Fit();
        if (msg == WM_SETFOCUS && child != IntPtr.Zero) SetFocus(child);
        return result;
    }
}
