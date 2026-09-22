# テストの実行

設計と未検証の範囲は `docs/implementation-v1-14.md` を参照する。

## .NET回帰テスト

```sh
dotnet test tests/YourSinger.Process.Tests/YourSinger.Process.Tests.csproj --configuration Release --filter "Category!=FfmpegIntegration"
```

データ境界の28件に加え、実子プロセスを起動する4件がある。プロセス境界テストではPython 3.11の実行ファイルを`YOURSINGER_TEST_PYTHON`へ指定する。
未指定の4件はスキップされる。CIはWindows / Linuxの双方で指定し、スキップせず実行する。
テスト対象は大量の標準エラー出力、応答ID不一致、非ゼロ終了、キャンセル時のワーカー停止。
これは長時間の実モデル学習・GUI中断の試験ではない。

## 実FFmpeg入力テスト

```sh
YOURSINGER_TEST_FFMPEG=ffmpeg dotnet test tests/YourSinger.Process.Tests/YourSinger.Process.Tests.csproj --configuration Release --filter "Category=FfmpegIntegration"
```

単一音声、フォルダ内の破損音声を含む継続処理、未変更音声の再利用の3件。CIのLinuxジョブで実行する。

## Pythonワーカー通信

```sh
python -m unittest discover -s tests/worker -v
```

標準ライブラリだけで8件を実行する。ML依存・重みを導入しない。
ワーカーは選択されたコマンドの依存だけを読み込み、ライブラリのログは標準エラーへ流す。
C#クライアントは標準出力・標準エラーを並行して読み取り、要求IDと終了コードを検証する。

各ケースは一時ディレクトリを使用する。実学習済みモデルの重み・ユーザー録音はテスト資産に含めない。
開発用のPython指定はテスト専用であり、配布アプリの既定起動先は引き続き同梱`YourSinger.ML`である。
