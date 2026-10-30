# 配布物の署名とattestation

## 目的

利用者へ渡る最終成果物の真正性を、「誰が・どのワークフローから生成したか」まで検証できるようにします(#49)。
利用者向けの検証手順は `README.md` の「配布物の検証」に記載します。この文書は、リポジトリオーナー向けの設計と対応状況です。

## 成果物の一覧

| 成果物                                                            | 生成元                                   | 署名・検証方式                         | 状況                                                                                                     |
| ----------------------------------------------------------------- | ---------------------------------------- | -------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| `YourSinger-windows-x64.zip`(自己完結配布物)                      | `package-worker.yml`                     | SHA-256、Artifact Attestation(keyless) | 実装済み                                                                                                 |
| `SHA256SUMS`                                                      | `tools/package_distribution_archive.ps1` | Artifact Attestation(keyless)          | 実装済み                                                                                                 |
| `YourSinger.exe`、`workers/YourSinger.ML.exe`、`YourSinger.*.dll` | `build_windows_distribution.ps1`         | Authenticode署名とtimestamp            | 開発段階は自己署名(`CN=Tomiya7688`)。公開リリースはSignPath Foundation(ワークフロー実装済み、申請は未了) |

次の成果物は生成していません: macOS・Linux・Android向け成果物、container image、Python package、NuGet package。GitHub Releaseは、タグ `v*` の実行が下書きだけを作ります(後述)。新しい成果物の生成を始める場合は、種類に合う署名方式を同じ文書へ追加してから公開します。

リポジトリ内のTrainer用Python runtimeやMFA bundleなど、第三者のruntimeは配布ZIPの内側に含まれます。これらは固定hash・固定版で管理し、ZIP全体をattestationの対象にします。

## 実装済みの仕組み

1. `tools/package_distribution_archive.ps1` が配布フォルダをZIP化し、`SHA256SUMS` を作ります。ZIP64対応のAPIを使うため、2GBを超える配布物も作れます。
2. `package-worker.yml` が、`actions/attest-build-provenance` でZIPと`SHA256SUMS`のattestationを作ります。OIDCを使うkeyless方式で、秘密鍵を持ちません。attestationを作るのは、`main` とリリースタグ(`v*`)から実行した場合だけです。未保護のブランチで変更したワークフローから、attestationを発行させないためです。
3. 同じワークフローで `gh attestation verify --signer-workflow` を実行し、attestationが生成元のワークフローと一致することを検証します。
4. `tools/verify_distribution_signing.ps1` が、`SHA256SUMS` と最終ZIPの一致を確認します。

注意: CIは `--source-ref` で実行時の参照元(`main` またはタグ)まで固定して検証します。利用者向けの手順(README)は、生成元のワークフローまでを検証し、公開リリースの配布物には `--source-ref refs/tags/v<版>` でタグまで固定する手順も載せています。

`verify_distribution_signing.ps1 -RequireRfc3161` は、署名のPKCS#7を読み、timestampがRFC 3161方式(OID `1.3.6.1.4.1.311.3.3.1`)であることを確認します。従来方式のtimestampは失敗にします。`Set-AuthenticodeSignature` の署名は従来方式、`signtool sign /tr` の署名はRFC 3161方式になることを、手元で確認済みです。

## 構築ワークフローの実行方法

`package-worker.yml` は約25分かかるため、PRでは自動実行せず、GitHub Actionsから手動(`workflow_dispatch`)で実行します。配布前の動作確認の扱いは、[作業開始からマージまで](../dev-guidelines/workflow.md)の「配布前の動作確認」に従います。

## 開発段階の署名(自己署名)

公開リリースまでは、手元で `tools/sign_distribution_self_signed.ps1` を実行し、自己署名証明書 `CN=Tomiya7688` で自社製バイナリに署名します。

- 証明書は、初回だけ `-CreateCertificate` を付けて、現在のユーザーの証明書ストア(`Cert:\CurrentUser\My`)に作ります。秘密鍵をPFXとして書き出したり、リポジトリへ置いたりしません。
- 署名後に `tools/update_distribution_manifest.ps1` で `distribution-manifest.json` のhashを更新し、`verify_distribution_signing.ps1 -AllowSelfSigned` で署名者とtimestampを検証します。
- 自己署名は信頼されたチェーンを持たないため、Windowsの発行元は「不明」のままです。公開配布の最終解にはしません。
- 手元の署名では、秘密鍵をPFXとして書き出しません。通常のビルド(PRや手動実行)のCIでは署名しません。
- SignPathの承認が下りるまでの間は、**リリース(タグ実行)に限り**、別に作ったリリース用の自己署名証明書(同じSubject)でCIが署名します。後述の「承認までの自己署名リリース」を参照してください。

## 公開リリースの署名とゲート(#62)

公開リリースでは、SignPath Foundation(OSS向けの無料コード署名)で署名します。承認が下りるまでは、自己署名のプレリリースを作ります。

### 流れ

タグ `v*` を push すると `package-worker.yml` が動き、次の順に進みます。どれか1つでも失敗すると、その時点で止まりGitHub Releaseは作られません。

1. 前提の確認: タグのコミットが `main` に含まれることと、署名方式の決定。`SIGNPATH_API_TOKEN`(secret)と `SIGNPATH_ORGANIZATION_ID`・`SIGNPATH_PROJECT_SLUG`(variables)が揃っていれば **SignPath**、無ければ `SELF_SIGN_PFX_BASE64`・`SELF_SIGN_PFX_PASSWORD`(secrets)による**自己署名**、どちらも無ければ失敗します。構築(約25分)の前に確認します。
2. 配布物を構築し、起動確認を行います。
3. 【SignPath】`tools/stage_signing_files.ps1 -Mode Collect` で自社製のexe/dllだけを取り出し、`actions/upload-artifact` でSignPathへ渡します(配布物全体は数GBのため送りません)。
4. 【SignPath】`signpath/github-action-submit-signing-request` で署名します。署名済みファイルは `stage_signing_files.ps1 -Mode Restore` が、取り出したものと同じ集合であることを確認して書き戻し、`update_distribution_manifest.ps1` でmanifestを更新します。【自己署名】secretの証明書を読み込み、`sign_distribution_self_signed.ps1` で署名します(PFXは読み込み後に削除します)。
5. ZIPと`SHA256SUMS`を作り、署名方式ごとのゲートを必須にします。SignPathは `verify_distribution_signing.ps1 -RequireAuthenticode -ExpectedSignerSubject "SignPath Foundation" -RequireRfc3161`(`-AllowSelfSigned` は使いません)。自己署名は `-ExpectedSignerSubject "CN=Tomiya7688" -ExpectedSignerThumbprint <変数SELF_SIGN_THUMBPRINT> -AllowSelfSigned` です。
6. Artifact Attestationを作って検証します。
7. 別ジョブ `release-draft`(`contents: write` と読み取り専用の `attestations: read` だけを持つ)が、SHA-256とattestationを再確認し、Releaseの**下書き**を作ります。自己署名の場合はプレリリースとし、説明に署名者のthumbprintを書きます。公開は人が行います。ZIPが2GiBを超える場合はReleaseへ添付できないため、このジョブは失敗します(分割は #72 で対応します)。

### 承認までの自己署名リリース

SignPath Foundationの承認を待たずにリリースを作れるよう、リポジトリオーナーが次を1回だけ行います。

```powershell
gh auth login   # 初回のみ
pwsh tools/new_release_self_signed_cert.ps1 -Repository tomiya7688/your_singer
```

このスクリプトは、リリース用の自己署名証明書(`CN=Tomiya7688`)を作り、Environment `release` を作成して保護(必須レビュアーは実行した本人、デプロイできるのはタグ `v*` だけ)を設定し、secret `SELF_SIGN_PFX_BASE64`・`SELF_SIGN_PFX_PASSWORD` と、variable `SELF_SIGN_THUMBPRINT` を登録します。秘密鍵とパスワードはファイルへ書かず、画面にも表示しません。`-DryRun` を付けると、何も登録せずに作成と確認だけを行います。

- 手元の開発用の証明書とは別の証明書です。秘密鍵はGitHubのEnvironment secretとして保存されるため、Environmentの保護が前提です。再実行すると証明書が作り直され、thumbprintが変わります。
- 一部だけ設定されている(SignPathの3項目の一部など)場合や、thumbprintの変数が空の場合は、ワークフローが失敗します。署名者は証明書のthumbprintまで固定して検証します。
- Environmentの保護の設定(GitHub APIの呼び出し)は、実際のGitHubでは未検証です。実行後に、リポジトリの Settings → Environments → release で、必須レビュアーとタグの制限を確認してください。
- 自己署名は信頼チェーンを持たないため、利用者のWindowsは発行元を「不明」と表示します。timestampは従来方式で、RFC 3161は要求しません(SignPathのリリースだけ要求します)。
- SignPathの設定(secret・variables)が揃うと、自動でSignPathの署名に切り替わります。切り替えたら、Environmentから `SELF_SIGN_*` を削除してください。

### オーナーが行う設定(未了)

- [ ] SignPath Foundationへ申請し、承認後にorganization・project・signing policy(slug: `release-signing`)・artifact configuration(slug: `initial`、内容は [`docs/signpath/artifact-configuration.xml`](./signpath/artifact-configuration.xml))を作る
- [ ] SignPath側のtrusted build systemにGitHub.comを追加し、このリポジトリに限定する
- [ ] GitHubのEnvironment `release` を作り、必須レビュアー(オーナー)を設定し、デプロイできるタグを `v*` に限定する。secret `SIGNPATH_API_TOKEN` と variables `SIGNPATH_ORGANIZATION_ID`・`SIGNPATH_PROJECT_SLUG` は、リポジトリ全体ではなくこのEnvironmentに置く。タグ実行のworkflowはタグのコミットの内容で動くため、Environmentの承認がないと、workflowを改変したタグ実行からtokenを読めないようにするためです
- [ ] タグ `v*` を作成できる人を限定するtag rulesetを設定する
- [ ] 最初の公開リリースの前に、SignPathが実際に出力する署名済みファイルの配置(`output-artifact-directory` の中身)で `Restore` が通ることを、テスト用タグで確認する
- [ ] 最初の証明書が発行されたら、署名者の確認(現在は Subject に `SignPath Foundation` を含むこと)に、証明書のsubject全体またはthumbprintの固定を追加する

署名済みの出力が想定と違う場合も、`Restore` が集合の不一致として失敗し、署名されていない配布物がリリースへ進むことはありません。

同じタグで `release-draft` を再実行するときは、先に作られた下書きReleaseを削除してください(既存のReleaseがあると `gh release create` は失敗します)。手動実行(`workflow_dispatch`)でタグを指定することはできません。署名なしの成果物にタグ起点のattestationが付くためです。

秘密鍵・PFX・パスワードはリポジトリにコミットしません。SignPathのAPIトークンはGitHub Secretsに置きます。

## 既存成果物の確認

2026-10-25時点で、GitHub Releaseは公開されていません。CIが生成する配布物(`YourSinger-windows-x64`)は14日で失効する開発用の成果物で、公開配布物ではありません。
PR #53 より前に生成された配布物は未署名で、検証できません。公開リリースは、署名のゲートを通過したものだけにします。
