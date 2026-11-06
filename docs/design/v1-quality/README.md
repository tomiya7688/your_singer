# v1 音声解析精度向上 — 設計入口

## 目的

**学習前の素材から、対象話者の正しい音声・音素・音高・歌唱/会話ラベルをできる限り正確に復元する**。研究用のモデル比較アプリを作るのではなく、ユーザーは普通の動画・音声フォルダを投入し、内部では複数の処理結果を組み合わせて実用的な歌唱モデルとトークモデルを生成する。

原則は **観測の保全 → 用途別候補生成 → 複数証拠の照合 → 採用根拠の記録 → 学習 → 実生成結果で改善を検証**。Demucs不可のときのfallbackだけを作って終わりにしない。

### この段階で変更しないもの

- UI / Process / Data のUPDと `src/process/YourSinger.Process/processing/ml/` にPython Workerを置く構成
- `YourSinger.exe` 一箇所起動、ユーザー側Python/.NETの別導入不要
- 歌唱とトークの自動仕分け、Universal Voice Dataset、ユーザーの話者修正と補完ON/OFF
- v1基本weightingと新規学習/追加学習の両経路（[v1学習設計](../v1-training/README.md)）

## 現行mainから見える改善箇所（2026-11-03時点）

| 箇所                         | 現行処理                                         | 主なリスク                             | 精度向上の設計                                |
| ---------------------------- | ------------------------------------------------ | -------------------------------------- | --------------------------------------------- |
| `preprocessing.py`           | Demucs `htdemucs` を通し、整音済音声で後段を処理 | もともと明瞭な話し声まで音色を加工     | raw/separated/cleaned等の複数候補と用途別選択 |
| `audio_quality.py`           | 分離後RMS÷元RMSを `music_leak` へ利用            | 音声の大きさとBGM残存率を混同          | 音楽残存と声質劣化を別指標で評価              |
| `speaker_analysis.py`        | embeddingのcos類似度をclampしてconfidenceとする  | 確率ではない値を信頼度と誤用           | 複数窓・複数表現と話者整合、未校正状態        |
| `content_classification.py`  | pitch/有声率等のヒューリスティック               | 語り調、ラップ、短歌唱を誤分類         | 複数特徴・短時間窓・区間内mixed検出           |
| `transcription_alignment.py` | Faster-Whisper単一路線                           | 歌唱、BGM、低音量の誤歌詞を伝播        | 複数候補・音素検証・歌詞が不確かな場合の保留  |
| `audio_features.py`          | 自己相関でF0生成                                 | オクターブミス/混合音/高音歌唱への弱さ | 歌唱向け頑健F0と独立な照合器を組み合わせる    |
| `AutoCorrectionService`      | 低confidenceで区間除外                           | 未校正MFAを0として誤除外               | 既存 #63を先行修正、異常値と未計測を分離      |

## 正確さの意味

- 精度は1個の `overall_quality` で表せない。**話者が合っている、発音が正しい、F0が正しい、声質が壊れていない、BGM混入が少ない、学習結果が使える**を分ける。
- 候補間一致は品質の手がかりだが正解証明ではない。共通誤差にも注意する。
- 生音を保持し、音声や特徴を選んだ理由・モデル版・時間軸・信頼性の意味を追跡する。
- 一つのモデルが全ステージで最良とは限らず、**特徴抽出ごとに別候補を使ってよい**。
- 軽量GPUでは全モデル常時実行しない。まず安い判定→難しい区間だけ追加解析するが、複数モデルで照合する能力自体はv1の初期機能。

## 設計文書

1. [候補音声の生成・比較・用途別選択](candidate-audio-routing.md)
2. [話者・歌唱分類・文字起こしの証拠統合](speaker-content-transcript.md)
3. [歌唱F0・歌詞/音素境界の多方式照合](pitch-and-alignment.md)
4. [基準データ・評価指標・品質ゲート](quality-gates.md)
5. [Issue一覧と依存順](implementation-plan.md)

## 既存の実装契約との関係

- 低品質でも有用な区間を残すweightingは [training-strategy](../v1-training/contracts-and-weighting.md) へ渡す。除外と重みを混ぜない。
- `UniversalVoiceDatasetRecord` の原データを上書きしない。新しい候補や不一致記録はversion付きの派生ファイルに置く。
- `#61 / PR #68` のMFA参照と `#63` の未校正confidence対策を前提とし、未統合PRと同じファイルをむやみに編集しない。
- ベンチマークだけ通って実モデルが劣化した場合、改善機能として既定採用しない。
