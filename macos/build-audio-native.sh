#!/bin/bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
EXPECTED=a35468f278f17cf9538ce189debf76a5854a77e139980f7b5185d67566a0c837
ACTUAL=$(shasum -a 256 native/vendor/dr_flac.h | awk '{print $1}')
[[ "$ACTUAL" == "$EXPECTED" ]] || { echo 'dr_flac source checksum mismatch' >&2; exit 1; }
DEST="${1:-$PWD/.downloads/audio-native}"
mkdir -p "$DEST"
for CPU in arm64 x86_64; do
    xcrun clang -std=c99 -O2 -fvisibility=hidden -arch "$CPU" -mmacosx-version-min=11.0 \
        -dynamiclib native/musicbridge_flac.c -o "$DEST/libmusicbridge_flac.$CPU.dylib" \
        -install_name @rpath/libmusicbridge_flac.dylib
done
xcrun lipo -create "$DEST/libmusicbridge_flac.arm64.dylib" "$DEST/libmusicbridge_flac.x86_64.dylib" -output "$DEST/libmusicbridge_flac.dylib"
codesign --force --sign - "$DEST/libmusicbridge_flac.dylib"
/usr/bin/lipo "$DEST/libmusicbridge_flac.dylib" -verify_arch arm64
/usr/bin/lipo "$DEST/libmusicbridge_flac.dylib" -verify_arch x86_64
