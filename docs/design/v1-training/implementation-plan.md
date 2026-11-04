# v1 学習品質・実用モデル生成 — Issue分割と実装順

この表は [親Issue #79](https://github.com/tomiya7688/your_singer/issues/79) に紐づく**実装単位**を示す。v1仕様の大枠（#1、#5、#9、#10、#11、#12、#13、#15）に、最近追加された学習要件を無理に一括実装せず、以下の小さな差分で積み上げる。

## まず把握すること（2026-11-02時点）

- `TrainingJobRecord` に学習modeとweightは未実装、`TrainingJobPlanningService` はSpeech/Singingの厳密一致で選択。
- `AutoCorrectionService` の未校正MFA confidenceの扱いは既存 [#63](https://github.com/tomiya7688/your_singer/issues/63) が先行。
- 歌唱MFA測定recordの入力接続は [#61](https://github.com/tomiya7688/your_singer/issues/61) / [PR #68](https://github.com/tomiya7688/your_singer/pull/68)。同じファイルを並行編集しない。
- 配布物構成は [#72](https://github.com/tomiya7688/your_singer/issues/72) / [PR #74](https://github.com/tomiya7688/your_singer/pull/74)。完成配布物の試験時に統合を確認。
- 文書・設計・合成fixtureの成功と、実データからの生成モデル実用性は別。
- 既存の `AGENTS.md` と `dev-guidelines/workflow.md` を守り、**1ブランチ1Issue、PRレビュー・CI・自己レビューが済むまでマージしない**。

## 具体的な実装順

### Phase 0: 安全な入力と前提（最優先）

[既存 #63](https://github.com/tomiya7688/your_singer/issues/63) を修正する。未計測・未校正confidenceを低品質データと誤認しないことが、後続すべての前提。歌唱MFAを扱うときはPR #68の最新状態も確認する。

### Phase 1: 学習用データの契約・基本weighting

| 順序 | Issue                                                      | 単独で作る成果物                              | 先行           |
| ---- | ---------------------------------------------------------- | --------------------------------------------- | -------------- |
| A01  | [#80](https://github.com/tomiya7688/your_singer/issues/80) | Assessment schema / repository / 旧形式区別   | #63            |
| A02  | [#81](https://github.com/tomiya7688/your_singer/issues/81) | 目的別eligibility・除外理由                   | A01            |
| A03  | [#82](https://github.com/tomiya7688/your_singer/issues/82) | 音素・音域のUtility                           | A02            |
| A04  | [#83](https://github.com/tomiya7688/your_singer/issues/83) | Quality×Utility→weight policyと純粋関数テスト | A03、#63       |
| A05  | [#84](https://github.com/tomiya7688/your_singer/issues/84) | 学習modeの型・Job保存互換                     | なし（並行可） |
| A06  | [#85](https://github.com/tomiya7688/your_singer/issues/85) | Job snapshot/fingerprintと再作成              | A01,A04,A05    |
| A07  | [#86](https://github.com/tomiya7688/your_singer/issues/86) | Speech/Singing/Ambiguousの用途別routing       | A02            |
| A08  | [#87](https://github.com/tomiya7688/your_singer/issues/87) | 両用途の共有話者特徴・来歴                    | A07            |
| A09  | [#88](https://github.com/tomiya7688/your_singer/issues/88) | Mode Resolver / GPU・資源preflight            | A05            |

**Phase 1 Gate:** 低品質でも有用な区間は正のweight、誤話者等は理由付き除外。unknownと実測0が異なり、修正するとfingerprintが変わる。すべてC# unit testで確認できる。

### Phase 2: Trainerへweightを実際に渡す

| 順序 | Issue                                                      | 成果物                                | 先行                |
| ---- | ---------------------------------------------------------- | ------------------------------------- | ------------------- |
| A10  | [#89](https://github.com/tomiya7688/your_singer/issues/89) | SBV2用 `esd.list` と別weight manifest | A04,A06             |
| A11  | [#90](https://github.com/tomiya7688/your_singer/issues/90) | DiffSinger学習入力とweight manifest   | A04,A06、#61/PR #68 |
| A12  | [#91](https://github.com/tomiya7688/your_singer/issues/91) | SBV2実samplerでweightを消費           | A10,A09             |
| A13  | [#92](https://github.com/tomiya7688/your_singer/issues/92) | DiffSinger実samplerでweightを消費     | A11,A09             |

**Phase 2 Gate:** manifestを出力しただけで完了にしない。weightの変更が実Trainerのsamplingに反映されることを、seed固定の統合テストで確認する。

### Phase 3: 複数学習方式と歌唱サブモデル

| 順序 | Issue                                                      | 成果物                                               | 先行            |
| ---- | ---------------------------------------------------------- | ---------------------------------------------------- | --------------- |
| A14  | [#93](https://github.com/tomiya7688/your_singer/issues/93) | SBV2の明示的fine-tune/scratch                        | A09             |
| A15  | [#94](https://github.com/tomiya7688/your_singer/issues/94) | DiffSinger acousticの明示的fine-tune/scratch         | A09             |
| A16  | [#95](https://github.com/tomiya7688/your_singer/issues/95) | 歌唱Durationの実測学習入力                           | A11、#61/PR #68 |
| A17  | [#96](https://github.com/tomiya7688/your_singer/issues/96) | Durationモデル学習・Exporterへ接続                   | A15,A16         |
| A18  | [#97](https://github.com/tomiya7688/your_singer/issues/97) | OpenUtauに対する任意Pitch/Varianceモデルの対応と実装 | A15,A17         |

**Phase 3 Gate:** Modeは必ずrequested / resolved / actually loadedが一致する。新規学習はベース話者checkpointを使わず、追加学習では明示checkpointを検証。**音響学習だけで完全なOpenUtau声庫と判定しない**。既存 `docs/implementation-v1-10.md` のOpenUtau 0.1.565では `dspitch`、`dsvariance` は任意であり、A18を必須の音響/duration/vocoderの検証と混同しない。

### Phase 4: 相互利用、実行証跡・失敗処理

| 順序 | Issue                                                        | 成果物                                   | 先行            |
| ---- | ------------------------------------------------------------ | ---------------------------------------- | --------------- |
| A19  | [#98](https://github.com/tomiya7688/your_singer/issues/98)   | 固定Backendの声質条件入力capability表    | A08             |
| A20  | [#99](https://github.com/tomiya7688/your_singer/issues/99)   | 会話声質→歌唱学習への実接続              | A08,A15,A19     |
| A21  | [#100](https://github.com/tomiya7688/your_singer/issues/100) | 歌唱声質→トーク学習への実接続            | A08,A14,A19     |
| A22  | [#101](https://github.com/tomiya7688/your_singer/issues/101) | 使用weight/初期重み/checkpointの実行報告 | A12,A13,A14,A15 |
| A23  | [#102](https://github.com/tomiya7688/your_singer/issues/102) | 子プロセス含む取消・異常終了回収         | A22             |

**Phase 4 Gate:** backend非対応の特徴を「利用した」と表示しない。トーク/歌唱の音声を別の用途の教師ラベルへ偽装しない。run reportが指定modeと異なる場合は失敗。

### Phase 5: 実モデル品質検証・利用者UI

| 順序 | Issue                                                        | 成果物                                                | 先行                        |
| ---- | ------------------------------------------------------------ | ----------------------------------------------------- | --------------------------- |
| A24  | [#103](https://github.com/tomiya7688/your_singer/issues/103) | SBV2未学習文章の実読み上げと状態記録                  | A14,A22                     |
| A25  | [#104](https://github.com/tomiya7688/your_singer/issues/104) | 歌詞＋音符の実歌唱とOpenUtau互換検証                  | A17,A22（A18は任意）        |
| A26  | [#105](https://github.com/tomiya7688/your_singer/issues/105) | checkpoint評価・比較・選定理由                        | A24,A25                     |
| A27  | [#106](https://github.com/tomiya7688/your_singer/issues/106) | 3つのUI Issueの統合操作確認                           | A33,A34,A35                 |
| A28  | [#107](https://github.com/tomiya7688/your_singer/issues/107) | CI回帰/fixture E2Eと偽成功排除                        | A06,A07,A12,A13,A14,A15,A22 |
| A29  | [#108](https://github.com/tomiya7688/your_singer/issues/108) | 実GPU・配布・UIの総合受入ゲート（機能開発を含まない） | A20,A21,A27,A28,A30,A31,A32 |

### Phase 5a: UI・実GPU・配布をさらに小分け

| Issue                                                            | 単独の成果物                                    | 先行                |
| ---------------------------------------------------------------- | ----------------------------------------------- | ------------------- |
| A30 [#109](https://github.com/tomiya7688/your_singer/issues/109) | 実GPUのSBV2 fine-tune / scratch、読み上げ試験   | A12,A14,A23,A24     |
| A31 [#110](https://github.com/tomiya7688/your_singer/issues/110) | 実GPUのDiffSinger fine-tune / scratch、歌唱試験 | A13,A15,A17,A23,A25 |
| A32 [#111](https://github.com/tomiya7688/your_singer/issues/111) | クリーンWindows配布物の単一起動とruntime検証    | A30,A31,A35、PR #74 |
| A33 [#112](https://github.com/tomiya7688/your_singer/issues/112) | 学習mode・Preflightの日本語UIだけ               | A05,A09             |
| A34 [#113](https://github.com/tomiya7688/your_singer/issues/113) | 学習採用/除外/重みの日本語UIだけ                | A04,A06             |
| A35 [#114](https://github.com/tomiya7688/your_singer/issues/114) | 検証ステータス/生成音声試聴の日本語UIだけ       | A24,A25,A26         |

A27 (#106) は A33〜A35 の操作統合だけ、A29 (#108) は A30〜A32 までの実機・配布記録を照合するリリース判定だけを扱う。大きな実装を1Issueで行わない。

**Phase 5 Gate:** Trainer完了／checkpoint検査／export／実推論／品質／外部互換を混ぜない。real GPU + クリーンWindows配布テストに完走証跡がない段階でv1.0.0を完成扱いしない。

## 並行実装してよいもの

- A05（学習mode保存）はA01-A04と並行できる。
- A07（routing）はA03/A04と並行できるが、A02の用途別適格性契約を維持する。
- A08（特徴共有）・A19（capability確認）は歌唱/トークのTrainer接続と並行できる。
- A10とA11、A12とA13、A14とA15は**別ブランチ・別PR**にして並行可能。ただし共通recordへの変更時は依存PRを明示。
- A24とA25は同じVerification schemaを利用するので先にstatus contractを固定し、別々に実装可能。
- A33とA34は別UI部分として並行可能。A35は検証recordが確定してから開始し、A27で統合する。
- A30とA31は別GPU学習経路なので並行可能。A32とA29は両者の証跡が揃ってから開始する。

## 1 Issueを始めるときの最小コンテキスト

1. 該当Issueを読む。**関連仕様は1ドキュメントだけ先に読む**（Issue末尾から）。
2. Issueで指定された既存クラス・テストと、関連する正本（`docs/implementation-v1-xx.md`）を確認する。全リポジトリを読み込まない。
3. 他PR・Issueの更新を確認。重複実装を避ける。
4. 入出力契約とエラー／例外／キャッシュ境界の変更点を先に書く。
5. 正常系・低品質を残す系・異常系・キャンセルをテストしてから実装する。
6. 1Issueに複数責務が出現したら、無理に実装せず子Issueへ分離し元Issueへ依存を追記する。
7. 変更実装＋テスト＋関連日本語ドキュメントのみを1PRにまとめる。メインブランチへの直接pushはしない。

## 各Issueの受け入れテンプレート

- [ ] 対象のメソッド／record／protocolを変更し、入出力schemaが明示されている
- [ ] 既存の動作と旧プロジェクト／旧job履歴を破壊していない
- [ ] 正常・異常・境界・未測定・キャンセルのうち該当ケースをテストした
- [ ] 固定したsource revision・checkpoint・resource hash・ライセンス要件を破っていない
- [ ] **本物の学習・推論が未検証なら、未検証と明示**した
- [ ] `pwsh tools/dev/check.ps1` および変更部分の xUnit / pytest を実行し、実行結果をPRへ記録した
- [ ] 1PR = 1Issue、CIと自己レビューを完了した
- [ ] 関連文書とテスト変更を同じPRへ含めた

## 運用上の注意

1. 既存の未統合PR #68、#74が存在する状態では、新しいPRで同じファイルを競合変更しない。
2. `#66`（開発運用の未処理PR棚卸し）の指針に従い、open PRが増えたら新規機能実装より依存PR統合を優先。
3. 主観的な「音質が良い」だけでは完了にしない。合成fixtureは契約試験、real GPUは実用試験、対象ソフト試用は互換性試験と分けて証跡を残す。
4. UI・設計書・Issueは日本語。本体はC#/Avalonia、MLはUPD Process層配下のPython worker。ユーザーにPython/.NET別導入を要求しない。
