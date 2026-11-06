from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

from .run_report import sha256_file

ACOUSTIC = "acoustic"


@dataclass(frozen=True)
class InitializationPlan:
    mode: str
    overlay_path: Path
    # 起動前に確認した初期化の記録(fine_tune は hash 確認済みの重み、from_scratch は components 空)。
    staged: dict
    checkpoint_path: Path | None


# {
#   責務: [_yaml_text: 値をYAMLの二重引用符文字列にする(JSONの文字列はYAMLとして有効)]
# }
def _yaml_text(value: str) -> str:
    return json.dumps(value, ensure_ascii=True)


# {
#   責務: [prepare_initialization: 計画の学習モードと初期重みを検証し、固定DiffSingerのconfigを上書きする小さなoverlayを作る]
#   処理: [
#     1: モードと初期化情報の整合を検証する(from_scratch に重みの指定は不可、fine_tune は acoustic 1件が必須)
#     2: fine_tune は重みの存在と sha256 を起動前に照合する
#     3: 作業先(checkpoints/<exp>)に既存の ckpt があれば拒否する(固定版は最新ckptから自動再開し、初期化が無視されるため)
#     4: 元のconfigを base_config に取り、finetune_* だけを上書きする overlay を保存する
#   ]
#   引数: [payload: resolved_training_mode・initialization を含む要求, config_path: 元の学習config, output_dir: ジョブの出力先, exp_name: ジョブ名]
#   戻り値: [plan: overlay と起動前の記録。resolved_training_mode が無い旧形式の要求は None]
#   エラー: [モード・初期化情報の不整合、重みの欠損・sha256不一致、既存ckptで ValueError/FileNotFoundError(無言でscratchへ移行しない)]
#   補足: [
#     固定版 base_task.py:176 は finetune_enabled かつ work_dir に ckpt が無いときだけ初期重みを読む。
#     --hparams では finetune_ckpt_path(既定 null)に文字列を渡せない(utils/hparams.py:110)ため、overlay を使う。
#   ]
# }
def prepare_initialization(
    payload: dict, config_path: Path, output_dir: Path, exp_name: str
) -> InitializationPlan | None:
    mode = payload.get("resolved_training_mode")
    if mode is None:
        return None
    if mode not in ("fine_tune", "from_scratch"):
        raise ValueError(f"未対応の学習モードです: {mode}")
    initialization = payload.get("initialization") or {}
    components = initialization.get("components") or []
    checkpoint: Path | None = None
    if mode == "from_scratch":
        if initialization.get("type") != "random_initialization" or components:
            raise ValueError("from_scratch では初期重みを指定できません。")
        staged = dict(type="random_initialization", components=[])
        overrides = ["finetune_enabled: false", "finetune_ckpt_path: null"]
    else:
        if initialization.get("type") != "pretrained_checkpoint" or [
            item.get("name") for item in components
        ] != [ACOUSTIC]:
            raise ValueError("fine_tune には acoustic の初期重みを1件だけ指定してください。")
        checkpoint = Path(str(components[0].get("path", ""))).resolve()
        if not checkpoint.is_file() or checkpoint.stat().st_size == 0:
            raise FileNotFoundError(f"fine_tune の初期重みがありません: {checkpoint}")
        actual = sha256_file(checkpoint)
        if str(components[0].get("sha256", "")).lower() != actual:
            raise ValueError("fine_tune の初期重みのsha256が計画と一致しません。")
        staged = dict(
            type="pretrained_checkpoint",
            components=[dict(name=ACOUSTIC, sha256=actual, path=str(checkpoint))],
        )
        overrides = [
            "finetune_enabled: true",
            f"finetune_ckpt_path: {_yaml_text(str(checkpoint))}",
            "finetune_ignored_params: []",
            "finetune_strict_shapes: true",
        ]

    work_dir = output_dir / "checkpoints" / exp_name
    if work_dir.is_dir() and any(work_dir.glob("*.ckpt")):
        raise ValueError(
            "作業先に既存のcheckpointがあります。固定版は自動再開し初期化が無視されるため、新しいジョブで実行してください。"
        )
    output_dir.mkdir(parents=True, exist_ok=True)
    overlay = output_dir / "init-overlay.yaml"
    if overlay.exists():
        raise FileExistsError(f"初期化overlayが既にあります(上書きしません): {overlay}")
    overlay.write_text(
        "\n".join(
            [
                "base_config:",
                f"  - {_yaml_text(str(config_path))}",
                *overrides,
                "",
            ]
        ),
        encoding="utf-8",
    )
    return InitializationPlan(mode, overlay, staged, checkpoint)


# {
#   責務: [verify_dumped_config: 固定DiffSingerが保存した実際のconfigが、計画の初期化方式と一致することを確認する]
#   引数: [plan: 起動前の計画, output_dir: ジョブの出力先, exp_name: ジョブ名]
#   エラー: [保存configが無い、または finetune_enabled / finetune_ckpt_path が計画と違うとき RuntimeError]
#   補足: [Trainerが実際に使った設定の確認であり、重みを読み込んだ確認ではない]
# }
def verify_dumped_config(plan: InitializationPlan, output_dir: Path, exp_name: str) -> None:
    dumped = output_dir / "checkpoints" / exp_name / "config.yaml"
    if not dumped.is_file():
        raise RuntimeError(f"Trainerが保存した設定がありません: {dumped}")
    text = dumped.read_text(encoding="utf-8")
    enabled = re.search(r"^finetune_enabled:\s*(\S+)\s*$", text, re.MULTILINE)
    expected = "true" if plan.mode == "fine_tune" else "false"
    if enabled is None or enabled.group(1).lower() != expected:
        raise RuntimeError(
            f"Trainerが使った finetune_enabled が計画({expected})と違います。暗黙のfallbackは認めません。"
        )
    path = re.search(r"^finetune_ckpt_path:\s*(.+?)\s*$", text, re.MULTILINE)
    if plan.mode == "fine_tune":
        if path is None or Path(path.group(1).strip("'\"")).resolve() != plan.checkpoint_path:
            raise RuntimeError("Trainerが使った finetune_ckpt_path が計画の重みと違います。")
    elif path is not None and path.group(1).strip().lower() not in ("null", "~"):
        raise RuntimeError("from_scratch なのに finetune_ckpt_path が設定されています。")


# {
#   責務: [collect_checkpoints: 作業先の checkpoint を、出力先からの成果物として列挙する]
#   戻り値: [paths: model_ckpt_steps_*.ckpt(昇順)]
#   エラー: [1件も無い、または空のとき FileNotFoundError(終了コード0でも成功にしない)]
# }
def collect_checkpoints(output_dir: Path, exp_name: str) -> list[Path]:
    work_dir = output_dir / "checkpoints" / exp_name
    paths = sorted(work_dir.glob("model_ckpt_steps_*.ckpt")) if work_dir.is_dir() else []
    if not paths or not all(path.stat().st_size for path in paths):
        raise FileNotFoundError(
            "Trainerが終了しましたが、checkpointが出力されていません。trainer.logを確認してください。"
        )
    return paths
