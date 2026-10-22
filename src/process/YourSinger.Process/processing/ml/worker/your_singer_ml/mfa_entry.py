from __future__ import annotations

import importlib.metadata
import os
import sys
from pathlib import Path


# {
#   責務: [main: 専用Conda runtimeで単一音声のMFA CLIを起動する]
#   処理: [1: DLLと同梱補助モジュールの探索先を固定する 2: 固定版MFAのalign_oneを起動する]
#   エラー: [runtimeの版が異なる場合や音声解析に失敗した場合に失敗する]
# }
def main() -> None:
    runtime = Path(sys.prefix)
    overlay = runtime.parent / "overlay"
    if overlay.is_dir():
        sys.path.insert(0, str(overlay))
    # Condaのactivateを利用者へ要求せず、配置先のDLLを直接参照する。
    bin_directory = runtime / "Library" / "bin"
    dll_handle = os.add_dll_directory(str(bin_directory)) if os.name == "nt" else None
    os.environ["PATH"] = os.pathsep.join((str(bin_directory), str(runtime / "Scripts"), str(runtime), os.environ.get("PATH", "")))
    if importlib.metadata.version("montreal-forced-aligner") != "3.4.2":
        raise RuntimeError("MFA 3.4.2が必要です。")
    from montreal_forced_aligner.command_line.align_one import align_one_cli
    try:
        align_one_cli()
    finally:
        if dll_handle is not None:
            dll_handle.close()


if __name__ == "__main__":
    main()
