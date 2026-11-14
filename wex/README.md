<!--
SPDX-License-Identifier: Apache-2.0
Copyright 2026 WineExplorer contributors
-->
# wex: Windows.UI.Xaml on Uno for WineExplorer

This folder is WineExplorer's addition to this fork of the Uno Platform. It lets UWP applications compiled against
the system XAML (`Windows.UI.Xaml`, C++/CX, C++/WinRT, .NET Native), which were never built against Uno, run on Wine
with Uno doing the XAML work. Licence: Apache-2.0, like the rest of this repository (`../License.md`).

WineExplorer (GPL-2.0-or-later, github.com/zcomputerwiz/WineExplorer) supplies the native side: its
`windows.ui.xaml.dll` (for now the experiment's `xamlproxy.dll`) starts the .NET runtime in the application's process
with `hostfxr` and asks `XamlShim.Activation.GetActivationFactory` for each `Windows.UI.Xaml` class. WineExplorer's
setup shows this component's terms and downloads it; it is never shipped inside WineExplorer.

| Folder | What |
|---|---|
| `WinmdGen/` | Reads `Windows.winmd` from the Windows SDK and writes `XamlShim/Abi.g.cs`: IIDs, one ABI interface per WinRT interface whose methods log "not implemented" unless implemented, vtables of `[UnmanagedCallersOnly]` thunks, callers for native objects, blittable structs, one partial class per runtime class (mirroring the class hierarchy) and per activation factory. Classes: `WinmdGen/classes.txt`. |
| `XamlShim/` | The component: native-callable wrappers with COM aggregation (`Ccw.cs`), activation (`Activation.cs`), the implemented members over Uno (`Wux.cs`), `Application.LoadComponent` for compiled XAML (`XbfLoader.cs`, with XbfKit). |

Build (Windows, .NET 10 SDK, Windows SDK 10.0.26100):

    .\generate.ps1
    cd XamlShim
    dotnet publish -c Release -f net10.0-desktop -r win-x64 --self-contained -o <component directory>

For now the projects build against the released `Uno.Sdk` (`XamlShim/global.json`); changes to Uno itself, when
needed, go to `../src` and the projects then build against it.

Third-party: XbfKit 0.4.0 (MIT, github.com/0x5bfa/XbfKit), referenced as a NuGet package; PriFormat (Apache-2.0,
from github.com/chausner/PriTools, `PriFormat/README.md`), copied unchanged; `XamlShim/Themes/UwpGeneric.xaml`, the
UWP system styles from microsoft-ui-xaml's `generic.xaml` (MIT, `Themes/WinUI-LICENSE.txt`; adapted, see its header);
Selawik 1.01 (OFL-1.1, `XamlShim/Assets/Fonts/Selawik-OFL.txt`), the open metric-compatible stand-in for Segoe UI.
`release/` builds a release: `make-release-zip.ps1` (after `dotnet publish`), `collect_notices.py` (NOTICE.txt from
the build's packages plus `release/extra-licenses`), `zipdir.py`, and `adapt_generic.py`, which regenerates
`UwpGeneric.xaml` from upstream's `generic.xaml`.

Status (2026-11-14): an unmodified compiled C++/CX XAML test application (WineExplorer `tests/uwp/xaml/cx-probe`)
runs on Wine x64 through this component: start, navigation to its compiled page, layout, `Loaded`, `Click` wiring,
`DispatcherTimer`, `Exit`. Not done: XBF from `resources.pri`, real object lifetimes, UWP default styles and metrics,
the window size, x86, most members (each logs "not implemented" when called).
