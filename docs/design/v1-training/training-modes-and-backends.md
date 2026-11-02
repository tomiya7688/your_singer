# 学習方式とバックエンド統合 — v1詳細設計

## この設計の前提

- トーク互換: Style-Bert-VITS2 **2.7.0**, commit `d8148f3090ee5038ca7b4e4b327116c64467f952`（既存 `docs/implementation-v1-11.md`）。
- 歌唱互換: OpenUtau **0.1.565**、学習側のOpenVPI DiffSinger **v2.5.1**, commit `323a5693245b827caba0ab1a7371fe5ca885b061`（既存 `docs/implementation-v1-10.md` と `docs/diffsinger-acoustic-input.md`）。
- `YourSinger.exe` 一箇所起動、別途Python/.NET導入不要、ローカルGPU基本、workerが外部Trainerを起動する現在の配置を維持。
- **Fine-tuning専用アプリにはしない**。トーク・歌唱それぞれに新規学習を提供するが、少量データでの品質を保証しない。

## 1. 学習モード契約

ユーザー指定（`TrainingMode` 提案enum）:

| 値 | 表示（日本語） | 正確な意味 |
|---|---|---|
| `auto` | 自動（推奨） | 必要資源と素材条件を事前検証したうえで `fine_tune` または `from_scratch` のどちらかを**実行前に確定** |
| `fine_tune` | 既存モデルから追加学習 | 対応した音響／歌唱checkpointの重みを明示的に読み込む |
| `from_scratch` | 最初から新規学習 | 対象音声合成モデルのtrainable weightsを初期化する。ベース話者モデルのcheckpointは一切読み込まない |

`from_scratch` でも文字言語エンコーダ・音声前処理・音素辞書・一般的なVocoder等、**モデルと異なる共有基盤を利用する場合はある**。これらはpretrainedであることと、話者モデルをFine-tuningすることを区別してUI・記録に示す。完全に全コンポーネントを乱数初期化するという誤表示は禁止。

事前学習重みが指定されていないのに自動で同梱checkpointを使用すること、重みの読み込み失敗を握りつぶして意図せずscratchに移行すること、scratch指定を無言でfine-tuneすることは禁止。

## 2. ジョブ保存契約

既存 `SpeakerTrainingSelection` / `TrainingJobRecord` に以下を拡張する（既存のpublic API互換性をテストする）。選択した方式と、実際に使う方式を区別する。

```json
{
  "job_id": "job-a1",
  "target": "singing",
  "requested_training_mode": "auto",
  "resolved_training_mode": "fine_tune",
  "backend": "diffsinger",
  "backend_revision": "323a5693245b827caba0ab1a7371fe5ca885b061",
  "model_profile": "balanced",
  "initialization": {
    "type": "pretrained_checkpoint",
    "components": [{"name": "acoustic", "sha256": "...", "path": "..." }]
  },
  "assessment_manifest": {"relative_path": "...", "sha256": "..."},
  "training_config": {"relative_path": "...", "sha256": "..."},
  "dataset_fingerprint": "..."
}
```

- `requested_training_mode` はユーザー設定、`resolved_training_mode` は事前チェック確定後の実行契約。Autoを毎回Worker側で勝手に解決し直さない。
- `initialization.type` は `pretrained_checkpoint` または `random_initialization`。scratch時の `components` は空であること。
- 内容が結果に影響するConfig、weight manifest、seed、version、resource SHA、user overrideはfingerprintへ含める。
- 相対パスはProjectWorkspace範囲内だけ。外部固定bundleは既存のresource lockで照合する。
- 旧ジョブ履歴は保持。新規モード指定を持たないジョブは明示的に再作成（旧recordを新仕様の学習成功とみなさない）。

## 3. プレフライト（Trainer起動前）

提案API: `TrainingModeResolver.Resolve(job, datasetStats, hardwareInfo, backendCapabilities, resourceLock)`。

順序:

1. `target` とbackendの対応・互換版を確認。
2. backendの `supported_modes`、初期重み要件、モデル構成（音響/時間長/ピッチ等）を確認。
3. データ量・音素／音域カバレッジ・評価用素材・GPU/VRAMを診断する。基準値未満なら根拠つき警告。成立不可能な条件（空dataset等）は明示的にエラー。
4. `auto` は適合する同梱checkpointを利用できれば原則 `fine_tune`、なければ `from_scratch` の成立可否を調べる。**どちらも成立しなければジョブ作成を失敗**。非対応モードへ無言で切替えない。
5. `fine_tune` は対象話者用の初期重みの存在・形式・hash・版互換を検証し、欠損時はエラー。
6. `from_scratch` は対象モデルを初期化できることと、不要な音声合成pretrained checkpointが参照されないことを確認する。
7. 結果（resolved mode, config, seed, resource IDs）を不変manifestへ保存してから実行する。
8. workerの起動結果では**実際に読込／初期化した重みの一覧**を返し、指定と突き合わせる。一致しなければ失敗。

UIの「高品質 / 標準 / 軽量」は速度・VRAM等のモデルプロファイルであり、学習モードと直交させる。CPU-onlyやVRAM不足を黙って成功扱いせず診断する。

## 4. トーク: Style-Bert-VITS2 2.7.0

