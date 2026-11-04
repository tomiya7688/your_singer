# データの方向性から学習戦略を自動構成する（v1）

本書は具体例と判定軸の補足。役割分離・処理順・候補比較・Jobの入力契約は [学習システム基本・詳細設計](learning-system-design.md) を正とする。登録する学習方法の機械検証形式は [Modプロトコル](learning-strategy-protocol.md) を参照する。

## 目的

ユーザーが **出力目的（歌唱/トーク）と質感プリセット** を選んだ後、手持ちの音声素材の方向性を自動解析して、登録済みの学習方法から適した工程を選択し組み合わせる。

これは従来検討した `Source Profile / Adaptive Training` の中核である。**素材から生成目的・質感を自動選択する機能ではない**。

v1で基本データプロフィールとルールベースのStrategy選択を実装する。v1.x/v2では収録環境や学習結果フィードバックに基づく細かなレシピの動的最適化を追加する。

## 1. 単一ラベルへ分類しない

1つの話者データが「会話豊富」「歌唱あり」「総量多」「低品質の音素も貴重」等に**同時に該当してよい**。カテゴリは説明用の派生タグ、選択の内部根拠は数値・測定状態・品質・多様性を保存する。

| 評価軸 | 必須情報 | 使い方 |
|---|---|---|
| speech teacher | 有効時間、独立発話数、音素/語彙の多様性、信頼できる文字起こし割合 | 文章→発話能力を追加学習できるか |
| singing teacher | 有効時間、独立曲/フレーズ数、音符相当/音高/母音伸長/音素境界の有効割合 | 歌詞+音符→歌唱能力を追加学習できるか |
| speaker timbre | 発話/歌唱にまたがる同一話者の声質参照、録音源の多様性 | 本人の声色を別用途へ適応できるか |
| data volume | 実効的な長さ、重複・同一曲再利用・セッション数、欠損・学習weight | 大規模学習/追加学習/少量適応の選択 |
| style coverage | やさしい/張りのある/息声/通常発声等の観測量、参考区間 | プリセット質感を再現する根拠の有無 |
| quality and reliability | 音楽漏れ、雑音、複数話者、推定値のmeasurement status | 適格性、教師用途、重み付け、必要な補正 |
| hardware/backend | GPU/VRAM/RAM、利用可能checkpoint、BackendCapability、対応モデル形式 | 実行可能な工程、費用、警告、失敗理由 |

各軸に `observed`、`estimated`、`uncalibrated`、`unavailable` 等を保持する。再生時間の長さだけで実効学習量を決めない。歌唱中のコーラス/伴奏が多い場合、長時間でも声質や音素教師に使えない部分がある。

## 2. 代表的なProfile tag（例示）

| タグ | 説明 | 必須入力情報 |
|---|---|---|
| `speech_dominant` | 有効会話データの比重が高い | 発話/歌唱の有効量・相対比 |
| `singing_dominant` | 有効歌唱データの比重が高い | 発話/歌唱の有効量・相対比 |
| `mixed_rich` | 発話と歌唱の両方が十分にある | 双方の独立量とカバレッジ |
| `speech_only` | 発話教師があり歌唱教師が無い | 歌唱量=確認済み0とunknownの区別 |
| `singing_only` | 歌唱教師があり発話教師が無い | 発話量=確認済み0とunknownの区別 |
| `small_data` | 教師データが少量 | 固定Backend別の必要量・多様性 |
| `abundant_data` | 量・内容の独立性と多様性が十分 | raw duration以外の十分性 |
| `sparse_but_valuable` | 全体品質が低いが希少音素/音域等がある | eligibility + utility + provenance |
| `texture_supported` | ユーザー希望の質感に近い参考区間がある | preset ID + 一致話者 + 参考品質 |

これらのタグは重複可。絶対的な「30分以上なら豊富」といった閾値を無根拠に固定しない。閾値は `strategy` ごとの `requirements` とプロファイルversionで検証する。

## 3. 戦略候補の構成と選択

```text
ユーザーが指定した target + texture_preset (+ 任意の参考区間)
                       ↓
         SpeakerDataInventory / BasicSourceProfile
                       ↓
     StrategyRegistry（組み込み + Mod / 第三者）
                       ↓
      target, preset traits, input roles, quantity,
      capabilities, resources, GPU, licenseを照合
                       ↓
    TalkStrategyPlanner または SingingStrategyPlanner
             ├─ stage候補A
             ├─ stage候補B
             └─ 不適格理由リスト
                       ↓
       TrainingPlanResolver（候補比較）
                       ↓
     実行する各stageと入力の役割を確定・保存
                       ↓
    Trainer/Adapter/出力/最終音声による品質検証
```

