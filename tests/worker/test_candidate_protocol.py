import contextlib
import io
import json
import types
import unittest
from unittest.mock import patch
from your_singer_ml import main


class CandidateProtocolTests(unittest.TestCase):
    def invoke(self, request, handler):
        output, errors = io.StringIO(), io.StringIO()
        with patch('sys.stdin', io.StringIO(request)), contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            with patch.object(main.importlib, 'import_module', return_value=types.SimpleNamespace(generate_phoneme_candidate=handler)):
                main.main()
        return output.getvalue(), errors.getvalue()

    def test_success_has_single_json_and_logs_are_separate(self):
        def handler(payload):
            print('ライブラリログ')
            return {'text': '日本語'}
        output, errors = self.invoke(json.dumps({'request_id':'r','command':'generate_phoneme_candidate','payload':{}}), handler)
        self.assertEqual(1, len(output.splitlines()))
        self.assertEqual('ok', json.loads(output)['status'])
        self.assertIn('ライブラリログ', errors)

    def test_missing_models_remain_structured_error(self):
        def handler(payload): raise FileNotFoundError('モデル未配置')
        output, _ = self.invoke(json.dumps({'request_id':'r','command':'generate_phoneme_candidate','payload':{}}), handler)
        self.assertEqual('error', json.loads(output)['status'])
        self.assertEqual('r', json.loads(output)['request_id'])

    def test_nonfinite_result_is_error(self):
        output, _ = self.invoke(json.dumps({'request_id':'r','command':'generate_phoneme_candidate','payload':{}}), lambda p: {'v':float('nan')})
        self.assertEqual('error', json.loads(output)['status'])

    def test_invalid_payload_is_error(self):
        output, _ = self.invoke(json.dumps({'request_id':'r','command':'generate_phoneme_candidate','payload':[]}), lambda p: {})
        self.assertEqual('error', json.loads(output)['status'])
