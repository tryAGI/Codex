#!/usr/bin/env python3
"""Prepare a private exact-source native GPT Live probe; run only after explicit authorization.

No provider requests, credential reads or login in prepare/self-test. The live
result is diagnostic evidence, not an independently attested acceptance receipt.
"""
import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import wave

from realtime_parity_evidence import ROOT, git, require_clean_source, parse_trx
from realtime_parity_live_probe import (
    DIRECT_ROUTE, WEBRTC_ROUTE, PROBE_METHOD, WEBRTC_PROBE_METHOD, inspect_files,
)

TEST_PROJECT = "src/tests/tryAGI.Codex.Tests/tryAGI.Codex.Tests.csproj"
SAMPLE_PROJECT = "samples/tryAGI.Codex.Sample/tryAGI.Codex.Sample.csproj"
OFFLINE = ("NativeRealtimeLiveGuardTests", "NativeRealtimeWebRtcMediaPeerTests")
ROUTES = {"direct": (DIRECT_ROUTE, PROBE_METHOD, "CODEX_NATIVE_REALTIME_LIVE"),
          "webrtc": (WEBRTC_ROUTE, WEBRTC_PROBE_METHOD, "CODEX_NATIVE_WEBRTC_LIVE")}
PROMPTS = ("Ask the backing agent for the verification phrase.",
           "Ask the backing agent to repeat the same verification phrase from the previous turn.")


def require(ok, message):
    if not ok:
        raise ValueError(message)


def digest(path):
    require(path.is_file() and not path.is_symlink(), "Expected a regular artifact")
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")


def safe_env():
    # Do not inherit any existing Codex/OpenAI credentials or opt-in flags.
    return {key: value for key, value in os.environ.items()
            if not key.upper().startswith(("CODEX_", "OPENAI_", "CHATGPT_", "AZURE_OPENAI_"))}


def clean_revision():
    commit, source_tree = require_clean_source()
    require(not git("status", "--porcelain=v1", "-z", "--untracked-files=all",
                    "--", "samples", "scripts/run-native-realtime-probe.py",
                    "scripts/realtime_parity_live_probe.py", "scripts/realtime_parity_evidence.py",
                    "scripts/realtime-parity.py", "docs/realtime-parity-matrix.json",
                    "docs/realtime-parity-reviews.json", "docs/realtime-parity-evidence"),
            "Probe runner, OAuth sample or parity gate inputs are not committed")
    return {"commit": commit, "sourceTree": source_tree,
            "gitTree": git("rev-parse", "HEAD^{tree}").decode().strip()}


def private_output(value, create=False):
    path = Path(value).absolute()
    require(path.resolve() == path, "Output path must not follow symlinks")
    require(not path.is_relative_to(ROOT.resolve()), "Keep probe output outside the repository")
    if create:
        path.mkdir(mode=0o700, parents=False, exist_ok=False)
    mode = path.stat()
    require(path.is_dir() and not path.is_symlink() and mode.st_uid == os.getuid()
            and stat.S_IMODE(mode.st_mode) & 0o077 == 0, "Expected an owner-only output directory")
    return path


def pcm(path):
    with path.open("rb") as stream:
        value = stream.read(384001)
    require(960 <= len(value) <= 384000 and len(value) % 2 == 0 and
            any(value) and not value.startswith(b"RIFF"), "Expected bounded non-silent raw PCM16")
    return value


