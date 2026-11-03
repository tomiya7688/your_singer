# v1-09 実装設計: 学習対象選択と再学習フロー

## 対象Issue

- #10 v1-09: 学習対象選択と再学習フローを実装する

## 目的

同じVoice Projectの解析済みデータから、話者ごとに歌唱のみ・トークのみ・両方を選択し、設定違いの学習ジョブを再生成できるようにする。

## 生成目的と質感を先に指定する（v1学習計画の補足）

入力のspeech/singing比率でユーザーの目的を決めない。ユーザーは話者ごとに**生成目的**と**目標声質・質感**を指定する。両方生成する場合はTalk/Singingそれぞれの質感・参考区間を選べる。

`SpeakerDataInventory`（提案）で目的別の教師データ量と、別用途でも本人の声質を抽出できる参照量を分ける。その後、`TalkTrainingStrategyPlanner` / `SingingTrainingStrategyPlanner` が個別に学習計画を作り、`TrainingJobPlanningService` が固定する。

例: 歌唱のみ50分・会話0分でユーザーが「トーク＋歌唱時の声質」を選んだ場合、発話教師0を理由に歌唱Jobへ切り替えない。対応するTTS/声質適応があるときはトークJobを作り、未対応なら不足能力を説明する。実際の固定Backendに対する能力確認が必要。

**学習手法を先に決めるのではない。** Fine-tune / scratchは段階ごとの初期化方式にすぎない。詳細は [目標→素材量→学習計画](design/v1-training/goal-texture-data-planner.md)。

## 登録された学習戦略の自動選択

目的・質感を指定した後、`SpeakerDataInventory` のspeech/singing教師量、声質参照量、独立フレーズ数、音素/音域カバレッジ、品質と校正状態を確認する。各値の十分性はBackendと学習strategyごとの条件で判断し、絶対分数だけで分岐しない。

`TrainingStrategyRegistry`（内蔵とMod）から適用候補を取得し、Talk/SingingそれぞれのStrategyPlannerがステージ候補を構成する。学習mode（fine_tune/scratch）はstageごとの初期化手段であり、最上位で学習方法を選ぶ唯一の軸ではない。利用したプリセット・strategy/providerのID/version・能力・source_profile hash・選定理由を学習Jobへ保存する。

モデルの系統調査とプリセットは [texture-preset-taxonomy](design/v1-training/texture-preset-taxonomy.md)、素材方向性は [data-profile-strategy-selection](design/v1-training/data-profile-strategy-selection.md)、第三者登録は [strategy-plugin-api](design/v1-training/strategy-plugin-api.md) を参照する。

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
