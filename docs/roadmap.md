# バージョンロードマップ

このロードマップは `your_singer` の開発方向を示すためのものです。
各バージョンの内容は固定契約ではなく、実装状況・品質検証・依存プロジェクトの変更に応じて調整します。

## v0.x — 開発・検証フェーズ

### 目的

v1.0.0に必要な基盤を段階的に実装し、実素材で検証します。

### 主な内容

- プロジェクト / フォルダ入力基盤
- 音声抽出・前処理
- 話者ダイアリゼーション / クラスタリング
- 話者選択・統合・除外
- Speech / Singing分類
- Universal Voice Dataset
- 50音 / 音素カバレッジ診断
- 歌唱向け品質診断
- 自動補完 / 補正 ON/OFF
- 低品質でも有用な区間を生かす用途別の基本weighting（適格性・品質・学習価値で判定）
- 学習モード（自動／事前学習モデルからの追加学習／新規学習）
- 自動Speech/Singing振り分け（手動での事前仕分け不要）
- 学習後の実推論・品質・互換性検証
- 学習ジョブ管理
- OpenUtau / DiffSinger系出力
- Style-Bert-VITS2系出力
- デスクトップUI
- self-contained配布
- E2E / 回帰 / 互換性テスト

## v1.0.0 — 初回安定版

### 目標

ノイズを含む動画・音声素材群から、対象話者の歌唱モデル・トークモデルを、一般的な既存形式で生成できる状態を初回安定版とします。

### 必須機能

- 単一ファイル / フォルダ入力
- フォルダ中心のプロジェクト管理
- 差分再解析
- 複数話者検出
- 話者選択 / 統合 / セグメント除外
- Speech / Singing分類
- 50音 / 音素カバレッジ診断
- 品質警告
- 補完 / 補正 ON/OFF（既定ON）
- 基本的なセグメント別学習weighting（低品質でも有用なデータは活用）
- ユーザーが生成目的と**音声モデル系統を踏まえた質感プリセット**を指定し、発話/歌唱/声質参照の実効量・多様性・比率を診断して、登録済み学習方法を自動選択・組み合わせる
- 会話主体・歌唱主体・大量・少量・混在・低品質だが有用な素材など、重複可能な基本Source Profileに基づく学習計画
- Modユーザーが宣言JSONで質感プリセット／既存stageを組み合わせた戦略を登録できる基盤と、第三者が独自Workerを接続する拡張契約
- 歌唱主体素材からのトーク生成、発話主体素材からの歌唱生成を対応Backendで実行可能にする
- 歌唱・トークそれぞれの追加学習および最初からの新規学習
- 入力をユーザーに事前分類させない自動振り分け
- 学習後の実音声生成・対象アプリ互換性・品質検証
- 歌唱のみ / トークのみ / 両方の生成
- ローカルGPUを基本とした学習
- ハードウェアに応じた High Quality / Balanced / Lightweight 等のモデルプロファイル
- OpenUtau / DiffSinger系の歌唱モデル出力
- Style-Bert-VITS2系のトークモデル出力
- Python / .NET Runtime の別途インストール不要
- 公開起動ポイントを YourSinger.exe 1箇所にする

### v1.0.0では必須にしないもの

- Source Profileを用いた高度なAdaptive Training（v1の基本weightingとは別）
- 独自モデル形式
- 声色の時系列スクリプト制御
- realtime voice runtime
- remote GPU training

## v1.x — 品質向上・素材理解の強化

### Source Profile

v1の基本Source Profile（会話/歌唱の有効量と方向性、話者声質、希少性・品質）を土台に、収録環境・発声様式などの**詳細なSource Profile**を保存・可視化できるようにします。

候補:

- recording quality
- content type
- music leak
- reverb
- compression / codec degradation
- noise characteristics
- speaking style
- singing style
- pitch range
- dataset density
- segment consistency
- confidence / reliability

