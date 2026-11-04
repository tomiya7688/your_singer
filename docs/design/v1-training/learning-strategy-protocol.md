# 学習戦略・プリセット・Mod Provider 詳細契約（v1）

> **適用範囲:** `learning-system-design.md` に定義した学習計画のうち、外部から追加できる要素、manifest/データ形式、型付きStage、検証手順。
>
> **現行実装状況:** `ExporterRegistry` は存在するが、`TexturePresetRegistry` / `TrainingStrategyRegistry` / `PluginWorkerAdapter` は設計対象であり現行mainにはない。
>
> **契約優先順位:** 本文 → `schema/*.schema.json` → `examples/`。もし不一致が見つかった場合は schema をこの本文へ一致するよう修正する。テストのための例示数値を性能・品質の保証値として使わない。

## 1. 責務を3つに分離する

**Preset** は「目指したい声」を定義する。**Strategy** は「その声へ近づくための既存Operationの組合せ」を定義する。**Provider** は「そのOperationを実際に実行するコード/学習モデル」を提供する。

この区別があるため、Modユーザーは独自モデルを持っていなくてもPresetだけ増やせる。既存Operationを選んでStrategyを作る場合も**C#やPythonを書く必要はない**。一方、独自の学習アルゴリズムが必要な開発者はProviderを追加できる。

```text
TexturePreset            TrainingStrategy             Backend/Provider
   id + traits         id + requirements + DAG      operation + runtime/weights
          └─────────────┬──────────────┘               │
                        ▼                              ▼
             TrainingPlanResolver ← BackendCapabilityRegistry
                        │
                        ▼
                validated TrainingPlan
                        │
                  Process ML Bridge
                        │
             built-in or enabled Provider
                        │
               run manifest / artifacts
```

`ExporterRegistry` は最終成果物を互換形式へ変換する責務だけを維持する。Strategyの選択や音色の判定をExporterへ移さない。

## 2. Registryと識別子

- IDは **publisher-qualified** `publisher.name` 形式を推奨。例: `builtin.classic_synthetic`, `community.retro_pack`。IDは大小文字を区別して固定し、空白・パス区切り・制御文字は禁止。
- manifestに `schema_version`（この契約では `1`）、`version`（SemVer）、日本語表示名、provider、必要な機能、資源参照を持たせる。
- Registryは `(id,version)` を一意キーとする。同じキーの別ハッシュは衝突で拒否し、組み込みIDの乗っ取りも拒否。
- 旧バージョンも共存できる場合は並列登録し、**新規計画**にはポリシーで最も適した互換versionを選ぶ。既存Jobのversionは置き換えない。
- 安定して列挙する: `id`→SemVer→providerId の順（ロケール依存の表示名順では選択を決めない）。
- Manifest読込失敗はそのModだけを `invalid` にし、他の内蔵戦略を壊さない。
- 候補status: `available`（前提と処理が実在）/ `partial`（任意traitだけ不足）/ `unavailable`（資源不足）/ `unsupported`（能力不一致）/ `invalid`（宣言や権利・安全性の検証失敗）/ `disabled`。
- 未実装Operationは `unsupported`。manifestの宣言をもって実能力としない。

## 3. Preset manifest

`preset.json` の必須項目と意味:

| 項目 | 内容 |
|---|---|
| `schema_version` | 対応する宣言形式の版。初期版は `"1"` |
| `preset_id`, `version` | 安定識別子と版 |
| `display_name_ja`, `description_ja` | 日本語UIに表示 |
| `supported_targets` | `talk` / `singing`（両方なら2要素） |
| `traits` | `syntheticness`, `expressiveness`, `breathiness`, `tension`, `articulation` の0〜1目標値 |
| `required_capability_tags` | これがない学習経路ではプリセットの**完全対応**とみなさない条件 |
| `preferred_strategy_tags` | 優先候補探索の助け。これだけでは実能力の証明にならない |

