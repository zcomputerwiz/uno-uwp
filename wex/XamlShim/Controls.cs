// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// Members of the common controls (the probe's ControlsPage): panels' Children, ItemsControl.Items, TextBox.Text,
// ToggleButton.IsChecked, ScrollViewer's extent, viewport, offset and ChangeView.
using Mux = Microsoft.UI.Xaml;
using UnoControls = Microsoft.UI.Xaml.Controls;

namespace XamlShim.Abi;

public unsafe partial class UI_Xaml_Controls_Panel
{
    VectorShim? children;

    int IUI_Xaml_Controls_IPanel_Abi.get_Children(IntPtr* result)
    {
        children ??= new VectorShim(new AbiList<Mux.UIElement>(((UnoControls.Panel)UnoObject!).Children), true, "Windows.UI.Xaml.Controls.UIElementCollection");
        *result = Ccw.GetDefault(children);
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_ItemsControl
{
    VectorShim? items;

    int IUI_Xaml_Controls_IItemsControl_Abi.get_Items(IntPtr* result)
    {
        items ??= new VectorShim(new AbiList<object>(((UnoControls.ItemsControl)UnoObject!).Items), false, "Windows.UI.Xaml.Controls.ItemCollection");
        *result = Ccw.GetDefault(items);
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_TextBox
{
    int IUI_Xaml_Controls_ITextBox_Abi.get_Text(IntPtr* result)
    {
        *result = HString.Create(((UnoControls.TextBox)UnoObject!).Text ?? "");
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_Primitives_ToggleButton
{
    /* IReference<bool>: Wine's boxed Boolean */
    int IUI_Xaml_Controls_Primitives_IToggleButton_Abi.get_IsChecked(IntPtr* result)
    {
        var value = ((UnoControls.Primitives.ToggleButton)UnoObject!).IsChecked;
        *result = value is bool b ? Boxing.Box(b) : IntPtr.Zero;
        return 0;
    }
}

public unsafe partial class UI_Xaml_Controls_ScrollViewer
{
    UnoControls.ScrollViewer Viewer => (UnoControls.ScrollViewer)UnoObject!;

    int IUI_Xaml_Controls_IScrollViewer_Abi.get_ExtentHeight(double* result) { *result = Viewer.ExtentHeight; return 0; }
    int IUI_Xaml_Controls_IScrollViewer_Abi.get_ViewportHeight(double* result) { *result = Viewer.ViewportHeight; return 0; }
    int IUI_Xaml_Controls_IScrollViewer_Abi.get_VerticalOffset(double* result) { *result = Viewer.VerticalOffset; return 0; }

    int IUI_Xaml_Controls_IScrollViewer2_Abi.ChangeView(IntPtr horizontal, IntPtr vertical, IntPtr zoom, byte* result)
    {
        *result = (byte)(Viewer.ChangeView(Boxing.UnboxDouble(horizontal), Boxing.UnboxDouble(vertical),
                                           (float?)Boxing.UnboxDouble(zoom)) ? 1 : 0);
        return 0;
    }

    int IUI_Xaml_Controls_IScrollViewer2_Abi.ChangeViewWithOptionalAnimation(IntPtr horizontal, IntPtr vertical, IntPtr zoom,
                                                                             byte disableAnimation, byte* result)
    {
        *result = (byte)(Viewer.ChangeView(Boxing.UnboxDouble(horizontal), Boxing.UnboxDouble(vertical),
                                           (float?)Boxing.UnboxDouble(zoom), disableAnimation != 0) ? 1 : 0);
        return 0;
    }
}

/* CalendarDatePicker's dates are Windows.Foundation.DateTime (100 ns ticks since 1601, UTC) here, DateTimeOffset in Uno */
public unsafe partial class UI_Xaml_Controls_CalendarDatePicker
{
    static DateTimeOffset ToDate(long ticks) => new(DateTime.FromFileTimeUtc(ticks), TimeSpan.Zero);
    static long FromDate(DateTimeOffset date) => date.UtcDateTime.ToFileTimeUtc();

    Microsoft.UI.Xaml.Controls.CalendarDatePicker Picker => (Microsoft.UI.Xaml.Controls.CalendarDatePicker)UnoObject!;

    int IUI_Xaml_Controls_ICalendarDatePicker_Abi.get_MinDate(long* result) { *result = FromDate(Picker.MinDate); return 0; }
    int IUI_Xaml_Controls_ICalendarDatePicker_Abi.put_MinDate(long value) { Picker.MinDate = ToDate(value); return 0; }
    int IUI_Xaml_Controls_ICalendarDatePicker_Abi.get_MaxDate(long* result) { *result = FromDate(Picker.MaxDate); return 0; }
    int IUI_Xaml_Controls_ICalendarDatePicker_Abi.put_MaxDate(long value) { Picker.MaxDate = ToDate(value); return 0; }
}
