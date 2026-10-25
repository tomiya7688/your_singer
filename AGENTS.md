<!-- shared-review-operations:v2:2026-10-24 -->

## 全プロジェクトの作業レビューとPRレビュー（最新ユーザー指示）

Eドライブまたは `C:/Users/tomiy/games` にソースコードを持つ全プロジェクトに適用する。Learning language / Learning_language / lenguage_learningは除外する。規模・初期段階・Gitの有無で除外しない。この規則は旧「大規模だけ」と旧Luna/Haiku未実施時の停止条件を置き換える。
各作業の実装・関連テスト・自己レビュー後に、Codex `gpt-6-luna` low、Claude `haiku` low、Antigravity無料モデル、Jules無料モデル、Continue CLI無料モデル、Qwen Code無料モデル、Grok CLI無料モデルの7者へそれぞれレビューを依頼する。PR作成・更新時にはGitHub CopilotとCodeRabbitの両方にも依頼する。モデル直接API呼出しを指定CLIによる実施と偽らない。
全員について枠切れ・未応答・接続失敗・未ログイン・未導入・無料か不明なら理由を1行記録して省略し、作業を続ける。無応答は原則90秒（最大180秒）、非同期Jules/PRは原則10分の確認期限で打ち切り、連続再試行・枠待ち・課金・自動昇格をしない。省略を成功にせず、有効な指摘の修正・必須テスト・CI・承認条件は維持する。
AntigravityとGrokは、指定された対象・必要な周辺ファイルの読取を許可する。両者を含む全レビュアーに編集・削除・移動・状態変更・別レビュアー起動をさせない。秘密/認証ファイルは読取許可の対象外。`E:/projects/.review-private/` の読取・復号はCodex/Claude主担当だけが行い、秘密をモデル本文・ログ・Gitへ出さない。
具体的な資料作成、各CLI起動、読取専用設定、タイムアウト、省略、PR依頼、結果記録は `E:/projects/REVIEW_OPERATIONS.md` を参照し、設定済みやdry-runだけで実施済みと報告しない。
<!-- /shared-review-operations -->

<!-- post-work-formatter-policy:2026-10-25 -->

## フォーマッターの導入と作業後の自動実行

各プロジェクトの実装担当は、既存の言語・設定・依存管理を確認し、未導入なら適切なformatterを開発用依存/管理済みツールとして導入する。既存formatterを優先し、競合する追加やグローバル最新版への依存を避け、バージョン・設定・対象除外とformat/checkの1コマンド実行方法を整える。
各作業の実装後、今回変更したファイル/モジュールにformatterを自動的に実行し、その後にformat/check・関連テスト・自己レビュー・追加レビューを行う。指摘修正後も必要な再整形と再検証をする。担当外のdirty変更、生成物、vendor、原文保持が必要な教材/サンプルを一括整形しない。整形は実装担当が行い、レビュー専任AIには編集させない。
既存の開発/CI手順に適合する整形確認を整え、実行コマンド・バージョン・対象・結果を完了報告に残す。未実行は理由と残件を記録し、導入済み/設定済みだけで整形済みとしない。詳細は `E:/projects/FORMATTER_OPERATIONS.md`。Learning languageもこの整形指示の対象（複数AIレビューの除外とは別）。
<!-- /post-work-formatter-policy -->

<!-- review-disclosure-authorization:2026-10-25 -->

## レビュー資料送信の継続許可

ユーザーは今回および今後どのプロジェクトでも、レビュー用コード資料を指定7者の提供元OpenAI・Anthropic・Google・OpenRouter・xAIへ送ってレビューすることを明示許可済み。同じレビュー目的/送信先についてプロジェクトごとに再確認しない。秘密/認証値/利用者の非公開データは送らず、無料枠・モデル指定・編集禁止・Learning languageのレビュー除外は `E:/projects/REVIEW_OPERATIONS.md` に従う。
<!-- /review-disclosure-authorization -->

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

| 作業                                          | 最初の参照先                                                                                              |
| --------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| 対象の実装・テストを探す                      | [ys-context](.agents/skills/ys-context/SKILL.md)と[作業別の確認先](dev-guidelines/context-map.md)の該当行 |
| 作業開始・検証・レビュー・コミット・PR/マージ | [作業フロー](dev-guidelines/workflow.md)の該当工程                                                        |
| Python・.NET・ML・配布ビルドを実行する        | [実行環境](dev-guidelines/execution.md)                                                                   |
| 「次」「続けて」・担当交代・中断              | [引き継ぎ](dev-guidelines/handoff.md)と対象Issue/PRの最新記録                                             |
| 指示やskillを追加・整理する                   | [保守方針と参照元](dev-guidelines/context-maintenance.md)                                                 |

skillsを自動発見しない環境では、上のリンク先を直接読んでください。ファイルを読めないChatGPTには、依頼者が入口と該当資料を渡してください。リンク先を全件展開せず、今回必要な節から原典へ進みます。
