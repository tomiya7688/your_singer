# Your Singer 学習システム基本・詳細設計（v1）

> **文書状態:** 実装設計の正本（提案契約・未実装部分を含む）
>
> **対象:** v1.0.0の目的・質感プリセット・基本素材プロフィール・学習戦略選定・Mod拡張
>
> **更新基準:** 2026-11-05
>
> **重要:** 本文の新しい型・保存ファイル・Operationは「実装すべき仕様」であり、現行mainに存在すると主張するものではない。既存クラスとの接続は§10を参照する。

## 0. 設計決定と文書の優先関係

この文書は「ユーザーが何を生成したいか」と「どの方法で学習させるか」を統合する**学習システムの正本**である。プリセットの候補説明は `texture-preset-taxonomy.md`、素材方向性の説明は `data-profile-strategy-selection.md`、Modの概念説明は `strategy-plugin-api.md`、Worker/登録の詳細契約は `learning-strategy-protocol.md` に分割する。これらに矛盾がある場合、本書の決定順序・状態・製品の完成条件を優先し、細部のschemaは `learning-strategy-protocol.md` を優先する。

**確定した製品要求**

1. ユーザーが**生成目的（トーク／歌唱／両方）**と**希望の声の質感**を決める。素材の多数派を理由に目標を勝手に変更しない。
2. 入力フォルダを会話用・歌唱用に仕分けさせない。会話・歌唱・混在・複数話者を内部で分析する。
3. 素材から、会話の教師量・歌唱の教師量・本人声質参照量・独立発話数・音素/音域カバレッジ・品質/測定状態を別々に求める。
4. **登録済みの学習戦略を目的×質感×素材の方向性×実行能力で選び、必要な複数工程を組み合わせる。** Fine-tuning/Scratchは工程ごとの初期化方式でしかない。
5. 歌唱素材主体→トーク、会話素材主体→歌唱も正式な入力組み合わせ。教師ラベルを偽装しない。対応するモデル生成経路がない場合は理由を返す。
6. 古いボカロ的な合成感は「劣化」ではなく表現上の目標。自然さ・音色の忠実性とプリセット一致度を別々に評価する。
7. ModユーザーはJSONでプリセットや既存処理の学習レシピを登録できる。開発者は別プロセスProviderを接続できる。既存のExporter拡張とは別責務。
8. 生成物は**実際に読み込んで任意文章／歌詞＋音符から推論可能な再利用モデル**である。単に試聴用WAVができただけでは要求達成としない。

**非目標:** 新しい独自TTS/SVSモデルを一から研究開発すること、すべての外部モデルを無差別に同梱すること、モデル間checkpoint形式を根拠なく変換すること、データがあれば全ての希望を必ず実現できると表示すること。

## 1. 全体アーキテクチャと責務

```text
[UI] 素材投入 → 対象話者選択 → 生成目的 → 質感プリセット
  │
  ▼
[Process] VoiceGoalService（ユーザー希望を不変記録）
  │
  ├─→ Universal Voice Dataset（元観測・補正・分類・話者・来歴）
  │    └─→ SpeakerDataInventoryService
  │          ├ speech_teacher（文章と発話の教師）
  │          ├ singing_teacher（歌詞/音符と歌唱の教師）
  │          ├ identity_reference（同一話者の音色）
  │          └ texture_reference（希望質感に合う同一話者の例）
  │
  ├─→ TexturePresetRegistry（組込み+Mod）
  ├─→ TrainingStrategyRegistry（組込み+Modの宣言）
  └─→ BackendCapabilityRegistry（Trainer/推論/Exporterの実能力）
            │
            ▼
     TalkTrainingStrategyPlanner  または  SingingTrainingStrategyPlanner
            │  戦略候補と不成立理由
            ▼
     TrainingPlanResolver（能力・素材・資源・品質証拠で選択）
            │
            ▼
     Immutable TrainingPlan（有向非巡回Stage群と入力hash）
       ├ 入力構築・weighting
       ├ 汎用発話/歌唱能力の利用
       ├ 声質・表現の適応
       ├ モデル固有の追加学習/新規学習
       ├ 合成時の制御設定と出力
       └ 推論・互換性・質感の評価
            │
            ▼
     ModelPackage + EvaluationReport + Provenance
```

