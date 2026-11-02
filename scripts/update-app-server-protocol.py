#!/usr/bin/env python3
"""Regenerate the packaged App Server method catalog from a checked-in schema snapshot.

Usage: scripts/update-app-server-protocol.py <version>

Reads  specs/codex-app-server/<version>/codex_app_server_protocol.schemas.json
Writes src/libs/tryAGI.Codex/AppServerProtocol.json and the version marker in README.md.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNIONS = {
    "clientRequests": "ClientRequest",
    "clientNotifications": "ClientNotification",
    "serverRequests": "ServerRequest",
    "serverNotifications": "ServerNotification",
}


def methods(definitions: dict, union: str) -> list[str]:
    variants = definitions[union].get("oneOf") or definitions[union].get("anyOf") or []
    names = []
    for variant in variants:
        method = variant.get("properties", {}).get("method", {})
        names.extend(method.get("enum") or ([method["const"]] if "const" in method else []))
    if not names:
        raise SystemExit(f"{union} lists no methods; the schema layout changed")
    return sorted(set(names))


def main() -> None:
    if len(sys.argv) != 2 or not re.fullmatch(r"\d+\.\d+\.\d+", sys.argv[1]):
        raise SystemExit("usage: update-app-server-protocol.py <major.minor.patch>")
    version = sys.argv[1]
    schema_path = ROOT / "specs" / "codex-app-server" / version / "codex_app_server_protocol.schemas.json"
    definitions = json.loads(schema_path.read_text(encoding="utf-8"))["definitions"]
    catalog = {"version": version}
    catalog.update({key: methods(definitions, union) for key, union in UNIONS.items()})
    output = ROOT / "src" / "libs" / "tryAGI.Codex" / "AppServerProtocol.json"
    output.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")

    readme = ROOT / "README.md"
    text = readme.read_text(encoding="utf-8")
    updated, count = re.subn(r"(<!-- app-server-version -->)[^<]*(<!-- /app-server-version -->)",
                             rf"\g<1>{version}\g<2>", text)
    if count == 0:
        raise SystemExit("README.md has no app-server-version marker")
    readme.write_text(updated, encoding="utf-8")
    print(f"App Server protocol catalog updated to {version}: "
          + ", ".join(f"{len(catalog[k])} {k}" for k in UNIONS))


if __name__ == "__main__":
    main()
