import array
import json
import math
import tempfile
import unittest
import wave
from pathlib import Path
from your_singer_ml.phoneme_supplement import generate_phoneme_candidate, cosine, file_hash


def tone():
    return array.array('h', (int(3000*math.sin(2*math.pi*220*i/48000)) for i in range(144000)))


# 合成器・ASR・話者モデルは検証用差し替え。実モデルの品質テストではない。
class FakeModels:
    fingerprint = 'a'*64
    def __init__(self, payload): self.payload = payload
    def phonemize(self, text): return ['k', 'a'] if text == 'か' else ['s', 'a']
    def synthesize(self, text, speaker): return 48000, tone()
    def recognize(self, path): return 'か', -0.1, 0.01
    def embedding(self, path): return [1.0, 0.0]
    def verify_unchanged(self): pass


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.reference = self.root / 'reference.wav'
        with wave.open(str(self.reference), 'wb') as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(48000); w.writeframes(tone().tobytes())
        self.payload = {'text': 'か', 'model_speaker': '話者', 'observed_phonemes': ['a'],
                        'reference_audio_path': str(self.reference), 'output_path': str(self.root / 'candidate.wav')}

    def run_candidate(self, cls=FakeModels):
        return generate_phoneme_candidate(self.payload, cls)

    def test_pass_has_real_wave_and_provenance_but_no_acceptance(self):
        before = file_hash(self.reference)
        result = self.run_candidate()
        self.assertTrue(result['machine_passed'])
        self.assertNotIn('accepted', result)
        self.assertEqual(['k'], result['missing_phonemes'])
        self.assertEqual(3.0, result['duration_sec'])
        self.assertEqual(file_hash(Path(self.payload['output_path'])), result['audio_sha256'])
        self.assertEqual(before, file_hash(self.reference))
        json.dumps(result, allow_nan=False)

    def test_wrong_transcript_is_not_adoptable(self):
        class Wrong(FakeModels):
            def recognize(self, path): return 'さ', -0.1, 0.01
        r = self.run_candidate(Wrong)
        self.assertFalse(r['machine_passed']); self.assertTrue(r['reasons'])
        self.assertTrue(Path(self.payload['output_path']).exists())

    def test_different_speaker_is_not_adoptable(self):
        class Wrong(FakeModels):
            def embedding(self, path): return [0., 1.] if path.name == 'candidate.wav' else [1., 0.]
        self.assertFalse(self.run_candidate(Wrong)['machine_passed'])

    def test_silence_is_not_adoptable(self):
        class Silent(FakeModels):
            def synthesize(self, text, speaker): return 48000, array.array('h', [0]*144000)
        self.assertFalse(self.run_candidate(Silent)['machine_passed'])

    def test_unconfident_asr_is_not_adoptable(self):
        class Weak(FakeModels):
            def recognize(self, path): return 'か', -2., .5
        self.assertFalse(self.run_candidate(Weak)['machine_passed'])

    def test_exception_removes_partial_audio(self):
        class Failed(FakeModels):
            def recognize(self, path): raise RuntimeError('モデル異常')
        with self.assertRaises(RuntimeError): self.run_candidate(Failed)
        self.assertFalse(Path(self.payload['output_path']).exists())

    def test_model_changed_during_generation(self):
        class Changed(FakeModels):
            def verify_unchanged(self): raise ValueError('変更')
        with self.assertRaises(ValueError): self.run_candidate(Changed)
        self.assertFalse(Path(self.payload['output_path']).exists())

    def test_reference_changed_during_generation(self):
        class Changed(FakeModels):
            def synthesize(self, text, speaker):
                Path(self.payload['reference_audio_path']).write_bytes(b'changed')
                return super().synthesize(text, speaker)
        with self.assertRaises(ValueError): self.run_candidate(Changed)
        self.assertFalse(Path(self.payload['output_path']).exists())

    def test_no_missing_phone_does_not_generate(self):
        self.payload['observed_phonemes'] = ['a','k']
        with self.assertRaises(ValueError): self.run_candidate()
        self.assertFalse(Path(self.payload['output_path']).exists())

    def test_existing_output_is_preserved(self):
        path = Path(self.payload['output_path']); path.write_bytes(b'original')
        with self.assertRaises(ValueError): self.run_candidate()
        self.assertEqual(b'original', path.read_bytes())

    def test_racing_output_is_preserved(self):
        class Racing(FakeModels):
            def synthesize(self, text, speaker):
                Path(self.payload['output_path']).write_bytes(b'original')
                return super().synthesize(text, speaker)
        with self.assertRaises(FileExistsError): self.run_candidate(Racing)
        self.assertEqual(b'original', Path(self.payload['output_path']).read_bytes())

    def test_nonfinite_scores_fail_closed(self):
        class Bad(FakeModels):
            def recognize(self,path): return 'か', float('nan'), 0.
        with self.assertRaises(ValueError): self.run_candidate(Bad)
        self.assertFalse(Path(self.payload['output_path']).exists())

    def test_zero_or_mismatched_embeddings_are_rejected(self):
        for a,b in [([0.,0.],[1.,0.]), ([1.],[1.,0.]), ([float('nan')],[1.])]:
            with self.subTest(a=a), self.assertRaises(ValueError): cosine(a,b)

    def test_invalid_requests_fail_before_models(self):
        for text in ['', 'a'*121, 'a\nb', 'a|b']:
            with self.subTest(text=text):
                self.payload['text']=text
                with self.assertRaises(ValueError): self.run_candidate()

    def test_missing_production_models_do_not_return_success(self):
        self.payload.update(model_directory=str(self.root/'absent'), resources_root=str(self.root/'resources'))
        with self.assertRaises(FileNotFoundError): generate_phoneme_candidate(self.payload)
        self.assertFalse(Path(self.payload['output_path']).exists())
