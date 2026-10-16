#!/usr/bin/env python3
"""Explicit, compare-and-set candidate switch; never deploy binaries or replace other apps' options."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import steam_options as s


def require_stopped():
    for name in ('steam_osx', 'Chill With You'):
        result = subprocess.run(['/usr/bin/pgrep', '-x', name], capture_output=True)
        if result.returncode != 1:
            raise RuntimeError('程序仍运行或无法可靠检查，请正常退出 Mac 游戏和 Steam。')


def changed(text, expected, target):
    if s.read_option(text) != expected:
        raise RuntimeError('启动选项与预期不符，拒绝覆盖外部修改。')
    result = s.set_option(text, target)
    if s.read_option(result) != target or s.set_option(result, expected) != text:
        raise RuntimeError('启动选项往返校验失败。')
    return result


def switch(config, record_path, expected_launcher, candidate):
    require_stopped()
    config, record_path = config.resolve(), record_path.resolve()
    expected = json.dumps(str(expected_launcher.resolve()), ensure_ascii=False) + ' --steam %command%'
    target = json.dumps(str(candidate.resolve()), ensure_ascii=False) + ' --steam %command%'
    if not expected_launcher.is_file() or not candidate.is_file() or expected == target:
        raise RuntimeError('启动目标缺失或未发生变化。')
    if record_path.exists():
        raise RuntimeError('恢复记录已存在，拒绝覆盖。')
    with config.open(newline='') as f:
        text = f.read()
    updated = changed(text, expected, target)
    record_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    backup = record_path.with_suffix('.vdf')
    with backup.open('x') as f:
        backup.chmod(0o600)
        f.write(text)
    record = {'config': str(config), 'previous': expected, 'installed': target,
              'backup': str(backup), 'beforeSha256': hashlib.sha256(text.encode()).hexdigest()}
    with record_path.open('x') as f:
        record_path.chmod(0o600)
        json.dump(record, f, indent=2, ensure_ascii=False)
    # Recheck immediately before replacement. Steam must remain stopped throughout.
    if config.read_bytes() != text.encode():
        raise RuntimeError('配置文件在检查后发生变化，未执行切换。')
    s.atomic_write(config, updated)
    with config.open(newline='') as f:
        if s.read_option(f.read()) != target:
            raise RuntimeError('切换读回失败，请使用保留的恢复记录。')
    return target


def restore(record_path):
    require_stopped()
    record = json.loads(record_path.read_text())
    config = Path(record['config'])
    with config.open(newline='') as f:
        text = f.read()
    if s.read_option(text) == record['previous']:
        return record['previous']
    updated = changed(text, record['installed'], record['previous'])
    if config.read_bytes() != text.encode():
        raise RuntimeError('配置文件在检查后发生变化，未恢复。')
    s.atomic_write(config, updated)
    with config.open(newline='') as f:
        if s.read_option(f.read()) != record['previous']:
            raise RuntimeError('恢复读回失败。')
    return record['previous']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['switch', 'restore'])
    parser.add_argument('--record', required=True, type=Path)
    parser.add_argument('--config', type=Path)
    parser.add_argument('--expected-launcher', type=Path)
    parser.add_argument('--candidate-launcher', type=Path)
    args = parser.parse_args()
    if args.action == 'restore':
        print(restore(args.record))
    else:
        if not all((args.config, args.expected_launcher, args.candidate_launcher)):
            parser.error('switch requires config, expected-launcher and candidate-launcher')
        print(switch(args.config, args.record, args.expected_launcher, args.candidate_launcher))


if __name__ == '__main__':
    main()
