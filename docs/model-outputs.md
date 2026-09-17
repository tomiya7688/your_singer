# モデル出力・互換形式

## 方針

v1 では独自形式を先に作るのではなく、既存の音声合成ソフトで利用できる一般的な形式への出力を優先します。

ユーザーは対象話者ごとに以下を選択できます。

- 歌唱モデルのみ
- トークモデルのみ
- 両方

## 歌唱モデル

### 初期ターゲット候補

- OpenUtau / DiffSinger 系互換

目的は、`your_singer` で学習したモデルを既存の歌唱エディタ側へ持ち込み、歌詞・ノート情報から歌唱生成できることです。

### Exporter責務

Exporterは共通内部データ / 学習済みモデルを、対象実装が要求するディレクトリ構成・設定・モデルファイルへ変換します。

想定構成の概念例:

```text
SingerName/
├─ character metadata
├─ config
├─ phoneme definitions
├─ acoustic model
├─ duration model
├─ pitch model
├─ variance model
└─ vocoder / vocoder reference
```

実際のファイル名・必須項目・ONNX要件等は、実装着手時に対象OpenUtau / DiffSingerの現行仕様を確認して固定します。

## トークモデル

### 初期ターゲット候補

- Style-Bert-VITS2 系互換

目的は、`your_singer` で学習した話者モデルを既存の対応TTS環境へ持ち込み、通常の文章読み上げに利用できることです。

### Exporter責務

想定構成の概念例:

```text
ModelName/
├─ config
├─ model weights
└─ style vectors
```

具体的な設定項目・モデル形式・推論要件は、実装時に対象バージョンへ合わせて固定します。

## 同一素材から両方を作る

共通解析結果から用途別にデータセットを構築します。

```text
Universal Voice Dataset
   ├─ speech segments  ─→ Talk Dataset    ─→ Talk Model
   └─ singing segments ─→ Singing Dataset ─→ Singing Model
```

同じ入力ファイルにトーク部分と歌唱部分が混在していても、それぞれ適した側へ振り分けます。

## 複数話者

複数の話者を選択した場合、話者ごとに独立した出力を生成します。

例:

```text
exports/
├─ speaker_01/
│  ├─ singing/
│  └─ talk/
└─ speaker_03/
   ├─ singing/
   └─ talk/
```

各話者について歌唱 / トークの生成有無を個別指定できる構造を推奨します。

## Exporterインターフェース

将来の形式追加を容易にするため、出力処理は本体から分離します。

概念例:

```text
Exporter
├─ validate(input)
├─ prepare(input)
├─ export(output_dir)
└─ verify(output_dir)
```

候補:

```text
exporters/
├─ diffsinger/
├─ style_bert_vits2/
└─ custom/
```

## 互換性の扱い

「DiffSinger互換」「Style-Bert-VITS2互換」といった名称だけでは互換範囲が曖昧になり得るため、実装時には以下を明記します。

- 対象プロジェクト名
- 対象バージョン / commit / schema
- 必須ファイル
- オプションファイル
- 推論時依存関係
- 学習時依存関係
- 実機読み込みテストの有無

v1のリリース条件には、生成物を対象ソフトで実際に読み込めることの検証を含める方針です。
