#!/bin/bash
# Steam invokes this silently before the unmodified game; there is no separate launcher window.
set -euo pipefail
MOD_ROOT="$(cd -- "$(dirname -- "$0")" && pwd)"
GAME_APP="${CHILL_GAME_APP:-$HOME/Library/Application Support/Steam/steamapps/common/Chill with You Lo-Fi Story/Chill With You.app}"
MODE="${1:-}"
LAUNCH_LOG_DIR="$MOD_ROOT/logs"
trace() {
    mkdir -p "$LAUNCH_LOG_DIR" 2>/dev/null || true
    printf '[MusicBridge] %s\n' "$*" >> "$LAUNCH_LOG_DIR/launcher-bootstrap.log" 2>/dev/null || true
}
fail() {
    echo "$1" >&2
    trace "ERROR: $1"
    exit 1
}
if [[ "$MODE" == --steam ]]; then
    shift
    [[ $# -gt 0 ]] || fail 'Steam 未提供游戏路径。'
    GAME_INPUT="$1"
    trace "steam raw game input: $(printf '%q' "$GAME_INPUT")"
    # Steam can pass %command% with one or more literal outer quote layers.
    # Strip matching outer pairs only; embedded quotes remain unchanged.
    while (( ${#GAME_INPUT} >= 2 )); do
        FIRST_CHAR="${GAME_INPUT:0:1}"
        LAST_CHAR="${GAME_INPUT: -1}"
        if [[ "$FIRST_CHAR" == "$LAST_CHAR" && ( "$FIRST_CHAR" == "'" || "$FIRST_CHAR" == '"' ) ]]; then
            GAME_INPUT="${GAME_INPUT:1:${#GAME_INPUT}-2}"
        else
            break
        fi
    done
    trace "steam normalized game input: $(printf '%q' "$GAME_INPUT")"
    shift
    if [[ "$GAME_INPUT" == *.app ]]; then
        GAME_APP="$GAME_INPUT"
        GAME_NAME=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$GAME_APP/Contents/Info.plist")
        GAME_BIN="$GAME_APP/Contents/MacOS/$GAME_NAME"
    else
        GAME_BIN="$GAME_INPUT"
        GAME_NAME="$(basename -- "$GAME_BIN")"
    fi
else
    if [[ "$MODE" == --check ]]; then shift; elif [[ -n "$MODE" ]]; then fail '未知启动选项。'; fi
    GAME_NAME=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$GAME_APP/Contents/Info.plist")
    GAME_BIN="$GAME_APP/Contents/MacOS/$GAME_NAME"
fi
trace "resolved game executable: $(printf '%q' "$GAME_BIN")"
[[ -x "$GAME_BIN" ]] || fail '未找到游戏可执行文件。'
if [[ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" == 1 ]]; then NATIVE_ARCH=arm64; else NATIVE_ARCH=x86_64; fi
RUN_ARCH="${MUSICBRIDGE_ARCH:-$NATIVE_ARCH}"
trace "selected architecture: $(printf '%q' "$RUN_ARCH")"
[[ "$RUN_ARCH" == arm64 || "$RUN_ARCH" == x86_64 ]] || fail '不支持的架构。'
# Steam itself may run under Rosetta. Force the native lipo helper so its
# Command Line Tools dependencies match this Apple Silicon host.
LIPO_OUTPUT=$(env -u DYLD_INSERT_LIBRARIES /usr/bin/arch -arm64 /usr/bin/lipo "$GAME_BIN" -verify_arch "$RUN_ARCH" 2>&1) || fail "游戏架构校验失败：$LIPO_OUTPUT"
[[ -f "$MOD_ROOT/BepInEx/core/MonoMod.Core.dll" && -f "$MOD_ROOT/libdoorstop.dylib" && -f "$MOD_ROOT/BepInEx/plugins/ChillWithYouMusicBridge/MusicBridge.Plugin.dll" ]] || fail '原生 Mod 运行库不完整，请重新构建。'
[[ -z "${DOORSTOP_DISABLE:-}" ]] || fail '当前环境设置了 DOORSTOP_DISABLE。'
if [[ "$MODE" == --check ]]; then
    echo "检查通过：$RUN_ARCH / 原生兼容 BepInEx / 不修改游戏程序"
    echo "Mod：$MOD_ROOT"
    exit 0
fi
if /usr/bin/pgrep -x "$GAME_NAME" >/dev/null; then fail '游戏已运行，请先正常退出。'; fi
mkdir -p "$MOD_ROOT/logs"
trace "launching architecture=$RUN_ARCH mode=${MODE:-manual}"
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY="$MOD_ROOT/BepInEx/core/BepInEx.Preloader.dll"
export DOORSTOP_MONO_DEBUG_ENABLED=0 DOORSTOP_MONO_DEBUG_SUSPEND=0
export SteamAppId=3548580
printf '[MusicBridge] launch architecture=%s mode=%s\n' "$RUN_ARCH" "${MODE:-manual}" >> "$MOD_ROOT/logs/launcher.log"
exec /usr/bin/arch "-$RUN_ARCH" -e "DYLD_INSERT_LIBRARIES=$MOD_ROOT/libdoorstop.dylib" "$GAME_BIN" "$@" -logFile "$MOD_ROOT/logs/Unity.log" >> "$MOD_ROOT/logs/launcher.log" 2>&1
