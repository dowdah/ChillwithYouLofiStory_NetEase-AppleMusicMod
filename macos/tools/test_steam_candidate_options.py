import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import steam_candidate_options as c
import steam_options as s
from test_steam_options import CONFIG


class CandidateTests(unittest.TestCase):
    def test_compare_and_set_preserves_other_apps(self):
        before = s.set_option(CONFIG, 'old')
        after = c.changed(before, 'old', 'new')
        self.assertEqual(c.changed(after, 'new', 'old'), before)
        self.assertIn('"LaunchOptions" "other game"', after)
        with self.assertRaises(RuntimeError):
            c.changed(after, 'old', 'wrong')

    def test_process_check_fails_closed(self):
        for code in (0, 2, 3):
            with patch.object(c.subprocess, 'run') as run:
                run.return_value.returncode = code
                with self.assertRaises(RuntimeError):
                    c.require_stopped()

    @patch.object(c, 'require_stopped')
    def test_switch_restore_and_conflicts(self, stopped):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            old, new, config, record = [root / p for p in ('old.sh', 'new.sh', 'config.vdf', 'record.json')]
            old.touch(); new.touch()
            previous = json.dumps(str(old)) + ' --steam %command%'
            text = s.set_option(CONFIG, previous)
            config.write_text(text)
            target = c.switch(config, record, old, new)
            self.assertEqual(s.read_option(config.read_text()), target)
            self.assertEqual(record.with_suffix('.vdf').read_text(), text)
            with self.assertRaises(RuntimeError):
                c.switch(config, record, old, new)
            config.write_text(s.set_option(config.read_text(), 'external'))
            with self.assertRaises(RuntimeError):
                c.restore(record)
            config.write_text(s.set_option(config.read_text(), target).replace('other game', 'externally changed other app'))
            self.assertEqual(c.restore(record), previous)
            self.assertIn('externally changed other app', config.read_text())
            self.assertEqual(c.restore(record), previous)


if __name__ == '__main__':
    unittest.main()
