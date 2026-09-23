# FLAC decoder

Vendored dr_flac 0.13.3, upstream commit `69d777c482775858e8ea8a7b047c9bcd451febc8`.
Source: https://github.com/mackron/dr_libs/tree/69d777c482775858e8ea8a7b047c9bcd451febc8
SHA-256 of dr_flac.h: `a35468f278f17cf9538ce189debf76a5854a77e139980f7b5185d67566a0c837`.
We select the MIT No Attribution license; the full upstream license is included.

`bash macos/build-audio-native.sh` builds a macOS 11+ universal dylib using only
Xcode Command Line Tools. Six public C symbols form ABI version 1. The library
does not own audio output and never runs on Unity's audio callback thread.
It accepts native FLAC with known length, mono/stereo, 16/24-bit, 8–192 kHz and
at most INT32_MAX PCM frames (Unity's clip-length limit). No DRM decoding.
