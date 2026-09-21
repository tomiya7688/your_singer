# v1-13 実装設計: Exporter拡張基盤

## 対象

- Issue #14
- DiffSinger / OpenUtau
- Style-Bert-VITS2
- 将来独自model package
- 将来Runtime Control
- 将来Automation Script

## 目的

Universal Voice Dataset以降を再利用しながら、出力形式だけを差し替えられる共通Exporter境界を用意する。

v1では独自形式そのものは実装しない。

## IModelExporter

全Exporterは以下を実装する。

- ExporterMetadata
- ExportAsync(ProjectWorkspace, ModelExportRequest)

具体形式固有のconfig生成・ファイル配置は各Exporter内部へ閉じ込める。

## Registry

`ExporterRegistry` へ `IModelExporter` を登録する。

UIや後続Automationはformat名のswitch文を持たず、`exporter_id` から実装を解決する。

組み込みExporter:

- `openutau-diffsinger`
- `style-bert-vits2`

## Metadata / versioning

各Exporterは以下を宣言する。

- exporter_id
- display_name
- exporter version
- category
- output_format
- target_runtime
- target_runtime_version
- runtime_controls
- automation_hooks

Exporter実装versionと対象runtime versionは分離する。

## ModelExportRequest

共通領域:

- exporter_id
- speaker_id
- model_name
- options
- extension_data

`options` は対象形式固有のJSON payloadとし、各Exporter adapterが既存の型付きrequestへ変換する。

`extension_data` は将来の独自package metadataなどを後付けするための領域とする。

## ModelExportResult

- exporter_id
- exporter_version
- output_directory
- generated_files
- extension_data

これによりUI、Automation、E2Eは個別Exporterの戻り値型を直接参照しない。

## Runtime Control境界

`RuntimeControlDescriptor` に将来の制御値定義を持たせる。

初期予約例:

- timbre
- formant
- breathiness
- style_weight

v1では値を音声モデルへ適用するRuntime Control engineは実装しない。
Exporter metadataとして「将来保持可能な制御軸」を宣言するだけに留める。

## Automation Script境界

`AutomationHookDescriptor` に以下を定義する。

- hook_id
- display_name
- payload_schema_version

歌唱ではノート単位、トークでは発話単位のAutomationを将来追加できる。

v1ではscript parser / executorは実装しない。

## 前処理からの独立

新Exporter追加時に以下を変更しないことを原則とする。

- メディア入力
- 音声前処理
- 話者解析
- Speech / Singing分類
- Universal Voice Dataset
- 自動補完・補正
- TrainingJob

新形式は学習成果物または共通datasetを入力とするExporter adapterとして追加する。

## 組み込み形式

`DiffSingerModelExporter` は既存 `DiffSingerExporter` をラップする。

`StyleBertVits2ModelExporter` は既存 `StyleBertVits2Exporter` をラップする。

各既存Exporterの形式固有validationはそのまま保持する。