UI LayerからPythonへ直接アクセスせず、Process Commander/Processing/ML Bridge経由。保存責務はData Layer。外部学習ProviderもProcessのAdapter境界を越えて勝手にUI/Dataへ接続しない。高価な解析結果を新しいプリセットを選ぶたびに作り直さない。

## 2. 用語とデータ契約

| 概念（論理型）         | 意味                                     | 主な必須値                                                                        |
| ---------------------- | ---------------------------------------- | --------------------------------------------------------------------------------- |
| `VoiceGoal`            | ユーザー希望。素材分析から変更しない     | speaker_id, target, preset_id/version, optional reference_ids, output_requirement |
| `TexturePreset`        | 目指す質感。モデルやcheckpoint名ではない | id, version, display_name_ja, traits, required_traits, targets                    |
| `SpeakerDataInventory` | 解析結果の量・分布・独立性               | source_fingerprint, speaker_id, role_metrics[], status                            |
| `SegmentRole`          | その区間の使い道                         | segment_id, target, role, eligibility, source_kind, provenance                    |
| `TrainingStrategy`     | データ条件と目的・質感に合う方法の宣言   | strategy_id/version, supported_targets, required_capabilities, stage_graph        |
| `BackendCapability`    | 実在するTrainer/推論/Exporterの能力      | backend_id/version, supported_operations, input_roles, output_types               |
| `TrainingPlan`         | 選定された工程の不変DAG                  | goal_hash, inventory_hash, stages[], resources[], strategy_versions[]             |
| `TrainingRun`          | 実行結果と出力証拠                       | job_id, plan_hash, stage_status, actual_resources, artifacts, verification        |

JSONへ永続化する際は `schema_version` を必須とし、列挙値を不正文字列で補間しない。未知の将来versionは読み飛ばして成功扱いにせず、互換表示/実行停止を分ける。元のUniversal Voice Datasetは観測原本として維持し、派生計画・区間の役割は別recordへ保存する。

### 2.1 目標の例（意味を固定した契約例）

```json
{
  "schema_version": "1",
  "speaker_id": "spk_01",
  "outputs": [
    {
      "target": "talk",
      "texture": {
        "preset_id": "builtin.classic_synthetic",
        "preset_version": "1.0.0",
        "reference_segment_ids": []
      },
      "output_requirement": {
        "kind": "reusable_model",
        "preferred_runtime": "style-bert-vits2",
        "require_preferred_runtime": false
      },
      "training_preference": "auto"
    }
  ]
}
```

- `target` は歌唱/トークの**機能**を表し、素材の分類ではない。両方なら `outputs` を2件に展開する。
- `preferred_runtime` は出力先の希望。**厳密互換が必要なら** `require_preferred_runtime=true` とする。実現不可能な場合、他のruntimeをSBV2/DiffSinger互換と偽らない。
- `training_preference` は通常 `auto`。高度な設定では各Stageでfine-tune/scratchを明示できるが、Goalより上位の設定として扱わない。
- `preset_id` は安定ID。別のpresetに変えても入力音声の元解析は再実行しない。参照区間が別人物ならエラー。

### 2.2 音声の用途と声質参照は別軸

`SegmentRole` は少なくとも以下の型を持つ。

| role                 | 扱い                                                                 |
| -------------------- | -------------------------------------------------------------------- |
| `speech_teacher`     | 会話テキストと対応した正当な発話教師                                 |
| `singing_teacher`    | 歌詞・音素・歌唱特徴と対応した正当な歌唱教師                         |
| `identity_reference` | 本人の音色・話者特徴を抽出するための参照。**教師にならなくてもよい** |
| `texture_reference`  | 指定質感を持つ同一話者区間の参照                                     |
| `feature_only`       | 特徴だけ利用し、教師ラベルとして使用しない                           |
| `review`             | 話者・歌詞・用途等が未確定で学習には保留                             |
| `excluded`           | 誤話者・破損・明示除外等で不適格。除外根拠を保存                     |

複数roleに対応できるが、同じ原音・加工音を独立録音と数えず、`source_id + time_interval + origin_hash` で重複管理する。同一話者に見えるか不明な区間は誤ったidentity_referenceへ強制投入しない。

## 3. 質感プリセットの技術的な意味

プリセットは「性能ランク」ではなく**表現目標の束**。必要ならユーザーが同一話者の参考音声を指定し、目標と本人性の両方を守る。

