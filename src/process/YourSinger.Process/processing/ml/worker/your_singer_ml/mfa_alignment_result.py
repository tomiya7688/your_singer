from __future__ import annotations

import math


# {
#   責務: [MfaPronunciation: 内部音素と日本語MFAモデルの一音素対応を管理する]
#   フィールド: [PHONE_MAP: 境界を分割・結合しない発音表記対応]
#   処理: [1: 対応できる音素列だけを発音辞書用に変換する]
# }
class MfaPronunciation:
    PHONE_MAP = {
        **{phone: phone for phone in "a i e o k s z ts t d n h b p m w v".split()},
        "u": "ɯ", "g": "ɡ", "r": "ɾ", "y": "j", "N": "ɴ",
        "sh": "ɕ", "ch": "tɕ", "j": "dʑ", "f": "ɸ", "ky": "c", "gy": "ɟ",
        "I": "i̥", "U": "ɯ̥",
        "ny": "ɲ", "hy": "ç", "my": "mʲ", "by": "bʲ", "py": "pʲ",
        "ry": "ɾʲ", "ty": "tʲ", "dy": "dʲ",
    }

    # {
    #   責務: [encode: 音素ごとの対応を保ってMFA表記へ変換する]
    #   処理: [1: 未対応音素を拒否する 2: 同じ個数のモデル音素を返す]
    #   引数: [phonemes: 内部音素列]
    #   戻り値: [model_phones: モデルへ与える発音列]
    #   エラー: [空列や促音など未対応の音素を含む場合に失敗する]
    # }
    @classmethod
    def encode(cls, phonemes: list[str]) -> list[str]:
        if not phonemes or any(not isinstance(phone, str) or phone not in cls.PHONE_MAP for phone in phonemes):
            raise ValueError("日本語MFAへ一音素ずつ対応できない音素があります。促音などは追加対応が必要です。")
        return [cls.PHONE_MAP[phone] for phone in phonemes]


# {
#   責務: [MfaAlignmentResultReader: 実測した音素列・時刻を検証して内部音素へ戻す]
#   処理: [1: 時刻範囲・順序・音素列の完全一致を検証する]
# }
class MfaAlignmentResultReader:
    # {
    #   責務: [read: MFA JSONの音素tierから学習入力用の実測区間を読む]
    #   処理: [1: 全区間の時刻を検証する 2: 無音だけを除く 3: 音素列を照合する]
    #   引数: [document: MFA JSON, phonemes: 内部音素列, duration_sec: 音声長]
    #   戻り値: [timings: 実測時刻と未校正の信頼度0を持つ内部音素区間]
    #   エラー: [欠損、未知音素、時刻の重なり、範囲外、不一致で失敗する]
    # }
    @staticmethod
    def read(document: dict, phonemes: list[str], duration_sec: float) -> list[dict]:
        model_phones = MfaPronunciation.encode(phonemes)
        if not math.isfinite(duration_sec) or duration_sec <= 0:
            raise ValueError("音声長が不正です。")
        if not isinstance(document, dict):
            raise ValueError("MFA出力がJSON objectではありません。")
        entries = document.get("tiers", {}).get("phones", {}).get("entries")
        if not isinstance(entries, list) or not entries:
            raise ValueError("MFA出力に音素区間がありません。")

        # 無音も含めた時刻順を検証し、不正区間の除去で成功に見せない。
        voiced_entries = []
        previous_end = 0.0
        for entry in entries:
            if not isinstance(entry, list) or len(entry) != 3:
                raise ValueError("MFA音素区間の形式が不正です。")
            start, end, label = entry
            if (type(start) not in (int, float) or type(end) not in (int, float)
                    or not math.isfinite(start) or not math.isfinite(end)
                    or start < previous_end - 1e-6 or end <= start or end > duration_sec
                    or not isinstance(label, str)):
                raise ValueError("MFA音素時刻の範囲または順序が不正です。")
            previous_end = end
            if label not in ("", "sil"):
                voiced_entries.append(entry)
        if [entry[2] for entry in voiced_entries] != model_phones:
            raise ValueError("MFA実測音素列が指定した発音列と一致しません。")

        # MFA JSONは校正済みの音素信頼度を持たないため、ASRの信頼度を流用しない。
        return [
            {"phoneme": phone, "start_sec": entry[0], "end_sec": entry[1], "confidence": 0.0}
            for phone, entry in zip(phonemes, voiced_entries, strict=True)
        ]
