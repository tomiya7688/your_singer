from __future__ import annotations

import os
import stat
import sys
from pathlib import Path
from typing import BinaryIO


# {
#   責務: [open_verified_file: 開いた入力が検査済みパスの通常ファイルであることを確認する]
#   引数: [path: 解決済みの入力パス]
#   戻り値: [stream: 読取専用の開いた入力]
#   エラー: [特殊ファイル、差し替え、実体を確認できない環境を拒否する]
# }
def open_verified_file(path: Path) -> BinaryIO:
    path = normalize_existing_path(path)
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NONBLOCK", 0)
    descriptor = os.open(path, flags)
    try:
        if not stat.S_ISREG(os.fstat(descriptor).st_mode):
            raise ValueError("入力には通常ファイルが必要です。")
        actual = _opened_path(descriptor)
        if os.path.normcase(os.path.abspath(actual)) != os.path.normcase(os.path.abspath(path)):
            raise ValueError("入力が検査後に差し替えられました。")
        return os.fdopen(descriptor, "rb")
    except BaseException:
        os.close(descriptor)
        raise


# { 責務: [normalize_existing_path: リンクを解決せずWindowsの短縮名を展開する] }
def normalize_existing_path(path: Path) -> Path:
    full = os.path.abspath(path)
    if sys.platform != "win32":
        return Path(full)
    import ctypes
    from ctypes import wintypes

    query = ctypes.WinDLL("kernel32", use_last_error=True).GetLongPathNameW
    query.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
    query.restype = wintypes.DWORD
    prefix = "\\\\?\\"
    if full.startswith(prefix):
        extended = full
    elif full.startswith("\\\\"):
        extended = prefix + "UNC\\" + full[2:]
    else:
        extended = prefix + full
    buffer = ctypes.create_unicode_buffer(32768)
    length = query(extended, buffer, len(buffer))
    if not length:
        raise ctypes.WinError(ctypes.get_last_error())
    if length >= len(buffer):
        raise OSError("検査対象の長いパス名を確認できません。")
    return Path(_strip_windows_prefix(buffer.value))


# { 責務: [_strip_windows_prefix: Windows APIの拡張パス接頭辞を通常形式に戻す] }
def _strip_windows_prefix(path: str) -> str:
    if path.upper().startswith("\\\\?\\UNC\\"):
        return "\\\\" + path[8:]
    if path.startswith("\\\\?\\"):
        return path[4:]
    return path


# { 責務: [_opened_path: OSのファイルハンドルから現在開いている実体パスを取得する] }
def _opened_path(descriptor: int) -> str:
    if sys.platform.startswith("linux"):
        return os.path.realpath(f"/proc/self/fd/{descriptor}", strict=True)
    if sys.platform == "win32":
        import ctypes
        import msvcrt
        from ctypes import wintypes

        query = ctypes.WinDLL("kernel32", use_last_error=True).GetFinalPathNameByHandleW
        query.argtypes = (wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD)
        query.restype = wintypes.DWORD
        buffer = ctypes.create_unicode_buffer(32768)
        length = query(msvcrt.get_osfhandle(descriptor), buffer, len(buffer), 0)
        if not length or length >= len(buffer):
            raise OSError("開いた入力の実体パスを確認できません。")
        return _strip_windows_prefix(buffer.value)
    raise NotImplementedError("このOSでは入力の安全な実体確認に対応していません。")
