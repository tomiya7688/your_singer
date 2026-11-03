# 生成目的・目標声質・素材量から学習計画を構成する（v1）

## 位置づけ

この文書は [学習方式とBackend設計](training-modes-and-backends.md)、[自動振り分けと相互利用](routing-and-cross-domain.md) を具体化する設計正本である。

関連する正本:
- [質感プリセット分類・音声モデル系統調査](texture-preset-taxonomy.md)
- [素材の方向性から学習戦略を自動選択](data-profile-strategy-selection.md)
- [第三者・Modの学習戦略登録API](strategy-plugin-api.md)

既存の `Source Profile / Adaptive Training` で議論した「会話が多い・歌唱が多い・素材が大量/少量」という方向性診断は、v1の学習計画にも取り込む。高度な収録環境別最適化だけをv2で拡張する。

**決定順序は「生成目的 → 目標の声質・質感 → 解析済み素材の量・多様性・信頼性 → 実行可能な学習工程の構成 → 学習・評価」とする。**
ユーザーの作りたいモデルを素材の多数派から自動決定しない。トーク素材が無いから歌唱モデルへ勝手に切り替えることも、歌唱素材が無いからトークモデルへ勝手に切り替えることもしない。

「生成目的」と「目標声質」はユーザーが選び、**「自動」は指定目的に対して学習計画を決める機能**である。Fine-tuningと新規学習のどちらか一つだけを選ぶ分類器ではなく、必要なら複数の適応段階を組み合わせる。

ユーザーが「トーク」と「歌唱」の両方を選んだ場合、それぞれ**独立した目標声質・学習計画・評価**を持ち、共通話者情報を再利用する。

## 1. 入力契約: ユーザー指定の目標

例（schemaは実装Issueで固定するが意味は固定）:

```json
{
  "speaker_id": "spk_02",
  "requested_outputs": [
    {
      "target": "talk",
      "texture": {
        "preset": "singing_like",
        "description": "歌唱時の張りと明るさを残した自然な話し声",
        "reference_segment_ids": ["seg_110"],
        "strength": 0.7
      },
      "training_preference": "auto"
    },
    {
      "target": "singing",
      "texture": {
        "preset": "natural",
        "description": "本人の普段の歌声に近い声",
        "reference_segment_ids": [],
        "strength": null
      },
      "training_preference": "auto"
    }
  ]
}
```

- `target`: `talk` または `singing`。内部では両方を**別Job**に展開する。その他の出力方式は将来のExporter拡張で追加。
- `texture`: **目標の音色・発声傾向**。元データがspeech/singingどちらかを意味しない。クラシックな電子的音声、高表現・複雑な声、自然、明瞭、柔らかい、力強い等のプリセット分類は `texture-preset-taxonomy.md` に定義。プリセット自体はModから追加可能で、各Backendで再現できるものだけを利用可能と表示する。
- `reference_segment_ids`: ユーザーが任意に選ぶ「この感じの声」の見本。話者が一致すること、元音声が参照可能であることを検証する。指定無しも許可し、適格な参照区間を自動推薦できる。
- `strength`: 目標質感の反映度を示す任意の希望値。Backendに対応パラメータが無い場合は**使ったことにせずunsupportedと表示**。初期版は無理に連続制御を公開せず、presetや参照区間による指定を優先してよい。
- `training_preference`: `auto`（推奨） / `prefer_fine_tune` / `prefer_from_scratch` / `force_fine_tune` / `force_from_scratch` の設計上の候補。旧 `auto/fine_tune/from_scratch` の意味を維持しつつ、推奨と強制を分ける。強制指定時は暗黙fallback禁止。最初の画面では高度な設定として折りたたむ。
- 目標質感を素材から勝手に上書きしない。利用可能な参照音声が無い／固定Backendに能力が無い場合は警告し、実現可能な代替計画を提案する。ユーザーの意図を別の目標へ無断変更しない。

## 2. 解析済み素材の棚卸し（TargetとTextureを指定した後）

ユーザーが入力を歌唱・トーク別にフォルダ分けする必要はない。Universal Voice Datasetと話者手動編集から、選択話者について**学習に使える実効的な量**を算出する。

