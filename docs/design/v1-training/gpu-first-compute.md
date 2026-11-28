# GPU優先の学習・重い推論

- 目的: 学習と重い推論を、GPUが使えるときはGPUで実行し、使えない環境ではCPUで動かす。
- 状態: コード実装済み・単体テスト済み。実機のGPU学習は未確認。
- 最終確認日: 2026-11-28
- 関連: [学習モードとバックエンド](training-modes-and-backends.md)

## 構成

実装は `your_singer_ml/compute_device.py` に集約します。

| 機能 | 動作 |
| --- | --- |
| デバイス選択 `pick_device` | cudaが使えればGPU、無ければCPU。補完のWhisper・TTS・話者埋め込みが利用します。 |
| 精度選択 `pick_precision` | GPUでは既定fp16。bf16対応GPUはbf16。CPUはfp32。 |
| 学習計画 `plan_training` | `request.json` の `training_compute` で精度・microバッチ・実効バッチ・勾配チェックポイントを指定します。実効バッチは勾配累積回数で維持します。 |
| fp32フォールバック | `run_training_with_fallback` が混合精度の学習失敗時に1回だけfp32で再実行します。 |

## 制約・未決事項

- 公式 `train_ms.py` は勾配累積と勾配チェックポイントを持ちません。`grad_accum_steps` と `gradient_checkpointing` はconfigへ記録するだけで、効かせるには公式ソースの限定パッチが必要です(未実装)。
- fp16はtorch.autocastのdtypeをbfloat16からfloat16へ差し替えて実現します。公式のGradScalerは `bf16_run` で有効になります。
- 補完のQwenはVRAM不足時に `device_map="auto"` でCPUへ一部オフロードします(accelerate必須、実機未確認)。
- `speaker_analysis.py` と `transcription_alignment.py` は元からcuda自動選択のため変更していません。
