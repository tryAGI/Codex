#!/usr/bin/env python3
"""Inspect a sanitized direct or WebRTC V3 probe trace and its exact TRX without network I/O.

This is a diagnostic validator, not a live acceptance receipt issuer. A valid
trace is not proof of execution, authorization, playback, or a provider-side
cost ceiling. The parity gate continues to reject live rows until independent
provider evidence and a reviewed execution envelope are implemented.
"""
import argparse
import copy
import datetime as dt
import hashlib
import json
import math
import re
import uuid
from pathlib import Path

from realtime_parity_evidence import parse_trx, source_revision

PROBE_CLASS = "NativeRealtimeLiveAcceptanceTests"
PROBE_METHOD = ("tryAGI.Codex.Tests." + PROBE_CLASS
                + ".DirectAudioDelegationToolFollowupAndHostInitiatedSpeech")
DIRECT_ROUTE = "chatgpt-native-direct-websocket"
WEBRTC_ROUTE = "chatgpt-native-webrtc-sideband"
WEBRTC_PROBE_METHOD = ("tryAGI.Codex.Tests." + PROBE_CLASS
                       + ".WebRtcAudioDelegationToolFollowupAndHostInitiatedSpeech")
HEX40 = re.compile(r"[a-f0-9]{40}")
HEX64 = re.compile(r"[a-f0-9]{64}")
MODEL = re.compile(r"[A-Za-z0-9_.-]{1,128}")
TRACE_KEYS = {
    "schemaVersion", "lane", "route", "protocol", "requestedVoiceModel",
    "requestedReasoningModel", "requestedReasoningTransport", "declaredCsharpCommit",
    "startedAt", "finishedAt", "outcome", "step", "errorType", "threadId",
    "logicalSessionId", "input", "toolCalls", "reasoningRequests", "completed",
    "incomplete", "notDispatched", "reasoningUsage", "stages", "cleanShutdown",
    "media", "interruption", "reconnect", "coverage", "storage", "tokenOrCreditCeiling",
}
STAGE_KEYS = {
    "Name", "UserTranscriptEvents", "AssistantTranscriptEvents", "OutputPcmBytes",
    "AssistantTurnDone", "PhraseObserved", "DelegationDigests",
    "ProviderSessionDigest", "ObservedModel", "VoiceTokens", "BackingTurnId",
}
WEBRTC_TRACE_KEYS = {"providerCallDigest", "mediaClosed", "attachmentBeforeId",
                     "attachmentAfterId", "reattachedCallDigest"}
WEBRTC_MEDIA_KEYS = {"PacketsSent", "PacketsReceived", "BytesReceived", "DecodedSamples",
                     "NonSilentSamples", "ConcealedSamples", "Codec"}
TOKEN_KEYS = {
    "InputTokens", "CachedInputTokens", "CacheWriteInputTokens", "OutputTokens",
    "ReasoningTokens", "TotalTokens", "RolloutBudgetUnits",
}
STAGES = ("audio-delegation-tool-feedback", "follow-up", "host-triggered-reasoning-speech")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def exact_keys(value, keys, label):
    require(type(value) is dict and set(value) == keys, label + ": invalid or unexpected fields")


def matches(pattern, value):
    return isinstance(value, str) and pattern.fullmatch(value) is not None


def bounded(value, minimum, maximum, label):
    require(type(value) is int and minimum <= value <= maximum, label + ": invalid count")


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON field")
        result[key] = value
    return result


def reject_constant(_):
    raise ValueError("Non-finite JSON number")


def load_trace(raw):
    require(len(raw) <= 64 * 1024, "Probe trace exceeds its bounded schema size")
    return json.loads(raw, object_pairs_hook=unique_json, parse_constant=reject_constant)


def parse_time(value):
    require(isinstance(value, str) and len(value) <= 40, "Invalid timestamp")
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(result.tzinfo is not None, "Timestamp lacks UTC offset")
    return result


