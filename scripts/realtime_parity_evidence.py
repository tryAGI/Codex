#!/usr/bin/env python3
"""Run the credential-free parity suite and produce a sanitized revision-bound receipt.

The source fingerprint covers the build inputs, not documentation/evidence commits.
Refuse dirty inputs both before and after execution. Do not run live tests here.
"""
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PIN = "3d86e43b075adeba2193ec5c22ac92f55afd82e6"
SOURCE_PATHS = (
    "src", "specs", "global.json", "Codex.slnx", "Directory.Build.props",
    "Directory.Build.targets", "Directory.Packages.props", "NuGet.config",
    "nuget.config", "packages.lock.json",
)
PROJECT = "src/tests/tryAGI.Codex.Tests/tryAGI.Codex.Tests.csproj"
NAMESPACE = "tryAGI.Codex.Tests."
# Only explicitly reviewed offline classes may be selected by this runner.
OFFLINE_CLASSES = (
    "RustParityFixtureTests", "RealtimeEventParityTests", "RealtimeSocketParityTests",
    "RealtimeSessionParityTests", "RealtimeAttachmentTests", "RealtimeSessionJournalTests",
    "RealtimeTranscriptAccumulatorTests", "RealtimeFeedbackStreamTests", "RealtimeApiCallTests",
    "CodexResponsesWebSocketTests", "CodexResponsesRoutingTests", "CodexRetainedContextTests",
    "CodexObservedUsageTests", "RealtimeFeedbackCorrelationTests",
    "NativeProtocolTests", "RealtimeModelRouteTests", "RealtimeHistoryTests",
)
TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
HEX40 = re.compile(r"[a-f0-9]{40}")
HEX64 = re.compile(r"[a-f0-9]{64}")


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True).stdout


def source_revision(revision="HEAD"):
    commit = git("rev-parse", "--verify", revision + "^{commit}").decode().strip()
    tree = git("ls-tree", "-z", commit, "--", *SOURCE_PATHS)
    if not HEX40.fullmatch(commit) or not tree:
        raise ValueError("Could not resolve source revision")
    return commit, hashlib.sha256(tree).hexdigest()


def source_is_clean():
    return not git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--",
                   *SOURCE_PATHS, ":(exclude,glob)**/TestResults/**")


def require_clean_source():
    if not source_is_clean():
        raise ValueError("Source/build inputs contain concurrent changes; no revision-bound receipt may be created")
    return source_revision()


def parse_trx(raw):
    root = ET.fromstring(raw)
    summary = root.find("t:ResultSummary/t:Counters", TRX_NS)
    if summary is None:
        raise ValueError("TRX has no counters")
    definitions = {}
    for test in root.findall("t:TestDefinitions/t:UnitTest", TRX_NS):
        method = test.find("t:TestMethod", TRX_NS)
        if method is None or not test.get("id") or test.get("id") in definitions:
            raise ValueError("Missing or duplicate TRX test definition")
        class_name = method.get("className", "")
        method_name = method.get("name", "")
        if not class_name.startswith(NAMESPACE) or class_name[len(NAMESPACE):] not in OFFLINE_CLASSES or not method_name:
            raise ValueError(f"Unexpected test in offline receipt: {class_name}.{method_name}")
        definitions[test.get("id")] = class_name + "." + method_name
    cases = {}
    for result in root.findall("t:Results/t:UnitTestResult", TRX_NS):
        method = definitions.get(result.get("testId"))
        name = result.get("testName", "")
        if not method or not name or name in cases:
            raise ValueError("Missing or duplicate TRX case")
        # Display names include the arguments of each xUnit theory case.
        if name != method and not name.startswith(method + "("):
            raise ValueError(f"Unexpected case identity: {name}")
        outcome = result.get("outcome", "")
        if not outcome:
            raise ValueError(f"Case has no outcome: {name}")
        cases[name] = {"method": method, "outcome": outcome}
    if not cases or len(cases) != int(summary.get("total", "-1")):
        raise ValueError("TRX case count does not match total")
    if sum(c["outcome"] == "Passed" for c in cases.values()) != int(summary.get("passed", "-1")):
        raise ValueError("TRX passed count does not match cases")
    return dict(sorted(cases.items()))


