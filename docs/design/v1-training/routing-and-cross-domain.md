# 自動分類・用途別routing・トーク/歌唱の相互活用

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
- `singing`: singing primary、talkはspeaker特徴を補助利用できるが、歌唱ラベルを発話ラベルとして流用しない。
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

## 失敗時の動作

- 全区間ambiguous → 自動学習の素材不足として診断を表示し、ユーザー修正もしくは再解析導線を案内する。データを任意に歌唱／発話へ偽分類しない。
- 一方の素材のみ → ユーザーが選択した当該用途のジョブだけ進める。他用途は「必要な教師データ不足／補助機能未対応」を明示する。
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
