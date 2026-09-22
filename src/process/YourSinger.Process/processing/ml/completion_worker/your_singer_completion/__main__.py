from __future__ import annotations

import contextlib
import json
import sys
import traceback

from .candidates import generate_candidates


def main() -> None:
    protocol = sys.stdout
    request_id = "unknown"
    try:
        line = sys.stdin.readline(262145)
        if not line:
            return
        if len(line) > 262144:
            raise ValueError("補完要求が大きすぎます。")
        request = json.loads(line)
        if not isinstance(request, dict):
            raise ValueError("補完要求の形式が不正です。")
        request_id = request.get("request_id")
        if not isinstance(request_id, str) or not 1 <= len(request_id) <= 128:
            request_id = "unknown"
            raise ValueError("要求IDが不正です。")
        if request.get("command") != "generate_phoneme_candidates":
            raise ValueError("未対応の補完コマンドです。")
        # モデルのimport時・推論時のPythonログをJSON通信から分離する。
        with contextlib.redirect_stdout(sys.stderr):
            result = generate_candidates(request.get("payload"))
        response = {"request_id": request_id, "status": "ok", "result": result, "error": None}
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        response = {"request_id": request_id, "status": "error", "result": None, "error": str(error)}
    protocol.write(json.dumps(response, ensure_ascii=False, allow_nan=False) + "\n")
    protocol.flush()


if __name__ == "__main__":
    main()