`traits` は音響効果を1:1に表す直接PyTorchパラメータではない。特に「クラシック電子的」にsyntheticnessを指定することと、音声を故意に歪ませることは同義ではない。

**例: クラシック系（宣言のみ）**

```json
{
  "schema_version": "1",
  "preset_id": "community.classic_clean",
  "version": "1.0.0",
  "display_name_ja": "明瞭な電子的音声",
  "description_ja": "音符・発音の正確さを保ちつつ規則的な合成表現を目標にする",
  "supported_targets": ["singing", "talk"],
  "traits": {
    "syntheticness": 0.8,
    "expressiveness": 0.25,
    "breathiness": 0.15,
    "tension": 0.5,
    "articulation": 0.9
  },
  "required_capability_tags": ["style_control"],
  "preferred_strategy_tags": ["controlled_prosody"]
}
```

このJSONは **nameplate** でしかない。Registryは実際に `style_control` を備えるOperationがなければ `partial/unsupported` と表示する。名前を変えただけで音声が変わるという誤実装は禁止。

## 4. Strategy manifest：Modユーザーのコード不要レシピ

単位はファイル `strategy.json`。構成を正確に検証できるよう、Stageの `depends_on` と `operation_id` を明示する。

```json
{
  "schema_version": "1",
  "strategy_id": "community.low-data-timbre",
  "version": "1.0.0",
  "display_name_ja": "少量データの声質活用レシピ",
  "description_ja": "登録済みの学習/推論工程をつなぐ",
  "supported_targets": ["talk"],
  "supported_preset_ids": ["community.classic_clean"],
  "requirements": [
    {"role": "identity_reference", "metric": "eligible_seconds", "op": "gte", "value": 30},
    {"role": "speech_teacher", "metric": "eligible_seconds", "op": "gte", "value": 0}
  ],
  "required_capabilities": ["talk_synthesis", "voice_adaptation", "reusable_talk_model"],
  "stages": [
    {
      "id": "base",
      "operation_id": "builtin.load_tts_base",
      "depends_on": [],
      "input_roles": [],
      "output_types": ["tts_base"]
    },
    {
      "id": "identity",
      "operation_id": "builtin.adapt_identity",
      "depends_on": ["base"],
      "input_roles": ["identity_reference"],
      "output_types": ["talk_model"]
    },
    {
      "id": "verify",
      "operation_id": "builtin.verify_talk_model",
      "depends_on": ["identity"],
      "input_roles": [],
      "output_types": ["verification_report"]
    }
  ],
  "expected_output_types": ["talk_model"],
  "resource_manifest": "resources.json"
}
```

**このサンプルのOperation IDは設計用の例示**であり、現行mainに実在しない。mod登録時に実際に提供されていない場合、`unknown_operation` で拒否する。JSONだけで未知のモデルをダウンロード・訓練できるようにはしない。

### Requirementsの意味

- `role`: `speech_teacher` / `singing_teacher` / `identity_reference` / `texture_reference`。Roleごとに独立した量と適格性を使う。
- `metric`: `eligible_seconds`, `independent_utterances`, `independent_phrases`, `distinct_sources`, `phoneme_coverage_count`, `pitch_bins_observed` 等、**アプリが実装済みの測定指標のみ**。
- `op`: `gte`, `lte`, `eq` 等の固定演算子。算術式、任意JS/Python、eval、動的importは禁止。
- `value`: このstrategyに必要な**成立条件**。全戦略共通の「少量/大量」の普遍閾値にしない。
- 計測状態が `unknown` なら条件の真偽を `unknown` とし、`0` と同一扱いしない。必要な条件なら `needs_measurement` で保留。
- `supported_preset_ids` が空の場合は汎用Strategyを意味する。特定presetのrequired traitsを満たすかは別途BackendCapabilityで判定。
- requirementsが全て満たされても、出力型とBackendの実能力が一致しなければ候補から除外する。

### Stage DAGの型検査

