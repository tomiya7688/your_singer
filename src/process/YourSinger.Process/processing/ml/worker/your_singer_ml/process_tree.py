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
#   責務: [_WindowsJob: Windowsで起動したTrainerをJob Objectへ入れ、閉じた時点で子孫を含め全員を終了させる]
#   補足: [親が先に終了してもJobに属する孫は閉じるときに終了する。作成・登録に失敗したときは None を返し、従来のtaskkillだけで回収する]
# }
class _WindowsJob:
    def __init__(self, handle):
        self.handle = handle

    @classmethod
    def attach(cls, process: subprocess.Popen) -> "_WindowsJob | None":
        if os.name != "nt":
            return None
        try:
            import ctypes
            from ctypes import wintypes

            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.CreateJobObjectW.restype = wintypes.HANDLE
            handle = kernel.CreateJobObjectW(None, None)
            if not handle:
                return None

            class BasicLimits(ctypes.Structure):
                _fields_ = [
                    ("PerProcessUserTimeLimit", ctypes.c_int64),
                    ("PerJobUserTimeLimit", ctypes.c_int64),
                    ("LimitFlags", wintypes.DWORD),
                    ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t),
                    ("ActiveProcessLimit", wintypes.DWORD),
                    ("Affinity", ctypes.c_size_t),
                    ("PriorityClass", wintypes.DWORD),
                    ("SchedulingClass", wintypes.DWORD),
                ]

            class IoCounters(ctypes.Structure):
                _fields_ = [(name, ctypes.c_uint64) for name in ("a", "b", "c", "d", "e", "f")]

            class ExtendedLimits(ctypes.Structure):
                _fields_ = [
                    ("BasicLimitInformation", BasicLimits),
                    ("IoInfo", IoCounters),
                    ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t),
                    ("PeakProcessMemoryUsed", ctypes.c_size_t),
                    ("PeakJobMemoryUsed", ctypes.c_size_t),
                ]

            limits = ExtendedLimits()
            limits.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            ok = kernel.SetInformationJobObject(
                wintypes.HANDLE(handle), 9, ctypes.byref(limits), ctypes.sizeof(limits)
            )
            ok = ok and kernel.AssignProcessToJobObject(
                wintypes.HANDLE(handle), wintypes.HANDLE(int(process._handle))
            )
            if not ok:
                kernel.CloseHandle(wintypes.HANDLE(handle))
                return None
            return cls(handle)
        except (OSError, AttributeError, ValueError):
            return None

    def close(self) -> None:
        # 閉じるとKILL_ON_JOB_CLOSEにより、Jobに残る孫プロセスも終了する。
        import ctypes
        from ctypes import wintypes

        ctypes.WinDLL("kernel32").CloseHandle(wintypes.HANDLE(self.handle))


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
    job = _WindowsJob.attach(process)
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
    finally:
        # 正常終了・失敗のいずれでも、Windowsは親が先に終了した孫もここで終了させる。
        if job is not None:
            job.close()
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