def prepare_clips(out, first, followup):
    if first or followup:
        require(first and followup, "Supply both synthetic PCM files")
        clips = [pcm(Path(first)), pcm(Path(followup))]
    else:
        require(sys.platform == "darwin" and shutil.which("say") and shutil.which("afconvert"),
                "Supply two synthetic PCM files on hosts without local macOS speech tools")
        clips = []
        with tempfile.TemporaryDirectory(prefix="speech-", dir=out) as temp:
            for index, prompt in enumerate(PROMPTS):
                aiff = Path(temp) / f"{index}.aiff"
                wav = Path(temp) / f"{index}.wav"
                subprocess.run(["say", "-v", "Samantha", "-o", str(aiff), prompt],
                               check=True, timeout=30, env=safe_env())
                subprocess.run(["afconvert", "-f", "WAVE", "-d", "LEI16@24000", "-c", "1",
                                str(aiff), str(wav)], check=True, timeout=20, env=safe_env())
                with wave.open(str(wav), "rb") as audio:
                    require(audio.getnchannels() == 1 and audio.getsampwidth() == 2
                            and audio.getframerate() == 24000 and audio.getnframes() <= 192000,
                            "Local speech did not produce bounded 24 kHz mono PCM16")
                    clips.append(audio.readframes(audio.getnframes()))
    require(clips[0] != clips[1], "The two synthetic prompts must differ")
    for name, value in zip(("first.pcm", "followup.pcm"), clips):
        path = out / name
        with path.open("xb") as stream:
            stream.write(value)
        pcm(path)


def route_list(value):
    return ["direct", "webrtc"] if value == "both" else [value]


def verify_local_ip(routes, address):
    if "webrtc" not in routes:
        require(address is None, "A direct-only probe does not need an ICE interface")
        return
    require(address is not None, "Supply the selected active local IPv4 interface for WebRTC")
    parsed = ipaddress.IPv4Address(address)
    require(not (parsed.is_loopback or parsed.is_unspecified or parsed.is_multicast or parsed.is_link_local),
            "Use a non-loopback local IPv4 interface")
    # Verify the address belongs to this host without sending any network traffic.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind((str(parsed), 0))


def admitted_build(project, artifacts, extra=()):
    subprocess.run(["host-build-admission", "--", "dotnet", "build", project, "-c", "Release",
                    "--artifacts-path", str(artifacts), "-m:1",
                    "-p:InstallAdvAfterBuild=false", "-p:VerifyAdvAfterBuild=false",
                    "-p:GeneratePackageOnBuild=false", *extra],
                   cwd=ROOT, env=safe_env(), timeout=600, check=True)


def assembly(out, project):
    return out / "artifacts" / "bin" / project / "release" / (project + ".dll")


def inventory(out):
    result = {}
    for name in ("tryAGI.Codex.Tests", "tryAGI.Codex.Sample"):
        entry = assembly(out, name)
        require(entry.is_file(), "Expected exact release assembly is missing")
        for path in sorted(entry.parent.rglob("*")):
            require(not path.is_symlink(), "Unexpected symlink in execution inputs")
            if path.is_file():
                result[path.relative_to(out).as_posix()] = digest(path)
    for name in ("first.pcm", "followup.pcm"):
        result[name] = digest(out / name)
    return result


def vstest(dll, selector, results, env, timeout):
    results.mkdir(mode=0o700, exist_ok=False)
    command = ["dotnet", "vstest", str(dll), "--TestCaseFilter:" + selector,
               "--ResultsDirectory:" + str(results), "--Logger:trx;LogFileName=probe.trx"]
    with (results / "runner.log").open("xb") as log:
        result = subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                                timeout=timeout, check=False)
    require(result.returncode == 0, "Test failed; inspect private runner.log locally")
    trx = results / "probe.trx"
    require(trx.is_file(), "Missing test result")
    return trx


