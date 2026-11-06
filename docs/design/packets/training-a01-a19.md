# v1学習 実装パケット（A01〜A19）

> **正本との関係**: Issueの実装順は [implementation-plan.md](../v1-training/implementation-plan.md)([PR #115](https://github.com/tomiya7688/your_singer/pull/115)、マージ済み)、目的・質感・Mod戦略は [PR #135](https://github.com/tomiya7688/your_singer/pull/135)(マージ済み。`goal-texture-data-planner.md` §10)が正本です。本書の「依存一覧」は `implementation-plan.md`(A01〜A35)と §10(#88 の追加条件)に合わせてあり、食い違いはパケットを直します([D-26](../decisions.md))。

実装者がこの1ファイルで着手できるよう、#79 と #80〜#98 を実装パケットにまとめる。契約・式・順序の正本は `docs/design/v1-training/` の4文書（contracts-and-weighting.md、routing-and-cross-domain.md、training-modes-and-backends.md、verification-and-release.md）で、本書は節名で参照するだけで複製しない。
Issue 本文と設計正本が食い違う箇所は黙って採用せず「要決定」に書く。コードの実在は worktree HEAD 340f515（2026-11-02 時点）で確認し、パスの後に「既存」「新規」「不明」を付ける。`implementation-plan.md` は `docs/design/v1-training/` に存在する(2026-11-05 時点の main で確認)。

## 依存一覧

- #79 親 <- #63, #61 / PR #68（既存前提）
- #80 A01 <- #63
- #81 A02 <- #80(#63 は #80 の先行で、直接の依存には置かない)
- #82 A03 <- #81
- #83 A04 <- #82, #63
- #84 A05 <- なし(並行可)。#136 A39 は #84 に依存する側で、逆向きの依存は置かない
- #85 A06 <- #80, #83, #84(#88 は先行に置かない。resolved mode と checkpoint hash は D-07 の `run_fingerprint` で扱う)
- #86 A07 <- #81
- #87 A08 <- #86
- #88 A09 <- #84(implementation-plan)、#132, #133, #134(`goal-texture-data-planner.md` §10。#98 は #133/#134 経由)。#137 は正本のどちらにも無く、依存に置かない
- #89 A10 <- #83, #85
- #90 A11 <- #83, #85, #61 / PR #68
- #91 A12 <- #89, #88
- #92 A13 <- #90, #88, #61 / PR #68
- #93 A14 <- #88
- #94 A15 <- #88
- #95 A16 <- #90, #61 / PR #68
- #96 A17 <- #95, #94
- #97 A18 <- #96, #94
- #98 A19 <- #87

## 同一ファイルを触るIssue

| ファイル/シンボル                                                                                                                                              | 触るIssue                | 衝突の注意                                                                                        |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------ | ------------------------------------------------------------------------------------------------- |
| src/data/YourSinger.Data/models/TrainingJobRecord.cs                                                                                                           | #84, #85, #88            | モード・初期化・manifest参照のフィールド追加が重なる。#84 を先にマージし、他は #84 の型に追従する |
| src/data/YourSinger.Data/repositories/TrainingJobRepository.cs                                                                                                 | #84, #85                 | 旧形式の読込分岐が重なる                                                                          |
| src/process/YourSinger.Process/processing/dataset/TrainingDatasetSnapshotService.cs（CreateFingerprint / CreateJobFingerprint）                                | #81（利用）, #85（改修） | #85 が version 文字列を変えると #81 の入力も変わる                                                |
| src/process/YourSinger.Process/processing/training/TrainingJobPlanningService.cs（AddTargetJob / RequiresRecreation）                                          | #84, #85, #88            | Both 展開（#84）と fingerprint（#85）が同じ関数を触る                                             |
| tests/YourSinger.Process.Tests/TrainingRecreationTests.cs                                                                                                      | #84, #85                 | 同一ファイルへの追記。テスト名の重複に注意                                                        |
| src/process/YourSinger.Process/processing/dataset/UsageDatasetRouter.cs、UniversalDatasetViewBuilder.cs                                                        | #81（利用）, #86（改修） | #86 が既存一覧APIを維持する前提で #81 は view を呼ぶ                                              |
| tests/YourSinger.Process.Tests/SharedSpeakerFeatureTests.cs                                                                                                    | #87, #98                 | #87 が作成、#98 が追記                                                                            |
| docs/design/v1-training/routing-and-cross-domain.md                                                                                                            | #86, #98                 | #98 が能力表を追記。#86 が role 説明を変えると衝突                                                |
| src/process/YourSinger.Process/processing/talk/StyleBertVits2DatasetBuilder.cs、src/data/YourSinger.Data/models/StyleBertVits2ModelRecord.cs                   | #89                      | 単独。#91 は manifest hash を読むだけ                                                             |
| tests/YourSinger.Process.Tests/StyleBertVits2TrainingTests.cs                                                                                                  | #89                      | 単独（#93 のworkerテストは tests/worker 側）                                                      |
| src/process/YourSinger.Process/processing/singing/DiffSingerDatasetBuilder.cs、src/data/YourSinger.Data/models/DiffSingerModelRecord.cs                        | #90, #95                 | #95 が duration 入力のために同じ record を拡張する                                                |
| tests/YourSinger.Process.Tests/DiffSingerAcousticInputTests.cs                                                                                                 | #90, #95                 | 同一ファイルへの追記                                                                              |
| src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/style_bert_vits2_session.py                                                                 | #91, #93                 | prepare() / command() が重なる（sampler と初期化）                                                |
| tools/prepare_style_bert_vits2_bundle.py（固定 train_ms.py への patch）                                                                                        | #91, #93                 | 同一 train_ms.py への patch。overlay を分けるか1本にするか要決定                                  |
| tests/worker/test_style_bert_vits2_training.py                                                                                                                 | #91, #93                 | 同一ファイル                                                                                      |
| src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_training.py                                                                      | #92, #94, #96, #97       | train_diffsinger に機能が集中。#94 を先に入れ #96 / #97 が追従する                                |
| tests/worker/test_diffsinger_training.py                                                                                                                       | #92, #94, #96            | 同一ファイル                                                                                      |
| src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_acoustic_input.py、tools/prepare_diffsinger_source.py                            | #92, #95, #96            | 固定ソース patch の管理場所が重なる                                                               |
| src/process/YourSinger.Process/processing/singing/DiffSingerTrainerService.cs                                                                                  | #94, #96                 | TrainAsync の引数拡張が重なる                                                                     |
| src/process/YourSinger.Process/processing/singing/DiffSingerExportValidator.cs、DiffSingerExporter.cs、tests/YourSinger.Process.Tests/DiffSingerExportTests.cs | #96, #97                 | required / optional の判定を #97 が変更する                                                       |
| src/process/YourSinger.Process/processing/ml/bridge/MlWorkerClient.cs と payload（version付きIPC）                                                             | #93, #94                 | 同じ IPC 契約拡張（training-modes「6」）。片方で共通化する                                        |

## Issueごとのパケット

### #79 親 重み付き学習・複数学習方式・自動振り分け・実用検証の実装計画

- 目的: 子Issue（#80〜#98 ほか）の進行管理。コードは持たない。
- 先行（着手できる条件）: #63 と #61 / PR #68（既存前提）。
- 触るもの:
  - docs/design/v1-training/implementation-plan.md（既存。PR #115 でマージ済み。依存順の正本）
- 契約: なし（契約は4文書と各子Issueが持つ）。
- 完了条件: 各子Issueに目的・対象・入出力・手順・異常系・テスト・非目標が記載される。単体検証と実GPU生成を区別する。生成モデルが対象ソフトで読み込まれ音声を生成する。成功・未検証・失敗を偽らない。
- テスト（自動）: なし（管理Issue）。
- テスト（自動化できない確認）: 親の完了条件にある実GPU生成と対象ソフトでの読込・音声生成は #109〜#111（本書の範囲外）。
- 非目標: OpenUtau 0.1.565 での Pitch/Variance は必須検証に含めない（親本文）。
- 決定済み: 依存順の正本は PR #115 の implementation-plan.md（[決定記録](../decisions.md) D-26）。

### #80 A01 学習用Assessmentの型・保存・旧データ読込を実装する

- 目的: 区間評価（適格性・品質・価値・重み）の型と、安全な保存・読込・旧UVDの取り込みを作る。
- 先行（着手できる条件）: #63。この worktree の src / tests に `uncalibrated` `legacy_unknown` は0件で、#63 の修正は未反映。
- 触るもの:
  - src/data/YourSinger.Data/models/TrainingAssessmentRecord.cs（新規。Issue は「TrainingAssessmentRecord等」とのみ記載）
  - src/data/YourSinger.Data/repositories/ のAssessment用repository（新規。クラス名は Issue になし）
  - src/data/YourSinger.Data/processing/ProjectWorkspace.cs（既存。安全なパス解決APIは見当たらない＝不明）
  - src/process/YourSinger.Process/processing/training/TrainingInputDirectory.cs（既存。一時→公開の既存パターンとして参照）
  - src/data/YourSinger.Data/models/UniversalVoiceDatasetRecord.cs（既存。AsrConfidence / AlignmentConfidence は double、読取のみ）
  - tests/YourSinger.Process.Tests/（新規テスト。ファイル名案 TrainingAssessmentStoreTests.cs）
- 契約: contracts-and-weighting.md「1. 入出力データ契約」の JSON。schema_version "1"、policy_version "training-weight-v1"、job_id、speaker_id、target、source_dataset_fingerprint、items[]（segment_id、eligibility、reason_codes、quality{value,status,components}、utility{value,status,reason_codes}、raw_weight、training_weight）。status は measured / uncalibrated / unavailable / legacy_unknown / invalid。eligibility は eligible / needs_review / excluded。保存先例 training/assessments/<job-id>/assessment.json。snake_case、items は SegmentId 昇順。
- 完了条件: 書込・読込で判定・null・原因コード・順序が維持される。測定済み0.0と未計測 null を区別する。不正スキーマは日本語の原因付きで失敗し、原UVDは変更しない。旧UVDの値だけから measured / uncalibrated を推測せず legacy_unknown で保持する。
- テスト（自動）: TrainingAssessmentStoreTests: RoundTrip_PreservesNullZeroOrderAndReasons / Load_LegacyUvd_YieldsLegacyUnknown / Load_InvalidEnumNanInfinityDuplicateSegmentOrEscapingPath_FailsInJapanese / Save_CanceledOrInterrupted_DoesNotPublishPartial
- テスト（自動化できない確認）: なし（実GPU不要）。
- 非目標: UI、判定アルゴリズム、学習器の変更。
- 要決定: 旧UVDで measured にしてよい「測定元が明示的に特定できる」条件の具体（設計は条件の存在のみ記載）
- 要決定: reason_codes の語彙一覧（設計の例: user_excluded / wrong_speaker / unreadable_audio / no_usable_voice / wrong_content_type / alignment_required / rare_phoneme。これを閉じた列挙にするか自由文字列にするか）
- 要決定: Assessment repository のクラス名と ProjectWorkspace への安全パスAPI追加の要否（追加するか、TrainingInputDirectory の方式を流用するか）

### #81 A02 話者×用途別の区間適格性と除外理由を決定する

- 目的: 話者×用途ごとに各区間を eligible / needs_review / excluded に判定し、主理由と副次理由を付ける。
- 先行（着手できる条件）: #80（型）。#63 は #80 の先行で、直接の依存には置かない。
- 触るもの:
  - src/process/YourSinger.Process/processing/dataset/TrainingEligibilityService.cs（新規。README の提案名）
  - src/process/YourSinger.Process/processing/dataset/TrainingDatasetSnapshotService.cs（既存。LoadAsync / ResolveJobAsync を利用）
  - src/data/YourSinger.Data/repositories/SpeakerAnalysisRepository.cs（既存）
  - src/process/YourSinger.Process/processing/dataset/UniversalDatasetViewBuilder.cs（既存。BuildTalkView / BuildSingingView が現行補正view）
  - tests/YourSinger.Process.Tests/TrainingEligibilityTests.cs（新規）
- 契約: contracts-and-weighting.md「2. 使用適格性」の判定順: ユーザー除外(excluded/user_excluded) → 誤話者確定(excluded/wrong_speaker) → 音声不在・破損・有効区間なし(excluded/unreadable_audio, no_usable_voice) → 用途と内容が不一致(needs_review/wrong_content_type) → 歌唱で検証済み音素境界なし(needs_review/alignment_required) → eligible。出力は #80 の eligibility と reason_codes。
- 完了条件: 全区間に理由コードと主理由が付く。低SNRでも使用可能な音声は eligible。user-excluded は両用途で再採用されない。歌唱の alignment 不足は needs_review で保持し、適格な別区間があれば処理を続行する。用途違いは feature_only 候補として残せるが primary 学習へ入れない。
- テスト（自動）: TrainingEligibilityTests: WrongSpeaker_IsExcluded / UnknownSpeaker_IsNeedsReview / Silence_IsExcluded / BrokenWav_IsExcluded / SingingWithoutAlignment_IsNeedsReviewAndOthersContinue / LowQualityUsable_StaysEligible / AllNeedsReview_ReportsNoUsableSegments / UserExcluded_NeverReadopted
- テスト（自動化できない確認）: なし。
- 非目標: 重み計算（#83）と再学習ジョブ保存（#85）。
- 解消(D-04): `role` と `eligibility` は別の軸。#81 は needs_review/wrong_content_type を返し、`role=feature_only` は #86 が付ける。#80 の record は `role` を保持する。
- 要決定: 「主理由」の保存位置（設計の JSON に primary フィールドがない）。選択肢: reason_codes[0] を主理由とする / primary_reason フィールドを追加
- 要決定: 「音声破損」「有効な有声区間なし」の判定基準（数値・無音割合は未記載）

### #82 A03 希少音素・歌唱音域から用途別Utilityを算出する

- 目的: 話者×用途ごとの音素出現数と歌唱の音域から、区間のUtility（0〜1）を決定的に計算する。
- 先行（着手できる条件）: #81。
- 触るもの:
  - src/process/YourSinger.Process/processing/dataset/TrainingUtilityService.cs（新規）
  - src/process/YourSinger.Process/processing/dataset/JapaneseCoverageCatalog.cs（既存。語彙を再利用）
  - src/process/YourSinger.Process/processing/dataset/SingingQualityDiagnosticService.cs（既存。F0特徴を再利用）
  - src/data/YourSinger.Data/models/UniversalVoiceDatasetRecord.cs（既存。Phonemes / F0FeaturePath / PitchReliability / MeanF0Hz を読取）
  - tests/YourSinger.Process.Tests/TrainingUtilityTests.cs（新規）
- 契約: contracts-and-weighting.md「U算出」。r(p)=1/sqrt(max(1,n(p)))。発話Uは区間内の確実に観測した音素の最大 r(p)。歌唱は音素希少度と音域bin希少度の平均で、片方のみなら片方、両方なしは0.5。bin = floor(12*log2(f0Hz/55)/6)（6半音幅・基準55Hz）。頻度0のbinは作らない。観測根拠がなければ 0.5 と status を残す。SegmentId 昇順で決定的、他話者の頻度を混ぜない。出力は #80 の utility{value,status,reason_codes}。
- 完了条件: 同一入力から同一Utility。希少な実測発音・音域は頻出より高い。未観測・不正・推定の音素や無効F0でUtilityが増えない。
- テスト（自動）: TrainingUtilityTests: RarePhoneme_ScoresHigherThanFrequent / SingingRangeBoundary_Bin / SpeakersAreSeparated / EmptyPhonemes_IsNeutralWithStatus / InvalidF0_ZeroNegativeNaN_NoRangeBin / OrderIndependent
- テスト（自動化できない確認）: なし。
- 非目標: training_weight の計算（#83）。
- ~~要決定: 音域bin希少度の式（設計は「音素希少度と平均」とだけ記載。bin出現数 n(bin) に r=1/sqrt(max(1,n)) を同様に使うか、区間内のどのF0フレームでbinを決めるか）~~ → 暫定(#82): 音域bin希少度は音素と同じ r=1/sqrt(max(1,n(bin))) とし、全F0フレームでbinを決め、区間内の最大希少度を使う。n(bin) も n(p) も「そのbin/音素を含む、話者×用途のeligible区間の数」(フレーム数ではない)
- ~~要決定: 「確実に観測した音素」の判定フィールド（Phonemes[].Confidence の閾値、ユーザー確定ラベルの識別、推定音素 PhonemeSupplement の除外方法）~~ → 暫定(#82): 「確実に観測した音素」は、AlignmentMethod が placeholder でなく、AlignmentConfidenceStatus が unavailable/invalid でなく、語彙(Core/Palatalized/Foreign)内で、時刻が有限かつ end>start の音素。歌唱は HasValidSingingTrainingAlignment も必須。Confidence の数値閾値は置かない。PhonemeSupplement(推定)は入力に含めない。ユーザー確定ラベルの識別は未対応
- ~~要決定: 測定不能時の utility.status の値（5値のうち unavailable でよいか）~~ → 暫定(#82): 測定不能時は 0.5 と status=unavailable でよい

### #83 A04 品質とUtilityから決定的な区間Weightを計算する

- 目的: Q と U から training_weight を決定的に計算する（式は設計で固定済み）。
- 先行（着手できる条件）: #82、#63。
- 触るもの:
  - src/process/YourSinger.Process/processing/dataset/TrainingWeightPolicy.cs（新規）
  - #80 の assessment への raw_weight / training_weight 反映（新規フィールドの利用）
  - tests/YourSinger.Process.Tests/TrainingWeightPolicyTests.cs（新規）
- 契約: contracts-and-weighting.md「3. v1の決定的な重み計算」。Q は measured 項目だけの算術平均、全項目が未計測なら評価上 Q=0.5。quality_factor=0.35+0.65Q、utility_factor=1.0+0.75U、raw=積。training_weight=clamp(raw/mean_raw, 0.25, 2.0)。excluded と needs_review は 0。eligible 0件はエラー。policy_version "training-weight-v1"。audio品質は AudioPreprocessingRecord.Quality（元ファイル粒度）を区間指標と再ラベルしない。
- 完了条件: 同じUなら高品質ほど高weight。低品質で希少な eligible も正の weight。unknown と measured=0 を区別し、入力順を変えても重みが変わらない。非有限値は拒否する。
- テスト（自動）: TrainingWeightPolicyTests: Q1U0_Raw1_0 / Q0U1_Raw0_6125_Positive / AllUnknown_UsesNeutralButKeepsStatus / ZeroEligible_Throws / Shuffle_SameWeights / NonFinite_Rejected / AutoCorrectionOnOff_SeparateInputs
- テスト（自動化できない確認）: なし。
- 非目標: Trainer への反映（#91 / #92）。
- 解消(D-05): 全項目が未計測のときは設計正本の `unavailable` を使う。Issue本文の `unknown` は `unavailable` と読み替える。
- 要決定: audio品質の 0〜1 化（noise / music_leak / reverb / clipping の向きと変換式。設計は「AudioQualityAnalyzer とテストに合わせて確定」）→ 未着手(#83): 重み計算は計測状態付きの指標を受け取るだけで、audio品質の変換は行わない。変換は別途（要決定のまま）
- ~~要決定: clamp 後に平均が 1 でなくなることを許容するか（設計は clamp のみ記載。選択肢: 許容 / clamp 後に再正規化）~~ → 暫定(#83): 許容する(設計どおり clamp のみ。再正規化しない)。unavailable の Utility は Value=null で保存し、評価上の0.5は重み計算内だけで使う(#82 の返り値を null に修正)

### #84 A05 出力目的・目標質感と学習モードのジョブ契約を保存する

- 目的: 出力目的（talk/singing）・目標質感・学習モード（requested / resolved）・初期化情報をジョブへ保存する。
- 先行（着手できる条件）: なし（Issue 明記）。
- 触るもの:
  - src/data/YourSinger.Data/models/TrainingJobRecord.cs（既存。SpeakerTrainingSelection / TrainingJobRecord / TrainingJobBatch）
  - src/data/YourSinger.Data/repositories/TrainingJobRepository.cs（既存）
  - VoiceModelGoal、TrainingMode enum、stage 初期化record（新規。src 内に該当名は0件）
  - tests/YourSinger.Process.Tests/TrainingRecreationTests.cs（既存。拡張）
- 契約: training-modes-and-backends.md「2. ジョブ保存契約」の JSON。requested_training_mode（auto / fine_tune / from_scratch）、resolved_training_mode、backend、backend_revision、model_profile、initialization{type: pretrained_checkpoint | random_initialization, components[{name, sha256, path}]}、assessment_manifest、training_config、dataset_fingerprint。Issue 追加: VoiceModelGoal（talk/singing）、requested_texture（preset・説明・同一話者参考segment ID）、checkpoint hash、seed。scratch 時 components は空。
- 完了条件: 同素材で talk+歌唱風texture と singing+自然texture を別Jobで保持できる。texture変更でJob fingerprint が変わり前段解析は変わらない。3モードを保存・再読込できる。旧historyを新規学習と偽装しない。FromScratch でベース話者重み指定は事前拒否。
- テスト（自動）: TrainingRecreationTests に追加: Serialization_RoundTripsModes / LegacyHistory_NotPassedOffAsNew / InvalidEnum_Rejected / Both_ExpandsWithIndependentTextureAndMode / Recreate_KeepsSettings / FromScratchWithBaseCheckpoint_Rejected
- テスト（自動化できない確認）: なし。
- 非目標: 実Trainer の起動。
- 解消(implementation-plan A05、`goal-texture-data-planner.md` §10): #84 は先行なしで並行可、#136 が #84 に依存する。したがって #136 を先行にせず、preset は文字列IDで保存し、語彙の検証は #136 側に置く。
- 解消(D-01): `requested_training_mode` は1ジョブの初期化方式。複数工程の計画は別に `TrainingStagePlan` で持ち、各工程が `resolved_training_mode` を持つ。
- ~~要決定: VoiceModelGoal と既存 TrainingTarget(Talk/Singing/Both, Flags) の関係。選択肢: 併存 / TrainingTarget を置換~~ → 暫定(#84): VoiceModelGoal の型は新設しない。ジョブの `Target`(Talk/Singing の1つ)が出力目的であり、選択の Both は用途ごとの2ジョブに展開する。用途ごとの質感・モード・シードは `SpeakerTrainingSelection.TalkOptions/SingingOptions` で独立に指定する
- #84 A05: 明示モード(fine_tune/from_scratch)は作成時に resolved=requested と初期化情報を保存し、auto は解決(preflight)まで resolved=null・initialization=null のまま保存する。質感の既定は preset `natural`(語彙の検証は #136)。backend / backend_revision / model_profile / assessment_manifest / training_config は #85・#88 で追加

### #85 A06 Weight/学習方式を含む学習入力Fingerprintと旧ジョブ再作成を接続する

- 目的: Assessment・重み・解決済みモード・checkpoint hash・seed を fingerprint に含め、旧ジョブは再作成に誘導する。
- 先行（着手できる条件）: #80、#83、#84。
- 触るもの:
  - src/process/YourSinger.Process/processing/dataset/TrainingDatasetSnapshotService.cs（既存。CreateFingerprint の version "training-snapshot-3"、CreateJobFingerprint の "training-job-input-2"）
  - src/process/YourSinger.Process/processing/training/TrainingJobPlanningService.cs（既存。RequiresRecreation / CreateBatchAsync / RecreateBatchAsync / AddTargetJob）
  - tests/YourSinger.Process.Tests/TrainingRecreationTests.cs（既存）、TrainingGuardrailTests.cs（既存。CreateJobFingerprint を使用）
- 契約: contracts-and-weighting.md「5. 再計算・指紋・キャッシュ」の項目: 観測データsnapshot hash、参加区間と用途別 eligibility・reason・quality status、区間ごとの weight と policy version、学習方式・backend revision・checkpoint hash（新規学習は未使用と明記）、seed / trainer設定。fingerprint の version を上げ、旧形式は RequiresRecreation。確定後 snapshot を凍結。
- 完了条件: weight や mode の変更で fingerprint が変わる。同じ snapshot なら再現可能。旧jobは閲覧・再作成でき、黙って別入力を使わない。元音声・UVDを上書きしない。
- テスト（自動）: TrainingRecreationTests に追加: Fingerprint_Fixed / Fingerprint_ChangesOnWeightOrMode / UserOverrides_Reflected / AutoCorrectionOnOff_Differs / OldHistory_RequiresRecreation / Canceled_NoPartialSave / SaveFailure_KeepsOld
- テスト（自動化できない確認）: なし。
- 非目標: UI、実Trainerへのweight適用。
- 解消(D-07、implementation-plan A06): #88 は #85 の先行に置かない。#85 は `input_fingerprint` を実装し、resolved mode と初期化資源のhashを含む `run_fingerprint` は #88 の Resolve 後に確定する。
- 解消(D-07): `input_fingerprint` は目的/質感を含む。
- ~~要決定: 新 fingerprint の version 名と、旧形式の判別根拠（TrainingJobBatch.InputVersion か fingerprint 文字列か）~~ → 暫定(#85): 新 fingerprint の version は `training-input-v1`。旧形式の判別は `TrainingJobBatch.InputVersion`（#84 で `v1-09.5` に更新済み）で行い、fingerprint 文字列からは判別しない
- 実装済み(#85 結線): `TrainingJobPlanningService` がジョブ作成時に評価を組み立てて `training/assessments/<job-id>/assessment.json` に保存し、ジョブへ `input_fingerprint` / `backend` / `backend_revision` を記録する。暫定の決定: 音声の可読性は作業領域内のファイル存在だけで判定する(wavの中身の破損検出は未実装)。歌唱のF0は区間のF0特徴ファイル(時刻付き配列)から読み、読めなければ音域は測れないものとする。backend と revision は設計書の固定値(`TrainingBackendCatalog`)。eligible が0件のジョブは作成を拒否し、失敗・取消では保存済みのAssessmentを削除する。入力版は `v1-09.6`

### #86 A07 会話/歌唱/曖昧区間を自動で用途別に振り分けるRoutingDecisionを作る

- 目的: speaker×segment×target で primary / feature_only / review / excluded を決める。
- 先行（着手できる条件）: #81。
- 触るもの:
  - src/process/YourSinger.Process/processing/dataset/UsageDatasetRouter.cs（既存。Build(ContentClassificationRecord) → UsageDatasetSelection）
  - src/process/YourSinger.Process/processing/dataset/UniversalDatasetViewBuilder.cs（既存。BuildTalkView / BuildSingingView）
  - src/data/YourSinger.Data/models/ContentClassificationRecord.cs（既存。SegmentContentClassification / ContentClassificationOverride / UsageDatasetSelection）
  - src/process/YourSinger.Process/processing/dataset/ContentClassificationOverrideService.cs（既存）
  - UsageRoutingDecision（新規・提案名）
  - tests/YourSinger.Process.Tests/UsageDatasetRouterTests.cs（新規）
- 契約: routing-and-cross-domain.md「提案するrouting契約」の JSON: segment_id、speaker_id、talk{role,status,reason_codes}、singing{...}、classification_origin、classification_confidence{value,status}。role は primary / feature_only / review / excluded。speech は talk primary、singing は singing primary、ambiguous は primary に入れず review。手動 override が最優先で原ラベルと理由を保存。既存の TalkSegmentIds / SingingSegmentIds / AmbiguousSegmentIds API を壊さない。
- 完了条件: 会話・歌唱混在フォルダで事前仕分けなしに両用途へ配分される。ambiguous が誤タスクの教師データにならない。手動修正後の再解析は後段のみ。
- テスト（自動）: UsageDatasetRouterTests: McPlusSinging_SplitsPrimary / AllAmbiguous_IsReview / ManualOverride_Wins / MixedSpeakers_Separated / UserExcluded_Reflected / ExistingSelectionApi_Unchanged
- テスト（自動化できない確認）: なし。
- 非目標: 分類モデル自体の全面刷新。目的決定とTrainer選択（分類器は行わない）。
- 解消(D-04): 2つは別の軸で、対応表は持たない。`role=feature_only` の区間は教師データの採否(eligibility)の対象にしない。
- ~~要決定: classification_confidence の status 語彙（既存 Confidence は double で未計測の区別がない。設計の status 5値を使うか）~~ → 暫定(#86): 設計の status 語彙（measured / legacy_unknown / unavailable / invalid）を使う。自動分類の信頼度は 0 を legacy_unknown（値は null）、0 超 1 以下を measured、非有限・範囲外を invalid とし、手動修正は信頼度が無いので unavailable。0 を理由に別人扱いや削除はしない
- ~~要決定: RoutingDecision の保存先とファイル名（設計・Issue とも未記載）。および feature_only 量を #132〜#134 へ渡す型~~ → 暫定(#86): RoutingDecision は保存せず、分類と話者から都度導出する派生データとする（`UsageRoutingService.Route`）。feature_only の量は `UsageRoutingResult.Count(target, role)` で渡す。保存が必要になった時点で保存先を決める。routing 専用の理由コード（speaker_reference / ambiguous_content / user_excluded / speaker_unconfirmed / classification_missing）は評価の reason_codes とは別の語彙

### #87 A08 同一話者のトーク・歌唱特徴参照を来歴付きで統合する

- 目的: 同一話者の speech / singing の共通特徴を source_segment_id 付きで集約し、非対応は unsupported と報告できるようにする。
- 先行（着手できる条件）: #86。
- 触るもの:
  - src/process/YourSinger.Process/processing/dataset/SharedSpeakerFeatureService.cs（新規）
  - src/data/YourSinger.Data/models/UniversalVoiceDatasetRecord.cs（既存。SpeakerEmbedding / StyleProsody / MeanF0Hz / F0StdDevHz / PitchReliability を読取）
  - tests/YourSinger.Process.Tests/SharedSpeakerFeatureTests.cs（新規）
- 契約: routing-and-cross-domain.md「トーク／歌唱間の話者特徴共有」。話者ごとに speech / singing 両方の speaker embedding・F0分布・style/prosody・品質を参照集約。各値に source_segment_id、用途、confidence/status、測定版。同一話者の根拠がある区間のみ。観測されない特徴を補完値と偽らない。
- 完了条件: 同一話者の両用途参照を検証できる。別話者混入・原音声改変・無根拠の声質混合がない。共有特徴をトレースできる。
- テスト（自動）: SharedSpeakerFeatureTests: SpeechOnly / SingingOnly / Mixed / WrongSpeaker_NotMerged / UserExcluded_NotMerged / NoEmbedding_ReportsUnavailable
- テスト（自動化できない確認）: なし。
- 非目標: Trainer への実 conditioning（後続Issue）。
- ~~要決定: 重み付き集約の重み（品質・confidence のどれをどう使うか。設計は「別途weightを付ける」のみ）~~ → 暫定(#87): 重みは使わず、測定できた区間の等重み平均にする(Q の v1 初期実装と同じ方針)。F0 の標準偏差は区間内分散と区間平均の分散を合成。品質・信頼度による重み付けはノイズ・BGM 区間の扱いが決まってから。対象は routing が対象話者のその用途の primary とした区間だけで、別話者・話者不明・除外・確認待ちは混ぜず、使わなかった区間と理由を `Skipped` に残す。保存はせず都度導出（`SharedSpeakerFeatureService`）
- 解消(D-08): 「対応外を報告する契約」は #98 に一本化する。#87 は区間側の共有特徴の参照と来歴だけを保存し、対応可否は判断しない。

### #88 A09 目的・質感・素材量に応じた学習計画の確定とPreflight

- 目的: Plannerの複数段階案をbackend能力・checkpoint・GPUで検証し、不変の計画manifestに確定する。
- 先行（着手できる条件）: #84、#132、#133、#134（implementation-plan と §10）。Issue本文は #137 TrainingStrategyRegistry も前提にするが、正本に無いため依存としない(要決定を参照)。#98 は #133/#134 経由。
- 触るもの:
  - src/process/YourSinger.Process/processing/training/TrainingModeResolver.cs（新規。src 内に0件）
  - 既存ML worker bundle / version 情報（具体ファイルは不明）
  - tests/YourSinger.Process.Tests/TrainingModeResolverTests.cs（新規）
- 契約: training-modes-and-backends.md「3. プレフライト」。提案API Resolve(job, datasetStats, hardwareInfo, backendCapabilities, resourceLock) の順序: target と backend の互換確認 → supported_modes と初期重み要件 → データ量・カバレッジ・GPU/VRAM診断 → auto の解決 → fine_tune の checkpoint 存在・形式・hash・版互換 → from_scratch の初期化可否 → 不変manifest保存 → worker の実読込一覧と照合。Issue 追加: 計画manifest に目標texture、inventory hash、stageごとの手法・初期化・参照音声・Backend資源、Strategy ID/version、Provider、根拠、代替候補、未対応理由、全stageのmodel/checkpoint hash。
- 完了条件: 目標・質感・選択計画・各stageのmodeと初期化方針が一致する。unsupported 構成は学習開始前に停止し、日本語で理由を示す。fine_tune はcheckpoint欠損・hash不一致・backend非互換で必ず失敗。目的を talk↔singing に勝手に変えない。プラグイン消失時に過去Jobを無言で再解決しない。
- テスト（自動）: TrainingModeResolverTests: SingingOnly50minTalkGoal_PlansSingingLikeTalk / TalkRichSingingGoal / BothTargets / SameMaterialDifferentTexture / BackendUnsupported_Stops / CheckpointMissing_Fails / HashMismatch_Fails / AutoDecisionFixedInJob / GpuShortage / EmptyDataset / UnsupportedMode / GoalNeverAutoChanged。実GPU確認は #109 / #110。
- テスト（自動化できない確認）: GPU/VRAM 不足判定の実機確認は自動化できない（#109 / #110）。
- 非目標: 外部Trainerの実行、UI追加。
- 解消(D-01): `auto` は段階的な候補計画を比較して解決し、各工程が自分の `resolved_training_mode` を持つ。checkpointの有無だけでは決めない。
- 要決定: GPU/VRAM の警告・停止閾値（設計・Issue とも数値なし）
- 解消(implementation-plan A09、§10): 依存は #84、#132、#133、#134。#98 は #133/#134 経由で先行する。#137 は正本のどちらにも無いので依存に加えない。ただしIssue本文は #137 を前提にしており、Issue本文との食い違いは未修正(Issue側の確認が必要)。
- 実装済み(#88 の解決部): `TrainingModeResolver.Resolve`(Talk/Singing プランナーの候補を順に検証し、最初に実行できる候補を確定する。Trainer 工程ごとに fine_tune / from_scratch / Auto を解決し、初期重みの欠損・hash 不一致・版の非互換・CUDA 無し・VRAM 不足・教師データ空を学習開始前に日本語の理由つきで停止する。target と質感は変更せず、候補の target が違えば失敗。SBV2 の固定版は事前学習重み必須のため from_scratch を宣言せず、DiffSinger は両方を宣言(実GPUでは未検証)。計画内容から manifest hash を計算する)。テスト: `TrainingModeResolverTests`。実装済み(Preflight): `TrainingPreflightService.PreflightAsync`(Pending のジョブの目的・質感・モードから用途別プランナーと `TrainingModeResolver` で解決し、`TrainingJobRecord.ResolvedPlan`(manifest hash・棚卸し版・各工程のモードと初期化・警告・停止理由)として保存する。解決できなくても停止理由つきで保存)と `RequireExecutable`(未解決・実行不可・棚卸しの版違いを学習前に止める)。テスト: `TrainingPreflightServiceTests`。実装済み(Trainer の入口): `StyleBertVits2TrainerService.TrainAsync` と `DiffSingerTrainerService.TrainAsync` は起動前に `TrainingPreflightService.RequireResolvedPlan` を呼び、未解決・実行不可・Backend またはモードの不一致のジョブを起動しない(棚卸し版の照合は呼び出し側が `RequireExecutable` で行う)。未実装: UI/実行フローから Preflight と RequireExecutable を呼ぶ接続(現状、Trainer を呼ぶ UI/オーケストレーションは存在しない)、worker の run manifest との照合、必要 VRAM の実測、GPU・初期重みの自動検出。

### #89 A10 Style-Bert-VITS2 Dataset Builderへ区間Weight manifestを出力する

- 目的: esd.list は変えずに、segment_id / audio_filename / training_weight を別JSONへ出力する。
- 先行（着手できる条件）: #83、#85。
- 触るもの:
  - src/process/YourSinger.Process/processing/talk/StyleBertVits2DatasetBuilder.cs（既存。BuildAsync、出力先 training/style-bert-vits2/<job-id>/）
  - src/data/YourSinger.Data/models/StyleBertVits2ModelRecord.cs（既存。StyleBertVits2DatasetRecord / StyleBertVits2TrainingItem）
  - tests/YourSinger.Process.Tests/StyleBertVits2TrainingTests.cs（既存）
- 契約: esd.list は公式4列 `<audio>|<speaker>|JP|<text>` のまま（implementation-v1-11.md）。別JSONに item ごとの segment_id / audio_filename / training_weight。設計は train_weights.json と assessment.json に言及。stable sort、training/ 配下に原子的公開、manifest SHA-256 を dataset record に記録して worker へ渡す。欠番・重複・範囲外 weight（0.25〜2.0）を拒否。
- 完了条件: 公式esd.listが従来通り読める。全学習itemとweightが1対1で欠落がない。manifestの存在・hashを検証できる。
- テスト（自動）: StyleBertVits2TrainingTests に追加: Manifest_SixSegmentWeights / MissingSegment_Rejected / Duplicate_Rejected / PathTraversal_Rejected / OldJob_RequiresRecreation / Canceled_NoPublish / EsdListUnchanged
- テスト（自動化できない確認）: なし。
- 非目標: Trainer 本体の weighted sampler（#91）。
- ~~要決定: manifest のファイル名とスキーマ（設計に train_weights.json の名があるが Issue は未指定）。train_weights.json を採用するか~~ → 暫定(#89): `train_weights.json` を採用。スキーマは `schema_version / job_id / policy_version / input_fingerprint / items[{segment_id, audio_filename, training_weight}] / skipped[{segment_id, eligibility, reason_codes}]`。dataset.json に `weight_manifest_file` と `weight_manifest_sha256` を記録する
- ~~要決定: esd.list に載らない needs_review / excluded を manifest に含めるか（含めない場合の検証方法）~~ → 暫定(#89): 学習に使うのは評価で eligible の区間だけで、esd.list と items の音声が1対1であることを検証する。確認待ち・除外は esd.list にも items にも入れず、理由付きで `skipped` に残す(重みは持たない)。これにより、従来すべてを esd.list に入れていた needs_review / excluded の区間は学習から外れる。
- 暫定(#89 / #81 の修正): 有声率が 0 の区間を「有効な有声区間なし」で除外するのは、有声特徴ファイルがあるとき(有声判定を実測したとき)だけ。補完生成区間などファイルが無い 0 は未測定として除外しない

### #90 A11 DiffSinger Dataset Builderへ区間Weight manifestを出力する

- 目的: 区間Weight manifest を別ファイルに出力し、binarization 後も segment_id と元音声hashを関連付ける。
- 先行（着手できる条件）: #83、#85、#61 / PR #68。
- 触るもの:
  - src/process/YourSinger.Process/processing/singing/DiffSingerDatasetBuilder.cs（既存。BuildAsync）
  - src/process/YourSinger.Process/processing/singing/DiffSingerAcousticInputService.cs（既存）
  - src/data/YourSinger.Data/models/DiffSingerModelRecord.cs（既存。DiffSingerDatasetRecord / DiffSingerTrainingItem）
  - tests/YourSinger.Process.Tests/DiffSingerAcousticInputTests.cs（既存）
- 契約: segment_id / audio / weight を1対1で別manifestに保存。alignment 未計測区間は needs_review に残して学習へ流さず素材は消さない。現行MFAの厳密照合（RequiredSingingTrainingAlignmentMethod = mfa-japanese-phone-forced-alignment-v1）を維持。既存 DiffSinger acoustic 入力形式を壊さない。
- 完了条件: 既存acoustic入力形式を壊さない。weight と binarized item を突き合わせられる。PR #68 未統合時は対象領域を競合変更しない。不正align・hash不整合・重複・無効weightは理由付きで失敗。
- テスト（自動）: DiffSingerAcousticInputTests に追加: NonZeroStartClip / MfaSuccessAndFailure / IndexMatch / HashMismatch_Fails / LowQualityRareRange_PositiveWeight / UncalibratedScore
- テスト（自動化できない確認）: なし。
- 非目標: DiffSingerのsampler改造（#92）と音響学習の完走。
- 要決定: manifest のファイル名とスキーマ（Issue 未記載）。#89 と共通の形式にするか
- 解消(D-12): PR #68(#61) が未統合の間は実装に着手しない。契約の設計までは進めてよい。

### #91 A12 SBV2公式Trainerで重み付きサンプリングを実動作させる

- 目的: 固定公式 train_ms.py に seeded weight sampler を限定適用し、使用証跡を run manifest に返す。
- 先行（着手できる条件）: #89、#88。
- 触るもの:
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/style_bert_vits2_session.py（既存。StyleBertVits2TrainingSession.prepare / command）
  - tools/prepare_style_bert_vits2_bundle.py（既存。patch / overlay 機構は未確認＝不明）
  - tests/worker/test_style_bert_vits2_training.py（既存）
- 契約: training-modes-and-backends.md「4. トーク」: 重みは train.list の実ファイル名に完全結合し、見つからない・重複・不一致は学習開始不可。適用方法は単一（初期候補 seeded weighted sampler）。sampling と loss の併用禁止。val は対象外。run_manifest.json に件数・最小最大・manifest hash・seed・sampler version。patch内容・hash・ライセンス表示を再現可能に固定。
- 完了条件: 全itemがweightと1対1。weight逆転でサンプリング頻度が期待方向に変わる。無効・欠落を等重みで成功扱いしない。DDP/未対応GPU構成は理由付きで失敗。
- テスト（自動）: test_style_bert_vits2_training.py: seeded 2:8 の頻度比統計 / ddp_unsupported_preflight / train_val_split / tampered_manifest_rejected / patch_hash_matches。実GPU確認は #109。
- テスト（自動化できない確認）: 実GPUでの重み反映確認は自動化できない（#109）。
- 非目標: 初期化mode（#93）とGUI。
- 要決定: 2/8 weight比の統計テストの許容誤差・試行回数（Issue 未記載）
- 要決定: sampler patch を #93 の fallback 検出 patch と同じ overlay に入れるか分けるか（同一 train_ms.py を触る）

### #92 A13 DiffSinger公式Trainerで重み付きサンプリングを実動作させる

- 目的: binarized train item へ segment_id で weight を結合し、固定版 dataset sampler に seeded 適用する。
- 先行（着手できる条件）: #90、#88、#61 / PR #68。
- 触るもの:
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_acoustic_input.py（既存）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_training.py（既存。train_diffsinger）
  - tools/prepare_diffsinger_source.py（既存。patch 管理の有無は不明）
  - tests/worker/test_diffsinger_training.py（既存）
- 契約: binarized item の順序が違っても segment_id から結合。参照不能・重複は即エラー。sampler は固定revisionからの patch として記録し、元 val 入力は変更しない。件数・分布・manifest hash を実行結果に記録（run_manifest.json）。
- 完了条件: item順序が違っても正しいweightに結合される。低品質で希少な eligible 区間もサンプルに参加する。重み変更で実samplingが変わる（ログだけの見かけ実装でない）。
- テスト（自動）: test_diffsinger_training.py: binarization_index_mismatch / weight_swap_changes_selection_rate / val_not_weighted / epoch_seed_reproducible / uncalibrated_mfa / rerun
- テスト（自動化できない確認）: 実GPUでの重み反映確認は自動化できない（#110）。
- 非目標: 新規・追加学習モードとサブモデル生成。
- 要決定: DiffSinger v2.5.1 の sampler 差し込み箇所（クラス名はIssue 未記載。固定 commit 323a569 のコード確認が必要）
- 解消(D-12): #90 と同じ。PR #68(#61) 未統合の間は実装に着手しない。

### #93 A14 Style-Bert-VITS2で明示的なFine-tune/新規学習を切り替える

- 目的: resolved_training_mode を worker に渡し、暗黙の scratch fallback を失敗として扱う。
- 先行（着手できる条件）: #88。
- 触るもの:
  - src/process/YourSinger.Process/processing/talk/StyleBertVits2TrainerService.cs（既存。TrainAsync）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/style_bert_vits2_session.py（既存。prepare が G_0 / D_0 / DUR_0 を必須としている）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/style_bert_vits2_training.py（既存。train_style_bert_vits2）
  - tests/worker/test_style_bert_vits2_training.py（既存）
  - tools/prepare_style_bert_vits2_bundle.py（既存。固定 train_ms.py への patch が #91 と衝突し得る）
- 契約: training-modes-and-backends.md「4」「6」。version付きIPC に resolved_training_mode / initialization_manifest / job_id / input_fingerprint / training_weights_path / seed / backend_revision を追加。fine_tune は G_0 / D_0 / DUR_0 の hash を事前検証し読込を報告。from_scratch は話者モデル初期重みを読まない（共有テキストEncoder等は定義に沿い可）。実行レポート不一致は失敗。output hash を記録。
- 完了条件: 明示Fine-tuneは欠損checkpointで無言scratchへ移行しない。明示ScratchでG_0等を読んだら失敗。どちらの経路でも実Trainer契約が成立。
- テスト（自動）: test_style_bert_vits2_training.py: fine_tune_missing_checkpoint_fails / scratch_loaded_g0_fails / report_mismatch_fails / fake_trainer_initialization_report / official_trainer_preflight。実GPUでの学習は #109（Issue の A27 は本範囲外）。
- テスト（自動化できない確認）: 実GPUでの学習完走は自動化できない（#109）。
- 非目標: 学習結果の品質判定。
- 要決定: from_scratch で prepare() の G_0 / D_0 / DUR_0 必須チェックをどう緩めるか。および「共有テキストEncoder等」に含める資源の定義（設計は「定義に沿って」とのみ記載で定義の所在なし）
- 解消(D-01): Workerは1回の呼出しで1工程を受け取り、工程ごとの結果は `stage-runs/<stage-id>` に別記録する。

### #94 A15 DiffSinger音響モデルで明示的なFine-tune/新規学習を切り替える

- 目的: acoustic の初期化方式を明示指定し、実読込を報告させ、fine_tune 失敗を scratch に落とさない。
- 先行（着手できる条件）: #88。
- 触るもの:
  - src/process/YourSinger.Process/processing/singing/DiffSingerTrainerService.cs（既存。TrainAsync、--reset 実行）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_training.py（既存）
  - DiffSinger固定config生成のProcess/worker module（具体名は不明）
  - tests/worker/test_diffsinger_training.py（既存）
- 契約: resolved_training_mode・initialization manifest・acoustic checkpoint hash を worker へ。固定 DiffSinger v2.5.1 の hparams / loader で初期checkpoint指定位置を検証して report 保存。fine_tune は互換・hash一致・読込成功のときのみ続行。from_scratch は base checkpoint 参照を全て無効化。既存の --reset、固定runtime、job別出力、path安全性を維持（implementation-v1-10.md）。
- 完了条件: 同じ入力でmode別にTrainerを起動できる。fine_tune失敗がscratchへフォールバックしない。実際のacoustic checkpointまでの経路が記録される。
- テスト（自動）: test_diffsinger_training.py: config_typo / base_checkpoint_missing / hash_mismatch / mode_report_mismatch / output_overwrite_rejected / official_entry_compat。実GPU確認は #110。
- テスト（自動化できない確認）: 実GPUでの学習は自動化できない（#110）。
- 非目標: duration / pitch / variance とOpenUtau実レンダリング。
- 要決定: config 生成 module の特定（Issue は「該当Process/worker module」と記載）
- 要決定: acoustic 以外のサブモデルの初期化は #96 / #97 が持つ。resolved mode を#94 で共通フィールドにするかサブモデル別にするか

### #95 A16 歌唱のDuration学習用入力をMFA測定値から生成する

- 目的: 検証済みMFAの音素区間から DiffSinger duration binarizer 用の入力を作る。
- 先行（着手できる条件）: #90、#61 / PR #68。
- 触るもの:
  - src/process/YourSinger.Process/processing/singing/ 配下の DurationDatasetBuilder（新規・提案名）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_acoustic_input.py（既存。DiffSinger input module として拡張する想定）
  - tests/YourSinger.Process.Tests/DiffSingerAcousticInputTests.cs（既存）
- 契約: 検証済みMFAの音素開始・終了・音素列・clip hash から duration 入力を作る。先頭無音・後端無音・未対応音素・音素長不正を区別し、推測値を実測として通さない。job fingerprint・segment_id・weight を結合。公式 DiffSinger v2.5.1 duration binarizer の要件に合わせ、schema と source lock（diffsinger-source.lock.json）を記録。
- 完了条件: aligned clip から公式duration入力が生成できる。未対応・偽alignのclipは原因付きで保留され破棄されない。元音声を変えず来歴を追跡できる。
- テスト（自動）: DiffSingerAcousticInputTests に追加: NonZeroPhonemeStart / OverlappingBoundaries / EndOverrun / UnsupportedLongVowelOrGeminate / ClipHashChanged / EmptyCandidates
- テスト（自動化できない確認）: 公式 binarizer の実行確認は自動化できない場合がある（#110）。
- 非目標: duration trainer 起動（#96）とOpenUtau出力構築。
- 要決定: 長音・促音など MFA が対応できない音素の扱い（implementation-v1-10.md は「未対応表現は実測成功にしない」。保留理由コードを何と呼ぶか）
- 要決定: duration 入力 schema のファイル名と形式（Issue 未記載。公式 binarizer 要件の確認が前提）

### #96 A17 DiffSinger Durationモデルを学習しOpenUtau用成果物へ接続する

- 目的: duration の公式Trainerを同梱runtimeから起動し、成果物をExporterとValidatorで検証して配置する。
- 先行（着手できる条件）: #95、#94。
- 触るもの:
  - src/process/YourSinger.Process/processing/singing/ の DurationTrainer 連携（新規。DiffSingerTrainerService との関係は不明）
  - src/process/YourSinger.Process/processing/singing/DiffSingerExporter.cs / DiffSingerExportValidator.cs（既存）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_training.py（既存）
  - tests/worker/test_diffsinger_training.py、tests/YourSinger.Process.Tests/DiffSingerExportTests.cs（既存）
- 契約: 固定DiffSinger v2.5.1 duration Trainer 入口と config を固定。acoustic とは別に新規／fine_tune 初期化・ログ・checkpoint を管理。duration / linguistic submodel の設定・ファイル・hash を Exporter で検証して配置。OpenUtau 0.1.565 の dsdur/dsconfig.yaml との対応（必須: dsdur/dsconfig.yaml・phonemes.txt・dsdict.yaml・linguistic.onnx・dur.onnx。implementation-v1-10.md）。
- 完了条件: duration用公式Trainerを同梱runtimeから起動できる。出力不足・不正形式・誤った初期化modeを成功扱いしない。OpenUtau向けduration構成を構築できる。
- テスト（自動）: test_diffsinger_training.py: train_command_contract / checkpoint_not_generated / config_broken / mode_initialization_report。DiffSingerExportTests: DurationLinguisticPaths / MissingDurFails
- テスト（自動化できない確認）: OpenUtau 0.1.565 での読込・レンダリングは自動化できない（#110 / #111）。
- 非目標: Pitch / Variance と音声全体のOpenUtauレンダリング。
- 要決定: Trainer 起動入口の具体（scripts/train.py の config 名など。Issue は「具体的に固定する」ことを手順に挙げ、値は未記載）
- 要決定: #92（重み付きsampler）を duration 学習にも適用するか（Issue に記載なし）

### #97 A18 DiffSinger Pitch/Variance関連モデルを学習・出力し歌唱構成を揃える

- 目的: Pitch / Variance の required / optional を表で固定し、必要なものの学習入力・初期化・出力を揃える。
- 先行（着手できる条件）: #96、#94。
- 触るもの:
  - src/process/YourSinger.Process/processing/singing/ の Pitch / Variance dataset・export 連携（新規）
  - src/process/YourSinger.Process/processing/ml/worker/your_singer_ml/diffsinger_training.py（既存）
  - src/process/YourSinger.Process/processing/singing/DiffSingerExportValidator.cs（既存。dspitch / dsvariance は存在時のみ検証）
  - tests/YourSinger.Process.Tests/DiffSingerExportTests.cs（既存）
- 契約: required / optional と理由を表で固定。実測F0・音素・ノート相当情報から学習入力（推測と実測を区別）。各モデルの fine_tune / scratch 経路。Vocoder を独自学習しない場合は固定Vocoderの版・hash・ライセンス・配置を記録。OpenUtau は dspitch/dsvariance を各ディレクトリの dsconfig.yaml で判定する（implementation-v1-10.md）。
- 完了条件: Validator が必要サブモデルの有無を判定する。欠損をプレースホルダで埋めて成功と表示しない。未対応のPitch/Varianceはその項目の互換性を未完成と明示する。
- テスト（自動）: DiffSingerExportTests: MissingSubmodel / InvalidF0 / EmptyNotes / ConfigVersionMismatch / MissingVocoder
- テスト（自動化できない確認）: OpenUtau実機試験は自動化できない（後続 #110 / #111）。
- 非目標: 最終OpenUtau実機音声試験。
- 解消(implementation-plan Phase 3 Gate と A25、D-06 暫定): pitch/variance は任意で、A18 を必須の検証と混同しない。未生成は「未提供」と表示し、エラーにしない。「音響だけで完全なOpenUtau声庫と判定しない」は維持する。
- 要決定: 固定 Vocoder の具体（版・hash・ライセンスは本Issueの成果物として記録する形だが候補名は Issue になし）

### #98 A19 トーク／歌唱の共有声質特徴を利用できるBackend能力表を固定する

- 目的: DiffSinger と SBV2 が受け入れ可能な共有特徴を yes/no で固定し、Adapter 契約と unsupported 表示を定める。
- 先行（着手できる条件）: #87。#88 の直接の依存には置かない(#133/#134 経由。`goal-texture-data-planner.md` §10)。
- 触るもの:
  - docs/design/v1-training/routing-and-cross-domain.md（既存。能力表を追記）
  - src/process/YourSinger.Process/processing/training/ の BackendCapabilities（新規・提案名。src 内に0件）
  - tests/YourSinger.Process.Tests/SharedSpeakerFeatureTests.cs（#87 で新規作成）
- 契約: 固定した DiffSinger・SBV2 の入力契約を調査し、speaker embedding / F0分布 / prosody 等の受入をyes/noで整理。feature_only を primary 教師音声に誤投入しない Adapter 契約（型・version・入力条件）。直接conditioning不可なら別経路と評価条件。未対応は unsupported としてジョブ記録とUIに出す形にする。
- 完了条件: 両Backendの能力と非対応が資料で明示される。受入可能と判定した特徴はテスト可能な入力契約を持つ。未対応を相互学習成功と偽らない。
- テスト（自動）: SharedSpeakerFeatureTests に追加: CapabilityTable_MatchesDocument / VoiceIdMismatch_Rejected / TargetMismatch_Rejected / Unsupported_ShownInJobRecord
- テスト（自動化できない確認）: なし（調査と契約の固定）。
- 非目標: 実際のfeature conditioning と学習反映。
- 解消(implementation-plan A09、§10): #88 の直接の依存には加えない。#98 は #133/#134 の先行で、#88 はそれらに依存する。
- ~~要決定: yes/no 判定の根拠となる固定commit（SBV2 d8148f3 / DiffSinger 323a569）のコード調査の記録先（docs の節か別文書か）~~ → 暫定(#98): `routing-and-cross-domain.md` の「固定版Backendの共有特徴 受入能力表（v1）」の節に、根拠のファイルと行番号を記録する。判定は、コードで確認できた範囲だけ。v1では直接conditioningできる共有特徴は無く、全て unsupported。別経路は未実装・評価条件つきとして記録

## 要決定一覧

- #79 親: （決定済み D-26: 依存順の正本は PR #115 の implementation-plan.md）
- #80 A01: 旧UVDで measured にしてよい「測定元が明示的に特定できる」条件の具体（設計は条件の存在のみ記載）
- #80 A01: reason_codes の語彙一覧（設計の例: user_excluded / wrong_speaker / unreadable_audio / no_usable_voice / wrong_content_type / alignment_required / rare_phoneme。これを閉じた列挙にするか自由文字列にするか）
- #80 A01: Assessment repository のクラス名と ProjectWorkspace への安全パスAPI追加の要否（追加するか、TrainingInputDirectory の方式を流用するか）
- ~~#81 A02: 用途不一致の扱い: 設計は needs_review/wrong_content_type、Issue は feature_only 候補として残す。feature_only は #86 の role であり #80 の eligibility 値ではない。選択肢: #81 は needs_review のみ返し role は #86 が付ける / #81 が role も返す~~ → (解消 D-04。上の節を参照)
- ~~#81 A02: 「主理由」の保存位置（設計の JSON に primary フィールドがない）。選択肢: reason_codes[0] を主理由とする / primary_reason フィールドを追加~~ → 実装済み(#81): `reason_codes[0]` を主理由とし、以降を副次理由とする。フィールドは増やさない
- ~~#81 A02: 「音声破損」「有効な有声区間なし」の判定基準（数値・無音割合は未記載）~~ → 暫定(#81): 音声が読めない(呼び出し側の判定)ときは unreadable_audio、有声率が非有限または0以下のときは no_usable_voice。数値の閾値は置かず、低SNR・低品質は理由にしない。閾値の導入は D-02/D-03 の確定後
- #81 A02: 話者未確定(SpeakerId 空)の区間は語彙 `speaker_unconfirmed` の needs_review とした(設計の語彙に追加。docs/data-model.md に反映済み)
- #82 A03: 音域bin希少度の式（設計は「音素希少度と平均」とだけ記載。bin出現数 n(bin) に r=1/sqrt(max(1,n)) を同様に使うか、区間内のどのF0フレームでbinを決めるか）
- #82 A03: 「確実に観測した音素」の判定フィールド（Phonemes[].Confidence の閾値、ユーザー確定ラベルの識別、推定音素 PhonemeSupplement の除外方法）
- #82 A03: 測定不能時の utility.status の値（5値のうち unavailable でよいか）
- ~~#83 A04: 全項目未計測時の quality.status: Issue は「unknown のまま」、設計は `unavailable`（status 5値に unknown はない）。どちらを正とするか~~ → (解消 D-05。上の節を参照)
- #83 A04: audio品質の 0〜1 化（noise / music_leak / reverb / clipping の向きと変換式。設計は「AudioQualityAnalyzer とテストに合わせて確定」）
- #83 A04: clamp 後に平均が 1 でなくなることを許容するか（設計は clamp のみ記載。選択肢: 許容 / clamp 後に再正規化）
- ~~#84 A05: texture preset の語彙と型（#136 A39 が定義するが #84 の依存行にない。選択肢: #136 を先行依存にする / 文字列IDだけ保存し検証を #136 以降に回す）~~ → (解消 implementation-plan A05。上の節を参照)
- ~~#84 A05: Auto の表現: 設計は auto を fine_tune か from_scratch の二択に実行前確定、Issue は複数stage計画を解決。stage record を持つか単一 resolved_training_mode か（#88 と共通の決定）~~ → (解消 D-01。上の節を参照)
- #84 A05: VoiceModelGoal と既存 TrainingTarget(Talk/Singing/Both, Flags) の関係。選択肢: 併存 / TrainingTarget を置換
- ~~#85 A06: resolved mode と checkpoint hash は #88 が確定するが #85 の依存に #88 がない。選択肢: ジョブ作成時は requested のみで fingerprint を作り resolve 後に再計算 / #88 を #85 の先行にする~~ → (解消 D-07。上の節を参照)
- ~~#85 A06: 目標texture（#84）を fingerprint に含める点は #84 に明記され #85 には記載なし。含めると確定してよいか~~ → (解消 D-07。上の節を参照)
- #85 A06: 新 fingerprint の version 名と、旧形式の判別根拠（TrainingJobBatch.InputVersion か fingerprint 文字列か）
- ~~#86 A07: routing の role と assessment の eligibility（#80/#81）の対応表（例: feature_only はどの eligibility か）~~ → (解消 D-04。上の節を参照)
- #86 A07: classification_confidence の status 語彙（既存 Confidence は double で未計測の区別がない。設計の status 5値を使うか）
- #86 A07: RoutingDecision の保存先とファイル名（設計・Issue とも未記載）。および feature_only 量を #132〜#134 へ渡す型
- #87 A08: 重み付き集約の重み（品質・confidence のどれをどう使うか。設計は「別途weightを付ける」のみ）
- ~~#87 A08: unsupported 報告の型を #98 の BackendCapabilities と一本化するか分けるか~~ → (解消 D-08。上の節を参照)
- ~~#88 A09: auto の意味: 設計は「同梱checkpointがあれば原則 fine_tune、なければ from_scratch の成否」の二択（checkpoint優先）、Issue は「checkpointの有無だけでは決めず素材量で複数stage計画」。どちらを正とするか~~ → (解消 D-01。上の節を参照)
- #88 A09: GPU/VRAM の警告・停止閾値（設計・Issue とも数値なし）
- ~~#88 A09: 依存行に #137（本文）と #98（設計のResolve引数 backendCapabilities）が無い。依存に加えるか~~ → (解消 implementation-plan A09 と §10。上の節を参照)
- #89 A10: manifest のファイル名とスキーマ（設計に train_weights.json の名があるが Issue は未指定）。train_weights.json を採用するか
- #89 A10: esd.list に載らない needs_review / excluded を manifest に含めるか（含めない場合の検証方法）
- #90 A11: manifest のファイル名とスキーマ（Issue 未記載）。#89 と共通の形式にするか
- ~~#90 A11: PR #68 が未マージの間の進め方（待つ／別ブランチで先行しマージ時に調整）~~ → (解消 D-12。上の節を参照)
- #91 A12: 2/8 weight比の統計テストの許容誤差・試行回数（Issue 未記載）
- #91 A12: sampler patch を #93 の fallback 検出 patch と同じ overlay に入れるか分けるか（同一 train_ms.py を触る）
- #92 A13: DiffSinger v2.5.1 の sampler 差し込み箇所（クラス名はIssue 未記載。固定 commit 323a569 のコード確認が必要）
- ~~#92 A13: PR #68 未マージ時の進め方（#90 と共通）~~ → (解消 D-12。上の節を参照)
- #93 A14: from_scratch で prepare() の G_0 / D_0 / DUR_0 必須チェックをどう緩めるか。および「共有テキストEncoder等」に含める資源の定義（設計は「定義に沿って」とのみ記載で定義の所在なし）
- ~~#93 A14: Auto の複数stage計画（#88）に対し worker が単一 resolved_training_mode を受ける設計の整合（stage ごとに別 worker 呼出しにするか）~~ → (解消 D-01。上の節を参照)
- #94 A15: config 生成 module の特定（Issue は「該当Process/worker module」と記載）
- #94 A15: acoustic 以外のサブモデルの初期化は #96 / #97 が持つ。resolved mode を#94 で共通フィールドにするかサブモデル別にするか
- #95 A16: 長音・促音など MFA が対応できない音素の扱い（implementation-v1-10.md は「未対応表現は実測成功にしない」。保留理由コードを何と呼ぶか）
- #95 A16: duration 入力 schema のファイル名と形式（Issue 未記載。公式 binarizer 要件の確認が前提）
- #96 A17: Trainer 起動入口の具体（scripts/train.py の config 名など。Issue は「具体的に固定する」ことを手順に挙げ、値は未記載）
- #96 A17: #92（重み付きsampler）を duration 学習にも適用するか（Issue に記載なし）
- ~~#97 A18: Pitch / Variance の位置づけ: implementation-v1-10.md と親 #79 は任意（dsdur・dsvocoder が必須）、training-modes-and-backends.md は「音響だけでduration/pitch未生成は完全歌唱モデルと呼ばない」。pitch を必須扱いにするか。選択肢: 任意のまま未生成なら『互換性未完成』と表示 / 必須に引き上げ~~ → (解消 implementation-plan Phase 3 Gate と D-06。上の節を参照)
- #97 A18: 固定 Vocoder の具体（版・hash・ライセンスは本Issueの成果物として記録する形だが候補名は Issue になし）
- ~~#98 A19: BackendCapabilities を #88 の Resolve 引数として使うなら #88 の依存に加えるか（#98 は #87 のみに依存）~~ → (解消 implementation-plan A09 と §10。上の節を参照)
- #98 A19: yes/no 判定の根拠となる固定commit（SBV2 d8148f3 / DiffSinger 323a569）のコード調査の記録先（docs の節か別文書か）
