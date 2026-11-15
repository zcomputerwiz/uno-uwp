# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
# Builds WinmdGen and writes XamlShim\Abi.g.cs for the classes in WinmdGen\classes.txt, from the Windows SDK metadata
# and WinUI 2's metadata (built from its MIT-licensed interface definitions by winui2\build-metadata.ps1).
# usage: generate.ps1 [-WinMd PATH] [-WinUI2 PATH]
#   -WinMd   Windows.winmd (default: that of the Windows SDK 10.0.26100.0)
#   -WinUI2  Microsoft.UI.Xaml.winmd (default: built by winui2\build-metadata.ps1)
param([string]$WinMd = (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd'),
      [string]$WinUI2)
$ErrorActionPreference = 'Stop'
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
if (-not (Test-Path $WinMd)) { throw "Windows SDK metadata not found: $WinMd" }
if (-not $WinUI2) { $WinUI2 = & (Join-Path $PSScriptRoot 'winui2\build-metadata.ps1') | Select-Object -Last 1 }
if (-not (Test-Path $WinUI2)) { throw "WinUI 2 metadata not found: $WinUI2" }
dotnet build (Join-Path $PSScriptRoot 'WinmdGen') -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'WinmdGen build failed' }
$classes = Get-Content (Join-Path $PSScriptRoot 'WinmdGen\classes.txt')
dotnet (Join-Path $PSScriptRoot 'WinmdGen\bin\Release\net10.0\WinmdGen.dll') "$WinMd;$WinUI2" (Join-Path $PSScriptRoot 'XamlShim\Abi.g.cs') XamlShim.Abi @classes
if ($LASTEXITCODE -ne 0) { throw 'WinmdGen failed' }
