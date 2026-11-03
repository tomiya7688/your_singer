# 複数音声候補・用途別入力選択

## 目的・責務

歌唱素材だけでなく会話・配信・ライブ録音も入力される。**原音を必ずDemucsへ通す**のではなく、原音・軽処理・音源分離・追加整音の候補を同じ時間基準で保存し、後段の目的ごとに使う音声を決める。

現行: `preprocessing.py` → `vocal_separation.py` → `audio_quality.py`、`AudioPreprocessingRecord`。これらへ互換性を保ちながら候補情報を追加する。

## 1. 音声候補の形式

```text
Source (immutable)
  ├── decoded_pcm（常に生成・基準時計）
  ├── raw_conditioned（必要ならDC/clip処理など、可逆性を記録）
  ├── separated_model_A（music/vocal共存時）
  ├── separated_model_B（モデル間照合の必要時）
  ├── denoised（必要時。原音/分離音それぞれの派生にできる）
  └── repaired（学習候補。元観測とは別に保持）
            ↓
     CandidateSelector(task=ASR|speaker|F0|acoustic_training|talk_training)
```

**モデル候補の初期例**: 既存Demucs htdemucs、別系統の音源分離モデル（MDX系・RoFormer系等）、音声向けノイズ抑制器。これは固定採用済み一覧ではない。配布可否・ライセンス・CPU/GPU・計測改善を確認したものだけ実際の選択肢に登録。v1では少なくとも**系統が異なる2方式の候補を生成・比較できる**ことを目標とし、特定製品名を固定要件にしない。

## 2. ワークスペース保存契約（新規・提案）

`metadata/audio-candidates/<source-id>.json`（原本 `sources/` と既存 `separated/` / `cleaned/` の互換名は維持）。

```json
{
  "schema_version": "1",
  "source_id": "src_a",
  "source_sha256": "...",
  "canonical_audio": {"path": "extracted/a.wav", "sample_rate": 44100, "start_sample": 0},
  "candidates": [
    {
      "candidate_id": "raw-0",
      "parent_candidate_id": null,
      "method": "decode",
      "model_id": null,
      "model_revision": null,
      "audio_path": "extracted/a.wav",
      "audio_sha256": "...",
      "sample_rate": 44100,
      "channel_layout": "stereo",
      "time_transform": {"offset_samples": 0, "scale_num": 1, "scale_den": 1},
      "metrics": {},
      "status": "valid"
    },
    {
      "candidate_id": "sep-A",
      "parent_candidate_id": "raw-0",
      "method": "vocal_separation",
      "model_id": "separator-A",
      "model_revision": "locked-sha",
      "audio_path": "separated/a-A.wav",
      "audio_sha256": "...",
      "sample_rate": 44100,
      "channel_layout": "stereo",
      "time_transform": {"offset_samples": 0, "scale_num": 1, "scale_den": 1},
      "metrics": {"music_residual_proxy": null, "vocal_artifact_proxy": null},
      "status": "valid"
    }
  ],
  "task_decisions": {
    "speaker": {"candidate_id": "raw-0", "decision": "supported", "reasons": []},
    "asr": {"candidate_id": "sep-A", "decision": "needs_comparison", "reasons": ["music_present"]},
    "f0": {"candidate_id": "raw-0", "decision": "needs_comparison", "reasons": ["polyphonic_pitch_model"]}
  }
}
```

- `model_id`、`model_revision`、設定hash、親候補hash、開始位置とサンプル比率は必須来歴。バージョン変更時のみ影響候補を再生成。
- サンプルレートやチャンネルを変えた候補は時刻対応を明示し、**1秒の原音位置に戻せる**こと。暗黙のずれ・切り詰めは禁止。
- 候補IDはプロジェクト内一意、保存順・表示順に依存せず `source id + parent hash + method + model version + settings` で決定。既存IDの使い回し禁止。
- 原素材・旧派生音声は消さず、追加候補はキャッシュ可能。成功確認前に既存の学習用候補へ上書きしない。
- 候補破損、空WAV、可聴音の喪失、時刻ずれは `invalid`。無効理由を残す。

