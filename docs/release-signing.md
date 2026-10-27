# 配布物の署名とattestation

## 目的

利用者へ渡る最終成果物の真正性を、「誰が・どのワークフローから生成したか」まで検証できるようにします(#49)。
利用者向けの検証手順は `README.md` の「配布物の検証」に記載します。この文書は、リポジトリオーナー向けの設計と対応状況です。

## 成果物の一覧

| 成果物                                                            | 生成元                                   | 署名・検証方式                         | 状況                                                                        |
| ----------------------------------------------------------------- | ---------------------------------------- | -------------------------------------- | --------------------------------------------------------------------------- |
| `YourSinger-windows-x64.zip`(自己完結配布物)                      | `package-worker.yml`                     | SHA-256、Artifact Attestation(keyless) | 実装済み                                                                    |
| `SHA256SUMS`                                                      | `tools/package_distribution_archive.ps1` | Artifact Attestation(keyless)          | 実装済み                                                                    |
| `YourSinger.exe`、`workers/YourSinger.ML.exe`、`YourSinger.*.dll` | `build_windows_distribution.ps1`         | Authenticode署名とtimestamp            | 開発段階は自己署名(`CN=Tomiya7688`)。公開リリースはSignPath Foundation(#62) |

次の成果物は生成していません: macOS・Linux・Android向け成果物、container image、Python package、NuGet package、GitHub Release。生成を始める場合は、種類に合う署名方式を同じ文書へ追加してから公開します。

リポジトリ内のTrainer用Python runtimeやMFA bundleなど、第三者のruntimeは配布ZIPの内側に含まれます。これらは固定hash・固定版で管理し、ZIP全体をattestationの対象にします。

## 実装済みの仕組み

1. `tools/package_distribution_archive.ps1` が配布フォルダをZIP化し、`SHA256SUMS` を作ります。ZIP64対応のAPIを使うため、2GBを超える配布物も作れます。
2. `package-worker.yml` が、`actions/attest-build-provenance` でZIPと`SHA256SUMS`のattestationを作ります。OIDCを使うkeyless方式で、秘密鍵を持ちません。attestationを作るのは、`main` とリリースタグ(`v*`)から実行した場合だけです。未保護のブランチで変更したワークフローから、attestationを発行させないためです。
3. 同じワークフローで `gh attestation verify --signer-workflow` を実行し、attestationが生成元のワークフローと一致することを検証します。
4. `tools/verify_distribution_signing.ps1` が、`SHA256SUMS` と最終ZIPの一致を確認します。

注意: CIは `--source-ref` で実行時の参照元(`main` またはタグ)まで固定して検証します。利用者向けの手順(README)は、生成元のワークフローまでを検証します。公開リリースが始まったら、READMEに `--source-ref refs/tags/v<版>` を追加します。

署名検証の既知の制限: `verify_distribution_signing.ps1` は、timestampの方式(従来方式かRFC 3161か)を区別できません。Authenticode署名の実装時に、`signtool` の `/tr` で署名し、`signtool verify` で確認します。

## 構築ワークフローの実行方法

`package-worker.yml` は約25分かかるため、PRでは自動実行せず、GitHub Actionsから手動(`workflow_dispatch`)で実行します。配布前の動作確認の扱いは、[作業開始からマージまで](../dev-guidelines/workflow.md)の「配布前の動作確認」に従います。

## 開発段階の署名(自己署名)

公開リリースまでは、手元で `tools/sign_distribution_self_signed.ps1` を実行し、自己署名証明書 `CN=Tomiya7688` で自社製バイナリに署名します。

- 証明書は、初回だけ `-CreateCertificate` を付けて、現在のユーザーの証明書ストア(`Cert:\CurrentUser\My`)に作ります。秘密鍵をPFXとして書き出したり、リポジトリへ置いたりしません。
- 署名後に `tools/update_distribution_manifest.ps1` で `distribution-manifest.json` のhashを更新し、`verify_distribution_signing.ps1 -AllowSelfSigned` で署名者とtimestampを検証します。
- 自己署名は信頼されたチェーンを持たないため、Windowsの発行元は「不明」のままです。公開配布の最終解にはしません。
- CIの構築ワークフローでは署名しません(秘密鍵をCIへ渡さないため)。

## 公開リリースの署名とゲート(#62)

公開リリースでは、SignPath Foundation(OSS向けの無料コード署名)で署名します。申請、ワークフローへの組み込み、リリースのゲートは #62 で行います。
公開リリースは、`tools/verify_distribution_signing.ps1 -RequireAuthenticode -ExpectedSignerSubject "SignPath Foundation"` が成功した場合だけ行います。`-AllowSelfSigned` は使いません。
秘密鍵・PFX・パスワードはリポジトリにコミットしません。SignPathのAPIトークンはGitHub Secretsに置きます。

## 既存成果物の確認

2026-10-25時点で、GitHub Releaseは公開されていません。CIが生成する配布物(`YourSinger-windows-x64`)は14日で失効する開発用の成果物で、公開配布物ではありません。
PR #53 より前に生成された配布物は未署名で、検証できません。公開リリースは、署名のゲートを通過したものだけにします。
