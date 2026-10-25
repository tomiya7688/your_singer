from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


# {
#   責務: [MfaMeasurementResult: 一回の実測音素と保存記録の同一性を呼出元へ渡す]
#   フィールド: [timings: 通常音素列, path: 保存済み記録, sha256: 記録内容hash,
#                 audio_sha256: 実測入力のhash]
# }
@dataclass(frozen=True)
class MfaMeasurementResult:
    timings: list[dict]
    path: Path
    sha256: str
    audio_sha256: str

    # {
    #   責務: [reference: プロジェクトを移動しても利用できる測定記録参照を作る]
    #   処理: [1: 領域内のパスを確認する 2: 相対パスと記録・音声のhashを返す]
    #   引数: [workspace: Voice Projectのルート]
    #   戻り値: [reference: path、sha256、audio_sha256を持つ参照]
    #   エラー: [領域外の記録はRuntimeErrorで拒否する]
    # }
    def reference(self, workspace: Path) -> dict:
        record_path = self.path.resolve()
        if not record_path.is_relative_to(workspace.resolve()):
            raise RuntimeError("MFA測定記録がVoice Projectの外です。")
        return {
            "path": record_path.relative_to(workspace.resolve()).as_posix(),
            "sha256": self.sha256,
            "audio_sha256": self.audio_sha256,
        }