```json
{
  "speaker_id": "spk_02",
  "inventory_revision": "1",
  "source_fingerprint": "...",
  "evidence": {
    "speech": {"raw_seconds": 180.0, "eligible_seconds": 40.0, "distinct_utterances": 12, "coverage": "sparse"},
    "singing": {"raw_seconds": 3300.0, "eligible_seconds": 1900.0, "distinct_phrases": 180, "pitch_coverage": "wide"},
    "timbre": {"eligible_seconds": 1850.0, "reliable_reference_segments": 50},
    "styles": {"singing_like": {"reference_count": 18}}
  },
  "measurement_status": "partial",
  "limitations": ["speech_transcript_uncertain"]
}
```

上の秒数は形式を説明する例であり、必要量・合格閾値ではない。**秒数だけで学習成立を判断しない**。

棚卸しで最低限区別する情報:

- (a) 発音・韻律を教師として使える **目的別の音声量**（話す/歌う能力に対する教師）
- (b) 同じ話者の声色を抽出できる **声質参照量**（歌唱からトークへ、トークから歌唱へ流用可能）
- (c) 目標質感の参考になる **声色・発声条件の分布**（強い歌唱、囁き、通常会話など）
- (d) 多様性（話者一致、独立発話数、異なる音素・母音伸長・音域、重複や近似重複）
- (e) 品質と測定状態（ノイズ、BGM、残響、話者重なり、ASR/MFA/F0の根拠、未計測）
- (f) ソース系統と録音session（同一曲・同じ台詞の切り出しを大量の独立データと誤数えない）

`raw_seconds`、`eligible_seconds`、`effective_training_seconds`（必要時）は別物として保存する。後者を定義する場合は重み・重複・用途適格性の計算式とversionを明示し、単純に重みを掛けて「確実な教師時間が増えた」と表現しない。

## 3. 学習プランナー: 目的ごとに異なる学習戦略

提案責務:

- `VoiceModelGoal`: 目標target/texture/任意の手動希望
- `SpeakerDataInventory`: 話者×用途の教師データ・声質データの実効量と制約
- `TalkTrainingStrategyPlanner`: **自然な発話能力と指定声質**を組み立てる
- `SingingTrainingStrategyPlanner`: **音符に従う歌唱能力と指定声質**を組み立てる
- `BackendCapabilityRegistry`: fixed checkpoint/Trainerに実際に可能な工程を列挙する
- `TrainingStrategyRegistry`: 内蔵/第三者が登録した学習方法と対応preset、データ量条件、実行可能stageを列挙する。Exporter Registryとは独立。
- `TrainingPlanResolver`: 実行可能性を検証し、候補計画を評価して確定する（目的を変更しない）
- `TrainingJobPlanningService`: 確定した計画・入力fingerprint・provenanceを保存する

順序:

1. ユーザーのtargetとtextureを確定し、参照区間の話者・内容・hashを検証。
2. 対象話者の共通データを棚卸しする。**必要なのは目標と同種の音声だけではない**。
3. 目標の主要能力（talk=発音/文からの発話/韻律、singing=歌詞/音符/音高/時間長）と目標声質を別要件として分ける。
4. 内蔵/Modの `TrainingStrategyRegistry` から、選択した質感のtraitsとデータ方向性profileに対応した学習方法を列挙する。既存事前学習モデル、声質適応、スタイル適応、目的別追加学習、新規学習の各工程の利用条件を固定Backend能力表から照合する。
5. 実行可能な段階的計画を複数候補として構成する。例: `base_tts→timbre_adaptation_from_singing→talk_style_adjustment→evaluate`。
6. 会話主体/歌唱主体/混在/少量/大量/低品質だが希少な区間等の重複可能なProfileと、データ量・多様性・欠損・品質・GPU/VRAM・事前学習資源の条件で候補を比較する。高品質を保証しない計画には警告。**Fine-tune vs scratchを先に決めない**。
7. 採用計画と代替候補・棄却理由・実際に使うデータ源/重み・checkpoint hash・stage versionを保存してから学習開始。
8. 生成後は指定目的の実音声生成と**指定textureへの近さ**を評価し、必要なら別候補計画またはcheckpointを比較。ユーザー指定目的/質感を密かに変更しない。

## 4. 典型的な素材量×目標のマトリクス

