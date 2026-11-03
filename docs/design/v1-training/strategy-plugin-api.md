# 学習戦略・質感プリセットの第三者登録（Mod / Plugin API v1）

## 目的

**第三者開発者とModユーザーが、Your Singer本体を再ビルドせず、新しい質感プリセットと学習方法を登録できる**こと。

- プリセット（ユーザーが目指す音声表現）と学習戦略（その表現を作る処理・条件）を分離する。
- `ExporterRegistry` は**出力ファイル形式**の拡張点であり、**学習戦略の登録機構ではない**。`TrainingStrategyRegistry` は独立に設けるが、既存UPD Process Layer配下で管理し第4レイヤーは作らない。
- 新戦略を登録してもUniversal Voice Dataset、話者認識、既存DiffSinger/SBV2の形式を作り直す必要はない。
- 一般ユーザーは通常通り生成目的→質感プリセットを選ぶだけ。開発者はManifestを追加する。

## 1. 登録方法を2段階にする

### Level A: Modユーザー向け、宣言のみ（コード不要）

- `preset.json` または `strategy.json` を `mods/strategies/<publisher>/<strategy-id>/` に置く。
- 宣言JSONでは **既に登録された安全な処理stage**（音声特徴共有、重み付きデータ選択、対応Trainer、checkpoint評価、合成時パラメータ）を設定値として組み合わせる。
- 新しいPythonやC#を書く必要なし。未知stageを動的import/evalしない。
- 好みの質感を簡単に追加したいだけなら、`preset.json` のみで可。見た目だけ変更して実際の音声に何も反映されないなら `unavailable` と表示する。

### Level B: 開発者向け、独自処理Provider

- manifest＋処理用worker executable / ランタイム資源を同梱する。`stdio + JSON Lines` の既存worker契約を拡張する。
- Providerは `probe`, `prepare`, `train`, `verify` 等のうち対応する機能を宣言する。全機能必須にせず、**提供するstageだけを実装可能**にする。
- C# UIが直接Python moduleをloadせず、Process層がPlugin Adapter経由で起動・IPC交換する。
- Pythonが必要なModはPythonランタイムを**Modに同梱**するか、Your Singerの互換した同梱worker APIを使う。一般ユーザーにpip/.NETを個別インストールさせない。
- プラグインのmodel weights、依存物、checkpointの再配布可否と、runtime/resource hashを宣言する。単にコードのApache-2.0適用を仮定しない。

## 2. Strategy Manifest（例）

```json
{
  "schema_version": "1",
  "strategy_id": "example.retro-voice",
  "strategy_version": "1.0.0",
  "provider": "example-mod",
  "display_name_ja": "クラシック電子的声質の追加学習",
  "description_ja": "規則的な歌唱/発話の表現を目標にする",
  "supported_targets": ["talk", "singing"],
  "supported_texture_presets": ["classic_synthetic"],
  "input_requirements": {
    "speech_teacher": "optional",
    "singing_teacher": "optional",
    "timbre_reference": "required"
  },
  "capabilities": ["timbre_adaptation", "controlled_pitch", "synthesize_verification"],
  "required_backend_capabilities": ["timbre_adaptation"],
  "cost_profile": {"gpu_required": false, "estimated_vram_mb": null},
  "stages": [
    {"stage_id": "timbre", "operation": "builtin.speaker_timbre_adaptation"},
    {"stage_id": "render", "operation": "builtin.classic_style_rendering"},
    {"stage_id": "verify", "operation": "builtin.synthesize_and_verify"}
  ],
  "outputs": ["model_artifact"],
  "resource_manifest": "resources.json",
  "license_file": "LICENSE"
}
```

**これは仕様例**であり、`builtin.classic_style_rendering` を現行実装がすでに提供するという意味ではない。Registryは知らないoperationを見つけたらインストールされたように装わず `unsupported_operation` を返す。

- `strategy_id` / `version`: 安定ID＋SemVer。複数publisher衝突時は登録失敗し、組み込み戦略は上書きされない。
- `supported_targets` と `supported_texture_presets`: 適合可能な目的・プリセット条件。プリセットに結びつかない汎用戦略も登録できる。
- `input_requirements`: 単純な秒数だけでなく教師・声質参照・音素/音域・品質などの条件を**別のサポート済みpredicate**で表現する。任意の式やJavaScript評価を許可しない。
- `cost_profile`: 実際に測っていないVRAM/時間はnull、未計測として扱う。
- `stages`: 登録済みoperationの有向非巡回グラフとして解釈し、依存・入出力型・targetを検証する。対象話者を知らないoperationへ勝手に本人声質を渡さない。
- `outputs`: 中間speaker features、学習済みモデル、参考音声、推論sample等の成果物種類。**モデル成果物を保証しない補助処理を「モデル生成strategy」と表示しない**。

## 3. 独自処理Provider契約（概念）

