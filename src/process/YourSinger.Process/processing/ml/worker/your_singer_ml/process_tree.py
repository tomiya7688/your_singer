from __future__ import annotations

import json
import os
import signal
import subprocess
import time
from pathlib import Path
from types import SimpleNamespace

KILL_GRACE_SECONDS = 5


# {
#   責務: [kill_tree: 指定プロセスとその子・孫プロセスをすべて終了する]
#   処理: [Windows は taskkill /T /F、POSIX はプロセスグループへ SIGTERM、猶予後に SIGKILL]
#   引数: [process: 起動したTrainerのPopen, grace_seconds: SIGTERMから強制終了までの猶予]
# }
def kill_tree(process: subprocess.Popen, grace_seconds: float = KILL_GRACE_SECONDS) -> None:
    if os.name == "nt":
        # 親が既に終了していても、残った子孫を /T で巻き込めない場合があるため、生存中のみ試みる。
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            capture_output=True,
            check=False,
        )
    else:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        deadline = time.monotonic() + grace_seconds
        while time.monotonic() < deadline and _group_alive(process.pid):
            time.sleep(0.05)
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=grace_seconds)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def _group_alive(group: int) -> bool:
    try:
        os.killpg(group, 0)
        return True
    except ProcessLookupError:
        return False


# {
#   責務: [run_reclaiming: Trainerを新しいプロセスグループで実行し、終了・取消・タイムアウト・中断のいずれでも子孫を残さない]
#   処理: [
#     1: 新しいプロセスグループ(Windows: CREATE_NEW_PROCESS_GROUP、POSIX: 新しいセッション)で起動する
#     2: timeout まで待つ。タイムアウト・例外・中断(KeyboardInterrupt/SystemExit)では子孫ごと終了してから再送出する
#     3: 正常終了後もPOSIXでは残った子孫を回収する
#   ]
#   引数: [command: 引数列(シェルを経由しない), timeout: 秒(Noneは無期限), 他: Popenへそのまま渡す(cwd・env・stdout・stderr・text・encoding・errors)]
#   戻り値: [result: returncode を持つ値]
#   エラー: [タイムアウトは TimeoutError、その他は元の例外(子孫の回収後)]
# }
def run_reclaiming(
    command, *, timeout: float | None = None, check: bool = False, **popen_arguments
):
    if os.name == "nt":
        popen_arguments["creationflags"] = (
            popen_arguments.get("creationflags", 0) | subprocess.CREATE_NEW_PROCESS_GROUP
        )
    else:
        popen_arguments["start_new_session"] = True
    process = subprocess.Popen(command, **popen_arguments)
    try:
        returncode = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired as error:
        kill_tree(process)
        raise TimeoutError(
            f"Trainerが{timeout:g}秒以内に終わらなかったため、子プロセスを含めて停止しました。"
        ) from error
    except BaseException:
        kill_tree(process)
        raise
    if os.name != "nt":
        # 正常終了したTrainerが残した子孫を回収する(グループが無ければ何もしない)。
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    if check and returncode != 0:
        raise subprocess.CalledProcessError(returncode, command)
    return SimpleNamespace(returncode=returncode)


# {
#   責務: [record_failure: 失敗・取消・タイムアウトの種別を、診断用に出力先へ残す(学習成功としては公開しない)]
#   引数: [output_dir: ジョブの出力先, kind: canceled / timeout / failed, message: 利用者向けの理由]
#   補足: [result.json を書かないため、Exporterは成果物として扱えない。部分ログは trainer.log に残る]
# }
def record_failure(output_dir: Path, kind: str, message: str) -> None:
    try:
        output_dir.mkdir(parents=True, exist_ok=True)
        (output_dir / "run_failure.json").write_text(
            json.dumps(dict(kind=kind, message=message), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
    except OSError:
        # 記録に失敗しても、元の失敗理由を隠さない。
        pass


# {
#   責務: [classify: 例外を失敗の種別(canceled / timeout / failed)にする]
# }
def classify(error: BaseException) -> str:
    if isinstance(error, (KeyboardInterrupt, SystemExit)):
        return "canceled"
    if isinstance(error, TimeoutError):
        return "timeout"
    return "failed"