| ID                   | 目標                       | 歌唱で検討する制御・学習経路                                                             | トークで検討する制御・学習経路                  |
| -------------------- | -------------------------- | ---------------------------------------------------------------------------------------- | ----------------------------------------------- |
| `classic_synthetic`  | 旧来の電子的、規則的な質感 | 正確な音素と音符、制御されたF0細部・時間長、合成音色の調整。**意図しない欠損を作らない** | 明瞭な音素・規則的な韻律/抑揚、対応する音色制御 |
| `clean_controlled`   | 明瞭・安定                 | 発音・F0/Duration精度を優先                                                              | 発音と抑揚の安定性                              |
| `natural`            | 自然な本人らしさ           | 声色保持・自然な歌唱遷移と微細表現                                                       | 声質、間、自然なアクセント/抑揚                 |
| `expressive_complex` | 複数の表現軸を使う豊かな声 | style/tension/breath/voice-color等の実能力がある場合に学習・制御                         | style/prosody/emotion等の条件と多様な参考例     |
| `soft_breathy`       | 柔らかく息のある声         | 参考歌唱とbreathinessの実制御があれば使用                                                | 軟らかな発話参考例と対応スタイル適応            |
| `powerful`           | 張りのある力強い声         | tension/energy/強い発声参考例等                                                          | 抑揚・音色・発声強度の対応制御                  |
| `reference_match`    | 指定した音声に近い         | 同一話者の具体的な歌唱参考区間へ合わせる                                                 | 同一話者の具体的な会話/歌唱参考区間へ合わせる   |

- 実際のOutputBackendが対応するかは**個別にcapabilityで照合**する。OpenUtauの表現曲線の一部はvoicebank自体の対応を要する。
- 旧VOCALOID系の表現は**目標音響傾向の参考**であり、商用エンジンに互換の声庫を生成できるという意味ではない。
- どのプリセットも別人の声に寄せたり、意図しないBGM/破裂/音割れを残したりしてはならない。
- 「プリセットを指定しただけで効果あり」は不合格。計画に目標traitsを満たす実stageか出力制御が含まれ、結果で差を確認する。
- 話者忠実性/音素正確性/音程と、ユーザー希望の電子的・高表現等の質感一致度は別指標。

## 4. Basic Source Profile（v1）の入力と算出

**「会話が多いか」「歌唱があるか」「総量が豊富か」は同じ軸ではない**。1話者に複数の属性が同時に成立する。数値の根拠と測定不能状態を保存する。

### 4.1 1話者×1projectのProfile

```json
{
  "schema_version": "1",
  "speaker_id": "spk_01",
  "dataset_fingerprint": "sha256-example",
  "speech": {"eligible_seconds": 25.0, "independent_utterances": 9, "transcript_usable_count": 4},
  "singing": {"eligible_seconds": 1200.0, "independent_phrases": 75, "pitch_bins_observed": 13},
  "identity_reference": {"eligible_seconds": 750.0, "distinct_sources": 6},
  "texture_evidence": [{"preset_id": "builtin.powerful", "status": "estimated", "reference_ids": ["seg_9"]}],
  "measurement_status": {
    "transcription": "partially_measured",
    "singing_f0": "measured",
    "speaker_identity": "partially_measured"
  },
  "derived_tags": ["singing_dominant", "speech_sparse", "has_identity_reference"]
}
```

この数値はschema説明例であり「25秒なら不足」などの製品閾値ではない。

### 4.2 集計規則

1. `speaker_id` の現在の手動修正・除外・分類を適用したDataset Snapshotを取り出す。
2. `speech_teacher`, `singing_teacher`, `identity_reference`, `texture_reference` ごとに利用適格性を判定する。
3. `raw_seconds`、`eligible_seconds`、`distinct_recordings`、`distinct_utterances/phrases`、`phoneme_coverage`、`pitch_coverage`、`measurement_status` を**別々に集計**する。
4. 同一録音の切り出し・同じ歌の反復・raw/sep派生はorigin groupで重複を制御する。物理秒数の合計をそのまま「独立データ量」としない。
5. 品質に難があっても声質だけ使える区間はidentity_referenceへ保持する。未計測score=0を無条件除外しない（#63と関連）。
6. 資料が不足しているときは `unknown`。測定できない指標を確定0と読み替えない。
7. Profileタグは**導出**するが、閾値はストラテジーに属する `input_requirements` が管理する。例: あるTrainerが必須とする有効発話秒数と音素数は、そのTrainerのversion付き条件として保持する。全Backendに一律「10分以上」を適用しない。
8. `source_fingerprint`、対象話者の手動修正revision、プロフィール計算versionを含めてcache化する。Goalの質感だけを変えた場合はProfileを再利用できる。

