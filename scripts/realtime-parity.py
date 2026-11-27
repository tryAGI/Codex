#!/usr/bin/env python3
"""Pinned Rust GPT Live parity inventory and fail-closed acceptance report.

Only refresh reads upstream. The checked-in JSON and check/report are offline.
Extraction yields candidates, not reviewed applicability or conformance.
"""
import argparse
import collections
import concurrent.futures
import hashlib
import json
import re
import subprocess
import urllib.request
from pathlib import Path
from realtime_parity_evidence import source_is_clean, source_revision, validate_receipt

ROOT = Path(__file__).resolve().parent.parent
MATRIX = ROOT / "docs/realtime-parity-matrix.json"
REVIEWS = ROOT / "docs/realtime-parity-reviews.json"
PIN = "3d86e43b075adeba2193ec5c22ac92f55afd82e6"
TAG = "rust-v0.172.0"
LIB = "src/libs/tryAGI.Codex/"
TEST = "src/tests/tryAGI.Codex.Tests/"
WIRE = re.compile(r'"((?:session|response|conversation|input_audio_buffer|input_audio|output_audio|input_transcript|output_transcript|delegation|turn|thread/realtime)(?:[./][a-z_]+)+|error)"')
TEST_FN = re.compile(r"(?m)^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+([a-zA-Z_]\w*)\s*\(")
FIELD = re.compile(r"(?m)^\s*pub\s+([a-z_]\w*)\s*:")
TYPE = re.compile(r"(?m)^\s*pub(?:\([^)]*\))?\s+(?:struct|enum)\s+([A-Z]\w*)")
FUNCTION = re.compile(r"(?m)^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+([a-zA-Z_]\w*)\s*\(")
STATUSES = {"unverified", "source-reviewed", "missing", "divergent", "passed", "failed", "skipped", "not-applicable"}
LANES = {"wire", "behavior", "live"}
EXACT = {
    "codex-rs/core/src/client.rs", "codex-rs/codex-api/src/common.rs",
    "codex-rs/app-server-protocol/src/protocol/common.rs",
    "codex-rs/app-server-protocol/src/protocol/v2/realtime.rs",
    "codex-rs/app-server-protocol/src/protocol/v2/realtime_history.rs",
    "codex-rs/codex-api/src/endpoint/responses.rs",
    "codex-rs/codex-api/src/requests/responses.rs",
    "codex-rs/codex-api/src/responses_headers.rs",
    "codex-rs/codex-api/src/sse/responses.rs",
    "codex-rs/codex-api/src/sse/responses_error.rs",
    "codex-rs/codex-api/src/sse/responses_error_parser_tests.rs",
    "codex-rs/codex-api/src/sse/responses_error_tests.rs",
    "codex-rs/config/src/config_toml.rs",
    "codex-rs/config/src/types.rs",
    "codex-rs/core/src/config/mod.rs",
    "codex-rs/protocol/src/protocol.rs", "codex-rs/protocol/src/models.rs",
    "codex-rs/core/tests/suite/client.rs",
    "codex-rs/core/tests/suite/client_websockets.rs",
    "codex-rs/core/tests/suite/client_websockets_runtime_auth.rs",
    "codex-rs/core/tests/suite/websocket_fallback.rs",
    "codex-rs/core/tests/suite/websocket_fallback_close.rs",
    "codex-rs/core/tests/suite/responses_api_proxy_headers.rs",
    "codex-rs/core/tests/suite/responses_system_proxy.rs",
    "codex-rs/core/tests/suite/token_usage_rollout.rs",
    "codex-rs/core/tests/suite/compact.rs",
    "codex-rs/core/tests/suite/compact_resume_fork.rs",
    "codex-rs/core/tests/suite/scenarios_compaction_tests.rs",
    "codex-rs/core/tests/suite/step_settings_compaction.rs",
}
# Each file family has an implementation owner and candidate C# locations.
# These are navigation hints, not individual verified mappings.
GROUPS = [
    ("app-server-protocol/", 48, "CodexThreadRpc.cs", "ThreadRpcTests.cs"),
    ("/config/", 29, "CodexRealtimeProtocol.cs", "NativeProtocolTests.cs"),
    ("responses_headers", 43, "CodexResponsesIdentity.cs", "CodexResponsesIdentityTests.cs"),
    ("/sse/responses_error", 47, "CodexUsageObserver.cs", "CodexUsageObserverTests.cs"),
    ("/sse/responses", 44, "CodexResponses.cs", "CodexResponsesIdentityTests.cs"),
    ("/endpoint/responses.rs", 44, "CodexResponses.cs", "CodexResponsesIdentityTests.cs"),
    ("/requests/responses.rs", 44, "CodexResponses.cs", "CodexResponsesIdentityTests.cs"),
    ("realtime_websocket/protocol", 31, "CodexRealtimeProtocol.cs", "RealtimeGaProtocolTests.cs"),
    ("realtime_websocket/methods/sideband", 32, "CodexRealtimeConnection.cs", "SidebandTests.cs"),
    ("realtime_websocket/methods", 29, "CodexRealtimeProtocol.cs", "NativeProtocolTests.cs"),
    ("realtime_call", 27, "CodexClient.cs", "NativeProtocolTests.cs"),
    ("realtime_attachment", 32, "CodexRealtimeConnection.cs", "SidebandTests.cs"),
    ("realtime_sideband", 42, "CodexRealtimeConnection.cs", "SidebandRetryTests.cs"),
    ("realtime_history", 41, "CodexThreadStore.cs", "ThreadAgentTests.cs"),
    ("realtime_handoff", 35, "CodexThreadAgent.cs", "ThreadAgentTests.cs"),
    ("realtime_delegation", 36, "CodexRealtimeStartupContext.cs", "RealtimeStartupContextTests.cs"),
    ("realtime_initial_items", 36, "CodexRealtimeInitialItems.cs", "RealtimeStartupContextTests.cs"),
    ("realtime_context", 36, "CodexRealtimeStartupContext.cs", "RealtimeStartupContextTests.cs"),
    ("realtime_conversation/bem", 37, "CodexRealtimeConnection.cs", "RealtimeFeedbackCorrelationTests.cs"),
    ("realtime_conversation/sideband", 42, "CodexRealtimeConnection.cs", "SidebandTests.cs"),
    ("realtime_conversation/existing_call", 32, "CodexRealtimeConnection.cs", "SidebandTests.cs"),
    ("realtime_conversation/transcript_tail", 30, "CodexRealtimeProtocol.cs", "NativeProtocolTests.cs"),
    ("app-server/", 48, "CodexThreadRpc.cs", "ThreadRpcTests.cs"),
    ("protocol/src/realtime", 41, "CodexThreadDocument.cs", "ThreadAgentTests.cs"),
    ("realtime_conversation", 34, "CodexThreadAgent.cs", "ThreadAgentTests.cs"),
    ("realtime", 28, "CodexRealtimeConnection.cs", "NativeProtocolTests.cs"),
    ("compact_remote_v2_installation", 46, "CodexCompaction.cs", "CodexCompactionTests.cs"),
    ("compact", 45, "CodexCompaction.cs", "CodexCompactionTests.cs"),
    ("responses_websocket", 44, "CodexResponses.cs", "CodexResponsesIdentityTests.cs"),
    ("websocket", 44, "CodexResponses.cs", "CodexResponsesIdentityTests.cs"),
    ("usage", 47, "CodexUsageObserver.cs", "CodexUsageObserverTests.cs"),
    ("client", 43, "CodexResponsesIdentity.cs", "CodexResponsesIdentityTests.cs"),
    ("protocol/src/models", 44, "CodexResponses.cs", "NativeProtocolTests.cs"),
    ("protocol/src/protocol", 31, "CodexRealtimeProtocol.cs", "NativeProtocolTests.cs"),
    ("common.rs", 43, "CodexResponses.cs", "CodexResponsesIdentityTests.cs"),
]