### 選択手順の固定順序

1. **目的と質感の固定**: target、preset、参考区間を先に確定し、ユーザーの意図は以後変更しない。
2. **素材棚卸し**: `SpeakerDataInventory` から量・多様性・教師適格性・声質参照を構成する。
3. **登録戦略列挙**: 内蔵+第三者のStrategyRegistryから目的/texture特性に対応し得る方式を取得する。
4. **必要能力の厳密チェック**: 同一話者の参照、文字ラベル、音符/F0、Style条件、baseモデル、GPU/VRAM、配布形式・Trainerがあるかを検証する。
5. **入力の役割割当**: `speech_teacher`、`singing_teacher`、`speaker_timbre_reference`、`texture_reference`、`feature_only` 等へ割り当てる。**片方の音声を無条件に別タスクの教師に変えない**。
6. **候補プラン生成**: 1つの学習レシピでも複数段階を持てる。事前学習能力＋声質適応＋必要な学習＋合成時制御＋検証等。
7. **候補比較**: 目的達成可能性、目標質感の対応度、教師/参考量の不足、本人らしさ、計算資源・ライセンス、これまでの実測品質を比較。
8. **計画確定**: plan ID/version、各stageのprovider、checkpoint hash、データ役割/weight、素材・preset・strategy fingerprint、選定理由・代替案を保存。
9. **評価・改善**: 未使用文章または歌詞+音符から実音声を出し、成功なら実績を保存。失敗なら別候補へ再プランする場合も**目的・textureを維持し、ユーザーに理由を説明**する。

モデル数が多いほど精度が上がるとは限らない。登録済み戦略をすべて実行するのではなく、実用性・計算コスト・互換性・ユーザー目標で選ぶ。

### 例：ユーザー希望がトークで、歌唱素材が多い

- 会話の教師データ: 0〜少量。
- 歌唱の教師データ: 大量。声質参照は利用可能。
- target: **talk**、texture: **classic_synthetic**。
- 適合する戦略: 事前学習済み発話能力＋歌唱由来の話者声質適応＋電子的な質感制御＋未学習文章の読み上げ検証。
- 必要な声質適応を固定Backendが実現できない場合は`unsupported_capability`。モデル目的をsingingへ変更しない。

### 例：ユーザー希望が歌唱で、発話素材が多い

- 歌唱教師データ: 0〜少量。
- 発話: 大量。同一人物のspeaker timbre参照は利用可能。
- target: **singing**、texture: **expressive_complex**。
- 適合する戦略: 歌詞と音符に対応する汎用歌唱能力＋発話由来の本人声質適応＋可能な表現条件の適応＋新規ノートで試験。
- 本人の本当の歌唱音域や特殊な技法が不明な場合、無根拠に「本人の歌い方を忠実に再現」と表示しない。

### 例：豊富な歌唱と発話、自然で高表現な歌声を希望

- 歌唱教師と声質教師が十分な場合、実測値を多く利用できる追加学習や、条件に応じた新規学習を候補化する。
- 高品質な実測区間を強く利用する一方、品質の低い希少音素/音域もzero weightにしない。
- Fine-tuningとscratchはユーザーの「声質」ではなく、**あるstageの初期化方法**。多くの素材があれば必ずscratchという意味ではない。

## 4. v1の最低実装範囲

- 量・話者・内容分布・カバレッジ・有効教師/声質参照の基本プロフィール。
- ユーザー指定の目標に合わせ、組み込み戦略と登録戦略を候補として列挙・前提を評価できる。
- talk/singing別に計画を構成し、少量/多量・片方のみ・混在に異なる計画または選定理由が出る。
- profile、preset、strategy、backend versionを含む不変job入力。
- 実際の学習・生成品質での確認。使えない機能や実学習未検証は成功と表示しない。

## 5. v1.x/v2以降に拡張する部分

- 素材の細かな収録条件（ライブ/圧縮/残響/発声スタイル）の高解像度Source Profile。
- 学習レシピの継続的な自動ハイパーパラメータ調整、Augmentation配分やloss weight動的更新。
- 実運用評価データに基づく戦略ランキング・学習済みmeta-policy。
- 大規模な多数モデル比較を継続して予測精度を上げる最適化。

**v1でデータ方向性から学習戦略を選ぶ機能が無い**ことを、「高度なAdaptive Trainingはv2」として正当化しない。
