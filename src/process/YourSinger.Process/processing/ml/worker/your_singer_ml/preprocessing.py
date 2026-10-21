from __future__ import annotations

import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from .audio_quality import AudioQualityAnalyzer
from .vocal_separation import DemucsVocalSeparator
from .voice_activity_segmentation import VoiceActivitySegmenter


# {
#   責務: [
#     preprocess_audio: 入力音声を分離・整音し学習候補区間と品質情報を作る
#   ]
#   処理: [
#     1: 要求値と出力先を検証する
#     2: 歌声分離・整音・区間分割を行う
#     3: 品質情報と処理履歴を返す
#   ]
#   引数: [payload: source_id、input_path、workspace_path、stage_versionを含む要求]
#   戻り値: [音声パス、候補区間、品質情報、処理履歴を含む抽出結果]
#   エラー: [入力音声またはsource_idが不正な場合や前処理に失敗した場合に失敗する]
# }
def preprocess_audio(payload: dict) -> dict:
    # 出力ファイル名へ安全に使えるsource_idだけを受け付ける。
    source_id = str(payload["source_id"])
    if re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,127}", source_id) is None:
        raise ValueError("source_idは英数字、アンダースコア、ハイフンで構成してください。")

    # 入力と作業領域を絶対パスに正規化してから存在確認する。
    input_path = Path(payload["input_path"]).resolve()
    workspace = Path(payload["workspace_path"]).resolve()
    stage_version = str(payload["stage_version"])
    if not input_path.exists():
        raise FileNotFoundError(f"入力音声が見つかりません: {input_path}")

    # 前処理段階ごとに出力先を分ける。
    separated_dir = workspace / "separated"
    cleaned_dir = workspace / "cleaned"
    segments_dir = workspace / "segments"
    separated_dir.mkdir(parents=True, exist_ok=True)
    cleaned_dir.mkdir(parents=True, exist_ok=True)
    segments_dir.mkdir(parents=True, exist_ok=True)

    separated_path = separated_dir / f"{source_id}.wav"
    cleaned_path = cleaned_dir / f"{source_id}.wav"
    history: list[dict] = []

    # 混合音声を歌声として誤用しないよう、分離に失敗したらここで中止する。
    _separate_vocals(input_path, separated_path)
    history.append(_history("separation", "demucs-v4", separated_path, workspace))

    # 歌声トラックをノイズ低減・音量正規化して保存する。
    _clean_audio(separated_path, cleaned_path)
    history.append(
        _history("denoise_normalize", "ffmpeg-afftdn-loudnorm-v1", cleaned_path, workspace)
    )

    # 整音済み音声から学習候補区間を検出する。
    segments = VoiceActivitySegmenter.detect_and_export(
        cleaned_path,
        segments_dir,
        source_id,
        workspace,
        _run,
    )
    history.append(_history("vad_segmentation", "energy-vad-v1", segments_dir, workspace))

    # 波形指標を専用分析器へ渡し品質レポートを作る。
    quality = AudioQualityAnalyzer.analyze(
        input_path,
        separated_path,
        cleaned_path,
    )

    # 後段が利用する品質値、相対パス、処理履歴を返す。
    return {
        "source_id": source_id,
        "stage_version": stage_version,
        "raw_audio_path": _relative(input_path, workspace),
        "separated_audio_path": _relative(separated_path, workspace),
        "cleaned_audio_path": _relative(cleaned_path, workspace),
        "segments": segments,
        "quality": quality,
        "history": history,
        "error_message": None,
    }

# {
#   責務: [
#     _separate_vocals: DemucsVocalSeparatorへ歌声分離を委譲する
#   ]
#   処理: [1: 既存の前処理呼び出し点を保ったまま専用分離器を呼ぶ]
#   引数: [input_path: 分離する音声, output_path: 歌声の保存先]
#   戻り値: []
#   エラー: [歌声分離に失敗した場合に分離器から伝播する]
# }
def _separate_vocals(input_path: Path, output_path: Path) -> None:
    DemucsVocalSeparator.separate_vocals(input_path, output_path)

# {
#   責務: [_clean_audio: 歌声WAVへ帯域制限・ノイズ低減・音量正規化を適用する]
#   処理: [1: ffmpegへPCM16 mono出力を要求する]
#   引数: [input_path: 整音する歌声, output_path: 整音結果]
#   戻り値: []
#   エラー: [ffmpegが失敗した場合にRuntimeErrorを送出する]
#   副作用: [output_pathへ整音済み音声を書き込む]
# }
def _clean_audio(input_path: Path, output_path: Path) -> None:
    _run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(input_path),
            "-af",
            "highpass=f=60,lowpass=f=18000,afftdn=nf=-25,loudnorm=I=-18:LRA=11:TP=-1.5",
            "-ac",
            "1",
            "-ar",
            "48000",
            "-c:a",
            "pcm_s16le",
            str(output_path),
        ]
    )


# {
#   責務: [_run: 外部コマンドを実行し失敗内容を呼び出し元へ伝える]
#   処理: [1: 標準出力と標準エラーを取得して終了コードを確認する]
#   引数: [command: 実行するコマンドと引数]
#   戻り値: []
#   エラー: [終了コードが0以外の場合にRuntimeErrorを送出する]
# }
def _run(command: list[str]) -> None:
    completed = subprocess.run(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )

    if completed.returncode != 0:
        raise RuntimeError(
            f"外部音声処理に失敗しました: {' '.join(command[:3])}\n{completed.stderr}"
        )


# {
#   責務: [_relative: workspaceを基準に音声パスを表現する]
#   処理: [1: workspace内ならPOSIX相対パス、外なら絶対パスを返す]
#   引数: [path: 対象ファイル, workspace: 相対パスの基準]
#   戻り値: [audio_path: 相対パスまたは絶対パス]
# }
def _relative(path: Path, workspace: Path) -> str:
    try:
        return path.resolve().relative_to(workspace.resolve()).as_posix()
    except ValueError:
        return str(path.resolve())


# {
#   責務: [_history: 前処理段階の版・完了時刻・出力先を記録する]
#   処理: [1: outputをworkspace相対表記へ変換し履歴レコードを作る]
#   引数: [stage: 処理段階, version: 処理版, output: 出力パス, workspace: 相対パスの基準]
#   戻り値: [history: 保存可能な処理履歴]
# }
def _history(stage: str, version: str, output: Path, workspace: Path) -> dict:
    return {
        "stage": stage,
        "version": version,
        "completed_at": datetime.now(timezone.utc).isoformat(),
        "output_path": _relative(output, workspace),
    }
