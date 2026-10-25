# 日本語音素境界の実測と配布資源

## 目的と処理

歌唱用の音素長を作るため、アプリは分離済み音声の音素境界をMontreal Forced Aligner（MFA）で実測します。
文字起こしをpyopenjtalkで音素化し、一音素ずつ日本語MFAモデルの表記へ対応させます。
区間専用の発音辞書を作り、MFA 3.4.2の`align_one`を専用Pythonから起動します。日本語の大規模辞書やtokenizerは使いません。

出力の音素列、正の区間長、時刻順、非重複、音声範囲を検証し、すべて通った結果だけに`mfa-japanese-phone-forced-alignment-v1`を記録します。
モデル音素を機械的に分割して時刻を作る処理は行いません。
`cl`（促音）、`pau`など現在の対応表で扱えない表現は実測成功とせず、ASR単語時刻による推定または`unavailable`を残します。
理由を`alignment_warning`へ保存します。推定時刻は歌唱ジョブ登録・入力生成で拒否されます。
必須runtime欠落、実行失敗、タイムアウトは解析エラーになります。

## 精度と修正歌詞

MFA JSONには校正済みの音素信頼度がないため、実測区間の`confidence`と`alignment_confidence`には0を記録し、警告を付けます。
ASR信頼度を音素境界の信頼度として流用しません。自動補正ONでは低信頼区間として除外される場合があります。
構造検証の成功は、歌詞の正確さや歌声に対する境界精度を証明しません。元の音声、歌詞、境界を確認してから学習へ利用してください。

利用者が文字起こしを修正した場合、アプリは最新の修正を再解析ワーカーへ渡し、再認識で上書きせずその音素列を実測します。
修正歌詞のhashを特徴キャッシュへ含め、以前の歌詞の結果を再利用しません。
修正履歴を再適用する際も最終値だけを使い、最終歌詞で再実測した時刻を古い履歴で破棄しません。

## 固定資源

| 資源                       | 固定値                                                             |
| -------------------------- | ------------------------------------------------------------------ |
| MFA                        | 3.4.2、専用Conda/Python 3.12環境                                   |
| Kaldi                      | 5.5.1172 CPU版                                                     |
| 日本語音響モデル           | `acoustic-japanese_mfa-v3.0.0/japanese_mfa.zip`                    |
| モデルSHA-256              | `85928ffb1024486872a677a92e1fa94d2f997462d0b84c73a58aa9bb0e35179a` |
| Windows音声入出力補助wheel | SoundFile 0.13.1                                                   |

