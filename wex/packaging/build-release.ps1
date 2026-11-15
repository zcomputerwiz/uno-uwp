# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
# Build a XamlShim release (win-x64) from this checkout: generate the ABI, publish the component self-contained, and
# write into OUT_DIR the release assets
#   xamlshim-VERSION-win-x64.zip  the component (no debug symbols) with classes.txt, LICENSE.txt, NOTICE.txt, README.txt
#   TERMS.txt                     the Apache License 2.0 and the third-party licences, the text WineExplorer setup shows
#   LICENSE.txt, NOTICE.txt
#   SHA256SUMS, notes.md          hashes, and the release description (NOTES_FILE, if given, then sizes and hashes)
# usage: build-release.ps1 -Version 0.2.0 -OutDir DIR [-Commit SHA] [-NotesFile FILE] [-WinMd PATH]
# Used by .github/workflows/wex-xamlshim-release.yml; runs the same on a developer's machine (Windows, .NET 10 SDK,
# Windows SDK 10.0.26100, Python 3).
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Commit,
    [string]$NotesFile,
    [string]$WinMd
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version must be MAJOR.MINOR.PATCH: $Version" }
$wex = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $wex
if (-not $Commit) { $Commit = (git -C $repo rev-parse HEAD).Trim() }
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-Text([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), $utf8) }
function Read-Text([string]$Path) { [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Trim() }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$work = Join-Path $OutDir 'work'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$dir = Join-Path $work 'XamlShim'

# 1. the ABI from the Windows SDK metadata, then the component
if ($WinMd) { & (Join-Path $wex 'generate.ps1') -WinMd $WinMd } else { & (Join-Path $wex 'generate.ps1') }
dotnet publish (Join-Path $wex 'XamlShim') -c Release -f net10.0-desktop -r win-x64 --self-contained -o $dir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem $dir -Recurse -Filter *.pdb | Remove-Item

# 2. the classes the component serves, written by the component itself
$list = Join-Path $dir 'classes.txt'
$p = Start-Process -FilePath (Join-Path $dir 'XamlShim.exe') -ArgumentList '--classes', "`"$list`"" -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0 -or -not (Test-Path $list)) { throw 'XamlShim --classes failed' }

# 3. licences: Apache-2.0 for XamlShim, NOTICE.txt from the packages of this build
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$notice = Join-Path $OutDir 'NOTICE.txt'
python -I (Join-Path $PSScriptRoot 'collect_notices.py') (Join-Path $wex 'XamlShim\obj\project.assets.json') $nuget $notice
if ($LASTEXITCODE -ne 0) { throw 'collect_notices failed' }
$license = Join-Path $OutDir 'LICENSE.txt'
Write-Text $license ((Read-Text (Join-Path $repo 'License.md')) + "`n")
Copy-Item $license (Join-Path $dir 'LICENSE.txt')
Copy-Item $notice (Join-Path $dir 'NOTICE.txt')
Write-Text (Join-Path $dir 'README.txt') ((@(
    "XamlShim $Version (win-x64): Windows.UI.Xaml on the Uno Platform, for UWP applications on Wine (WineExplorer).",
    '',
    "Source: https://github.com/zcomputerwiz/uno-uwp, commit $Commit, folder wex/",
    'Licence: Apache License 2.0 (LICENSE.txt); third-party software and its licences: NOTICE.txt.',
    '',
    "Wine's windows.ui.xaml.dll loads this directory when HKLM\Software\Wine\Xaml ComponentPath names it, for the",
    'classes in classes.txt registered to windows.ui.xaml.dll. WineExplorer setup installs and registers it.'
) -join "`n") + "`n")
Write-Text (Join-Path $OutDir 'TERMS.txt') ((@(
    "Terms of XamlShim $Version (UWP XAML support for WineExplorer): the Apache License 2.0, under which XamlShim is distributed, followed by the licences of the third-party software it contains.",
    '',
    (Read-Text $license),
    '',
    (Read-Text $notice)
) -join "`n") + "`n")

# 4. the archive: standard '/' entry names, fixed timestamps
$zipName = "xamlshim-$Version-win-x64.zip"
$zip = Join-Path $OutDir $zipName
if (Test-Path $zip) { Remove-Item $zip }
python -I (Join-Path $PSScriptRoot 'zipdir.py') $dir $zip
if ($LASTEXITCODE -ne 0) { throw 'zip failed' }

# 5. hashes and the release description
$assets = @($zipName, 'TERMS.txt', 'LICENSE.txt', 'NOTICE.txt')
$sums = @()
$table = @('| File | Size (bytes) | SHA-256 |', '|---|---|---|')
foreach ($name in $assets) {
    $path = Join-Path $OutDir $name
    $sha = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$sha  $name"
    $table += "| $name | $((Get-Item $path).Length) | $sha |"
}
Write-Text (Join-Path $OutDir 'SHA256SUMS') (($sums -join "`n") + "`n")
$intro = if ($NotesFile -and (Test-Path $NotesFile) -and (Read-Text $NotesFile)) { Read-Text $NotesFile } else {
    "XamlShim $Version (win-x64): ``Windows.UI.Xaml`` on the Uno Platform, for UWP applications on Wine (WineExplorer)." }
Write-Text (Join-Path $OutDir 'notes.md') ((@(
    $intro,
    '',
    "Source: folder ``wex/`` at commit $Commit.",
    '',
    'Licence: Apache License 2.0 (`LICENSE.txt`); third-party software and its licences, including the .NET runtime,',
    'SkiaSharp, HarfBuzz and ICU: `NOTICE.txt`. WineExplorer setup shows `TERMS.txt` before it downloads the component.',
    ''
) + $table -join "`n") + "`n")
"classes: $((Get-Content $list).Count), files: $((Get-ChildItem $dir -Recurse -File).Count)"
Remove-Item $work -Recurse -Force
Get-Content (Join-Path $OutDir 'SHA256SUMS')
