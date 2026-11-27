#!/usr/bin/env python3
"""Pinned Rust-process differential runner; never a provider or native-only receipt."""
import argparse
import collections
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile

from realtime_parity_evidence import ROOT, PIN, PROJECT, source_revision, require_clean_source, source_is_clean, parse_trx

TAG = "rust-v0.172.0"
ASSET = "codex-package-x86_64-unknown-linux-musl.tar.gz"
ASSET_SHA256 = "63e3b00065ed7f62166d0522e17f663cb866a31d80d03e63365482ed34a1d991"
CLASSES = ("RustFileSystemDifferentialTests",)


def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def network_namespace(parent):
    current = os.readlink("/proc/self/ns/net")
    interfaces = {line.split(":", 1)[0].strip() for line in Path("/proc/net/dev").read_text().splitlines()[2:]}
    if not re.fullmatch(r"net:\[\d+\]", parent) or current == parent or interfaces != {"lo"}:
        raise ValueError("Rust differential tests require a new network namespace with loopback only")
    return current


def run(archive, output, parent_namespace):
    if os.uname().sysname != "Linux" or os.uname().machine != "x86_64":
        raise ValueError("Initial differential runner supports Linux x64 only")
    namespace = network_namespace(parent_namespace)
    if archive.name != ASSET or sha(archive) != ASSET_SHA256:
        raise ValueError("Rust release archive differs from the pinned digest")
    if output.exists():
        raise ValueError("Refusing to overwrite a differential receipt")
    before_commit, before_tree = require_clean_source()
    with tempfile.TemporaryDirectory(prefix="codex-differential-") as tmp:
        root = Path(tmp)
        package = root / "package"
        package.mkdir()
        # The archive is from the explicitly pinned upstream release. Reject
        # escaping links, special files and unsafe archive paths.
        with tarfile.open(archive, "r:gz") as stream:
            stream.extractall(package, filter="data")
        executables = sorted({p.resolve() for name in ("codex", "codex-x86_64-unknown-linux-musl")
                              for p in package.rglob(name) if p.is_file() and os.access(p, os.X_OK)})
        if len(executables) != 1 or not executables[0].is_relative_to(package):
            raise ValueError("Expected exactly one pinned Codex CLI executable")
        executable = executables[0]
        executable_digest = sha(executable)
        home = root / "home"
        home.mkdir()
        # No inherited auth, provider or GitHub credentials. The only network
        # interface is loopback, and the fixture issues filesystem RPCs only.
        env = {"PATH": str(executable.parent) + os.pathsep + os.environ["PATH"],
               "HOME": str(home), "CODEX_HOME": str(home / "codex"),
               "DOTNET_CLI_HOME": str(home), "DOTNET_NOLOGO": "1",
               "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
               "CODEX_RUST_DIFFERENTIAL": "1",
               "CODEX_RUST_DIFFERENTIAL_EXECUTABLE": str(executable),
               "CODEX_RUST_DIFFERENTIAL_PARENT_NETNS": parent_namespace}
        for key in ("DOTNET_ROOT", "NUGET_PACKAGES"):
            if os.environ.get(key):
                env[key] = os.environ[key]
        version = subprocess.check_output([str(executable), "--version"], env=env, cwd=home, text=True, timeout=20).strip()
        if not re.fullmatch(r"codex(?:-cli)? 0\.172\.0", version):
            raise ValueError(f"Unexpected Rust binary version: {version}")
        completed = subprocess.run(
            ["dotnet", "test", PROJECT, "--no-restore", "--no-build",
             "--filter", "FullyQualifiedName~RustFileSystemDifferentialTests",
             "--results-directory", str(root), "--logger", "trx;LogFileName=differential.trx"],
            cwd=ROOT, env=env, timeout=240)
        trx = root / "differential.trx"
        if not trx.is_file():
            raise ValueError(f"No completed differential test artifact (exit {completed.returncode})")
        raw = trx.read_bytes()
        cases = parse_trx(raw, offline_classes=CLASSES)
        after_commit, after_tree = source_revision()
        clean_after = source_is_clean()
        namespace_after = network_namespace(parent_namespace)
        expected_cases = 5
        eligible = (before_commit == after_commit and before_tree == after_tree and clean_after
                    and namespace == namespace_after and sha(executable) == executable_digest
                    and sha(archive) == ASSET_SHA256 and completed.returncode == 0
                    and len(cases) == expected_cases and all(c["outcome"] == "Passed" for c in cases.values()))
        receipt = {"schemaVersion": 1, "lane": "rust-runtime-differential", "scope": "filesystem",
                   "upstreamCommit": PIN, "upstreamRelease": TAG, "releaseAsset": ASSET,
                   "releaseAssetSha256": ASSET_SHA256, "rustExecutableSha256": executable_digest,
                   "rustVersion": version, "platform": "linux-x64",
                   "networkNamespaceIsolated": namespace == namespace_after,
                   "csharpCommit": before_commit, "sourceTree": before_tree,
                   "sourceTreeAtEnd": after_tree, "sourceCleanAtStart": True,
                   "sourceCleanAtEnd": clean_after, "acceptanceEligible": eligible,
                   "artifactSha256": hashlib.sha256(raw).hexdigest(),
                   "exitCode": completed.returncode, "cases": cases}
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("x", encoding="utf-8") as file:
            json.dump(receipt, file, indent=2, sort_keys=True)
            file.write("\n")
        counts = collections.Counter(c["outcome"] for c in cases.values())
        print(json.dumps({"acceptanceEligible": eligible, "counts": counts, "csharpCommit": before_commit}))
        return 0 if eligible else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--parent-netns", required=True)
    args = parser.parse_args()
    return run(args.archive.resolve(), args.output.resolve(), args.parent_netns)


if __name__ == "__main__":
    raise SystemExit(main())
