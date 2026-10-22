---
name: ys-review
description: your_singer の変更を コーディング規約.md と dev-guidelines/レビュー基準.md に照らしてレビューする。変更をコミット/PRにする前に使う。
---

# ys-review

1. `コーディング規約.md` と `dev-guidelines/レビュー基準.md` を読む。
2. `git diff HEAD` と未追跡ファイル(`git ls-files --others --exclude-standard`)を対象にする。
3. `pwsh tools/dev/check_file_size.ps1 -ChangedOnly` で行数基準を確認する。
4. 次の観点で指摘を作る:
   - 命名(`data`/`result`等の曖昧名、真偽値の `is/has/can/should`)
   - 宣言コメント(JSON-like Comment Outs形式、日本語キー、実装との一致)
   - 関数50/80行、ファイル300/500行
   - 境界条件・失敗時状態・キャンセル・再実行、パス結合・外部プロセス・削除/上書き
   - 仕様書・docsの用語/文体(利用者主語は「してください」、アプリ主語は「します」)と実装の一致
   - テスト・CI: 変更に対応するテストの有無、実行できなかった項目の明示
5. 各指摘は「該当箇所・再現条件・影響・必要な修正」を書く。主観だけで終えない。
6. 問題が無ければ、確認した範囲と未確認の範囲を明記して承認する。
