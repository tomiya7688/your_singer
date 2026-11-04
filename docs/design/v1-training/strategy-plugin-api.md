# Modユーザーと学習Provider開発者向けガイド（v1）

この文書は **導入の入口**。実装が従うべき仕様は [学習システム基本・詳細設計](learning-system-design.md) と [学習戦略・Provider詳細プロトコル](learning-strategy-protocol.md)、機械検証用の [JSON Schema](schema/) を正とする。過去の概念例と実際に読み込めるManifestを混同しない。

## 1. Modでできること

**質感を追加したい場合:** [preset.jsonの例](examples/mod-classic/preset.json) の日本語表示名、目標traits、必要な制御能力を変更して保存する。C#やPythonは不要。

**既存の学習方法を組み合わせたい場合:** [strategy.jsonの例](examples/mod-classic/strategy.json) の入力条件、対象target、依存関係のあるstage一覧を指定する。使用可能なoperationはアプリの `OperationRegistry` で確認し、存在しないOperationは `unsupported` とする。

**自分の学習モデルを実行したい場合:** 別プロセスのworker、`provider.json`、必要なモデル資源とライセンス情報をセットにして登録する。アプリの再ビルドは不要だが、互換API・資源hashと明示的なコード有効化が必要。

## 2. インストール形式（Windowsの設計）

```text
%LOCALAPPDATA%/YourSinger/mods/
└─ community.my-pack/
    ├─ preset.json         # 省略可: 質感の追加
    ├─ strategy.json       # 省略可: 学習レシピの追加
    ├─ provider.json       # 独自コードを使うときのみ
    ├─ resources.json      # 外部モデルがあるとき
    ├─ LICENSE
    └─ worker/             # 独自学習アルゴリズムがあるとき
```

アプリ上で「拡張機能を再読み込み」して新しい宣言を検証する。宣言Modの読込で任意コードを実行しない。コード入りProviderは**出所・要求権限を見せて明示的に有効化**する。別プロセス起動だけでOS上の安全なsandboxになるわけではない。

## 3. 登録状態の区別

| 状態 | 意味 |
|---|---|
| 利用可能 | 登録済みOperation、実行資源、必要な入力役割が揃っている |
| 一部対応 | 希望の任意traitsを全ては実現できない |
| 資源不足 | 必要なモデル/VRAM/依存が満たされない |
| 未対応 | 不明Operation・出力形式・Provider能力が足りない |
| 無効 | 明示的に無効化されている |
| 不正 | schema、ID衝突、依存循環、hash等が不正 |

「利用可能」は、**実際に目標の音声が完成した**という意味ではない。生成後の推論・互換性・質感試聴による検証は別である。

## 4. 学習プランナーへの連携

登録されたpresetは **ユーザーが選ぶ生成目的・質感**。学習方法は、対象話者の会話教師/歌唱教師/声質参照の実効量と多様性から `TrainingStrategyRegistry` が候補を列挙する。

Plannerは必要なStageを選び組み合わせるが、歌唱しかないから勝手に歌唱モデルを作ることはない。歌唱由来の本人声質を参照し、TTS基盤が別途発話能力を提供できれば、**トークモデルを目指すプラン**を作る。最終的に再利用可能なモデル形式へ出せない場合は未対応とする。

## 5. Manifestの検証項目

- `schema_version`、IDとSemVer、targets、traits、Stage入出力型・依存DAG
- Stageに必要な独立教師データと声質参照があること（未知値を0へ変換しない）
- 実在するOperationの利用、ModelPackageの出力能力とExporter互換性
- Worker資源（runtime/checkpointを含む）のhash・別ライセンス・パス検査
- 入力/出力の領域外参照拒否、取消・異常終了、旧JobのProvider pinning

実行仕様・エラーメッセージの形式、バージョン互換・安全性は [詳細契約](learning-strategy-protocol.md) を参照する。

## 6. サンプルについて

[examples/README.md](examples/README.md) に示すOperation IDは設計上の例であり、現行mainでは実装されていない。**サンプルManifestを配置しただけで実際に学習できると表示してはいけない**。Preset単独登録と、Trainerを動かす能力は別の段階で検証する。

利用者に別Python/.NETランタイムの導入を求めないこと、YourSinger.exeを単一起動口とすること、UPD責務を維持することは通常のProviderでも変わらない。