def canonical_guid(value, label):
    require(isinstance(value, str) and len(value) == 36, label + ": invalid GUID")
    parsed = uuid.UUID(value)
    require(parsed.int != 0 and str(parsed) == value, label + ": invalid GUID")


def usage_tokens(value):
    if value is None:
        return False
    exact_keys(value, TOKEN_KEYS, "Usage")
    for key, number in value.items():
        if number is None:
            continue
        if key == "RolloutBudgetUnits":
            require(type(number) in (float, int) and math.isfinite(number)
                    and 0 <= number <= 10**15, "Invalid rollout budget units")
        else:
            bounded(number, 0, 2**63 - 1, "Observed token count")
    # Never interpret null as zero, or infer total from other counters.
    return value["TotalTokens"] is not None


def inspect_trace(trace, expected_commit):
    require(matches(HEX40, expected_commit), "An immutable C# commit is required")
    require(type(trace) is dict, "Invalid trace")
    web_rtc = trace.get("route") == WEBRTC_ROUTE
    exact_keys(trace, TRACE_KEYS | WEBRTC_TRACE_KEYS if web_rtc else TRACE_KEYS, "Trace")
    for key, expected in {
        "schemaVersion": 1, "lane": "live", "route": WEBRTC_ROUTE if web_rtc else DIRECT_ROUTE,
        "protocol": "v3", "requestedReasoningTransport": "sse",
        "declaredCsharpCommit": expected_commit, "outcome": "passed",
        "step": "clean shutdown", "errorType": None, "cleanShutdown": True,
        "media": "provider-webrtc-rtp-decoded-pcm-not-played" if web_rtc else "provider-pcm-received-not-played",
        "interruption": "not-run",
        "reconnect": "same-call-detach-reattach" if web_rtc else "not-run",
        "coverage": ("webrtc-connect-two-voice-turns-same-call-reattach-host-speech-clean-end" if web_rtc
                     else "direct-connect-two-voice-turns-host-triggered-reasoning-speech-clean-end-only"),
        "storage": "process-local-cas-fixture",
        "tokenOrCreditCeiling": "not-enforced-by-provider",
    }.items():
        require(type(trace[key]) is type(expected) and trace[key] == expected, "Invalid " + key)
    if web_rtc:
        require(matches(HEX64, trace["providerCallDigest"]) and
                trace["reattachedCallDigest"] == trace["providerCallDigest"], "WebRTC call identity changed")
        canonical_guid(trace["attachmentBeforeId"], "Original attachment")
        canonical_guid(trace["attachmentAfterId"], "Successor attachment")
        require(trace["attachmentBeforeId"] != trace["attachmentAfterId"], "Reused attachment fence")
        require(trace["mediaClosed"] is True, "Missing media shutdown")
    for key in ("requestedVoiceModel", "requestedReasoningModel"):
        require(matches(MODEL, trace[key]), "Invalid bounded model identifier")
    canonical_guid(trace["threadId"], "Thread")
    require(matches(re.compile(r"[a-f0-9]{32}"), trace["logicalSessionId"])
            and int(trace["logicalSessionId"], 16) != 0, "Invalid logical session identity")
    elapsed = (parse_time(trace["finishedAt"]) - parse_time(trace["startedAt"])).total_seconds()
    require(0 < elapsed <= 120, "Probe exceeded active and cleanup time bounds")

    clips = trace["input"]
    require(type(clips) is list and len(clips) == 2, "Expected two input clips")
    for clip in clips:
        exact_keys(clip, {"sha256", "bytes"}, "PCM input")
        require(matches(HEX64, clip["sha256"]), "Invalid PCM digest")
        bounded(clip["bytes"], 960, 48000 * 8, "PCM input bytes")
        require(clip["bytes"] % 2 == 0, "Unaligned PCM input")
    require(clips[0]["sha256"] != clips[1]["sha256"], "Input clips must differ")

    bounded(trace["toolCalls"], 1, 1, "Tool calls")
    # First turn requires a tool round trip, plus follow-up and host reasoning.
    bounded(trace["reasoningRequests"], 4, 5, "Reasoning reservations")
    bounded(trace["completed"], trace["reasoningRequests"], trace["reasoningRequests"], "Completed requests")
    bounded(trace["incomplete"], 0, 0, "Incomplete requests")
    bounded(trace["notDispatched"], 0, 0, "Undispatched reservations")
    receipts = trace["reasoningUsage"]
    require(type(receipts) is list and len(receipts) == trace["completed"], "Missing reasoning usage receipt")
    requests, responses = set(), set()
    known_reasoning = 0
    missing_response_ids = 0
    for receipt in receipts:
        exact_keys(receipt, {"RequestDigest", "ResponseDigest", "Tokens"}, "Reasoning receipt")
        request, response = receipt["RequestDigest"], receipt["ResponseDigest"]
        require(matches(HEX64, request) and request not in requests, "Duplicate or invalid request identity")
        requests.add(request)
        if response is None:
            missing_response_ids += 1
        else:
            require(matches(HEX64, response) and response not in responses, "Duplicate or invalid response identity")
            responses.add(response)
        known_reasoning += usage_tokens(receipt["Tokens"])

    stages = trace["stages"]
    require(type(stages) is list and len(stages) == len(STAGES), "Incomplete stage coverage")
    turns, handoffs, sessions, models = set(), set(), set(), set()
    known_voice = 0
    received_pcm = 0
    web_packets = web_bytes = web_samples = web_non_silent = 0
    for i, stage in enumerate(stages):
        exact_keys(stage, STAGE_KEYS | {"WebRtcMedia"} if web_rtc else STAGE_KEYS, "Voice stage")
        require(stage["Name"] == STAGES[i], "Unexpected stage order")
        bounded(stage["UserTranscriptEvents"], 1 if i < 2 else 0,
                10000 if i < 2 else 0, "User transcript events")
        bounded(stage["AssistantTranscriptEvents"], 0, 10000, "Assistant transcript events")
        bounded(stage["OutputPcmBytes"], 0 if web_rtc else 2, 48000 * 20, "Sideband PCM bytes")
        require(stage["OutputPcmBytes"] % 2 == 0, "Unaligned output PCM")
        received_pcm += stage["OutputPcmBytes"]
        if web_rtc:
            media = stage["WebRtcMedia"]
            exact_keys(media, WEBRTC_MEDIA_KEYS, "Native WebRTC media")
            sent = (clips[i]["bytes"] + 959) // 960 + 50 if i < 2 else 0
            bounded(media["PacketsSent"], sent, sent, "Paced RTP sends")
            bounded(media["PacketsReceived"], 1, 40000, "Authenticated RTP packets")
            bounded(media["BytesReceived"], media["PacketsReceived"],
                    1275 * media["PacketsReceived"], "Encoded Opus bytes")
            bounded(media["DecodedSamples"], 1024,
                    min(24000 * 90, 2880 * media["PacketsReceived"]), "Decoded PCM samples")
            bounded(media["NonSilentSamples"], 1024, media["DecodedSamples"], "Non-silent PCM samples")
            bounded(media["ConcealedSamples"], 0, 0, "Synthesized samples")
            require(media["Codec"] == "audio/opus", "Unexpected WebRTC codec")
            web_packets += media["PacketsReceived"]
            web_bytes += media["BytesReceived"]
            web_samples += media["DecodedSamples"]
            web_non_silent += media["NonSilentSamples"]
        require(stage["AssistantTurnDone"] is True and stage["PhraseObserved"] is True,
                "Missing terminal phrase receipt")
        canonical_guid(stage["BackingTurnId"], "Backing turn")
        require(stage["BackingTurnId"] not in turns, "Reused backing turn identity")
        turns.add(stage["BackingTurnId"])
        require(matches(HEX64, stage["ProviderSessionDigest"]), "Missing provider session identity")
        sessions.add(stage["ProviderSessionDigest"])
        observed_model = stage["ObservedModel"]
        if observed_model is not None:
            require(matches(MODEL, observed_model), "Invalid observed model")
            models.add(observed_model)
        known_voice += usage_tokens(stage["VoiceTokens"])
        delegation = stage["DelegationDigests"]
        require(type(delegation) is list and len(delegation) == (1 if i < 2 else 0),
                "Unexpected delegation count")
        for digest in delegation:
            require(matches(HEX64, digest) and digest not in handoffs, "Duplicate or invalid delegation")
            handoffs.add(digest)
    require(len(sessions) == 1, "Provider session changed during probe")
    require(len(models) <= 1, "Observed voice model changed during probe")
    if web_rtc:
        bounded(web_samples, 3072, 24000 * 120, "Total decoded media samples")
    return {
        "diagnosticOnly": True,
        "eligibleForLiveAcceptance": False,
        "route": trace["route"],
        "coverage": list(STAGES),
        "completedReasoningRequests": trace["completed"],
        "reasoningReceiptsMissingResponseId": missing_response_ids,
        "reasoningReceiptsWithReportedTotal": known_reasoning,
        "voiceSnapshotsWithReportedTotal": known_voice,
        "receivedPcmBytes": received_pcm,
        "receivedWebRtcPackets": web_packets,
        "receivedWebRtcOpusBytes": web_bytes,
        "decodedWebRtcPcmSamples": web_samples,
        "nonSilentWebRtcPcmSamples": web_non_silent,
        "sameCallReattachmentReported": web_rtc,
        "playbackVerified": False,
        "interruptionVerified": False,
        "reconnectVerified": False,
        "providerCostCeilingVerified": False,
        "authorizationVerified": False,
        "executionProvenanceVerified": False,
    }


