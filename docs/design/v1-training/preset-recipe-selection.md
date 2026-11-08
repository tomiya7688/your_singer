# 質感プリセットを学習レシピへ変換する設計（v1必須）

## 文書の役割

[学習システム基本・詳細設計](learning-system-design.md) の「生成目的 → 質感 → 素材の方向性 → 学習戦略」を、**実際に異なる学習・推論工程を組み立てる契約**にする。

**プリセット機能は製品必須**。特定のBackendに直接「プリセット」という引数がないことは、機能を削除する理由にならない。Backend内の学習方法・モデル構成・追加の表現モデル・合成時制御を組み合わせ、必要なら別Backendと再利用可能なモデル生成stageを選択する。

ただし、実装できていない機能を「対応済み」とは表示しない。**実装要件が未達なら完成判定を保留する**。実行可能性がない場合に別の質感を勝手に選ぶことも禁止。

## 1. 二つの独立な選択

- ユーザーは**出力目的**（talk/singing/both）と**目標質感プリセット**（natural/classic_synthetic/expressive_complex/soft_breathy/powerful等）を選ぶ。
- Your Singerは解析済みの **発話教師量・歌唱教師量・声質参照量・独立フレーズ・音素・音域・品質・測定状態**から実効的な入力を求める。
- TalkTrainingStrategyPlanner と SingingTrainingStrategyPlanner は別々の戦略を列挙する。素材が歌唱主体でもトークを選べる。
- プリセットは**単一のモデル名・ハイパーパラメータ・自然度評価ではない**。目標と実際の学習・合成能力を結ぶ契約である。

## 2. 質感実現の二段階

### 第1段階 — 学習レシピ自体を変える（必須）

プリセットで変化させられる学習stageを固定する。

1. **input_select**: 同一話者の教師データ／声質参照／表現参考区間と、用途別の採否・重み・重複制御。少量で希少な音素を品質だけで捨てない。
2. **base_select**: 歌唱能力を持つ基盤モデルや、トーク用TTSを選択する。互換形式でない重みを勝手に混ぜない。
3. **feature_extract**: 実測F0、音素/時間長、息/有声/張り等の利用可能な特徴を得る。未計測と実測0を分ける。
4. **acoustic_train**: 使う教師区間、初期checkpoint、埋め込み条件、モデル構造、凍結/学習範囲、サンプル重みを変える。
5. **auxiliary_train**: 用途に応じてDuration、Pitch、Variance等を別stageで訓練する。各stageの入力条件と初期化を独立記録。
6. **texture_adapt**: モデルが実際に対応する場合のみstyle/voice color/conditionを学習する。非対応の共有embeddingを読み込んだことにしない。
7. **compose_export**: 必須checkpoint・Vocoder・音素辞書と設定を揃え、対応runtimeが読めるモデルPackageを構築する。
8. **verify_texture**: 同じ未学習歌詞/文章・音符で目標質感の適合と、音声品質・話者らしさを確認する。

**要求:** preset Aとpreset Bで選ばれる学習方法が異なる場合、`recipe_id/version`、`stages`、`selected_segment_roles`、`config_sha256`、`model_artifact_sha256`等を比較できる。少なくとも1つの学習stage/入力配分/モデル構成が実際に異なり、名前の書換えだけで「学習プリセット適用」としない。

### 第2段階 — 同一Backend内の表現幅を活かす（必須）

DiffSingerなどのモデルが提供する **条件付き入力・補助予測・音高/Duration・Voice Color等**を使って、同じモデル系統でも質感を変えられるようにする。

- **学習時の違い:** Acoustic/Duration/Pitch/Varianceの機能有効化、条件付き学習、表現区間のbalancing、拡張の安全な有効化、学習済み重みの適応/新規学習。
- **推論時の違い:** 本当に対応した制御curve（PITD/DYN、BREC/VOIC/TENC等）・style/mode・推論設定を使う。モデルにない入力を設定だけで生やさない。
- **成果物上の違い:** 単一の表現可能な声庫に複数の制御特性を保持するか、必要なら**異なる学習レシピの複数checkpoint/声庫**をそれぞれ生成・選択する。

「1個のDiffSinger acoustic checkpointだけで、あらゆる異なる声質を自由に生成できる」という前提は置かない。**条件付きモデルで表現可能な差**と、**別の学習済みモデルにする必要のある差**を区別する。

## 3. Presetのレシピ分類

