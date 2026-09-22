from __future__ import annotations

import io
import json
import os
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

REPOSITORY = Path(__file__).resolve().parents[2]
WORKER = REPOSITORY / "src/process/YourSinger.Process/processing/ml/worker"
WORKER = Path(os.environ.get("YOURSINGER_WORKER_SOURCE", str(WORKER)))
sys.path.insert(0, str(WORKER))
from your_singer_ml import main as worker  # noqa: E402


class WorkerProtocolTests(unittest.TestCase):
    def invoke(self, request: str, handler=None, import_error=None):
        output, errors = io.StringIO(), io.StringIO()
        module = SimpleNamespace(classify_content=handler or (lambda payload: payload))
        with patch.object(sys, "stdin", io.StringIO(request)), patch.object(sys, "stdout", output), \
             patch.object(sys, "stderr", errors), patch.object(worker.importlib, "import_module",
                 return_value=module, side_effect=import_error):
            worker.main()
        return output.getvalue(), errors.getvalue()

    def request(self, **changes):
        value = {"request_id": "日本語要求", "command": "classify_content", "payload": {"text": "こんにちは"}}
        value.update(changes)
        return json.dumps(value, ensure_ascii=False) + "\n"

    def test_success_is_exactly_one_json_line(self):
        output, _ = self.invoke(self.request())
        self.assertEqual(len(output.splitlines()), 1)
        response = json.loads(output)
        self.assertEqual(response["status"], "ok")
        self.assertEqual(response["request_id"], "日本語要求")
        self.assertEqual(response["result"], {"text": "こんにちは"})

    def test_library_logs_use_stderr(self):
        def noisy(payload):
            print("ライブラリの進捗ログ")
            return payload
        output, errors = self.invoke(self.request(), handler=noisy)
        self.assertEqual(json.loads(output)["status"], "ok")
        self.assertNotIn("進捗ログ", output)
        self.assertIn("進捗ログ", errors)

    def test_missing_dependency_is_structured_error(self):
        output, _ = self.invoke(self.request(), import_error=ModuleNotFoundError("不足する依存"))
        response = json.loads(output)
        self.assertEqual(response["status"], "error")
        self.assertEqual(response["request_id"], "日本語要求")
        self.assertIsNone(response["result"])

    def test_malformed_json_and_payload_are_errors(self):
        for request in ("{\n", "[]\n", self.request(payload=[]), self.request(request_id=123), self.request(command="unknown")):
            with self.subTest(request=request):
                output, _ = self.invoke(request)
                self.assertEqual(json.loads(output)["status"], "error")
                self.assertEqual(len(output.splitlines()), 1)

    def test_nonfinite_result_does_not_emit_invalid_json(self):
        output, _ = self.invoke(self.request(), handler=lambda payload: {"bad": float("nan")})
        self.assertEqual(json.loads(output)["status"], "error")
        self.assertEqual(len(output.splitlines()), 1)

    def test_handler_failure_preserves_request_id(self):
        def fail(payload):
            raise RuntimeError("処理失敗")
        output, _ = self.invoke(self.request(), handler=fail)
        self.assertEqual(json.loads(output)["request_id"], "日本語要求")
        self.assertEqual(json.loads(output)["error"], "処理失敗")

    def test_empty_input_has_no_response(self):
        output, _ = self.invoke("")
        self.assertEqual(output, "")

    def test_unknown_command_works_without_ml_dependencies_in_subprocess(self):
        env = dict(os.environ, PYTHONPATH=str(WORKER))
        result = subprocess.run([sys.executable, "-m", "your_singer_ml.main"],
            input=self.request(command="unknown"), capture_output=True,
            encoding="utf-8", env=dict(env, PYTHONIOENCODING="utf-8"), timeout=10, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["status"], "error")
        self.assertEqual(len(result.stdout.splitlines()), 1)


if __name__ == "__main__":
    unittest.main()
