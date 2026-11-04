# 精度向上 実装パケット (#117〜#130)

> **正本との関係**: 精度向上の設計(README・候補音声・話者/分類/文字起こし・音高と境界・品質ゲート・実装順)は [PR #131](https://github.com/tomiya7688/your_singer/pull/131) の `docs/design/v1-quality/` が正本化中です(未マージ)。本書は、Issueごとの実在確認(既存/新規)と要決定の洗い出しで、設計内容の複製ではありません。#131 が `main` に入った後に、食い違いを突き合わせます。

親Issue #116 の子Issue 14件の実装パケット。設計の入口は #131 の `docs/design/v1-quality/README.md`(未マージ)。本文にない数値・指標・閾値は書かず「要決定」とした。
ファイルの既存/新規は この worktree 内の確認結果。パス接頭辞は `P=src/process/YourSinger.Process/processing`、`W=P/ml/worker/your_singer_ml`、`D=src/data/YourSinger.Data`。

## 依存一覧

- `#129 Q00a <- 記載なし`(設計由来: 最上流)
- `#117 Q00 <- #129`
- `#118 Q01 <- #117`
- `#119 Q02 <- #118`
- `#130 Q03a <- 記載なし`(設計由来: #129。正解付き歌声+BGM素材で比較するため)
- `#120 Q03 <- #119, #117, #130`
- `#121 Q04 <- #120, #117`
- `#122 Q05 <- #118, #117`(設計由来: #121。どの音声候補を入力にするかが未定)
- `#123 Q06 <- #117, #118`
- `#124 Q07 <- #119, #117`(設計由来: #118。候補音声hashを記録するため)
- `#125 Q08 <- #118, #117`(設計由来: #119。分離音声を使うため)
- `#126 Q09 <- #124, #61 / PR #68, #63`
- `#127 Q10 <- #117, #122, #123, #124, #125`(設計由来: #126。MFA未校正0を扱うため)
- `#128 Q11 <- #121, #122, #123, #124, #125, #126, #127, #109, #110`(設計由来: #120。#121経由)

## 同一ファイルを触るIssue

| ファイル                                                     | 状態          | Issue                   | 注意                                                    |
| ------------------------------------------------------------ | ------------- | ----------------------- | ------------------------------------------------------- |
| `W/universal_features.py`                                    | 既存          | #124, #125, #126        | 保存フィールドが衝突しやすい。順序は #124 → #125 → #126 |
| `D/models/AudioPreprocessingRecord.cs`                       | 既存          | #118, #121              | #118 で候補契約を入れてから #121 が拡張                 |
| `W/audio_quality.py`                                         | 既存          | #119, #121              | #121 は判定を分離する方針                               |
| `P/dataset/` の UVD系(`UniversalVoiceDatasetRecord.cs` ほか) | 既存          | #61(PR #68), #126, #127 | PR #68 の統合後に着手                                   |
| `P/dataset/AutoCorrectionService.cs`                         | 既存          | #63, #127               | #63 を先行                                              |
| `docs/design/v1-quality/quality-gates.md`                    | 不明(PR #131) | #117, #128              | 統合状況を確認                                          |
| `tests/fixtures/quality`                                     | 新規          | #129                    | #117 が読み込む                                         |

## Issueごとの実装パケット

### #129 Q00a 正解付き評価データの注釈・分割・来歴を固定する

- 目的: 正解ラベルの基準と gold/validation/holdout 分割を定義・検証する。既存モデルは実行しない。
- 先行: 記載なし(上記の設計由来を参照)。
- 触るもの: `tests/fixtures/quality`(新規)、分割検証スクリプト(新規、置き場は不明)。
- 契約: fixture schema(source_id、session_id、audio hash、話者、speech/singing、文字/読み、F0、境界、注釈可否)と版管理。正解不明は null/unknown。
- 完了条件: 単一sourceの全派生は同一split。異codecや近似重複を同一originと検知。未測定F0を0にしない。schema不正・時刻超過・音声hash変更で失敗。gold集合を再生成可、非公開素材を露出しない。
- テスト: 自動=schema検証、分割検証、近似重複検知、hash変更。実素材評価=非公開実素材の注釈作業(Gitに含めない)。
- 非目標: 既存モデルの実行。
- 要決定: 層(clear speech 等6種)ごとの最低件数と分割比率。数値は本文になし。
- 要決定: 近似重複の検知方式(音声指紋か、メタ情報のみか)。

### #117 Q00 正解付き評価セットと現行単一路線のBaselineを固定する

- 目的: 現行の単一路線のBaselineと評価基盤を固定する。モデル改善はしない。
- 先行: #129。
- 触るもの: `tests/worker/`(既存)、既存GPU検証手順、`quality-gates.md`。評価スクリプトは新規。
- 契約: Baseline manifest(Demucs/自己相関/Faster-Whisper/単一speaker/classifier の revision・設定・失敗率)。評価の入出力JSONとコマンド。状態 `not_run` / `missing`。実行設定hash。
- 完了条件: 正解付き素材のみ CER/DER/F0 を評価し、正解なしは missing。再実行で同じモデル・入力・指標を取得。非公開音声をリポジトリ・CI artifactへ含めない。
- テスト: 自動=timebase、session重複排除、label欠損、モデル欠損、not_run、設定hash。実GPU=Baseline実行(未実行区間を明記)。
- 非目標: モデル改善。
- 要決定: 「失敗率」の定義と評価指標の一覧(失敗のみか、評価不能を含むか)。
- 要決定: 非公開素材の評価をローカルGPUのみで行う前提か。CI上は合成fixtureのみか。

### #118 Q01 元音声/派生音声のCandidate manifest・時間軸・キャッシュを保存する

- 目的: raw/sep/denoised 候補の manifest と時間軸変換を保存する。
- 先行: #117。
- 触るもの: `D/models/AudioPreprocessingRecord.cs`(既存、候補フィールドなし)、`D/repositories/AudioPreprocessingRepository.cs`(既存)、`P/audio/AudioPreprocessingService.cs`(既存)。
- 契約: candidate_id、parent、method、model revision、音声hash、time_transform。manifest は atomic 保存、schema版、stageキャッシュ無効化、旧プロジェクトの読取互換。
- 完了条件: 原音・既存 sep/cleaned の参照を維持。条件違いの候補を衝突させない。不正音声・時刻逆転・領域外pathは理由付き invalid。44.1k→16k や切り出し時刻のずれを検出。
- テスト: 自動=JSON往復、header WAV、時間軸変換、copy/symlink安全性、hash改竄、旧記録再解析。
- 非目標: 新しい分離モデル、自動選択。
- 要決定: time_transform の表現(オフセット+比率か、区間ごとの対応表か)。
- 要決定: candidate_id の生成規則(内容hash由来か、連番か)。

### #119 Q02 前処理を素材条件で分岐しRaw/分離/軽整音の複数候補を生成する

- 目的: 素材条件で分岐し、Raw/分離/軽整音の複数候補を生成する。
- 先行: #118。
- 触るもの: `W/preprocessing.py`、`W/vocal_separation.py`、`W/audio_quality.py`(いずれも既存)。
- 契約: 候補manifest(stage/model hash、時刻対応、例外)。既定候補を既存APIへ投影する。
- 完了条件: 会話のみの素材をDemucs一律加工しない。BGM付き歌唱で複数音声が残る。全候補失敗でも原音と失敗理由を保持。分離で声が消えたら invalid で元音を維持。
- テスト: 自動=clear speech、music speech、live歌唱、zero audio、短clip、モデル未同梱、時刻ずれ。実素材=分岐判定の妥当性(未検証と明記)。
- 非目標: 第2分離モデル(Q03)。
- 要決定: 簡易の音声・BGM・ノイズ診断の判定基準と閾値。

### #130 Q03a 第2音声分離モデルの再配布条件・実行資源・比較候補を固定する

- 目的: 第2分離モデル採用前の技術・権利・配布チェックを固定する。Worker実装は変更しない。
- 先行: 記載なし(上記の設計由来を参照)。
- 触るもの: 資料のみ。
- 契約: 決定する4項目 `model_revision`、`weights_sha256`、`resource_lock`、`license`。記録項目=モデル名、コードrevision、checkpoint hash、配布元、対応OS、必要GPU、推論API。
- 完了条件: 実装担当が迷わず1モデルを同梱・固定できる資料がある。採用不能モデルは理由付き候補扱い。未確認ライセンスを「MIT扱い」で通過させない。
- テスト: 実素材評価=正解付き歌声+BGMでの分離品質・声質保持・速度・VRAMを baseline と比較。Windowsオフライン配布で自動ダウンロード不要の確認。自動テストなし。
- 非目標: Workerへの実装変更。
- 要決定: 成果物(資料)の置き場。
- 要決定: 採用判断の基準値(比較指標の合格線)。本文になし。

### #120 Q03 独立系統の第2分離モデルAdapterを導入し候補を比較可能にする

- 目的: 第2分離モデルの Adapter を導入し、候補を比較可能にする。
- 先行: #119, #117, #130。
- 触るもの: `W/` 配下の新separator adapter(新規)、`tools/build_ml_worker.ps1`(既存)、固定資源lock(新規)。
- 契約: 共通separator契約(source audio、sample rate、output path、hash、time transform、実行status)。status値 `budget_limited` / `failed`。
- 完了条件: 同一素材に異なる2手法の候補が生成される。checkpoint の license・版・hash が再現可能で、エラー時に元音を消さない。ユーザーにモデルPython導入を要求しない。空の分離結果を成功扱いしない。
- テスト: 自動=fake adapter契約、資源不在、GPU不足、時刻合わせ、声の欠落。実GPU=固定実モデルの preflight。
- 非目標: 最良候補の選択、学習品質比較(Q04/Q11)。
- 要決定: 「必要な音楽混入区間だけ追加モデルを起動」の判定主体(Q02の診断か、Q04の判定か)。

### #121 Q04 目的別の音声候補品質判定と選択理由を実装する

- 目的: 用途別に候補の品質を判定し、選択理由を保存する。
- 先行: #120, #117。
- 触るもの: `P/dataset/`(既存)、`W/audio_quality.py`(既存)、`AudioPreprocessingRecord`(既存)。
- 契約: music_leak の意味を整理し、音楽残存と声の歪みを別 score_kind にする。用途(ASR/speaker/F0/音響学習)ごとの指標。比較不能・モデル不一致・測定欠損は `unknown`。選択理由と代替候補IDを保存。採用条件は version 管理。
- 完了条件: RMSだけで残BGMの正解率を主張しない。用途ごとに採用candidateが異なってよい。不適格候補の自動採用を防ぎ、選択理由・失敗理由を可視化できる。
- テスト: 自動=raw勝ち、sep勝ち、音声消失、複数候補不一致、未校正、source別選択、低GPU。実素材=Q00 gold set で選択ルールと退行ケースを確認。
- 非目標: モデル新規学習、重みpolicy。
- 要決定: 採用条件の閾値(本文は version 管理とだけ記載)。
- 要決定: 選択理由の可視化先(記録のみか、UIにも出すか)。

### #122 Q05 話者同定を複数窓・overlap検知・スコア由来区別で強化する

- 目的: 複数窓とoverlap検知で話者同定を強化し、スコアの意味を区別する。
- 先行: #118, #117。
- 触るもの: `W/speaker_analysis.py`、`D/models/SpeakerAnalysisRecord.cs`(いずれも既存)。
- 契約: 複数窓の埋め込み。低情報窓・同時発話はレビュー候補へ。score_kind、calibration_status、cluster margin を保存。第2encoder導入時はモデル別scoreと不一致を残し、無条件平均しない。
- 完了条件: 違う話者の混入率が baseline より悪化しない。同時発話を単一話者の純教師データと確定しない。手動の話者統合・除外が再クラスタで消えない。
- テスト: 自動=二人overlap、長/短話者、talk/singing、別録音、旧schema、同点score。実素材=Q00の話者正解で talk/singing 差と別録音照合を評価。
- 非目標: UI話者選択(既存動線を使う)。
- 要決定: 窓長とoverlap検知の手法。
- 要決定: 第2encoderを本Issueで導入するか、見送るか(導入時のライセンス確認の担当も未定)。

### #123 Q06 歌唱/会話の区間内混在を検出し多特徴で分類候補を照合する

- 目的: 区間内の speech/singing 混在を検出し、多特徴で分類候補を照合する。
- 先行: #117, #118。
- 触るもの: `W/content_classification.py`、`D/models/ContentClassificationRecord.cs`(既存)。
- 契約: 短時間 window の speech/singing/ambiguous 候補。score_kind を保存。MC→歌唱の途中変化、ラップ、息声、短歌唱は mixed/ambiguous。既存の規則ベースは baseline として維持。
- 完了条件: 事前の素材仕分け不要で通常経路が進む。speech⇔singing の誤投入率が gold set で悪化しない。ambiguous は残り、確認必須でない区間で学習続行できる。再分類・user override は後段のみ再処理。
- テスト: 自動=MC+歌唱、短歌唱、ラップ、BGM付きMC、window境界、手動override、model未対応。実素材=gold set の誤投入率。
- 非目標: 話者モデル、トーク/歌唱Trainerの書換え。
- 要決定: 独立系統の音響分類器の候補(ライセンス確認の経路を含む)。
- 要決定: window長と ambiguous 判定の閾値。

### #124 Q07 文字起こしの複数候補と音素整合を保存・選択する

- 目的: ASRの複数候補と音素整合を保存・選択する。
- 先行: #119, #117。
- 触るもの: `W/transcription_alignment.py`、`W/universal_features.py`(既存)、UVD関連record。
- 契約: raw/sep/cleaned ごとの Whisper 候補、候補音声hash、モデル版、score_kind。ASR方式・辞書制約を追加できるインターフェース。日本語の文字正規化・音素化・辞書/歌詞整合と不一致理由。歌詞修正で旧 phoneme/alignment を無効化し、該当区間のみ再解析。
- 完了条件: 同一候補を二重に独立投票として扱わない。歌詞不明でも元音声を消さず他の声質特徴に使える。日本語CER・歌詞誤り率を baseline と比較。avg_logprob 由来の値を正解確率に見せず、確証不足は needs_review。
- テスト: 自動=一致/不一致、同じ誤りに同意、漢字読み、長音、促音、低SNR、ユーザー修正と再解析。実素材=日本語CER。
- 非目標: MFA境界推定(Q09)。
- 要決定: 追加するASR方式(モデル名)。
- 要決定: needs_review に留める基準。

### #125 Q08 歌唱F0に複数推定器を適用しオクターブ誤りを検出する

- 目的: 複数F0推定器でオクターブ誤りを検出する。
- 先行: #118, #117。
- 触るもの: `W/audio_features.py`(既存、自己相関)、`W/universal_features.py`、F0保存record。RMVPE系の資源は repo 内に見当たらない(新規)。
- 契約: raw多声音声と分離音声から別の F0/voicing series を抽出し、元音声 sample 時刻へ整列。score_kind と未解決区間を保存。元F0も保存。補完ONは短い欠損のみ。
- 完了条件: 音楽/分離失敗でも別候補のF0を検査できる。F0の単純平均で偽の中間音高を作らない。基準F0のある歌唱で RPA・オクターブ誤り・voicing を baseline と比較。
- テスト: 自動=オクターブ差、無音、裏声、音域跳躍、BGM付き歌唱、サンプルレート変更、モデル未導入。実素材=基準F0付き歌唱の RPA。
- 非目標: ノート生成、DiffSinger学習、分離モデル本体。
- 要決定: 採用するF0推定器(RMVPE系は第一検証候補。実モデル資源とライセンスは未確認)。
- 要決定: 分離音声を入力にするため、#119 を先行に加えるか。

### #126 Q09 音素境界の候補評価とMFA歌詞不一致・未校正の扱いを正す

- 目的: MFA境界の評価と、歌詞不一致・未校正confidenceの扱いを正す。
- 先行: #124, #61 / PR #68, #63。
- 触るもの: `W/mfa_alignment.py`(既存)、`W/universal_features.py`、音素alignment関連record。
- 契約: 歌詞候補と phoneme列を元音声hashに結び、MFA境界は推定として記録。音素並び一致・時刻単調・時間範囲一致と音声との実一致精度を別 status で保存。confidence 0 の未校正と実測0を区別(`uncalibrated` と `measured`)。歌詞修正後は旧hashを継承せず再アライン。
- 完了条件: MFAの形式的成功だけで歌詞/境界を正しいと判断しない。誤音素・無声化母音・促音・長音を無断置換しない。手動正解付き素材で境界誤差を測り、未計測なら未計測と表示。失敗区間は歌唱duration教師に入れず他の経路を保持。
- テスト: 自動=MFA成功なのに歌詞不一致、未校正0、境界順序と時間違反、古いhash、ユーザー修正、overlap。実素材=手動境界付き素材での誤差。
- 非目標: PR #68 が未統合なら重複した参照record修正を始めない。
- 要決定: PR #68 の統合を待って着手するか、並行するか(並行は重複修正の恐れ)。
- 要決定: 無音(SP)・息(AP)の分類をどのIssueが担うか(#11 のコメントに課題、本文に担当なし)。

### #127 Q10 信頼度の測定状態・校正・用途別評価への引渡しを統一する

- 目的: 信頼度の測定状態・校正・用途別評価への引渡しを統一する。
- 先行: #117, #122, #123, #124, #125。
- 触るもの: `D/models/` のconfidence value/status(既存は `double AsrConfidence`/`AlignmentConfidence`)、`P/dataset/` の TrainingAssessment系(その名の型は不明。v1-training の `TrainingEligibilityService` 等は提案名で、未確認)。
- 契約: raw_score、score_kind、calibration_status、model/source revision、校正データID の共通契約。calibration_status は v1-training の status 語彙(`measured` / `uncalibrated` / `unavailable` / `legacy_unknown` / `invalid`)に合わせる。ECE/Brier/層別校正誤差(ラベルがある場合のみ)。学習weightのQへ calibrated/uncalibrated を渡す。再学習fingerprintを更新。
- 完了条件: 未計測・未校正・測定済0を区別。UIに0〜1のスコアを正解率として表示しない。#80/#83 の schema と矛盾しない。
- テスト: 自動=score_kind異種比較禁止、unknown、測定済0、domain shift、job fingerprint、古いrecord。校正評価=ラベル付き素材(Q00a)。
- 非目標: 学習weightの数式変更(#83)。
- 要決定: 契約フィールドの型と、v1-training の schema との対応(score_kind の位置づけ)。
- 要決定: 校正を行う最低ラベル量。本文になし。

### #128 Q11 複数モデル協調の精度改善を実素材・最終音声まで比較検証する

- 目的: 精度改善を実素材と最終生成音声まで比較検証する。
- 先行: #121, #122, #123, #124, #125, #126, #127, #109, #110。
- 触るもの: `quality-gates.md`、test runner、`docs/implementation-v1-14.md`(既存)、実GPUテスト記録。
- 契約: 記載なし(比較表と release gate)。
- 完了条件: 未検証を改善確定と表示しない。低SNR・歌唱高音・複数話者で重大退行を増やさない。品質・速度・対応GPUの比較表。根拠不足の処理は既定オンにせず旧経路へ戻せる release gate。
- テスト: 実GPU・実素材=DER/JER、分類macroF1とabstain、CER/歌詞誤認、F0 RPA/オクターブ誤り、残BGM/声質劣化の層別評価。学習データ固定でトーク/歌唱の出力を比較。標準/軽量/高品質のVRAM・時間・サイズ。自動=破損、音声消失、失敗時fallback、モデル資源欠損、rollback。
- 非目標: v2 Source Profile駆動Adaptive Training、新規TTS研究。
- 要決定: #109 と #110 の内容(本文に説明なし)。
- 要決定: 「重大退行」の閾値と、最終音声の評価方法(客観指標か聴取か)。

## 要決定一覧

- #129: 層ごとの最低件数と分割比率 / 近似重複の検知方式
- #117: 失敗率の定義と指標一覧 / 非公開素材評価の実行場所
- #118: time_transform の表現 / candidate_id の生成規則
- #119: 簡易診断の判定基準と閾値
- #130: 資料の置き場 / 採用判断の基準値
- #120: 追加モデル起動条件の判定主体
- #121: 採用条件の閾値 / 選択理由の可視化先
- #122: 窓長とoverlap検知の手法 / 第2encoderの導入可否
- #123: 独立系音響分類器の候補 / window長とambiguous閾値
- #124: 追加ASR方式 / needs_review の基準
- #125: F0推定器の採用 / #119 を先行に加えるか
- #126: PR #68 との着手順 / AP/SP分類の担当Issue
- #127: 契約フィールドの型とv1-trainingの対応 / 校正の最低ラベル量
- #128: #109・#110 の内容 / 重大退行の閾値と最終音声の評価方法
