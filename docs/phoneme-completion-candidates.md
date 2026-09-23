# 不足音素の発話候補生成（Issue #9 追補）

## この段階で追加するもの

選択した話者の会話録音と文章を条件として、不足音素を含む新しい発話のWAV候補を生成する。
音高特徴の補間とは異なる処理であり、Qwen3-TTS Baseの音声クローニングAPIを呼び出す。
生成された音声の発音や話者一致を検証する処理、候補の採用、学習データへの追加はまだ実装しない。
「音声ファイルが生成された」と「不足音素を正しく補完できた」は区別する。

## 公式実装の参照先

2026年9月22日に確認した一次情報:

- Qwen3-TTS公式README: https://github.com/QwenLM/Qwen3-TTS
- 呼び出しAPI: https://github.com/QwenLM/Qwen3-TTS/blob/main/qwen_tts/inference/qwen3_tts_model.py
- パッケージ定義: https://github.com/QwenLM/Qwen3-TTS/blob/main/pyproject.toml
- 対象モデル: https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-Base

互換対象パッケージは `qwen-tts==0.1.1`。公式の `generate_voice_clone` に、ローカル参照音声、参照文章、`language="Japanese"` を渡す。
固定話者モデルや声の説明からの生成に置き換えず、Baseモデルを使用する。

## 入力の選択

C#側は既存の `TrainingDatasetSnapshotService` により手動統合・分類・除外を反映した読み取り結果を使う。
対象は選択した話者の会話だけ。他の話者や歌唱の観測値で不足を充足扱いにしない。
ASR信頼度0.75以上・音素時刻信頼度0.65以上の区間を候補とする。これは暫定の選別条件であり、認識精度の保証ではない。
参照文章と3〜15秒の区間を選び、元波形のSHA-256と解析結果の識別子を固定する。
Python側でもWAVの実際の長さ、モノラルPCM16、サンプルレート、振幅、クリッピングを確認する。
前後で入力が変われば処理を止める。参照の再確認をせず、確認画面表示時の値をそのまま使い続けない。

## 不足音素から文章を選ぶ

固定した日本語の例文を `pyopenjtalk` で音素化し、不足音素を含むものを決定的な順序で選ぶ。
1回のUI操作では最大4文に制限する。文字存在の比較や、音素化に失敗した際の文字単位への代用は行わない。
例文で扱えない音素、生成上限のため選べなかった音素は `unplanned_phonemes` に残す。
`expected_phonemes` は文章から計算した期待列であり、生成音声で観測・検証した音素列ではない。

## ワーカーの分離と配布

既存のDemucs・SpeechBrain等のワーカーと依存関係を分ける。

```text
src/process/YourSinger.Process/processing/ml/completion_worker/
  pyproject.toml
  your_singer_completion/
```

C#が起動する公開されない内部実行ファイルは以下に固定する。

```text
workers/completion/YourSinger.Completion.exe   # Windows
workers/completion/YourSinger.Completion       # Linux
models/phoneme-completion/qwen3-tts-base/       # トークナイザーを含むローカルモデル一式
```

端末の `python` コマンドは呼ばない。ワーカーがなければ起動前に日本語エラーを返す。
モデルの実行時自動取得や外部サービスへの音声送信は要求しない。モデルIDやURLではなく絶対パスの実ファイルを要求し、Hugging Faceのオフラインモードを有効にする。
`pyopenjtalk` 辞書も同梱を要求し、辞書不足を自動取得で隠さない。

**このPRはワーカーソースと呼び出し経路の追加であり、配布用exeやモデル重みの作成・同梱は行っていない。**
PyTorch、トークナイザー、日本語辞書を含む独立ランタイムのパッケージングと、対象端末での実推論がリリース前に必要。
利用者へPythonの手動導入を要求する設計には変更しない。

開発環境ではこのディレクトリを独立したPython 3.11/3.12環境へ導入し、`python -m your_singer_completion` をJSON Linesで起動できる。
この開発用の起動方法は、配布アプリの利用者向け操作ではない。

## 出力・出自

```text
cache/phoneme-candidates/<要求ID>/
  candidate-01.wav
  ...
  manifest.json
```

`origin=synthetic`、`content_type=speech`、`training_eligible=false`、`state=pending_review` を固定する。
録音、保存済みUVD、学習ジョブ、カバレッジ診断は変更しない。
モデルファイル一式の内容ハッシュ、参照区間・参照波形ハッシュ・文章、入力識別子、期待音素列、目標音素、乱数の種、音声ハッシュ、長さと振幅を記録する。
`phoneme_verification` と `speaker_verification` は `not_performed`。未検証の品質を数値スコアで装わない。

通常の失敗では専用の一時フォルダを削除し、完成時だけ公開する。同一要求IDの既存候補を上書きしない。
OSによる強制終了やワーカーの強制停止では `.pending-*` が残る場合がある。完成候補として扱わず、自動採用もしない。
乱数の種を保存しても異なる端末・GPUでバイト単位に同じ音声を生成する保証ではない。

## UI

「話者・分類」で1人を選び、「学習・書き出し」の「不足音素の補完候補」を開く。
参照区間、参照文、不足音素を確認してから生成する。
保存先を開いてWAVとmanifestを確認できる。画面を閉じる操作は実行中の内部ワーカーを中止する。
既存の補完ON/OFFを押しただけで、重い音声生成や候補の学習への追加が自動実行されることはない。

## 検証範囲

モデル不要テストは、文章の選択、条件検査、原本保護、通常失敗時の後片付け、入力・モデル変更検出、JSON通信、公式APIへの引数を検証する。
そこで使う既知の正弦波は制御フローのfixtureであり、話者や音素の生成結果ではない。
C#テストは選択話者の分離、手動除外、参照条件、確認後の入力変更、読み取り専用計画を確認する。

実Qwenモデルを用いた日本語音声生成、生成音素の確認、話者の一致、GUI実操作、配布物のオフライン実行は未検証。
これらと候補の採用・学習接続が揃うまで、Issue #9の不足音素補完を完了扱いにしない。