def inspect_trx(raw, route=DIRECT_ROUTE):
    require(len(raw) <= 4 * 1024 * 1024, "TRX exceeds the probe bound")
    # Reuse the exact case parser with a single explicit class, never the offline
    # receipt issuer. No mixed suite or skipped result may stand for this probe.
    cases = parse_trx(raw, offline_classes=(PROBE_CLASS,))
    require(route in (DIRECT_ROUTE, WEBRTC_ROUTE), "Unexpected probe route")
    method = WEBRTC_PROBE_METHOD if route == WEBRTC_ROUTE else PROBE_METHOD
    require(set(cases) == {method}
            and cases[method] == {"method": method, "outcome": "Passed"},
            "TRX does not contain exactly the successful live probe")


def bounded_file(path, limit):
    with Path(path).open("rb") as stream:
        data = stream.read(limit + 1)
    require(len(data) <= limit, "Evidence file exceeds its bound")
    return data


def inspect_files(trace_path, trx_path, commit):
    raw = bounded_file(trace_path, 64 * 1024)
    trx = bounded_file(trx_path, 4 * 1024 * 1024)
    result = inspect_trace(load_trace(raw), commit)
    inspect_trx(trx, result["route"])
    resolved, tree = source_revision(commit)
    require(resolved == commit, "Expected exact immutable revision")
    # The caller's declaration is matched to a real commit, not an attestation
    # that the binary was built from that commit. Do not call it a live receipt.
    result.update(declaredCsharpCommit=resolved, declaredSourceTree=tree,
                  traceSha256=hashlib.sha256(raw).hexdigest(),
                  trxSha256=hashlib.sha256(trx).hexdigest())
    return result


