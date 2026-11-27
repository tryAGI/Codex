# Independent global Rust parity (#62 / #67)

Acceptance baseline: rust-v0.172.0, commit 3d86e43b075adeba2193ec5c22ac92f55afd82e6. This is not the packaged protocol baseline.

## Inventory and evidence

Run `python3 -B scripts/global-parity.py refresh` once to generate a verified tree/manifest/schema inventory from the pinned public archive. It checks Git blob hashes against the non-truncated upstream tree. Run `check`, `self-test`, `report` or `gate` offline. The catalog parser is shared with the packaged protocol generator. The source list, lexical API and Rust test candidates are navigation evidence, not semantic coverage or a final denominator. Crate, feature, platform target, configuration and RPC rows are provisional until independently reviewed. State transitions and dynamic/generated APIs need explicit reviewed scenarios. Every applicable capability needs exact implementation, test and evidence references.

Four separate lanes: native SDK (no Rust process), host adapters (real effects/permissions/platforms), optional Rust App Server interoperability, and real provider acceptance. A raw method catalog, generic JSON-RPC, skipped test or a successful mock does not prove native behavior. Unknown is not N/A. No percentages until the respective denominator is reviewed; N/A must have an independently accepted rationale. No accepted rows are manufactured by this initial generator.

Realtime delegates to #25 and its existing matrix/evidence runner. The global inventory records source files and links realtime candidates without copying their statuses or evidence. Unresolved or ambiguous references require review by the #25 owner. Do not close #25 on behalf of Agent 5.

## Domain routing

#63: threads, history, agents, #93 memory, #94 rollout. #64: tools/extensions/MCP and #95 remote control. #65: execution, approvals, sandbox/review and #98 platform adapters. #66: config, accounts, providers, #96 collaboration modes, #97 diagnostics. #67: independent protocol and differential acceptance.

## Release gate (#53 + #91)

The global gate is deliberately red until independently reviewed scope, exact-revision offline evidence, applicable platform and host evidence, optional Rust interop, actual live provider acceptance and the Realtime gate all pass. Reuse `scripts/realtime_parity_evidence.py` for revision-bound offline receipts; extend evidence support to explicit credential-free non-realtime classes instead of running authenticated tests implicitly. Never publish on catalog parity or build success alone. No automatic publishing switch changes. Coordinate gate wiring with Agent 5 before editing the shared release workflow.

For #89, compare the pinned RPC catalog to the packaged snapshot. Record typed request/result/notification/server-request support separately from native dispatch. Preserve extension data and raw payloads, register new types in the source-generated JSON context, and require schema, round-trip and NativeAOT tests. Catalog drift is reported independently and is not native coverage.
