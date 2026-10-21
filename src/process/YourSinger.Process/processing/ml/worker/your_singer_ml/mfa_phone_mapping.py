from __future__ import annotations


# {
#   責務: [MfaPhoneSequenceConverter: MFAの音素列を既知の表記差だけ変換する]
#   処理: [1: 音素数と各音素を検証し、対応後の列が期待列と一致するか確認する]
# }
class MfaPhoneSequenceConverter:
    _PHONE_ALIASES = {
        "ɡ": "g",
        "ɾ": "r",
    }

    # {
    #   責務: [convert: MFA音素列を期待する学習用音素列と照合する]
    #   処理: [
    #     1: 入力の型と音素数を検証する
    #     2: MFA音素を同一音素の既知表記へ変換する
    #     3: 変換後の列が期待列と完全一致することを確認する
    #   ]
    #   引数: [
    #     mfa_phones: MFAのforced alignmentが返した音素列
    #     expected_phones: 歌詞からpyopenjtalkで生成した音素列
    #   ]
    #   戻り値: [phones: 期待列と一致した音素列]
    #   エラー: [未対応の表記、空音素、または音素列の不一致がある場合に失敗する]
    # }
    @classmethod
    def convert(
        cls,
        mfa_phones: list[str],
        expected_phones: list[str],
    ) -> list[str]:
        # 空列や音素数の差を先に拒否し、時刻とラベルの対応を保つ。
        if not mfa_phones or len(mfa_phones) != len(expected_phones):
            raise ValueError("MFA音素列と期待する音素列の長さが一致しません。")

        # 空白を含む値は単一音素ではないため、区切り誤りとして扱う。
        if any(
            not isinstance(phone, str) or not phone or any(char.isspace() for char in phone)
            for phone in [*mfa_phones, *expected_phones]
        ):
            raise ValueError("音素列に空または不正な音素があります。")

        # MFAの記号を許可済みの同一音素表記へ変換する。
        converted_phones = [
            cls._PHONE_ALIASES.get(phone, phone)
            for phone in mfa_phones
        ]

        # 既知の表記差以外は一音素ずつ照合し、未対応の音素を通さない。
        if converted_phones != expected_phones:
            raise ValueError("MFA音素列を期待する音素列へ安全に対応づけられません。")

        return converted_phones
