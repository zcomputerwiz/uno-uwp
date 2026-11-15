# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
# Builds WinUI 2's Microsoft.UI.Xaml.winmd from its MIT-licensed interface definitions (github.com/microsoft/
# microsoft-ui-xaml, branch winui2/main, pinned below), as that repository's build does: one IDL that #includes
# idl\Microsoft.UI.Xaml.idl and every control's IDL, compiled with MIDL 3.0 against the Windows SDK, then split per
# namespace with mdmerge. Experimental controls (MUX_EXPERIMENTAL) and WebView2 (it needs the WebView2 SDK's
# metadata) are left out, as release 2.8 has no experimental controls and XamlShim no WebView2.
# No Microsoft binary is used: the NuGet package's winmd is under Microsoft's proprietary licence.
# usage: build-metadata.ps1 [-OutDir DIR]   (default: winui2\build; prints the path of Microsoft.UI.Xaml.winmd)
# Needs git, Visual Studio 2022 C++ build tools (MIDL preprocesses with cl) and the Windows SDK 10.0.26100.
param([string]$OutDir = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
$commit = 'ac1341314cc895c23cee47e941ca6ad387ef9907'
$sdkVersion = '10.0.26100.0'
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$winmd = Join-Path $OutDir 'merged\Microsoft.UI.Xaml.winmd'
$stamp = Join-Path $OutDir 'commit.txt'
if ((Test-Path $winmd) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $commit)) { return $winmd }

# 1. the interface definitions only (sparse checkout of the pinned commit)
$src = Join-Path $OutDir 'src'
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $src | Out-Null
git -C $src init -q
git -C $src remote add origin https://github.com/microsoft/microsoft-ui-xaml.git
git -C $src config core.sparseCheckout true
Set-Content -Path (Join-Path $src '.git\info\sparse-checkout') -Value @('/idl/', '/dev/**/*.idl', '/LICENSE') -Encoding ascii
git -C $src fetch -q --depth 1 --filter=blob:none origin $commit
if ($LASTEXITCODE -ne 0) { throw 'fetching microsoft-ui-xaml failed' }
git -C $src checkout -q FETCH_HEAD
if ($LASTEXITCODE -ne 0) { throw 'checking out microsoft-ui-xaml failed' }

# 2. the merged IDL, as the repository's MergeIDLFiles target writes it
$files = @(Join-Path $src 'idl\Microsoft.UI.Xaml.idl')
$files += Get-ChildItem (Join-Path $src 'dev') -Recurse -Filter *.idl | Sort-Object FullName |
    Where-Object { $_.FullName -notmatch '\\WebView2\\' -and (Get-Content $_.FullName -Raw) -notmatch 'MUX_EXPERIMENTAL' } |
    ForEach-Object FullName
$merged = Join-Path $OutDir 'merged.idl'
Set-Content -Path $merged -Value ($files | ForEach-Object { "#include <$_>" }) -Encoding ascii

# 3. MIDL 3.0 and mdmerge, in the Visual Studio developer environment (cl preprocesses the IDL)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ build tools not found' }
$bin = Join-Path $sdk "bin\$sdkVersion\x64"
$meta = Join-Path $sdk "UnionMetadata\$sdkVersion"
$unmerged = Join-Path $OutDir 'unmerged'
$out = Join-Path $OutDir 'merged'
New-Item -ItemType Directory -Path $unmerged, $out | Out-Null
$cmd = Join-Path $OutDir 'midl.cmd'
Set-Content -Path $cmd -Encoding ascii -Value @(
    '@echo off',
    "call `"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>&1",
    "`"$bin\midl.exe`" /nologo /winrt /W1 /nomidl /h nul /enum_class /ns_prefix /metadata_dir `"$meta`" /reference `"$meta\Windows.winmd`" /winmd `"$unmerged\Microsoft.UI.winmd`" /I `"$src\idl`" /I `"$sdk\Include\$sdkVersion\winrt`" `"$merged`" > `"$OutDir\midl.log`" 2>&1",
    'if errorlevel 1 exit /b 1',
    "`"$bin\mdmerge.exe`" -i `"$unmerged`" -o `"$out`" -metadata_dir `"$meta`" -partial -n:3 > `"$OutDir\mdmerge.log`" 2>&1")
cmd /c "`"$cmd`""
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $winmd)) {
    Get-Content (Join-Path $OutDir 'midl.log') -ErrorAction SilentlyContinue | Select-String 'error' | Select-Object -First 10 | ForEach-Object Line
    throw 'building the WinUI 2 metadata failed'
}
Set-Content -Path $stamp -Value $commit -Encoding ascii
return $winmd
