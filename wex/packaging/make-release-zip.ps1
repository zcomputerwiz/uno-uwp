# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
# Package the published XamlShim (win-x64) as the release zip: the component without debug symbols, classes.txt,
# LICENSE.txt (Apache-2.0), NOTICE.txt, README.txt. usage: make-release-zip.ps1 VERSION COMMIT
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Commit)
$ErrorActionPreference = 'Stop'
$rel = Join-Path $env:TEMP 'claude\xamlshim-release'
$stage = Join-Path $rel 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$dir = Join-Path $stage 'XamlShim'
New-Item -ItemType Directory -Path $dir | Out-Null
Copy-Item (Join-Path $rel 'XamlShim\*') $dir -Recurse
Get-ChildItem $dir -Recurse -Filter *.pdb | Remove-Item
# the classes the component serves, written by the component itself
$list = Join-Path $dir 'classes.txt'
$p = Start-Process -FilePath (Join-Path $dir 'XamlShim.exe') -ArgumentList '--classes', "`"$list`"" -Wait -PassThru
if ($p.ExitCode -ne 0 -or -not (Test-Path $list)) { throw 'XamlShim --classes failed' }
Copy-Item 'D:\DeepThought\Projects\uno-uwp\License.md' (Join-Path $dir 'LICENSE.txt')
Copy-Item (Join-Path $rel 'NOTICE.txt') (Join-Path $dir 'NOTICE.txt')
@(
    "XamlShim $Version (win-x64): Windows.UI.Xaml on the Uno Platform, for UWP applications on Wine (WineExplorer).",
    '',
    "Source: https://github.com/zcomputerwiz/uno-uwp, branch wex/xamlshim, commit $Commit, folder wex/",
    'Licence: Apache License 2.0 (LICENSE.txt); third-party software and its licences: NOTICE.txt.',
    '',
    "Wine's windows.ui.xaml.dll loads this directory when HKLM\Software\Wine\Xaml ComponentPath names it, for the",
    'classes in classes.txt registered to windows.ui.xaml.dll. WineExplorer setup installs and registers it.'
) | Set-Content -Path (Join-Path $dir 'README.txt') -Encoding utf8
$zip = Join-Path $rel "xamlshim-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
# standard '/' entry names (Compress-Archive of Windows PowerShell 5.1 writes '\')
python -I (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'zipdir.py') $dir $zip
if ($LASTEXITCODE -ne 0) { throw 'zip failed' }
$item = Get-Item $zip
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$($item.Name) $($item.Length) $sha"
"classes: $((Get-Content $list).Count), files: $((Get-ChildItem $dir -Recurse -File).Count)"