1. `id` が全Stage内で一意。
2. `depends_on` は存在するStageのみ参照。循環依存は禁止。
3. `operation_id` が `OperationRegistry` へ登録済みで、現在のProviderが利用可能。
4. 入力roleの型・数量・対象話者が合っている。
5. 各Operationの出力artifact型と次Stageの期待入力型が一致。
6. 最終的な `expected_output_types` が `reusable_model` の必要条件を満たす。内部preview音声だけのStageをモデル生成と呼ばない。
7. Providerの資源hash、ライセンス、GPU条件がpreflightを通過。
8. すべてのStage/設定をcanonical JSONに正規化してplan hashへ格納。

実際のinput/outputの結合（Stage output名→次Stage input名）はOperationの正式schemaで固定し、型名だけで適当に接続しない。

## 5. 第三者Provider manifest：独自の学習器を実装する人向け

宣言的レシピから一歩進み、独自の実計算を行う場合のみ `provider.json` と実行用workerを配布する。

```json
{
  "schema_version": "1",
  "provider_id": "community.mytrainer",
  "version": "1.0.0",
  "protocol_version": "1",
  "display_name_ja": "外部学習Provider",
  "runtime": {"kind": "external_process", "entry": "worker/my-trainer.exe"},
  "operations": [
    {"operation_id": "community.mytrainer.adapt_timbre", "version": "1.0.0"}
  ],
  "resource_manifest": "resources.json",
  "permissions_requested": ["read_provided_artifacts", "write_job_artifacts"],
  "license_file": "LICENSE"
}
```

- パスはProviderのルートからの相対パス。絶対パス、`..`、シンボリックリンク等による脱出は禁止。
- `runtime.entry` の実行ファイルと全モデル資源のhashを `resources.json` に記録。Python実行が必要ならProvider側に互換runtime一式を同梱するか、実装済みの共通worker契約を使う。ユーザーにpip導入を要求しない。
- 提供元・ソフトコード・checkpoint/weight等の別ライセンスを明示。再配布不可の資源が必須なら本体へ自動同梱しない。
- 任意コード入りProviderは**ユーザーが明示的に有効化**するまで絶対に起動しない。manifest表示や候補の静的照合で外部exeを実行しない。
- 外部プロセス化はリソース/権限を完全隔離するOS sandboxではない。Windows等で実際に強制できない権限は `requested` として表示し、保証しない。ハッシュ一致も安全性の証明ではない。

## 6. Providerとアプリ間のJSON Lines（概念的なv1メッセージ契約）

標準入力へ1行1JSONのrequestを送り、標準出力には1行1JSONの構造化response/eventだけを出す。警告・学習ログはstderr、または許可されたJobログへ保存する。WAVやcheckpointのバイナリをIPC本文へ載せない。

```json
{
  "protocol_version": "1",
  "request_id": "req-123",
  "operation": "probe",
  "provider_id": "community.mytrainer",
  "job_id": null,
  "payload": {
    "target": "talk",
    "preset_id": "builtin.natural",
    "inventory_manifest": "artifacts/input-inventory.json"
  }
}
```

通常response:

```json
{
  "protocol_version": "1",
  "request_id": "req-123",
  "type": "result",
  "ok": true,
  "result": {
    "supported": true,
    "capabilities": ["talk_synthesis", "voice_adaptation"],
    "warnings": []
  }
}
```

エラーresponse:

```json
{
  "protocol_version": "1",
  "request_id": "req-123",
  "type": "error",
  "ok": false,
  "error": {
    "code": "RESOURCE_UNAVAILABLE",
    "message_ja": "必要なモデル資源が見つかりません。",
    "retryable": false
  }
}
```

`probe` は**実行コードを含むProviderでは有効化後だけ**許可する。宣言だけからの静的な能力診断はRegistryがProvider起動なしで行う。

実行operationsは `probe`、`plan`、`execute`、`verify` のうち実装済み分だけを登録できる。必要なstageを満たさないProviderは、そのstageの計画に選ばない。progress eventはrequest_idとstage_id、0〜1進捗を持つが、complete resultを装わない。