| 目標 | ある素材 | 主に検討する学習経路 | 非保証点 |
|---|---|---|---|
| トーク・自然 | 発話が豊富 | 対象発話教師＋事前学習TTSの声質/韻律適応、条件次第で新規学習も候補 | すべての話し方を再現できるとは限らない |
| **トーク・歌唱時の声質** | **歌唱が豊富、発話がゼロ/少量** | **歌唱から本人声質・狙うtextureを抽出**＋汎用TTSの発話能力＋対応可能な声質/スタイル適応。追加の発話教師データが少なくてもプランを作る | 歌唱F0・長母音を会話へそのまま教師流用しない。対応Backendが声質適応できなければ未対応 |
| トーク・柔らかい声 | 発話は少、歌唱が多い | 歌唱から同一話者声質＋発話能力の事前学習基盤＋柔らかい発声の利用可能な参照/条件付け | 参照がない質感を再現できるとは保証しない |
| 歌唱・自然 | 歌唱が豊富 | 歌唱用音響/時間長モデルの学習・声質適応＋必要サブモデル | 歌詞・音符からの実レンダリングは別途検証 |
| **歌唱・トーク時の声質** | **発話が豊富、歌唱がゼロ/少量** | **発話から本人声質を抽出**＋汎用歌唱能力/歌唱用事前学習モデル＋歌唱声質適応 | 本人特有の歌唱技法・音域は発話だけでは分からない |
| どちらも | 混在素材 | 共通話者参照を再利用し、talk/singingで**別プラン**を実行 | 片方の成果物が片方の品質を自動保証しない |
| どちらも | ほぼ雑音/別話者/利用不能 | 使用可能な声質証拠・教師データ・Backend能力を確認。不成立なら理由別不足を表示 | データを捏造して成功扱いしない |

特に「歌唱しかないからトークモデルは作れない」という**入力内容だけの事前ブロックは禁止**。実際のBackendに必要な声質適応能力があるかを検査して、可否を判断する。

## 5. 何を組み合わせ、何を選択するか

**組み合わせるもの**: 学習準備・話者特徴抽出・声質適応・目的別能力の維持/学習・必要な補完/補正・重み付け・checkpoint評価。これらは1件の学習計画に複数段階として入れられる。

**選択するもの**: 各段階で使用するBackend/初期重みと、利用するトーク・歌唱素材の役割。追加学習/新規学習は**段階の初期化方式の選択**であってユーザーの生成目的ではない。新規学習を明示指定した場合はその対象コンポーネントで勝手に事前学習済み重みを使用しない。

「複数モデル」を常に全て同時に学習するわけではない。利用可能な資源・実用品質・互換性・ライセンスを考慮して必要なものだけ実行する。v1の基本weightingは各Trainerの有効な教師区間へ適用する。

## 6. 計画保存契約・例外処理

```json
{
  "schema_version": "1",
  "job_id": "job-talk-001",
  "speaker_id": "spk_02",
  "goal": {"target": "talk", "texture_preset": "singing_like", "texture_reference_ids": ["seg_110"]},
  "inventory_hash": "...",
  "requested_training_preference": "auto",
  "plan_id": "talk-from-singing-v1",
  "planner_version": "goal-texture-quantity-v1",
  "stages": [
    {"name": "speech_base", "method": "pretrained_tts", "initialization": "checkpoint", "status": "planned"},
    {"name": "speaker_timbre", "source_role": "singing_voice_reference", "method": "backend_supported_adaptation", "status": "planned"},
    {"name": "evaluate_talk", "method": "synthesize_unseen_text", "status": "planned"}
  ],
  "used_sources": [
    {"content_type": "singing", "role": "timbre_reference", "segment_ids": ["seg_110"]}
  ],
  "alternatives": [],
  "limitations": ["speech_teacher_data_sparse"],
  "plan_hash": "..."
}
```

すべてのStageには `backend_id/revision`、実際の入出力artifact hash、`weight_policy_version`、失敗理由・未検証状態を持たせる。例は論理的な学習工程であり、固定Style-Bert-VITS2 / DiffSingerに実現できると宣言したものではない。**固定Backendで未対応なら能力追加・別Backend導入の小Issueに分離**し、silent fallbackや架空の成果物を禁止する。

失敗例: 入力の目標参考区間が別話者→拒否し再選択、目標texture用の参考発声なし→その質感はunsupported/代替案提示、目的側の教師音声0だが汎用能力と話者適応に対応→生成を試行、対象の事前学習能力なし→不足を明示、GPU不足→対応する軽量計画へ再解決を提案（無断実行しない）。

