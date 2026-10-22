---
name: ys-finish
description: your_singer の作業終了フロー。検証→コミット→PR作成→CI確認→自己レビュー→問題なければsquashマージまでを AGENTS.md に従って行う。
---

# ys-finish

`AGENTS.md` の「作業の流れ」3〜5 を実行します。

1. `ys-check` で検証する。失敗したら直す。
2. `ys-review` で自分のdiffを `コーディング規約.md` に照らしてレビューし、指摘を直す。
3. `ys-commit` でコミットし、pushして `.github/pull_request_template.md` に沿ったPRを作る(`Closes #番号`)。自己レビュー結果をPR本文に書く。
4. `gh pr checks <PR> --watch` でCIの完了を待ち、結果を読む。失敗なら原因を直して再push。
5. CI全成功・自己レビュー指摘ゼロ・競合なしなら `gh pr merge <PR> --squash --delete-branch`。
6. `git checkout main && git pull --ff-only` で最新化する。

条件を満たさない場合はマージせず、PRに状況と引き継ぎ事項を書く。force pushとmainへの直接pushはしない。
