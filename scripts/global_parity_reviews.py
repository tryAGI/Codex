"""Lane-separated review overlay; inventory candidates never imply acceptance."""
import collections
import functools
import json
import re
import subprocess
from pathlib import Path

from realtime_parity_evidence import source_revision, source_is_clean, validate_receipt
from global_parity_runtime import validate_runtime_observation

ROOT = Path(__file__).resolve().parent.parent
REVIEWS = ROOT / "docs/global-parity-reviews.json"
PIN = "3d86e43b075adeba2193ec5c22ac92f55afd82e6"
LANES = ("native-sdk", "host-adapter", "rust-app-server-interop", "live-provider")
STATUSES = {"source-reviewed", "missing", "divergent", "not-applicable", "passed"}
HEX40 = re.compile(r"[a-f0-9]{40}")


@functools.lru_cache(maxsize=1024)
def blob(revision, path):
    if not HEX40.fullmatch(revision):
        raise ValueError("Review requires an immutable Git revision")
    if not path.startswith("src/") or Path(path).is_absolute() or ".." in Path(path).parts:
        raise ValueError(f"Review path must be inside src: {path}")
    try:
        return subprocess.check_output(["git", "show", f"{revision}:{path}"], cwd=ROOT, stderr=subprocess.DEVNULL)
    except subprocess.CalledProcessError as error:
        raise ValueError(f"Unknown review source {revision}:{path}") from error


def references(values, revision):
    if not isinstance(values, list) or not values or len(values) != len(set(values)):
        raise ValueError("Review requires exact, unique source references")
    paths = set()
    for reference in values:
        path, sep, symbol = reference.partition("::")
        name = symbol.split("(")[0].rsplit(".", 1)[-1]
        if not sep or not name or not path.endswith(".cs"):
            raise ValueError(f"Expected C# path::symbol: {reference}")
        content = blob(revision, path).decode("utf-8-sig")
        if not re.search(r"\b" + re.escape(name) + r"\b", content):
            raise ValueError(f"Missing reviewed symbol: {reference}")
        paths.add(path)
    return paths


def read_reviews():
    return json.loads(REVIEWS.read_text(encoding="utf-8"))