## 7. UIの基本順

```text
1. 素材の読み込み（会話と歌唱は混在してよい）
2. 話者を選択
3. 作りたいものを選択：トーク / 歌唱 / 両方
4. 欲しい声の質感を選択・任意の参考区間を試聴して選択
5. 素材を自動棚卸し：話し声・歌声・声質参照に使える量を表示
6. おすすめ学習計画を提示（使用する素材役割・方法・理由・制約）
7. 学習・本物の音声生成・評価
```

「追加学習」「最初から新規学習」をユーザーが手動指定できる機能は残すが、一般ユーザーにはその選択を**目的・質感より前に要求しない**。Advanced設定として表示する。

## 8. 必須の自動/実GPUテスト

- **歌唱のみ→トーク**: user target=talk, texture=singing_like。歌唱を声質参照としてプランに入れ、必要なTTS基盤の能力があれば計画可能。生成したら任意日本語を実読み上げし声質・韻律を検証。
- **発話のみ→歌唱**: target=singing。発話を声質参照、歌唱モデルのメロディー/歌詞能力を別に確保。未学習ノートと歌詞から実レンダリング。
- 同じ素材×別texture → 別plan hash、元音声や分類は再計算不要。
- 同じ目標×素材量の違い（少量/豊富/大半が低品質/重複多）→適格データと学習工程が変わる。
- 両方を選択→2つの別plan、声質参照のみ共有。どちらか失敗してももう片方の成果物/状態は壊さない。
- 手動 `force_from_scratch` 指定→対象のモデル初期化契約でpretrained話者checkpointを読まない。実行不能なら理由付き失敗。
- 生成目的/textureをユーザー未指定のまま素材構成から勝手に決定しない。保存旧Jobはmigration/再作成。
- データゼロ、別話者混入、MFA未校正、GPU不足、Backend非対応、reference hash変更、キャッシュ更新を検証。
- Unitのプラン生成だけを実品質達成とみなさず、実素材・実GPU・既存互換形式の読み込みと推論で最終確認。

## 9. v1とv2の境界

**v1必須**: 目的・質感プリセットの指定、話者×用途・質感向けの素材量と方向性profile、targetごと独立した学習計画、歌唱→トーク/発話→歌唱という少量/片側素材の適応可能性判定、登録された内蔵/Mod学習戦略の照合、対応経路での実生成評価。

**v2以降**: Source Profileを使った詳細な収録条件・話法・録音品質によるレシピ最適化、動的な補正強度、モデルアンサンブルと定量学習されたPlanner最適化。v1の「目的と質感と手持ちデータから計画を作る」基礎をv2に先送りしない。

## 10. 小粒度Issueの依存順

このテーマの実装順は以下とし、既存の [学習Issue親 #79](https://github.com/tomiya7688/your_singer/issues/79) と、既存の [実装一覧PR #115](https://github.com/tomiya7688/your_singer/pull/115) の追加条件として扱う。PR #115がマージされた後に依存順の一覧へ取り込む。

1. [#84](https://github.com/tomiya7688/your_singer/issues/84): ユーザー指定のtarget/texture/参考区間とmodeの保存（ほかのIssueと並行可）
2. [#132](https://github.com/tomiya7688/your_singer/issues/132): 教師用途別の実効素材量と声質参照量を棚卸し（#80/#81/#82/#87依存）
3. [#133](https://github.com/tomiya7688/your_singer/issues/133): TalkTrainingStrategyPlanner（#132/#98依存）
4. [#134](https://github.com/tomiya7688/your_singer/issues/134): SingingTrainingStrategyPlanner（#132/#98依存、#133とは並行可）
5. [#88](https://github.com/tomiya7688/your_singer/issues/88): Backend Preflightによる計画候補の確定（#84/#132/#133/#134依存）
6. [#112](https://github.com/tomiya7688/your_singer/issues/112): 目標→質感→素材量→推奨計画の日本語UI
7. [#100](https://github.com/tomiya7688/your_singer/issues/100): 歌唱のみ→トーク実接続、[#99](https://github.com/tomiya7688/your_singer/issues/99): 発話のみ→歌唱実接続
8. [#108](https://github.com/tomiya7688/your_singer/issues/108): 実音声生成と最終受入

各Issueは自分の責務だけを実装し、複数のトレーナーや歌唱/トークの能力を大きな一つのIssueへ戻さない。
