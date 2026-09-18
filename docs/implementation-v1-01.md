# v1-01 実装設計: プロジェクト管理・入力基盤

## 対象Issue

- #2 v1-01: プロジェクト管理・フォルダ中心の入力基盤を実装する

## 実装範囲

この段階では、後続の音声前処理や話者解析へ渡せる「素材登録済みプロジェクト」を作成する。

実装するもの:

- 新規Voice Projectの作成
- フォルダ入力を主導線とした最小UI
- 単一ファイル入力
- 再帰的なメディア走査
- 対応拡張子によるフィルタ
- SHA-256による内容識別
- size / mtime / hash / analysis version の保存
- 未変更素材の再解析省略
- FFmpegによる内部WAVへのデコード
- 元素材と抽出済み音声の分離
- 処理進捗通知
- 読み取り不能ファイルの安全なスキップ

## ソース配置

```text
src/
├─ ui/
│  └─ YourSinger.App/
├─ process/
│  └─ YourSinger.Process/
│     └─ processing/
│        ├─ media/
│        └─ project/
└─ data/
   └─ YourSinger.Data/
      ├─ models/
      ├─ processing/
      └─ repositories/
```

MLはこのIssueでは使用しない。後続Issueで必要になった時点で
`src/process/.../processing/ml/` 以下へ追加する。

## プロジェクトワークスペース

```text
project/
├─ project.json
├─ sources/
├─ extracted/
├─ separated/
├─ cleaned/
├─ segments/
├─ rejected/
├─ metadata/
├─ features/
├─ cache/
└─ exports/
```

元素材はユーザーが選択した場所を参照し、加工済み音声は
`extracted/` へ保存する。元ファイルを上書きしない。

## 差分判定

素材ごとに以下を保持する。

- path
- size
- modified_time
- content_hash
- analysis_version

同一パスについてこれらが一致し、抽出済み成果物も存在する場合は
再デコードを行わない。

content hashはSHA-256を使用する。ファイル名だけでは同一性を判断しない。

## 内部音声

FFmpegで以下へ統一する。

- WAV
- PCM 16-bit
- 48 kHz
- mono

これはv1-01時点の後段処理用共通入力であり、将来必要になれば
stage versionを変更して再生成できる。

## エラー処理

個別素材の読み込み・デコード失敗はプロジェクト全体を破棄しない。
Sourceに失敗状態とエラー内容を保存し、他素材の処理を継続する。

FFmpegが見つからない場合は、日本語メッセージで配布物の確認を促す。

## UI方針

UI文言は日本語とする。

v1-01では以下のみを実装する。

- 「フォルダから新規プロジェクト」を主ボタンとして表示
- 「単一ファイルを選択」を補助導線として表示
- 現在の処理内容と進捗を表示
- 登録結果の概要を表示

話者選択、品質診断、学習画面などは #13 で実装する。

## 配布との関係

メインアプリは `YourSinger` としてビルドする。
公開時は self-contained publish により `YourSinger.exe` を唯一の起動導線にする。

FFmpegは公開パッケージの `tools/ffmpeg/` に同梱する想定とし、
ユーザーへFFmpegの手動導入を要求しない。