def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "tryAGI-Codex-parity"})
    with urllib.request.urlopen(req, timeout=45) as response:
        return response.read()

def selected(path):
    if path in EXACT:
        return True
    roots = ("codex-rs/core/", "codex-rs/codex-api/", "codex-rs/protocol/", "codex-rs/app-server/")
    return path.startswith(roots) and path.endswith(".rs") and any(
        name in path for name in ("realtime", "responses_websocket", "compact_remote")
    )

def candidate(path):
    for fragment, issue, implementation, test in GROUPS:
        if fragment in path:
            return issue, [LIB + implementation], [TEST + test]
    return 54, [], []

def url(path, line):
    return f"https://github.com/openai/codex/blob/{PIN}/{path}#L{line}"

def line_number(source, offset):
    return source.count("\n", 0, offset) + 1

def row(path, line, kind, symbol, lane, owner=None, applicability="pending"):
    issue, implementations, tests = candidate(path)
    return {
        "id": f"{kind}:{path.removeprefix('codex-rs/')}:{symbol}",
        "lane": lane, "kind": kind, "symbol": symbol,
        "source": url(path, line), "ownerIssue": owner or issue,
        "implementationCandidates": implementations, "testCandidates": tests,
        "applicability": applicability, "status": "unverified",
        "implementation": None, "test": None, "evidence": None,
    }

