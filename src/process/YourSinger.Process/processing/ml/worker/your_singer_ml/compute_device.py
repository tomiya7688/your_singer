from __future__ import annotations


# {
#   責務: [pick_device: cudaが使えればGPU、無ければCPUを返す(CPU実行を引き続き維持する)]
#   処理: [1: torchの取り込みを試す 2: cuda利用可否を問い合わせる 3: 失敗時はcpuにする]
#   引数: [torch_module: 差し替え用のtorch互換オブジェクト。Noneなら実物を使う]
#   戻り値: ["cuda" または "cpu"]
#   エラー: [torch未導入や問い合わせ失敗は例外にせずcpuへ落とす]
# }
def pick_device(torch_module=None) -> str:
    try:
        torch = torch_module
        if torch is None:
            import torch
        return "cuda" if torch.cuda.is_available() else "cpu"
    except Exception:
        return "cpu"


# {
#   責務: [pick_precision: GPUの能力から混合精度の種類を選ぶ(GTX1080等のbf16非対応GPUはfp16)]
#   処理: [1: 要求がfp32ならfp32 2: cpuならfp32 3: autoならbf16対応でbf16、非対応ならfp16]
#   引数: [device: "cuda"または"cpu"。requested: "auto"|"fp16"|"bf16"|"fp32"。torch_module: 差し替え用]
#   戻り値: ["fp16" | "bf16" | "fp32"]
# }
def pick_precision(device: str, requested: str = "auto", torch_module=None) -> str:
    if requested == "fp32" or device != "cuda":
        return "fp32"
    if requested in ("fp16", "bf16"):
        return requested
    try:
        torch = torch_module
        if torch is None:
            import torch
        return "bf16" if torch.cuda.is_bf16_supported() else "fp16"
    except Exception:
        return "fp16"


# {
#   責務: [plan_training: SBV2学習の精度・勾配累積・勾配チェックポイントを決める(実効バッチは維持する)]
#   処理: [1: 設定の既定値を補う 2: デバイスと精度を決める 3: 目標実効バッチをmicroバッチ×累積で満たす]
#   引数: [request: request.jsonの辞書(training_compute: precision, micro_batch_size, effective_batch_size, gradient_checkpointing, force_device)。torch_module: 差し替え用]
#   戻り値: [device, precision, micro_batch_size, grad_accum_steps, effective_batch_size, gradient_checkpointing を持つ辞書]
# }
def plan_training(request: dict, torch_module=None) -> dict:
    settings = request.get("training_compute") or {}
    device = settings.get("force_device") or pick_device(torch_module)
    precision = pick_precision(device, settings.get("precision", "auto"), torch_module)
    effective = max(1, int(settings.get("effective_batch_size", 2)))
    micro = max(1, min(effective, int(settings.get("micro_batch_size", effective))))
    # 端数が出る場合は累積回数を切り上げ、実効バッチを下回らせない。
    accumulation = -(-effective // micro)
    return {
        "device": device,
        "precision": precision,
        "micro_batch_size": micro,
        "grad_accum_steps": accumulation,
        "effective_batch_size": micro * accumulation,
        "gradient_checkpointing": bool(settings.get("gradient_checkpointing", False)),
    }


# {
#   責務: [apply_to_hyperparameters: 計画を公式config.jsonのtrainへ反映する]
#   処理: [1: batch_sizeにmicroバッチを入れる 2: fp16/bf16はbf16_run(公式の混合精度スイッチ)を有効にする 3: 追加キーを記録する]
#   引数: [train: config.jsonのtrain辞書(更新される)。plan: plan_trainingの戻り値]
#   戻り値: [なし]
#   副作用: [trainを直接更新する]
# }
def apply_to_hyperparameters(train: dict, plan: dict) -> None:
    train.update(
        batch_size=plan["micro_batch_size"],
        bf16_run=plan["precision"] in ("fp16", "bf16"),
        mixed_precision=plan["precision"],
        grad_accum_steps=plan["grad_accum_steps"],
        gradient_checkpointing=plan["gradient_checkpointing"],
    )


# {
#   責務: [fallback_to_fp32: fp16で失敗した後にfp32へ戻す設定へ書き換える]
#   引数: [train: config.jsonのtrain辞書(更新される)]
#   戻り値: [なし]
#   副作用: [bf16_runをFalse、mixed_precisionをfp32にする]
# }
def fallback_to_fp32(train: dict) -> None:
    train.update(bf16_run=False, mixed_precision="fp32")


# {
#   責務: [install_fp16_autocast: 公式学習のbf16 autocastをfp16へ置き換える(GradScalerは公式のものがbf16_runで有効になる)]
#   処理: [1: torch.autocastを包む 2: dtypeがbfloat16ならfloat16にする]
#   引数: [torch: torchモジュール]
#   戻り値: [元のautocast(復元用)]
#   副作用: [torch.autocastとtorch.cuda.amp.autocastを差し替える]
# }
def install_fp16_autocast(torch):
    original = torch.autocast

    def patched(*args, **kwargs):
        if kwargs.get("dtype") == torch.bfloat16:
            kwargs["dtype"] = torch.float16
        return original(*args, **kwargs)

    torch.autocast = patched
    return original
