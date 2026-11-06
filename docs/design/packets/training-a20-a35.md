# v1学習 実装パケット(A20〜A35)

> **範囲の注記**: A36〜A42(#132〜#139。質感プリセット・学習戦略Registry・Mod・素材量の棚卸し)は、[PR #135](https://github.com/tomiya7688/your_singer/pull/135) で設計が正本化されたため、別ファイル [training-a36-a42.md](training-a36-a42.md) にまとめています。このファイルの `#132`〜`#139` への言及は、依存関係を示すための参照です。

本書は #99〜#114(A20〜A35)を、実装者が着手できる単位にまとめた設計書です。
契約・式・順序は設計正本(README.md、contracts-and-weighting.md、routing-and-cross-domain.md、training-modes-and-backends.md、verification-and-release.md)が正で、Issue 本文との食い違いは「要決定」に記録します。
既存・新規・不明は、この worktree(`E:/projects/worktrees/design-portal/your_singer`)の Glob/Grep 結果です。

## 前提と注記

- 依存順の正本 [implementation-plan.md](../v1-training/implementation-plan.md)(A01〜A35)は PR #115 でマージ済みです。本書の「依存一覧」はこれに合わせてあり、食い違いはパケットを直します([D-26](../decisions.md))。A36〜A42 の追加条件は `goal-texture-data-planner.md` §10 にあります。
- 追補(goal-texture-data-planner.md、data-profile-strategy-selection.md、texture-preset-taxonomy.md、strategy-plugin-api.md)は PR #135 でマージ済みです。本書は作成時にこれらを読めていなかったため、A36〜A42 の契約を [training-a36-a42.md](training-a36-a42.md) が扱います。このファイルで「設計正本が扱っていない」と書いた箇所は、その追補を読み直していません(不明)。
- training-modes-and-backends.md は、Auto を「`fine_tune` または `from_scratch` を実行前に確定する」と定義しています。Issue #112・#133・#134 は、Auto を複数工程の計画として扱う前提で書かれています。この食い違いは各節の要決定に載せました。
- 設計正本は評価ファイルを `assessment.json` / `train_weights.json` と呼び、Issue は「weight manifest」と呼びます。同一物かは要決定です。
- 層ルール(docs/architecture.md、README.md の UPD 図): UI は UI Messenger を介し、Python を直接呼びません。Python worker は `src/process/YourSinger.Process/processing/ml/worker/` 配下に置き、第 4 層にしません。
- 略記: 「既存」はファイルまたはクラスが存在する、「新規」は存在せず Issue が新設を求める、「不明」は Issue が場所を示さず確認できない。
- `ml/worker/your_singer_ml/` は `src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/` の省略です。

## 依存一覧

- `#99 A20 <- #98, #87, #94, #132, #134 (#132/#134 はIssue本文由来。implementation-plan は #87, #94, #98)`
- `#100 A21 <- #98, #87, #93, #132, #133 (#132/#133 はIssue本文由来。implementation-plan は #87, #93, #98)`
- `#101 A22 <- #91, #92, #93, #94`
- `#102 A23 <- #101`
- `#103 A24 <- #93, #101`
- `#104 A25 <- #96, #101 (#97 は任意)`
- `#105 A26 <- #103, #104`
- `#106 A27 <- #112, #113, #114`
- `#107 A28 <- #85, #86, #91, #92, #93, #94, #101`
- `#108 A29 <- #109, #110, #111, #106, #107, #99, #100`(#128 は含めない。D-11)
- `#109 A30 <- #93, #91, #103, #102`
- `#110 A31 <- #92, #94, #96, #102, #104`
- `#111 A32 <- #109, #110, #114, #74`
- `#112 A33 <- #84, #88`(#132/#133/#134 は #88 経由。#136/#137 は本文で参照するが、implementation-plan と §10 に依存として無く、置かない。D-03)
- `#113 A34 <- #83, #85`
- `#114 A35 <- #103, #104, #105`

循環: #137 は #133・#134 に依存し、#133・#134 は #137 の Registry を使う前提で書かれています(実際の依存方向は決定記録 D-03 が案を持ち、状態は待ち)。#139 と #112 も、依存方向が往復しています。

## 同一ファイルを触るIssue

| ファイル/シンボル                                                                                                | 触るIssue                          | 衝突の注意                                                                                                       |
| ---------------------------------------------------------------------------------------------------------------- | ---------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| `src/data/YourSinger.Data/models/TrainingJobRecord.cs`                                                           | #101、#112、#136、#137、#133、#134 | 現状は mode・initialization・plan を持たない(設計正本 §2 が拡張を要求)。フィールド追加の順序と所有者を先に決める |
| `src/process/YourSinger.Process/processing/training/TrainingJobPlanningService.cs`                               | #112、#133、#134、#136、#137       | `RequiresRecreation` と旧ジョブ再作成の整合を全員が壊しうる                                                      |
| `src/process/YourSinger.Process/processing/dataset/TrainingDatasetSnapshotService.cs`(`CreateJobFingerprint`)    | #107、#132、#136、#137             | fingerprint に preset・strategy・inventory を混ぜる箇所が競合する。version 更新を一度にまとめる                  |
| `src/process/YourSinger.Process/processing/ml/protocol/MlWorkerMessage.cs`(`MlWorkerRequest`/`MlWorkerResponse`) | #101、#138                         | schema_version 付与と provider の IPC が重なる                                                                   |
| `src/process/YourSinger.Process/processing/ml/bridge/MlWorkerClient.cs`(`SendAsync`)                             | #102、#138                         | キャンセル・タイムアウト処理が重複する                                                                           |
| `ml/worker/your_singer_ml/diffsinger_training.py`                                                                | #99、#102、#101、#110              | run report・子プロセス回収・補助条件入力を同じ関数に足さない                                                     |
| `ml/worker/your_singer_ml/style_bert_vits2_training.py`                                                          | #100、#102、#101、#103             | 同上                                                                                                             |
| `src/process/YourSinger.Process/processing/talk/`                                                                | #100、#103                         | Adapter と Verifier を別ファイルに分ける                                                                         |
| `src/process/YourSinger.Process/processing/singing/`                                                             | #99、#104                          | 同上                                                                                                             |
| `src/ui/YourSinger.App/MainWindow.axaml`・`MainWindow.axaml.cs`                                                  | #112、#113、#139、#106             | 学習設定・品質欄・Mod 画面を区画ごとに分ける                                                                     |
| `src/ui/YourSinger.App/MainWindow.TrainingHistory.cs`・`TrainingHistoryWindow.axaml`                             | #114                               | 単独。履歴の旧形式表示は #112 の再作成と整合させる                                                               |
| `tests/worker/test_style_bert_vits2_training.py`・`test_diffsinger_training.py`                                  | #100、#99、#103、#101              | 同一テストファイルへの追記が衝突しうる                                                                           |
| `tests/YourSinger.Process.Tests/DiffSingerExportTests.cs`                                                        | #104                               | 単独                                                                                                             |
| `.github/workflows/build.yml`                                                                                    | #107                               | 既存構成内の確認のみ                                                                                             |
| `docs/implementation-v1-10.md`・`docs/implementation-v1-11.md`                                                   | #110、#109                         | 実行記録は 1 PR 1 ファイル                                                                                       |

## Issue別パケット

### #99 A20 発話主体素材から指定質感の歌唱モデルへ声質適応する

- 目的: 発話由来の声質を指定した歌唱モデルへ実際に反映し、歌唱教師不足でも生成に使える経路を検証する。
- 先行: #98、#87、#94、#132、#134 が契約確定済みであること。#88 は #94 の先行で、#101 は implementation-plan で #99 の先行ではない。
- 触るもの: `src/process/YourSinger.Process/processing/singing/` に適応入力組立(新規。クラス名は Issue になし)。`DiffSingerAcousticInputService.cs`・`DiffSingerDatasetBuilder.cs`(既存)。Python DiffSinger Adapter は `diffsinger_training.py` または `diffsinger_acoustic_input.py`(既存。どれが Adapter か不明)。`tests/worker/test_diffsinger_training.py`(既存)。
- 契約: 同一話者 speech の speaker 特徴・声質統計を、provenance と weight 付きの補助条件として渡す。routing-and-cross-domain.md の `feature_only` ロールに従う。Adapter の消費記録は segment_id、用途、方式、input hash。
- 完了条件: 共有特徴が歌唱経路に読み込まれる。speech-only から未学習歌詞+音符を実レンダリングし、本人声質と目標 texture への適合を記録する。別話者・未対応特徴は使われない。実効果が未検証なら「学習効果確認済」と表示しない。
- テスト: 自動 `SingingTimbreAdaptationTests`(同一/別話者、speech only、feature toggle、対応外 backend、input hash 変更)。手動・実GPU: 有効/無効の実音声差分比較。
- 非目標: 疑似歌声の自動生成、VC の未検証音声を教師に混入すること。
- 要決定: 適応入力組立の担当クラス名と配置(新規クラスか、既存 `DiffSingerAcousticInputService` の拡張か)。
- 解消(D-10): 「A19」は #98。
- 要決定: 「本人声質・目標 texture への適合」の測定指標と合格基準。
- 解消(D-09): 未対応と根拠を記録し、UIとジョブに「未対応」と表示することで完了とする。この場合 v1 で「トーク/歌唱の相互利用」とは称さない。
- 実装済み(#99 の部品): `SingingTimbreAdaptationService`(同一話者の発話区間を feature_only の補助条件として選ぶ。provenance(segment・source・cache key)と暫定の一律 weight 1.0、input hash を持つ。別話者・話者不明・除外・無音・歌唱区間は含めない。DiffSinger 固定版は話者エンコーダ条件を受け取れないため既定は Support=unsupported・Used=false で、D-09 のとおり「未対応」と根拠を記録。対応 Backend でも Adapter の消費記録(segment_id・用途・方式・input hash)が選んだ参照と一致するときだけ `Confirm` で Used=true にする)。テスト: `SingingTimbreAdaptationTests`。未実装: 話者エンコーダの限定パッチ(S1 本体。固定ソースへの再現可能な overlay が必要)、Adapter の消費記録の出力、実音声の ON/OFF 比較(未検証。「トーク/歌唱の相互利用」とは称さない)。

### #100 A21 歌唱主体素材から指定質感のトークモデルへ声質適応する

- 目的: 歌唱由来の声質を指定したトークモデルへ実際に反映し、発話教師不足でもモデル生成に使える経路を検証する。
- 先行: #98、#87、#93、#132、#133。#88 は #93 の先行で、#101 は implementation-plan で #100 の先行ではない。
- 触るもの: `src/process/YourSinger.Process/processing/talk/` に適応入力組立(新規)。`StyleBertVits2DatasetBuilder.cs`・`StyleBertVits2TrainerService.cs`(既存)。Python は `style_bert_vits2_session.py`・`style_bert_vits2_training.py`(既存)。`tests/worker/test_style_bert_vits2_training.py`(既存)。
- 契約: singing 由来の話者特徴・発声様式を talk 補助条件として渡す(`feature_only`)。job 内に「実際に利用した特徴」と「利用できなかった理由」を記録する。
- 完了条件: 共有特徴が消費される。singing-only から実日本語読み上げを 1 経路以上検証して texture 適合を評価する(未実施は未検証と記録)。他話者・元音声が混入しない。非対応は unsupported を記録する。
- テスト: 自動 `TalkTimbreAdaptationTests`(singing only 補助条件、発話 0 秒、他話者、style mismatch、同一入力再実行、feature toggle)。手動・実GPU: ON/OFF の読み上げ差分。
- 非目標: 歌唱を話し声へ変換して自動で教師データ化すること。
- 解消(D-09): unsupported と根拠を記録して完了とする。
- 要決定: 「texture を満たす」の評価指標と基準(#99 と共通化するか)。
- 実装済み(#100 T1 の部品): `TalkTimbreAdaptationService`(同一話者の歌唱区間から参照音声を選ぶ。発話教師が0件でも作れる。別話者・話者不明・除外・確認待ち・発話区間は含めない。無効/参照なしは `Used=false` と理由を記録)、`style_bert_vits2_timbre.py`(参照音声の埋め込みを検査し、平均を新しいスタイルとして `style_vectors.npy` と `config.json` の `style2id`/`num_styles` へ原子的に追記)。
- 実装済み(#100 worker 組み込み): `train_style_bert_vits2` のペイロードに `timbre_references`(ワークスペース内の絶対パス)を受け付け、ジョブ内 `timbre_refs/` へ複製(wav以外・欠落は拒否)、学習成功後に `timbre_style` 工程で pyannote(wespeaker, 公式 style_gen.py と同じ 256 次元)の埋め込みを計算して新スタイルを追記し、`timbre_style_report.json` を残す。使える埋め込みが0件なら学習成功にせず失敗。C# 側は Trainer の報告で追記を確認できた場合だけ `SharedFeatureUsage.Used=true` にする(報告が無ければ失敗)。
- 未実装・未検証(#100): UI/オーケストレーションからの `timbre` 引数の接続、実日本語読み上げでの ON/OFF 評価(未実施は未検証と記録)。pyannote 実モデルでの追記は実機未実行。

### #101 A22 Workerが実際の学習モード・重み使用・成果物Hashを記録する

- 目的: worker が実際の学習モード・重み使用・成果物 Hash を記録し、C# が job 設定と照合する。
- 先行: #91、#92、#93、#94。
- 触るもの: `src/process/YourSinger.Process/processing/ml/protocol/MlWorkerMessage.cs`(既存。`MlWorkerRequest`/`MlWorkerResponse`)。`src/data/YourSinger.Data/models/TrainingJobRecord.cs`(既存)。Python worker の run report 処理(新規。ファイル名は不明)。
- 契約: IPC 要求・応答に `schema_version`、`job_id`、resolved mode、weight manifest hash、seed、資源 revision。`run_manifest.json` は training-modes-and-backends.md §6 のとおり(モード、読み込んだ初期モデルの hash、weight の件数と範囲、Trainer 終了コード、成果物の path と hash)。巨大バイナリは IPC に流さず path/hash で扱う。
- 完了条件: 指定 mode・weight と実行 report が違えば失敗。空成果物・不正 path は失敗。旧 worker 応答を新仕様の成功に読み替えない。失敗・未検証も履歴に残す。
- テスト: 自動 `WorkerRunReportTests`(hash 改竄、missing report、mode fallback、bad relative path、exit 0 でも成果物欠損)。実GPU検証はなし(#109/#110 で実施)。
- 非目標: 子プロセスの停止制御(#102)、品質評価 UI。
- 実装済み(#101 の契約部): worker の `run_report.py`(`run_manifest.json`: schema_version・job_id・要求された解決済みモード・seed・Trainer終了コード・成果物の相対パスとsha256・観測した初期重み。成果物が欠損・空・出力先の外なら失敗)、C# の `TrainingRunReport` / `TrainingRunReportVerifier`(計画・seed・終了コード・モードfallback・from_scratch での初期重み読込・fine_tune の初期重み不一致・成果物の欠損/空/不正パス/hash改竄を検証。記録なし・旧形式は失敗。初期重みの実読込が未観測なら Verified にせず Unverified)。`StyleBertVits2TrainerService` は payload に `resolved_training_mode`・`seed` を渡し、返却の run_report を照合して `TrainingJobRecord.RunVerification` に残す(Failed は例外)。テスト: `test_run_report.py`・`TrainingRunReportVerifierTests`。未実装: 初期重みの実読込の観測(#93/#94 の worker 側)、DiffSinger 側の run_report 出力、weight manifest hash の照合。
- 要決定: `run_manifest.json` のフィールド一覧と schema_version の初期値(設計正本は項目のみで型は未定)。
- 要決定: 「weight manifest」と設計正本の `assessment.json`/`train_weights.json` の対応(同一物か別物か)。
- 要決定: 「資源 revision」の表現(固定 commit hash か、lock ファイルの ID か)。

### #102 A23 Trainerキャンセル・異常終了時の子プロセス回収を実装する

- 目的: キャンセルまたは異常終了のとき、Trainer の子・孫プロセスを残さず回収する。
- 先行: #101。
- 触るもの: `ml/worker/your_singer_ml/diffsinger_training.py`・`style_bert_vits2_training.py`・`src/process/YourSinger.Process/processing/ml/bridge/MlWorkerClient.cs`(すべて既存)。FFmpeg キャンセル実装は参照のみ(場所は不明)。PR #56 の状態は不明。
- 契約: job 状態は `TrainingJobState` の Failed/Canceled を区別して記録する(`TrainingJobRecord.cs` は既存で、両値あり)。partial log と中間成果物は診断用に残し、成功として公開しない。再実行は新 job。
- 完了条件: キャンセル後に `train_ms.py`・`scripts/train.py` の子が残らない。前 job を上書きしない。ログと利用者向けエラーが残り、モデル完成と誤表示しない。
- テスト: 自動 `TrainerCancellationTests`(sleeping fake trainer、spawned child process、強制終了、出力の未公開)と、tests/worker の対応テスト。Windows 停止・SIGTERM は OS 別 CI の範囲を確認。実GPU: 実 Trainer での回収を手動確認。
- 非目標: OS 全般のプロセスマネージャの全面書き換え。
- 要決定: タイムアウト値と猶予時間。
- 要決定: 孫プロセス回収の方式(Windows の Job Object か、別の方式か)。
- 要決定: PR #56 の知見の取り込み範囲。
- 実装済み(#102 worker 側): `process_tree.py` の `run_reclaiming` が Trainer を新しいプロセスグループ/セッションで起動し、タイムアウト(`stage_timeout_seconds`)・例外・中断のいずれでも子孫ごと終了する(Windows は `taskkill /T /F`、POSIX は `killpg`)。失敗は `run_failure.json`(kind: canceled / timeout / failed)に残し、`result.json` は書かない(成功として公開しない)。C# の `MlWorkerClient` は既にキャンセル時に `Kill(entireProcessTree: true)` でワーカーごと停止する。tests/worker `test_process_tree.py` で孫プロセスの回収を実プロセスで確認済み。
- 未実装: TrainingJobState の Failed/Canceled を `TrainingJobPlanningService` へ記録する呼出し(学習実行のオーケストレーション・UI 接続は未着手)。実 Trainer での回収は実GPU手動確認が必要(未確認)。

### #103 A24 トークモデルの本物の読み上げ推論と結果検証を実装する

- 目的: トークモデルを固定 Style-Bert-VITS2 2.7.0 で実際に読み上げさせ、結果を検証記録に残す。
- 先行: #93、#101。
- 触るもの: `src/process/YourSinger.Process/processing/talk/` の TalkInferenceVerifier(新規)。worker の推論コマンド(新規。`main.py` が既存の入口)。`tests/worker/test_style_bert_vits2_training.py`(既存)。
- 契約: verification-and-release.md の `verification.json`(stages: checkpoint・export・inference・external_compatibility・quality。state: not_run・running・passed・warning・failed・unavailable)。Issue は not_run/failed/passed の 3 状態を挙げ、設計正本はより多い状態を持つ。WAV は job 内に保存し、artifact hash を持つ。
- 完了条件: 空 checkpoint・dummy WAV で合格しない。未学習日本語文の実読み上げが確認できる。検証不可・失敗は not_run/failed で、passed と表示しない。
- テスト: 自動(fake 推論は契約のみ): `TalkInferenceVerifierTests`(破損 wav、空 wav、runtime 欠損、句読点・拗音)。実GPU検証: 実 runtime による未学習文の読み上げ。
- 非目標: 自動 checkpoint ランキング(#105)、UI(#114)。
- 要決定: 無音・長さ・短文例外の閾値(設計正本は「config/version で固定」とするのみ)。
- 要決定: 検証に使う日本語文の数と出所(固定リストか、生成か)。

### #104 A25 歌唱モデルの実レンダリングとOpenUtau互換性検証を実装する

- 目的: 歌唱モデルの実レンダリングと OpenUtau 互換性を、独立した状態として検証する。
- 先行: #96、#101(#97 の Pitch/Variance は任意)。
- 触るもの: `src/process/YourSinger.Process/processing/singing/` の SingingInferenceVerifier(新規)。歌唱推論 Adapter(新規。場所は不明)。`tests/YourSinger.Process.Tests/DiffSingerExportTests.cs`(既存)。`DiffSingerExportValidator.cs`(既存)も関連。
- 契約: OpenUtau 0.1.565 向けに acoustic・duration・pitch・必要な variance・Vocoder・config を検査する。`verification.json` の inference と external_compatibility を独立に保存する。
- 完了条件: 必要サブモデル欠落で歌唱モデル完成としない。内部生成成功だけで OpenUtau 互換成功と表示しない。未学習歌詞+音符の実歌唱を証跡付きで残す。
- テスト: 自動 `SingingInferenceVerifierTests`(欠損 duration、必要モデル不在、音符範囲外、不正音声、内部/外部状態の独立性)。実機確認: OpenUtau 実機での読込・生成(#110)。
- 非目標: 歌唱音声の自動主観評価(研究機能)。
- 要決定: F0 追従・破綻の測定指標と閾値(設計正本は「測定できる範囲」)。
- 解消(D-15 暫定): v1 では手動で記録する(環境・手順・結果)。自動化は将来。

### #105 A26 複数Checkpointを比較して最良候補と根拠を保存する

- 目的: 複数 checkpoint を実測指標で比較し、最良候補と根拠を保存する。
- 先行: #103、#104。
- 触るもの: `src/process/YourSinger.Process/processing/training/CheckpointEvaluationService.cs`(新規。設計正本に提案名の言及あり)。`src/data/YourSinger.Data/models/` の検証 record(新規)。`tests/YourSinger.Process.Tests/CheckpointEvaluationTests.cs`(新規)。
- 契約: 発音・類似度・音程・歪みなど計測済み指標で比較する。未計測は 0 にも満点にもしない。比較不能なら自動 best を保留し、候補を提示する。ユーザー選択と自動推薦の根拠、checkpoint hash、入力文を永続化する。
- 完了条件: 実測根拠があるときだけ最良を選ぶ。最後の epoch を無条件に最良としない。選定前の checkpoint を削除・上書きしない。
- テスト: 自動 `CheckpointEvaluationTests`(3 候補、1 破損、全測定不能、同点、異なるターゲット、比較方法 version 変更)。実GPU検証はなし。
- 非目標: 推論 Backend の変更。
- 要決定: 指標の一覧、各指標の重み、比較方法 version の形式(設計正本は「Backend 固定の評価設定と weight を文書化」と述べるのみ)。
- 要決定: 同点時の扱い(保留か、決定的な順序か)。

### #106 A27 日本語UIで学習方式・素材重み・モデル検証を操作できるようにする

- 目的: #112〜#114 の UI を統合確認し、フォルダ入力から試聴までの操作を確認する(実装は持たない)。
- 先行: #112、#113、#114。
- 触るもの: なし(確認のみ)。追加変更は #112〜#114 に戻す。
- 契約: 記載なし。
- 完了条件: 実 UI の統合テストと手動確認で、環境・入力・結果を記録する。同一素材を事前仕分けせず歌唱・トーク両 job に振り分けられる。旧学習履歴と再作成が壊れず、未検証を完成と誤表示しない。
- テスト: 自動の統合テストと手動確認(UI 自動化の有無は Issue に記載なし)。実素材での確認を含む。
- 非目標: 画面個別の開発。
- 要決定: 統合テストの自動/手動の分担と、使う実素材。
- 解消(implementation-plan A27): #106 の先行は #112、#113、#114 の3つのUIだけで、#139 と #136/#137 の画面は対象外。

### #107 A28 重み・学習方式・自動振り分け・失敗系の自動E2Eを整備する

- 目的: CI で契約破損と偽成功を検出する自動 E2E を整備する。
- 先行: #85、#86、#91、#92、#93、#94、#101(implementation-plan A28)。cancel の試験は #102 の成果を使うが、#102 を先行には置かない。
- 触るもの: `tests/YourSinger.Process.Tests/`(既存。学習統合テストは新規ファイル)。`tests/worker/`(既存)。`.github/workflows/build.yml`(既存。既存構成内で確認)。
- 契約: 合成 fixture(混在音声、複数話者、低品質だが希少な音素)で、ジョブ生成から weight manifest、backend 要求、結果検証状態まで通す。
- 完了条件: 契約破損と偽成功を CI が検出する。Source/Manual override 更新で必要な後段以外を再実行しない。各失敗が利用者向け状態に保存される。fake worker の成功を実モデル生成と説明しない。
- テスト: 自動 `TrainingContractE2ETests`(train_weight 変更で snapshot 変更、mode 違いで fingerprint 変更、replay、malformed response、cancel、未知 confidence、未校正 MFA、誤話者、破損、除外)。外部 GPU 学習は CI から分離し、実GPU検証は #109/#110。
- 非目標: 実 GPU での学習完走(Issue 本文は「A29 へ分離」と記載)。
- 解消(D-10): 実GPU完走の実体は #109・#110・#111(A30〜A32)。
- 要決定: CI の追加 job の要否(`build.yml` の既存 job で足りるか)。

### #108 A29 実GPUとクリーンWindows配布物で歌唱・トーク両モードをE2E検証する

- 目的: 分割済みの実 GPU・配布・UI・CI の証跡を照合し、v1 学習品質の完成可否を判定する(実装は持たない)。
- 先行: #109、#110、#111、#106、#107、#99、#100(implementation-plan A29)。チェックリストの #128 は完了判定に含めない(D-11)。
- 触るもの: なし(照合のみ)。
- 契約: 各検証の固定モデル revision、入力 fingerprint、weight manifest、checkpoint hash、結果音声 hash、未検証項目を保存する。
- 完了条件: 単体、実 GPU、外部ソフト互換、クリーン配布の証跡を分けて明示する。どれかが失敗・未実施なら「v1品質要件完了」としない。すべて通過した場合のみ #79 の学習品質要件を完了とする。
- テスト: 自動テストなし。手動の照合チェックリスト(Issue 本文のとおり。verification-and-release.md のリリース用チェックリストと対応づける)。
- 非目標: 新しい学習器や UI の実装。
- 解消(D-11 暫定、implementation-plan A29): 含めない。学習E2Eの受入は #108 に閉じ、精度向上は #116 の別軸で評価する。
- 要決定: 実素材(歌唱のみ・発話のみ)の入手元と権利状態の管理方法。

### #109 A30 実GPUでSBV2の追加学習/新規学習を実素材で完走確認する

- 目的: 同じ話者の実音声で `fine_tune` と `from_scratch` を別 job で実学習し、推論まで確認する。
- 先行: #93、#91、#103、#102(implementation-plan A30。#101 は #93 経由)。
- 触るもの: 非公開実素材プロジェクト(リポジトリ外)。`docs/implementation-v1-11.md`(既存)の実行記録。トーク用 GPU 実行手順とログ manifest(新規。公開できる範囲のみ)。
- 契約: GPU・VRAM・ドライバーと固定 SBV2 資源 hash を記録する。実際の initialization と weight 消費 report を 2 方式で比較する。未学習文を合成し、音声 hash・異常・失敗理由・検証 status を記録する。
- 完了条件: 2 方式の学習と推論の証跡が揃い、偽 fallback がない。失敗時も checkpoint 未完成と未検証を区別する。素材と話者情報を公開 Git に載せない。
- テスト: すべて実GPU・実素材の手動検証(自動化できない)。自動化できる補助: GPU 不足の警告、Pretrained 欠損、実 job 再作成、低品質な希少音素の採用。
- 非目標: 歌唱学習、Windows 配布パッケージ作成。
- 待ち(D-14 暫定): #109・#110 で実測した環境を記録した後に確定する。
- 解消(D-14 暫定): 公開してよい証跡は hash・指標・環境の要約まで。ログ全文と素材は非公開。

### #110 A31 実GPUでDiffSingerの追加学習/新規学習と歌唱生成を完走確認する

- 目的: DiffSinger の `fine_tune`/`from_scratch` を実学習し、歌唱 WAV と OpenUtau 実機の互換を確認する。
- 先行: #92、#94、#96、#102、#104(implementation-plan A31。#101 は #104 経由)。
- 触るもの: 非公開実歌唱素材。`docs/implementation-v1-10.md`(既存)の実行記録。歌唱実機検証ログ manifest(新規)。
- 契約: 固定 DiffSinger/OpenUtau・MFA 資源(`diffsinger-source.lock.json` は `src/process/YourSinger.Process/processing/ml/worker/resources/` に既存)と GPU 条件を記録する。acoustic・duration・必要資源と weight の使用を 2 方式で検証する。
- 完了条件: 2 方式の完走、歌唱 WAV、OpenUtau 実機結果の証拠。不完全な音響モデルだけを歌唱モデル完成と呼ばない。失敗条件と未対応音域を記録する。
- テスト: すべて実GPU・実機の手動検証。補助の自動テスト: 無音/低 F0、MFA 保留、duration 欠損、checkpoint 破損、OpenUtau ロード失敗。
- 非目標: トーク学習、Windows リリース配布確認。
- 要決定: 検証する音域・歌詞・音符の範囲。
- 一部解消(D-15 暫定): 手動で環境・手順・結果を記録する。実施環境の指定は正本に無い(不明)。

### #111 A32 クリーンWindows配布物で単一起動・内蔵Trainer・検証表示を確認する

- 目的: クリーン Windows で `YourSinger.exe` が単独起動し、内蔵 Trainer と検証表示が動くことを確認する。
- 先行: #109、#110、#114、#74。
- 触るもの: `tools/build_windows_distribution.ps1`(既存)の検証のみ。`docs/ml-worker-distribution.md`(既存)への手順追記。配布 UI の確認記録(新規)。
- 契約: 完成配布物の hash を固定する。署名・attestation の未完了は未完了として記録する。第三者モデルのライセンスと NOTICE を確認する。
- 完了条件: pip/.NET のインストールや worker の手動起動を要求しない。resource path の破損と初回ネットワーク依存がない。
- テスト: 手動(クリーン Windows 環境): 初回起動、worker 起動失敗、DLL 欠損、モデル欠損、zip 分割統合とハッシュ照合。自動化できる範囲は Issue に記載なし。
- 非目標: 素材の 4 学習経路の完走(#109/#110)。
- 一部解消(D-16 暫定): 署名の検証は含めず、起動・runtime・Trainer の内蔵確認に絞る。PR #74 の現在の状態は未確認(不明)。
- 要決定: クリーン Windows の用意方法(VM か実機か)。

### #112 A33 日本語UIに目的・質感・素材量に応じた学習計画を追加する

- 目的: 生成目的と目標質感を先に選び、素材量と推奨学習計画を提示する。
- 先行: #84、#88(implementation-plan A33)。#132、#133、#134 は #88 経由で先行する(§10)。本文は #136、#137 も参照するが、依存には置かない。
- 触るもの: `src/ui/YourSinger.App/MainWindow.axaml`・`MainWindow.axaml.cs`(学習設定部分のみ。既存)。`TrainingJobRecord.cs`(既存。mode の保存)。
- 契約: 選択順は 生成目的(トーク/歌唱/両方)→ 目標声質・質感 → 任意の参考音声。発話教師量・歌唱教師量・声質参照量と、用途別の推奨工程、各段階の初期化方式を表示する。手動の初期化モード(自動/追加学習/最初から新規学習)は高度な設定に残す。選択 mode は job 作成時に保存し、再作成で保持する。
- 完了条件: 目的と質感を維持し、Auto が何をどの素材から学習するか説明する。unsupported を無言で別方式に変換しない。
- テスト: 自動(UI ロジック): `TrainingPlanUiStateTests`(歌唱のみ素材で「トーク+歌唱風」、発話のみで「歌唱+話し声風」、Both で別質感、再作成、未対応 GPU、checkpoint 欠損、入力値の保存)。手動: 画面表示の確認。
- 非目標: 素材 weight の表示(#113)、生成サンプル試聴(#114)、Mod の管理(#139)。
- 解消(D-01): `auto` は段階的な候補計画で、各工程が自分の `resolved_training_mode` を持つ。
- 一部解消(implementation-plan A33、§10): 依存ヘッダには #136・#137 を加えない。プリセット表示部分を #139 側に寄せるかは D-03(待ち)に従う。

### #113 A34 日本語UIに低品質採用・除外理由・区間Weightを表示する

- 目的: 用途別の採用・除外件数と、区間の Q/U/weight、reason_codes を日本語で表示する。
- 先行: #83、#85。
- 触るもの: `src/ui/YourSinger.App/MainWindow.axaml`(品質/素材欄)・`MainWindow.axaml.cs`(採用区間表示のみ)。どちらも既存。
- 契約: contracts-and-weighting.md §1 の `assessment.json`(eligibility: eligible/needs_review/excluded、quality.status、`reason_codes`、`training_weight`)を表示源とする。件数区分は 採用・低品質だが採用・needs_review・除外。未計測・未校正の confidence を 0% 品質と見せない。ユーザーが話者・分類・採用を修正する導線を維持する。
- 完了条件: 低品質で重みを持つ区間を確認できる。全件が品質のみで強制除外されない。
- テスト: 自動(表示モデル): `AssessmentDisplayTests`(eligible 低 Q、有用な高 U、unknown、needs_review、user override、用途別の数値)。手動: 画面確認。
- 非目標: Trainer 本体への weight 適用、学習モード UI。
- 要決定: `reason_codes` の日本語文言の一覧と所有者(contracts-and-weighting.md はコード例のみ)。
- 要決定: 「低品質だが採用」の判定に使う Q の境界(設計正本に境界の定めなし)。

### #114 A35 日本語UIに学習後の検証状態と実音声試聴を追加する

- 目的: 学習後の検証状態を別々に表示し、保存済みテスト音声を試聴できるようにする。
- 先行: #103、#104、#105。
- 触るもの: `src/ui/YourSinger.App/MainWindow.TrainingHistory.cs`・`TrainingHistoryWindow.axaml`(既存)。音声試聴の UI processing(必要最小限。新規)。
- 契約: `verification.json` の各 stage を表示源とする。学習完了、checkpoint 検査、推論試験、外部互換、品質を別状態で表示する。sample WAV は ProjectWorkspace の正しい成果物から読み込む。検証記録のない旧 job は「未検証」と表示する。
- 完了条件: 失敗・未検証を「利用可能」に偽装しない。checkpoint 候補と選定理由を提示し、未評価なら選定保留を表示する。
- テスト: 自動(表示モデル): `VerificationDisplayTests`(検証未実施、失敗、Warning、正常 sample、ファイル欠損、旧 history)。手動: 実際の再生。
- 非目標: 実推論処理(Issue 本文は「A24/A25」と記載)。
- 要決定: 設計正本の区分(学習中/学習完了・検証待ち/生成試験中/利用可能/注意あり/検証失敗)と、Issue の 5 状態の対応づけ。
- 要決定: 試聴の再生手段(UI 層での音声再生ライブラリ)。

## 要決定一覧

各節の要決定を Issue 番号つきで再掲します。

- #99 A20: 適応入力組立の担当クラス名と配置(新規クラスか、既存 `DiffSingerAcousticInputService` の拡張か)。
- ~~#99 A20: Issue が参照する「A19」に当たる Issue 番号(#98 か別か)。~~ → (解消 D-10。上の節を参照)
- #99 A20: 「本人声質・目標 texture への適合」の測定指標と合格基準。
- ~~#99 A20: Backend 非対応のとき、別 Issue 化した時点で本 Issue を完了とするか。~~ → (解消 D-09。上の節を参照)
- ~~#100 A21: 固定 SBV2 に声質条件付けの能力が無い場合、別 Backend を採用するか、unsupported で完了とするか。~~ → (解消 D-09。上の節を参照)
- #100 A21: 「texture を満たす」の評価指標と基準(#99 と共通化するか)。
- #101 A22: `run_manifest.json` のフィールド一覧と schema_version の初期値(設計正本は項目のみで型は未定)。
- #101 A22: 「weight manifest」と設計正本の `assessment.json`/`train_weights.json` の対応(同一物か別物か)。
- #101 A22: 「資源 revision」の表現(固定 commit hash か、lock ファイルの ID か)。
- #102 A23: タイムアウト値と猶予時間。
- #102 A23: 孫プロセス回収の方式(Windows の Job Object か、別の方式か)。
- #102 A23: PR #56 の知見の取り込み範囲。
- #103 A24: 無音・長さ・短文例外の閾値(設計正本は「config/version で固定」とするのみ)。
- #103 A24: 検証に使う日本語文の数と出所(固定リストか、生成か)。
- #104 A25: F0 追従・破綻の測定指標と閾値(設計正本は「測定できる範囲」)。
- ~~#104 A25: OpenUtau 実機検証を自動化するか、手動記録のみか。~~ → (解消 D-15 暫定。上の節を参照)
- #105 A26: 指標の一覧、各指標の重み、比較方法 version の形式(設計正本は「Backend 固定の評価設定と weight を文書化」と述べるのみ)。
- #105 A26: 同点時の扱い(保留か、決定的な順序か)。
- #106 A27: 統合テストの自動/手動の分担と、使う実素材。
- ~~#106 A27: #139(Mod UI)と #136/#137 の画面が統合確認の対象かどうか。~~ → (解消 implementation-plan A27。上の節を参照)
- ~~#107 A28: 「A29へ分離」の指す先が #108(A29)か #109/#110 か(実 GPU 完走は #109/#110 に当たる)。~~ → (解消 D-10。上の節を参照)
- #107 A28: CI の追加 job の要否(`build.yml` の既存 job で足りるか)。
- ~~#108 A29: #128(解析精度の複数モデル協調)を依存に含めるか、完了判定から外すか。~~ → (解消 D-11 暫定。上の節を参照)
- #108 A29: 実素材(歌唱のみ・発話のみ)の入手元と権利状態の管理方法。
- #109 A30: 対象 GPU の最低条件(VRAM)と素材量。
- ~~#109 A30: 公開してよい証跡の範囲(hash のみか、ログ抜粋を含めるか)。~~ → (解消 D-14 暫定。上の節を参照)
- #110 A31: 検証する音域・歌詞・音符の範囲。
- ~~#110 A31: OpenUtau 実機検証の実施環境と記録形式。~~ → (一部解消 D-15 暫定。上の節を参照)
- ~~#111 A32: #74 の状態と、署名未完了を許容して確認するか。~~ → (一部解消 D-16 暫定。上の節を参照)
- #111 A32: クリーン Windows の用意方法(VM か実機か)。
- ~~#112 A33: Auto の定義。設計正本は fine_tune/from_scratch の二択、Issue は複数工程の計画(正本の更新は PR #135 側)。~~ → (解消 D-01。上の節を参照)
- ~~#112 A33: 依存ヘッダに #136・#137 を加えるか、プリセット表示部分を #139 側に寄せるか。~~ → (一部解消 implementation-plan A33 と D-03。上の節を参照)
- #113 A34: `reason_codes` の日本語文言の一覧と所有者(contracts-and-weighting.md はコード例のみ)。
- #113 A34: 「低品質だが採用」の判定に使う Q の境界(設計正本に境界の定めなし)。
- #114 A35: 設計正本の区分(学習中/学習完了・検証待ち/生成試験中/利用可能/注意あり/検証失敗)と、Issue の 5 状態の対応づけ。
- #114 A35: 試聴の再生手段(UI 層での音声再生ライブラリ)。
