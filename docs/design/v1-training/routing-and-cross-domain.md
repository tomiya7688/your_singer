# 自動分類・用途別routing・トーク/歌唱の相互活用

この文書は学習データの用途別分類を扱う。**ユーザーが生成したいモデルの種類と声質・質感の選択は、入力音声の分類とは別概念**であり、[生成目的・質感・素材量からの学習計画](goal-texture-data-planner.md) を正とする。

## 原則

ユーザーは1つのフォルダ／動画へ会話・歌唱を混在させたまま投入する。事前に「歌唱用」「トーク用」を選り分ける作業は不要。話者選択・必要な手動修正は任意。

現行: `ContentClassificationService` が `SegmentContentClassification` の `ContentType` と `SpeechScore` / `SingingScore` を保存し、`UsageDatasetRouter`、`UniversalDatasetViewBuilder`、`TrainingJobPlanningService.AddTargetJob` が用途別の厳密一致で区間を選択する。これを破壊せず拡張する。

## 提案するrouting契約

入力: 手動上書き済み `UniversalVoiceSegment`、最新classification、Speaker assignment、対象target、補完設定。

出力: `UsageRoutingDecision`（提案クラス）。

```json
{
  "segment_id": "seg_2",
  "speaker_id": "spk_01",
  "talk": {"role": "primary", "status": "eligible", "reason_codes": []},
  "singing": {"role": "feature_only", "status": "eligible", "reason_codes": ["speaker_reference"]},
  "classification_origin": "automatic",
  "classification_confidence": {"value": 0.83, "status": "measured"}
}
```

`role`: `primary`（対応タスクの音声・教師ラベルとして使用）、`feature_only`（音声そのものを別タスクの教師ラベルにせず共有話者特徴として使用）、`review`（分類曖昧）、`excluded`。

- `speech`: talk primary、singingはspeaker特徴を補助利用できるが、勝手に歌唱の教師データに変換しない。
- `singing`: singing primary、talkでは**歌唱由来の声質参照を主体として**利用できる。歌唱ラベルを発話教師として偽装しないが、ユーザーがトークモデルを選んだ場合にその生成を拒否する理由にはしない。
- `ambiguous`: primaryへ無断投入しない。両用途で確認候補として保持し、他の適格区間で通常フローを継続。ユーザーは必要時だけ変更可能。
- manual override > automatic label。手動変更時は変更後のclassを使用し、provenanceを保持する。
- speaker不明・誤話者・ユーザー除外は共有特徴に混ぜない。
- 重要: `ClassificationConfidence` が未計測であっても、0という理由だけで別人扱いや削除をしない。

## トーク／歌唱間の話者特徴共有

### v1で確実に作る下地

1. 話者ごとに `speech` / `singing` 両方の共通特徴（speaker embedding、F0分布、style/prosody、品質）の参照を集約する。
2. 各値の `source_segment_id`、用途、confidence/status、測定版を保存する。
3. **同一話者である根拠がある**区間のみ統合。ノイズ・BGM強い区間の話者特徴は別途weightを付ける。
4. 利用先AdapterがFeature conditioningを受け入れる場合は、同一speakerの共有特徴を変換して利用する。
5. 利用先が特定の特徴を受け付けない場合、静かに捨てて「相互学習済み」と表示しない。対応外と根拠をUI／ジョブ記録へ残す。

### v1の制約と出口

DiffSinger / Style-Bert-VITS2の指定固定版が直接受け入れない特徴を無理にcheckpointへ注入しない。**歌声変換や疑似音声の自動生成を、未検証のまま本番学習データに追加しない。**

相互利用のv1受け入れ条件は (a) 共有特徴の抽出・保存・来歴管理、(b) 許容されるモデル経路での実入力反映、(c) 実音声での声質類似性の評価。後段のAdapterで活用できない構成なら「未対応」と表示する。v1.0.0で「相互利用ができる」と称する場合は、最低1つの歌唱・トーク双方への実接続を検証する。実接続先が無い場合、対応する追加Issueで提供するまで完了扱いにしない。

