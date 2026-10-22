---
name: ys-check
description: your_singerの変更に応じたローカル検証を選び、共通チェックの結果と未実行範囲を記録する。
---

# 変更範囲を検証する

[実行環境](../../../dev-guidelines/execution.md)で一時領域を設定し、[検証手順](../../../dev-guidelines/workflow.md#check)を実行します。
長時間処理には利用環境の非同期実行を使い、終了コードとログの場所を記録します。
検証項目・省略理由・実モデル等との区別は手順の正本に従います。