def prepare(args):
    source = clean_revision()
    routes = route_list(args.route)
    require(re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", args.model), "Supply an explicit supported reasoning model")
    verify_local_ip(routes, args.local_ip)
    out = private_output(args.output, create=True)
    prepare_clips(out, args.first, args.followup)
    artifacts = out / "artifacts"
    admitted_build(TEST_PROJECT, artifacts)
    admitted_build(SAMPLE_PROJECT, artifacts, ("-p:UseLocalProject=true",))
    selector = "|".join("FullyQualifiedName~tryAGI.Codex.Tests." + cls + "." for cls in OFFLINE)
    before = inventory(out)
    trx = vstest(assembly(out, "tryAGI.Codex.Tests"), selector, out / "offline", safe_env(), 90)
    cases = verify_offline_trx(trx)
    require(source == clean_revision() and before == inventory(out), "Prepared source or artifacts changed")
    write_json(out / "prepared.json", {"schemaVersion": 1, **source, "routes": routes, "model": args.model,
               "localIp": args.local_ip, "inputs": before, "offlineTrxSha256": digest(trx),
               "offlinePassed": len(cases), "providerRequests": 0})
    print(json.dumps({"prepared": True, "commit": source["commit"], "offlinePassed": len(cases),
                      "routes": routes, "providerRequests": 0}))


def verify_offline_trx(trx):
    cases = parse_trx(trx.read_bytes(), offline_classes=OFFLINE)
    require(all(case["outcome"] == "Passed" for case in cases.values()) and
            all(any("." + cls + "." in case["method"] for case in cases.values()) for cls in OFFLINE),
            "Offline media and guard suite must pass without skips")
    return cases


def offline_gate(source):
    report = json.loads(subprocess.check_output(
        [sys.executable, "-B", str(ROOT / "scripts/realtime-parity.py"), "report"], cwd=ROOT, env=safe_env()))
    require(report["sourceClean"] and report["sourceTree"] == source["sourceTree"]
            and report["inventoryReviewed"] and all(
                report["lanes"][name]["passed"] == report["lanes"][name]["denominator"]
                and report["lanes"][name]["staleEvidence"] == 0
                and report["lanes"][name]["pendingApplicability"] == 0
                for name in ("wire", "behavior")), "Offline parity acceptance is not complete; OAuth was not started")


def read_secret(directory, name):
    path = directory / name
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
            and stat.S_IMODE(info.st_mode) & 0o077 == 0 and 0 < info.st_size <= 16384,
            "Credential-only sample returned an invalid private file")
    return path.read_text(encoding="utf-8").strip()