```json
{
  "schema_version": "1",
  "provider_id": "example-mod",
  "provider_version": "1.0.0",
  "protocol_version": "1",
  "runtime": {"type": "external_process", "entry": "workers/example-ml.exe"},
  "supported_operations": ["train", "probe"],
  "resources": [{"path": "workers/example-ml.exe", "sha256": "..."}],
  "permissions_requested": ["read_project_artifacts", "write_job_output"],
  "training_strategies": ["example.retro-voice"]
}
```

APIの基本:

- `probe(context)`: 目標target・質感・素材プロフィール・Backend能力から対応可否と理由を返す。ここでは学習しない。
- `plan(context)`: 具体的なstage、利用区間役割、必要モデル、設定・出力契約を返す。ユーザー指定目標は変更できない。
- `execute(request)`: 許可されたjobの入力artifactに対して処理し、log/progress/resultを返す。
- `verify(request)`: 実成果物を読み込み、対応する未学習文章/歌詞+音符で品質/互換状態を返す。

重い音声ファイルやcheckpointをIPCへ直送しない。manifest・hash・Workspace内pathを交換する。子workerの失敗、タイムアウト、取消、GPU不足は既存ジョブを破壊せずに原因付きで戻す。

## 4. ユーザー操作・簡易導入

- Modユーザー: フォルダへ `preset.json` または `strategy.json` を追加→アプリで「拡張機能を再読み込み」→使えるプリセットが表示される。
- 第三者開発者: zipを解凍して `mods/strategies/` に配置する形式を基準とし、manifest・Worker・モデル資源をパッケージへ含める。
- UI: 組み込み／拡張機能、対応目的、サポートする質感、データ条件、GPU要件、状態（利用可能／要資源／未対応／無効）と無効化操作を表示する。
- **任意コードを含む拡張を「ただのJSON」と同じ信頼レベルで自動起動しない。** コードを含むModはユーザーが明示的に有効化する。信頼できないModには危険性があることを説明し、出所・hash・要求権限を提示する。

## 5. セキュリティと信頼性

- manifest schema/版と全Pathを検証し、作業領域外への相対path脱出を拒否。
- strategyの評価段階は**純粋な宣言/metadata**で実施し、初見のModのcodeを勝手に起動しない。
- executable workerはメインアプリと別プロセスとし、既定ではProjectの読み込み/Job出力以外のアクセスを要求しない。ただし**別プロセス化だけでOS上の厳密なサンドボックスになるとは主張しない**。実際にOSレベルでアクセス制限できる場合のみsandbox保証を表示する。
- manifestの `permissions_requested` はユーザーの判断材料であり、実装済みの強制能力と区別する。未対応の権限制限が必要なら実行を拒否するか、明示的に信頼に基づく有効化が必要。
- 署名/hash・配布元・実行ファイルの真正性を検証できる範囲を記録。hash一致のみで安全と断定しない。
- 既定の組み込みstrategy、モデル資源、UVD、元音声をModが上書きしない。出力は独立job領域。
- Registryの読み込み失敗1件が全学習を止めない。無効化、エラー、日本語の対処方法を提示。
- 相互依存・循環依存・同一ID/version衝突・無効schema・未対応target/preset・資源欠損・worker crash/timeoutを回帰テスト。

## 6. Strategy Registryの責務と選択

```text
VoiceModelGoal (target + preset)
  + SpeakerDataInventory / Source Profile
  + BackendCapabilityRegistry
            ↓
 TrainingStrategyRegistry
  ├─ built-in strategy descriptors
  ├─ declarative Mod JSON
  └─ optional external worker providers
            ↓
 TalkTrainingStrategyPlanner / SingingTrainingStrategyPlanner
            ↓
        TrainingPlanResolver
```

registryは `supported targets`, `preset traits/tags`, `requirements`, `available backends`, `required resources` を公開する。Plannerは**同じ話者・質感・素材プロフィールに対して候補を列挙し、理由付きで選択**。選択の際は順序依存しないstable sortとversion付きルールを使い、同点時はユーザーの自動設定または明示選択を尊重する。

追加・削除・disable・version変更で過去のジョブを無言で別手法に切り替えない。Jobには `strategy_id`、`strategy_version`、`provider_id`、`provider_hash`、`preset_id/version`、`input_fingerprint` を固定する。再現不可時は「プラグインが存在しないため再作成/実行不可」と説明する。

## 7. v1初期と将来

**v1必須**: JSONでの質感プリセット登録、既存内蔵operationから学習戦略を組み立てる宣言形式、Registryの競合/互換検査、Plannerへの候補公開、実音声による反映検証。

**第三者独自Worker実行**: セキュリティ・パッケージ・モデルライセンス・IPC互換の受け入れが必要。小Issueに分けて実装する。Provider機能がまだ未実装なら「登録可能」と表示しない。

**v2以降**: 依存解決自動インストール、異種OSへの汎用配布、オンラインMarketplace、自動戦略ランキング。学習方法を簡単に拡張できる骨格を将来送りにしない。
