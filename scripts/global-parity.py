#!/usr/bin/env python3
"""Pinned, independent global Rust Codex inventory (#62/#67/#88).

The inventory is deliberately not an acceptance receipt. Realtime is delegated
to its existing matrix and gate. Only refresh accesses the network.
"""
import argparse
import collections
import hashlib
import importlib.util
import json
import re
import subprocess
import tarfile
import tomllib
import urllib.request
from pathlib import Path
from global_parity_reviews import read_reviews, validate_reviews, summarize, self_test as review_self_test

ROOT = Path(__file__).resolve().parent.parent
PIN = "3d86e43b075adeba2193ec5c22ac92f55afd82e6"
TAG = "rust-v0.172.0"
MATRIX = ROOT / "docs/global-parity-matrix.json"
REALTIME = ROOT / "docs/realtime-parity-matrix.json"
CATALOG = ROOT / "src/libs/tryAGI.Codex/AppServerProtocol.json"
RPC = "codex-rs/app-server-protocol/schema/json/codex_app_server_protocol.schemas.json"
CONFIG = "codex-rs/core/config.schema.json"
FEATURES = "codex-rs/features/src/lib.rs"
COMMON = "codex-rs/app-server-protocol/src/protocol/common.rs"
LANES = ("native-sdk", "host-adapter", "rust-app-server-interop", "live-provider")
UNIONS = {"clientRequests": "ClientRequest", "clientNotifications": "ClientNotification",
          "serverRequests": "ServerRequest", "serverNotifications": "ServerNotification"}
STABLE_EXPECTED = {"clientRequests": 110, "clientNotifications": 1,
                   "serverRequests": 10, "serverNotifications": 87}
SOURCE_EXPECTED = {"clientRequests": 192, "clientNotifications": 1,
                   "serverRequests": 10, "serverNotifications": 89}
MACROS = {"clientRequests": "client_request", "clientNotifications": "client_notification",
          "serverRequests": "server_request", "serverNotifications": "server_notification"}
TEST = re.compile(r"(?m)^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+(\w+)\s*\(")
PUBLIC = re.compile(r"(?m)^\s*pub\s+(?:async\s+|unsafe\s+|const\s+)?(?:fn|struct|enum|trait|type|static|const)\s+(\w+)")
FEATURE = re.compile(r'\bkey:\s*"([^"]+)"')
ROUTING = (
    (r"memory|memories", 93), (r"rollout", 94), (r"remote.?control", 95),
    (r"collaboration.?mode", 96), (r"diagnostic|feedback|(?:^|/)otel|trace", 97),
    (r"linux-sandbox|windows-sandbox|mxc-sandbox|bwrap|process-hardening|/pty/", 98),
    (r"guardian|review", 82), (r"permission|approval", 80),
    (r"execpolicy|sandbox", 81), (r"file-system|file-search|file-watcher|apply-patch|^fs/|fuzzyfilesearch", 79),
    (r"exec|process", 78), (r"hook|code-mode", 77),
    (r"dynamic|user.?input", 76), (r"skill|plugin|connector|marketplace|^app/", 75),
    (r"mcp", 74), (r"tools?|tool.?search", 73),
    (r"agent|message-board", 72), (r"project|section|attachment", 71),
    (r"goal|queue|turn", 70), (r"history|timeline|item", 69),
    (r"thread", 68), (r"rate.?limit|credit|usage", 87),
    (r"provider|network|proxy|http-client|ollama|lmstudio", 86),
    (r"account|login|auth|keyring|credential", 85), (r"model", 84),
    (r"config|feature", 83),
)
HINTS = {
    68: "CodexThreadStore.cs", 69: "CodexThreadRpc.Queries.cs", 70: "CodexThreadAgent.cs",
    73: "CodexAgent.cs", 79: "CodexWorkspacePatch.cs", 83: "CodexThreadRpc.Configuration.cs",
    85: "CodexDeviceAuthorization.cs", 87: "CodexAccountUsage.cs",
    89: "CodexAppServerModels.cs", 97: "CodexResponseDiagnostic.cs",
}


