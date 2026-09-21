# v1-09 実装設計: 学習対象選択と再学習フロー

## 対象Issue

- #10 v1-09: 学習対象選択と再学習フローを実装する

## 目的

同じVoice Projectの解析済みデータから、話者ごとに歌唱のみ・トークのみ・両方を選択し、設定違いの学習ジョブを再生成できるようにする。

## 学習対象

`TrainingTarget` は以下を持つ。

- Talk
- Singing
- Both

話者ごとに `SpeakerTrainingSelection` を作成し、補完/補正ON/OFFも同じ設定に含める。

## ジョブ分割

複数話者を選択した場合は話者ごとに分割する。

さらに `Both` は内部でTalkジョブとSingingジョブへ分割する。

これにより成果物・状態・失敗を用途単位で独立管理できる。

## 解析済みデータの再利用

学習ジョブ作成時には以下を再実行しない。

- メディア抽出
- 音声前処理
- 話者解析
- Speech / Singing分類
- 共通特徴抽出

Universal Voice Dataset と自動補完・補正viewを参照してsegment ID一覧を確定する。

## 補完/補正設定

各ジョブに `AutoCorrectionEnabled` と `DatasetFingerprint` を保存する。

これにより同じVoice Projectでも補完ON/OFFの違いを追跡できる。

## 再学習

過去の `TrainingJobBatch` から話者・用途・補完設定を再構築し、新しいbatchを生成する。

解析済みデータは共通で再利用する。

## 状態

各ジョブは以下の状態を持つ。

- Pending
- Running
- Succeeded
- Failed
- Canceled

開始時刻・完了時刻・エラー内容も記録する。

## 成果物

`TrainingArtifactRecord` に以下を保存する。

- kind
- path
- format
- version

実際のDiffSinger / Style-Bert-VITS2形式への出力は後続Exporter Issueで担当する。

## 永続化

```text
metadata/training-jobs/<batch-id>.json
```

にbatch単位で保存する。

## 完了条件との対応

- 同じVoice Projectから設定違いで再学習可能
- 話者単位でジョブ分割
- 用途単位でジョブ分割
- 補完/補正ON/OFFを保持
- 学習ジョブ設定・状態・成果物を永続化
