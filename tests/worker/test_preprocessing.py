import builtins
import unittest
from pathlib import Path
from unittest.mock import patch

from your_singer_ml import preprocessing


class VocalSeparationTests(unittest.TestCase):
    def test_missing_demucs_does_not_copy_mixed_audio_as_separated_vocals(self):
        source = Path("unused-mixed-audio.wav")
        output = Path("never-created-vocals.wav")
        original_import = builtins.__import__

        def import_without_demucs(name, *args, **kwargs):
            if name == "demucs.separate":
                raise ImportError("demucs not installed")
            return original_import(name, *args, **kwargs)

        with patch("builtins.__import__", side_effect=import_without_demucs):
            with self.assertRaisesRegex(RuntimeError, "伴奏入り音声を分離済みとして扱わない"):
                preprocessing._separate_vocals(source, output)

        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
