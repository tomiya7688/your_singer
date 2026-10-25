# コンテキスト案内の保守

指示・skill・確認先を追加するときだけ参照します。[ai-context-reducer](https://github.com/tomiya7688/ai-context-reducer)の「機械で先に探す・手順を再利用する・必要箇所だけ読む」をこのリポジトリへ適用します。

## 正本と置き場所

| 内容 | 保存先 | 読む条件 |
| --- | --- | --- |
| 常時必要な共同作業ルール | `AGENTS.md` | 作業開始時 |
| 製品固有の入口・呼出方法 | `CLAUDE.md`、`.github/copilot-instructions.md` | 対応する製品 |
| 繰り返す作業の選択と短い手順 | 該当する`SKILL.md` | descriptionと依頼が一致するとき |
| 詳細な作業・実行手順と担当の案内 | `dev-guidelines/` | AGENTS/skillが示す該当工程 |
| 仕様と設計 | `docs/` | 対象機能を変更・確認するとき |
| 最新の作業状態 | 対象Issue/PR、ローカルは`artifacts/dev-handoff/` | 継続・担当交代時 |

共通規則の本文は一か所に置き、入口とskillから適用条件つきでリンクします。最新のテスト件数、長い障害履歴、今回限りの条件を恒久ルールへ足しません。
全プロジェクトのレビュー指示はAGENTSの共有ブロックを優先し、具体的なCLI操作・無料枠・読取制限は`E:/projects/REVIEW_OPERATIONS.md`で確認します。各製品の入口とskillへ同じ共有ブロックを複製しません。

## 大きくしない目安

- AGENTSは60行、製品別入口は15行、各SKILL.mdは40行程度までを目安にします。必要な規則を削って合わせず、条件付きの詳細を対応する資料へ移します。
- 案内資料は一つの役割に絞り、100行を超える前に重複・古い情報・分割の必要を見直します。全ファイル一覧や依存ライブラリの仕様を写しません。
- ファイル・シンボル・テストを移動した変更では、該当する案内行も更新します。版・テスト結果は元のコード・CI・lockで確認します。
- 引用した資料をすべて自動読込する設定、製品ごとの同内容のskillsコピー、同じ名前のskillの多重登録を増やしません。

## 整理後の確認

相対リンクとskillのfrontmatterを検査し、代表的な依頼から「最初の実装・テスト・確認方法」へ進めるか確かめます。リンク検査だけでは自動発見や実際のトークン削減を証明できません。各製品での自動読込は実機で確認した場合だけ報告します。

## 参照した手法

ローカル参照版: ai-context-reducer commit `9c189c3cbe4766d23d94ae57fc1910e4485941ed`。原文は、読む量を減らすための案内であり、このリポジトリのCIや安全条件を置き換えません。

- [読む情報を絞る3つの方法](https://github.com/tomiya7688/ai-context-reducer/blob/9c189c3cbe4766d23d94ae57fc1910e4485941ed/docs/jp/手法/AIに読む情報を絞る3つの方法.md)
- [作業場所に合った指示書](https://github.com/tomiya7688/ai-context-reducer/blob/9c189c3cbe4766d23d94ae57fc1910e4485941ed/docs/jp/手法/AIに作業場所に合った指示書を読ませる.md)
- [調査を止める条件](https://github.com/tomiya7688/ai-context-reducer/blob/9c189c3cbe4766d23d94ae57fc1910e4485941ed/docs/jp/手法/AIに調査を止めさせる条件を決める.md)
- [実装・テストから担当を探す](https://github.com/tomiya7688/ai-context-reducer/blob/9c189c3cbe4766d23d94ae57fc1910e4485941ed/docs/jp/手法/ファイルやフォルダーの担当を探す.md)
- [Copilotのskill保存先](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)、[Claudeのskillsと補助ファイル](https://code.claude.com/docs/en/skills)
