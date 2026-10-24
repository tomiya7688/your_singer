# Pythonのpip・証明書bundle管理

## 目的

配布物の構築に使うPythonで、古いpipや古いCA bundle(certifi)が混ざらないようにします。
証明書インシデント対応(#48)で、次の最低版を全ての構築経路に要求します。

| パッケージ | 最低版 | 用途 |
| --- | --- | --- |
| pip | 26.2.2 | 依存の取得 |
| certifi | 2026.10.19 | pipやPython製ツールが参照するCA bundle |
| uv | 0.13.4 | 現在は使用していません |
| sigstore | 4.5.1 | 現在は使用していません |

uvとsigstoreは、リポジトリ内のスクリプト・CI・lockのいずれにも登場しないことを確認済みです。導入する場合は上の最低版以上にしてください。

## 確認方法

構築に使うPythonで次を実行します。

```powershell
python -I tools/verify_python_trust.py
```

実行中のPythonの版、pipとcertifiの版、`certifi.where()`が指すCA bundleの実ファイル、bundle内のemSign/eMudhra系の認証局を表示します。
最低版を満たさない場合は終了コード1で終わります。

## 反映先

- `tools/diffsinger-training-requirements.lock.txt` と `tools/style-bert-vits2-training-requirements.lock.txt`: pipとcertifiの版を固定します。
- `tools/build_diffsinger_runtime.ps1` と `tools/build_style_bert_vits2_runtime.ps1`: 他の依存を取得する前にpip 26.2.2へ更新し、構築後に`verify_python_trust.py`で検証します。
- `tools/build_ml_worker.ps1` と worker の `pyproject.toml`: pipとcertifiを最低版以上にし、構築後に検証します。
- `.github/workflows/build.yml`、`package-worker.yml`: CI上のPythonとMFA用Conda環境を最低版へ更新し、検証します。
- `tools/dev/check.ps1`: ローカル検証用のvenvを最低版で作ります。

## 実際に参照するCA bundleの扱い

最低版のcertifiでも、bundleにはemSign/eMudhra系のroot証明書が残っています(2026.10.19 と 2026.10.23 で確認)。
certifiのbundleは「発行日以降の証明書だけを信頼しない」といった部分的な不信頼を表せないため、`verify_python_trust.py`は、これらを失敗にせず記録だけ行います。
問題対象の証明書チェーンが実際に信頼されないことの確認は、対象チェーンが特定できた時点で、その証明書を使ったTLS接続で別途確認してください。

## 構築済みの成果物

`artifacts/` 配下の構築済みruntimeは、本変更より前に作られたものは最低版を満たしません。再構築すると、更新後のスクリプトで最低版になります。
