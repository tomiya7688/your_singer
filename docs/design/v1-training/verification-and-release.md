# 学習後の実音声検証・モデル選択・リリース判定

## 原則

Your Singerは研究用実験を目的とせず、ユーザーが実際に利用可能なモデルを出力する。以下の工程は独立した状態を持つ。

```text
学習入力検証 → Trainer完了 → checkpoint検査 → Export構築
                                      ↓
                         実推論 → 互換環境で再試行 → 品質評価
                                      ↓
                              出力可能 / 注意 / 失敗
```

checkpointが存在するだけで「モデル完成」「音声生成成功」としない。

## 保存契約（提案）

`training/verification/<job-id>/verification.json`

```json
{
  "schema_version": "1",
  "job_id": "job-example",
  "dataset_fingerprint": "...",
  "candidate_checkpoint_sha256": "...",
  "backend": "style-bert-vits2",
  "compatibility_target": "Style-Bert-VITS2 2.7.0",
  "stages": {
    "checkpoint": {"state": "passed", "reasons": []},
    "export": {"state": "passed", "reasons": []},
    "inference": {"state": "passed", "reasons": []},
    "external_compatibility": {"state": "not_run", "reasons": ["runner_unavailable"]},
    "quality": {"state": "warning", "reasons": ["low_intelligibility"]}
  },
  "test_inputs": [],
  "artifacts": []
}
```

stage.state: `not_run`, `running`, `passed`, `warning`, `failed`, `unavailable`。`not_run`、`unavailable` は `passed` ではない。

各記録に `source_hash`, `target_revision`, `runner_version`, `timestamp`, `stdout_log_path`（必要時）、`audio_path`, `audio_sha256` を保存する。ログ・サンプル音声はproject配下へ保存し、ユーザーが確認・再試行できるようにする。学習ジョブ本体の既存 `TrainingJobState.Succeeded` は**学習器の成功**であり、製品の完全な検証成功とは異なる。過去の履歴は新しい検証結果がない場合「未検証」と表示する。

## トークモデルの検証

1. 対象Style-Bert-VITS2 2.7.0の設定、スタイルベクトル、safetensorsを実際に読み込む。
2. 学習に投入していない日本語文章（短文、長文、句読点、濁音・拗音、レアな音素）を使って**本物の推論**を行う。固定文章のhard-coded dummy WAVを許可しない。
3. 生成WAVが正常デコードされ、有限値・非空・非ほぼ無音であることを確認。無音判定の閾値と短文例外をconfig/versionで固定。
4. 音声認識による文字一致率、話者類似度、歪み／音割れ指標を取得できる場合は保存。取得不能な指標は `unavailable`、空値を満点にしない。
5. 推論環境での実音声生成を再確認し、証跡（モデルhash、文章、推論runtime、出力WAV）を残す。

## プリセット別の実品質・能力検証

質感プリセットは必須であり、生成モデルの実用性だけでなく、**目標質感に沿って学習レシピ・推論制御・実際の出力が変化しているか**を確認する。手法と要件の正本は [Preset→学習レシピ設計](preset-recipe-selection.md)、[DiffSingerの質感実現](diffsinger-preset-realization.md)。

- 話者/同一素材/同一の未学習歌詞・音符で `classic_synthetic` と `expressive_complex` ほかを比較。
- 使用した `recipe_id/version`、Dataset/Weight、Acoustic/Duration/Pitch/Varianceの構成、予測できる表現軸、実checkpoint/音声hashを記録。
- **目標質感の差が出るか**と、**発音/音程/本人性/無音・歪みが悪化していないか**を分けて評価する。歌唱の電子的表現を自然度スコアだけで落とさない。
- 表現Embeddingを有効化しても学習用教師/予測model/Exporterが無い場合は未対応。モデルが変わらない/声質が壊れる場合は未完成。
- OpenUtauの追加expressionはVoicebankの実対応を検証。独立voicebank variantは別Singerとして読み込めること。
- 「現在の固定DiffSingerではその質感が作れない」を**仕様削除の理由にしない**。必要な学習stage/モデル構成の追加を完了条件へ含める。

## 歌唱モデルの検証

