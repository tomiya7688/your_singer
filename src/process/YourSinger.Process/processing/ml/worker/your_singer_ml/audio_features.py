from __future__ import annotations

import array
import json
import math
import wave
from pathlib import Path


# {
#   責務: [
#     _extract_frame_features: 音声フレームからF0・エネルギー・有声情報を抽出する
#   ]
#   処理: [
#     1: 40ミリ秒窓と10ミリ秒間隔で音声を走査する
#     2: 各フレームの音響特徴を集計する
#   ]
#   引数: [
#     samples: PCM16モノラル音声
#     sample_rate: サンプルレート
#   ]
#   戻り値: [
#     features: F0・エネルギー・有声情報の系列
#   ]
# }
def _extract_frame_features(
    samples: array.array,
    sample_rate: int,
) -> tuple[list[dict], list[dict], list[dict]]:
    frame_size = max(1, int(sample_rate * 0.040))
    hop_size = max(1, int(sample_rate * 0.010))

    f0_values: list[dict] = []
    energy_values: list[dict] = []
    voicing_values: list[dict] = []

    for start in range(0, max(1, len(samples) - frame_size + 1), hop_size):
        frame = samples[start:start + frame_size]
        if len(frame) < frame_size:
            break

        time_sec = start / sample_rate
        energy = _rms(frame) / 32768.0
        pitch, confidence = _estimate_pitch(frame, sample_rate)

        f0_values.append(
            {
                "time_sec": time_sec,
                "value": pitch or 0.0,
                "confidence": confidence,
            }
        )
        energy_values.append({"time_sec": time_sec, "value": energy})
        voicing_values.append(
            {
                "time_sec": time_sec,
                "value": 1.0 if pitch is not None and confidence >= 0.55 else 0.0,
            }
        )

    return f0_values, energy_values, voicing_values


# {
#   責務: [
#     _estimate_pitch: 自己相関から基本周波数と信頼度を推定する
#   ]
#   処理: [
#     1: 無音に近いフレームを除外する
#     2: FFT自己相関と累積二乗和で各遅延の正規化相関を求める
#     3: 最大相関の遅延から基本周波数を返す
#   ]
#   引数: [
#     frame: PCM16音声フレーム
#     sample_rate: サンプルレート
#   ]
#   戻り値: [
#     pitch: 推定F0または未検出
#     confidence: 自己相関値
#   ]
# }
def _estimate_pitch(frame: array.array, sample_rate: int) -> tuple[float | None, float]:
    if sample_rate <= 0 or len(frame) < 3 or _rms(frame) < 250.0:
        return None, 0.0

    import numpy as np

    min_hz = 60.0
    max_hz = 1200.0
    min_lag = max(1, int(sample_rate / max_hz))
    max_lag = min(len(frame) - 2, int(sample_rate / min_hz))

    if max_lag < min_lag:
        return None, 0.0
    centered = np.asarray(frame, dtype=np.float64)
    centered = centered - centered.mean()
    # ゼロ詰めにより循環相関を避け、従来の有限区間の相関分子をO(N log N)で得る。
    fft_size = 1 << (2 * len(centered) - 1).bit_length()
    spectrum = np.fft.rfft(centered, n=fft_size)
    autocorrelation = np.fft.irfft(spectrum * spectrum.conjugate(), n=fft_size)
    # 遅延ごとに異なる左右区間のエネルギーも、再走査せず累積和で正規化する。
    squared_prefix = np.concatenate(([0.0], np.cumsum(centered * centered)))
    lags = np.arange(min_lag, max_lag + 1)
    energy_product = squared_prefix[len(centered) - lags] * (squared_prefix[-1] - squared_prefix[lags])
    denominators = np.sqrt(np.maximum(energy_product, 0.0))
    correlations = np.divide(autocorrelation[lags], denominators, out=np.zeros(len(lags)), where=denominators > 1e-9)
    best_index = int(np.argmax(correlations))
    best_corr = float(correlations[best_index])
    if best_corr < 0.45:
        return None, _clamp(best_corr)
    return sample_rate / int(lags[best_index]), _clamp(best_corr)


