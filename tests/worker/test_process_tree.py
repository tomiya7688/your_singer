import json
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

from your_singer_ml import process_tree

# 孫プロセスを起動して自分のPIDを書き出し、待ち続ける親。
PARENT = (
    "import subprocess,sys,time;"
    "c=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);"
    "open(sys.argv[1],'w').write(str(c.pid));time.sleep(60)"
)


def alive(pid: int) -> bool:
    if sys.platform == "win32":
        out = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}"], capture_output=True, text=True
        ).stdout
        return str(pid) in out
    try:
        import os

        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


# {
#   責務: [ProcessTreeTests: タイムアウト時に子孫プロセスまで回収され、失敗種別が記録されることを確認する]
# }
class ProcessTreeTests(unittest.TestCase):
    def test_timeout_reclaims_grandchild_and_raises_timeout_error(self):
        with tempfile.TemporaryDirectory() as directory:
            pid_file = Path(directory) / "child.pid"
            with self.assertRaises(TimeoutError):
                process_tree.run_reclaiming(
                    [sys.executable, "-c", PARENT, str(pid_file)], timeout=3
                )
            self.assertTrue(pid_file.is_file())
            child = int(pid_file.read_text())
            deadline = time.monotonic() + 5
            while alive(child) and time.monotonic() < deadline:
                time.sleep(0.1)
            self.assertFalse(alive(child), "孫プロセスが残っています")

    def test_normal_exit_returns_the_exit_code(self):
        result = process_tree.run_reclaiming(
            [sys.executable, "-c", "raise SystemExit(3)"], timeout=30
        )
        self.assertEqual(result.returncode, 3)

    def test_failure_is_classified_and_recorded_without_a_result_file(self):
        self.assertEqual(process_tree.classify(KeyboardInterrupt()), "canceled")
        self.assertEqual(process_tree.classify(TimeoutError()), "timeout")
        self.assertEqual(process_tree.classify(RuntimeError()), "failed")
        with tempfile.TemporaryDirectory() as directory:
            process_tree.record_failure(Path(directory) / "out", "timeout", "理由")
            record = json.loads(
                (Path(directory) / "out" / "run_failure.json").read_text(encoding="utf-8")
            )
            self.assertEqual(record, dict(kind="timeout", message="理由"))
            self.assertFalse((Path(directory) / "out" / "result.json").exists())


if __name__ == "__main__":
    unittest.main()