重み付き学習は `contracts-and-weighting.md` を正とする。**重み付き秒数は新しい観測データが増えたという意味ではない**。欠損の補間・推定値は実測を水増ししない。

## 5. 学習戦略の構成単位（Stageの型）

すべてのStageは `stage_id`、`operation_id`、`version`、`inputs`、`outputs`、`initialization`、`required_capabilities`、`resource_refs` と依存先IDを宣言する。

| 種類               | 入力例                           | 出力例                   | 設計上の禁止                         |
| ------------------ | -------------------------------- | ------------------------ | ------------------------------------ |
| `prepare_dataset`  | 目的別適格区間/音素/weights      | 学習入力・hash           | 誤話者や別用途の教師を無根拠に投入   |
| `use_base_ability` | 事前学習TTS/SVS + config         | 発話/歌唱能力の基盤      | checkpointを持たずに持つと表示       |
| `adapt_identity`   | 本人の参照音声・同定根拠         | 話者条件・適応重み       | 話者が異なる参考音声の流用           |
| `adapt_texture`    | preset traits + 同一話者の参考例 | style/preset条件         | 未対応traitsを実現済みと表示         |
| `train_talk`       | 発話教師・TTS能力・初期重み      | トークモデルのcheckpoint | 歌唱ラベルを発話教師と偽装           |
| `train_singing`    | 歌唱教師・音素/F0/音符情報       | 歌唱音響/Duration等      | 音響だけで完全声庫と表示             |
| `compose_model`    | 互換な学習成果物と声質制御       | 再利用可能なModelPackage | 異種checkpointを無検証で混合・変換   |
| `verify_model`     | 本物のモデル/target runtime      | 推論WAV・検証レポート    | ダミーWAV/出力ファイル存在だけで合格 |

**Stageは依存関係を持つDAG**。別プロセスProviderで実行する場合も、未知operation・依存循環・入出力型不一致・必要資源欠損があれば計画段階で不成立とする。音声サンプルのみを作る `audio_preview` と、実際の `model_artifact` を出すStageは別物。

### 5.1 Output Contractが成立する条件

- `talk`: 対応推論runtimeで**新しい任意の日本語文章**を入力でき、モデル/設定・必須資源を再利用可能な形で保存できる。
- `singing`: 対応runtimeで**新しい歌詞と音符**を入力でき、音響・Duration・Vocoder等、そのruntimeの必須成果物が揃う。
- `both`: 独立したTalk/Singing計画と成果物を持ち、共通話者参照を共有する。片方が失敗しても他方の成果物は破壊しない。
- 出力形式は `IModelExporter` の能力と照合する。別TTSモデルが推論できても、その重みをStyle-Bert-VITS2へ直接変換できるとは限らない。同様にVCがWAVを生成できてもDiffSinger声庫が完成したとは言えない。
- ユーザーが特定runtimeを厳密要求した場合、非互換な別モデルへ無断切替しない。

## 6. TalkとSingingを分ける計画ルール

### 6.1 TalkTrainingStrategyPlanner

ゴールは「文章から狙った声で自然／意図した質感を持つ発話を生成すること」。

- **発話が豊富**: 正しい発話教師を使うTTS適応、発音/韻律の追加学習、声質やstyleの保持を候補化。
- **歌唱のみ**: 本人のidentity/texture referenceは歌唱から作る。文章から発話する能力は対応する事前学習TTS等から得て、**その能力に話者声質を結びつける実装済みStage**があることを必須とする。
- **発話がごく少ない**: 少数の発話を発音/韻律の検証に使い、歌唱からの声質参照と矛盾しないか比較する。
- **クラシック電子的**: 対応するstyle/prosody/phoneme-timingの制御Stageがある場合に候補化し、モデルが単に粗いだけの劣化を狙わない。
- **高表現**: スタイル条件付き学習と参考音声があり、実Backendが複数表現軸を維持できる候補を優先。素材に無い表情を観測済みと偽らない。

### 6.2 SingingTrainingStrategyPlanner

ゴールは「歌詞＋音符から狙った声質で音程・長さを守って歌えること」。

