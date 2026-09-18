# 技術アーキテクチャ / 配布設計

## 目的

`your_singer` は、AI / 音声処理には Python エコシステムを活用しつつ、ユーザーには Python や .NET Runtime の手動インストールを要求しない、**単一のデスクトップアプリケーションとして配布・起動できる構成**を採用します。

このドキュメントは v1 の技術構成と責務境界を固定するためのものです。

## 採用技術

### メインアプリケーション

- 言語: **C#**
- ランタイム: **.NET**
- GUI: **Avalonia**
- 設計: **UPD Commander Base Design + OOP**
- OOP checker: 必須ではない。CI / レビュー補助として任意利用。

### ML バックエンド

- 言語: **Python**
- Deep Learning: **PyTorch**
- 音声処理 / ASR / diarization / F0 / separation / training 等は Python エコシステムを優先して利用する。

### AI 開発支援

- **ai-context-reducer** を導入する。
- `AI_CONTEXT.md` 等を用い、Source of Truth / Responsibility Map / Task Routing / targeted validation を整備する。

## 配布上の絶対条件

ユーザーに以下を要求しない。

- Python の別途インストール
- pip による環境構築
- .NET Runtime の別途インストール
- 開発ツールの導入

ユーザーが起動する公開エントリポイントは原則として **`YourSinger.exe` 1箇所**とする。

内部実装上、複数の exe / worker / DLL / runtime / native library を含むことは許容する。

例:

```text
YourSinger/
├─ YourSinger.exe              # 唯一の公開起動ポイント
├─ *.dll
├─ workers/
│  ├─ YourSinger.ML.exe
│  ├─ YourSinger.Training.exe
│  └─ ...
├─ runtimes/
├─ models/
└─ resources/
```

「製品内部の実行ファイル数」ではなく、**ユーザーが一つのアプリとして起動・操作できること**を必須条件とする。

## プロセス構成

```text
YourSinger.exe
  │
  ├─ C# / Avalonia Main Application
  │   ├─ UI Layer
  │   ├─ Process Layer
  │   ├─ Data Layer
  │   ├─ Project Management
  │   ├─ Job Management
  │   ├─ Hardware Profile Selection
  │   └─ ML Worker Control
  │
  └─ IPC
      ↓
Process Layer
  └─ processing/
      └─ ml/
          ├─ bridge / protocol / profiles
          └─ worker/
              ├─ media/audio analysis
              ├─ separation / denoise
              ├─ diarization
              ├─ ASR
              ├─ phoneme alignment
              ├─ F0 / energy
              ├─ completion / correction
              ├─ singing training
              └─ talk training
```

## UPD への適用

メインアプリケーションは UPD Commander Base Design に従う。

### UI Layer

- Avalonia View
- UI Commander
- UI Messenger
- UI Processing

### Process Layer

- Process Commander
- Process Messenger
- Process Processing
- ML Worker 呼び出しの指揮
- 学習ジョブ / 解析ジョブの orchestration

### Data Layer

- Data Commander
- Data Messenger
- Data Processing
- Project / Settings / Cache / Training Job / Model Artifact 等の永続化

最重要原則:

- Commander は実処理を持たない。
- Messenger は層間通信を行い、業務処理を持たない。
- 実処理は Processing / service / adapter 側へ分離する。
- UI は ML ライブラリや Data 実装を直接知らない。
- Python Worker は Avalonia / UI を知らない。

## ML Worker 境界

C# 側から Python ライブラリを直接呼び出す設計は避ける。

ML 処理は worker 境界の後ろへ隔離する。

概念例:

```text
Process
  ↓
ML Worker Client
  ↓
IPC Request
  ↓
Python Worker
  ↓
AudioSeparator / SpeakerAnalyzer / PitchExtractor / Trainer
```

C# 側は個別の Python ライブラリ名や内部 API を知らず、用途単位の request / result 契約のみを扱う。

## IPC 方針

v1 では複雑な分散システムを前提にしない。

初期候補:

1. **stdio + JSON Lines**
2. localhost の socket / named pipe

基本方針:

- 小さな命令・状態・結果 metadata を IPC で送る。
- WAV / tensor / model weights 等の巨大バイナリを IPC に直接流さない。
- 大きな成果物は project workspace 上のファイルとして保存し、IPC では path / artifact id を受け渡す。
- request id / job id を持ち、非同期ジョブの進捗・キャンセル・失敗を追跡可能にする。
- worker が異常終了した場合、C# 側で検出してユーザーへ通知できるようにする。

