# 将来の独自形式構想

## 位置づけ

このドキュメントは将来構想です。v1では実装必須ではありません。

v1では既存形式への出力を優先しますが、内部構造は将来的な独自形式を追加できるように設計します。

## 目的

将来の独自形式では、単一の固定された声色モデルではなく、モデル本体・ランタイム制御・スクリプトを組み合わせて、発声中に声色や発声特性を動的に変化させられることを目指します。

```text
Voice Model
+
Runtime Control
+
Automation Script
```

## 制御対象候補

- timbre / 声色
- formant
- breathiness
- power / tension
- softness
- whisper
- brightness
- age-like character
- emotion / style
- vibrato characteristics
- pitch-dependent timbre
- phoneme-dependent timbre

## 時系列制御の例

```text
0.0s  voice = normal
2.4s  timbre = soft
4.0s  breathiness = 0.7
6.2s  formant = +0.15
8.0s  style = whisper
```

スクリプトAPIの概念例:

```python
voice.timbre("soft", 0.8)
voice.formant(-0.2)
voice.breathiness(0.5)
voice.interpolate("soft", "power", duration=2.0)
```

## 自動制御候補

スクリプトによる明示指定だけでなく、以下のようなルール駆動も将来候補です。

- 高音域では自動的にpower寄りへ変化
- 低音域ではsoft寄りへ変化
- 特定の母音だけformant補正
- 長音ではbreathinessを徐々に増やす
- 文末ではsoftへ補間
- 感情タグに応じてスタイルを変化

## v1との接続

独自形式のために前処理を作り直さないことが重要です。

```text
Universal Voice Dataset
├─→ DiffSinger Exporter
├─→ Style-Bert-VITS2 Exporter
└─→ Future Custom Model Builder
```

そのためv1から以下を保持します。

- 話者特徴
- 音素特徴
- F0
- energy
- style / prosody特徴
- 音域別特徴
- 実測 / 推定の区別
- 補完 / 補正履歴

## 非目標

現段階では以下を固定しません。

- 独自ファイル拡張子
- スクリプト言語
- ランタイムAPI
- パラメータ名
- モデルアーキテクチャ
- リアルタイム動作要件

これらはv1の前処理・モデル生成基盤が安定した後に設計します。