- **歌唱が豊富**: 正しい音素境界/F0・歌詞を持つ教師を中心に、音響・Duration・必要な表現Stageを組み立てる。
- **発話のみ**: 声質のidentity referenceは発話から得る。音符/歌唱能力は対応する事前学習SVSなどから供給し、**再利用可能な歌唱モデルへ適応・出力できるStage**があることが必須。
- **歌唱少量**: 残っている本物の歌唱で音域・表現を検証し、歌唱能力の不足を補助Stageで補う。希少な音素/音域は使用適格なら正の重みで残す。
- **クラシック電子的**: 正確な音符・音素長の制御と合成時の電子的な質感設定を検討。
- **高表現**: F0微細変動、ブレス、tension、声色モード等、対応voicebankが実装した軸を候補化。**存在しないpitch/expressionモデルを必須モデルとして捏造しない**。

## 7. 計画解決アルゴリズム（固定の決定順）

```text
Resolve(goal, inventory, registered_strategies, backend_capabilities, resources):
  1. validate speaker, requested target, texture preset, reference provenance
  2. freeze goal and inventory hashes
  3. enumerate matching strategy descriptors (built-in and enabled Mods)
  4. for each strategy:
       check schema/protocol/target/preset compatibility
       check required INPUT ROLES separately (teacher vs identity vs texture)
       check required capabilities, backend, model resources and output_kind
       build a typed stage DAG; reject cycles, missing producers, invalid hashes
       preflight hardware and license-distribution requirements
       record feasibility + reasons + evidence level
  5. select among feasible candidates with deterministic policy (§7.1)
  6. lock plan, stage init modes, resource hashes, data roles and evaluation set
  7. show the selected plan, alternatives, unsupported conditions
  8. run only after user starts training; never silently alter the target/texture
```

### 7.1 候補比較の優先順位（v1の非学習型ルール）

**必要条件を満たさない候補は採点しない**。以下を順に比較する辞書式順位を使用する。単一の適当な合成点で、致命的な不一致を隠さない。

1. ユーザー要求targetと `reusable_model` 出力が成立する。厳密runtime指定は必須条件。
2. presetに定義された**必須traits/制御能力**を満たす。任意traitsのみ不足なら `partial_support` を記録する。
3. 対応する事前学習基盤や教師・声質参照の必要条件を満たし、異なる話者・無校正値に依存していない。
4. 同じ条件で測定した**実生成モデルの検証結果**があれば高い方を優先。測定なしは `unverified` とし、実証済みの性能と同格に扱わない。
5. 有効データの多様性・目的別カバレッジ・本人声質の保持を比べる。低品質でも貴重な区間を無根拠に捨てない。
6. プロファイル（高品質/標準/軽量）で許容されるVRAM・時間・ストレージを超えない範囲で、計算負荷が小さい方を優先。
7. それでも同順位なら `strategy_id + version + provider_id` の安定した文字列順。配列列挙順やインストール順を選定根拠にしない。

**スコアを調整するためにテスト素材の正解をPlannerへ漏らしてはならない**。閾値と評価指標はversion付きpolicyで管理し、変更は再学習計画のhashを変える。モデル品質のエビデンスが弱い場合は最初の候補に「推奨（未検証）」と明示する。選定理由と棄却候補の理由コードを保存する。

### 7.2 見積りと例外

- 必要な音声の情報が `unknown` ならゼロとみなして拒否しない。再計測可能なら追加解析を提案し、未確定の条件は確定実行不可/要確認に分ける。
- `from_scratch` 強制指定は「モデルを構成するtrainableな対象Stage」の初期化方針である。言語辞書や固定Vocoderを利用したら全モデルscratchと誤記しない。
- 実装済みの声質適応stageがない場合、歌唱→トークのPlan候補は `unsupported_capability`。歌唱modelを作って成功にしない。
- GPU不足・配布モデルのライセンス未確認・Mod未有効化・plugin API不一致・出力runtime非互換は異なるreason_codeにする。
- 同じ目標を変更せずに実行可能な別戦略を提案できる。自動再実行は過去のrun manifestを再利用せず新しいPlan/Jobにする。

## 8. 計画の保存、実行の状態と再現性

```text
VoiceProject/
├─ metadata/voice-goals/<speaker-id>.json        # ユーザーの指定
├─ metadata/speaker-data-inventory/<speaker-id>.json
├─ training/plans/<plan-id>/plan.json            # 不変の学習工程と理由
├─ training/plans/<plan-id>/inputs.json          # segment roles / source hashes
├─ training/jobs/<job-id>/stage-runs/<stage-id>/
│    ├─ request.json
│    ├─ run-manifest.json
│    └─ log.txt
└─ training/verification/<job-id>/verification.json
```

