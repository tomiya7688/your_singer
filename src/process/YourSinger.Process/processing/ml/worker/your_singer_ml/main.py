from __future__ import annotations

import importlib
import json
import sys
import traceback
from contextlib import redirect_stdout

# コマンド選択後にだけML依存を読み込む。未導入時もJSONのエラー応答を返す。
_COMMANDS = {
    "preprocess_audio": ("preprocessing", "preprocess_audio"),
    "analyze_speakers": ("speaker_analysis", "analyze_speakers"),
    "classify_content": ("content_classification", "classify_content"),
    "extract_universal_features": ("universal_features", "extract_universal_features"),
    "train_diffsinger": ("diffsinger_training", "train_diffsinger"),
    "train_style_bert_vits2": ("style_bert_vits2_training", "train_style_bert_vits2"),
}


def _write(payload: dict) -> None:
    # シリアライズを完了してから書き込み、不正なNaNや半端なJSONを出力しない。
    line = json.dumps(payload, ensure_ascii=False, allow_nan=False)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def main() -> None:
    line = sys.stdin.readline()
    if not line:
        return
    request_id = "unknown"
    try:
        request = json.loads(line)
        if not isinstance(request, dict):
            raise ValueError("要求はJSONオブジェクトで指定してください。")
        candidate_id = request.get("request_id")
        if not isinstance(candidate_id, str) or not candidate_id.strip():
            raise ValueError("request_idには空でない文字列を指定してください。")
        request_id = candidate_id
        command = request.get("command")
        if not isinstance(command, str) or command not in _COMMANDS:
            raise ValueError(f"未対応のワーカーコマンドです: {command}")
        payload = request.get("payload", {})
        if not isinstance(payload, dict):
            raise ValueError("payloadはJSONオブジェクトで指定してください。")
        module_name, function_name = _COMMANDS[command]
        # ライブラリのprintやインポート時ログでJSON Lines通信を壊さない。
        with redirect_stdout(sys.stderr):
            module = importlib.import_module(f".{module_name}", package=__package__)
            result = getattr(module, function_name)(payload)
        _write({"request_id": request_id, "status": "ok", "result": result, "error": None})
    except Exception as exc:
        traceback.print_exc(file=sys.stderr)
        _write({"request_id": request_id, "status": "error", "result": None, "error": str(exc)})


if __name__ == "__main__":
    main()
