#!/bin/bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
DEST="${1:-$PWD/.downloads/media-native}"
mkdir -p "$DEST"
for CPU in arm64 x86_64; do
    xcrun clang++ -std=c++17 -O2 -fobjc-arc -fblocks -fvisibility=hidden -arch "$CPU" \
        -mmacosx-version-min=11.0 -dynamiclib native/musicbridge_media.mm \
        -framework Foundation -framework MediaPlayer \
        -o "$DEST/libmusicbridge_media.$CPU.dylib" \
        -install_name @rpath/libmusicbridge_media.dylib
done
xcrun lipo -create "$DEST/libmusicbridge_media.arm64.dylib" \
    "$DEST/libmusicbridge_media.x86_64.dylib" -output "$DEST/libmusicbridge_media.dylib"
codesign --force --sign - "$DEST/libmusicbridge_media.dylib"
/usr/bin/lipo "$DEST/libmusicbridge_media.dylib" -verify_arch arm64
/usr/bin/lipo "$DEST/libmusicbridge_media.dylib" -verify_arch x86_64
shasum -a 256 "$DEST/libmusicbridge_media.dylib"
