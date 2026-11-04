# v1 既存の未完了Issue 残作業一覧

2026-11-05 時点のIssue本文・コメントに基づく。リリース関連(#48, #49, #62, #72)は動作確認後の方針で保留のため含めない。精度向上(#116〜#130)は [packets/accuracy-q00-q12.md](packets/accuracy-q00-q12.md)。「根拠docs」は設計の根拠となる既存文書。

### #1 v1 親Issue

- 残作業: 本文のチェックは全て未更新。進捗は子Issue側で管理される。追加の完成条件(2026-10-31)=重み付き学習、新規学習、自動分類、実生成の確認。未達の完了条件: 全て。
- 依存: 子 #2〜#15。根拠docs: `docs/spec-v1.md` §2、`docs/training-strategy.md`。
- 着手可否: 保留(子の完了待ち。本文の未チェックと実状況の不整合は要確認)。

### #9 自動補完・補正エンジン

- 残作業: 未録音音素の実生成と誤ラベル再認識。実モデル一式での合成→再認識→話者検証、配布ワーカーと補助モデルの同梱、GUI実操作、学習品質への効果が未確認。PR #34(F0補間)と #37(音素補完の実験経路)は反映済み。初回モデルのない話者と歌唱条件付き生成が残る。
- 依存: #6, #7, #8。根拠docs: `docs/implementation-v1-08.md`、`docs/phoneme-supplements.md`、`docs/phoneme-completion-candidates.md`、`docs/spec-v1.md` §8。
- 着手可否: 着手可(未確認の実モデル検証から)。

### #11 歌唱モデル生成・Exporter

- 残作業: 公式 binarization が `AP`/`SP` の音素被覆検査で失敗(2区間で実測、2026-10-25)。無音・息の根拠ある検出とラベル付け、未知区間の拒否の設計。その後に GPU学習、ONNX出力、OpenUtau レンダリング、weight付き学習、新規学習。未達の完了条件: OpenUtau導入と実レンダリング成功。
- 依存: #6, #8, #9, #10、測定記録の接続 #61。根拠docs: `docs/model-outputs.md`、`docs/diffsinger-acoustic-input.md`、`docs/forced-alignment.md`、`docs/implementation-v1-10.md`。
- 着手可否: 先行待ち(#61 / PR #68。AP/SP を担当するIssueは本文になし)。

### #12 トークモデル生成・Exporter

- 残作業: 本文のタスクと追加要件が全て未チェック(コメントなし)。未達の完了条件: 成果物を対象環境で読み込み、任意文章の音声生成まで成功すること。追加学習・新規学習の実動作、読み上げ評価の記録が必要。
- 依存: #6, #7, #9, #10。根拠docs: `docs/implementation-v1-11.md`、`docs/talk-trainer-bundle.md`、`docs/training-strategy.md`。
- 着手可否: 先行待ち(#9 が未完了)。

### #13 デスクトップUIフロー

- 残作業: 全タスクが未チェック(コメントなし)。追加要件=手動仕分け不要、学習方式の日本語選択、採用・除外区間と重み状況の確認、生成音声の試聴と失敗/未検証/完了の区別。未達の完了条件: フォルダ選択から Export までGUIのみで到達できること。
- 依存: #2, #4, #7, #8, #9, #10。根拠docs: `docs/implementation-v1-12.md`、`docs/pipeline.md`、`docs/design/v1-training/verification-and-release.md`。
- 着手可否: 先行待ち(#9 が未完了)。

### #15 E2E検証・回帰テスト

- 残作業: 実素材E2E(再開済み)。完全な binarization・実学習・ONNX変換・OpenUtau実レンダリングが未確認。fixture/import/配布CIの成功と実素材E2Eを分けて記録する。未達の完了条件: フォルダ投入から歌唱・トーク両モデルの生成と利用、weightが学習入力・lossへ反映される検証、#63 の検証。
- 依存: #11, #12, #13, #14。根拠docs: `docs/implementation-v1-14.md`、`docs/training-strategy.md`、`docs/design/v1-training/verification-and-release.md`。
- 着手可否: 先行待ち。

### #61 MFA測定記録の参照を共通datasetと歌唱jobへ接続する

- 残作業: PR #68(OPEN、Draftではない)で Codex が作業中。PR本文は「全差分のレビュー、実学習とGUIは未確認」とし、未解決のリスクとして次の3つを挙げる: 並行するデータセット保存で編集が失われる可能性、対象外segmentまでの検証、リンクを含む作業領域の扱い。Julesの限定レビューは処理中。Issue本文の完了条件(参照の保持と検証、旧キャッシュ再解析、歌詞変更での無効化、歌唱jobへの由来接続、C# formatter)の充足は未確認。
- 依存: #59 / PR #60(マージ済み)。根拠docs: `docs/forced-alignment.md`、`docs/implementation-v1-05.md`。
- 着手可否: 着手済み(PR #68 のレビューと3リスクの解消待ち)。

### #63 未校正のMFA信頼度を自動補正で低信頼と判定しない

- 残作業: `AutoCorrectionService` が `AsrConfidence`/`AlignmentConfidence` の 0.35 未満を除外する(コード上で確認)。MFAは未校正のため 0 を記録する。未達の完了条件: 未校正を reject しない、測定済み0と null/unknown の区別、ON/OFF・旧UVD・ユーザー修正・実低confidence の回帰テスト、#80 のStatus型との整合。
- 依存: #61 と整合、関連 #80。根拠docs: `docs/forced-alignment.md`「精度と修正歌詞」、`docs/design/v1-training/contracts-and-weighting.md` §3。
- 着手可否: 着手可(#61 の保存形式と矛盾させない)。

### #65 到達不能コードの静的検査をCIで継続する

- 残作業: Python・PowerShell・JavaScript の検査の選定とバージョン固定、CI組込み、合成例での検出確認、例外理由の記録。未達の完了条件: ローカルとCIで同じ検査を実行、検出範囲外の条件を明記。
- 依存: なし。根拠docs: `tools/dev/check.ps1`、`AGENTS.md`。
- 着手可否: 着手可(PowerShell検査ツールは未選定)。

### #67 READMEの実装状況の更新

- 残作業: 「設計・仕様策定段階」を処理段階ごとの実装・試作・未実装・未検証の区別に置換。mainのみと open PR 上のみの機能を区別。未達の完了条件: 全項目。
- 依存: 関連 #66。根拠docs: `docs/roadmap.md`、`docs/pipeline.md`。
- 着手可否: 着手可(実E2E結果が出るまで「検証済み」は書けない)。

### #75 生成会話の少数観測補助の確認

- 残作業: (1) `phoneme_supplement.py` の `_phones` が無声化母音 `I/U` を `i/u` として扱うか確認し、必要なら統一。(2) 生成候補の再検証に自動補正の前後どちらの観測を使うか確認し、仕様に明記。いずれも回帰テストを先に書く。
- 依存: Refs #9, #66, #43。根拠docs: `docs/implementation-v1-08.md`、`docs/phoneme-supplements.md`。
- 着手可否: 着手可(補正前後の判断は仕様決定が必要)。
