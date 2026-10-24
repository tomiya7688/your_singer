from __future__ import annotations

import argparse
import importlib.metadata
import json
import re
import sys
from pathlib import Path

# 証明書インシデント対応(#48)の最低版。lock・CI・配布ビルドはこの値以上を要求する。
MINIMUM_VERSIONS = {"pip": "26.2.2", "certifi": "2026.10.19"}
OPTIONAL_MINIMUM_VERSIONS = {"uv": "0.13.4", "sigstore": "4.5.1"}
# 問題対象の認証局名。CA bundleに残っていても、証明書の発行日などによる部分的な不信頼は
# bundleの形式で表せないため、ここでは失敗にせず記録だけ行う(実際の信頼判定は対象チェーンで確認する)。
EMSIGN_AUTHORITY_PATTERN = re.compile(r"emsign|emudhra", re.IGNORECASE)


# { 責務: [parse_version: 数字区切りの版を比較可能なタプルへ変換する] }
def parse_version(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", version.split("+")[0].split("-")[0]))


# {
#   責務: [find_emsign_roots: CA bundle内のemSign/eMudhra系の認証局を列挙する]
#   引数: [bundle_path: certifi.where() が指す実ファイル]
#   戻り値: [labels: emSign/eMudhra系に一致したIssuer/Subject/Labelコメント行]
# }
def find_emsign_roots(bundle_path: Path) -> list[str]:
    labels = []
    for line in bundle_path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith(("# Issuer:", "# Subject:", "# Label:")) and EMSIGN_AUTHORITY_PATTERN.search(line):
            labels.append(line.strip())
    return labels


# {
#   責務: [collect_report: 実行中のPythonが使うpip・certifi・CA bundleの状態を集める]
#   戻り値: [report: 版、CA bundleの実パス、問題対象の認証局、不足している条件]
# }
def collect_report() -> dict:
    report = {"python_version": sys.version.split()[0], "executable": sys.executable,
              "versions": {}, "certifi_bundle": None, "emsign_roots_in_bundle": [], "issues": []}
    for package, minimum in {**MINIMUM_VERSIONS, **OPTIONAL_MINIMUM_VERSIONS}.items():
        try:
            installed = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            if package in MINIMUM_VERSIONS:
                report["issues"].append(f"{package} が導入されていません(必要: {minimum} 以上)。")
            continue
        report["versions"][package] = installed
        if parse_version(installed) < parse_version(minimum):
            report["issues"].append(f"{package} {installed} は {minimum} 未満です。")
    if "certifi" in report["versions"]:
        import certifi
        bundle_path = Path(certifi.where())
        report["certifi_bundle"] = str(bundle_path)
        report["emsign_roots_in_bundle"] = find_emsign_roots(bundle_path)
    return report


# { 責務: [main: 結果をJSONで表示し、条件を満たさなければ終了コード1で終わる] }
def main() -> None:
    argparse.ArgumentParser(description="Pythonのpip・certifi・CA bundleが最低版を満たすか確認する").parse_args()
    report = collect_report()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    sys.exit(1 if report["issues"] else 0)


if __name__ == "__main__":
    main()
