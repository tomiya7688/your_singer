# Third-Party Notices

Your Singer includes or may include third-party libraries, runtimes, machine-learning models, tools, and other components.

Each third-party component remains subject to its own license and copyright terms.

This file will be populated as dependencies and bundled models are selected during implementation.

## Policy

- Verify the license of each bundled dependency and pretrained model before release.
- Preserve required copyright, attribution, license, and NOTICE text.
- Do not assume that the Apache License 2.0 for Your Singer applies to third-party components.
- Where practical, record component name, version or commit, source, license identifier, and redistribution requirements.


## Selected ML runtime components for the phoneme-supplement path

The following components are selected by the current implementation. This is not yet a complete transitive dependency inventory for a public release.

- Style-Bert-VITS2 2.7.0
  - Source: official tag commit `d8148f3090ee5038ca7b4e4b327116c64467f952`
  - License: AGPL-3.0
  - Bundled into the internal ML worker. Public releases must satisfy the applicable AGPL redistribution and source-availability requirements.
- SpeechBrain 1.1.1
  - License: Apache-2.0
- faster-whisper 1.1.1
  - License: MIT
- pyopenjtalk-dict 0.3.4.dev2
  - License: MIT
- `ku-nlp/deberta-v2-large-japanese-char-wwm`
  - The pinned model revision is recorded in the phoneme-supplement resource lock.
  - Its model license and attribution requirements remain applicable.
- `Systran/faster-whisper-small`
  - The pinned model revision is recorded in the resource lock.
  - Its model license and attribution requirements remain applicable.
- `speechbrain/spkrec-ecapa-voxceleb`
  - The pinned model revision is recorded in the resource lock.
  - Its model license and attribution requirements remain applicable.

Before a public binary release, generate a complete dependency/model inventory and include all required license texts, copyright notices, source availability information, and attributions. The Apache-2.0 license for Your Singer does not replace third-party terms.

## Japanese forced alignment

- Montreal Forced Aligner 3.4.2: https://github.com/MontrealCorpusTools/Montreal-Forced-Aligner (MIT).
- Japanese MFA acoustic model v3.0.0: https://github.com/MontrealCorpusTools/mfa-models/releases/tag/acoustic-japanese_mfa-v3.0.0 (CC BY 4.0).
  - Attribution: Michael McAuliffe and Morgan Sonderegger. The model is unchanged; Your Singer supplies a custom pronunciation dictionary.
  - SHA-256: `85928ffb1024486872a677a92e1fa94d2f997462d0b84c73a58aa9bb0e35179a`.
- SoundFile 0.13.1: https://github.com/bastibe/python-soundfile (BSD-3-Clause). The Windows wheel includes libsndfile under its applicable LGPL terms.
- The dedicated Conda environment includes Kaldi and additional dependencies. Its `bundle-manifest.json` records package versions, sources, license identifiers, and available package hashes. Complete transitive license texts and notices must be checked before public binary release.

## Dedicated DiffSinger acoustic Trainer bundle

- OpenVPI DiffSinger 2.5.1, commit `323a5693245b827caba0ab1a7371fe5ca885b061`: Apache-2.0. The pinned source and its `LICENSE` are included in `workers/diffsinger/source`.
- The dedicated Python runtime and exact package versions are recorded in `tools/diffsinger-training-requirements.lock.txt` and the bundle manifest. CUDA-enabled PyTorch 2.3.1+cu121 is included; no pretrained vocoder or training audio is bundled.
- Complete transitive license and notice review remains required before a public binary release.

## Dedicated Style-Bert-VITS2 training bundle

- The training runtime is separate from the general ML worker: Python 3.12.10, PyTorch/torchaudio 2.3.1+cu121, NumPy 1.26.4, and pyopenjtalk-dict 0.3.4.dev2.
- Exact Python package versions are recorded in `tools/style-bert-vits2-training-requirements.lock.txt` and the bundle manifest. Wheel-supplied license files remain in the runtime.
- Exact model revisions and SHA-256 values are recorded in `resources/style-bert-vits2-training.lock.json` and copied to the bundle.
- Japanese BERT: CC-BY-SA-4.0. Style speaker model: CC-BY-4.0.
- WavLM's model card links to the UniSpeech license, which is CC-BY-SA-3.0; it is not recorded as MIT.
- The pinned Style-Bert-VITS2 1.0 base model card does not declare a license. Its redistribution terms and complete transitive notices remain release requirements, recorded in `redistribution_review_required`.
- Local private audio, transcripts, training features, and trained models are excluded from this stock-resource bundle.
