import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


MODULE = Path(__file__).with_name("runtime_update.py")
spec = importlib.util.spec_from_file_location("runtime_update_under_test", MODULE)
runtime_update = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime_update)


class RollbackTests(unittest.TestCase):
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
