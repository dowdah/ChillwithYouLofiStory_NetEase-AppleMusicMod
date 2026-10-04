#!/usr/bin/env python3
"""Apply the staged runtime without touching plugin config, caches, or game files."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import time
import uuid

PROJECT = Path(__file__).resolve().parents[2]
RUNTIME = PROJECT/'macos/dist/ChillMusicMac'
STAGE = PROJECT/'.local/native-staging'
LEGACY_FILES = ['BepInEx/plugins/ChillWithYouMusicBridge/MusicBridge.Plugin.dll', 'Start-Music-Mod.command', 'launch-core.sh', 'libdoorstop.dylib']
FILES = LEGACY_FILES + ['BepInEx/plugins/ChillWithYouMusicBridge/music.js',
    'BepInEx/plugins/ChillWithYouMusicBridge/libmusicbridge_flac.dylib',
    'BepInEx/plugins/ChillWithYouMusicBridge/libmusicbridge_media.dylib',
    'BepInEx/plugins/ChillWithYouMusicBridge/LICENSE.dr_libs',
    'BepInEx/LICENSE.native-MIT', 'BepInEx/LICENSE.harmony-MIT', '使用说明.md',
    'NATIVE-RUNTIME.md', 'TEST-REPORT.md', 'THIRD-PARTY-NOTICES.md',
    'EXPERIENCE-ENHANCEMENTS.md', 'EXPERIENCE-ACCEPTANCE.md', 'MEDIA-CONTROL-POC.md']
CONFIG_REL = 'BepInEx/plugins/ChillWithYouMusicBridge/config/musicbridge.options.json'
BINARY_FILES = {
    'managedDll': FILES[0],
    'audioDylib': 'BepInEx/plugins/ChillWithYouMusicBridge/libmusicbridge_flac.dylib',
    'mediaDylib': 'BepInEx/plugins/ChillWithYouMusicBridge/libmusicbridge_media.dylib',
}

def verify_candidate(stage):
    runtime=RUNTIME.resolve()
    if stage == runtime or stage in runtime.parents or runtime in stage.parents:
        raise RuntimeError('候选目录与现用运行目录重叠，拒绝安装。')
    manifest_path=stage/'release-manifest.json'
    try:
        release=json.loads(manifest_path.read_text())
        if release.get('sourceWorkingTreeDirty') is not False or release.get('mediaAbi') != 2:
            raise ValueError('清单不是干净源码构建的 ABI 2 候选')
        core=stage/'BepInEx/core'
        if core.is_symlink() or any(path.is_symlink() for path in core.iterdir()):
            raise ValueError('候选原生运行库含符号链接')
        core_lines=''.join(hashlib.sha256(path.read_bytes()).hexdigest()+'  ./'+path.name+'\n'
            for path in sorted(core.iterdir()) if path.is_file())
        if hashlib.sha256(core_lines.encode('utf-8')).hexdigest() != release['nativeCoreDigest']:
            raise ValueError('候选原生运行库摘要不符')
        for key, rel in BINARY_FILES.items():
            item=release['binaries'][key]
            expected=item['sha256']
            if item['relativePath'] != rel or not isinstance(expected,str) or len(expected) != 64:
                raise ValueError('清单文件路径或哈希格式不符：'+key)
            digest=hashlib.sha256((stage/rel).read_bytes()).hexdigest()
            if digest != expected:
                raise ValueError('候选二进制哈希不符：'+key)
    except (OSError,ValueError,KeyError,TypeError) as error:
        raise RuntimeError('候选包清单校验失败：'+str(error)) from error

def main():
    p=argparse.ArgumentParser();p.add_argument('action',choices=['apply','rollback'])
    p.add_argument('--stage',type=Path,help='apply 时使用的完整候选包目录及发布清单')
    args=p.parse_args()
    if subprocess.run(['/usr/bin/pgrep','-x','Chill With You'],stdout=subprocess.DEVNULL).returncode==0:
        raise RuntimeError('请先正常退出游戏，不能替换正在加载的运行库。')
    root=PROJECT/'.local/runtime-backups'
    manifest=root/'latest.json'
    if args.action=='apply':
        stage=(args.stage or STAGE).resolve()
        for rel in ['BepInEx/core/MonoMod.Core.dll',*FILES]:
            if not (stage/rel).is_file():raise RuntimeError('缺少准备好的构建产物：'+rel)
        if args.stage is not None:verify_candidate(stage)
        else:
            runtime=RUNTIME.resolve()
            if stage == runtime or stage in runtime.parents or runtime in stage.parents:
                raise RuntimeError('候选目录与现用运行目录重叠，拒绝安装。')
        root.mkdir(parents=True,exist_ok=True)
        backup=root/(time.strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex);backup.mkdir()
        shutil.copytree(RUNTIME/'BepInEx/core',backup/'core')
        absent=[]
        for rel in FILES:
            if (RUNTIME/rel).is_file():
                target=backup/rel;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(RUNTIME/rel,target)
            else:absent.append(rel)
        config_present=(RUNTIME/CONFIG_REL).is_file()
        if config_present:
            target=backup/CONFIG_REL;target.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(RUNTIME/CONFIG_REL,target)
        record={'backup':str(backup),'absent':absent,'files':FILES,'config_present':config_present}
        try:
            shutil.rmtree(RUNTIME/'BepInEx/core')
            shutil.copytree(stage/'BepInEx/core',RUNTIME/'BepInEx/core')
            for rel in FILES:shutil.copy2(stage/rel,RUNTIME/rel)
            pending_manifest=root/('latest-'+uuid.uuid4().hex+'.json')
            try:
                pending_manifest.write_text(json.dumps(record,indent=2))
                pending_manifest.replace(manifest)
            finally:
                pending_manifest.unlink(missing_ok=True)
        except Exception:
            restore(record);raise
        print('已安装原生运行库与优化版插件；配置、曲库缓存和游戏文件保持不变。')
        print('备份：',backup)
    else:
        restore(json.loads(manifest.read_text()))
        print('已恢复上一次运行库和启动脚本；用户配置与缓存保持不变。')

def restore(record):
    backup=Path(record['backup'])
    if backup.parent!=(PROJECT/'.local/runtime-backups') or not (backup/'core').is_dir():raise RuntimeError('无效备份路径。')
    files=record.get('files',LEGACY_FILES)
    if any(rel not in FILES for rel in files):raise RuntimeError('无效备份文件列表。')
    for rel in files:
        if rel not in record['absent'] and not (backup/rel).is_file():raise RuntimeError('备份不完整：'+rel)
    if record.get('config_present') and not (backup/CONFIG_REL).is_file():raise RuntimeError('配置备份不完整。')
    if 'config_present' in record:
        current=RUNTIME/CONFIG_REL
        if current.is_file():
            saved=backup/('config-before-rollback-'+time.strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex+'.json')
            shutil.copy2(current,saved)
    shutil.rmtree(RUNTIME/'BepInEx/core')
    shutil.copytree(backup/'core',RUNTIME/'BepInEx/core')
    for rel in files:
        if rel in record['absent']:(RUNTIME/rel).unlink(missing_ok=True)
        else:shutil.copy2(backup/rel,RUNTIME/rel)
    if 'config_present' in record:
        current=RUNTIME/CONFIG_REL
        if record['config_present']:
            current.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(backup/CONFIG_REL,current)
        else:current.unlink(missing_ok=True)

if __name__=='__main__':
    try:main()
    except (RuntimeError,OSError) as e:raise SystemExit(str(e))