## 固定版Backendの共有特徴 受入能力表（v1）

固定版のソースを読んで確認した「外部から条件として入力できるか」の判定。`BackendCapabilities`（コード）とこの表は同じ内容で、テストで一致を確認する。**コードで確認できたものだけを判定する**。この表の「受入」は、固定版のコードが共有特徴を**そのまま入力として受け取れるか**（直接入力）であり、**「相互利用ができない」という結論ではない**。直接入力できない特徴は、実現経路（下の節）で実現する。直接受け入れられない特徴を、無理にcheckpointへ注入しない。

| Backend          | 特徴              | 受入        | 根拠（固定版のソース）                                                 |
| ---------------- | ----------------- | ----------- | ---------------------------------------------------------------------- |
| diffsinger       | speaker_embedding | unsupported | modules/fastspeech/acoustic_encoder.py:60-62,102-108（話者IDの表のみ） |
| diffsinger       | f0_distribution   | unsupported | modules/fastspeech/acoustic_encoder.py:83,110-112（F0はフレーム列）    |
| diffsinger       | style_prosody     | unsupported | modules/fastspeech/acoustic_encoder.py:32-58（フレーム列。要約は不可） |
| style-bert-vits2 | speaker_embedding | unsupported | models_jp_extra.py:1006-1007,1033-1034（話者IDの表。styleは別の特徴）  |
| style-bert-vits2 | f0_distribution   | unsupported | models_jp_extra.py（F0の入力引数なし）                                 |
| style-bert-vits2 | style_prosody     | unsupported | models.py:1063-1067（推論時の係数のみ）                                |

- 調査の対象は DiffSinger v2.5.1（`323a5693245b827caba0ab1a7371fe5ca885b061`）と Style-Bert-VITS2 2.7.0（`d8148f3090ee5038ca7b4e4b327116c64467f952`）。調査の記録先はこの節とする。品質の信頼度を入力として受ける引数は両Backendで確認できなかった。
- 別経路（未実装・評価条件つき）: Style-Bert-VITS2 は推論時に参照音声からスタイルベクトルを作れる（`tts_model.py` の `get_style_vector_from_audio`）。DiffSinger は連続の話者ベクトルを入れるにはコードの改造が必要で、改造は未検証。どちらも実音声での声質類似性の評価（v1の受け入れ条件 (c)）を満たすまで「相互利用できる」と表示しない。
- 共有特徴は `SharedFeatureAdapterRequest`（Backend・revision・用途・話者・共有特徴の参照）で渡す。**音声や教師ラベルは含めない**ため、feature_only の区間を別用途の教師音声として投入できない。Backend・revision・用途・話者が一致しない要求は拒否する。
- 未対応の特徴は `SharedFeatureUsageEntry`（`Used=false` と日本語の理由）としてジョブに記録する。実利用が無い間は「相互学習済み」と表示しない。

## 実現経路（設計）— 直接入力できない共有特徴を、どうやって使うか

方針: **相互利用は「できるか」ではなく「実現する」**。Backendは固定ではなく、学習は単体モデルに限らない。複数工程、別モデルの併用、限定的なパッチ（固定ソースへの再現可能なoverlay）を選べる。最終成果物は、歌唱が OpenUtau で使える DiffSinger 系の声庫、トークが新しい日本語文章を読める TTS として再利用できる形であること。歌声変換や疑似音声を未検証のまま本番学習データに加えない。

調査（Luna による公開資料の調査、固定版のコード読取）の記録: [cross-domain-realization-2026-11-06.md](research/cross-domain-realization-2026-11-06.md)。