## 3. 候補実行ポリシー

1. デコード品質、音楽らしさ、重複話者らしさ、VAD、クリッピング等を**原音から先に評価**する。
2. 音楽が弱い明瞭な発話区間は原音候補を優先する。不要なDemucsをかけて声質を変形させない。
3. 音楽・他話者の混入がある区間は少なくとも原音と分離/抑制候補を比較する。曖昧なら第2系統モデルで候補追加。
4. **音響学習**では残BGM・声の非自然な金属音・残響と話者類似性、**ASR**では認識整合、**speaker**では異なる録音間の話者一致、**F0**では音高軌跡・voicing・オクターブ不一致を評価する。
5. 選択後にも `second_best` / `unresolved` と不一致理由を残す。候補比較不能なら `unknown`。強制で偽の高confidenceを付けない。
6. 時間・GPU制限で全候補を実行できない場合は `budget_limited` と表示し、実行済み候補だけから選んだと記録する。

## 4. `music_leak` の意味を正す

現行 `audio_quality.py` の `separated RMS / raw RMS` は、**分離後に残った音楽の割合を測定していない**。主に残留エネルギーの近似であり、`music_leak` としての信頼性を確かめずに重み付けへ流すのは危険。

新しい `music_residual_proxy` は、伴奏残存を示す分類器の出力、楽器帯域/声以外の構造成分、原音と残差の比較、手動正解付き評価で校正した指標等から作る。**音楽残存と声の加工歪みを別々に保持**する。

- 真の clean vocal reference がない素材では SDR/SIR/SAR を計算済みと表示しない。
- 合成混合（クリーン歌唱 + BGM）ではground truthがあるのでSDR等を評価可能。
- 会話を歌声分離した結果の改善効果も別途測る。
- 「音楽残存が少ない」だけで声質破壊・歌詞の脱落を許容しない。

## 5. スコアと採用根拠

各候補につき `separation_utility(task)` を以下の**ベクトル**として保持（単一scoreへ即時合算しない）。

- `speech_preservation`: VAD/音節・phoneme消失の有無
- `speaker_preservation`: 話者埋め込み・音色の整合性（評価対象の別モデルも使用）
- `background_residual`: 音楽や環境音残存
- `artifacts`: 非自然な音の発生、音割れ、位相・スペクトル異常
- `task_consistency`: ASR/F0/話者推定を再計測した整合
- `resource_cost`: 推論時間/VRAMと利用プロファイル

ここからルールと校正済み指標で用途別選択。スコア統合の係数は「高品質に見える」だけで採用せず、`quality-gates.md` のgold setを使って固定する。

## 6. 誤判定・劣化への対策

- 完全な無音でないのに分離後に音声が消えた → 元音に戻し、候補を `invalid_speech_loss` と記録。
- 元音の方が声質/ASRが良い → 元音を利用。分離実行の事実と採用を分ける。
- 複数分離器が一致しない → 相互の音声を安易に足し合わせない。波形平均は位相・音色劣化の危険。
- 音量だけ高い音声を高品質扱いしない。メタデータの音量正規化・リミッター影響を考慮。
- 入力の一部だけBGM有り → **区間別**に選択可能。source全体へ単一候補を押し付けない。
- 出力の差分評価に同じモデルの判定結果を独立根拠として二重計上しない。

## 7. 回帰テスト

- 明瞭な会話のみ、BGM付き歌唱、MC+歌唱、二人同時発話、強い残響、音割れ、短い歌唱。
- time transform: raw とsepの時刻対応、44.1k↔16k、先頭・終端の境界で1frame以上のずれがないか。
- 元音、sep A、sep Bのファイル/hashが一致せず、同じ設定ならcache再利用。
- 音声が消えた候補は `invalid` で採用されない。原音が残る。
- ボーカル残存エネルギーとBGM残留を別指標にする。
- 音声候補の最適選択は、ground truthのあるデータと実学習の両方でベースライン比較する。