def fixture():
    # Synthetic data for validator tests only; never write a live evidence receipt.
    def digest(n):
        return f"{n:064x}"

    return {
        "schemaVersion": 1, "lane": "live", "route": "chatgpt-native-direct-websocket",
        "protocol": "v3", "requestedVoiceModel": "fixture-voice",
        "requestedReasoningModel": "fixture-reasoning", "requestedReasoningTransport": "sse",
        "declaredCsharpCommit": "a" * 40,
        "startedAt": "2026-01-01T00:00:00+00:00", "finishedAt": "2026-01-01T00:00:30+00:00",
        "outcome": "passed", "step": "clean shutdown", "errorType": None,
        "threadId": str(uuid.UUID(int=1)), "logicalSessionId": "b" * 32,
        "input": [{"sha256": digest(1), "bytes": 960}, {"sha256": digest(2), "bytes": 1920}],
        "toolCalls": 1, "reasoningRequests": 4, "completed": 4, "incomplete": 0,
        "notDispatched": 0, "reasoningUsage": [
            {"RequestDigest": digest(10+i), "ResponseDigest": digest(20+i), "Tokens": None}
            for i in range(4)
        ],
        "stages": [
            {"Name": name, "UserTranscriptEvents": 1 if i < 2 else 0,
             "AssistantTranscriptEvents": 1, "OutputPcmBytes": 960,
             "AssistantTurnDone": True, "PhraseObserved": True,
             "DelegationDigests": [digest(30+i)] if i < 2 else [],
             "ProviderSessionDigest": digest(50), "ObservedModel": None,
             "VoiceTokens": None, "BackingTurnId": str(uuid.UUID(int=i+2))}
            for i, name in enumerate(STAGES)
        ],
        "cleanShutdown": True, "media": "provider-pcm-received-not-played",
        "interruption": "not-run", "reconnect": "not-run",
        "coverage": "direct-connect-two-voice-turns-host-triggered-reasoning-speech-clean-end-only",
        "storage": "process-local-cas-fixture", "tokenOrCreditCeiling": "not-enforced-by-provider",
    }


