// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// Types the application's compiled XAML uses by name that the application's own metadata does not describe: helpers of a
// control library's default styles, which the library's own XAML names. The XAML reader finds them by their CLR name.
using Mux = Microsoft.UI.Xaml;

namespace CommunityToolkit.WinUI.Controls;

/* The converter of the settings expander's default style: the corner radius of the content area under the header, whose
 * top corners are square (the header above has the rounded ones). */
public sealed class CornerRadiusConverter : Mux.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is Mux.CornerRadius radius ? new Mux.CornerRadius(0, 0, radius.BottomRight, radius.BottomLeft) : new Mux.CornerRadius(0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/* The style of an item of a settings expander: the clickable one for a card that can be clicked, the default one otherwise. */
public sealed class SettingsExpanderItemStyleSelector : Mux.Controls.StyleSelector
{
    public Mux.Style? ClickableStyle { get; set; }
    public Mux.Style? DefaultStyle { get; set; }

    protected override Mux.Style? SelectStyleCore(object item, Mux.DependencyObject container) =>
        item?.GetType().GetProperty("IsClickEnabled")?.GetValue(item) is true ? ClickableStyle : DefaultStyle;
}