def validate_reviews(data, review):
    if review.get("schemaVersion") != 1 or review.get("upstreamCommit") != PIN:
        raise ValueError("Global review baseline differs from inventory")
    if not isinstance(review.get("inventoryReviewed"), bool) or not isinstance(review.get("rows"), dict):
        raise ValueError("Invalid review inventory")
    if set(review) != {"schemaVersion", "upstreamCommit", "inventoryReviewed", "rows"}:
        raise ValueError("Unexpected global review fields")
    candidates = {row["id"]: row for row in data["rows"]}
    current_commit, current_tree = source_revision()
    reviewed = {}
    stale = set()
    for rid, lane_reviews in review["rows"].items():
        if rid not in candidates or not isinstance(lane_reviews, dict) or not lane_reviews or not lane_reviews.keys() <= set(LANES):
            raise ValueError(f"Unknown, delegated or invalid reviewed row: {rid}")
        for lane, entry in lane_reviews.items():
            status = entry.get("status")
            revision = entry.get("reviewedCommit", "")
            rationale = entry.get("rationale")
            owner = entry.get("ownerIssue")
            if status not in STATUSES or not HEX40.fullmatch(revision) or not isinstance(rationale, str) or not rationale.strip() or type(owner) is not int or owner < 25:
                raise ValueError(f"Incomplete review: {rid}/{lane}")
            if set(entry) - {"status", "reviewedCommit", "rationale", "ownerIssue", "scope", "implementation", "test", "basis", "evidence", "testEvidence", "upstreamTests", "runtimeEvidence", "runtimeObservation"}:
                raise ValueError(f"Unknown review field: {rid}/{lane}")
            _, reviewed_tree = source_revision(revision)
            paths = set()
            if status in {"source-reviewed", "passed", "divergent"}:
                paths.update(references(entry.get("implementation"), revision))
                paths.update(references(entry.get("test"), revision))
            else:
                if entry.get("implementation") or entry.get("test"):
                    raise ValueError("Missing and N/A decisions use an explicit source basis, not fake mappings")
                basis = entry.get("basis")
                if not isinstance(basis, list) or not basis or len(set(basis)) != len(basis):
                    raise ValueError("Missing or N/A decision requires exact source basis")
                for path in basis:
                    blob(revision, path)
                paths.update(basis)
            scope = entry.get("scope")
            allowed_scopes = {
                "native-sdk": {"native-behavior", "native-dispatch"},
                "host-adapter": {"host-effect"},
                "rust-app-server-interop": {"typed-contract-only", "rust-runtime"},
                "live-provider": {"live-provider"},
            }
            if scope not in allowed_scopes[lane]:
                raise ValueError(f"Review scope does not match its lane: {rid}/{lane}")
            upstream_tests = entry.get("upstreamTests", [])
            if not isinstance(upstream_tests, list) or len(upstream_tests) != len(set(upstream_tests)):
                raise ValueError("Invalid pinned upstream test references")
            for reference in upstream_tests:
                path, sep, symbol = reference.partition("::")
                candidates_in_source = data["sourceInventory"].get(path, {}).get("testCandidates", [])
                if not sep or not any(name == symbol for name, _ in candidates_in_source):
                    raise ValueError(f"Unknown upstream test: {reference}")
            if status == "passed":
                if lane != "native-sdk" or scope != "native-behavior":
                    raise ValueError("Only native behavior has an offline acceptance verifier; other lanes remain blocked")
                evidence = entry.get("evidence")
                if not isinstance(evidence, dict) or evidence.get("lane") != "offline" or evidence.get("outcome") != "passed":
                    raise ValueError("Native acceptance requires an exact offline receipt")
                if evidence.get("csharpCommit") != revision:
                    raise ValueError("Reviewed and tested commits differ")
                location = evidence.get("receipt", "")
                if location.startswith("docs/global-parity-evidence/"):
                    receipt_tree = validate_receipt({"lane": "behavior", "test": entry["test"]}, evidence,
                                                    evidence_directory="docs/global-parity-evidence", expected_scope="global")
                else:
                    # Shared offline cases can be referenced without copying their receipt.
                    receipt_tree = validate_receipt({"lane": "behavior", "test": entry["test"]}, evidence)
                if receipt_tree != reviewed_tree:
                    raise ValueError("Receipt source tree differs from reviewed commit")
            elif entry.get("evidence") is not None:
                raise ValueError("Only a verified pass may carry acceptance evidence")
            observed = entry.get("testEvidence")
            if observed is not None:
                # A passed contract test is a fact about an exact source revision, not
                # proof that the entire Rust capability or an external runtime passes.
                if status != "source-reviewed" or lane not in {"native-sdk", "rust-app-server-interop"}:
                    raise ValueError("Test-only evidence must remain a source-reviewed offline mapping")
                if not isinstance(observed, dict) or observed.get("lane") != "offline" or observed.get("outcome") != "passed" or observed.get("csharpCommit") != revision:
                    raise ValueError("Invalid test-only evidence")
                location = observed.get("receipt", "")
                if location.startswith("docs/global-parity-evidence/"):
                    observed_tree = validate_receipt({"lane": "behavior", "test": entry["test"]}, observed,
                                                     evidence_directory="docs/global-parity-evidence", expected_scope="global")
                else:
                    observed_tree = validate_receipt({"lane": "behavior", "test": entry["test"]}, observed)
                if observed_tree != reviewed_tree:
                    raise ValueError("Observed test source tree differs from reviewed commit")
            runtime = entry.get("runtimeEvidence")
            if runtime is not None:
                if status != "source-reviewed" or lane != "rust-app-server-interop" or scope != "rust-runtime":
                    raise ValueError("Runtime observations must remain narrow interop evidence")
                if validate_runtime_observation(candidates[rid], entry, runtime) != reviewed_tree:
                    raise ValueError("Runtime evidence source tree differs from review")
            additional = entry.get("runtimeObservation")
            if additional is not None:
                # Keep the original typed contract evidence intact. A later real
                # runtime observation has its own independently pinned source.
                if (lane != "rust-app-server-interop" or scope != "typed-contract-only"
                        or status != "source-reviewed" or runtime is not None
                        or not isinstance(additional, dict)
                        or set(additional) != {"reviewedCommit", "scope", "rationale", "implementation", "test", "runtimeEvidence"}
                        or additional.get("scope") != "rust-runtime"
                        or not HEX40.fullmatch(additional.get("reviewedCommit", ""))
                        or not isinstance(additional.get("rationale"), str) or not additional["rationale"].strip()):
                    raise ValueError("Invalid independent runtime observation on typed review")
                additional_rev = additional["reviewedCommit"]
                references(additional["implementation"], additional_rev)
                references(additional["test"], additional_rev)
                validate_runtime_observation(candidates[rid], additional, additional["runtimeEvidence"])
            key = (rid, lane)
            reviewed[key] = entry
            # Negative conclusions can be invalidated by a new handler in another file;
            # accepted behavior likewise requires the exact full source tree.
            if status in {"missing", "not-applicable", "passed"}:
                if reviewed_tree != current_tree:
                    stale.add(key)
            else:
                try:
                    if any(blob(revision, p) != blob(current_commit, p) for p in paths):
                        stale.add(key)
                except ValueError:
                    stale.add(key)
    if review["inventoryReviewed"] and len(reviewed) != len(candidates) * len(LANES):
        raise ValueError("Global inventory cannot be reviewed while any lane decision is absent")
    return reviewed, stale


