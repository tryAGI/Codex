# Independent GPT Live / backing reasoning parity acceptance

Tracking: [meta #25](https://github.com/tryAGI/Codex/issues/25), [inventory #54](https://github.com/tryAGI/Codex/issues/54), [fixtures #49](https://github.com/tryAGI/Codex/issues/49), [upstream drift #24](https://github.com/tryAGI/Codex/issues/24).

Reference: Rust `rust-v0.172.0` / `3d86e43b075adeba2193ec5c22ac92f55afd82e6`, confirmed newest stable tag on 2026-11-27. This is the **acceptance target**, not a claim that the existing packaged protocol baseline has already been reviewed.

## Reproduce

```sh
python3 scripts/realtime-parity.py check
python3 scripts/realtime-parity.py self-test
python3 scripts/realtime-parity.py report
python3 scripts/realtime-parity.py gate
dotnet test src/tests/tryAGI.Codex.Tests/tryAGI.Codex.Tests.csproj --filter FullyQualifiedName~RustParityFixtureTests
```

Use the machine-wide build admission wrapper on a shared Advantage host. The checker and report require no credentials or network. Only `refresh` fetches the pinned public Rust tree and source files. The generator records SHA-256 for each source and stable row IDs with source-line links. Use `review` after editing `realtime-parity-reviews.json`. Do not update the packaged baseline until #24 is accepted.

## What is counted

- Wire: distinct source-scoped literal messages, types and fields, then individually reviewed schema/configuration scenarios.
- Behavior: Rust test and function candidates and explicitly mapped state-transition scenarios.
- Live: direct WebSocket, ChatGPT WebRTC sideband and API WebRTC sideband; connect, delegation/real tool loop, follow-up, interruption, reconnect, reasoning-initiated speech and shutdown are separate required scenarios. An unavailable route remains unverified until an upstream-supported N/A rationale is reviewed.
- Candidate extraction is deliberately over-inclusive. It is lexical, not a Rust AST or proof of exhaustive semantics. Review every applicable row, add omitted dynamic/generated messages, configuration values, error cases and state transitions, and justify unrelated tests/functions as N/A. Multiple source references to one behavior are not independent passed E2E scenarios. The current denominator is provisional until `inventoryReviewed` is true and no applicability is pending.
- Candidate C# paths are navigation hints only. Add exact implementation and test references before accepting a row. Source review, a mock result, an unverified test, a skipped test and an actual live result are distinct. A pass requires a reviewed applicable row, exact C# revision and a recorded matching test receipt. A live row must use live evidence and explicitly distinguish received audio from actual playback.
- The release gate fails while any applicable row is not passed, applicability is pending, an evidence record is invalid, the inventory is not independently reviewed, or a lane has no applicable rows.

The fixture bundle `src/tests/tryAGI.Codex.Tests/Fixtures/RustRealtimeParity.v1.json` uses only synthetic data from pinned Rust test assertions. The fake socket replays JSON messages through the C# connection and compares event ordering, raw payloads and explicitly projected semantics. Fake HTTP compares selected Rust request assertions and replays SSE output. The V1 reference raw trace remains unchanged; a separate valid PCM16 fixture covers typed audio, input transcripts and the legacy handoff. V2 handoff/noop fixtures and exact outbound request sequences for V1/V2/V3 include standalone context and transport-close semantics. The prior V2 typed input delta divergence (#31) must be re-tested on a clean revision. The fake socket does not verify DNS, TLS, actual WebSocket handshake, WebRTC negotiation, provider behavior or playback. No model wording, timestamps or private production data are golden fixtures.

## Revision-bound offline evidence

```sh
python3 -B scripts/realtime_parity_evidence.py self-test
python3 -B scripts/realtime_parity_evidence.py run --host-admission --output /tmp/realtime-parity-offline-receipt.json
```

Omit `--host-admission` only on a standalone CI runner not sharing the owner's build queue. The runner selects only explicitly reviewed credential-free test classes. It refuses dirty build inputs before and after testing, parses the completed TRX, and records exact xUnit case display names/outcomes, the TRX digest, the Git commit and the source-tree fingerprint. A compile failure creates no receipt. A failed test run can create a diagnostic receipt, but cannot certify rows.

Review the generated receipt before checking it in under `docs/realtime-parity-evidence/`. In the row review, use `status: passed` only with exact implementation and test references and evidence containing `lane: offline`, `outcome: passed`, `csharpCommit`, `receipt` (repo-relative path), `receiptSha256` and `cases` (the exact passed xUnit display names). Method-wide mappings require all its discovered theory cases; use a complete xUnit display name when mapping just one case. No skipped or failed case counts. The checker verifies the receipt digest and its source fingerprint against the claimed Git revision. The report marks accepted evidence stale if build inputs change; dirty source also prevents release. CI must fetch history for this validation. Live rows remain closed to offline receipts and fail closed until the separate native live evidence verifier is implemented.

## Release and live safety

The parity report workflow checks matrix consistency, executes the credential-free conformance suite and records JSON report/receipt artifacts. The normal build/publish workflow also computes release readiness and requires both that result and the owner's NuGet publication switch for every package publication step. This does not block ordinary compilation/test runs while acceptance is incomplete. The repository's `NUGET_RELEASES` variable was temporarily set to false on 2026-11-27, because the normal publish workflow otherwise releases automatically on main pushes. Restore it only after the full acceptance/release decision; an ordinary green build is not final parity acceptance.

Do not perform live tests automatically in this workflow. Run the opt-in native direct and WebRTC tests only after offline acceptance, using the specifically authorized provider/credential domain, bounded call count and usage budget. Store sanitized correlated test evidence, not credentials, raw customer media or arbitrary model text. A received PCM frame is not proof of playback. Preserve #25 open until all applicable scenarios, CI, provenance and the actual NuGet package are verified.
