// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
namespace XamlShim;

/* XamlShim is not run as a program: Wine's windows.ui.xaml.dll starts the runtime with hostfxr and calls Activation.
 * "XamlShim.exe --classes FILE" writes the runtime classes the component serves, one per line, for the setup to
 * register (a file: the program is built for the GUI subsystem and has no console). */
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--classes")
        {
            /* only the XAML namespace: other generated classes (activation arguments) belong to Wine */
            File.WriteAllLines(args[1], XamlShim.Abi.FactoryTable.Create.Keys
                .Where(n => n.StartsWith("Windows.UI.Xaml.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            return 0;
        }
        return 2;
    }
}