この配置は**目標schema**で、既存 `metadata/training-jobs/<batch-id>.json` と `training/<backend>/<job-id>/` の実パスを壊すために一括移行するものではない。既存ProjectWorkspaceの安全な相対パス解決と保存APIに統合し、段階的に追加する。

計画は少なくとも以下でfingerprintを生成する。

```text
SHA256(canonical_json(
  goal + texture preset id/version + reference hashes
  + speaker inventory hash + manual corrections snapshot
  + selected strategy id/version/provider hash
  + typed stage DAG + operation versions + stage parameters
  + backend model/checkpoint/resource hashes
  + input role/segment ids + weight policy + data split seed
  + output runtime requirements + planner policy version
))
```

保存順序とJSONのkey順を固定し、非有限数/同一ID重複を拒否する。旧Job履歴は閲覧可能にするが、必要契約が欠けるときに新方式のJobとして無断実行しない。実行では `planned → preparing → running → trained → exported → verified` と、各段階の `failed/canceled/unavailable/not_run` を区別する。学習器成功とモデル推論成功と外部互換検証成功を別の状態として持つ。

## 9. UIでのユーザー操作

```text
[素材を読み込む]
  ↓（会話/歌唱の手動仕分け不要）
[話者を選ぶ]
  ↓
[作るものを選ぶ]  トーク / 歌唱 / 両方
  ↓
[声の質感を選ぶ]  電子的 / 自然 / 高表現 / 柔らかい ...
  ↓（任意）同じ話者の試聴区間を参考として指定
[素材の診断]
  会話: 有効〇分、歌唱: 有効〇分、声質参照: 有効〇分
  独立発話・音素/音域・測定不能・低品質で採用した区間
  ↓
[推奨学習計画]
  使う技術と順序 / 何をどの素材から学ぶか / 見込みのない点
  Mod/組み込みの提供元、GPU目安、対応出力、代替候補
  ↓
[学習する] → [生成音声を試聴] → [互換形式へ出力]
```

通常UIでは「Fine-tuneかScratchか」を最初に尋ねない。高度な設定で手動希望を残す。Modは提供元と状態（利用可能／資源不足／未対応／未検証）を表示し、外部実行コードがある場合は明示有効化を求める。コード入りModを単なるJSONの読み込みと同じ安全性として扱わない。

**学習前にできるのは能力と素材の診断**であり、実出力音質の保証ではない。正解付き評価を通した推奨であっても、本人らしいか、選択した質感になるかを試聴可能にする。

## 10. 現行コードとの接続点と移行

| 現行mainのクラス/処理                           | v1で追加する責務                                                          | 保持する互換性                         |
| ----------------------------------------------- | ------------------------------------------------------------------------- | -------------------------------------- |
| `SpeakerTrainingSelection`, `TrainingJobRecord` | 目的/texture/参照・plan ID/hash、段階的学習の結果                         | 旧Job履歴の閲覧・再作成                |
| `TrainingJobPlanningService`                    | Goal→Inventory→Talk/Singing Plan→不変Job                                  | 話者ごとの複数Job、補完ON/OFF          |
| `TrainingDatasetSnapshotService`                | 最新手動修正・区間role・weightとfingerprint                               | 原UVDと重い解析キャッシュを破壊しない  |
| `UsageDatasetRouter`                            | 素材分類とユーザーの生成目的を区別                                        | ambiguous保留、手動修正の優先          |
| `DiffSingerDatasetBuilder`                      | `singing_teacher` と別のidentity/texture reference、Stageごとの教師適格性 | OpenUtau声庫形式、MFA時刻検証          |
| `StyleBertVits2DatasetBuilder`                  | `speech_teacher` と別のidentity/texture reference                         | 公式 `esd.list` の4列互換              |
| `MlWorkerClient`                                | stage/provider/operationのversion付きIPCと結果hash照合                    | メインアプリは1つのexe入口             |
| `IModelExporter`/`ExporterRegistry`             | Stage出力のModelPackageを対応runtimeへexport                              | 出力形式登録であり学習戦略登録ではない |

