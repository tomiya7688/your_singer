from __future__ import annotations

import contextlib
import importlib
import json
import sys
import traceback

def _configure_stdio() -> None:
    # PyInstaller化したWindows workerをPowerShell等から直接起動しても、
    # 日本語JSONとログが既定コードページへ落ちないよう明示的にUTF-8へ固定する。
    for stream, errors in ((sys.stdin, "strict"), (sys.stdout, "strict"), (sys.stderr, "backslashreplace")):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            reconfigure(encoding="utf-8", errors=errors)


COMMANDS = {
    "preprocess_audio": ("preprocessing", "preprocess_audio"),
    "analyze_speakers": ("speaker_analysis", "analyze_speakers"),
    "classify_content": ("content_classification", "classify_content"),
    "extract_universal_features": ("universal_features", "extract_universal_features"),
    "train_diffsinger": ("diffsinger_training", "train_diffsinger"),
    "train_style_bert_vits2": ("style_bert_vits2_training", "train_style_bert_vits2"),
    "generate_phoneme_candidate": ("phoneme_supplement", "generate_phoneme_candidate"),
    "phoneme_supplement_preflight": ("runtime_preflight", "check_phoneme_supplement_runtime"),
    "review_transcript": ("transcript_correction", "review_transcript"),
}


def main() -> None:
    _configure_stdio()
    line = sys.stdin.readline()
    if not line:
        return
    request_id = "unknown"
    try:
        request = json.loads(line)
        if not isinstance(request, dict) or not isinstance(request.get("request_id"), str):
            raise ValueError("要求IDがありません。")
        request_id = request["request_id"]
        command = request.get("command")
        payload = request.get("payload")
        if not isinstance(command, str) or command not in COMMANDS or not isinstance(payload, dict):
            raise ValueError("コマンドまたは要求データが不正です。")
        module, function = COMMANDS[command]
        with contextlib.redirect_stdout(sys.stderr):
            handler = getattr(importlib.import_module("." + module, __package__), function)
            result = handler(payload)
        response = json.dumps(dict(request_id=request_id, status="ok", result=result, error=None), ensure_ascii=False, allow_nan=False)
    except Exception as exception:
        traceback.print_exc(file=sys.stderr)
        response = json.dumps(dict(request_id=request_id, status="error", result=None, error=str(exception)), ensure_ascii=False, allow_nan=False)
    sys.stdout.write(response + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    main()