def run(output, host_admission):
    before = require_clean_source()
    with tempfile.TemporaryDirectory(prefix="codex-parity-") as tmp:
        selector = "|".join(f"FullyQualifiedName~{NAMESPACE}{c}." for c in OFFLINE_CLASSES)
        command = ["dotnet", "test", PROJECT, "--filter", selector,
                   "--results-directory", tmp, "--logger", "trx;LogFileName=parity.trx"]
        if host_admission:
            command = ["host-build-admission", "--", *command]
        completed = subprocess.run(command, cwd=ROOT)
        after = require_clean_source()
        if before[1] != after[1]:
            raise ValueError("Source tree changed during the test run; receipt discarded")
        trx = Path(tmp) / "parity.trx"
        if not trx.is_file():
            raise ValueError(f"No completed test artifact (exit {completed.returncode})")
        raw = trx.read_bytes()
        cases = parse_trx(raw)
        receipt = {
            "schemaVersion": 1, "upstreamCommit": PIN, "lane": "offline",
            "csharpCommit": before[0], "sourceTree": before[1],
            "artifactSha256": hashlib.sha256(raw).hexdigest(),
            "exitCode": completed.returncode, "cases": cases,
        }
        if output.exists():
            raise ValueError("Refusing to overwrite an existing evidence receipt")
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("x", encoding="utf-8") as file:
            file.write(json.dumps(receipt, indent=2, sort_keys=True) + "\n")
        counts = {}
        for case in cases.values():
            counts[case["outcome"]] = counts.get(case["outcome"], 0) + 1
        print(json.dumps({"csharpCommit": before[0], "sourceTree": before[1],
                          "counts": counts, "exitCode": completed.returncode}, sort_keys=True))
        return completed.returncode


def validate_receipt(row, evidence, root=ROOT):
    """Validate that an offline row references real, exact passed test cases."""
    location = evidence.get("receipt")
    digest = evidence.get("receiptSha256", "")
    if not isinstance(location, str) or not HEX64.fullmatch(digest):
        raise ValueError("Missing receipt path or digest")
    path = (root / location).resolve()
    allowed = (root / "docs/realtime-parity-evidence").resolve()
    if not path.is_relative_to(allowed) or path.suffix != ".json" or not path.is_file():
        raise ValueError(f"Missing checked-in receipt: {location}")
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != digest:
        raise ValueError(f"Receipt digest mismatch: {location}")
    receipt = json.loads(raw)
    if (receipt.get("schemaVersion") != 1 or receipt.get("upstreamCommit") != PIN
            or receipt.get("lane") != "offline" or receipt.get("csharpCommit") != evidence.get("csharpCommit")
            or not HEX64.fullmatch(receipt.get("sourceTree", ""))
            or not HEX64.fullmatch(receipt.get("artifactSha256", ""))):
        raise ValueError(f"Invalid receipt provenance: {location}")
    if row["lane"] == "live":
        raise ValueError("Offline TRX cannot certify live provider scenarios")
    if receipt.get("exitCode") != 0:
        raise ValueError("A failed suite is diagnostic, not acceptance evidence")
    cases = evidence.get("cases")
    if not isinstance(cases, list) or not cases or len(cases) != len(set(cases)):
        raise ValueError("Pass must identify exact unique test cases")
    references = row.get("test")
    if isinstance(references, str):
        references = [references]
    if not isinstance(references, list) or not references:
        raise ValueError("Missing exact test reference")
    receipt_cases = receipt.get("cases", {})
    required = set()
    for reference in references:
        source, sep, method = reference.partition("::")
        if not sep or not method or not (root / source).is_file():
            raise ValueError(f"Invalid test reference: {reference}")
        name = method if method.startswith(NAMESPACE) else NAMESPACE + method
        if "(" in name:
            # An explicit xUnit display name restricts the reviewed mapping to one theory case.
            matched = {name} if name in receipt_cases else set()
        else:
            # A method-wide mapping requires every discovered theory case, not a green subset.
            matched = {n for n, c in receipt_cases.items() if c.get("method") == name}
        if not matched:
            raise ValueError(f"Receipt does not contain the mapped test: {reference}")
        required.update(matched)
    if set(cases) != required:
        raise ValueError("Receipt does not cover exactly every mapped test case")
    for name in required:
        if receipt_cases[name].get("outcome") != "Passed":
            raise ValueError(f"Missing, skipped or failed test: {name}")
    return receipt["sourceTree"]


