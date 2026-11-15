// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// <StaticResource x:Key="A" ResourceKey="B"/> in a resource dictionary inside a page's content: Uno's XAML reader
// knows no StaticResource type, so XbfLoader.AliasResources renames the element to this markup extension (which Uno's
// reader takes in element form), and the entry A gets the value of the resource B from the application's resources.
using Mux = Microsoft.UI.Xaml;

namespace XamlShim.Abi;

public sealed class ResourceAlias : Mux.Markup.MarkupExtension
{
    public string? ResourceKey { get; set; }

    protected override object? ProvideValue()
    {
        if (ResourceKey != null && Mux.Application.Current?.Resources is { } resources && resources.TryGetValue(ResourceKey, out var value))
            return value;
        Ccw.Log($"resource alias {ResourceKey}: not found in the application's resources");
        return null;
    }
}
