"""設計書・開発文書の機械的な照合。使い方: python tools/dev/check_docs.py

- 相対リンクの切れ
- decisions.md の状態が 確定/暫定/待ち/実装内 のどれかであること
- packets/ の全パケットが docs/design/README.md の索引から参照されていること
"""

import re
import sys
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
SCAN = ["AGENTS.md", "dev-guidelines", "docs/design"]
STATES = {"確定", "暫定", "待ち", "実装内"}


def markdown_files():
    for entry in SCAN:
        path = ROOT / entry
        if path.is_file():
            yield path
        else:
            yield from sorted(path.rglob("*.md"))


def check_links(errors):
    for file in markdown_files():
        text = re.sub(r"```.*?```", "", file.read_text(encoding="utf-8"), flags=re.S)
        for match in re.finditer(r"\[[^\]]*\]\(([^)\s]+)\)", text):
            target = match.group(1)
            if re.match(r"^(https?:|mailto:|#)", target):
                continue
            relative = unquote(target.split("#")[0])
            if relative and not (file.parent / relative).resolve().exists():
                errors.append(f"リンク切れ {file.relative_to(ROOT)}: {target}")


def check_decisions(errors):
    file = ROOT / "docs/design/decisions.md"
    if not file.exists():
        return
    for line in file.read_text(encoding="utf-8").splitlines():
        if re.match(r"^\| D-\d+ ", line):
            state = line.strip().strip("|").split("|")[-1].strip()
            if state not in STATES:
                errors.append(f"決定記録の状態が不正 ({state}): {line[:40]}")


def check_packet_index(errors):
    index = ROOT / "docs/design/README.md"
    packets = ROOT / "docs/design/packets"
    if not index.exists() or not packets.exists():
        return
    text = index.read_text(encoding="utf-8")
    for packet in sorted(packets.glob("*.md")):
        if packet.name not in text:
            errors.append(f"索引に無いパケット: {packet.name}")


def main():
    errors = []
    check_links(errors)
    check_decisions(errors)
    check_packet_index(errors)
    for message in errors:
        print(message)
    print(f"docs check: {len(errors)} error(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
