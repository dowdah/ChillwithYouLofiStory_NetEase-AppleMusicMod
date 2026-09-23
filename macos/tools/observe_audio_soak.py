"""Read-only, bounded game-process observation. Does not drive or restart the game.

Records RSS, CPU time and count of open MusicBridge audio files, not file names.
This is resource evidence only: the user still verifies audible playback.
"""
import argparse
import datetime as dt
import json
import pathlib
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--seconds", type=int, default=3600)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--log", type=pathlib.Path, help="Current MusicBridge log; final playback errors invalidate the run")
    args = parser.parse_args()
    if not 1 <= args.seconds <= 7200:
        raise SystemExit("Duration must be 1–7200 seconds")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    samples = []
    reason = "duration_completed"
    cursor = args.log.stat().st_size if args.log else 0
    pending = b""
    ready_events = 0
    end_events = 0
    with args.output.open("x") as output:
        while True:
            elapsed = time.monotonic() - start
            if args.log:
                with args.log.open("rb") as audio_log:
                    audio_log.seek(cursor)
                    pending += audio_log.read()
                    cursor = audio_log.tell()
                complete, _, pending = pending.rpartition(b"\n")
                lines = complete.decode("utf-8", errors="replace").splitlines()
                ready_events += sum("FLAC就绪" in line or "MP3就绪" in line for line in lines)
                end_events += sum("FLAC有效PCM播放结束" in line for line in lines)
                if any(any(marker in line for marker in ("FLAC播放失败", "网易云播放未完成", "私人FM错误：")) for line in lines):
                    reason = "playback_failure_logged"
                    break
            result = subprocess.run(["/bin/ps", "-p", str(args.pid), "-o", "pid=,rss=,etime=,time="], capture_output=True, text=True)
            if result.returncode or not result.stdout.strip():
                reason = "game_exited_or_unavailable"
                break
            fields = result.stdout.split()
            opened = subprocess.run(["/usr/sbin/lsof", "-p", str(args.pid), "-Fn"], capture_output=True, text=True, timeout=15)
            count = sum(line.startswith("n") and "/ChillWithYouMusicBridge/cache/audio/" in line for line in opened.stdout.splitlines())
            sample = {"utc": dt.datetime.now(dt.timezone.utc).isoformat(), "elapsed_seconds": round(elapsed, 3),
                      "rss_kib": int(fields[1]), "process_age": fields[2], "cpu_time": fields[3], "open_audio_files": count,
                      "ready_events": ready_events, "flac_end_events": end_events}
            samples.append(sample)
            output.write(json.dumps(sample) + "\n")
            output.flush()
            print(f"soak {int(elapsed)}s: RSS={sample['rss_kib']/1024:.1f} MiB, audio files={count}", flush=True)
            if elapsed >= args.seconds:
                break
            time.sleep(min(30, args.seconds - elapsed))
    summary = {"reason": reason, "elapsed_seconds": round(time.monotonic() - start, 3), "samples": len(samples),
               "ready_events": ready_events, "flac_end_events": end_events}
    if samples:
        summary.update(first_rss_kib=samples[0]["rss_kib"], last_rss_kib=samples[-1]["rss_kib"],
                       min_rss_kib=min(s["rss_kib"] for s in samples), max_rss_kib=max(s["rss_kib"] for s in samples),
                       max_open_audio_files=max(s["open_audio_files"] for s in samples))
    args.output.with_suffix(".summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary), flush=True)
    return 0 if reason == "duration_completed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
