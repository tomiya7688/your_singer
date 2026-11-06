# v1学習 実装パケット(A36〜A42)

本書は #132〜#134、#136〜#139(A36〜A42)を、実装者が着手できる単位にまとめた設計書です。#135 は設計を正本化した PR(マージ済み)で、実装 Issue ではないため対象外です。
契約・式・順序の正本は `docs/design/v1-training/` の次の文書で、本書は節名で参照するだけで複製しません。

- [learning-system-design.md](../v1-training/learning-system-design.md)(決定順序・Profile・Stage・計画解決・保存)
- [learning-strategy-protocol.md](../v1-training/learning-strategy-protocol.md)(Preset/Strategy/Provider の manifest、Registry、JSON Lines)
- [texture-preset-taxonomy.md](../v1-training/texture-preset-taxonomy.md)、[data-profile-strategy-selection.md](../v1-training/data-profile-strategy-selection.md)、[strategy-plugin-api.md](../v1-training/strategy-plugin-api.md)
- [goal-texture-data-planner.md](../v1-training/goal-texture-data-planner.md)(§2 棚卸し、§3 Planner、§10 子 Issue の依存順)
- 機械検証用 schema: [texture-preset](../v1-training/schema/texture-preset.schema.json)、[training-strategy](../v1-training/schema/training-strategy.schema.json)、[training-provider](../v1-training/schema/training-provider.schema.json)
- [training-modes-and-backends.md](../v1-training/training-modes-and-backends.md)(§1 モード、§2 ジョブ保存、§3 プレフライト)

Issue 本文と設計正本の食い違いは「要決定」に記録します。既存・新規・不明は、この worktree(`E:/projects/worktrees/packets-a36/your_singer`、origin/main 基点)の Glob/Grep 結果です。

## 前提と注記