def scan(path, source):
    rows = {}
    realtime_wire = "/realtime_websocket/" in path and "/protocol" in path and not path.endswith("_tests.rs")
    if realtime_wire or path.endswith("/realtime_call.rs") or "responses_websocket" in path or path.endswith("/common.rs") or "/sse/responses" in path or "/requests/responses" in path or "app-server-protocol/" in path:
        for m in WIRE.finditer(source):
            r = row(path, line_number(source, m.start()), "wire-symbol", m.group(1), "wire")
            rows.setdefault(r["id"], r)
    # Test modules contain helpers; only attributes naming tests qualify.
    for m in TEST_FN.finditer(source):
        prefix = source[max(0, m.start() - 1500):m.start()]
        attrs = re.search(r"((?:\s*#\[[^\]]+\])+\s*)$", prefix)
        if attrs and re.search(r"#\[(?:(?:\w+::)*test(?:\s*\(|\s*\])|rstest|test_case)", attrs.group(1)):
            r = row(path, line_number(source, m.start()), "rust-test", m.group(1), "behavior", 50)
            rows.setdefault(r["id"], r)
    # Preserve fields and types in the narrow realtime protocol/configuration files.
    if ("/realtime_websocket/protocol.rs" in path or "/protocol/src/realtime.rs" in path
        or "/realtime_websocket/methods" in path or path.endswith("/common.rs")) and not path.endswith("_tests.rs"):
        last_type = None
        for m in sorted([*FIELD.finditer(source), *TYPE.finditer(source)], key=lambda m: m.start()):
            if m.re is TYPE:
                last_type = m.group(1)
                r = row(path, line_number(source, m.start()), "rust-type", last_type, "wire")
            elif last_type:
                r = row(path, line_number(source, m.start()), "rust-field", f"{last_type}.{m.group(1)}", "wire")
            else:
                continue
            rows.setdefault(r["id"], r)
    if "/config/" in path:
        for m in FIELD.finditer(source):
            if re.search(r"realtime|voice|websocket|compact|context_window", m.group(1)):
                r = row(path, line_number(source, m.start()), "config-field", m.group(1), "wire")
                rows.setdefault(r["id"], r)
    if "/app-server-protocol/src/protocol/v2/realtime" in path or "/requests/responses.rs" in path:
        last_type = None
        for m in sorted([*FIELD.finditer(source), *TYPE.finditer(source)], key=lambda m: m.start()):
            if m.re is TYPE:
                last_type = m.group(1)
                r = row(path, line_number(source, m.start()), "rust-type", last_type, "wire")
            elif last_type:
                r = row(path, line_number(source, m.start()), "rust-field", f"{last_type}.{m.group(1)}", "wire")
            else:
                continue
            rows.setdefault(r["id"], r)
    # State-machine candidate entry points are explicitly distinct from tests.
    if path.startswith("codex-rs/core/src/") and ("realtime" in path or "compact_remote" in path or path.endswith("/client.rs")) and not path.endswith("_tests.rs"):
        for m in FUNCTION.finditer(source):
            r = row(path, line_number(source, m.start()), "rust-function", m.group(1), "behavior")
            rows.setdefault(r["id"], r)
    return list(rows.values())