def owner(symbol, path, default=88):
    for value in (symbol, path):
        for pattern, issue in ROUTING:
            if re.search(pattern, value, re.I):
                return issue
    return default


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def realtime_index():
    matrix = load_json(REALTIME)
    if matrix["upstreamCommit"] != PIN:
        raise ValueError("Realtime and global pins differ")
    index = collections.defaultdict(list)
    for row in matrix["rows"]:
        index[row["symbol"]].append(row["id"])
    return index


def candidate(kind, path, symbol, default=88, **fields):
    issue = owner(symbol, path, default)
    return {"id": f"{kind}:{path}:{symbol}", "kind": kind,
            "source": path, "symbol": symbol, "ownerIssue": issue,
            "implementationCandidate": HINTS.get(issue),
            "implementation": None, "test": None, "evidence": None,
            "lanes": {lane: "pending" for lane in LANES}, **fields}


def schema_fields(node, pointer=""):
    # Traverse schema positions, not examples/default payloads. References are
    # inventoried at their declaration, so recursive references cannot loop.
    if not isinstance(node, dict):
        return
    for name, child in sorted(node.get("properties", {}).items()):
        path = pointer + "/properties/" + name.replace("~", "~0").replace("/", "~1")
        yield path, name
        yield from schema_fields(child, path)
    for key in ("definitions", "$defs", "patternProperties"):
        for name, child in sorted(node.get(key, {}).items()):
            yield from schema_fields(child, pointer + "/" + key + "/" + name.replace("~", "~0").replace("/", "~1"))
    for key in ("items", "additionalProperties", "if", "then", "else", "not"):
        if isinstance(node.get(key), dict):
            yield from schema_fields(node[key], pointer + "/" + key)
    for key in ("oneOf", "anyOf", "allOf", "prefixItems"):
        for i, child in enumerate(node.get(key, [])):
            yield from schema_fields(child, pointer + "/" + key + "/" + str(i))