- 設計正本(上記)と `implementation-plan.md` は main にマージ済みです。ただし `implementation-plan.md` は A36〜A42 を表に載せていません。この 7 件の順序は goal-texture-data-planner.md §10 と各 Issue 本文が根拠です。
- 設計正本は、`TexturePresetRegistry` / `TrainingStrategyRegistry` / `PluginWorkerAdapter` / `OperationRegistry` / `BackendCapabilityRegistry` を「設計対象で現行 main にない」と述べています。Grep でも `src`・`tests` に該当する型は見つかりません(`ExporterRegistry` だけが既存で、学習戦略の Registry ではありません)。
- 設計正本の schema・JSON 例のうち、サンプルの Operation ID(`builtin.load_tts_base` など)は設計上の例で、実在しません。サンプル manifest を置いただけで「学習できる」と表示してはいけません(examples/README.md)。
- 設計正本は、`resources.json` の形式、`OperationRegistry` の Operation 一覧、BackendCapability の型を定めていません(schema も `resources.json` 用は存在しません)。これらを前提にする箇所は各節の要決定に載せました。
- 型の定義元は決定記録 D-02(`VoiceModelGoal`=#84、`TrainingStagePlan`=#88、`BackendCapability`=#98、`SpeakerDataInventory`=#132)で、状態は「待ち」です。#133・#134・#137 は利用側として扱います。
- 層ルール(docs/architecture.md、v1-training/README.md): UI は UI Messenger を介し、Python を直接呼びません。外部 Provider も Process の Adapter 境界を越えて UI/Data に接続しません。第 4 層を作りません。
- 略記: 「既存」はファイルまたはクラスが存在する、「新規」は存在せず Issue が新設を求める、「不明」は Issue が場所を示さず確認できない。
- Issue 本文の「A36〜A42」は、Issue ヘッダの識別子(#136=A39、#137=A40、#138=A41、#139=A42)と、本文中の呼び名(#132=A36、#133=A37、#134=A38)に基づきます。

## 依存一覧

- `#132 A36 <- #80, #81, #82, #87`
- `#133 A37 <- #132, #98 (関連: #88, #100, #93)。追補節は #136, #137 を使う(ヘッダ外)`
- `#134 A38 <- #132, #98 (関連: #88, #99, #94, #96)。追補節は #136, #137 を使う(ヘッダ外)`
- `#136 A39 <- #84`
- `#137 A40 <- #136, #132, #133, #134, #98`
- `#138 A41 <- #137, #101 (設計由来: 子プロセス回収・取消は #102 の MlWorkerClient 側共通実装を使う。D-13)`
- `#139 A42 <- #136, #137, #138, #112`

順序の根拠(確認済み): [implementation-plan.md](../v1-training/implementation-plan.md) は A36〜A42 を載せていません。`goal-texture-data-planner.md` §10 は、#132 が #80/#81/#82/#87 に、#133・#134 が #132/#98 に依存し、#88(A09)が #84/#132/#133/#134 に依存すると述べます([training-a01-a19.md](training-a01-a19.md) の #88 はこれに合わせてあります)。#137 を #88 や #112 の依存にする記述は、正本のどちらにもありません。

循環: #137 のヘッダは #133・#134 に依存し、#133・#134 の追補節は #137 の Registry を使う前提で書かれています。goal-texture-data-planner.md §10 は「#137 は #136・#132・#133/#134 との契約を確認してから着手」と述べるだけで、先後を決めていません。実際の先後は決定記録 D-03(状態は待ち)が案を持ちます。D-03 が確定するまで、#137 の `TrainingStrategyRegistry` の公開 API と、#133・#134 が呼ぶ列挙 API は、同じ PR 群で擦り合わせて契約を先に固定します。

実装順の根拠(goal-texture-data-planner.md §10): #136 と #132 は並行可。#137 は契約確認後、#138・#139 は Registry 完成後。#112(A33)は #139 の依存で、#136・#137 を本文で参照します(training-a20-a35.md の #112 を参照)。

## 同一ファイルを触るIssue

| ファイル/シンボル                                                                                             | 触るIssue                          | 衝突の注意                                                                                                                                                           |
| ------------------------------------------------------------------------------------------------------------- | ---------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/data/YourSinger.Data/models/TrainingJobRecord.cs`                                                        | #136、#137、#133、#134(+#84、#88)  | 現状は preset・strategy・plan を持たない(`DatasetFingerprint` と `SegmentIds` のみ)。フィールド追加の順序と所有者を先に決める。training-a20-a35.md と同じ表が正本    |
| `src/process/YourSinger.Process/processing/training/TrainingJobPlanningService.cs`                            | #133、#134、#136、#137             | `CurrentInputVersion` / `RequiresRecreation` で旧バッチを再作成扱いにする。preset・strategy を入力へ足すときは version を 1 回だけ上げる                             |
| `src/process/YourSinger.Process/processing/dataset/TrainingDatasetSnapshotService.cs`(`CreateJobFingerprint`) | #136、#137、#132(+#85)             | 現行は `training-job-input-2`(snapshot、speakerId、target、segmentIds)。preset ID/version、参考区間、strategy、profile hash を混ぜる箇所が競合する                   |
| `src/process/YourSinger.Process/processing/training/`(新規 Registry・Planner・Descriptor 群)                  | #132、#133、#134、#136、#137、#138 | 現状このディレクトリは `TrainingInputDirectory.cs` と `TrainingJobPlanningService.cs` のみ。新規クラスは 1 責務 1 ファイルにし、同名の Descriptor を重複して作らない |
| `src/data/YourSinger.Data/models/`(`TexturePresetRecord`、`TrainingStrategyDescriptor`、棚卸し record)        | #136、#137、#132                   | 新規 record。命名と置き場所(Data か Process か)は Issue 間で食い違う(#137 は Data、#138 の `ProviderDescriptor` は Process)。要決定                                  |
| `src/process/YourSinger.Process/processing/ml/bridge/MlWorkerClient.cs`                                       | #138(+#102)                        | 既存は 1 回起動して 1 リクエスト(`request_id`/`command`/`payload`)。#138 の `PluginWorkerAdapter` は別クラスにし、取消・タイムアウトは #102 の共通実装を使う(D-13)   |
| `src/ui/YourSinger.App/MainWindow.axaml`・`MainWindow.axaml.cs`                                               | #139、#112(+#113、#106)            | 学習設定と Mod 画面を区画(または別ウィンドウ)で分ける。既存の別画面は `PhonemeCandidateWindow.cs` などを別ファイルにしている前例がある                               |
| `tests/YourSinger.Process.Tests/`(`TrainingRecreationTests.cs`、`TrainingGuardrailTests.cs`)                  | #136、#137、#133、#134             | 既存の旧ジョブ再作成テストに追記する場合は、テスト名の重複に注意                                                                                                     |
| `tests/worker/`                                                                                               | #138                               | fake worker 用の新規ファイル。既存の `test_protocol.py` を壊さない                                                                                                   |
| `docs/design/v1-training/schema/*.schema.json` と `examples/`                                                 | #136、#137、#138                   | 契約が変わるときは schema を本文へ合わせる(protocol の優先順位)                                                                                                      |

## Issue別パケット

### #132 A36 話者ごとに発話教師・歌唱教師・声質参照の実効データ量を棚卸しする

- 目的: 話者ごとに、発話教師・歌唱教師・声質参照の実効量を別軸で集計し、後続の Planner・Registry が使う入力を作る。学習方式は決めない。
- 先行: #80(Assessment)、#81(適格性)、#82(音素/音域の価値)、#87(声質特徴参照)。
- 触るもの: `SpeakerDataInventoryService`(新規。Issue は「提案」名。置き場所は Issue になし。`src/process/YourSinger.Process/processing/` 配下のどこか、不明)。棚卸し record(新規。`src/data/YourSinger.Data/models/` が自然だが Issue になし)。入力は `UniversalVoiceDatasetRecord`・`TrainingDatasetSnapshotService`(既存)。テストは `tests/YourSinger.Process.Tests/`(既存)。
- 契約: 出力は `speech_teacher {raw_seconds, eligible_seconds, unique_utterances, phoneme_coverage}`、`singing_teacher {raw_seconds, eligible_seconds, unique_phrases, pitch_coverage}`、`timbre_reference {eligible_seconds, representative_segment_ids, acoustic_qualities}`、`texture_evidence [{texture_id, reference_ids, support_status}]`、`unknown_metrics[]`、`warnings[]`、`dataset_fingerprint`、`inventory_version`。集計規則は learning-system-design.md §4.2(raw/eligible/distinct を別集計、origin group で重複除外、`unknown` を 0 にしない)。Profile の派生タグ(`speech_dominant`、`singing_dominant`、`mixed_rich`、`small_data`、`abundant_data`、`sparse_but_valuable` ほか)は重複可で、根拠は有効量・声質参照・独立フレーズ数・音素/音域・品質・unknown 状態。タグの閾値は data-profile-strategy-selection.md のとおり strategy の `requirements` が持ち、全体で固定しない。
- 完了条件: speech 0 秒・singing 50 分でも `timbre_reference` > 0、singing 0 秒・speech 主体でも `timbre_reference` > 0。測定不能を 0 秒の確定情報と混同しない。ソース・manual override・policy の変化で fingerprint が更新される。目標 target と texture を素材 Profile から勝手に変更しない。
- テスト: 自動(クラス名は Issue になし): 上の 2 ケース、収録重複・区間除外・別話者・モデル未計測の区別、正常・空・破損・旧 record・キャッシュ再利用。実GPU検証は不要。
- 非目標: 学習方式/Backend の決定、UI 表示、TTS/歌唱 Trainer の実行。
- ~~要決定: 出力フィールド名の対応。Issue は `unique_utterances`・`unique_phrases`・`timbre_reference`・`inventory_version`、設計正本は `independent_utterances`・`independent_phrases`・`identity_reference`・`inventory_revision`(goal-texture-data-planner.md §2)。strategy の `requirements.metric`(`eligible_seconds`、`independent_utterances`、`independent_phrases`、`distinct_sources`、`phoneme_coverage_count`、`pitch_bins_observed`)と同じ名前に揃えないと照合できない。~~ → 暫定(#132): 設計正本の名前を採用する(`independent_utterances` / `independent_phrases` / `identity_reference` / `inventory_revision`)。strategy の `requirements.metric` と同じ名前で照合できる。Issue の `unique_*` / `timbre_reference` / `inventory_version` は使わない
- ~~要決定: `phoneme_coverage`・`pitch_coverage` の型。Issue は名前のみで、設計正本の metric は件数(`phoneme_coverage_count`、`pitch_bins_observed`)。~~ → 暫定(#132): 件数にする(`phoneme_coverage_count`、`pitch_bins_observed`)。音素は推定でない実測の語彙内音素の種類数、音域は6半音幅binのうち実測F0で観測した数
- ~~要決定: 「品質」「多様性」を `requirements` の metric にするか(#137 のヘッダは両方を Profile の軸と述べるが、strategy schema の `metric` 列挙に無い)。~~ → 暫定(#132): metric にしない。棚卸しは秒数・独立数・カバレッジ・測定状況だけを持ち、品質と多様性は含めない(strategy schema の `metric` 列挙に無いため)
- ~~要決定: 棚卸し record と `SpeakerDataInventoryService` の置き場所。保存先は learning-system-design.md §8 の `metadata/speaker-data-inventory/<speaker-id>.json` が目標 schema(実パスへの一括移行はしない)。~~ → 暫定(#132): record は `YourSinger.Data/models/SpeakerDataInventory.cs`、サービスは `processing/dataset/SpeakerDataInventoryService.cs`。保存せず都度導出する(`metadata/speaker-data-inventory/` への保存は後続)
- ~~要決定: キャッシュ再利用の無効化条件。設計正本は「`source_fingerprint`、手動修正 revision、プロフィール計算 version を含める」とのみ(§4.2-8)。~~ → 暫定(#132): `inventory_revision` を素材の fingerprint・手動修正 revision・計算規則の版・集計結果から作る。保存・再利用の仕組みは後続

### #133 A37 ユーザー指定のトーク目的・質感と素材量から Talk 学習工程案を構成する

- 目的: ユーザーの talk 目的と target texture を固定し、棚卸しと BackendCapability から、発話能力と話者声質を分けた段階的な計画候補を返す。歌唱しかなくても候補を検討する。
- 先行: #132、#98(BackendCapability)。関連: #88(計画確定/Preflight)、#100(歌唱→トーク声質の実接続)、#93(SBV2 初期化)。追補節は #136(`TexturePresetRegistry`)と #137(`TrainingStrategyRegistry`)を使う(依存ヘッダには無い)。
- 触るもの: `TalkTrainingStrategyPlanner`(新規。Issue は「提案」名。配置は Issue になし。`src/process/YourSinger.Process/processing/training/` が自然)。`TrainingStagePlan`・`VoiceModelGoal`・`BackendCapability` は他 Issue が定義(D-02)。テストは `tests/YourSinger.Process.Tests/`(既存)。
- 契約: 入力は `VoiceModelGoal(target=talk, texture, references)`、`SpeakerDataInventory`、BackendCapability 一覧。出力は可能な `TrainingStagePlan[]`、使うデータの `role`(speech teacher / singing timbre reference など)、代替案、unsupported reasons、warnings。主要 stage 例は `speech_base`、`speaker_timbre_adaptation`、`talk_style_adaptation`、`evaluate_unseen_text`。role の語彙は learning-system-design.md §2.2(`speech_teacher`、`identity_reference`、`texture_reference`、`feature_only`)。決定順は §7(目標を固定 → 棚卸し → 登録戦略の列挙 → 能力チェック → 候補 → 確定)。
- 追補(Registry 連携): 目標 trait を `TexturePresetRegistry` から取得し、`TrainingStrategyRegistry` から talk に対応する方式を列挙する。Source Profile(`speech_dominant`、`singing_dominant`、多量・少量など)を複数同時に評価し、対応しない方式には理由コードを返す。同じ歌唱中心素材で `classic_synthetic` と `expressive_complex` の候補レシピが変わることを検査する。
- 完了条件: 歌唱のみ + talk + singing_like の計画を、対応 Backend があれば生成する(非対応は理由付き未対応)。発話豊富なら発話教師を使う候補を作る。texture が違えば計画と参考区間が変わる。モード暗黙 fallback なし。誤話者・空の声質参照・無効 Backend で明示的に失敗する。歌唱が優勢でも target=singing へ変更しない。歌唱音声を通常発話の正解音声・韻律教師として流用しない。参考区間の話者・hash・同一性を検証する。固定 SBV2 に声質条件付けの能力が無い場合は unsupported とし、対応 Backend の追加は別 Issue(#100 が Backend 採否を持つ)。
- テスト: 自動(クラス名は Issue になし): 歌唱のみ+talk(Backend あり/なし)、発話豊富、texture 違い、別話者の参考区間、空の声質参照、無効 Backend、同一話者の talk と singing が別目標になる場合の独立。
- 非目標: 歌唱プラン、GPU 資源の解決(#88)、Trainer 実装、UI。
- 要決定: 依存方向。ヘッダは #132・#98 のみで、追補は #136・#137 を使う。#137 のヘッダは #133 に依存する(循環。D-03)。
- 要決定: 「singing_like」はビルトイン preset の一覧(`classic_synthetic`、`clean_controlled`、`natural`、`expressive_complex`、`soft_breathy`、`powerful`、`characterized`、`reference_match`)に無い。goal-texture-data-planner.md §1 の例にだけ現れる。テストの入力を、どの preset(または `reference_match` + 参考区間)で表すか。
- 要決定: Issue の role 名(speech teacher、singing timbre reference)と設計正本の role 名(`speech_teacher`、`identity_reference`)の対応。
- 要決定: 候補比較の「推奨(未検証)」表示の保持先(learning-system-design.md §7.1 は選定理由と棄却理由コードの保存を求めるが、型は未定)。

### #134 A38 ユーザー指定の歌唱目的・質感と素材量から Singing 学習工程案を構成する

- 目的: ユーザーの singing 目的と target texture を固定し、歌詞/音符の制御と話者声質を分けた段階的な候補を返す。発話しかなくても歌唱計画を検討する。
- 先行: #132、#98。関連: #88、#99(トーク→歌唱の声質実接続)、#94(DiffSinger 音響初期化)、#96(Duration)。追補節は #136・#137 を使う(ヘッダ外)。
- 触るもの: `SingingTrainingStrategyPlanner`(新規。配置は Issue になし)。#133 と同じ型を使う(D-02)。テストは `tests/YourSinger.Process.Tests/`(既存)。
- 契約: 入力は `VoiceModelGoal(target=singing, texture, references)`、`SpeakerDataInventory`、BackendCapability 一覧。出力は音響・長さ・必要なら音高・表現などの `TrainingStagePlan[]`、教師・声質参照の役割、代替案、不成立理由。stage 例は `singing_base`、`speaker_timbre_adaptation`、`acoustic/duration`、`evaluate_unseen_notes`。歌唱計画の制約は learning-system-design.md §6.2。歌唱の構成段階の表示は D-06(音響のみ/音響+duration/+pitch・variance。「利用可能」の最低線は音響+duration)。
- 追補(Registry 連携): `TexturePresetRegistry` と `TrainingStrategyRegistry` から singing に対応する方式を取得し、Source Profile の歌唱有効量・カバレッジ・声質参照・品質・多量/少量を同時に評価して、適格条件と根拠を返す。歌唱量が多いほど必ず scratch にする固定ルールを持たない。`classic_synthetic` と `expressive_complex` が、音高・音素長・表現条件などの異なる実装計画になることを検査する。
- 完了条件: 発話のみ + singing 目的 + talk-like texture を、対応 Backend なら計画可能にする。歌唱豊富なら実測歌唱教師を使う計画を作れる。必須 submodel の欠損・未校正 F0・歌詞不明を隠さない。同一話者の talk と singing が別目標でも両プランが独立する。発話優勢でも target=talk へ変更しない。発話から未知の歌唱音域・ビブラート・音符教師を捏造しない。OpenUtau/DiffSinger の必須 submodel と Vocoder を考慮し、能力不足を明示する。texture の参考区間と制約を保持し、別計画・別 fingerprint にする。
- テスト: 自動(クラス名は Issue になし): 発話のみ+singing、歌唱豊富、必須 submodel 欠損、未校正 F0、歌詞不明、talk と singing の独立。
- 非目標: TalkPlanner、Trainer 改変、GPU preflight と計画の最終決定(#88)、UI。
- 要決定: #133 と同じ(依存の循環、`singing_like` 相当の preset 表現、role 名の対応)。
- 要決定: pitch/variance の扱い。設計正本は「存在しない pitch/expression モデルを必須にしない」(§6.2)、D-06 は pitch/variance を任意とする。Planner が必須 submodel をどの型で BackendCapability から受け取るか(#98 の型待ち)。

### #136 A39 質感プリセットの Schema と Registry を実装する

- 目的: 質感 preset の宣言形式を定義し、組み込みと Mod の preset を同じ Registry で検証・登録する。
- 先行: #84(目標を Job へ保存)。
- 触るもの: `src/data/YourSinger.Data/models/` の `TexturePresetRecord`(新規)。`src/process/YourSinger.Process/processing/training/` の `TexturePresetRegistry`(新規)。`tests/YourSinger.Process.Tests/`(既存)。機械検証用 schema は [texture-preset.schema.json](../v1-training/schema/texture-preset.schema.json)(既存。契約の正本)。
- 契約: manifest は `schema_version`(`"1"`)、`preset_id`(`publisher.name` 形式、正規表現あり)、`version`(SemVer)、`display_name_ja`、`description_ja`、`supported_targets`(`talk`/`singing`)、`traits`(`syntheticness`・`expressiveness`・`breathiness`・`tension`・`articulation` の 0〜1。`additionalProperties: false`)、`required_capability_tags`、`preferred_strategy_tags`。Registry は `(id, version)` を一意キーとし、同じキーの別ハッシュと組み込み ID の乗っ取りを拒否する。列挙は `id` → SemVer → provider の順(learning-strategy-protocol.md §2)。読込失敗はその Mod だけ `invalid` にする。状態は `available`/`partial`/`unavailable`/`unsupported`/`invalid`/`disabled`。初期候補は taxonomy の 8 件(`builtin.classic_synthetic`、`clean_controlled`、`natural`、`expressive_complex`、`soft_breathy`、`powerful`、`characterized`、`reference_match`)。実現できるか確認できない preset は `unsupported` または `preview` とし、すべてを実装済みと表示しない。preset ID/version とユーザー選択の参考区間を、学習 Job の fingerprint へ渡す。
- 完了条件: preset が出力 target と独立に選択でき、音声モデル自体を強制変更しない。preset 未対応は失敗/未対応として区別され、選択しただけで音声品質改善を偽らない。Mod JSON を追加しても本体の再ビルドが要らない。
- テスト: 自動(クラス名は Issue になし): built-in preset の読込、Mod の追加、ID 競合、version 違い、traits が 0〜1 の外、target 不一致、Job fingerprint への反映、破損 JSON と unsupported schema version の理由付き拒否。実音声の差の確認は #136 の範囲外(下の注記)。
- 非目標: 学習方式の登録(#137)、外部 worker の実行(#138)、UI(#139)。
- 注記: 「名前だけ変わった成果物を preset 成功と表示しない」(learning-system-design.md §3、taxonomy §5)の検証は、実 stage と実生成が揃う #99・#100・#108 側の範囲。#136 は Registry 上の状態表示までを担当する。
- ~~要決定: Issue の「reference 要件」と「strategy tags」。schema には `required_capability_tags`・`preferred_strategy_tags` はあるが、reference(参考区間)の要件を持つ項目が無い。参考区間は preset ではなく Goal 側(`reference_segment_ids`)の入力であるため、preset 側で何を宣言するか。~~ → 暫定(#136): preset 側には参考区間の要件を持たせない。参考区間はジョブの目標(`reference_segment_ids`)の入力で、preset は `required_capability_tags`(例: `same_speaker_reference`)だけで必要性を宣言する。`preferred_strategy_tags` は schema どおり
- ~~要決定: ビルトイン preset 8 件の `traits` の初期値と `required_capability_tags`。taxonomy は目標特徴を文章で述べるのみ(D-00: 根拠が無い間は保存するだけで合否に使わない)。~~ → 暫定(#136): `BuiltInTexturePresets` に方向を示す初期値を置く(根拠が得られるまで保存・表示のみで合否に使わない)。数値は検証済みではない
- ~~要決定: `characterized` は taxonomy の表と Issue の列挙に出るが、learning-system-design.md §3 の表には無い。初期候補に含めるか。~~ → 暫定(#136): 初期候補に含める(8件)
- ~~要決定: `TexturePresetRecord`(Data 層)と Registry(Process 層)の分担、および Mod 用 `preset.json` の読込を担う層(#139 の「Registry の読込導線」との境界)。~~ → 暫定(#136): record は `YourSinger.Data/models/TexturePresetRecord.cs`、検証(`TexturePresetValidator`)と登録簿(`TexturePresetRegistry`)は `processing/training/`。Mod の `preset.json` は文字列またはファイル単位で `RegisterMod` / `RegisterModFile` に渡す。ディレクトリの走査と読込導線は #139
- ~~要決定: fingerprint への渡し方。現行の `CreateJobFingerprint` は `training-job-input-2`。D-07 の `input_fingerprint`(目的/質感を含む)と `run_fingerprint` の分離に合わせる。~~ → 暫定(#136): `TrainingTextureRequest.PresetVersion`(任意)を追加し、ジョブの契約 fingerprint に `preset=<id>@<version>` と参考区間を含める。version が無いときは従来と同じ値のまま(既存ジョブの fingerprint を変えない)。入力全体の fingerprint は #85 の `training-input-v1` で契約 fingerprint を含む

### #137 A40 組み込み/Mod 学習戦略の Registry と宣言 JSON の検証を実装する

- 目的: 組み込みと Mod の学習戦略を同じ Registry に登録し、宣言 JSON の検証と、データプロフィールに基づく適合判定を行う。
- 先行: #136、#132、#133、#134、#98。
- 触るもの: `src/process/YourSinger.Process/processing/training/` の `TrainingStrategyRegistry`(新規)。`src/data/YourSinger.Data/models/` の `TrainingStrategyDescriptor`(新規)。`tests/YourSinger.Process.Tests/`(既存)。schema は [training-strategy.schema.json](../v1-training/schema/training-strategy.schema.json)(既存。契約の正本)。`OperationRegistry`(新規。Issue は名前を挙げず「存在しない operation を拒否」とのみ述べる。設計正本の型名で、置き場所は不明)。
- 契約: manifest は `strategy_id`、`version`、`supported_targets`、`supported_preset_ids`(空なら汎用)、`requirements`(`role` × `metric` × `op`(`gte`/`lte`/`eq`/`gt`/`lt`)× `value`。算術式・任意コード・eval・動的 import は禁止)、`required_capabilities`、`stages`(`id`、`operation_id`、`depends_on`、`input_roles`、`output_types`)、`expected_output_types`、`resource_manifest`。DAG 検査は learning-strategy-protocol.md §4 の 8 項目(id 一意、`depends_on` の参照先、循環禁止、`operation_id` 登録済み、入力 role の型、出力型の接続、`reusable_model` 条件、資源 hash/ライセンス/GPU、canonical JSON と plan hash)。計測状態が `unknown` なら条件を `unknown` とし 0 と同一視しない(必要なら `needs_measurement` で保留)。複数の軸(会話/歌唱の有効量、総量、音素/音域、多様性、品質)を同時に評価し、固定分数の閾値で決め打ちしない。Planner は候補と不成立理由を取得し、選んだ strategy/stage を計画 manifest へ保存する。選定の順位は §7.1(辞書式。最後は `strategy_id + version + provider_id` の文字列順)。JSON Mod は登録済み operation の組み合わせだけを許可し、任意コードや未知 operation を黙認しない。再現のため、strategy ID/version/provider/preset/profile hash を job へ保存する。
- 完了条件: 会話優勢/歌唱優勢/混在/多量/少量で候補と理由が変わる。ユーザーの target/texture を Profile から上書きしない。登録・削除しても旧 Job の内容を保持する。
- テスト: 自動(クラス名は Issue になし): stage DAG(循環・存在しない依存)、manifest 無効、priority 衝突、Tag 組合せ、同じ素材でも別質感、別素材でも同一目標、登録・削除時の旧 Job 保持、provider 違い、重複 ID、schema 違い、未知 operation、`unknown` を 0 にしない。
- 非目標: 新しい学習アルゴリズム自体の実装、独自 worker の起動(#138)。
- ~~要決定: 「priority 衝突」。strategy schema に `priority` 項目は無く、選定順位は §7.1 の辞書式(最後は文字列順)。Issue のテスト項目を、どの定義で書くか。~~ → 暫定(#137): `priority` 項目は作らない。順位は決定的に、成立(met)が先、次に対象プリセットに特化した戦略、strategy_id の文字列順、新しい version の順とし、同順位は生じない。§7.1 の完全な辞書式の定義が確定したら置き換える
- ~~要決定: 「provider 違い」の保持場所。strategy manifest は provider を持たず、operation が Provider に紐づく(provider の `operations[].operation_id`)。Registry のキーは `(id, version)`、衝突は hash で判定(§2)。~~ → 暫定(#137): Registry のキーは `(id, version)` で、衝突は hash で判定する。ジョブには `TrainingStrategyReference`（戦略 ID・version・hash・provider ID・プリセット・棚卸し revision）を保存する。provider の実体は #138
- ~~要決定: `OperationRegistry` の初期 Operation 一覧と、組み込み strategy の定義。examples の Operation ID は実在しない。D-03 の読み替えのとおり、#133・#134 が組み込み戦略を提供する案。~~ → 暫定(#137): `OperationRegistry` は空で始め、operation と組み込み戦略は #133・#134 などが登録する(`RegisterBuiltIn`)。未登録の operation を含む戦略は `unknown_operation` で拒否する
- ~~要決定: `TrainingStrategyDescriptor` を Data に置く(#137)か、`ProviderDescriptor` を Process に置く(#138)かの整合。両者の関係(Descriptor が manifest の読込結果か、保存 record か)。~~ → 暫定(#137): `TrainingStrategyDescriptor` は manifest の読込結果として `YourSinger.Data/models/` に置く。保存するのはジョブ側の `TrainingStrategyReference`。`ProviderDescriptor` は #138 で別に定義する
- ~~要決定: 品質/多様性を判定に使う方法。`metric` 列挙に無い(#132 の要決定と共通)。~~ → 暫定(#137): `metric` 列挙にある指標だけを判定に使う(品質・多様性は使わない。#132 と共通)

### #138 A41 第三者学習 Worker Provider の IPC 契約・登録・実行を実装する

- 目的: 第三者が本体を再ビルドせず、自前の学習 stage を manifest と worker で追加できるようにする。
- 先行: #137、#101(学習実行記録)。D-13: 子プロセス回収・取消・タイムアウトは #102 が `MlWorkerClient` 側に共通実装し、#138 はそれを使う。
- 触るもの: `src/process/YourSinger.Process/processing/ml/bridge/` の `PluginWorkerAdapter`(新規)。`src/process/YourSinger.Process/processing/training/` の `ProviderDescriptor`(新規)。`tests/worker/` と `tests/YourSinger.Process.Tests/`(既存)。schema は [training-provider.schema.json](../v1-training/schema/training-provider.schema.json)(既存。契約の正本)。既存の `MlWorkerClient.cs` は参照のみ(`SendAsync` が起動ごとに 1 リクエストを送る形で、持続接続を想定しない)。
- 契約: `provider.json` は `provider_id`、`version`、`protocol_version`(`"1"`)、`runtime`(`kind=external_process`、`entry`)、`operations`、`resource_manifest`、`permissions_requested`(`read_provided_artifacts`/`write_job_artifacts`)、`license_file`。パスは Provider ルートからの相対のみで、絶対パス・`..`・シンボリックリンクによる脱出は禁止。stdin/stdout は 1 行 1 JSON(learning-strategy-protocol.md §6)。request は `protocol_version`、`request_id`、`operation`(`probe`/`plan`/`execute`/`verify` のうち実装済み分だけ)、`provider_id`、`job_id`、`payload`。response は `type`(`result`/`error`)、`ok`、`error.code`/`message_ja`/`retryable`。警告・学習ログは stderr または許可された Job ログ。WAV・checkpoint のバイナリは IPC に載せない。Host は `request_id`、`job_id`、`plan_hash`、`provider_id`、operation/version、入力 artifact の `sha256`、許可した出力先を照合し、不正な JSON・サイズ上限超過・別 request の応答・予期しない event を拒否する。成功応答でも成果物の存在・許可 path・hash・型・必須内容を Host が再確認する。runtime/model weights が必要な Mod は、配布パッケージ内に同梱されていることを preflight で確認する(ユーザーに Python/pip を求めない)。外部コードを含む Mod は、ユーザーの明示有効化と出所・要求権限の提示が無いと起動せず、`probe` も有効化後だけ。manifest の表示や静的照合で外部 exe を実行しない。外部プロセス化を OS sandbox と偽らない(実際に強制できない権限は `requested` と表示する)。
- 完了条件: 第三者が `YourSinger.exe` を再ビルドせず、自前の学習 stage を manifest/worker から追加できる。worker が落ちても他の Mod や既存 Trainer を壊さない。未知の権限・未対応 API・資源欠損を黙ってスキップして成功扱いしない。旧 Job は元の provider hash が無ければ再現不可と表示する(閲覧は可)。
- テスト: 自動(クラス名は Issue になし): fake worker の IPC、protocol mismatch、malformed JSON、worker timeout、unexpected file、checksum mismatch、明示有効化/無効化、旧 Job 再現不可。キャンセル・子プロセス回収は #102 の共通実装に対するテストを使う。実 Python worker の実行は #138 の範囲外。
- 非目標: プラグインマーケットプレイス、配布アップデータ、OS の完全な sandbox。
- 要決定: Issue の「supported operations」と schema の `operations` の対応。schema の `operations` は `operation_id`+`version` で、`probe`/`plan`/`execute`/`verify` の宣言項目は provider.json に存在しない。実装済みの verb を `probe` 応答で受けるのか、manifest に項目を足すのか。
- 要決定: Issue の「schema/protocol/API version」。schema には `schema_version` と `protocol_version` はあるが、「API version」に当たる項目が無い。
- 要決定: `resources.json` の形式(hash・ライセンスの項目)。設計正本は用途のみで schema が無い。
- 要決定: 既存の `MlWorkerClient` の request 形式(`request_id`/`command`/`payload`、1 起動 1 リクエスト)と、Provider 用 JSON Lines(`protocol_version`/`operation`/`provider_id`)の関係。`PluginWorkerAdapter` が既存形式を再利用するか、別実装か。
- 要決定: 出力サイズ上限、タイムアウト値、猶予時間(D-13 は #102 が持つが、値は決まっていない。D-00 に従い設定と version で持つ)。

### #139 A42 学習 Mod の配置・再読み込み・有効化と日本語状態表示を実装する

- 目的: Mod の配置を一覧し、再読み込み・有効化・状態表示を日本語 UI で行えるようにする。
- 先行: #136、#137、#138、#112(目的/質感 UI)。
- 触るもの: `src/ui/YourSinger.App/`(`MainWindow.axaml`・`MainWindow.axaml.cs` は既存。Mod 画面は新規。ファイル名は Issue になし)。`src/process/YourSinger.Process/processing/training/` の Registry 読込導線(新規)。`tests/YourSinger.Process.Tests/`(既存)。
- 契約: 一覧する対象は `preset.json`・`strategy.json`・`provider.json`(Issue は `mods/strategies/` と記す)。表示状態は 利用可能・不足(`unavailable`)・無効(`disabled`)・非対応(`unsupported`)・不正(`invalid`)・一部対応(`partial`)(strategy-plugin-api.md §3、protocol §2)。エラーはその Mod に局所化する。宣言型 Mod は再読み込みで追加できる(manifest の再検証のみで、処理中 Job を別 version に切り替えない)。コード付き Mod は要求権限と出所を示し、ユーザーの明示有効化を要求する(`provider.json` を置くだけで有効化しない)。UI の順は 生成目的 → 質感 preset → 実効データ量 → 推奨戦略と理由(learning-system-design.md §9)。Mod が増えても目的を変えない。無効化/削除でも旧 Job 履歴を維持し、再実行できなければ再導入/再作成を案内する。「利用可能」は完成した音声を意味しない(protocol §8)。
- 完了条件: 宣言 Mod を追加すると、本体の再ビルドなしで preset・戦略が現れる。本体/Mod が未対応の質感・戦略は利用可能に見えない。同じ入力でも選択 preset に応じて推奨計画が変わり、実生成結果の検証へ進める。
- テスト: 自動(UI ロジック/Registry):日本語表示、再読み込み、zip 解凍パスの安全性、Mod ID 衝突、コード付き Mod の確認、無効化、既存 Job 履歴。手動: 画面表示(Mod 画面の自動 UI テストの有無は Issue になし)。
- 非目標: 学習モード選択(#112)、モデル推論による生成、独自 worker の計算実装。
- 要決定: Mod の配置先。設計正本は `%LOCALAPPDATA%/YourSinger/mods/<pack-id>/`(protocol §7、strategy-plugin-api.md §2)、Issue は `mods/strategies/`。階層(pack ごとのフォルダか、種別ごとか)も食い違う。
- 要決定: zip 解凍の入口。zip 取込を #139 が実装するのか、手動配置のみか(Issue のテストに「zip 解凍パス安全性」)。既存のパス安全性ヘルパーは `ProjectWorkspace`(相対パス解決)など。Mod 用に流用するか新設するか。
- 要決定: 「提供元/署名状態」の表示(protocol §7 は表示を求めるが、署名の検証手段は設計正本に無い)。
- 要決定: #112 との境界。#112 の「依存ヘッダに #136・#137 を加えるか、プリセット表示部分を #139 側に寄せるか」(training-a20-a35.md の #112 の要決定、D-03)。

## 要決定一覧

各節の要決定を Issue 番号つきで再掲します。決定記録に ID がある項目は、その状態に従います。

- #132 A36: 出力フィールド名の対応(`unique_*`/`timbre_reference`/`inventory_version` と `independent_*`/`identity_reference`/`inventory_revision`、strategy の `metric` 名)。
- #132 A36: `phoneme_coverage`・`pitch_coverage` の型。
- #132 A36: 品質・多様性を `requirements` の metric にするか。
- #132 A36: 棚卸し record と Service の置き場所。
- #132 A36: キャッシュ再利用の無効化条件。
- #133 A37: 依存方向(#137 との循環。D-03)。
- #133 A37: 「singing_like」に当たる preset の表現。
- #133 A37: Issue の role 名と設計正本の role 名の対応。
- #133 A37: 「推奨(未検証)」と棄却理由コードの保持先。
- #134 A38: #133 と同じ要決定(依存の循環、preset の表現、role 名の対応)。
- #134 A38: pitch/variance の扱いと、必須 submodel の受け取り型(D-06、#98 待ち)。
- #136 A39: reference 要件・strategy tags を宣言する項目。
- #136 A39: ビルトイン preset 8 件の traits の初期値と `required_capability_tags`(D-00)。
- #136 A39: `characterized` を初期候補に含めるか。
- #136 A39: `TexturePresetRecord` と Registry の層分担、Mod の読込を担う層。
- #136 A39: fingerprint への渡し方(D-07)。
- #137 A40: 「priority 衝突」の定義。
- #137 A40: 「provider 違い」の保持場所。
- #137 A40: `OperationRegistry` の初期 Operation 一覧、組み込み strategy の定義(D-03)。
- #137 A40: `TrainingStrategyDescriptor` と `ProviderDescriptor` の置き場所と関係。
- #137 A40: 品質・多様性の判定方法(#132 と共通)。
- #138 A41: 「supported operations」と schema の `operations` の対応。
- #138 A41: 「API version」に当たる項目。
- #138 A41: `resources.json` の形式。
- #138 A41: 既存 `MlWorkerClient` の request 形式と Provider 用 JSON Lines の関係。
- #138 A41: 出力サイズ上限・タイムアウト・猶予時間(D-00、D-13)。
- #139 A42: Mod の配置先と階層。
- #139 A42: zip 取込の有無とパス安全性の実装。
- #139 A42: 「提供元/署名状態」の表示手段。
- #139 A42: #112 との境界(D-03)。
