# 配布物の署名とattestation

## 目的

利用者へ渡る最終成果物の真正性を、「誰が・どのワークフローから生成したか」まで検証できるようにします(#49)。
利用者向けの検証手順は `README.md` の「配布物の検証」に記載します。この文書は、リポジトリオーナー向けの設計と対応状況です。

## 成果物の一覧

| 成果物 | 生成元 | 署名・検証方式 | 状況 |
| --- | --- | --- | --- |
| `YourSinger-windows-x64.zip`(自己完結配布物) | `package-worker.yml` | SHA-256、Artifact Attestation(keyless) | 実装済み |
| `SHA256SUMS` | `tools/package_distribution_archive.ps1` | Artifact Attestation(keyless) | 実装済み |
| `YourSinger.exe`、`workers/YourSinger.ML.exe`、`YourSinger.*.dll` | `build_windows_distribution.ps1` | Authenticode署名とRFC 3161 timestamp | 未実装(証明書の選定待ち) |

次の成果物は生成していません: macOS・Linux・Android向け成果物、container image、Python package、NuGet package、GitHub Release。生成を始める場合は、種類に合う署名方式を同じ文書へ追加してから公開します。

リポジトリ内のTrainer用Python runtimeやMFA bundleなど、第三者のruntimeは配布ZIPの内側に含まれます。これらは固定hash・固定版で管理し、ZIP全体をattestationの対象にします。

## 実装済みの仕組み

1. `tools/package_distribution_archive.ps1` が配布フォルダをZIP化し、`SHA256SUMS` を作ります。ZIP64対応のAPIを使うため、2GBを超える配布物も作れます。
2. `package-worker.yml` が、`actions/attest-build-provenance` でZIPと`SHA256SUMS`のattestationを作ります。OIDCを使うkeyless方式で、秘密鍵を持ちません。フォークからのPRはOIDC権限がないため、attestationを作りません。
3. 同じワークフローで `gh attestation verify --signer-workflow` を実行し、attestationが生成元のワークフローと一致することを検証します。
4. `tools/verify_distribution_signing.ps1` が、`SHA256SUMS` と最終ZIPの一致を確認します。

## 公開リリースのゲート(未実装)

公開リリースは、`tools/verify_distribution_signing.ps1 -RequireAuthenticode` が成功した場合だけ行います。このオプションは、自社製のexe/dllにRFC 3161 timestamp付きの有効なAuthenticode署名が無いと失敗します。
リリース用ワークフロー(`release-windows.yml`)は、Authenticode署名の方式が決まってから追加します。

### 決めること

公開配布でWindowsの発行元が「不明」にならないよう、信頼されたCode Signing証明書が必要です。自己署名証明書は最終解にしません。候補は次のとおりです。

- Azure Artifact Signing(旧Trusted Signing): 証明書の秘密鍵を持たず、GitHub ActionsからOIDCで署名できます。利用資格の審査があります。
- EV/OVのCode Signing証明書: クラウドHSM経由の署名が必要です(秘密鍵のエクスポートは不可)。

どの方式でも、秘密鍵・PFX・パスワードはリポジトリにコミットせず、GitHub Secretsまたは外部の署名サービスに置きます。

## 既存成果物の確認

2026-10-25時点で、GitHub Releaseは公開されていません。CIが生成する配布物(`YourSinger-windows-x64`)は14日で失効する開発用の成果物で、公開配布物ではありません。
本変更より前に生成された配布物は未署名で、検証できません。公開リリースは、署名のゲートを通過したものだけにします。
