# v1-02 実装設計: 音声前処理パイプライン

## 対象Issue

- #3 v1-02: 音声前処理パイプラインを実装する

## 目的

v1-01で作成した抽出済み音声を、後続の話者解析・分類・特徴抽出で再利用できる前処理済みデータへ変換する。

処理結果だけでなく、元音声・処理段階・品質値・stage versionを追跡できることを必須とする。

## 処理順

```text
extracted
  ↓
BGM / ボーカル分離
  ↓
ノイズ低減
  ↓
音量正規化
  ↓
品質解析
  ↓
VAD / セグメント分割
  ↓
separated / cleaned / segments + metadata
```

## BGM / ボーカル分離

初期実装は Demucs v4 の2-stem分離を使用する。

workerパッケージにDemucsを同梱し、ユーザー環境のPythonを利用しない。

Demucsが利用できない開発環境ではpass-throughを許可するが、その場合は
`music_leak` を高い注意値として扱う。本番配布物ではDemucsをworkerへ含める。

## ノイズ低減・正規化

FFmpegフィルタを用いて以下を行う。

- 60 Hz以下の不要低域を抑制
- 18 kHz以上の不要高域を抑制
- `afftdn` によるノイズ低減
- `loudnorm` による音量正規化
- mono / 48 kHz / PCM16へ統一

元データは上書きしない。

## 残響

v1-02ではまず残響量を品質指標として推定し、強い残響を検出可能にする。
残響低減処理そのものは、素材へ常時適用せず、後続の専用モデル差し替えを可能にする。

不要な残響除去で話者特徴を破壊することを避けるためである。

## VAD

初期実装では30 ms窓のエネルギーベースVADを使用する。

- ノイズフロアを素材ごとに推定
- 短い無音を同一発話として結合
- 極端に短い区間を除外
- segmentごとに開始・終了時刻を保存
- segment WAVを `segments/` へ保存

後からニューラルVADへ差し替えても上位層を変更しない。

## 品質値

最低限以下を0〜1で保存する。

- overall
- noise
- music_leak
- reverb
- clipping
- silence

これらはv1-02時点の品質推定値であり、将来専用評価モデルへ差し替え可能とする。

## 処理履歴

各処理に以下を保存する。

- stage
- version
- completed_at
- output_path

例:

```text
separation          demucs-v4
denoise_normalize   ffmpeg-afftdn-loudnorm-v1
vad_segmentation    energy-vad-v1
```

## キャッシュ

`metadata/preprocessing/<source_id>.json` に前処理結果を保存する。

stage versionが一致し、cleaned成果物が存在する場合は再処理を省略する。

## worker境界

C#側は個別MLライブラリを直接呼ばない。

```text
AudioPreprocessingService
  ↓
MlWorkerClient
  ↓ JSON Lines
YourSinger.ML
  ↓
preprocessing.py
```

大きな音声データはIPCへ流さずpathのみを渡す。

## エラー処理

worker例外はJSON応答へ変換し、C#側で日本語のエラー表示へ渡せる状態にする。

個別sourceの失敗はmetadataへ記録し、プロジェクト全体の元データを破壊しない。

## 配布

Python workerは最終的にNuitka standalone等で `YourSinger.ML.exe` としてビルドし、
`YourSinger.exe` からのみ起動する。

ユーザーへPython・pip・Demucsの手動導入は要求しない。
