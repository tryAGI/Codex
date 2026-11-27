"""Verify narrow real Rust observations without granting broad parity."""
import hashlib
import json
import re
from pathlib import Path

from realtime_parity_evidence import ROOT, PIN, NAMESPACE, source_revision
from rust_differential_receipt import ASSET, ASSET_SHA256, TAG

CLASS = "RustFileSystemDifferentialTests"
TEST_PATH = f"src/tests/tryAGI.Codex.Tests/{CLASS}.cs"
HEX40 = re.compile(r"[a-f0-9]{40}")
HEX64 = re.compile(r"[a-f0-9]{64}")
EXECUTABLE_SHA256 = "ad83b94c093f0cdd8176a6bc8005a461bdf23a759863e157d40f562b8a9d664d"

BINARY = "BinaryWriteReadAndOverwriteMatchRust"
DIRECTORY = "DirectoryAndMetadataProjectionMatchRust"
INVALID = "InvalidBase64PreservesExistingFileAndMatchesRustError"
RELATIVE = "RelativePathsAreRejectedWithMatchingError"
COPY = "CopyAndRemoveHaveMatchingEffects"
METHOD_CASES = {
    "fs/readFile": (BINARY, RELATIVE),
    "fs/writeFile": (BINARY, INVALID),
    "fs/createDirectory": (DIRECTORY,),
    "fs/getMetadata": (DIRECTORY,),
    "fs/readDirectory": (DIRECTORY,),
    "fs/copy": (COPY,),
    "fs/remove": (COPY,),
}
THREAD_CLASS = "RustThreadLifecycleDifferentialTests"
MODEL_CLASS = "RustModelCatalogDifferentialTests"
THREAD_MODEL_PATH = "src/tests/tryAGI.Codex.Tests/RustThreadModelDifferentialTests.cs"
NAME = "NamedEmptyThreadResumesBeforeAndAfterRestart"
ARCHIVE = "ArchiveEmptyThreadReadAndRestoreMatchRust"
MODEL = "ExplicitCatalogDisplayOrderDefaultsAndPaginationMatchRust"
SUITES = {
    "filesystem": (CLASS, TEST_PATH, (BINARY, DIRECTORY, INVALID, RELATIVE, COPY)),
    "thread-lifecycle": (THREAD_CLASS, THREAD_MODEL_PATH, (NAME, ARCHIVE)),
    "model-catalog": (MODEL_CLASS, THREAD_MODEL_PATH, (MODEL,)),
}
SCENARIOS = {("clientRequests", method): ("filesystem", cases)
             for method, cases in METHOD_CASES.items()}
SCENARIOS.update({
    ("clientRequests", "thread/name/set"): ("thread-lifecycle", (NAME,)),
    ("serverNotifications", "thread/name/updated"): ("thread-lifecycle", (NAME,)),
    ("clientRequests", "thread/archive"): ("thread-lifecycle", (ARCHIVE,)),
    ("clientRequests", "thread/unarchive"): ("thread-lifecycle", (ARCHIVE,)),
    ("serverNotifications", "thread/archived"): ("thread-lifecycle", (ARCHIVE,)),
    ("serverNotifications", "thread/unarchived"): ("thread-lifecycle", (ARCHIVE,)),
    ("clientRequests", "thread/read"): ("thread-lifecycle", (ARCHIVE,)),
    ("clientRequests", "thread/resume"): ("thread-lifecycle", (NAME, ARCHIVE)),
    ("clientRequests", "model/list"): ("model-catalog", (MODEL,)),
})


def validate_runtime_observation(row, entry, evidence, root=ROOT):
    if not isinstance(evidence, dict) or set(evidence) != {
        "lane", "outcome", "csharpCommit", "receipt", "receiptSha256", "cases"
    }:
        raise ValueError("Invalid runtime observation envelope")
    revision = entry.get("reviewedCommit", "")
    if (evidence["lane"] != "rust-runtime-differential" or evidence["outcome"] != "passed"
            or evidence["csharpCommit"] != revision or not HEX40.fullmatch(revision)):
        raise ValueError("Runtime observation revision or lane differs from reviewed mapping")
    scenario = SCENARIOS.get((row.get("direction"), row.get("method")))
    if scenario is None:
        raise ValueError("No independently reviewed Rust differential scenario for this RPC")
    scope, methods = scenario
    cls, test_path, suite_cases = SUITES[scope]
    expected_tests = {f"{test_path}::{cls}.{method}" for method in methods}
    expected_cases = {NAMESPACE + cls + "." + method for method in methods}
    all_cases = {NAMESPACE + cls + "." + method for method in suite_cases}
    if set(entry["test"]) != expected_tests:
        raise ValueError("Runtime observation test references do not match the reviewed RPC")
    if not isinstance(evidence["cases"], list) or len(evidence["cases"]) != len(expected_cases) or set(evidence["cases"]) != expected_cases:
        raise ValueError("Runtime observation must identify exactly the applicable scenarios")
    location = evidence["receipt"]
    if not isinstance(location, str) or not HEX64.fullmatch(evidence["receiptSha256"]):
        raise ValueError("Runtime receipt path or digest is invalid")
    path = (root / location).resolve()
    if not path.is_relative_to((root / "docs/global-parity-evidence").resolve()) or path.suffix != ".json" or not path.is_file():
        raise ValueError("Missing checked-in differential receipt")
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != evidence["receiptSha256"]:
        raise ValueError("Differential receipt digest mismatch")
    receipt = json.loads(raw)
    if (receipt.get("schemaVersion") != 1 or receipt.get("lane") != "rust-runtime-differential"
            or receipt.get("scope") != scope or receipt.get("platform") != "linux-x64"
            or receipt.get("upstreamCommit") != PIN or receipt.get("upstreamRelease") != TAG
            or receipt.get("releaseAsset") != ASSET or receipt.get("releaseAssetSha256") != ASSET_SHA256
            or receipt.get("rustExecutableSha256") != EXECUTABLE_SHA256
            or receipt.get("rustVersion") != "codex-cli 0.172.0"
            or receipt.get("csharpCommit") != revision
            or not HEX64.fullmatch(receipt.get("sourceTree", ""))
            or not HEX64.fullmatch(receipt.get("artifactSha256", ""))):
        raise ValueError("Differential receipt provenance or executable identity differs")
    if (receipt.get("acceptanceEligible") is not True or receipt.get("networkNamespaceIsolated") is not True
            or receipt.get("sourceCleanAtStart") is not True or receipt.get("sourceCleanAtEnd") is not True
            or receipt.get("sourceTreeAtEnd") != receipt["sourceTree"]
            or receipt.get("diagnostic") is not None or receipt.get("exitCode") != 0):
        raise ValueError("Differential run is diagnostic only, unisolated, dirty, incomplete or failed")
    cases = receipt.get("cases")
    if not isinstance(cases, dict) or set(cases) != all_cases:
        raise ValueError("Expected the complete independently reviewed differential suite")
    if any(case.get("method") != name or case.get("outcome") != "Passed" for name, case in cases.items()):
        raise ValueError("Differential suite contains a failed, skipped or mismatched test case")
    if source_revision(revision)[1] != receipt["sourceTree"]:
        raise ValueError("Differential source tree differs from the immutable tested commit")
    return receipt["sourceTree"]