def live_rows():
    paths = [
        ("native-websocket", 51, "codex-rs/core/tests/suite/realtime_conversation.rs"),
        ("chatgpt-webrtc-sideband", 52, "codex-rs/core/tests/suite/realtime_attachment.rs"),
        ("api-webrtc-sideband", 52, "codex-rs/codex-api/src/endpoint/realtime_call.rs"),
    ]
    for route, issue, path in paths:
        for scenario in ("connect", "audio-delegation-reasoning-tool-feedback-audio",
                         "follow-up", "interruption", "reconnect", "reasoning-initiated-speech",
                         "clean-shutdown"):
            yield row(path, 1, "live-scenario", f"{route}.{scenario}", "live", issue, "applicable")

def apply_reviews(data):
    """Overlay reviewed, exact row dispositions without inventing evidence."""
    if not REVIEWS.exists():
        return data
    review = json.loads(REVIEWS.read_text(encoding="utf-8"))
    if review.get("schemaVersion") != 1 or review.get("upstreamCommit") != PIN:
        raise ValueError("Review file does not match the pinned matrix")
    rows = {r["id"]: r for r in data["rows"]}
    allowed = {"ownerIssue", "applicability", "status", "implementation", "test", "evidence", "rationale"}
    for rid, changes in review["rows"].items():
        if rid not in rows or not changes.keys() <= allowed:
            raise ValueError(f"Invalid review mapping: {rid}")
        rows[rid].update(changes)
    for scenario in review.get("scenarios", []):
        anchor = rows.get(scenario["anchor"])
        if anchor is None:
            raise ValueError(f"Unknown scenario anchor: {scenario['anchor']}")
        rid = "scenario:" + scenario["id"]
        if rid in rows and rows[rid]["source"] != anchor["source"]:
            raise ValueError(f"Scenario source anchor changed: {rid}")
        result = dict(anchor)
        result.update(id=rid, kind="scenario", symbol=scenario["id"], lane="behavior",
                      ownerIssue=scenario["ownerIssue"], applicability="applicable",
                      status="unverified", implementation=None, test=None, evidence=None)
        result.update({k: v for k, v in scenario.items() if k in allowed})
        rows[rid] = result
    data["rows"] = sorted(rows.values(), key=lambda r: r["id"])
    return data

def write_matrix(data):
    validate(data)
    MATRIX.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

def refresh():
    tree = json.loads(fetch(f"https://api.github.com/repos/openai/codex/git/trees/{PIN}?recursive=1"))
    if tree.get("truncated"):
        raise ValueError("Upstream tree is truncated")
    paths = sorted(i["path"] for i in tree["tree"] if i["type"] == "blob" and selected(i["path"]))
    if not EXACT <= set(paths):
        raise ValueError("Required upstream sources missing")
    def download(path):
        content = fetch(f"https://raw.githubusercontent.com/openai/codex/{PIN}/{path}")
        return path, content
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        files = dict(pool.map(download, paths))
    old = json.loads(MATRIX.read_text()) if MATRIX.exists() else {}
    if old and old["upstreamCommit"] != PIN:
        raise ValueError("A new pin requires an explicit review migration")
    previous = {r["id"]: r for r in old.get("rows", [])}
    rows = [r for path, content in files.items() for r in scan(path, content.decode("utf-8"))]
    rows.extend(live_rows())
    # Keep explicit reviewed dispositions, but never quietly remove a row.
    for r in rows:
        if r["id"] in previous:
            prior = previous.pop(r["id"])
            if prior["source"] != r["source"]:
                raise ValueError(f"Source anchor changed for {r['id']}")
            for key in ("ownerIssue", "applicability", "status", "implementation", "test", "evidence", "rationale"):
                if key in prior:
                    r[key] = prior[key]
    # Reviewed semantic rows are reconstituted from their explicit anchor mapping.
    previous = {rid: r for rid, r in previous.items() if r["kind"] != "scenario"}
    if previous:
        raise ValueError(f"Refresh would discard {len(previous)} previous rows; review required")
    data = {"schemaVersion": 1, "upstreamRelease": TAG, "upstreamCommit": PIN,
            "inventoryReviewed": old.get("inventoryReviewed", False),
            "sourceFiles": {p: hashlib.sha256(c).hexdigest() for p, c in files.items()},
            "rows": sorted(rows, key=lambda r: r["id"])}
    write_matrix(apply_reviews(data))

