# 開発参加者向け資料

Claude Code、Codex、ChatGPT、Copilotと人が同じ原典を参照するための案内です。作業開始時は[AGENTS.md](../AGENTS.md)を確認し、以下から今回必要な資料だけを選んでください。

| 読む作業                        | 資料                                                                                  |
| ------------------------------- | ------------------------------------------------------------------------------------- |
| 実装・テストを探す              | [作業別の確認先](context-map.md)、[ys-context](../.agents/skills/ys-context/SKILL.md) |
| 開始・検証・レビュー・PR/マージ | [作業フロー](workflow.md)の該当工程                                                   |
| 変更ファイルの整形・整形確認    | [整形手順](formatting.md)                                                             |
| 実行場所・一時領域・学習環境    | [実行環境](execution.md)                                                              |
| 継続・担当交代                  | [引き継ぎ](handoff.md)                                                                |
| 規約に沿った実装・レビュー      | [コーディング規約](../コーディング規約.md)、[レビュー基準](レビュー基準.md)           |
| 指示・skillの追加や短縮         | [保守方針と参照元](context-maintenance.md)                                            |

## 作業を再利用するskills

| 作業             | 入口                                                |
| ---------------- | --------------------------------------------------- |
| 調査と再開       | [ys-context](../.agents/skills/ys-context/SKILL.md) |
| 検証             | [ys-check](../.claude/skills/ys-check/SKILL.md)     |
| 自己レビュー     | [ys-review](../.claude/skills/ys-review/SKILL.md)   |
| コミット         | [ys-commit](../.claude/skills/ys-commit/SKILL.md)   |
| PR作成からマージ | [ys-finish](../.claude/skills/ys-finish/SKILL.md)   |

Claude Codeは既存の`.claude/skills/`、Copilot CLIは[公式の対応先](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)に置いたskillsを利用できます。自動発見されないskillやファイル参照が必要な環境では、上のパスを直接指定してください。配置・リンク検査と、各製品による自動発見の実機確認は別です。

開発参加者向け資料はこのフォルダー、設計・仕様は`docs/`、利用者向け説明はREADMEへ置きます。共有ルールを各製品の入口へ複製しません。