def self_test():
    def document(outcome, total=1):
        return f"""<TestRun xmlns="{TRX_NS['t']}">
          <Results><UnitTestResult testId="a" testName="{NAMESPACE}RustParityFixtureTests.Example" outcome="{outcome}"/></Results>
          <TestDefinitions><UnitTest id="a"><TestMethod className="{NAMESPACE}RustParityFixtureTests" name="Example"/></UnitTest></TestDefinitions>
          <ResultSummary><Counters total="{total}" passed="{int(outcome == 'Passed')}"/></ResultSummary></TestRun>""".encode()
    assert list(parse_trx(document("Passed")).values())[0]["outcome"] == "Passed"
    assert list(parse_trx(document("NotExecuted")).values())[0]["outcome"] == "NotExecuted"
    try:
        parse_trx(document("Passed", total=2))
    except ValueError:
        pass
    else:
        raise AssertionError("Mismatched TRX totals accepted")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        test_file = root / "test.cs"
        test_file.write_text("test")
        location = "docs/realtime-parity-evidence/test.json"
        path = root / location
        path.parent.mkdir(parents=True)
        case_name = NAMESPACE + "RustParityFixtureTests.Example"
        receipt = {"schemaVersion": 1, "upstreamCommit": PIN, "lane": "offline",
                   "csharpCommit": "a" * 40, "sourceTree": "b" * 64,
                   "artifactSha256": "c" * 64, "exitCode": 0,
                   "cases": {case_name: {"method": case_name, "outcome": "Passed"}}}
        row = {"lane": "wire", "test": "test.cs::RustParityFixtureTests.Example"}
        evidence = {"receipt": location, "csharpCommit": receipt["csharpCommit"], "cases": [case_name]}
        def save():
            raw = json.dumps(receipt).encode()
            path.write_bytes(raw)
            evidence["receiptSha256"] = hashlib.sha256(raw).hexdigest()
        def rejects():
            try:
                validate_receipt(row, evidence, root)
            except ValueError:
                return
            raise AssertionError("Invalid acceptance receipt was accepted")
        save()
        assert validate_receipt(row, evidence, root) == "b" * 64
        receipt["cases"][case_name]["outcome"] = "NotExecuted"
        save()
        rejects()
        receipt["cases"][case_name]["outcome"] = "Passed"
        receipt["exitCode"] = 1
        save()
        rejects()
        receipt["exitCode"] = 0
        save()
        row["lane"] = "live"
        rejects()
        row["lane"] = "wire"
        evidence["receiptSha256"] = "0" * 64
        rejects()
        save()
        evidence["cases"] = [case_name + "Missing"]
        rejects()
        evidence["cases"] = [case_name]
        receipt["cases"][case_name + "(case: 2)"] = {"method": case_name, "outcome": "NotExecuted"}
        save()
        rejects()
    print("Evidence self-test: TRX identity, counts, skipped/failed cases, receipt digests and lane isolation verified.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("run", "self-test"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--host-admission", action="store_true")
    args = parser.parse_args()
    if args.command == "self-test":
        self_test()
        return 0
    if not args.output:
        parser.error("run requires --output")
    return run(args.output.resolve(), args.host_admission)


if __name__ == "__main__":
    raise SystemExit(main())