### Host側の要求（必須）

- リクエストの `request_id`、`job_id`、`plan_hash`、`provider_id`、`operation_id/version`、入力artifactの `sha256` と許可した出力先を照合する。
- Provider stdoutを不正なJSON・サイズ上限超過・別request応答・予期しないeventで拒否する。
- Providerが成功と応答しても、成果物ファイルの存在・許可path・hash・型・必須内容をHostが再確認し、偽成功を防止する。
- 中断時はworker子プロセスも回収する。時間超過、GPU不足、worker crash、Provider削除、resource改竄を原因別に記録する。
- 不正なProviderを無効化してもProject原本/旧Jobを破壊しない。過去Jobは元のprovider hashがないと同じPlanを再現できない旨を表示する。

## 7. 置き場所とインストール契約

Windowsの通常ユーザー領域を推奨し、実行ファイルのインストール先が読み取り専用でも利用可能にする。

```text
%LOCALAPPDATA%/YourSinger/mods/
└─ community.retro-pack/
    ├─ preset.json
    ├─ strategy.json
    ├─ provider.json       # 宣言のみModなら不要
    ├─ resources.json      # 外部資源不要なら空/省略可
    ├─ LICENSE
    └─ worker/             # コード入りProviderのみ
         └─ my-trainer.exe
```

- 配置ファイルが `preset.json` だけでもRegistryはプリセットを読める。
- `strategy.json` は既存Operationを使う場合に動的コードを必要としない。
- `provider.json` を置くだけで実行コードを自動有効化しない。
- 再読み込みでmanifestだけ再検証し、処理中Jobを途中から別versionへ切替えない。
- UIのMod管理には、提供元/署名状態/ライセンス/必要資源/対応目的/質感/有効化状態/エラーを日本語表示する。
- project固有のコピー/固定version参照は将来の移行を考慮し、Jobにはhashを残す。

## 8. プリセットからModelへ繋がるための厳密な証拠

- `available` はProvider/Stage/資源が揃い実行可能という意味で、**望む音声の完成が保証されたという意味ではない**。
- `verified` は指定targetに対する実推論ができ、互換形式の要件を満たし、可能な範囲でtexture目標を検査した後にしか付けない。
- `classic_synthetic` は音割れ・無音・誤音素と同義にしない。`expressive_complex` は発音や本人性の欠損を隠すものではない。
- Qwen3-TTSのようなモデルが音声を生成できても、そのcheckpointをSBV2モデル形式としてexportできることを意味しない。互換性を実際に検証する。
- OpenUtau DiffSingerのBREC/TENC/voice color等はvoicebank側の機能有無で変わる。要求されたpreset traitを全て実現できなければ `partial` と明示する。

## 9. 実装者の最短確認手順

1. `TexturePresetRegistry` と `TrainingStrategyRegistry` のmanifest読込・schema version・ID衝突をテストする。
2. `SpeakerDataInventory` と `requirements` の各role/metric条件を照合する（未測定を0にしない）。
3. `OperationRegistry` の型付きDAG検証、BackendCapability照合、最終ModelPackage型チェックを実装する。
4. Providerはコード不要宣言Modを先に有効化し、外部Workerは後から別責務で追加する。
5. Workerを追加するときは明示有効化、stdio JSONL protocol、パス/hash照合、停止・失敗・ログを一通りテストする。
6. 既存Jobのinput fingerprint/strategy version/Provider hashを保持し、追加/削除/Mod更新で結果が変わらないようにする。
7. **本物の歌唱・トーク生成音声の検証**で、指定した質感と出力形式の互換性を確認する。未実装Operationを含むModサンプルを成功実装として説明しない。

関連する既存コード: `src/process/YourSinger.Process/processing/export/IModelExporter.cs`、`src/process/YourSinger.Process/processing/ml/bridge/MlWorkerClient.cs`。それらは参考にするが、学習戦略Registryが既に存在すると誤認しない。
