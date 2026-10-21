---
name: ys-check
description: your_singer の変更後にCI相当の検証(行数基準・Pythonワーカー・.NETビルド/回帰テスト)をローカルで実行し、結果を報告する。
---

# ys-check

1. `pwsh tools/dev/check.ps1` を実行します。長いので `run_in_background` を使います。
   - 絞り込み: `-SkipDotnet` / `-SkipWorker` / `-SkipSize`
   - Python側だけの変更なら `-SkipDotnet`、C#のみなら `-SkipWorker` を使ってよい。
2. 失敗したステップは、出力からテスト名・原因を特定して報告します。
3. 報告ルール(`dev-guidelines/レビュー基準.md`準拠):
   - 実行しなかったステップと理由を明記する。
   - fixtureやモックの成功を、実モデル・実音声・GUIでの成功と言わない。
4. 行数チェックで「要分割」(500行超)が出た場合は、責務ごとの分割を提案する。

補足: numpy が無い環境では `artifacts/dev-venv` に検証用venvが自動作成されます。
