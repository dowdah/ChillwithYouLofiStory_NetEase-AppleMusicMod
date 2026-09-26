#!/usr/bin/env python3
"""Write a hash-bound manifest for an isolated experience candidate directory."""
import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import platform
import re
import subprocess


ROOT = Path(__file__).resolve().parents[2]
PLUGINS = Path("BepInEx/plugins/ChillWithYouMusicBridge")
FILES = {
    "managedDll": PLUGINS / "MusicBridge.Plugin.dll",
    "audioDylib": PLUGINS / "libmusicbridge_flac.dylib",
    "mediaDylib": PLUGINS / "libmusicbridge_media.dylib",
}


def command(*parts):
    return subprocess.check_output(parts, cwd=ROOT, text=True).strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_number(path, pattern):
    match = re.search(pattern, path.read_text(encoding="utf-8"))
    if not match:
        raise SystemExit(f"无法从源码确认版本：{path}")
    return match.group(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--native-core-digest", required=True)
    parser.add_argument("--native-core-provenance", choices=("cached", "source-rebuild"), default="cached")
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    binaries = {}
    for key, relative in FILES.items():
        path = candidate / relative
        if not path.is_file():
            raise SystemExit(f"缺少候选二进制：{relative}")
        binaries[key] = {"relativePath": str(relative), "sha256": sha256(path)}
    media = candidate / FILES["mediaDylib"]
    for arch in ("arm64", "x86_64"):
        subprocess.run(["/usr/bin/lipo", str(media), "-verify_arch", arch], check=True)
    plugin_version = source_number(ROOT / "macos/src/MusicBridge/Plugin.cs",
                                   r'PluginVersion\s*=\s*"([^"]+)"')
    config_schema = int(source_number(ROOT / "macos/src/MusicBridge/MusicBridgeOptions.cs",
                                      r'CurrentSchemaVersion\s*=\s*(\d+)'))
    media_abi = int(source_number(ROOT / "macos/native/musicbridge_media.h",
                                  r'#define\s+MB_MEDIA_ABI\s+(\d+)u'))
    library = ctypes.CDLL(str(media))
    library.mb_media_abi_version.restype = ctypes.c_uint32
    if library.mb_media_abi_version() != media_abi:
        raise SystemExit("候选媒体 dylib 的 ABI 与源码声明不一致")
    core = candidate / "BepInEx/core"
    core_lines = "".join(f"{sha256(path)}  ./{path.name}\n" for path in sorted(core.iterdir()) if path.is_file())
    actual_core_digest = hashlib.sha256(core_lines.encode("utf-8")).hexdigest()
    if actual_core_digest != args.native_core_digest:
        raise SystemExit("候选运行库摘要与指定的已验缓存不匹配")
    manifest = {
        "sourceCommit": command("git", "rev-parse", "HEAD"),
        "sourceWorkingTreeDirty": bool(command("git", "status", "--porcelain", "--untracked-files=no")),
        "baselineCommit": "3084e9f411d0290dcee43b1961848fcb302c5fb0",
        "pluginVersion": plugin_version + "-candidate",
        "configSchema": config_schema,
        "mediaAbi": media_abi,
        "mediaArchitectures": ["arm64", "x86_64"],
        "nativeCoreDigest": args.native_core_digest,
        "nativeCoreProvenance": args.native_core_provenance,
        "buildHost": {"macOS": platform.mac_ver()[0], "cpu": platform.machine()},
        "binaries": binaries,
        "gameHostAcceptance": "NotRun",
        "intelGameAcceptance": "NotRun",
        "oneHourFinalSoak": "NotRun",
        "realRollback": "NotRun",
    }
    destination = candidate / "release-manifest.json"
    destination.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(destination)


if __name__ == "__main__":
    main()
