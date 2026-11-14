# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
"""Adapt WinUI 3's system generic.xaml (MIT) for XamlShim: keep Microsoft's notice, add provenance, map the system
font to Selawik. usage: adapt_generic.py IN OUT COMMIT"""
import sys

src, out, commit = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(src, encoding='utf-8-sig').read()
first_end = text.index('-->') + 3
notice = text[:first_end]
body = text[first_end:]
selawik = 'ms-appx:///Assets/Fonts/selawk.ttf#Selawik'
count = body.count('>XamlAutoFontFamily<') + body.count('"XamlAutoFontFamily"')
body = body.replace('>XamlAutoFontFamily<', '>' + selawik + '<').replace('"XamlAutoFontFamily"', '"' + selawik + '"')
if 'XamlAutoFontFamily' in body:
    raise SystemExit('unmapped XamlAutoFontFamily left')
# On Windows these are default styles (lowest precedence); merged into the application's resources they become
# implicit styles that also reach the templates of Uno's own window chrome. Left out until Uno registers them as
# default styles: the ContentPresenter one (Left/Top) shrank the window content to its desired size.
removed = []
for target in ('ContentPresenter',):
    start = body.find('    <Style TargetType="' + target + '">')
    if start < 0:
        raise SystemExit('style for ' + target + ' not found')
    end = body.index('</Style>', start) + len('</Style>')
    body = body[:start] + '    <!-- XamlShim: the default style for ' + target + ' is left out (see the header) -->' + body[end:]
    removed.append(target)
header = (notice + '\n<!--\n'
          '  From https://github.com/microsoft/microsoft-ui-xaml (dxaml/xcp/dxaml/themes/generic.xaml, commit ' + commit + '):\n'
          '  the XAML framework\'s own default styles, which are the UWP system styles (Windows.UI.Xaml without WinUI 2).\n'
          '  XamlShim uses them so applications built against Windows.UI.Xaml look and measure as on Windows.\n'
          '  Changed for XamlShim: XamlAutoFontFamily (Segoe UI on Windows) is Selawik, an open metric-compatible font;\n'
          '  the default style for ContentPresenter (Left/Top) is left out: merged into the application resources these\n'
          '  default styles act as implicit styles and would also reach the templates of Uno\'s own window chrome.\n'
          '-->')
open(out, 'w', encoding='utf-8', newline='\n').write(header + body.replace('\r\n', '\n'))
print(f'{count} font references mapped')
