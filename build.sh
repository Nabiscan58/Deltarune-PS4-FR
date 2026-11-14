#!/bin/sh
# Builds DeltaruneFR-PS4-<version>.zip: the Windows launcher (mcs, .NET Framework 4.5), Port.csx, the user guide and
# UndertaleModTool's command-line tool for Windows.
#   ./build.sh <UTMT_CLI_v0.9.2.0-Windows.zip> [out dir]
# The UndertaleModTool archive comes from https://github.com/UnderminersTeam/UndertaleModTool/releases (0.9.2.0).
# Needs: mono-mcs, python3, zip, unzip.
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
UTMT=${1:?path of UTMT_CLI_v0.9.2.0-Windows.zip}
OUT=${2:-$HERE/out}
VERSION=$(cat "$HERE/VERSION")
W=$(mktemp -d)
B=$W/DeltaruneFR-PS4
mkdir -p "$B/utmt" "$OUT"
unzip -q "$UTMT" -d "$B/utmt"
mcs -langversion:5 -target:winexe -optimize+ -sdk:4.5 -r:System.dll -r:System.Core.dll -r:System.Drawing.dll \
    -r:System.Windows.Forms.dll -out:"$B/DeltaruneFR-PS4.exe" "$HERE/launcher/DeltaruneFR-PS4.cs"
cp "$HERE/port/Port.csx" "$HERE/LISEZ-MOI.txt" "$B/"
# fichiers.crc: what the launcher checks before starting (crc32, size, path)
python3 - "$B" <<'PY'
import os, sys, zlib
root = sys.argv[1]
out = []
for dp, dns, fns in os.walk(root):
    for fn in fns:
        p = os.path.join(dp, fn)
        rel = os.path.relpath(p, root).replace(os.sep, '/')
        if rel in ('fichiers.crc', 'DeltaruneFR-PS4.exe', 'LISEZ-MOI.txt'):
            continue
        out.append('%08x %d %s' % (zlib.crc32(open(p, 'rb').read()), os.path.getsize(p), rel))
out.sort(key=lambda l: l.split(' ', 2)[2].lower())
open(os.path.join(root, 'fichiers.crc'), 'w', newline='\r\n').write('\n'.join(out) + '\n')
PY
rm -f "$OUT/DeltaruneFR-PS4-$VERSION.zip"
(cd "$W" && zip -qr -6 "$OUT/DeltaruneFR-PS4-$VERSION.zip" DeltaruneFR-PS4)
rm -rf "$W"
echo "$OUT/DeltaruneFR-PS4-$VERSION.zip"