1. OpenUtau 0.1.565互換のvoicebank設定、音素辞書、acoustic/duration/pitch等の必要なmodelを検査する。
2. 学習に含まない歌詞＋音符・音域を組み合わせ、対応するDiffSinger/OpenUtau推論経路で実レンダリングする。
3. 音声がデコード可能で非ほぼ無音、途中破綻／欠け／異常な無音がないことを機械検証する。
4. 参照ノートに対するF0誤差（有声部の中央値・外れ値等）、音素/歌詞の実現、話者類似性を測定できる範囲で記録する。未測定は合格扱いにしない。
5. OpenUtauによる実機導入・歌唱レンダリングが未実施の場合は `external_compatibility=not_run`。内部WAV生成のみで「OpenUtau互換実証済」とは言わない。

## 学習・評価データ分離

- 評価用区間は**同一録音または強い重複を考慮**して学習集合から分ける。単純な連続区間分割で同一歌唱フレーズを学習・評価へ入れない。
- バックエンドの既存train/val分割を必ず照合し、train重みをvalに適用しない。
- 評価用テキスト／歌詞と楽譜は学習データ外のものを明示する。
- 生成モデルが素材の音質欠損を再現していないか（雑音/圧縮歪み）、対象話者以外の声が混じっていないかを検査する。
- 音声認識やspeaker embeddingによる数値だけでは十分な品質を保証できない。結果を試聴可能にし、不確実性を明記する。

## 最良checkpoint選択

`CheckpointEvaluationService`（提案）:

1. まず読み込み不可能、NaN/無音等の明確に破損した候補を不適格にする。
2. 適格候補を、検証用音声の発音・話者類似度・音程・歪み等の**取得可能な指標**で比較する。
3. v1ではBackend固定の評価設定とweightを文書化し、根拠を保存して「自動候補」を提示する。無い指標を0や満点で補わない。
4. 自動選択不能なら無理に1位を付けず、ユーザーが比較試聴して選択できるようにする。
5. 出力された最良モデルとcheckpointの対応・選定理由を保存し、学習結果を置換しない。

## UIと実用メッセージ

- 区分: 「学習中」「学習完了・検証待ち」「生成試験中」「利用可能」「注意あり」「検証失敗」。
- ユーザーには使用された区間数・低品質だが採用された件数・除外理由・使用学習方式とcheckpointを表示する。
- 学習に対して「利用可能」と表示するには、対象用途の**実音声生成**が成功していること。外部ソフトでの互換性は別ステータスとして必ず示す。
- 生成サンプルをUIから再生・保存できる。自動検証が実行できない場合は操作・失敗理由・再試行導線を示す。
- テスト済みと未検証を混同しない。表示文言はすべて日本語。

## 受け入れテストの階層

1. **Unit**: 文字列・enum・ステータス遷移、閾値・正当なWAV検証、候補選択、path/hash検査。
2. **Integration（mock worker）**: 要求payload・進捗・キャンセル・異常終了・再開可能性と未完了公開防止。
3. **Integration（実Worker/固定runtime）**: 公式Trainerと本物の推論コードを起動、偽の成果物で合格しない。
4. **GPU E2E（実素材）**: 単一／フォルダ、複数話者、MC+歌唱混在、低品質の希少音素、両学習方式、トーク文章生成、歌唱音符生成。
5. **配布E2E**: クリーンWindowsで `YourSinger.exe` だけから起動。Python/.NET事前導入なし、実ランタイム資源に対応、インストール指示なし。

実GPUテストのための素材は権利状態が把握された非公開テスト資源として管理し、録音ファイルを公開リポジトリへコミットしない。機密素材をCIログへ出さない。

## リリース用チェックリスト

- [ ] 検証済みの固定バックエンドとcheckpoint hash・ライセンスを記録
- [ ] 追加学習／新規学習を歌唱・トークでそれぞれ実行
- [ ] 品質weightの実Trainer反映証跡がある
- [ ] 混在素材を無仕分けで両用途へ振り分ける
- [ ] モデルの実推論を検証し、外部互換未検証なら明示
- [ ] 分割・重複・失敗・未校正confidenceの回帰テスト
- [ ] 配布環境が独立Python/.NET不要、アプリ起動口は1つ
- [ ] 導入前の `#61 / PR #68`、`#63`、配布PR `#74` との競合を確認