| 経路 | 目的             | 方式                                                                                                                                                                                    | 作業と限界                                                                                                                                                    | 状態                     | 担当       |
| ---- | ---------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------ | ---------- |
| T1   | 歌声→トーク      | SBV2 のスタイルベクトルを、歌唱区間の音声から `style_gen` と同じ方式（pyannote の話者埋め込み）で作り、トークモデルの `style_vectors` へ追加する。学習時の発話教師がある場合の声質補強  | 作成した npy の追加は固定版のコードで受け入れられる。歌唱由来のピッチ・伸ばし・残響が混入するかは未評価。AGPL-3.0 の配布条件に注意                            | 設計済み・未実装・未評価 | #100       |
| T2   | 歌声→トーク      | 参照音声から話者を作るゼロショット日本語TTS（Qwen3-TTS: Apache-2.0、Irodori-TTS: コード MIT・重みは条件確認）を Provider として組み込み、歌声の参照で話者条件を作る                     | 発話教師が 0 件でもトークを作れる唯一の経路。出力は SBV2 形式ではなく、runtime とモデル保存の契約が要る。歌声参照の適合性は未評価                             | 設計済み・未実装・未評価 | #100、#138 |
| S1   | 話し声→歌唱      | Learn2Sing 型: 歌唱教師コーパスと対象話者の発話を、共有の話者表現で学習する。DiffSinger の音響モデルに話者エンコーダと投影層を足す限定パッチ（固定ソースへのoverlay）と、追加の学習工程 | 大きな改修。外部の話者埋め込みを入れるには抽出器・投影層・学習処理が要る。歌唱教師コーパスのライセンスと素材量が前提。まず最小の試作で #110 の実GPUで評価する | 設計済み・未実装・未評価 | #99        |
| S2   | 話し声→歌唱      | 歌唱変換（RVC など、コード MIT）で教師歌唱を対象話者の声に変換して歌唱コーパスを増やし、DiffSinger を学習する                                                                           | 変換音声を教師にするため、ピッチ・母音・音域の誤差が学習に入る。変換音声は本番の教師データに混ぜず、別の学習工程として評価してから採用を決める                | 設計済み・未実装・未評価 | #99        |
| S3   | 歌唱が少量の話者 | DiffSinger の話者ID表を拡張して複数話者で学習し、話者混合を使う                                                                                                                         | 既存の仕組みで可能。歌唱が少ないと音域・母音の不足が残る                                                                                                      | 既存機能の利用           | #94        |

- 状態に「未評価」とあるものは、実音声での声質類似性の評価（v1の受け入れ条件 (c)）を満たすまで「相互利用できる」と表示しない。
- 実装の優先順位: T1（最小の追加で効果を確認できる）→ S1 の最小試作 → T2 → S2。評価の結果で順序を変えてよい。
- 経路を実装したら、`BackendCapabilities` の直接入力の判定は変えず、経路ごとの利用記録（`SharedFeatureUsageEntry` の `Used`）で「実際に使った」ことを残す。

## 失敗時の動作

- 全区間ambiguous → 自動学習の素材不足として診断を表示し、ユーザー修正もしくは再解析導線を案内する。データを任意に歌唱／発話へ偽分類しない。
- **一方の素材のみ** → ユーザーが選んだ生成目的を維持する。目的の能力を持つ事前学習基盤と他用途からの声質適応が利用可能なら、トーク←歌唱または歌唱←トークの計画を提示する。固定Backendが必要な声質適応へ非対応なら不足能力を明示し、勝手に別目的のジョブを作らない。
- ユーザーが除外した区間 → 全用途で除外し、キャッシュ・履歴へ変更を反映する。
- 長尺素材に混在 → 再スキャン不要で用途別jobの入力を更新する。

## 必須テスト

1. MC＋歌唱混在の1ファイルでtalk/singing primaryの区間が分かれる。
2. 同じ話者の話し声と歌声が、それぞれ `feature_only` 候補として追跡される。
3. 異なる話者のspeaker embeddingが絶対に統合されない。
4. 手動修正→後段のみ無効化、再解析不要。
5. ambiguousは残し、ユーザー確認なしで教師データに捏造しない。
6. Adapter未対応時は実利用ゼロを記録し、成功と表示しない。

関連Issue: 元の #5、#10、#11、#12、#13。層のルールは `docs/architecture.md`。