Source Profileは単一ファイル単位だけでなく、セグメント・話者・プロジェクト集約値を持てる構造を想定します。

### 品質診断の高度化

- Source Profileの可視化
- データセットの偏り表示
- 推奨追加素材の提示
- 不足している発音・音域・スタイルの案内
- 学習結果との相関を利用した診断改善

### Adaptive Trainingの準備

v1で動作する基本Source Profile→学習戦略自動選択を土台に、収録条件や実際の生成品質との相関を詳しく記録し、戦略の精度を高めます。v1で学習方法の自動選択を先送りする意味ではありません。

## v2.x — 高度なAdaptive Training

v1の基本プロフィールと登録済み戦略からの自動構成を発展させ、収録条件・学習結果のフィードバックなどに応じてweightやaugmentation・学習段階をより細かく自動最適化します。

```text
Source / Segment Profiles
        ↓
Training Strategy Selector
        ↓
Dataset Builder / Weighting
        ↓
Training
```

調整候補:

- dataset sampling ratio
- Source Profileに応じた動的なsegment weighting（基本weightingはv1から提供）
- augmentation強度
- completion / correction強度
- 実測値 / 推定値の重み
- loss weighting
- speech / singingデータ比率
- style / prosodyの利用方法
- 高品質素材を優先するsampling
- ノイズ・残響・BGM漏れが強い素材の重み低減
- 歌唱データが少ない場合の声質適応寄り学習
- 十分な本人歌唱がある場合の実測重視学習

例:

### 高品質な本人歌唱が豊富

- 実測歌唱を強く利用
- 補完 / 一般化の介入を必要最小限へ
- 本人固有の発声・歌唱傾向を保持

### ライブ / BGM混入歌唱

- BGM漏れ・残響confidenceを考慮
- 低信頼セグメントのweightを下げる
- 補正・整合化を強める

### 会話中心で歌唱が少ない

- target speakerの声質特徴を強く利用
- 汎用歌唱能力との適応を利用
- 歌唱側の補完・一般化を強める

### ノイズや圧縮劣化が強い素材

- 品質confidenceに基づいてsampling / weightを調整
- 外れ値除去や補正を強化
- 元素材の劣化を話者特徴として誤学習しないようにする

Adaptive Trainingは「素材タイプごとに完全に別パイプラインを作る」のではなく、Universal Voice Datasetを共通基盤とし、その後段の学習入力・重み・戦略を変える形を基本とします。

## 将来 — 独自Voice Runtime / Scriptable Voice

v1で構築したUniversal Voice Datasetとモデル生成基盤を利用し、既存形式に限定されない独自形式へ発展させます。

候補:

- 独自Voice Model package
- Runtime Control
- Automation Script
- timbre / formant / breathiness等の時間変化
- pitch / phoneme / emotion依存の声色変化
- voice morphing
- realtime / low-latency inference

詳細は `future-format.md` を参照してください。

## v1とv2の境界

v1.0.0からユーザー指定の目的・質感プリセットと基本Source Profile（会話/歌唱の方向性、実効素材量・多様性・品質）に応じた戦略選択、Mod学習戦略Registry、品質・希少性に基づく重み付き学習、素材自動分類、追加学習／新規学習、実推論での検証を提供します。v2.xは収録環境・フィードバックに基づく連続的な戦略最適化を高度化します。詳細は `training-strategy.md` を参照してください。

## 設計上の原則

ロードマップ上の将来機能のためにv1を過剰設計しない一方、次の境界はv1から保持します。

- 元データ / 加工データ / 推定データを区別する
- provenanceを保持する
- Source / Segment単位のmetadataを拡張可能にする
- Universal Voice Datasetを共通基盤とする
- Dataset Builder / Trainer / Exporterを分離する
- 学習戦略を後から差し替えられるようにする
- Source Profile追加のために既存Projectを作り直す必要がない構造にする