def run(args):
    out = private_output(args.output)
    prepared = json.loads((out / "prepared.json").read_text(encoding="utf-8"))
    source = clean_revision()
    require(prepared["schemaVersion"] == 1 and all(prepared[key] == val for key, val in source.items())
            and prepared["inputs"] == inventory(out)
            and prepared["offlineTrxSha256"] == digest(out / "offline/probe.trx"),
            "Prepared revision, media inputs or binary evidence changed")
    require(prepared["offlinePassed"] == len(verify_offline_trx(out / "offline/probe.trx")) and
            prepared["providerRequests"] == 0, "Invalid offline preparation evidence")
    require(re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", prepared["model"]), "Invalid prepared reasoning model")
    routes = prepared["routes"]
    require(routes in (["direct"], ["webrtc"], ["direct", "webrtc"]), "Unexpected route selection")
    verify_local_ip(routes, prepared["localIp"])
    offline_gate(source)
    require(args.confirm_live and sys.stdin.isatty() and sys.stderr.isatty(),
            "Run interactively on the selected host after explicit live budget authorization")
    # The attempt marker is exclusive and precedes OAuth: never retry a possibly executed session.
    write_json(out / "live-attempt.json", {"commit": source["commit"], "routes": routes,
              "maxReasoningRequestsPerRoute": 5, "automaticRetry": False})
    print("Approve the one-time ChatGPT device login in your browser. No credential is printed by this runner.",
          file=sys.stderr)
    env = safe_env()
    with tempfile.TemporaryDirectory(prefix="oauth-", dir=out) as temp:
        directory = Path(temp)
        subprocess.run(["dotnet", str(assembly(out, "tryAGI.Codex.Sample")), "--live-credentials", str(directory)],
                       cwd=ROOT, env=env, timeout=300, check=True)
        env.update(CODEX_ACCESS_TOKEN=read_secret(directory, "access-token"),
                   CODEX_ACCOUNT_ID=read_secret(directory, "account-id"))
    try:
        for route in routes:
            require(source == clean_revision() and prepared["inputs"] == inventory(out), "Prepared execution inputs changed")
            label, method, flag = ROUTES[route]
            trace = out / (route + ".json")
            route_env = dict(env, CODEX_MODEL=prepared["model"], CODEX_NATIVE_REALTIME_REVISION=source["commit"],
                             CODEX_NATIVE_REALTIME_PCM_FIRST=str(out / "first.pcm"),
                             CODEX_NATIVE_REALTIME_PCM_FOLLOWUP=str(out / "followup.pcm"),
                             CODEX_NATIVE_REALTIME_TRACE=str(trace))
            if route == "webrtc":
                route_env["CODEX_NATIVE_WEBRTC_LOCAL_IP"] = prepared["localIp"]
            route_env[flag] = "1"
            trx = vstest(assembly(out, "tryAGI.Codex.Tests"), "FullyQualifiedName=" + method,
                         out / route, route_env, 150)
            require(source == clean_revision() and prepared["inputs"] == inventory(out), "Executed source or binary changed")
            observed = inspect_files(trace, trx, source["commit"])
            require(observed["route"] == label, "Wrong route result")
            write_json(out / (route + "-diagnostic.json"), {"inspection": observed,
                       "preparedManifestSha256": digest(out / "prepared.json"),
                       "testAssemblySha256": digest(assembly(out, "tryAGI.Codex.Tests")),
                       "sourceAndArtifactsUnchanged": True, "independentAcceptance": False})
            print(json.dumps({"route": route, "diagnosticPassed": True, "liveAcceptanceIssued": False}))
    finally:
        env.pop("CODEX_ACCESS_TOKEN", None)
        env.pop("CODEX_ACCOUNT_ID", None)


def self_test():
    require(route_list("both") == ["direct", "webrtc"], "Route selection failed")
    require(ROUTES["direct"][1] != ROUTES["webrtc"][1] and ROUTES["direct"][2] != ROUTES["webrtc"][2],
            "Routes must remain distinct")
    with tempfile.TemporaryDirectory() as temp:
        out = private_output(temp)
        first = out / "a.pcm"
        second = out / "b.pcm"
        first.write_bytes(b"\x01\x00" * 480)
        second.write_bytes(b"\x02\x00" * 480)
        prepare_clips(out, str(first), str(second))
        require(digest(out / "first.pcm") == digest(first), "PCM copy mismatch")
        secret = out / "secret"
        secret.write_text("synthetic-test-fixture")
        secret.chmod(0o600)
        require(read_secret(out, "secret") == "synthetic-test-fixture", "Private credential file check failed")
        secret.chmod(0o644)
        try:
            read_secret(out, "secret")
        except ValueError:
            pass
        else:
            raise AssertionError("Public credential file was accepted")
        for invalid in (b"\x00" * 960, b"RIFF" + b"x" * 956, b"x" * 961, b"x" * 384002):
            first.write_bytes(invalid)
            try:
                pcm(first)
            except ValueError:
                pass
            else:
                raise AssertionError("Invalid PCM accepted")
        marker = out / "attempt.json"
        write_json(marker, {"fixture": True})
        try:
            write_json(marker, {"fixture": False})
        except FileExistsError:
            pass
        else:
            raise AssertionError("Attempt marker was overwritten")
    require(not any(key.upper().startswith(("CODEX_", "OPENAI_", "CHATGPT_")) for key in safe_env()),
            "Inherited provider settings")
    print("Native probe runner self-test passed; no provider request or OAuth.")


def main():
    if os.name != "posix":
        raise SystemExit("Use the selected POSIX development host for the private interactive OAuth runner.")
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("self-test")
    prep = commands.add_parser("prepare")
    prep.add_argument("--route", choices=("direct", "webrtc", "both"), required=True)
    prep.add_argument("--model", required=True)
    prep.add_argument("--output", required=True)
    prep.add_argument("--local-ip")
    prep.add_argument("--first")
    prep.add_argument("--followup")
    launch = commands.add_parser("run")
    launch.add_argument("--output", required=True)
    launch.add_argument("--confirm-live", action="store_true")
    args = parser.parse_args()
    try:
        {"prepare": prepare, "run": run, "self-test": lambda _: self_test()}[args.command](args)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        # Never dump subprocess environment, raw provider diagnostics, or credential values.
        print("Native probe refused/failed: " + (str(error) if isinstance(error, ValueError) else type(error).__name__),
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
