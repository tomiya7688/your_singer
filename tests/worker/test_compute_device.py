import unittest
from types import SimpleNamespace

from your_singer_ml import compute_device as cd


# { 責務: [fake_torch: cudaとbf16の可否を指定できる検証用torch互換オブジェクトを作る] 引数: [cuda: cuda可否, bf16: bf16可否] 戻り値: [SimpleNamespace] }
def fake_torch(cuda: bool, bf16: bool):
    return SimpleNamespace(cuda=SimpleNamespace(is_available=lambda: cuda, is_bf16_supported=lambda: bf16))


# {
#   責務: [ComputeDeviceTests: GPU/CPU両分岐のデバイス・精度・累積計画を確認する]
#   処理: [1: torchをモックする 2: 各分岐の戻り値を検査する]
# }
class ComputeDeviceTests(unittest.TestCase):
    def test_device_gpu_or_cpu(self):
        self.assertEqual(cd.pick_device(fake_torch(True, False)), "cuda")
        self.assertEqual(cd.pick_device(fake_torch(False, False)), "cpu")

    def test_precision_branches(self):
        self.assertEqual(cd.pick_precision("cuda", "auto", fake_torch(True, False)), "fp16")
        self.assertEqual(cd.pick_precision("cuda", "auto", fake_torch(True, True)), "bf16")
        self.assertEqual(cd.pick_precision("cpu", "auto", fake_torch(False, False)), "fp32")
        self.assertEqual(cd.pick_precision("cuda", "fp32", fake_torch(True, True)), "fp32")

    def test_plan_keeps_effective_batch_with_accumulation(self):
        request = {"training_compute": {"effective_batch_size": 8, "micro_batch_size": 3,
                                        "gradient_checkpointing": True}}
        plan = cd.plan_training(request, fake_torch(True, False))
        self.assertEqual((plan["precision"], plan["grad_accum_steps"]), ("fp16", 3))
        self.assertGreaterEqual(plan["effective_batch_size"], 8)
        self.assertTrue(plan["gradient_checkpointing"])

    def test_plan_cpu_defaults_to_fp32(self):
        plan = cd.plan_training({}, fake_torch(False, False))
        self.assertEqual((plan["device"], plan["precision"], plan["grad_accum_steps"]), ("cpu", "fp32", 1))

    def test_hyperparameters_and_fp32_fallback(self):
        train = {}
        cd.apply_to_hyperparameters(train, cd.plan_training({}, fake_torch(True, False)))
        self.assertTrue(train["bf16_run"])
        self.assertEqual(train["mixed_precision"], "fp16")
        cd.fallback_to_fp32(train)
        self.assertFalse(train["bf16_run"])
        self.assertEqual(train["mixed_precision"], "fp32")

    def test_fp16_autocast_replaces_bfloat16(self):
        calls = []
        torch = SimpleNamespace(bfloat16="bf", float16="fp", autocast=lambda *a, **k: calls.append(k))
        cd.install_fp16_autocast(torch)
        torch.autocast("cuda", dtype="bf")
        self.assertEqual(calls[0]["dtype"], "fp")


# {
#   責務: [FallbackTests: fp16の学習失敗時にfp32で再実行されることを確認する]
#   処理: [1: runpyを失敗→成功に差し替える 2: configがfp32になることを検査する]
# }
class FallbackTests(unittest.TestCase):
    def test_fp32_retry_after_fp16_failure(self):
        import json, sys, tempfile
        from pathlib import Path
        from unittest.mock import patch
        from your_singer_ml import style_bert_vits2_entry as entry

        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            (output / "data").mkdir()
            config = output / "data" / "config.json"
            config.write_text(json.dumps({"train": {"mixed_precision": "bf16"}}), encoding="utf-8")
            calls = []

            def fake_run(path, run_name):
                calls.append(1)
                if len(calls) == 1:
                    raise RuntimeError("overflow")

            with patch.object(entry.runpy, "run_path", fake_run):
                entry.run_training_with_fallback(Path(temp), output, [])
            self.assertEqual(len(calls), 2)
            self.assertEqual(json.loads(config.read_text(encoding="utf-8"))["train"]["mixed_precision"], "fp32")