def validate(data):
    if data.get("schemaVersion") != 1 or data.get("upstreamCommit") != PIN or data.get("upstreamRelease") != TAG:
        raise ValueError("Unknown matrix schema or upstream pin")
    if not isinstance(data.get("inventoryReviewed"), bool) or not data.get("sourceFiles") or not data.get("rows"):
        raise ValueError("Empty or invalid inventory")
    files = data["sourceFiles"]
    if not EXACT <= files.keys() or any(not re.fullmatch(r"[a-f0-9]{64}", h) for h in files.values()):
        raise ValueError("Missing or invalid source fingerprint")
    ids = set()
    source_cache = {}
    def check_references(references, rid):
        if references is None:
            return
        if isinstance(references, str):
            references = [references]
        if not isinstance(references, list) or not references:
            raise ValueError(f"Invalid mapped references: {rid}")
        for reference in references:
            path, sep, symbol = reference.partition("::")
            absolute = (ROOT / path).resolve()
            if not sep or not symbol or not absolute.is_relative_to(ROOT / "src") or not absolute.is_file():
                raise ValueError(f"Invalid mapped reference: {rid}: {reference}")
            if absolute not in source_cache:
                source_cache[absolute] = absolute.read_text(encoding="utf-8-sig")
            name = symbol.split("(")[0].rsplit(".", 1)[-1]
            if not name or not re.search(r"\b" + re.escape(name) + r"\b", source_cache[absolute]):
                raise ValueError(f"Missing mapped symbol: {rid}: {reference}")
    for r in data["rows"]:
        rid = r["id"]
        if rid in ids:
            raise ValueError(f"Duplicate row: {rid}")
        ids.add(rid)
        anchor = re.fullmatch(r"https://github.com/openai/codex/blob/" + PIN + r"/(.+)#L([1-9]\d*)", r["source"])
        if not anchor or anchor.group(1) not in files:
            raise ValueError(f"Missing pinned source link: {rid}")
        if r["lane"] not in LANES or r["status"] not in STATUSES or r["applicability"] not in {"applicable", "pending", "not-applicable"}:
            raise ValueError(f"Invalid classification: {rid}")
        if not isinstance(r["ownerIssue"], int) or not 24 <= r["ownerIssue"]:
            raise ValueError(f"Missing owning issue: {rid}")
        for candidate_path in r["implementationCandidates"] + r["testCandidates"]:
            if not (ROOT / candidate_path).is_file():
                raise ValueError(f"Missing C# candidate {candidate_path}: {rid}")
        check_references(r.get("implementation"), rid)
        check_references(r.get("test"), rid)
        if r["status"] == "not-applicable" or r["applicability"] == "not-applicable":
            if r["status"] != "not-applicable" or r["applicability"] != "not-applicable" or not r.get("rationale"):
                raise ValueError(f"N/A requires explicit justification: {rid}")
        if r["status"] == "passed":
            evidence = r.get("evidence")
            if r["applicability"] != "applicable" or not r.get("implementation") or not r.get("test") or not isinstance(evidence, dict):
                raise ValueError(f"Pass without reviewed mapping/evidence: {rid}")
            if not re.fullmatch(r"[a-f0-9]{40}", evidence.get("csharpCommit", "")) or evidence.get("outcome") != "passed" or not evidence.get("receipt"):
                raise ValueError(f"Pass without revision-bound test receipt: {rid}")
            expected = "live" if r["lane"] == "live" else "offline"
            if evidence.get("lane") != expected:
                raise ValueError(f"Wrong evidence lane: {rid}")
            if expected == "live":
                # Native live evidence validation must be implemented with #51/#52; fail closed meanwhile.
                raise ValueError(f"Live receipt verifier is not implemented: {rid}")
            tree = validate_receipt(r, evidence)
            try:
                _, claimed_tree = source_revision(evidence["csharpCommit"])
            except subprocess.CalledProcessError as error:
                raise ValueError(f"Unknown evidence revision: {rid}") from error
            if tree != claimed_tree:
                raise ValueError(f"Receipt source tree differs from its commit: {rid}")
    return data

