# 設計例の位置づけ

このディレクトリは [学習システム設計書](../learning-system-design.md) と [Mod/Provider詳細契約](../learning-strategy-protocol.md) に対応する**設計上のサンプル**です。

`mod-classic/preset.json` はコード不要のプリセット宣言例です。`mod-classic/strategy.json` は登録済みOperationを並べる記述例ですが、ここに記載した `builtin.load_tts_base`、`builtin.adapt_identity`、`builtin.verify_talk_model` は**概念上のOperation ID**です。現行mainに実装済みではありません。OperationRegistryが実際にそれらを提供するまでは使用可能にしてはいけません。

`planner-contract-cases.json` は、入力条件が変わっても破ってはならない目的保持・教師/声質分離・互換性チェックの設計テストです。秒数は実学習の品質や必要最低量を実証したものではありません。

`../schema/` のJSON Schemaと合わせて、実装時のvalidator・回帰テスト・ユーザー向けMod作成資料の基礎として利用します。利用可能なBackendが存在しないケースは `unsupported` を返すことが正解です。
