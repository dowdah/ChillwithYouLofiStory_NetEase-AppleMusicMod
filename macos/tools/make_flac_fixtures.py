"""Deterministic test-only PCM, encoded by the system FLAC reference encoder.

Requires `flac` on the developer machine, never at runtime. No copyrighted audio.
"""
import pathlib
import random
import subprocess
import tempfile


def sample(frame, channel, bits):
    return ((frame * 31 + channel * 101) % (1 << bits)) - (1 << (bits - 1))


def main():
    dest = pathlib.Path(__file__).resolve().parents[1] / "test-artifacts/netease-phase2/fixtures"
    dest.mkdir(parents=True, exist_ok=True)
    for rate in (44100, 48000, 96000, 192000):
        for bits in (16, 24):
            for channels in (1, 2):
                name = f"{rate}-{bits}-{channels}"
                output = dest / f"{name}.flac"
                with tempfile.TemporaryDirectory() as tmp:
                    raw = pathlib.Path(tmp) / "pcm.raw"
                    with raw.open("wb") as stream:
                        for start in range(0, rate * 3, 8192):
                            block = bytearray()
                            for frame in range(start, min(start + 8192, rate * 3)):
                                for channel in range(channels):
                                    block.extend(sample(frame, channel, bits).to_bytes(bits // 8, "little", signed=True))
                            stream.write(block)
                    subprocess.run(["flac", "--silent", "--force", "--force-raw-format", "--endian=little", "--sign=signed",
                                    f"--channels={channels}", f"--bps={bits}", f"--sample-rate={rate}", "-o", str(output), str(raw)], check=True)
    benchmark = dest.parent / "bench-60sec.flac"
    with tempfile.TemporaryDirectory() as tmp:
        raw = pathlib.Path(tmp) / "benchmark.raw"
        rng = random.Random(20260923)
        remaining = 60 * 48000 * 2 * 2
        with raw.open("wb") as stream:
            while remaining:
                block = rng.randbytes(min(65536, remaining))
                stream.write(block)
                remaining -= len(block)
        subprocess.run(["flac", "--silent", "--force", "--force-raw-format", "--endian=little", "--sign=signed",
                        "--channels=2", "--bps=16", "--sample-rate=48000", "-o", str(benchmark), str(raw)], check=True)
    print(f"16 deterministic FLAC fixtures: {dest}")
    print(f"60-second deterministic benchmark FLAC: {benchmark}")


if __name__ == "__main__":
    main()
