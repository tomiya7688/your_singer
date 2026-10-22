# AGENTS.md — 共同作業の入口

Claude Code、Codex、ChatGPT、Copilotと人が共有する正本です。製品別の入口とskillsへ同じ規則を複製しません。

## 常に守ること

- 実装時は[コーディング規約](コーディング規約.md)、レビュー時は[レビュー基準](dev-guidelines/レビュー基準.md)に従います。例外の理由はPRに記載します。
- 開始時に`git pull --ff-only`を実行し、対象IssueのコメントとオープンPRを確認します。失敗した場合は最新性を未確認と記録します。
- 1ブランチは1Issueです。他者の変更を保持し、今回のファイルだけをstageします。
- 作業終了時はPRを作成し、最新headのCI成功・自己レビュー解決・競合なしを確認してsquashマージします。手順は[作業フロー](dev-guidelines/workflow.md)に従います。
- force push・mainへの直接push・他者のPRの無断マージを行いません。
- 実行していない検証を確認済みと書きません。fixture、実モデル、実音声、GUI、CIの根拠をそれぞれ記録します。
- このWindows端末では`E:\projects\your_singer`で作業し、一時ファイル・cache・非公開素材・生成物もE:内のGit無視対象へ置きます。実行前に[実行環境](dev-guidelines/execution.md)を参照してください。
- 開発者向け資料は`dev-guidelines/`、設計・仕様は`docs/`、利用者向け説明はREADMEへ置きます。

## 必要なときだけ読む

| 作業 | 最初の参照先 |
| --- | --- |
| 対象の実装・テストを探す | [ys-context](.agents/skills/ys-context/SKILL.md)と[作業別の確認先](dev-guidelines/context-map.md)の該当行 |
| 作業開始・検証・レビュー・コミット・PR/マージ | [作業フロー](dev-guidelines/workflow.md)の該当工程 |
| Python・.NET・ML・配布ビルドを実行する | [実行環境](dev-guidelines/execution.md) |
| 「次」「続けて」・担当交代・中断 | [引き継ぎ](dev-guidelines/handoff.md)と対象Issue/PRの最新記録 |
| 指示やskillを追加・整理する | [保守方針と参照元](dev-guidelines/context-maintenance.md) |

skillsを自動発見しない環境では、上のリンク先を直接読んでください。ファイルを読めないChatGPTには、依頼者が入口と該当資料を渡してください。リンク先を全件展開せず、今回必要な節から原典へ進みます。
