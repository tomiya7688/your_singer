# DiffSinger v2.5.1で複数の質感を実現する詳細設計（v1必須）

## 0. 結論と根拠

**「DiffSingerに『プリセット』という機能が直接存在しない」ことと「Your Singerの質感プリセットをDiffSingerで実現できない」ことは別。** Your Singerは**(A)プリセット別の学習レシピ**を選び、さらに**(B)DiffSingerの学習済み条件付き特徴・補助モデル・OpenUtauでの表現制御**を使い、必要なら異なるAcoustic checkpoint / 声庫を生成する。

ただしDiffSingerで**無条件に好きな音色へ変化できる**わけではない。本人の収録条件、必要な実測教師、trained embeddingとInference/Exporterの実装が必要。利用できない機能を「対応済み」と表示しない。組込プリセットを正式対応とするには、**実際に使える学習経路と出力・実音声の検証**を完成させる。

**固定対象**

- OpenVPI DiffSinger `323a5693245b827caba0ab1a7371fe5ca885b061`（v2.5.1相当）
- OpenUtau `0.1.565` を既存互換基準とする
- Your Singerの`BuiltinSingingStrategies`、`SingingTrainingStrategyPlanner`、`DiffSingerDatasetBuilder`、`DiffSingerBinarizeService`、`DiffSingerTrainerService`、`DiffSingerExporter`を**現在の実装上の接続対象**とする
- 上記バージョンで実際に宣言・参照されるキーに限定。新しいDiffSinger/MakeDiffSingerの機能を勝手に固定版へ転用しない

### 固定ソースで確認した根拠