def report(data):
    result = {}
    _, current_tree = source_revision()
    stale = set()
    for r in data["rows"]:
        if r["status"] == "passed" and r["lane"] != "live":
            if validate_receipt(r, r["evidence"]) != current_tree:
                stale.add(r["id"])
    for lane in sorted(LANES):
        rows = [r for r in data["rows"] if r["lane"] == lane]
        counts = collections.Counter("unverified" if r["id"] in stale else r["status"] for r in rows)
        pending = sum(r["applicability"] == "pending" for r in rows)
        denominator = sum(r["applicability"] == "applicable" for r in rows)
        passed = counts["passed"]
        result[lane] = {"passed": passed, "denominator": denominator,
                        "percent": round(100 * passed / denominator, 2) if denominator else None,
                        "candidateDenominator": denominator + pending,
                        "pendingApplicability": pending,
                        "staleEvidence": sum(r["id"] in stale for r in rows),
                        "statuses": {s: counts[s] for s in sorted(STATUSES)}}
    clean = source_is_clean()
    ready = clean and data["inventoryReviewed"] and not stale and all(
        r["applicability"] != "pending" and r["status"] in {"passed", "not-applicable"} for r in data["rows"]
    ) and all(result[lane]["denominator"] > 0 for lane in LANES)
    return {"upstreamCommit": data["upstreamCommit"], "inventoryReviewed": data["inventoryReviewed"],
            "sourceFiles": len(data["sourceFiles"]), "rows": len(data["rows"]),
            "sourceTree": current_tree, "sourceClean": clean, "lanes": result, "releaseReady": ready}

def self_test(data):
    """Verify the checker cannot convert unsupported evidence into acceptance."""
    import copy

    def reject(mutator):
        sample = copy.deepcopy(data)
        mutator(sample)
        try:
            validate(sample)
        except ValueError:
            return
        raise AssertionError("Invalid matrix was accepted")

    reject(lambda d: d["rows"].append(d["rows"][0]))
    reject(lambda d: d["rows"][0].update(source="https://example.invalid"))
    reject(lambda d: d["rows"][0].update(status="passed", applicability="applicable"))
    reject(lambda d: d["rows"][0].update(status="not-applicable", applicability="not-applicable"))
    reject(lambda d: d["rows"][0].update(ownerIssue=0))
    sample = copy.deepcopy(data)
    sample["inventoryReviewed"] = True
    sample["rows"][0].update(status="skipped", applicability="applicable")
    assert not report(sample)["releaseReady"]
    print("Checker self-test: invalid evidence, duplicate IDs, N/A and skipped cases rejected.")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("refresh", "review", "check", "report", "gate", "self-test"))
    args = parser.parse_args()
    if args.command == "refresh":
        refresh()
    if args.command == "review":
        write_matrix(apply_reviews(json.loads(MATRIX.read_text(encoding="utf-8"))))
    data = validate(json.loads(MATRIX.read_text(encoding="utf-8")))
    if data != apply_reviews(json.loads(MATRIX.read_text(encoding="utf-8"))):
        raise ValueError("Review mappings changed; run review before committing")
    if args.command == "self-test":
        self_test(data)
    result = report(data)
    print(json.dumps(result, indent=2, sort_keys=True))
    if args.command == "gate" and not result["releaseReady"]:
        return 1
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
