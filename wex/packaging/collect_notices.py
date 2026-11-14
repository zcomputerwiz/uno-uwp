# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
"""Write NOTICE.txt for the XamlShim release: every NuGet package of the build (project.assets.json, net10.0-desktop
win-x64, the runtime pack included) with its licence from the nuspec and the full text of the licence and notice files
the package ships. usage: collect_notices.py ASSETS_JSON NUGET_ROOT OUT"""
import json, os, re, sys, glob
import xml.etree.ElementTree as ET

assets, root, out = sys.argv[1], sys.argv[2], sys.argv[3]
data = json.load(open(assets, encoding='utf-8'))
libs = {k: v for k, v in data['libraries'].items() if v.get('type') == 'package'}
names = set()
for target, entries in data['targets'].items():
    names.update(entries.keys())
# the runtime packs of a self-contained publish are not in the assets file
for pack in sys.argv[4:]:
    ident, version = pack.split('/')
    libs[pack] = {'path': f'{ident.lower()}/{version}', 'type': 'package'}
    names.add(pack)
# not in the Release output: Debug-only Uno tooling, a build-time generator, other platforms' native packages
EXCLUDE = re.compile(r'^(Uno\.UI\.HotDesign|Uno\.UI\.App\.Mcp|Uno\.Settings\.DevServer|Uno\.WinUI\.DevServer|'
                     r'Uno\.UI\.RemoteControl|Tmds\.DBus\.Generator|Uno\.icu-macos|.*NativeAssets\.(Linux|macOS))/', re.I)
names = {n for n in names if not EXCLUDE.match(n)}
lines = []
missing = []
seen_texts = {}
for key in sorted(names, key=str.lower):
    if key not in libs: continue
    pkg_dir = os.path.join(root, libs[key]['path'])
    ident, version = key.split('/')
    nuspecs = glob.glob(os.path.join(pkg_dir, '*.nuspec'))
    lic = url = ''
    if nuspecs:
        tree = ET.parse(nuspecs[0])
        for e in tree.iter():
            tag = e.tag.split('}')[-1]
            if tag == 'license': lic = (e.get('type', '') + ': ' + (e.text or '').strip())
            if tag == 'licenseUrl': url = (e.text or '').strip()
            if tag == 'projectUrl' and not url: pass
    files = [f for f in glob.glob(os.path.join(pkg_dir, '*')) + glob.glob(os.path.join(pkg_dir, '*', '*'))
             if re.search(r'(licen[cs]e|notice|third.?party|copying)', os.path.basename(f), re.I) and os.path.isfile(f)
             and os.path.getsize(f) < 2_000_000]
    lines.append('=' * 100)
    lines.append(f'{ident} {version}')
    if lic: lines.append(f'License {lic}')
    if url: lines.append(f'License URL: {url}')
    if not files and not lic and not url: missing.append(key)
    for f in sorted(files):
        try:
            text = open(f, encoding='utf-8-sig', errors='replace').read().strip()
        except OSError:
            continue
        if text in seen_texts:
            lines.append(f'--- {os.path.basename(f)}: same text as in {seen_texts[text]}')
            continue
        seen_texts[text] = f'{ident} {version}'
        lines.append(f'--- {os.path.basename(f)}')
        lines.append(text)
    lines.append('')
# licences of shipped packages whose nuspec names none, taken from their source repositories
EXTRA = [('Selawik 1.01 (https://github.com/microsoft/Selawik), fonts in Assets/Fonts, SIL Open Font License 1.1',
          '../../XamlShim/Assets/Fonts/Selawik-OFL.txt'),
         ('Themes/UwpGeneric.xaml: generic.xaml of microsoft-ui-xaml (https://github.com/microsoft/microsoft-ui-xaml, '
          'commit 8463f45162149de0ec3ad7df752596893fe3e13e), compiled into XamlShim.dll, MIT License',
          '../../XamlShim/Themes/WinUI-LICENSE.txt'),
         ('Uno.icu-win 77.4.1 (https://github.com/unoplatform/uno.icu)', 'uno.icu-LICENSE.md'),
         ('ICU 77 (International Components for Unicode, in icuuc77.dll and icudt77.dll)', 'ICU-77-LICENSE.txt'),
         ('Uno.Diagnostics.Eventing 2.0.1 (https://github.com/unoplatform/Uno.Diagnostics.Eventing)', 'Uno.Diagnostics.Eventing-License.md')]
extra_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'extra-licenses')
for title, name in EXTRA:
    lines.append('=' * 100)
    lines.append(title)
    lines.append(open(os.path.join(extra_dir, name), encoding='utf-8').read().strip())
    lines.append('')
missing = [m for m in missing if not m.startswith(('Uno.icu-win/', 'Uno.Diagnostics.Eventing/'))]
header = ['Third-party software in the XamlShim component (WineExplorer UWP XAML support)', '',
          'XamlShim itself is licensed under the Apache License 2.0 (LICENSE.txt). It contains PriFormat from PriTools',
          '(https://github.com/chausner/PriTools, Apache License 2.0, Copyright Christoph Hausner) and is built with the',
          'packages below, which are redistributed under their own licences, reproduced or referenced here.', '']
open(out, 'w', encoding='utf-8', newline='\n').write('\n'.join(header + lines) + '\n')
print(f'{len(names)} packages, {len(seen_texts)} distinct licence texts, without licence information: {missing}')
