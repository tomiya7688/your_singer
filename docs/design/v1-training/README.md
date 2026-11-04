# v1 学習品質・実用モデル生成 — 実装設計入口

## この文書の位置づけ

今回の学習戦略は **生成目的 → 質感プリセット → 素材の方向性（会話/歌唱/量/品質） → 登録済み学習方法の選択・組み合わせ** で構成する。自動判断するのは学習工程であり、ユーザー指定の目標ではない。

本ディレクトリは `docs/training-strategy.md` のv1必須要件を、実装可能な入出力契約・責務・検証へ落とした設計群です。**学習システムの決定順序・状態・成功条件は `learning-system-design.md` を正本とし、登録形式は `learning-strategy-protocol.md` と `schema/` を正とします。**将来の `docs/roadmap.md` の Source Profile駆動Adaptive Training（v2.x）とは区別します。

**製品のゴール**: ユーザーが雑多な動画・音声フォルダを投入するだけで、話者を選び、歌唱／トーク／両方のモデルを現実に生成・試聴・出力できること。品質の低い素材を機械的に捨てず、有用性に応じて扱う。

## mainの実装確認に基づく現状（2026-11-02）

| 境界           | 既存実装                                                                                             | 追加／確認すべき内容                                     |
| -------------- | ---------------------------------------------------------------------------------------------------- | -------------------------------------------------------- |
| 共通データ     | `UniversalVoiceDatasetRecord`、`TrainingDatasetSnapshotService`                                      | 計測状態と用途別適格性・重み。元観測値は変更しない       |
| 話者・内容分類 | `ContentClassificationService`、`UsageDatasetRouter`、`UniversalDatasetViewBuilder`                  | 曖昧区間の扱い、手動事前仕分け不要、用途別routing        |
| 自動補正       | `AutoCorrectionService`                                                                              | 未計測confidenceの誤除外（既存 #63）、適格性と重みを分離 |
| ジョブ         | `TrainingJobPlanningService`、`TrainingJobRecord`、`TrainingDatasetSnapshotService`                  | 学習方式、input snapshot、weight policy version、再作成  |
| 歌唱学習       | `DiffSingerDatasetBuilder`、`DiffSingerTrainerService`、Python `diffsinger_training.py`              | weightの実学習反映、明示的な初期重み選択、歌唱推論検証   |
| トーク学習     | `StyleBertVits2DatasetBuilder`、`StyleBertVits2TrainerService`、Python `style_bert_vits2_session.py` | weightの実学習反映、明示的な初期重み選択、読み上げ検証   |
| UI             | `src/ui/YourSinger.App/MainWindow.*`                                                                 | 日本語の方法選択・利用素材の根拠・検証状態               |
| テスト         | `tests/YourSinger.Process.Tests/`、`tests/worker/`                                                   | 契約、低品質活用、偽成功排除、実素材GPU検証              |

既存の `docs/implementation-v1-04.md`、`-09.md`、`-10.md`、`-11.md`、`-14.md` は保持し、ここに記載した新しい要求と競合する箇所は、対象IssueのPRで修正する。過去の実装済み機能やテスト成功を、未検証の機能の完成根拠に転用しない。

## 層と処理順（UPD）

```text
UI（フォルダ/話者/歌唱・トーク/学習方式/検証結果）
  ↓ UI Messenger
Process Commander（段階の指揮）
  ↓ Process Processing
  TrainingDatasetSnapshotService（現行の手動修正を反映）
    → AutoCorrectionService（設定に従い観測・補正viewを生成）
    → TrainingEligibilityService（用途別の使用適格性）
    → TrainingUtilityService（音素/音域などの価値）
    → TrainingWeightPolicy（決定的に重み計算）
    → TrainingJobPlanningService（不変のジョブ入力・方式を確定）
    → Dataset Builder（用途別形式へ変換）
    → ML Bridge → Python Worker（対応Trainer）
    → Verification（推論・互換性・品質検証）
  ↓ Data Messenger
Data Layer（project workspace内への永続化）
```

上の新しいService名は**提案名**。正式採用時には既存フォルダ命名・公開APIに合わせる。Python workerは `src/process/YourSinger.Process/processing/ml/worker/` 配下とし、独立した第4層にしない。UI/DataからPythonを直接呼ばない。

## 実装者への固定ルール

1. **1 Issue = 1責務と小さな検証可能な差分**。新しい無関係な機能・リファクタリングを混ぜない。
2. 学習入力（選択区間、重み、学習方法、採用したcheckpoint、使用したpolicy version）を固定し、再実行で別結果を無言で生成しない。
3. 観測データと派生した学習用評価を分離する。モデルによる推測値、測定済み値、未計測値を混同しない。
4. 不明なバックエンド挙動は静かに代替しない。互換版の公式コード・資源hashを確認し、未対応を明示する。
5. 学習成功・成果物生成・対象ソフトでの推論成功・品質合格を**別々に記録**する。
6. User UI、設計文書、Issue、テスト説明は日本語。コード識別子は既存命名に合わせる。
7. 既存の未統合PR #68（MFA参照）と #74（配布）は着手前に状況を再確認し、無関係な差分を混ぜない。既存Issue #63（未校正confidence）は重複Issueを作らず先行修正する。
8. 実装完了には「テスト追加・該当テスト実行結果・変更ファイル・非検証項目・PR」まで記録する。モデル完成をsynthetic fixtureだけで宣言しない。

## 読む順番

1. **[学習システム基本・詳細設計（正本）](learning-system-design.md)**
2. **[学習戦略/Mod Providerの詳細契約（正本）](learning-strategy-protocol.md)**
3. [機械検証用JSON Schema](schema/texture-preset.schema.json) / [Strategy Schema](schema/training-strategy.schema.json) / [Provider Schema](schema/training-provider.schema.json)
4. [Mod/学習計画の具体例](examples/README.md)
5. [目的・質感・素材量からの学習計画（事例）](goal-texture-data-planner.md)
6. [音声方式・質感プリセットの分類](texture-preset-taxonomy.md)
7. [素材傾向からの学習方法選択](data-profile-strategy-selection.md)
8. [Modユーザー/開発者向け導入ガイド](strategy-plugin-api.md)
9. [データ契約・重み付け](contracts-and-weighting.md)
10. [自動仕分けと声質特徴の共有](routing-and-cross-domain.md)
11. [学習モード・Trainer統合](training-modes-and-backends.md)
12. [検証・完成判定](verification-and-release.md)
13. [実装Issueの依存順（PR #115・マージ待ち）](https://github.com/tomiya7688/your_singer/blob/issue-79-v1-training-issue-plan/docs/design/v1-training/implementation-plan.md)

実行・レビュー手順は `AGENTS.md`、`dev-guidelines/workflow.md`、`tools/dev/check.ps1` を正とする。
