#!/usr/bin/env python3
"""Write a hash-bound manifest for an isolated experience candidate directory."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--native-core-digest", required=True)
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
    core = candidate / "BepInEx/core"
    core_lines = "".join(f"{sha256(path)}  ./{path.name}\n" for path in sorted(core.iterdir()) if path.is_file())
    actual_core_digest = hashlib.sha256(core_lines.encode("utf-8")).hexdigest()
    if actual_core_digest != args.native_core_digest:
        raise SystemExit("候选运行库摘要与指定的已验缓存不匹配")
    manifest = {
        "sourceCommit": command("git", "rev-parse", "HEAD"),
        "sourceWorkingTreeDirty": bool(command("git", "status", "--porcelain", "--untracked-files=no")),
        "baselineCommit": "3084e9f411d0290dcee43b1961848fcb302c5fb0",
        "pluginVersion": "1.5.0.0-candidate",
        "configSchema": 2,
        "mediaAbi": 1,
        "mediaArchitectures": ["arm64", "x86_64"],
        "nativeCoreCacheDigest": args.native_core_digest,
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