def self_test():
    trace = fixture()
    result = inspect_trace(load_trace(json.dumps(trace).encode()), "a" * 40)
    assert result["diagnosticOnly"] and not result["eligibleForLiveAcceptance"]
    assert result["reasoningReceiptsWithReportedTotal"] == 0
    assert not result["authorizationVerified"] and not result["playbackVerified"]
    assert not result["executionProvenanceVerified"]

    def rejects(change, template=trace):
        bad = copy.deepcopy(template)
        change(bad)
        try:
            inspect_trace(bad, "a" * 40)
        except (ValueError, TypeError):
            return
        raise AssertionError("Invalid live trace accepted")

    for mutation in (
        lambda t: t.update(outcome="failed"), lambda t: t.update(lane="offline"),
        lambda t: t.update(declaredCsharpCommit="b" * 40),
        lambda t: t.update(toolCalls=2), lambda t: t.update(reasoningRequests=6),
        lambda t: t.update(notDispatched=1), lambda t: t.update(cleanShutdown=False),
        lambda t: t.update(tokenOrCreditCeiling="verified"),
        lambda t: t.update(media="played"), lambda t: t.update(reconnect="passed"),
        lambda t: t["input"][1].update(sha256=t["input"][0]["sha256"]),
        lambda t: t["input"][0].update(bytes=960001),
        lambda t: t["stages"].pop(),
        lambda t: t["stages"][0].update(PhraseObserved=False),
        lambda t: t["stages"][0].update(OutputPcmBytes=0),
        lambda t: t["stages"][1].update(BackingTurnId=t["stages"][0]["BackingTurnId"]),
        lambda t: t["stages"][1].update(DelegationDigests=t["stages"][0]["DelegationDigests"]),
        lambda t: t["stages"][2].update(UserTranscriptEvents=1),
        lambda t: t["stages"][2].update(ProviderSessionDigest="e" * 64),
        lambda t: t["reasoningUsage"][1].update(RequestDigest=t["reasoningUsage"][0]["RequestDigest"]),
        lambda t: t["reasoningUsage"][0].update(Tokens={"TotalTokens": 0}),
        lambda t: t.update(rawTranscript="unexpected"),
    ):
        rejects(mutation)

    web = copy.deepcopy(trace)
    web.update(route=WEBRTC_ROUTE, media="provider-webrtc-rtp-decoded-pcm-not-played",
               reconnect="same-call-detach-reattach",
               coverage="webrtc-connect-two-voice-turns-same-call-reattach-host-speech-clean-end",
               providerCallDigest="c" * 64, reattachedCallDigest="c" * 64, mediaClosed=True,
               attachmentBeforeId=str(uuid.UUID(int=10)), attachmentAfterId=str(uuid.UUID(int=11)))
    for i, stage in enumerate(web["stages"]):
        stage["OutputPcmBytes"] = 0
        stage["WebRtcMedia"] = {
            "PacketsSent": (web["input"][i]["bytes"] + 959) // 960 + 50 if i < 2 else 0,
            "PacketsReceived": 4, "BytesReceived": 320, "DecodedSamples": 1920,
            "NonSilentSamples": 1600, "ConcealedSamples": 0, "Codec": "audio/opus",
        }
    web_result = inspect_trace(load_trace(json.dumps(web).encode()), "a" * 40)
    assert web_result["sameCallReattachmentReported"] and not web_result["reconnectVerified"]
    assert web_result["receivedPcmBytes"] == 0 and web_result["decodedWebRtcPcmSamples"] == 5760
    assert not web_result["eligibleForLiveAcceptance"]
    for mutation in (
        lambda t: t.update(reattachedCallDigest="d" * 64),
        lambda t: t.update(attachmentAfterId=t["attachmentBeforeId"]),
        lambda t: t.update(mediaClosed=False),
        lambda t: t.update(interruption="passed"),
        lambda t: t["stages"][0].pop("WebRtcMedia"),
        lambda t: t["stages"][0]["WebRtcMedia"].update(PacketsSent=0),
        lambda t: t["stages"][2]["WebRtcMedia"].update(PacketsSent=1),
        lambda t: t["stages"][0]["WebRtcMedia"].update(NonSilentSamples=0),
        lambda t: t["stages"][0]["WebRtcMedia"].update(ConcealedSamples=1),
        lambda t: t["stages"][0]["WebRtcMedia"].update(BytesReceived=6000),
        lambda t: t["stages"][0]["WebRtcMedia"].update(Codec="audio/other"),
        lambda t: t["stages"][0]["WebRtcMedia"].update(rawAudio="unexpected"),
    ):
        rejects(mutation, web)

    def trx(outcome="Passed", method=PROBE_METHOD):
        class_name, name = method.rsplit(".", 1)
        return (f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                f'<TestDefinitions><UnitTest id="fixture"><TestMethod className="{class_name}" '
                f'name="{name}"/></UnitTest></TestDefinitions><Results>'
                f'<UnitTestResult testId="fixture" testName="{method}" outcome="{outcome}"/>'
                f'</Results><ResultSummary><Counters total="1" passed="{int(outcome == "Passed")}"/>'
                '</ResultSummary></TestRun>').encode()

    inspect_trx(trx())
    inspect_trx(trx(method=WEBRTC_PROBE_METHOD), WEBRTC_ROUTE)
    for wrong_trx, route in ((trx(), WEBRTC_ROUTE), (trx(method=WEBRTC_PROBE_METHOD), DIRECT_ROUTE)):
        try:
            inspect_trx(wrong_trx, route)
        except ValueError:
            continue
        raise AssertionError("Wrong route TRX accepted")
    for bad in (trx("NotExecuted"), trx("Failed"), trx(method="tryAGI.Codex.Tests.RustParityFixtureTests.Fixture")):
        try:
            inspect_trx(bad)
        except ValueError:
            continue
        raise AssertionError("Invalid probe TRX accepted")
    for raw in (b'{"lane":"live","lane":"offline"}', b'{"usage":NaN}'):
        try:
            load_trace(raw)
        except ValueError:
            continue
        raise AssertionError("Ambiguous JSON accepted")
    print("Live probe diagnostic self-test: strict schema, bounds, correlation, coverage, and exact TRX verified; no acceptance receipt issued.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("self-test")
    inspect = commands.add_parser("inspect")
    inspect.add_argument("--trace", required=True)
    inspect.add_argument("--trx", required=True)
    inspect.add_argument("--commit", required=True)
    args = parser.parse_args()
    if args.command == "self-test":
        self_test()
    else:
        print(json.dumps(inspect_files(args.trace, args.trx, args.commit), indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
