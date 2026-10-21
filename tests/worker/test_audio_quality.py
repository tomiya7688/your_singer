import array
import io
import unittest
import wave
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch

from your_singer_ml import audio_quality


# {
#   責務: [AudioQualityInputTests: 圧縮音声を含む品質解析入力を検証する]
#   処理: [1: WAV以外の入力がFFmpeg変換を経て統計化されることを確認する]
# }
class AudioQualityInputTests(unittest.TestCase):
    # {
    #   責務: [test_mp3_input_is_decoded_to_pcm16_without_temporary_files: MP3入力の変換経路を検証する]
    #   処理: [1: MP3を直接開けない状況を作る 2: FFmpeg標準出力のPCM16 WAVが集計されることを確認する]
    #   戻り値: []
    # }
    def test_mp3_input_is_decoded_to_pcm16_without_temporary_files(self):
        # FFmpegが返す短いPCM16 WAVをメモリ上に作る。
        decoded_audio = io.BytesIO()
        with wave.open(decoded_audio, "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(48000)
            wav.writeframes(array.array("h", [1000] * 480).tobytes())

        # 入力側のwave.openは失敗し、変換結果側は成功する状態を再現する。
        open_decoded_audio = wave.open(io.BytesIO(decoded_audio.getvalue()), "rb")
        ffmpeg_result = CompletedProcess(
            args=[], returncode=0, stdout=decoded_audio.getvalue(), stderr=b""
        )
        with (
            patch(
                "your_singer_ml.audio_quality.wave.open",
                side_effect=[wave.Error("not a WAV file"), open_decoded_audio],
            ),
            patch(
                "your_singer_ml.audio_quality.subprocess.run",
                return_value=ffmpeg_result,
            ) as run_ffmpeg,
        ):
            stats = audio_quality.AudioQualityAnalyzer._read_wave_stats(
                Path("private-input.mp3")
            )

        self.assertAlmostEqual(stats.duration_sec, 0.01)
        self.assertGreater(stats.rms, 0.0)
        self.assertIn("pipe:1", run_ffmpeg.call_args.args[0])
        self.assertEqual(run_ffmpeg.call_args.kwargs["stdout"], audio_quality.subprocess.PIPE)


if __name__ == "__main__":
    unittest.main()