| preset | 学習計画の主な方向性 | DiffSinger内の実装方向性 | 完了の確認 |
|---|---|---|---|
| `classic_synthetic` | 発音/F0/時間長の正確さ・安定性・音色の規則性を優先。必要なら専用Acoustic variant | 無根拠に音声を劣化させない。正確なDurationと制御可能なF0。過度な表現条件付けを要求しない構成を比較 | 電子的質感があるが音割れ/発音欠損ではない |
| `clean_controlled` | 正解に近い音素・音符/Duration/低ノイズの教師を重視 | 音響/Durationと必要なPitchの正確さ、安定した制御可能性 | 歌詞・音程・明瞭度の保持 |
| `natural` | 本人らしい声質、自然な遷移・F0/強弱・表現を学習 | Acousticと対応するPitch/必要なVariance。声質破壊のないVocoder | 話者類似度・自然な聴感 |
| `expressive_complex` | 多様な発声・音色・息・張りを教師として保持し、条件付き学習stageを追加 | Acousticの表現埋め込み、Pitch/Variance（Breathiness/Voicing/Tension）、必要なら別Vocal Mode | 同じ歌詞・音符から複数の実際に異なる表現が可能 |
| `soft_breathy` | 同一話者の柔らかい/息成分を持つ区間を優先し、品質の不明な息成分を捏造しない | BreathinessとVoicing等の実測特徴、対応Acoustic/Variance予測と制御 | 息表現が変化しても音素・声質が破綻しない |
| `powerful` | 同一話者の張り/芯のある発声を優先。強い声だけで声帯を傷める表現を真似する目標ではない | Tension/VoicingとAcoustic条件付け。音量・張りの結び付きによる不自然さを検証 | 張りは増えるが歪み/過度な鼻声にならない |
| `characterized` | 同一話者の話法・歌い方の個性を持つ素材を選択 | voice color/multi-modeは**教師がモード区別できる場合のみ**。不足なら専用variant | 別人物の声に変わらない |
| `reference_match` | 指定同一話者のreferenceと音色差が近い区間を重視 | 対応するStyle/modeを活かす。困難なら別checkpointの評価・選定 | 参考に近づき、本人らしさを保持 |

この表は**実装で選ぶアルゴリズムの候補空間**。推論時の単一パラメータがそのままユーザーのtrait 0〜1と同値だと扱わない。全てのプリセットの実現を実データ・固定Backend・OpenUtau実機で検証する。

## 4. レシピの選択アルゴリズム

```text
target + texture preset + target runtime requirements
        ↓
SpeakerDataInventory (talk/singing teacher, identity, style, quality)
        ↓
strategy discovery: builtin + enabled Mods
        ↓
capability filter:
  reusable model output? + preset controls? + required submodels?
  resources/license? + same-speaker refs? + GPU/preflight?
        ↓
recipe candidates per strategy:
  training inputs + augmentation + conditioning + trainer stages
  + inference defaults + exporter package + verification
        ↓
deterministic fit ranking with evidence + resource cost
        ↓
immutable resolved recipe, training plan and input fingerprint
        ↓
training → export → true inference → objective + listening evaluation
```

- 目標の必須traits・再利用可能モデル出力がない候補は**不可**。単純な音声ファイル/Voice Conversion試聴のみは成功ではない。
- 少量の歌唱では、実際に互換な事前学習歌唱能力を使える場合、専用の初期重みから適応するレシピを優先候補にする。大量だから必ずScratchとはしない。
- 質感の一部しか対応できない場合、`partial` と不足軸を記録し、**必須の目標が未達なら完成扱いしない**。ただし既定の選択肢は削除せず、選択したユーザーに不足の理由を示す。
- 速度・VRAM・学習時間は出力目的/質感と独立のProfile。Lightweightは内容が少ないことではなく計算資源条件。
- レシピが変わったら `goal/texture/strategy`、実行stage、重み・学習資源・cfgが変わり `training_plan_hash` が変わる。原音の解析は不要なら再実行しない。

## 5. 新しい契約：PresetRecipePlan

以下は**実装するschemaの構造例**であり、現在の `TrainingStrategyDescriptor` はstage optionsをまだ保持しない。機械検証用の [PresetRecipePlan JSON Schema](schema/preset-recipe-plan.schema.json) と [具体例](examples/diffsinger-preset-recipe.json) を追加した。SHA-256等の例示値は実ファイルの内容ではなく、**実学習の成功を意味しない**。

