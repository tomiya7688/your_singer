from __future__ import annotations

import tempfile
import threading
import wave
from functools import partial
from pathlib import Path


# {
#   責務: [
#     DemucsVocalSeparator: Demucsの保存形式を制御し、歌声ファイルを安全に分離する
#   ]
#   フィールド: [
#     _AUDIO_SAVE_LOCK: Demucs共有保存関数の一時差し替えを直列化するロック
#   ]
#   処理: [
#     1: Demucs出力を互換性のあるPCM16 WAVで保存する
#     2: 一時出力先を使い、完成した歌声ファイルだけを公開する
#   ]
# }
class DemucsVocalSeparator:
    _AUDIO_SAVE_LOCK = threading.Lock()

    # {
    #   責務: [_save_demucs_audio: Demucs出力をPCM16 WAVまたは元の保存関数へ渡す]
    #   処理: [1: WAV以外を元処理へ委譲する 2: WAVを形式検査し標準waveで保存する]
    #   引数: [path: 保存先, waveform: Demucs音声, sample_rate: サンプルレート, original_save: 元保存関数]
    #   戻り値: []
    #   エラー: [非対応形式または不正な音声形状の場合に失敗する]
    # }
    @staticmethod
    def _save_demucs_audio(
        path: str | Path,
        waveform,
        sample_rate: int,
        *,
        original_save,
        **kwargs,
    ) -> None:
        # WAV以外はDemucsが元から利用する保存実装へ委譲する。
        if Path(path).suffix.lower() != ".wav":
            original_save(path, waveform, sample_rate=sample_rate, **kwargs)
            return

        encoding = kwargs.get("encoding", "PCM_S")
        bits_per_sample = kwargs.get("bits_per_sample", 16)
        if encoding != "PCM_S" or bits_per_sample != 16:
            raise RuntimeError("Demucs出力はPCM16 WAVで保存する必要があります。")

        import torch

        samples = waveform.detach().to(device="cpu", dtype=torch.float32)
        if samples.ndim == 1:
            samples = samples.unsqueeze(0)
        if samples.ndim != 2:
            raise ValueError("Demucs出力の音声テンソルは[channels, frames]である必要があります。")

        # チャンネル別音声をwaveが要求するPCM16フレーム配列へ変換する。
        pcm = (
            samples.clamp(-1.0, 1.0)
            .mul(32768.0)
            .round()
            .clamp(-32768, 32767)
            .to(dtype=torch.int16)
            .transpose(0, 1)
            .contiguous()
        )
        with wave.open(str(path), "wb") as output:
            output.setnchannels(int(samples.shape[0]))
            output.setsampwidth(2)
            output.setframerate(int(sample_rate))
            output.writeframes(pcm.numpy().astype("<i2", copy=False).tobytes())

    # {
    #   責務: [separate_vocals: Demucsで入力音声から歌声トラックを作る]
    #   処理: [1: Demucsを確認する 2: 一意な一時領域で分離する 3: 歌声だけを出力先へ置く]
    #   引数: [input_path: 分離する音声, output_path: 歌声の保存先]
    #   戻り値: []
    #   エラー: [Demucsが利用できない場合または歌声出力が一意でない場合に失敗する]
    #   副作用: [output_pathへ分離した歌声を書き込む]
    # }
    @classmethod
    def separate_vocals(cls, input_path: Path, output_path: Path) -> None:
        try:
            from demucs import audio as demucs_audio
            from demucs import separate as demucs_separate
        except ImportError as error:
            raise RuntimeError(
                "歌声分離に必要なDemucsが利用できません。伴奏入り音声を分離済みとして扱わないため、処理を中止しました。"
            ) from error

        # 同名出力の並行実行でも既存ファイルを消さないよう親フォルダを用意する。
        output_path.parent.mkdir(parents=True, exist_ok=True)
        with cls._AUDIO_SAVE_LOCK:
            # 前処理の保存フックを退避し、Demucs実行中だけPCM16対応の保存処理へ差し替える。
            original_save = demucs_audio.ta.save
            demucs_audio.ta.save = partial(
                cls._save_demucs_audio,
                original_save=original_save,
            )
            try:
                with tempfile.TemporaryDirectory(
                    prefix=f".demucs-{output_path.stem}-",
                    dir=output_path.parent,
                ) as temporary_output_dir:
                    demucs_separate.main(
                        [
                            "--two-stems=vocals",
                            "--name=htdemucs",
                            "--out",
                            temporary_output_dir,
                            str(input_path),
                        ]
                    )

                    vocal_candidates = list(
                        Path(temporary_output_dir).glob("*/**/vocals.wav")
                    )
                    if len(vocal_candidates) != 1:
                        raise RuntimeError("Demucsは歌声ファイルを1件だけ生成する必要があります。")

                    # 一時領域と同じ親から完成済みファイルだけを置き換える。
                    vocal_candidates[0].replace(output_path)
            finally:
                demucs_audio.ta.save = original_save
