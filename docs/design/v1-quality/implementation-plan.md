# v1 音声解析精度 — 実装Issueと依存順

親Issue: [#116](https://github.com/tomiya7688/your_singer/issues/116)

## 目的

現行のDemucs固定→整音→規則ベース分類→単一ASR→自己相関F0という経路をベースラインとして、**異なる手法を同じ区間に適用・照合し、採用理由と実生成精度を検証できる処理**へ進化させる。

- 1 Issueで1つの契約または処理境界だけを変更。
- 1 Issueが完了しても、単独で「最終モデルの精度改善を実証」とは言わない。
- 旧プロジェクトと既存キャッシュを壊さず、段階的に接続。
- 低品質でも有用な音声は [v1学習設計 #79](https://github.com/tomiya7688/your_singer/issues/79) の用途別eligibility/weightに渡す。

## P0（基準・破損防止）

既存 [#63](https://github.com/tomiya7688/your_singer/issues/63): MFA confidence=0（未校正）の誤除外を修正。

| 順 | Issue | 1件の成果物 | 依存 |
|---|---|---|---|
| Q00 | [#117](https://github.com/tomiya7688/your_singer/issues/117) | 正解付き素材・分割・Baselineレポート | なし |
| Q01 | [#118](https://github.com/tomiya7688/your_singer/issues/118) | raw/sep/cleanedの候補manifest・元時刻への変換 | Q00 |
| Q02 | [#119](https://github.com/tomiya7688/your_singer/issues/119) | rawを残し素材条件で候補生成 | Q01 |

**Gate A:** 元の音声が消えず、音声候補の時間ずれ・派生の来歴・キャッシュが一意。合成混合の正解付き評価が再実行できる。

## P1（異種モデルでの精度改善）

| Issue | 単独の成果物 | 依存 |
|---|---|---|
| Q03 [#120](https://github.com/tomiya7688/your_singer/issues/120) | 第2系統の分離モデルAdapter＋資源ロック | Q02,Q00 |
| Q04 [#121](https://github.com/tomiya7688/your_singer/issues/121) | 目的別候補品質判定・選択・rawへの戻り道 | Q03,Q00 |
| Q05 [#122](https://github.com/tomiya7688/your_singer/issues/122) | 話者複数窓/重複話者/未校正スコア | Q01,Q00 |
| Q06 [#123](https://github.com/tomiya7688/your_singer/issues/123) | 会話/歌唱のwindowと曖昧区間・混在分割 | Q00,Q01 |
| Q07 [#124](https://github.com/tomiya7688/your_singer/issues/124) | 複数ASR候補と日本語歌詞/音素整合 | Q02,Q00 |
| Q08 [#125](https://github.com/tomiya7688/your_singer/issues/125) | 複数F0系列・オクターブ誤差検出 | Q01,Q00 |
| Q09 [#126](https://github.com/tomiya7688/your_singer/issues/126) | 歌詞候補とMFA音素境界の整合/未校正 | Q07、既存#61/PR #68、#63 |

**Gate B:** 少なくとも異なる分離方式2系統、F0の独立2方式、複数認識候補を比較可能。違いがあれば解決根拠を保存し、根拠不足なら `unresolved`。異常や声の消失を黙認しない。

## P1（信頼性と実利用）

| Issue | 成果物 | 依存 |
|---|---|---|
| Q10 [#127](https://github.com/tomiya7688/your_singer/issues/127) | score_kind、校正状態、用途別評価・weightへの接続 | Q00,Q05,Q06,Q07,Q08 |
| Q11 [#128](https://github.com/tomiya7688/your_singer/issues/128) | Baselineとの比較＋実モデル音声品質ゲート | Q04〜Q10、実GPU試験 (#109/#110) |

**Gate C:** speaker DER/JER、speech/singing macro-F1/abstain、歌詞CER、F0/voicing、残BGM・加工劣化、最終声質を層別評価している。**すべてのモデル/候補を増やしたこと自体を改善の証拠としない**。

## 並行可能な作業

- Q00開始後、Q01と評価・注釈の拡張は並行可能だが、gold holdoutをモデルに合わせて変更しない。
- Q01確定後、Q05（話者）、Q06（分類）、Q08（F0）は別モジュールとして並行可能。
- Q02後、Q07（ASR）とQ03（第二分離）を並行可能。Q04はQ03完了後に選択評価。
- Q09はPR #68が未統合なら接続先を先に確認。Q10はstatus契約で #80/#83と整合。
- Q11はモデル精度と速度を同じ実環境で計測する。旧経路を廃止するPRに含めない。

## 実装者向け「読めば着手できる」手順

1. 指定Issueとリンク先の設計文書**1本**を読む。
2. 対象の既存クラス/テストだけを開く（最初からリポジトリ全体を再解析しない）。
3. 入出力record、`source/candidate/segment` のIDとhash、timebase、model revision、result statusを確定する。
4. 正常例1つ・破損/誤話者/無音等の負例・キャンセル・キャッシュ更新のテストを書く。
5. UPDのProcess processing配下の実処理で実装し、MessengerやCommanderへ機械学習を移さない。
6. AI/MLのスコアを「正解率」とラベルしない。測定校正がない場合 `uncalibrated` / `unknown` とする。
7. 既存本番経路が劣化したらすぐ戻せるように、モデルID/設定versionと採用candidateをジョブへ保存する。
8. `pwsh tools/dev/check.ps1`、対象xUnit/pytest、必要な実モデル手動試験を実行して結果をPRへ記録する。
9. UI・仕様説明・Issue/PRは日本語。配布・checkpoint license確認は各model adapterごとに行う。

## 精度改善の完了基準

- 資源を同梱した実Workerで候補生成できる（ダミー候補だけでない）。
- 素材条件別に**生成モデルの最終結果**を比較し、悪化を検知できる。
- 学習データの誤ラベル・誤話者・誤F0を減らす改善であることが正解付きデータと最終モデル試験で示される。
- 効果が不明なら `inconclusive`、未実施なら `not_run` と記録し、「精度が上がった」とは表示しない。
- 元音を含む全候補を保全し、低品質だが有用なデータは用途別weightingへ提供できる。
- `#79` の実用学習・実音声検証と合わせてv1.0.0の完成条件とする。