新設概念はProcess/Processingの `training/` にPlanner/Registry/Resolver、Data/modelsとrepositoriesにrecord、Python workerに固定Backend Adapterを置く。新しい第4層を作らない。**同梱する外部学習モデルやcheckpointはコードのApache-2.0とは別にライセンス・再配布条件を検査**する。生成モデルの扱いは `docs/license-policy.md` を正とする。

## 11. テストシナリオと合否

| 入力/目標                                | 正常な計画決定                                                           | 禁止される誤動作                                      |
| ---------------------------------------- | ------------------------------------------------------------------------ | ----------------------------------------------------- |
| 歌唱豊富・発話0、目標Talk+クラシック電子 | 歌唱を声質参照として利用できるTTS/出力経路を探索。なければ能力不足を明示 | 自動で歌唱モデルに変更、歌唱WAVだけをトークモデル扱い |
| 発話豊富・歌唱0、目標Singing+高表現      | 汎用歌唱能力+発話由来声質適応を探索し、歌詞+音符で検証                   | 本人の未知の歌唱技法・音域を実測と表示                |
| 両方豊富、目標TalkとSingingで異なる質感  | 2プランを独立構成。声質証拠を共有                                        | 同一のtexture/Trainerを強制                           |
| 少量/重複多、低品質だが希少音素あり      | 教師適格性・有効量を分けて計画、価値ある区間はweightで残す               | 再生時間のみで「大量」と判定しゼロweight              |
| 同じ素材で自然→クラシックへ変更          | Profileキャッシュを再利用し、新Goal/Plan hashを作成                      | 前処理再実行、Jobを上書き                             |
| Mod presetは登録済み、実stageなし        | 選択可能状態と実現能力未対応を区別                                       | 名前だけ変わった成果物を「プリセット成功」と表示      |
| 追加戦略Provider不在/資源hash不一致      | 旧Job閲覧可、実行は停止し理由表示                                        | 代替Providerへ無断移行                                |
| Talk出力の実際のSBV2互換性なし           | 対応別runtimeがあれば説明して選択可能。厳密SBV2指定なら不成立            | 別TTSモデルをSBV2互換としてexport                     |

**自動テスト:** schema/バージョン/legacyの読込、role分離、Profileの重複除外、strategyの適合・安定順位・DAG検証、同一入力の再現性、manual overrideの反映、unsupportedの伝播、Modの異常、キャンセル・部分成果物、出力runtime互換。

**実機受け入れ:** ローカルGPUで同一話者/複数話者、発話主体・歌唱主体・混在、クラシック・自然・高表現を少なくとも確認可能なsubsetで実行し、**実生成された音声とプリセット条件**を評価。未対応プリセットをv1完成として数えない。非公開素材をGitHub公開artifactへ載せない。

## 12. 要検証事項を「未確定」として残す

- 固定SBV2/DiffSinger版で実行できるtexture制御軸と学習stageの範囲（実コード・実モデルでpreflight必要）
- 歌唱のみから再利用可能なTalkモデル、発話のみから再利用可能なSingingモデルを実際に出力できるProvider（Zero-shot再生だけでは不十分）
- 音色のクラシック化や高表現化に対する人手試聴と評価基準、同一話者らしさとのトレードオフ
- Strategyごとの教師量/声質参照量/VRAMの最小要件と、少量/大量タグの校正（現段階で普遍的な秒数は決めない）
- 独自Providerが必要とするOS制限と配布資源。外部プロセスは自動的にサンドボックス化されない

これらは**機能を将来送りにするための曖昧さ**ではなく、実装時のpreflight・成果物検証で偽成功を防ぐための明示的な契約とする。仕様が満たせないときは理由付き `unsupported` として利用者へ表示する。

## 参考資料（方式の機能を示すもので性能保証ではない）

- [OpenUtau DiffSingerの表現項目](https://www.openutau.com/DiffSinger-support/) — pitch/dynamics等。BREC/TENC/voice color等はvoicebank側の対応が必要。
- [OpenVPI DiffSinger](https://github.com/openvpi/DiffSinger) — 歌唱モデルの構造。v1は固定revisionを `docs/implementation-v1-10.md` に記載。
- [Style-Bert-VITS2](https://github.com/litagin02/Style-Bert-VITS2) — style vectorを持つ発話合成。v1固定revisionは `docs/implementation-v1-11.md`。
- [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) — 参照音声による声質クローンやVoiceDesignの例。**推論可能であることとStyle-Bert-VITS2互換の学習重みを生成できることは別**。