- [固定版Acoustic config](https://github.com/openvpi/DiffSinger/blob/323a5693245b827caba0ab1a7371fe5ca885b061/configs/acoustic.yaml): `use_breathiness_embed` / `use_voicing_embed` / `use_tension_embed` / `use_energy_embed` / `use_key_shift_embed` / `use_speed_embed` / `use_spk_id` は設定できるが**既定false**。`augmentation_args` はpitch/time変換の定義を持ち、固定設定では既定false。shallow diffusionやfine-tune/freezing設定も存在。
- [固定版Variance config](https://github.com/openvpi/DiffSinger/blob/323a5693245b827caba0ab1a7371fe5ca885b061/configs/variance.yaml): `predict_dur` / `predict_pitch` / `predict_breathiness` / `predict_voicing` / `predict_tension` 等を独立に設定できる。
- [固定版Best Practices](https://github.com/openvpi/DiffSinger/blob/323a5693245b827caba0ab1a7371fe5ca885b061/docs/BestPractices.md): Variance組合せの注意、DurationとPitch/Varianceの学習分離、Vocoder適応、音高変換/時間伸長、freeze、shallow diffusion等。
- [OpenUtau公式のDiffSinger表現仕様](https://www.openutau.com/DiffSinger-support/): PITD/DYNに加えBREC/TENC/VOIC/GENC/VELC/PEXP/voice color等。ただし多くは**声庫の対応が前提**。

ここでいう「現在使える」とは**固定公式ソースに選択肢がある**意味であり、Your SingerのWorkerと出力済み声庫が対応済みという意味ではない。

## 1. 機能分類とプリセットへの対応

| 表現軸・能力 | 固定版の設定/出力 | v1で必要な実装条件 | 適する質感の例 |
|---|---|---|---|
| 音高曲線 | Acoustic入力F0、Variance `predict_pitch`、OpenUtau PITD/PEXP | pitchの教師/Note入力、対応モデル、dspitchを必要時に生成 | クラシックの安定音高 / 自然・高表現の揺れ |
| 音素長・歌い回し | `predict_dur`、Durationモデル、VELCはvoicebank依存 | 正しいphoneme alignment、Duration/linguistic出力、VELC対応の設定 | クリア、クラシック、自然 |
| Breathiness | Acoustic `use_breathiness_embed` + Variance `predict_breathiness` | harmonic/noise分離と測定品質、学習済みAcoustic条件、必要時dsvarianceとOpenUtau BREC | 柔らかい息・高表現 |
| Voicing | Acoustic `use_voicing_embed` + Variance `predict_voicing` | 声の有声成分を測定し、条件付きモデル/予測を実際に学習 | 力強い・自然・息声 |
| Tension | Acoustic `use_tension_embed` + Variance `predict_tension` | harmonic featuresと品質、声質が不自然に変わる副作用の検証 | 力強い・高表現 |
| Energy | Acoustic `use_energy_embed` + Variance `predict_energy` | Voicing/Breathinessとの相関に注意し別レシピで検証 | 一部の強弱表現 |
| Formant/Key shift | `augmentation_args.random_pitch_shifting` / `use_key_shift_embed`、OpenUtau GENCは声庫依存 | 実augmentを使ったtrain itemとweightの整合、推論用embeddingが実際にexportされること | 個性的・キャラ系 |
| Velocity | `random_time_stretching` / `use_speed_embed`、OpenUtau VELCは声庫依存 | 同上。音素長の教師精度を傷めないこと | 明瞭・速い/遅い歌唱 |
| Multi-speaker / mode | `use_spk_id`、`num_spk`、speaker IDやvoice color | 同一話者の複数発声モードを**別labelで区別**し、声色の差を実測確認。別人を混入させない | 高表現・キャラ系 |
| Vocoder | `vocoder_ckpt` / NSF-HiFiGAN等 | checkpointの互換/ライセンス、参照音の音色保持、対応OpenUtau読み込み | すべて |
| Acoustic variant | `finetune_enabled`/checkpoint/optimizer、モデル構成 | preset別にTrainer入力を固定し、別artifact/checkpointと推論結果を保存 | すべて、特に違いが大きい質感 |

### 技術的禁止事項

- **Energy、Breathiness、Voicingをすべて無検証で同時有効化しない。** 固定版Best Practicesでは強く推奨されない組合せ。既定は`energy=false`、対応する場合 `breathiness/voicing/tension`を比較対象にする。
- Tension/Voicingは音量・鼻声・本人性との相関がある。数値を増やせば必ず「力強い声になる」と断言しない。
- `use_breathiness_embed=true` を設定するだけでは、教師の特徴列・学習済モデル・OpenUtau BRECは手に入らない。
- `predict_pitch=true` には音符/楽譜入力の教師条件が必要。**歌唱動画から推定したF0を、そのまま正解のノート列と偽装しない**。
- 公式のmulti-speakerはそのまま「任意の同一話者の複雑な声質モード」を実装したことではない。mode-label・出力voice colorの受け入れは別の検証が必要。
- F0曲線をむやみに平滑化するとビブラート/滑音が消える。クラシック系の質感は**推論時の制御曲線と学習レシピの違い**で実現し、間違った教師F0を大量に作らない。
- 別presetのcheckpointを単純に平均/融合しない。構造・辞書・メル/出力形式が異なるものを重み平均してはいけない。

## 2. プリセット別に生成するDiffSingerレシピ

### D-A: `classic_synthetic` — クラシック合成系

- 目的: **規則性・発音と音符への忠実さ・意図した電子的な音色**。音割れ・破綻・低品質を「レトロ」とみなさない。
- 入力: phoneme/Duration/F0の正確な観測を優先。低品質でも希少な音素・音域は正のweightで活用。教師音声そのものへ大量のF0平滑化を施さない。
- Recipe: 対応するAcoustic fine-tune/scratch、Durationを確保し、Pitchモデルは独立候補として評価。Breathiness/Tensionなどの表現モデルを**デフォルトで必須にしない**。再現可能な`classic`声庫variantを別成果物として持つことを許可。
- 推論: 手動PITDや指定したF0/時間長カーブを優先し、不要な自動表現変動を抑える設定を試行。**音の合成感は単なるF0固定だけでは実現保証しない**。必要なら音響variantの学習・表現制御で再現する。
- 検証: 意図した合成質感と、音素の欠落/出力音声の破綻を分離して評価。

### D-B: `natural` / `clean_controlled` — 自然・明瞭系

- 入力: 言語発音や音色が保全された音声を中心に、話者の声質のぶれ・雑音を別に記録。歌唱特有の自然な抑揚とピッチを残す。
- Recipe: Acoustic+Durationを基盤とし、`natural`はPitchモデルや対応するVoicing等を比較、`clean_controlled`は明瞭さ・音高/時間長の正確さを強調。自然さのために発音の正確さを犠牲にしない。
- 推論: OpenUtauのPITD/DYNに対応し、追加表現を有効にするのは学習・exportが検証済みの場合だけ。
- 検証: 本人類似度/自然度/音素・音高・時間長/破綻を独立して評価。

### D-C: `expressive_complex` — 高表現系

- 入力: 同一話者の多様な歌唱から、声質・息/有声/張り・F0の変動を観測できた部分を使う。**多様な発声がない素材から多モードの教師ラベルを捏造しない**。
- Recipe: Acoustic `use_breathiness_embed=true`, `use_voicing_embed=true`, `use_tension_embed=true` を**能力候補**とし、必要な特徴を正しくbinarizeして学習。Tension同時使用で声質が劣化する場合は外し、実験結果を残す。
- Pitch/Variance: 実際のNote教師があるならPitch predictor、対応する音響特徴を得られるならBreathiness/Voicing/Tensionを予測。VarianceのDurationとPitch/他varianceは**段階的に分けて**比較する。
- 同一話者の複数modeが識別できる場合のみmulti-mode/multi-voice-colorを試す。無い場合は連続curve対応と別Acoustic variantを比較する。
- 検証: 同じ歌詞/音符を**少なくとも2設定**でレンダリングし、聞き分けられる表現差と本人性・音素保持を確認。コントロール無効で同じWAVなら「高表現成功」としない。

### D-D: `soft_breathy` / `powerful`

- `soft_breathy`: 同一人物の柔らかい歌唱の出現・音色を基礎に、Breathiness/Voicing系に必要なharmonic/noiseを検証してから条件付け。単に小音量化/帯域カットしたWAVを「息声学習」と呼ばない。
- `powerful`: 張りある同一人物の教師を選び、Voicing/Tensionの条件を能力/実測に応じたRecipeへ。無検証の固定高Tensionは不自然な鼻声/音割れに繋がりうるため禁止。
- **どちらも**特徴が無い/導入できない場合は、Acoustic variantとしてのデータ選択・学習stageでどこまで実現できるかを比較し、達成できないtraitを黙って「済」にしない。

### D-E: `characterized` / `reference_match`

- 同じ話者の録音モード/参考音源に合わせたデータ選択・条件付け。本人が複数の声色を使っている場合は明示labelと分割学習。
- 参考素材が1件しかないときは表現全体への一般化を保証しない。クロスファイル・未学習音素を含む生成結果を検証。
- 別人の声や、Voice Conversionで得た本人性の低い音声を参照として無検証に入れない。

## 3. 固定ソースへのConfig Overlay変換契約

`DiffSingerTextureRecipeCompiler`（提案）をProcessの歌唱処理に設け、指定Preset/Strategy/データProfileとRuntimeの能力から**公式の固定Acoustic/Variance設定に対する検証済みoverlay**を生成する。

現在の `diffsinger_binarize.py.write_training_config()` は、固定`configs/acoustic.yaml`をbaseとし、辞書・データ・validation・vocoderと、**augmentation無効**だけを書いている。これを上書きせず、Stageの出力先で**別`recipe-acoustic.yaml`/ `recipe-variance.yaml`**を作成し、完全に解決された設定で学習する。

**例: 高表現型のAcoustic候補（必ず前提を満たしたときだけ）**

```yaml
base_config:
  - "<fixed bundle>/source/configs/acoustic.yaml"
use_energy_embed: false
use_breathiness_embed: true
use_voicing_embed: true
use_tension_embed: true
augmentation_args:
  random_pitch_shifting:
    enabled: false
  fixed_pitch_shifting:
    enabled: false
  random_time_stretching:
    enabled: false
```

**例: 高表現型のVariance候補（durationとの分離が前提）**

```yaml
base_config:
  - "<fixed bundle>/source/configs/variance.yaml"
predict_dur: false
predict_pitch: false
predict_energy: false
predict_breathiness: true
predict_voicing: true
predict_tension: true
```

ここでは`predict_pitch=false`であり、**独立Pitch stageが必要なら別学習configで`predict_pitch=true`を設定**する。この例は設計上の構成で、同時学習・性能・モデル資源を検証済みと主張しない。

Config Compilerは次の整合条件を満たさない場合、**起動前に失敗**する。

1. `use_*_embed` をtrueにする場合、学習用に必要なfeature extractorと音響入力列を準備・検証する。空値を0として水増ししない。
2. `predict_*` をtrueにする場合、そのVariance training inputに必要な元観測/正しい教師があること。NoteがないとPitch predictorは「対象外/要測定」。
3. Variance予測を使用する場合、対応するAcousticモデルのEmbeddingと同じ特徴名・正規化範囲・timebase/versionを使う。
4. Energy + Breathiness + Voicingの三者同時有効化は既定で拒否（公式の注意に従う）。
5. Pitch/Time augmentationはmetadata付き学習データを生成し、`segment_id → derived item`のlineage/重みの紐付けを証明するまで無効のまま。
6. `finetune_enabled/ckpt` と`from_scratch`は既存`diffsinger_initialization.py`の検査を流用。Texture処理によって初期化modeを暗黙に変えない。
7. 可用Vocoder資源の有無・hash・ライセンス・出力側の互換性を必ず確認する。見つからなければ学習成功/モデル完成へ進まない。
8. Config源、`recipe_id/version`、選択した教師/参考segment、`input_fingerprint`、`effective_resolved_hparams_sha256`を不変manifestに保存。
9. 現在の`TrainDurationAsync`は独立の新規学習入口であり、**同じモード設定でDurationまで追加学習されたと偽らない**。各Stageに独立したmode/evidenceを保持。
10. 既存jobの`acoustic-training.yaml`や`train_weights.json`を学習途中に書換えない。新Recipeは新job/new artifactに保存する。

## 4. augmentationと重み付きサンプリングを両立する

現行`diffsinger_binarize.py`ではAugmentationを切って、`binary/train.names.json`と`train_weights.json`の重みをitem単位に照合している。この安全性をプリセットで崩してはならない。

1. Binarizerが派生item `derived_item_id`、`origin_segment_id`、`augmentation_kind`、`augmentation_settings_hash`、`artifact_hash`を保存する。公式dataset indexとの対応をworkerが生成する。
2. 派生itemも元のrole/eligibilityを継承する。元がexcludedなら派生もexcluded。**学習元が同じ複数派生によって学習時の合計weightが意図せず増幅しない**よう、重み合計を元区間ごとに正規化する。
3. trainのみにAugmentationを適用。validationの元録音と同一セッション・派生音声をtrainに混入しない。歌唱のPitch表現を変えた場合、F0/Note/Duration等の対応labelも物理変換と整合させる。
4. `use_key_shift_embed` と`use_speed_embed`を有効にする場合は、固定版`AcousticBinarizer`の派生itemのキーを実際に生成し、Trainerのdata loading・Exporter推論入力が対応することを確認する。
5. 派生の`train.names.json`と`train_weights.json`が重複/欠落/不一致なら学習を停止。単に`enabled: true`へ変えたPRは不合格。
6. できるまでは**Augmentationを無効のまま**、別データ選択・Acoustic/Variance Stage・Inference側制御でプリセット差を作る。機能不足を隠さない。

## 5. VarianceとDurationの訓練方法を分ける

固定`BestPractices.md`は、Durationと他のVarianceを同時に予測すると、Durationの正確さには有利な場合がある一方、Pitch/Varianceの表現が不安定になる可能性を説明している。

**選定可能な実装レシピ**

- `duration_accuracy_recipe`: Durationを独立して学習、もしくは公式推奨のDuration＋Variance共同学習後にVariance部分を採用しない方式を比較。
- `pitch_recipe`: 歌詞/Note/F0の教師がある場合だけPitch予測を学習。
- `breath_voicing_tension_recipe`: 必要な音響特徴と資料がある場合、Pitch/Varianceを必要に応じて別Stageで学習。
- `vocoder_recipe`: 声質保持とライセンスが成立する固定/適応Vocoderを選択。

それぞれ別のinput/hash/学習mode/初期重み/run-report/validation statusを記録する。全てを単一の`train_diffsinger`成功フラグに潰さない。

## 6. 出力：2つの方式を正式に認める

### A. 単一のDiffSinger声庫に連続表現を持たせる

Acousticが必要なEmbeddingを学習し、`dspitch/`、`dsvariance/`（必要時）、Duration、Vocoder、辞書等が揃い、**OpenUtau 0.1.565が対応制御を実際に受け取れる場合**。PITD/DYN以外の制御は声庫の機能確認が必要。

利用可能な制御軸を`texture_capabilities.json`（Your Singer用付加metadata）へ保存するが、**OpenUtauがこの独自JSONを解釈すると仮定してはいけない**。機能を適切な`dsconfig.yaml`/サブフォルダ/ONNXで出力することが必要。

### B. 質感ごとに別のAcoustic/声庫を生成する

ユーザーが選んだプリセットごとに、**教師重み・モデル構成・学習段階が異なるcheckpoint**を生成する。OpenUtauのvoice color統合が未検証なら、`SingerName-クラシック` / `SingerName-高表現` 等の**独立した声庫**として出力する。互換が実証された場合のみ単一声庫のmode/voice colorへ統合する。

- いずれも元の学習済みモデルを勝手に上書きしない。
- Shared Duration/Vocoderを使う場合はphoneme/音響仕様互換、checkpoint hash、別ライセンスを照合する。
- 出力名だけ差があり同一checkpoint/同一レンダリングになるなら、質感プリセットの実現と認定しない。

## 7. Vocoderと第三者権利に関するリリースゲート

現行`SingingTrainingPipeline.RequireVocoder()`は`models/vocoder/nsf_hifigan/model.ckpt`を要求しており、既定の候補のライセンスを **CC BY-NC-SA 4.0（非商用）** として扱い、同梱せず利用者による取得を案内している（`docs/diffsinger-acoustic-input.md` 参照）。この状態は、ユーザーが手動で外部資源を用意しなくても利用できる製品要件と整合していない。

**プリセット設計の成否と独立のブロッカーとして**: 配布可能かつ対象DiffSingerの音響仕様に互換なVocoderを選定/学習し、ライセンス・成果物・品質を固定する。非商用モデルを勝手に製品へ同梱/再配布しない。再配布できない場合はユーザー向けの明確な権利情報と利用導線を検討するが、初回完全オフラインの要件を満たしたと宣言しない。

## 8. レシピ選択、評価・完成判定

同じ話者・同じ未学習歌詞・音符・音域で**プリセット別に生成モデルを比較**する。比較対象はDiffSinger既定単一路線と、少なくとも`classic_synthetic` / `expressive_complex`の2系列。さらに`natural`/soft/powerfulのうち対応できる系統を追加する。

| 検証層 | 必須確認 |
|---|---|
| 宣言 | preset ID → strategy ID → DiffSinger recipe ID/versionが一意に決まり、意思と能力に合う |
| Dataset | same speaker、音素/MFA/F0/音量/variance実測、重みとAugmentationの派生itemが追跡できる |
| Config | Acoustic/Duration/Pitch/Variance/Inference/Exporterが同じfeature version・shape・timebaseで一致 |
| Trainer | 実学習config、初期重みの読込/初期化、sampling、checkpoint hashをrun reportで確認 |
| Voicebank | 必須Acoustic/Duration/Vocoder＋選択した補助モデルが実際に読み込める |
| 推論 | OpenUtau 0.1.565で新しい歌詞/音符・対応curvesを実レンダリング |
| 品質 | **話者類似、発音、F0追従、無音/破綻、目標質感の適合、複数の設定での表現差**を評価 |

- **古い合成質感**は自然度だけで落とさない。ただし音割れ・音素欠落・誤音程は不合格。
- **高表現**はBREC/TENC/VOICの名前が入ったconfigだけでは不合格。学習済み声庫で複数の実表現・本人らしさを確認する。
- `classic`と`expressive`で実際の学習条件またはcheckpointに違いがあり、さらに**生成音声の違いが目標の方向にある**ことを検証する。
- UIは `未実装` / `入力不足` / `学習可能` / `学習済み・推論未検証` / `質感確認済み` を区別。**プリセットを消したり、対応しないのに代わりにnaturalを選ぶことをしない**。
- 実GPUとOpenUtau実機の検証が終わるまでプリセット対応の完了としない。

## 9. 実装が小さくなるように責務を分ける

| 既存クラス／ファイル | 追加責務（設計上） |
|---|---|
| `BuiltInSingingStrategies` | preset別の実Recipeを戦略候補へ紐付け。宣言上あるだけの`expression_conditioning`は実装前にsupportedへ変えない |
| `SingingTrainingStrategyPlanner` | 目標とDataset Profileから候補を決め、必須表現軸と必要Stageを含むRecipe候補を返す |
| `TrainingModeResolver` / `TrainingResolvedPlanRecord` | 学習方法だけでなく、stage別config/入力hash・初期化modeを固定 |
| `DiffSingerBinarizeService` / `diffsinger_binarize.py` | Recipe別のAcoustic/Variance input＋設定、重みmanifest整合 |
| `diffsinger_weighted_sampler.py` | 質感別weightと将来のAugmentation派生itemのlineageを照合 |
| `DiffSingerTrainerService` | 必要なAcoustic/Duration/Pitch/Variance Stageと完了状態を独立管理 |
| `DiffSingerExporter` / `DiffSingerExportValidator` | 追加表現モデルと制御対応、独立voicebank variantを安全に出力 |
| `TrainingVerificationRecord` | 同じ未学習フレーズで目標質感の達成と本人性・音程等を別記録 |

**UI・設計・成果物の説明は日本語**。実装の正本は [学習システム基本設計](learning-system-design.md)、質感の選択ルールは [Preset→学習レシピ設計](preset-recipe-selection.md)。DiffSinger固定版の学習成果物・学習入力条件は`docs/implementation-v1-10.md`、`docs/diffsinger-acoustic-input.md`へ引き続き記載する。