# {
#   責務: [
#     _load_pcm16_mono: PCM16 WAVを読み込みモノラルへ変換する
#   ]
#   処理: [
#     1: WAV形式とサンプル情報を読み込む
#     2: 複数チャンネルを平均してモノラル化する
#   ]
#   引数: [path: 読み込むWAVファイル]
#   戻り値: [audio: サンプルレートとPCM16モノラル音声]
#   エラー: [PCM16以外のWAVの場合に失敗する]
# }
def _load_pcm16_mono(path: Path) -> tuple[int, array.array]:
    with wave.open(str(path), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError(f"PCM16 WAVのみ特徴抽出できます: {path}")

        sample_rate = wav.getframerate()
        channels = wav.getnchannels()
        samples = array.array("h", wav.readframes(wav.getnframes()))

    if channels == 1:
        return sample_rate, samples

    mono = array.array("h")
    for index in range(0, len(samples), channels):
        mono.append(int(sum(samples[index:index + channels]) / channels))

    return sample_rate, mono


# {
#   責務: [
#     _write_feature: 特徴系列をJSONファイルへ保存する
#   ]
#   処理: [
#     1: 保存先ディレクトリを作成する
#     2: UTF-8のコンパクトJSONを書き込む
#   ]
#   引数: [path: 保存先, values: 特徴系列, workspace: 相対パスの基準]
#   戻り値: [relative_path: workspaceからの相対パス]
# }
def _write_feature(path: Path, values: list[dict], workspace: Path) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(values, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    return _relative(path, workspace)


# {
#   責務: [_relative: workspace内のパスを相対形式にする]
#   処理: [1: 解決済みパスをworkspaceからのPOSIX相対パスへ変換する]
#   引数: [path: 対象パス, workspace: 相対パスの基準]
#   戻り値: [relative_path: POSIX形式の相対パス]
#   エラー: [pathがworkspace外の場合に失敗する]
# }
def _relative(path: Path, workspace: Path) -> str:
    return path.resolve().relative_to(workspace.resolve()).as_posix()


# {
#   責務: [_pitch_range_semitones: F0系列の外れ値を抑えて音高幅を求める]
#   処理: [1: 10・90パーセンタイル相当のF0比を半音へ変換する]
#   引数: [values: F0値のHz系列]
#   戻り値: [range: 音高幅の半音数]
# }
def _pitch_range_semitones(values: list[float]) -> float:
    positive = sorted(value for value in values if value > 0)
    if len(positive) < 2:
        return 0.0

    low = positive[max(0, int(len(positive) * 0.10) - 1)]
    high = positive[min(len(positive) - 1, int(len(positive) * 0.90))]
    if low <= 0 or high <= low:
        return 0.0

    return 12.0 * math.log2(high / low)


# {
#   責務: [_coefficient_of_variation: 値の変動係数を求める]
#   処理: [1: 平均が有効な系列を標準偏差で正規化する]
#   引数: [values: 測定値の系列]
#   戻り値: [coefficient: 標準偏差を平均で割った値]
# }
def _coefficient_of_variation(values: list[float]) -> float:
    if not values:
        return 0.0

    mean = sum(values) / len(values)
    if mean <= 1e-9:
        return 0.0

    return _stddev(values) / mean


# {
#   責務: [_stddev: 母標準偏差を求める]
#   処理: [1: 系列の平均から二乗偏差を集計する]
#   引数: [values: 測定値の系列]
#   戻り値: [deviation: 母標準偏差]
# }
def _stddev(values: list[float]) -> float:
    if len(values) < 2:
        return 0.0

    mean = sum(values) / len(values)
    return math.sqrt(sum((value - mean) ** 2 for value in values) / len(values))


# {
#   責務: [_rms: PCMサンプルの二乗平均平方根を求める]
#   処理: [1: 空系列を処理し、二乗平均平方根を計算する]
#   引数: [samples: PCMサンプル系列]
#   戻り値: [rms: 振幅の二乗平均平方根]
# }
def _rms(samples: array.array) -> float:
    if not samples:
        return 0.0
    return math.sqrt(
        sum(float(value) * float(value) for value in samples) / len(samples)
    )


# {
#   責務: [_clamp: 数値を0から1の範囲へ制限する]
#   処理: [1: floatへ変換し下限と上限を適用する]
#   引数: [value: 制限する数値]
#   戻り値: [clamped: 0以上1以下の数値]
# }
def _clamp(value: float) -> float:
    return max(0.0, min(1.0, float(value)))
