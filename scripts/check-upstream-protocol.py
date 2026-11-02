#!/usr/bin/env python3
"""Compare the reviewed upstream Rust protocol sources with a newer openai/codex release.

ProtocolBaseline.json records a SHA-256 fingerprint and the literal wire symbols
(`session.*`, `response.*`, `input_audio.*`, ...) of every codex-rs file the native
Responses and realtime clients were reviewed against. A changed source needs review;
it does not by itself prove an incompatibility.

Usage:
  scripts/check-upstream-protocol.py check [--tag rust-vX.Y.Z] [--report FILE]
      Exits 0 and prints "unchanged" when nothing differs, exits 3 when a review is
      needed (the Markdown report goes to FILE or stdout). The newest stable tag is
      used by default. Writes tag=... and changed=... to $GITHUB_OUTPUT when set.
  scripts/check-upstream-protocol.py update --tag rust-vX.Y.Z
      After review: rewrites the fingerprints, wire symbols, reviewedRelease and
      upstreamCommit together.
  scripts/check-upstream-protocol.py --self-test
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASELINE = ROOT / "src" / "libs" / "tryAGI.Codex" / "ProtocolBaseline.json"
REPOSITORY = "https://github.com/openai/codex"
RAW = "https://raw.githubusercontent.com/openai/codex"
STABLE_TAG = re.compile(r"rust-v(\d+)\.(\d+)\.(\d+)")
WIRE_SYMBOL = re.compile(
    r'"((?:session|response|conversation|input_audio|output_audio|input_transcript|'
    r'output_transcript|delegation|turn)(?:\.[a-z_]+)+)"')
NEEDS_REVIEW = 3


def wire_symbols(source: str) -> list[str]:
    return sorted(set(WIRE_SYMBOL.findall(source)))


def stable_tags() -> dict[str, str]:
    """Maps each stable rust-vX.Y.Z tag to its commit (annotated tags are peeled)."""
    output = subprocess.run(["git", "ls-remote", "--tags", REPOSITORY, "rust-v*"],
                            check=True, capture_output=True, text=True).stdout
    tags: dict[str, str] = {}
    for line in output.splitlines():
        commit, ref = line.split("\t")
        name = ref.removeprefix("refs/tags/")
        peeled = name.endswith("^{}")
        name = name.removesuffix("^{}")
        if STABLE_TAG.fullmatch(name) and (peeled or name not in tags):
            tags[name] = commit
    if not tags:
        raise SystemExit("openai/codex lists no stable rust-v* tags")
    return tags


def newest(tags: dict[str, str]) -> str:
    return max(tags, key=lambda tag: tuple(int(part) for part in STABLE_TAG.fullmatch(tag).groups()))


def fetch(commit: str, path: str) -> bytes | None:
    try:
        with urllib.request.urlopen(f"{RAW}/{commit}/{path}", timeout=60) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def compare(baseline: dict, commit: str) -> tuple[dict[str, bytes | None], list[dict]]:
    sources = {path: fetch(commit, path) for path in baseline["sourceFiles"]}
    changes = []
    for path, fingerprint in baseline["sourceFiles"].items():
        content = sources[path]
        if content is None:
            changes.append({"path": path, "missing": True})
            continue
        if hashlib.sha256(content).hexdigest() == fingerprint:
            continue
        before = set(baseline["wireSymbols"].get(path, []))
        after = set(wire_symbols(content.decode("utf-8")))
        changes.append({"path": path, "missing": False,
                        "added": sorted(after - before), "removed": sorted(before - after)})
    return sources, changes


def report(baseline: dict, tag: str, commit: str, changes: list[dict]) -> str:
    reviewed = baseline["reviewedRelease"]
    lines = [
        f"`{tag}` changed {len(changes)} of the {len(baseline['sourceFiles'])} upstream files "
        f"the native clients were reviewed against (`{reviewed}`; "
        f"[full diff]({REPOSITORY}/compare/{reviewed}...{tag})).",
        "",
        "| File | Wire symbols added | Wire symbols removed |",
        "|------|--------------------|----------------------|",
    ]
    for change in changes:
        link = f"[`{change['path']}`]({REPOSITORY}/blob/{commit}/{change['path']})"
        if change["missing"]:
            lines.append(f"| `{change['path']}` | **file removed or moved** | |")
        else:
            lines.append(f"| {link} | "
                         f"{', '.join(f'`{s}`' for s in change['added']) or '—'} | "
                         f"{', '.join(f'`{s}`' for s in change['removed']) or '—'} |")
    lines += [
        "",
        "A source change needs review; it does not by itself prove an incompatibility. "
        "After reviewing the client code against these files, record the review with:",
        "",
        "```bash",
        f"python3 scripts/check-upstream-protocol.py update --tag {tag}",
        "```",
    ]
    return "\n".join(lines) + "\n"


def set_output(**values: str) -> None:
    if path := os.environ.get("GITHUB_OUTPUT"):
        with open(path, "a", encoding="utf-8") as output:
            output.writelines(f"{key}={value}\n" for key, value in values.items())


def check(tag: str | None, report_path: str | None) -> int:
    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    tags = stable_tags()
    tag = tag or newest(tags)
    if tag not in tags:
        raise SystemExit(f"{tag} is not a stable openai/codex tag")
    if tag == baseline["reviewedRelease"]:
        print(f"{tag} is the reviewed release: unchanged")
        set_output(tag=tag, changed="false")
        return 0
    _, changes = compare(baseline, tags[tag])
    set_output(tag=tag, changed="true" if changes else "false")
    if not changes:
        print(f"{tag}: every reviewed source is byte-identical to {baseline['reviewedRelease']}: unchanged")
        return 0
    text = report(baseline, tag, tags[tag], changes)
    if report_path:
        Path(report_path).write_text(text, encoding="utf-8")
        print(f"{tag}: {len(changes)} reviewed source(s) changed; report written to {report_path}")
    else:
        print(text, end="")
    return NEEDS_REVIEW


def update(tag: str) -> None:
    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    tags = stable_tags()
    if tag not in tags:
        raise SystemExit(f"{tag} is not a stable openai/codex tag")
    commit = tags[tag]
    sources, changes = compare(baseline, commit)
    if missing := [change["path"] for change in changes if change["missing"]]:
        raise SystemExit("Removed or moved upstream files must be replaced in ProtocolBaseline.json by hand first: "
                         + ", ".join(missing))
    baseline["upstreamCommit"] = commit
    baseline["reviewedRelease"] = tag
    for path, content in sources.items():
        baseline["sourceFiles"][path] = hashlib.sha256(content).hexdigest()
        baseline["wireSymbols"][path] = wire_symbols(content.decode("utf-8"))
    BASELINE.write_text(json.dumps(baseline, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"ProtocolBaseline.json now records {tag} ({commit}); {len(changes)} source(s) changed")


def self_test() -> None:
    source = '''
        #[serde(rename = "session.update")]
        json!({"type": "conversation.item.truncate", "item_id": id});
        const A: &str = "input_audio_buffer.append"; // a different, unwatched prefix
        let state = "realtime_conversation.running_state";
        "response.output_audio.delta" "response.output_audio.delta" "turn"
    '''
    assert wire_symbols(source) == ["conversation.item.truncate", "response.output_audio.delta", "session.update"], \
        wire_symbols(source)
    assert newest({"rust-v0.9.0": "a", "rust-v0.10.0": "b", "rust-v0.165.0": "c"}) == "rust-v0.165.0"
    assert not STABLE_TAG.fullmatch("rust-v0.166.0-alpha.1")
    baseline = {"reviewedRelease": "rust-v1.0.0", "sourceFiles": {"a.rs": "", "b.rs": ""}}
    text = report(baseline, "rust-v1.1.0", "abc", [
        {"path": "a.rs", "missing": False, "added": ["session.new"], "removed": []},
        {"path": "b.rs", "missing": True}])
    assert "`session.new`" in text and "file removed or moved" in text and "update --tag rust-v1.1.0" in text
    print("self-test passed")


def main() -> None:
    if sys.argv[1:] == ["--self-test"]:
        self_test()
        return
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    check_parser = commands.add_parser("check")
    check_parser.add_argument("--tag")
    check_parser.add_argument("--report")
    update_parser = commands.add_parser("update")
    update_parser.add_argument("--tag", required=True)
    arguments = parser.parse_args()
    if arguments.command == "check":
        sys.exit(check(arguments.tag, arguments.report))
    update(arguments.tag)


if __name__ == "__main__":
    main()
