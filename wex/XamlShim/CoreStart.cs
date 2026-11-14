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
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
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
        SetParent(child, core);
        Fit();
        oldProc = SetWindowLongPtrW(core, GWLP_WNDPROC, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&CoreProc);
        _ = ShowWindow(child, 5);
        SetFocus(child);
        int hr = UI_Core_ICoreWindow_Call.Activate(FrameworkView.CoreWindow);
        Ccw.Log($"Uno window {child:x} placed in the CoreWindow {core:x} ({found.Count} candidates), CoreWindow.Activate {hr:x8}");
        return true;
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
