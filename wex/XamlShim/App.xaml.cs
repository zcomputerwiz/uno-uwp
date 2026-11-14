// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
namespace XamlShim;

/* the Uno application; Windows.UI.Xaml.Application.Start runs it and is told when it has launched */
public partial class UnoHostApp : Microsoft.UI.Xaml.Application
{
    public static Microsoft.UI.Xaml.Window? MainWindow;
    public static Action? Launched;

    /* UWP's default text font is Segoe UI, which only Windows has; Selawik (Microsoft, OFL-1.1, Assets/Fonts) is its
     * metric-compatible open fallback */
    static UnoHostApp()
    {
        global::Uno.UI.FeatureConfiguration.Font.DefaultTextFontFamily = "ms-appx:///Assets/Fonts/selawk.ttf#Selawik";
    }

    public UnoHostApp()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindow = new Microsoft.UI.Xaml.Window();
        Launched?.Invoke();
    }
}