def summarize(data, review):
    entries, stale = validate_reviews(data, review)
    result = {}
    _, current_tree = source_revision()
    clean = source_is_clean()
    for lane in LANES:
        counts = collections.Counter()
        test_observed = test_current = runtime_observed = runtime_current = 0
        for row in data["rows"]:
            key = (row["id"], lane)
            entry = entries.get(key)
            counts["stale" if key in stale else entry["status"] if entry else "pending"] += 1
            if entry and entry.get("testEvidence"):
                test_observed += 1
                if source_revision(entry["reviewedCommit"])[1] == current_tree and clean:
                    test_current += 1
            runtime_entry = entry.get("runtimeObservation", entry) if entry else None
            if runtime_entry and runtime_entry.get("runtimeEvidence"):
                runtime_observed += 1
                if source_revision(runtime_entry["reviewedCommit"])[1] == current_tree and clean:
                    runtime_current += 1
        unresolved = counts["pending"] + counts["stale"]
        applicable = sum(counts[s] for s in ("source-reviewed", "missing", "divergent", "passed"))
        result[lane] = {
            "pending": counts["pending"], "stale": counts["stale"],
            "sourceReviewed": counts["source-reviewed"], "missing": counts["missing"],
            "divergent": counts["divergent"], "notApplicable": counts["not-applicable"],
            "passed": counts["passed"], "applicableReviewed": applicable,
            "testOnlyEvidenceObserved": test_observed, "testOnlyEvidenceCurrent": test_current,
            "runtimeEvidenceObserved": runtime_observed, "runtimeEvidenceCurrent": runtime_current,
            "reviewedDenominator": applicable if review["inventoryReviewed"] and not unresolved else None,
        }
    # Narrow runtime observations do not certify complete RPCs. Full platform,
    # Rust-runtime and live-provider acceptance still require their own verifiers.
    ready = clean and review["inventoryReviewed"] and not stale and all(
        lane["reviewedDenominator"] is not None and lane["reviewedDenominator"] > 0
        and lane["passed"] == lane["reviewedDenominator"] for lane in result.values())
    return result, ready, len(entries), len(stale)


def self_test(data, review):
    import copy
    def reject(mutate):
        sample = copy.deepcopy(review)
        mutate(sample)
        try:
            validate_reviews(data, sample)
        except ValueError:
            return
        raise AssertionError("Invalid global review accepted")
    rid, lanes = next(iter(review["rows"].items()))
    lane = next(iter(lanes))
    reject(lambda r: r["rows"].update({"unknown-row": lanes}))
    reject(lambda r: r.update(inventoryReviewed=True))
    reject(lambda r: r["rows"][rid][lane].update(reviewedCommit="HEAD"))
    reject(lambda r: r["rows"][rid][lane].update(rationale=""))
    reject(lambda r: r["rows"][rid][lane].update(scope="host-effect"))
    reject(lambda r: r["rows"][rid][lane].update(status="passed"))
    reject(lambda r: r["rows"][rid][lane].update(evidence={"outcome": "passed"}))
    reject(lambda r: r["rows"][rid][lane].update(testEvidence={"outcome": "passed"}))
    reject(lambda r: r["rows"][rid][lane].update(upstreamTests=["unknown::missing"]))
    reject(lambda r: r["rows"][rid].update({"live-provider": {**lanes[lane], "status": "passed"}}))
    runtime_row = next((key for key, value in review["rows"].items()
                        if value.get("rust-app-server-interop", {}).get("runtimeEvidence")), None)
    if runtime_row is not None:
        def runtime_entry(r):
            return r["rows"][runtime_row]["rust-app-server-interop"]
        reject(lambda r: runtime_entry(r)["runtimeEvidence"].update(receiptSha256="0" * 64))
        reject(lambda r: runtime_entry(r)["runtimeEvidence"].update(cases=[]))
        reject(lambda r: runtime_entry(r).update(scope="typed-contract-only"))
        reject(lambda r: runtime_entry(r).update(status="passed"))
    additional_row = next((key for key, value in review["rows"].items()
                           if value.get("rust-app-server-interop", {}).get("runtimeObservation")), None)
    if additional_row is not None:
        def typed_entry(r):
            return r["rows"][additional_row]["rust-app-server-interop"]
        reject(lambda r: typed_entry(r)["runtimeObservation"].update(reviewedCommit="HEAD"))
        reject(lambda r: typed_entry(r)["runtimeObservation"]["runtimeEvidence"].update(cases=[]))
        reject(lambda r: typed_entry(r).update(scope="rust-runtime"))
    assert not summarize(data, review)[1]
    print("Global review self-test: orphan, mutable revision, unjustified or evidence-free pass and wrong lane rejected.")
