# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
# Builds WinmdGen and writes XamlShim\Abi.g.cs from the Windows SDK metadata for the classes in WinmdGen\classes.txt.
# usage: generate.ps1 [-WinMd PATH]   (default: Windows.winmd of the Windows SDK 10.0.26100.0)
param([string]$WinMd = (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd'))
$ErrorActionPreference = 'Stop'
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
if (-not (Test-Path $WinMd)) { throw "Windows SDK metadata not found: $WinMd" }
dotnet build (Join-Path $PSScriptRoot 'WinmdGen') -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'WinmdGen build failed' }
$classes = Get-Content (Join-Path $PSScriptRoot 'WinmdGen\classes.txt')
dotnet (Join-Path $PSScriptRoot 'WinmdGen\bin\Release\net10.0\WinmdGen.dll') $WinMd (Join-Path $PSScriptRoot 'XamlShim\Abi.g.cs') XamlShim.Abi @classes
if ($LASTEXITCODE -ne 0) { throw 'WinmdGen failed' }