モデルの取得元は[MFA公式リリース](https://github.com/MontrealCorpusTools/mfa-models/releases/tag/acoustic-japanese_mfa-v3.0.0)です。
モデルはCC BY 4.0として帰属を同梱し、改変せず配置します。
Conda依存の版・build・取得元・ライセンス・取得時hashを`bundle-manifest.json`へ記録します。
公開リリースには、依存全体の必要なライセンス本文・NOTICEの確認も必要です。

## 配布物を構築する開発者の手順

リポジトリのルートで、Python 3.11または3.12を使って実行してください。
利用者向け配布物には構築済みの専用runtimeとモデルを含めます。利用者のPATH、Conda環境、初回ダウンロードには依存しません。

```powershell
$env:TEMP = Join-Path (Get-Location) 'artifacts/temp'
$env:TMP = $env:TEMP
$env:MAMBA_ROOT_PREFIX = Join-Path (Get-Location) 'artifacts/mamba-root'
$env:PIP_CACHE_DIR = Join-Path (Get-Location) 'artifacts/pip-cache'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
micromamba create -y -p artifacts/mfa-runtime -c conda-forge python=3.12 montreal-forced-aligner=3.4.2 'kaldi=5.5.1172=cpu*'
python tools/prepare_mfa_bundle.py --runtime artifacts/mfa-runtime --output artifacts/mfa-bundle
./tools/build_windows_distribution.ps1 -MfaBundleDirectory artifacts/mfa-bundle
```

取得済みモデルは`--acoustic-model`で指定できます。bundle構築は既存出力を上書きしないため、再構築時は別の出力先を指定してください。
配布スクリプトはMFA版とモデルhashを検証し、不足や改変があると失敗します。
構築先は`artifacts`内へ制限し、入力bundleと重なる構築先やjunction経由の再帰削除を拒否します。

```text
workers/
├─ YourSinger.ML.exe
├─ _internal/your_singer_ml/mfa_entry.py
└─ mfa/
   ├─ runtime/python.exe、DLL、MFAとその依存
   ├─ overlay/SoundFileのWindows wheel資源
   ├─ models/japanese_mfa.zip
   ├─ bundle-manifest.json
   └─ MODEL-NOTICE.txt
```

GitHub Actionsには、専用環境作成、bundle構築、配布スクリプト実行、包装済みworkerの`forced_alignment_preflight`を設定しています。
preflightは資源検査です。実音声の精度やOpenUtauレンダリングは別途確認します。
開発時にだけ`YOURSINGER_MFA_BUNDLE`でローカルbundleを指定できます。

## キャッシュと実行領域

共通特徴抽出の処理版を`v1-05.3-mfa`へ更新し、以前の推定時刻を再解析対象にします。
MFA呼出しはVoice Project内の`alignment/.mfa-*`へ辞書・一時出力・子のキャッシュを閉じ込め、完了・失敗時に除去します。
子プロセスが失敗した場合は、調査用ログだけを`alignment/`へ残します。
子MFAは親Pythonの探索設定を引き継がず、配置先runtimeのDLLと専用補助wheelを使います。
非公開の音声・歌詞・モデル・ログはリポジトリや公開配布物へ含めないでください。

### 無音を含む測定記録

通常音素だけを返す既存APIでは、MFAの無音tier情報が失われていました。
新しく成功したMFA実行では、`phones` tierの全区間の時刻と通常音素の完全一致を検証した後、
`alignment/measurements/measurement-<一意な名前>.json`へ測定記録を保存します。
`extract_universal_features`の新しい応答には、今回保存した記録への`alignment_measurement`が含まれます。
この参照は`path`（Voice Project相対パス）、`sha256`（保存JSONのSHA-256）、`audio_sha256`（実測入力のSHA-256）を持ちます。
開発時に結果を調べる場合は、この参照先を確認してください。ファイル名の並びや更新日時から記録を選ばないでください。
推定アラインメントまたは音素なしの場合、参照は`null`になります。
保存・記録のhash読込・領域外パスの異常は抽出失敗として伝えます。
参照を返す直前にもJSONの存在とhashを照合し、測定後の変更や欠落を拒否します。
参照生成や後続の特徴抽出で失敗した場合、保存済みの測定記録が参照されずに残る場合があります。
診断用として保持し、再実行時は別の一意名で保存します。残った記録を次回の結果として自動採用しません。
後続の読込側でも利用直前のhash照合が必要です。

内部の`measure()`は通常音素と保存記録を一つの結果として返します。従来の`align()`は通常音素のlistを返す互換APIです。
C#共通datasetへの保存・参照検証・キャッシュ無効化と歌唱学習ジョブへの接続は未実装です。
現行のC#読込器は追加フィールドを保持しないため、この応答追加だけでは既存のアプリ解析データへ記録参照は残りません。
再実行時も以前の記録は上書きしません。古い特徴キャッシュから測定記録を復元する処理はありません。

記録には、音声のSHA-256、音声長、固定モデルのSHA-256、MFA版、測定方式、
対応済み音素と元の`phones` tierのラベル・時刻が含まれます。`words`など他のtierは今回の検証・保存対象に含めません。
`sil`、空ラベル、tier自体が存在しない未観測時間を別々に保存します。
音声の実行前後のhashが異なる場合、不正なMFA結果、保存失敗の場合は成功として返しません。
MFAには専用一時領域へ作成しhashを照合したコピーを渡します。元パスの一時的な差替えが測定音声へ混入することを防ぎ、コピーも実行後に削除します。
MFAへ渡す1区間の音声は1GiB以内の通常ファイルへ制限します。特殊ファイルや読込中にサイズが変わる入力は拒否します。

`sil`はMFAによる分類結果であり、息の正解ラベルではありません。
信頼度は未校正です。空ラベルや未知の隙間を無音・息へ置き換えません。
この記録は、後続のSP/AP分類の根拠を保持する準備です。
共通dataset・学習CSVへの取り込み、息の推定、完全なbinarization、実学習は引き続きIssue #11で実装・検証します。
