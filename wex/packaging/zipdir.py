# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 WineExplorer contributors
"""Zip a directory with standard entry names ('/' separators, the directory itself as top level), sorted, fixed
timestamps. (Windows PowerShell 5.1's Compress-Archive writes '\\' separators.) usage: zipdir.py DIR OUT.zip"""
import os, sys, zipfile

src, out = os.path.abspath(sys.argv[1]), sys.argv[2]
top = os.path.basename(src)
entries = []
for base, dirs, files in os.walk(src):
    dirs.sort()
    for name in sorted(files):
        full = os.path.join(base, name)
        entries.append((top + '/' + os.path.relpath(full, src).replace(os.sep, '/'), full))
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for arcname, full in entries:
        info = zipfile.ZipInfo(arcname, date_time=(2026, 11, 14, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with open(full, 'rb') as f:
            z.writestr(info, f.read(), compresslevel=9)
print(len(entries), 'files')