具体的な IPC 実装は実装開始時に固定するが、この責務境界は変更しない。

## Python Worker の配布

Python はユーザー環境の Python を利用しない。

Python worker は build 時に standalone 配布物へ変換する。

初期候補:

- Nuitka standalone
- 必要に応じて PyInstaller 等を比較

原則:

- ML / CUDA / PyTorch 系は one-file 化を必須としない。
- worker 配下に runtime / native DLL / Python module が複数存在してよい。
- 起動は必ずメインアプリから行う。
- ユーザーが worker を手動で起動する運用は想定しない。

## .NET の配布

メインアプリは self-contained publish を前提とする。

ユーザー環境に .NET Runtime がなくても起動できるパッケージを作る。

## GPU / Hardware Profile

学習・重い推論は **ローカル GPU を基本前提**とする。

ただしハードウェア差を吸収するため、処理モデルを差し替え可能にする。

ユーザー向け profile:

- Auto
- High Quality
- Balanced
- Lightweight

概念例:

```text
HardwareProfiler
  ↓
ModelProfileSelector
  ↓
ModelProfile
  ├─ separation
  ├─ diarization
  ├─ ASR
  ├─ pitch
  ├─ completion/correction
  ├─ singing training
  └─ talk training
```

Auto では GPU / VRAM / RAM 等を確認し、適切な profile を選ぶ。

ユーザーは手動で profile を上書きできる。

CPU-only 環境は完全非対応とはしないが、処理時間や利用可能機能に制約が出ることを許容し、Lightweight profile 等で可能な範囲を提供する。

## Adapter 方針

ML backend の具体的実装を上位層へ漏らさない。

例:

```text
ISpeakerAnalyzer
  ├─ HighQualitySpeakerAnalyzer
  └─ LightweightSpeakerAnalyzer

IPitchExtractor
  ├─ HighQualityPitchExtractor
  └─ LightweightPitchExtractor
```

実装モデルの変更・比較・軽量化を行っても、UI / Project / Dataset / Exporter を大きく変更しない構造を目指す。

## OOP Checker の位置づけ

`oop-design-checker` の利用は **必須条件ではない**。

設計の Source of Truth は:

1. このリポジトリの仕様書
2. UPD Commander Base Design
3. 本プロジェクトで定める OOP / 責務境界

とする。

Checker は以下の用途で任意利用する。

- CI の補助
- C# コードの設計逸脱検出
- レビュー時の静的チェック

Checker を通すために不自然な設計へ変更してはならない。

## v1 の非目標

- Python を完全排除すること
- ML コードを C# へ再実装すること
- 全内部 worker を 1 exe に物理結合すること
- cloud training を必須にすること
- 分散 worker / remote worker を v1 で実装すること

## 将来拡張

責務境界を維持することで、将来以下を追加可能にする。

- worker の追加 / 分割
- ONNX / native inference worker
- remote GPU worker
- custom voice runtime
- realtime inference
- 独自モデル形式
- automation script runtime


## ソース配置方針

`ml` は独立した第4層にはしない。ML処理は **Process層が担当する実処理の一部**として配置する。

概念構成:

```text
src/
├─ ui/
│  ├─ commanders/
│  ├─ messengers/
│  ├─ processing/
│  └─ views/
├─ process/
│  ├─ commanders/
│  ├─ messengers/
│  └─ processing/
│     ├─ media/
│     ├─ audio/
│     ├─ speaker/
│     ├─ dataset/
│     ├─ training/
│     └─ ml/
│        ├─ bridge/
│        ├─ protocol/
│        ├─ profiles/
│        └─ worker/
└─ data/
   ├─ commanders/
   ├─ messengers/
   ├─ processing/
   ├─ repositories/
   └─ models/
```

Python worker のソースも `process/processing/ml/worker/` 配下に置く方針とし、MLをトップレベルの独立レイヤーとして扱わない。

責務上は:

- UI → Process を通して ML を利用する
- UI から ML worker を直接呼ばない
- Data から ML worker を直接呼ばない
- Process が ML worker の起動・ジョブ送信・進捗受信・キャンセル・異常終了処理を指揮する
- 実際のAI/音声処理は worker 内の処理モジュールが担当する

この配置は、MLを特別扱いした独立アーキテクチャへせず、UPDの責務境界の中へ収めることを目的とする。
