// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The generated default event accessors: add_X subscribes a delegate to the Uno object's event of the same name that
// invokes the application's handler (a WinRT delegate: Invoke is slot 3) with the sender and the arguments as
// IInspectable (their shims, or boxed values); remove_X takes it off again and releases the handler.
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace XamlShim.Abi;

public static class Events
{
    sealed record Subscription(object Target, EventInfo Event, Delegate Handler, IntPtr Native);

    static readonly ConcurrentDictionary<long, Subscription> subscriptions = new();
    static readonly MethodInfo raise = typeof(Events).GetMethod(nameof(Raise), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static unsafe int Add(object shim, string name, IntPtr handler, long* token)
    {
        var target = ((ShimObject)shim).UnoObject;
        var info = target?.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
        if (target == null || info?.EventHandlerType is not { } type)
        {
            Ccw.NotImplemented($"{((ShimObject)shim).RuntimeClassName}.{name} event (no Uno event)");
            *token = 0;
            return unchecked((int)0x80004001);
        }
        var parameters = type.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        if (parameters.Length != 2)
        {
            Ccw.NotImplemented($"{((ShimObject)shim).RuntimeClassName}.{name} event ({parameters.Length} parameters)");
            *token = 0;
            return unchecked((int)0x80004001);
        }
        /* (sender, args) => Events.Raise(handler, shim, sender, args) */
        var body = Expression.Call(raise, Expression.Constant(handler), Expression.Constant(shim, typeof(ShimObject)), Expression.Constant(name),
                                   Expression.Convert(parameters[0], typeof(object)), Expression.Convert(parameters[1], typeof(object)));
        var callback = Expression.Lambda(type, body, parameters).Compile();
        Ccw.ComAddRef(handler);
        info.AddEventHandler(target, callback);
        *token = Shim.Token();
        subscriptions[*token] = new Subscription(target, info, callback, handler);
        return 0;
    }

    public static int Remove(object shim, string name, long token)
    {
        if (subscriptions.TryRemove(token, out var s))
        {
            s.Event.RemoveEventHandler(s.Target, s.Handler);
            Ccw.ComRelease(s.Native);
        }
        return 0;
    }

    /* the sender is the element's shim (its outer object when the application derived from it) */
    static void Raise(IntPtr handler, ShimObject owner, string name, object? sender, object? args)
    {
        IntPtr s = IntPtr.Zero, a = IntPtr.Zero;
        try
        {
            s = sender == owner.UnoObject ? Ccw.Get(owner) : Boxing.Box(sender);
            a = Boxing.Box(args);
            int hr = Shim.Invoke(handler, s, a);
            if (Ccw.Calls) Ccw.Log($"raised {owner.RuntimeClassName} {(owner.UnoObject as Microsoft.UI.Xaml.FrameworkElement)?.Name}.{name}: {hr:x8}");
            if (hr < 0)
            {
                Ccw.Log($"event handler returned {hr:x8} ({owner.RuntimeClassName} {(owner.UnoObject as Microsoft.UI.Xaml.FrameworkElement)?.Name}.{name})");
                if (args is Microsoft.UI.Xaml.DataContextChangedEventArgs dc)
                {
                    int q = Ccw.QueryInterface(s, Iids.UI_Xaml_IFrameworkElement, out var fe);
                    if (q >= 0) Ccw.ComRelease(fe);
                    Ccw.Log($"  sender {s:x} args {a:x} QI(IFrameworkElement) {q:x8}");
                }
                if (args is Microsoft.UI.Xaml.DataContextChangedEventArgs dcn && dcn.NewValue is NativeObject nobj)
                    unsafe
                    {
                        IntPtr h = IntPtr.Zero;
                        int hn = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)nobj.Pointer)[4])(nobj.Pointer, &h);
                        Ccw.Log($"  NewValue pointer {nobj.Pointer:x} class {(hn >= 0 ? HString.Read(h) : hn.ToString("x8", System.Globalization.CultureInfo.InvariantCulture))}");
                    }
                if (args is Microsoft.UI.Xaml.DataContextChangedEventArgs dc2)
                    Ccw.Log($"  DataContext of {sender?.GetType().Name} {(sender as Microsoft.UI.Xaml.FrameworkElement)?.Name}: new {dc2.NewValue?.GetType().FullName ?? "null"} {dc2.NewValue}");
            }
        }
        catch (Exception e)
        {
            Ccw.Log($"event raise failed: {e}");
        }
        finally
        {
            if (s != IntPtr.Zero) Ccw.ComRelease(s);
            if (a != IntPtr.Zero) Ccw.ComRelease(a);
        }
    }
}