```json
{
  "schema_version": "1",
  "job_id": "job-singing-01",
  "target": "singing",
  "preset": {"id": "builtin.expressive_complex", "version": "1.0.0"},
  "strategy": {"id": "builtin.diffsinger.expressive", "version": "1.0.0"},
  "recipe": {"id": "diffsinger-v2.5.1-expressive", "version": "1"},
  "dataset": {"inventory_hash": "sha256-...", "assessment_hash": "sha256-..."},
  "stages": [
    {"stage_id": "acoustic", "operation_id": "builtin.diffsinger.acoustic",
     "options_ref": "recipes/acoustic.json", "options_sha256": "...",
     "initialization": "fine_tune"},
    {"stage_id": "duration", "operation_id": "builtin.diffsinger.duration",
     "options_ref": "recipes/duration.json", "options_sha256": "...",
     "initialization": "fine_tune"},
    {"stage_id": "pitch_variance", "operation_id": "builtin.diffsinger.variance",
     "options_ref": "recipes/variance.json", "options_sha256": "...",
     "initialization": "from_scratch"},
    {"stage_id": "export", "operation_id": "builtin.diffsinger.export",
     "options_ref": "recipes/export.json", "options_sha256": "...",
     "initialization": "not_applicable"}
  ],
  "render_profile": {"path": "recipes/render.json", "sha256": "..."},
  "required_output": {"format": "openutau_diffsinger", "version": "0.1.565"},
  "plan_hash": "sha256-..."
}
```

`options_ref` は**実行時の固定入力**。C# `ResolvedStage` に大きなPython固有設定を直書きせず、version付き`RecipeOptions`を型検証し、ML workerへ読み取り専用path/hashで渡す。旧Jobは同じバイナリで無言に新Recipeへ移行させない。

- Modの `TrainingStrategyDescriptor v1` はrole/stage DAGしか持たない。プリセット別Recipeを実用化する際、**本体組込RecipeResolverでpreset + stage operationから実設定へ展開**する。Modが独自設定を宣言する場合はschema versionを上げ、操作ごとの許可されたoptionsだけ受け入れる。既存v1の宣言を破壊しない。
- `RecipeOptions` は `operation_id` ごとに型を固定。任意YAMLをModel/Workerへ通す一般UI・任意コード評価は禁止。
- config生成→公式binarization→学習→推論で有効にした能力が同じことをハッシュ・実資源で確認する。
- 素材の質感とモデル訓練時のsampling/augmentationの設定をjobごとに凍結する。

## 6. 自動評価と完了ゲート

**必須:** 同じ本人・同じ未学習歌詞/音符で`classic_synthetic`と`expressive_complex`など複数プリセットを比較する。

- `phoneme_accuracy`、`pitch_accuracy`、`duration_accuracy`、`speaker_similarity`、`distortion`は目的に関係なく最低条件として評価する。
- `texture_match` はプリセットの目的に沿った差分として別評価する。電子的表現を自然さが低いだけで不合格としない。
- 同じファイルをPreset名だけ付け替えた場合、`recipe_diff_missing` として失敗。
- 実学習の方法が異なっても、生成音声が差を示さない/低品質になる場合は`texture_unverified` / `quality_failed`。音質改善を捏造しない。
- checkpointやVocoderの再配布・利用条件を含めて検証。第三者ライセンス未確認のモデルを暗黙で採用しない。
- これらの検証は**正式な製品完成条件**。サンプルwavだけの合成やfixtureでのconfig差分では達成しない。

## 7. 実装順と既存コードへの接続

1. 既存 `TexturePresetRegistry`（目標）と `TrainingStrategyRegistry`（学習stage）を維持。
2. `SingingTrainingStrategyPlanner` が**各presetに対応する実Recipe候補**を出す。現在の `BuiltInSingingStrategies.AvailableCapabilities` は音響入口のみで、`expression_conditioning` はまだ未提供。表示だけで対応済みとしない。
3. `TrainingModeResolver` は各Stageの初期化（fine-tune/scratch）を確定するが、最上位のRecipeと目標を上書きしない。
4. `DiffSingerDatasetBuilder` / `DiffSingerBinarizeService` は入力/設定をRecipeごとに生成し、重みmanifestと1対1のitem lineageを保証。
5. `DiffSingerTrainerService` はAcoustic/Duration/Pitch/Variance等のStageを必要に応じて実行。音響だけ完了したときにモデル全体を`verified`としない。
6. `DiffSingerExporter` と `DiffSingerExportValidator` はRecipeに対応するモデル/制御・必要ファイルを確認して出力。
7. `SingingInferenceVerifier` が実際に歌わせ、`CheckpointEvaluationService` がプリセットへの適合度と品質の両方を比較する。

詳細なDiffSinger固定版マッピングは [DiffSingerプリセット実現の詳細設計](diffsinger-preset-realization.md) を参照。
