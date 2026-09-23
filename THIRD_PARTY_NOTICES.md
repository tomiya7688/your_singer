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
- pyopenjtalk 0.4.1
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
