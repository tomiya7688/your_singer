from __future__ import annotations

import json
import sys
import traceback

from .preprocessing import preprocess_audio


def _write(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main() -> None:
    line = sys.stdin.readline()
    if not line:
        return

    try:
        request = json.loads(line)
        request_id = str(request["request_id"])
        command = str(request["command"])
        payload = request.get("payload") or {}

        if command == "preprocess_audio":
            result = preprocess_audio(payload)
        else:
            raise ValueError(f"未対応のworkerコマンドです: {command}")

        _write(
            {
                "request_id": request_id,
                "status": "ok",
                "result": result,
                "error": None,
            }
        )
    except Exception as exc:  # worker境界では例外をJSONへ正規化する
        traceback.print_exc(file=sys.stderr)
        _write(
            {
                "request_id": locals().get("request_id", "unknown"),
                "status": "error",
                "result": None,
                "error": str(exc),
            }
        )


if __name__ == "__main__":
    main()