現行 `StyleBertVits2TrainingSession.prepare()` は `pretrained/G_0.safetensors`、`D_0.safetensors`、`DUR_0.safetensors` を必須とする。現行の公式 `train_ms.py` は既存checkpointがなければpretrained読み込みを試み、例外時にscratchで進む動作がある。**この暗黙fallbackを製品の学習モード選択として流用しない。**

追加する責務:

- `StyleBertVits2TrainerService`: requested / resolved modeをworkerへ送る。
- `StyleBertVits2TrainingSession`: モードに応じたモデル初期化配置とpreflight。初期化時に使用した資源hashを記録。
- `style_bert_vits2_training.py`: modeの検証、実行後のinitialization report照合。
- `train_ms.py` 固定コミットの入力ローダ／初期化箇所を限定パッチとして包むか、明示的にconfig / model_dirへ初期重みを準備する。意図しないfallbackは検出して失敗。
- 無関係な公式資源を勝手に変更しない。固定ソースを変更する場合は `tools/prepare_style_bert_vits2_bundle.py` の再現可能なoverlay / patchとして管理し、base revision、patch hash、ライセンス表示を更新する。

`esd.list` は公式の4列形式のまま維持。重みは `assessment.json` / `train_weights.json` として別管理し、前処理が確定した `train.list` の **実ファイル名に完全結合**させる。見つからない／重複／不一致なら学習開始不可。

重みの反映は公式データローダに対する **単一の適用方法** に限る（初期候補: seeded weighted sampler）。利用中のPyTorch sampler、DDP数、epochの扱いを調査・テストし、1 GPUでの再現可能な実行をまず確保する。複数GPU未対応ならpreflightで明示する。

## 5. 歌唱: OpenVPI DiffSinger v2.5.1

現行 `DiffSingerTrainerService` は `train_diffsinger` にjob/configを渡し、workerが `scripts/train.py --config ... --exp_name ... --reset` を実行する。実際の学習方式・初期checkpointはconfigに依存するため、**modeの記録・検査・初期化方法の明示化が必要**。

- 公式の音響 `acoustic`、duration、pitch、varianceおよびVocoderへの依存を**別々に確認**する。完成する歌唱モデルに必要なサブモデルを隠さない。
- 新規学習ではtrainable voice modelの必要サブモデルを新規初期化する。固定Vocoder等を再利用する場合はユーザーに明示する。
- 追加学習では固定revisionに対応した初期checkpointの実読み込みを各サブモデルで検証する。
- 固定済み `diffsinger-source.lock.json` と配布runtimeを維持。学習ジョブのmanifestと公式bin内容のhashを照合する。
- 高品質データだけを残して希少な発音・音域を学習から失わない。DiffSinger用のbinarization後にもsegment_id→訓練sampleの対応を追跡する。
- Samplingの修正は学習用集合にのみ適用し、val集合には適用しない。公式の内部item indexと `assessment.json` の各重みが一致することを証明するテストが必要。
- 音響だけ学習してduration/pitchが未生成の状態を、OpenUtauへ持ち込める完全歌唱モデルと呼ばない。

## 6. Worker IPC・異常系

- 既存の `MlWorkerClient` に合わせ、`train_style_bert_vits2` / `train_diffsinger` のpayloadを**後方互換可能なversion付き契約**へ拡張。
- 追加フィールド: `job_id`, `input_fingerprint`, `resolved_training_mode`, `initialization_manifest`, `training_weights_path`, `seed`, `backend_revision`。旧payloadは無言で重み付き学習へ移行させない。
- Workerは `run_manifest.json`（モード・読み込んだ初期モデルのhash・weightの件数と範囲・Trainer終了コード・成果物path/hash）を生成する。
- 取消／異常終了時に子プロセスとGPU memoryを回収し、結果は失敗・取消として保存。未完了ディレクトリを成果物として公開しない。
- stdoutはIPC JSON Lines専用、ログはstderrかjob内ログへ書く。大きな音声データはfile pathで渡す。

## 7. 分割する実装Issueの順

1. モード・capabilityとジョブ保存契約（既存データ互換・再作成）
2. モードresolveとpreflight
3. トークbackendのモード実行（暗黙fallback解消）
4. 歌唱音響backendのモード実行
5. 歌唱のサブモデルと出力整合
6. 用途別weight manifest
7. トークweighted sampler／実効果テスト
8. 歌唱weighted sampler／実効果テスト
9. Workerのrun manifest・失敗回収
10. 検証フェーズへ接続

## 8. テスト・受け入れ

- 同一入力でも `fine_tune` と `from_scratch` のinitialization reportが異なる。
- `fine_tune` は指定checkpointが欠損・hash不一致・互換性不一致の場合は**必ず失敗**する。
- `from_scratch` はベース話者checkpointを読み込んだら失敗する。
- `auto` のresolve結果がjobに固定され、workerが勝手に変更しない。
- 同一version＋seed＋入力でtraining weight消費順が再現できる（GPU演算の完全bit一致までは保証しない）。
- 元の `esd.list` / DiffSinger data schema / OpenUtau互換を壊さない。
- 実GPU検証が未実施の場合、「モードの仕様とプロトコル実装済み」であっても「実モデル生成完了」とは表示しない。