def methods(schema):
    # Reuse the existing schema extraction rather than a second wire parser.
    spec = importlib.util.spec_from_file_location("app_server_catalog", ROOT / "scripts/update-app-server-protocol.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return {key: module.methods(schema["definitions"], union) for key, union in UNIONS.items()}


def source_methods(content):
    """Inventory every declared variant, including experimental and legacy APIs."""
    result = {}
    for direction, macro in MACROS.items():
        header = re.search(r"(?m)^" + macro + r"_definitions!\s*\{\s*$", content)
        if header is None:
            raise ValueError(f"Missing protocol macro: {macro}")
        end = re.search(r"(?m)^\}", content[header.end():])
        if end is None:
            raise ValueError(f"Missing closing protocol macro: {macro}")
        body = content[header.end():header.end() + end.start()]
        variants = re.finditer(r'(?m)^    ([A-Z]\w*)(?:\s*=>\s*"([^"]+)"|\s*[,({])', body)
        methods_by_name = {}
        for variant in variants:
            name, wire_name = variant.groups()
            # Explicit serde rename overrides the macro's camelCase default.
            attrs = re.search(r"(?m)((?:^    #\[[^\n]+\]\n)+)$", body[:variant.start()])
            rename = re.search(r'serde\(rename\s*=\s*"([^"]+)"\)', attrs.group(1)) if attrs else None
            wire_name = wire_name or (rename.group(1) if rename else name[0].lower() + name[1:])
            if wire_name in methods_by_name:
                raise ValueError(f"Duplicate source method: {wire_name}")
            methods_by_name[wire_name] = content.count("\n", 0, header.end() + variant.start()) + 1
        result[direction] = methods_by_name
    if {key: len(v) for key, v in result.items()} != SOURCE_EXPECTED:
        raise ValueError(f"Unexpected source method counts: { {k: len(v) for k,v in result.items()} }")
    return result


def scan_rust(content):
    tests = []
    for match in TEST.finditer(content):
        prefix = content[max(0, match.start() - 1800):match.start()]
        attrs = re.search(r"((?:\s*#\[[^\]]+\])+\s*)$", prefix)
        if attrs and re.search(r"#\[(?:(?:\w+::)*test(?:\s*\(|\s*\])|rstest|test_case)", attrs.group(1)):
            tests.append([match.group(1), content.count("\n", 0, match.start()) + 1])
    public = [[m.group(1), content.count("\n", 0, m.start()) + 1] for m in PUBLIC.finditer(content)]
    return tests, public


def upstream_tree():
    result = subprocess.run(["gh", "api", f"repos/openai/codex/git/trees/{PIN}?recursive=1"],
                            cwd=ROOT, check=True, capture_output=True)
    tree = json.loads(result.stdout)
    if tree.get("truncated"):
        raise ValueError("Incomplete pinned upstream tree")
    return {entry["path"]: entry["sha"] for entry in tree["tree"]
            if entry["type"] == "blob" and entry["path"].startswith("codex-rs/")}


def refresh():
    if MATRIX.exists():
        raise ValueError("Initial inventory is immutable; review changes explicitly")
    source_blobs = upstream_tree()
    source_inventory = {}
    rows = []
    delegated = []
    rt = realtime_index()
    stable_wire = None
    source_wire = None
    required = {RPC, CONFIG, FEATURES, COMMON}
    seen = set()

    def add(row, realtime_symbol=None, realtime_owned=False):
        if realtime_owned:
            # Keep only a cross-reference, never copy evidence/status.
            delegated.append({"id": row["id"], "source": row["source"],
                              "symbol": realtime_symbol, "ownerIssue": 25,
                              "realtimeCandidates": sorted(rt.get(realtime_symbol, []))})
        else:
            rows.append(row)

    request = urllib.request.Request(f"https://codeload.github.com/openai/codex/tar.gz/{PIN}",
                                     headers={"User-Agent": "tryAGI-Codex-global-parity"})
    with urllib.request.urlopen(request, timeout=90) as response, tarfile.open(fileobj=response, mode="r|gz") as archive:
        for member in archive:
            _, _, path = member.name.partition("/")
            if path not in source_blobs:
                continue
            seen.add(path)
            # Symlink contents are their link target in Git, not the target file.
            if member.issym():
                raw = member.linkname.encode()
            elif member.isfile():
                raw = archive.extractfile(member).read()
            else:
                raise ValueError(f"Unexpected Git archive member: {path}")
            blob = hashlib.sha1(f"blob {len(raw)}\0".encode() + raw).hexdigest()
            if blob != source_blobs[path]:
                raise ValueError(f"Git blob mismatch: {path}")
            info = {"gitBlob": blob, "ownerIssue": owner("", path)}
            source_inventory[path] = info
            if path.endswith("/Cargo.toml"):
                manifest = tomllib.loads(raw.decode("utf-8"))
                if "package" in manifest:
                    package = manifest["package"]["name"]
                    info["crate"] = package
                    rows.append(candidate("crate", path, package))
                    for feature in sorted(manifest.get("features", {})):
                        rows.append(candidate("cargo-feature", path, feature))
                    for target in sorted(manifest.get("target", {})):
                        rows.append(candidate("cargo-target", path, target))
            if path.endswith(".rs"):
                text = raw.decode("utf-8")
                tests, public = scan_rust(text)
                if tests:
                    info["testCandidates"] = tests
                if public:
                    info["apiCandidates"] = public
                if path == COMMON:
                    source_wire = source_methods(text)
                if path == FEATURES:
                    for match in FEATURE.finditer(text):
                        symbol = match.group(1)
                        add(candidate("feature-flag", path, symbol, 83),
                            symbol, "realtime" in symbol.lower())
            if path == CONFIG:
                for pointer, symbol in schema_fields(json.loads(raw)):
                    add(candidate("config-field", path, pointer, 83),
                        symbol, "realtime" in symbol.lower())
            if path == RPC:
                schema = json.loads(raw)
                stable_wire = methods(schema)
                info["schemaDefinitions"] = sorted(schema["definitions"])
    if seen != source_blobs.keys() or not required <= seen:
        raise ValueError(f"Archive/tree coverage mismatch: {len(source_blobs.keys() - seen)} missing")
    if stable_wire is None or {key: len(v) for key, v in stable_wire.items()} != STABLE_EXPECTED:
        raise ValueError("Pinned stable RPC catalog counts changed")
    if source_wire is None:
        raise ValueError("Missing source protocol inventory")
    wire = {key: sorted(v) for key, v in source_wire.items()}
    for direction, names in wire.items():
        if not set(stable_wire[direction]) <= set(names):
            raise ValueError(f"Stable schema has a method absent from source: {direction}")
        for symbol in names:
            add(candidate("rpc", COMMON, direction + ":" + symbol, 89,
                          direction=direction, method=symbol,
                          sourceLine=source_wire[direction][symbol],
                          stableSchema=symbol in stable_wire[direction]),
                symbol, "realtime" in symbol.lower())
    data = {"schemaVersion": 1, "upstreamRelease": TAG, "upstreamCommit": PIN,
            "inventoryReviewed": False, "upstreamTreeBlobCount": len(source_blobs),
            "realtimeMatrix": "docs/realtime-parity-matrix.json",
            "sourceInventory": dict(sorted(source_inventory.items())),
            "rpcCatalog": wire, "stableRpcCatalog": stable_wire,
            "delegatedRealtime": sorted(delegated, key=lambda r: r["id"]),
            "rows": sorted(rows, key=lambda r: r["id"])}
    validate(data)
    # One record per line keeps large inventories diffable without bloating them.
    def compact(value):
        return json.dumps(value, separators=(",", ":"), ensure_ascii=False)
    lines = ["{"]
    for i, (key, value) in enumerate(data.items()):
        comma = "," if i + 1 < len(data) else ""
        if key == "sourceInventory":
            lines.append(f'  "{key}": {{')
            entries = list(value.items())
            lines.extend("    " + compact(k) + ": " + compact(v) + ("," if j + 1 < len(entries) else "")
                         for j, (k, v) in enumerate(entries))
            lines.append("  }" + comma)
        elif key in ("rows", "delegatedRealtime"):
            lines.append(f'  "{key}": [')
            lines.extend("    " + compact(v) + ("," if j + 1 < len(value) else "")
                         for j, v in enumerate(value))
            lines.append("  ]" + comma)
        else:
            lines.append("  " + compact(key) + ": " + compact(value) + comma)
    lines.append("}")
    MATRIX.write_text("\n".join(lines) + "\n", encoding="utf-8")


def validate(data):
    if (data["schemaVersion"], data["upstreamCommit"], data["upstreamRelease"]) != (1, PIN, TAG):
        raise ValueError("Incorrect global inventory pin")
    if data["inventoryReviewed"] is not False:
        raise ValueError("Global review and receipt acceptance are not implemented yet")
    source = data["sourceInventory"]
    if len(source) != data["upstreamTreeBlobCount"] or not {RPC, CONFIG, FEATURES, COMMON} <= source.keys():
        raise ValueError("Incomplete upstream source inventory")
    if any(not re.fullmatch(r"[0-9a-f]{40}", s["gitBlob"]) or s["ownerIssue"] < 1 for s in source.values()):
        raise ValueError("Invalid source provenance or owner")
    if {key: len(v) for key, v in data["rpcCatalog"].items()} != SOURCE_EXPECTED:
        raise ValueError("Source RPC catalog incomplete")
    if {key: len(v) for key, v in data["stableRpcCatalog"].items()} != STABLE_EXPECTED:
        raise ValueError("Stable RPC catalog incomplete")
    if any(not set(v) <= set(data["rpcCatalog"][key]) for key, v in data["stableRpcCatalog"].items()):
        raise ValueError("Stable catalog must be contained in source catalog")
    all_rows = data["rows"] + data["delegatedRealtime"]
    ids = [r["id"] for r in all_rows]
    if len(ids) != len(set(ids)):
        raise ValueError("Duplicate capability identifier")
    if any(r["source"] not in source or r["ownerIssue"] < 1 for r in all_rows):
        raise ValueError("Unclassified or orphaned capability")
    for r in data["rows"]:
        if set(r["lanes"]) != set(LANES) or any(v != "pending" for v in r["lanes"].values()):
            raise ValueError("Unreviewed capability cannot receive acceptance credit")
        if any(r[k] is not None for k in ("implementation", "test", "evidence")):
            raise ValueError("Initial matrix must not invent acceptance mappings")
    expected_rpc = {f"rpc:{COMMON}:{direction}:{method}" for direction, names in data["rpcCatalog"].items() for method in names}
    if expected_rpc != {rid for rid in ids if rid.startswith("rpc:")}:
        raise ValueError("RPC rows must cover the full catalog exactly once")
    if data["realtimeMatrix"] != "docs/realtime-parity-matrix.json":
        raise ValueError("Realtime evidence must use the existing matrix")
    for r in data["delegatedRealtime"]:
        if r["ownerIssue"] != 25 or "lanes" in r or "evidence" in r:
            raise ValueError("Delegated realtime capabilities must not be counted twice")


def report(data):
    validate(data)
    rt = subprocess.run(["python3", "-B", str(ROOT / "scripts/realtime-parity.py"), "report"],
                        cwd=ROOT, check=True, capture_output=True, text=True)
    realtime = json.loads(rt.stdout)
    catalog = load_json(CATALOG)
    drift = {key: {"missing": sorted(set(names) - set(catalog[key])),
                   "extra": sorted(set(catalog[key]) - set(names))}
             for key, names in data["rpcCatalog"].items()}
    rt_index = realtime_index()
    review = read_reviews()
    lanes, global_ready, reviewed_count, stale_count = summarize(data, review)
    return {"upstreamCommit": PIN, "sourceFiles": len(data["sourceInventory"]),
            "candidateKinds": dict(collections.Counter(r["kind"] for r in data["rows"])),
            "rustTestCandidates": sum(len(s.get("testCandidates", [])) for s in data["sourceInventory"].values()),
            "rustApiCandidates": sum(len(s.get("apiCandidates", [])) for s in data["sourceInventory"].values()),
            "lanes": lanes, "reviewedLaneDecisions": reviewed_count, "staleLaneDecisions": stale_count,
            "delegatedRealtime": len(data["delegatedRealtime"]),
            "unresolvedRealtimeReferences": [r["id"] for r in data["delegatedRealtime"] if not rt_index.get(r["symbol"])],
            "realtimeReleaseReady": realtime["releaseReady"], "catalogVersion": catalog["version"],
            "stableRpcCounts": {k: len(v) for k, v in data["stableRpcCatalog"].items()},
            "sourceRpcCounts": {k: len(v) for k, v in data["rpcCatalog"].items()},
            "catalogDrift": drift, "inventoryReviewed": review["inventoryReviewed"],
            "releaseReady": global_ready and realtime["releaseReady"]
                and all(rt_index.get(r["symbol"]) for r in data["delegatedRealtime"])
                and not any(d["missing"] or d["extra"] for d in drift.values())}


def self_test(data):
    import copy
    def reject(mutator):
        sample = copy.deepcopy(data)
        mutator(sample)
        try:
            validate(sample)
        except ValueError:
            return
        raise AssertionError("Invalid global inventory accepted")
    reject(lambda d: d["rows"].append(d["rows"][0]))
    reject(lambda d: d["rows"][0].update(ownerIssue=0))
    reject(lambda d: d["rows"][0]["lanes"].update({"native-sdk": "passed"}))
    reject(lambda d: d.update(inventoryReviewed=True))
    reject(lambda d: d["rpcCatalog"]["clientRequests"].pop())
    reject(lambda d: d["delegatedRealtime"][0].update(evidence="copied"))
    assert not report(data)["releaseReady"]
    print("Global parity self-test: duplicate, orphan, premature pass, missing RPC and duplicated evidence rejected.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("refresh", "check", "report", "gate", "self-test"))
    action = parser.parse_args().action
    if action == "refresh":
        refresh()
        return
    data = load_json(MATRIX)
    validate(data)
    if action == "self-test":
        self_test(data)
        review_self_test(data, read_reviews())
    elif action in ("report", "gate"):
        result = report(data)
        print(json.dumps(result, indent=2))
        if action == "gate" and not result["releaseReady"]:
            raise SystemExit(1)
    else:
        validate_reviews(data, read_reviews())
        print(f"Validated {len(data['sourceInventory'])} pinned sources and {len(data['rows'])} global candidates.")


if __name__ == "__main__":
    main()
