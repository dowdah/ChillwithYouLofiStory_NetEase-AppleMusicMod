import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


MODULE = Path(__file__).with_name("runtime_update.py")
spec = importlib.util.spec_from_file_location("runtime_update_under_test", MODULE)
runtime_update = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime_update)


class RollbackTests(unittest.TestCase):
    def test_failed_apply_restores_runtime_and_keeps_previous_rollback_target(self):
        old_project, old_runtime, old_stage = (
            runtime_update.PROJECT, runtime_update.RUNTIME, runtime_update.STAGE)
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            runtime = project / "runtime"
            stage = project / ".local/native-staging"
            runtime_update.PROJECT, runtime_update.RUNTIME, runtime_update.STAGE = project, runtime, stage
            try:
                for root, version in ((runtime, b"old"), (stage, b"new")):
                    core = root / "BepInEx/core"
                    core.mkdir(parents=True)
                    (core / "MonoMod.Core.dll").write_bytes(version)
                    for rel in runtime_update.FILES:
                        file = root / rel
                        file.parent.mkdir(parents=True, exist_ok=True)
                        file.write_bytes(version)
                manifest = project / ".local/runtime-backups/latest.json"
                manifest.parent.mkdir(parents=True)
                manifest.write_text("previous rollback target")
                copy2 = runtime_update.shutil.copy2

                def fail_new_plugin(source, destination, *args, **kwargs):
                    if Path(source) == stage / runtime_update.FILES[0]:
                        raise OSError("simulated interrupted deployment")
                    return copy2(source, destination, *args, **kwargs)

                with patch.object(sys, "argv", ["runtime_update.py", "apply"]), \
                     patch.object(runtime_update.subprocess, "run", return_value=SimpleNamespace(returncode=1)), \
                     patch.object(runtime_update.shutil, "copy2", side_effect=fail_new_plugin):
                    with self.assertRaisesRegex(OSError, "simulated interrupted deployment"):
                        runtime_update.main()
                self.assertEqual((runtime / "BepInEx/core/MonoMod.Core.dll").read_bytes(), b"old")
                for rel in runtime_update.FILES:
                    self.assertEqual((runtime / rel).read_bytes(), b"old")
                self.assertEqual(manifest.read_text(), "previous rollback target")
            finally:
                runtime_update.PROJECT, runtime_update.RUNTIME, runtime_update.STAGE = (
                    old_project, old_runtime, old_stage)

    def test_full_apply_and_rollback_preserve_data_and_matching_files(self):
        old_project, old_runtime, old_stage = (
            runtime_update.PROJECT, runtime_update.RUNTIME, runtime_update.STAGE)
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            runtime = project / "runtime"
            stage = project / ".local/native-staging"
            runtime_update.PROJECT, runtime_update.RUNTIME, runtime_update.STAGE = project, runtime, stage
            try:
                (runtime / "BepInEx/core").mkdir(parents=True)
                (stage / "BepInEx/core").mkdir(parents=True)
                (runtime / "BepInEx/core/BepInEx.dll").write_bytes(b"old-core")
                (stage / "BepInEx/core/BepInEx.dll").write_bytes(b"new-core")
                (stage / "BepInEx/core/MonoMod.Core.dll").write_bytes(b"new-monomod")
                old_files = set(runtime_update.FILES[:4])
                for rel in runtime_update.FILES:
                    source = stage / rel
                    source.parent.mkdir(parents=True, exist_ok=True)
                    source.write_bytes(b"new:" + rel.encode())
                    if rel in old_files:
                        target = runtime / rel
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes(b"old:" + rel.encode())
                config = runtime / runtime_update.CONFIG_REL
                config.parent.mkdir(parents=True, exist_ok=True)
                config.write_text('{"SchemaVersion":1,"Netease":{"RepeatQueue":false}}')
                cache = runtime / "BepInEx/plugins/ChillWithYouMusicBridge/cache/keep.bin"
                cache.parent.mkdir(parents=True, exist_ok=True)
                cache.write_bytes(b"keep-cache")
                with patch.object(sys, "argv", ["runtime_update.py", "apply"]), \
                     patch.object(runtime_update.subprocess, "run", return_value=SimpleNamespace(returncode=1)):
                    runtime_update.main()
                self.assertEqual((runtime / "BepInEx/core/BepInEx.dll").read_bytes(), b"new-core")
                for rel in runtime_update.FILES:
                    self.assertEqual((runtime / rel).read_bytes(), b"new:" + rel.encode())
                self.assertIn('"SchemaVersion":1', config.read_text())
                self.assertEqual(cache.read_bytes(), b"keep-cache")
                config.write_text('{"SchemaVersion":2,"Netease":{"NoRepeatShuffle":true}}')
                with patch.object(sys, "argv", ["runtime_update.py", "rollback"]), \
                     patch.object(runtime_update.subprocess, "run", return_value=SimpleNamespace(returncode=1)):
                    runtime_update.main()
                self.assertEqual((runtime / "BepInEx/core/BepInEx.dll").read_bytes(), b"old-core")
                for rel in runtime_update.FILES:
                    target = runtime / rel
                    if rel in old_files:
                        self.assertEqual(target.read_bytes(), b"old:" + rel.encode())
                    else:
                        self.assertFalse(target.exists())
                self.assertIn('"SchemaVersion":1', config.read_text())
                self.assertEqual(cache.read_bytes(), b"keep-cache")
                manifest = project / ".local/runtime-backups/latest.json"
                backup = Path(json.loads(manifest.read_text())["backup"])
                saved = list(backup.glob("config-before-rollback-*.json"))
                self.assertEqual(len(saved), 1)
                self.assertIn('"SchemaVersion":2', saved[0].read_text())
            finally:
                runtime_update.PROJECT, runtime_update.RUNTIME, runtime_update.STAGE = (
                    old_project, old_runtime, old_stage)

    def test_paired_binaries_and_v1_config_restore(self):
        old_project, old_runtime = runtime_update.PROJECT, runtime_update.RUNTIME
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            runtime = project / "runtime"
            backup = project / ".local/runtime-backups/one"
            runtime_update.PROJECT, runtime_update.RUNTIME = project, runtime
            files = [
                "BepInEx/plugins/ChillWithYouMusicBridge/MusicBridge.Plugin.dll",
                "BepInEx/plugins/ChillWithYouMusicBridge/libmusicbridge_media.dylib",
            ]
            try:
                for root, version in ((runtime, b"new"), (backup, b"old")):
                    core = root / ("BepInEx/core" if root == runtime else "core")
                    core.mkdir(parents=True)
                    (core / "BepInEx.dll").write_bytes(version)
                    for rel in files:
                        path = root / rel
                        path.parent.mkdir(parents=True, exist_ok=True)
                        path.write_bytes(version)
                v1 = backup / runtime_update.CONFIG_REL
                v2 = runtime / runtime_update.CONFIG_REL
                v1.parent.mkdir(parents=True, exist_ok=True)
                v2.parent.mkdir(parents=True, exist_ok=True)
                v1.write_text(json.dumps({"SchemaVersion": 1}))
                v2.write_text(json.dumps({"SchemaVersion": 2}))
                runtime_update.restore({"backup": str(backup), "absent": [],
                                        "files": files, "config_present": True})
                for rel in files:
                    self.assertEqual((runtime / rel).read_bytes(), b"old")
                self.assertEqual(json.loads(v2.read_text())["SchemaVersion"], 1)
                saved = list(backup.glob("config-before-rollback-*.json"))
                self.assertEqual(len(saved), 1)
                self.assertEqual(json.loads(saved[0].read_text())["SchemaVersion"], 2)
            finally:
                runtime_update.PROJECT, runtime_update.RUNTIME = old_project, old_runtime


if __name__ == "__main__":
    unittest.main()
