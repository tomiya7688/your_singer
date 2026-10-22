# 作業別の最初の確認先

実装・テスト・設計を探すときだけ、今回の仕事に該当する行を選びます。代表箇所は基点`5906083`のコードとテスト名を確認しました。動作・合格状態は表から判断せず、現在の原典と実行結果を確認してください。

パスを省略するため、`Process`は`src/process/YourSinger.Process/processing/`、`worker`はその`ml/worker/your_singer_ml/`、`C# tests`は`tests/YourSinger.Process.Tests/`を示します。

| 依頼の対象 | 最初の実装と担当 | 対応する確認先 | 関係する設計 |
| --- | --- | --- | --- |
| 音声分離・文字起こし・音素時刻 | [前処理入口](../src/process/YourSinger.Process/processing/audio/AudioPreprocessingService.cs)とworkerの`preprocessing.py`、`mfa_alignment.py` | [worker前処理](../tests/worker/test_preprocessing.py)、[MFA](../tests/worker/test_mfa_alignment.py) | [前処理](../docs/implementation-v1-05.md)、[alignment](../docs/forced-alignment.md) |
| 共通特徴・学習データへの編集反映 | Processの`dataset/UniversalVoiceDatasetService.cs`と`TrainingDatasetSnapshotService.cs` | [特徴](../tests/worker/test_universal_features.py)、[編集回帰](../tests/YourSinger.Process.Tests/EditingRegressionTests.cs) | [データ構造](../docs/data-model.md) |
| 音素の補完・補正 | [補完処理](../src/process/YourSinger.Process/processing/dataset/completion/)とworkerの`phoneme_supplement.py` | [補完](../tests/YourSinger.Process.Tests/PhonemeSupplementTests.cs)、[候補通信](../tests/worker/test_candidate_protocol.py) | [補完](../docs/phoneme-supplements.md) |
| 学習jobの作成・再作成・状態 | Processの`training/TrainingJobPlanningService.cs`が入力条件と遷移を検証します | [境界条件](../tests/YourSinger.Process.Tests/TrainingGuardrailTests.cs)、[再作成](../tests/YourSinger.Process.Tests/TrainingRecreationTests.cs) | [再作成](../docs/training-job-recreation.md) |
| DiffSingerの入力・学習・出力 | [歌唱処理](../src/process/YourSinger.Process/processing/singing/)とworkerの`diffsinger_acoustic_input.py`、`diffsinger_training.py` | [音響入力](../tests/worker/test_diffsinger_acoustic_input.py)、[起動](../tests/worker/test_diffsinger_training.py)、[出力](../tests/YourSinger.Process.Tests/DiffSingerExportTests.cs) | [歌唱設計](../docs/implementation-v1-10.md)、[音響入力](../docs/diffsinger-acoustic-input.md) |
| Talkの入力・学習・出力 | [トーク処理](../src/process/YourSinger.Process/processing/talk/)とworkerの`style_bert_vits2_training.py`、`style_bert_vits2_session.py` | [起動・失敗条件](../tests/worker/test_style_bert_vits2_training.py)、[C#連携](../tests/YourSinger.Process.Tests/StyleBertVits2TrainingTests.cs) | [トーク設計](../docs/implementation-v1-11.md)、[bundle](../docs/talk-trainer-bundle.md) |
| 画面・学習履歴・補完候補 | [UI](../src/ui/YourSinger.App/)の`MainWindow.*`と対象Windowから呼出先を追います | C# testsの編集・job回帰を確認します。実GUI操作の自動テストはこの案内では未特定です | [UI設計](../docs/implementation-v1-12.md) |
| Windows配布・ML依存・runtime | [配布スクリプト](../tools/build_windows_distribution.ps1)、[worker包装](../tools/build_ml_worker.ps1)、`.github/workflows/package-worker.yml` | 該当`prepare_*`・`probe_*`と配布manifest、対象headのpackage CI。単体テスト成功だけで配布起動済みとしません | [配布](../docs/ml-worker-distribution.md)、対象Trainerのbundle資料 |

## 表から原典へ進む

- 該当行のシンボルを対象ディレクトリ内で検索し、定義と呼出元を確認します。workerコマンドの対応表は`ml/worker/your_singer_ml/main.py`です。
- C#・worker双方に処理がある場合は、渡すpayloadと受け取る結果を境界で照合します。
- 読み始める場所が分かれば、この表にない資料を一括で開きません。足りない疑問が分かったときだけ範囲を広げます。
- ファイル移動や責務変更時は該当行を更新します。全ファイルの列挙やテスト結果の蓄積には使いません。
