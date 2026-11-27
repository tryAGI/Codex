# Independent global Rust parity (#62 / #67)

Acceptance baseline: rust-v0.172.0, commit 3d86e43b075adeba2193ec5c22ac92f55afd82e6. This is not the packaged protocol baseline.

## Inventory and evidence

Run `python3 -B scripts/global-parity.py refresh` once to generate a verified tree/manifest/schema inventory from the pinned public archive. It checks Git blob hashes against the non-truncated upstream tree. Run `check`, `self-test`, `report` or `gate` offline. The catalog parser is shared with the packaged protocol generator. The source list, lexical API and Rust test candidates are navigation evidence, not semantic coverage or a final denominator. Crate, feature, platform target, configuration and RPC rows are provisional until independently reviewed. State transitions and dynamic/generated APIs need explicit reviewed scenarios. Every applicable capability needs exact implementation, test and evidence references.

Four separate lanes: native SDK (no Rust process), host adapters (real effects/permissions/platforms), optional Rust App Server interoperability, and real provider acceptance. A raw method catalog, generic JSON-RPC, skipped test or a successful mock does not prove native behavior. Unknown is not N/A. No percentages until the respective denominator is reviewed; N/A must have an independently accepted rationale. No accepted rows are manufactured by this initial generator.

Realtime delegates to #25 and its existing matrix/evidence runner. The global inventory records source files and links realtime candidates without copying their statuses or evidence. Unresolved or ambiguous references require review by the #25 owner. Do not close #25 on behalf of Agent 5.

## Independent review overlay

`docs/global-parity-reviews.json` records per-capability, per-lane decisions without rewriting the upstream inventory. Each decision binds a full published commit, exact source references, owner and rationale. Source review, missing behavior, divergence, justified N/A and receipt-backed passes are separate. Changed source references invalidate source review; changes to the source tree invalidate negative conclusions and accepted receipts. Typed-only App Server review never receives native credit. A lane without a verified platform, Rust-runtime or live-provider receipt verifier cannot pass.

The CI workflow `global-parity.yml` runs the offline checker and a separate six-class cross-domain test suite, reusing the shared Realtime evidence parser and revision binding. Run locally only with admitted compilation on shared hosts:

```sh
python3 -B scripts/global_parity_evidence.py --host-admission --output /tmp/global-parity-receipt.json
```

Store reviewed sanitized global receipts under `docs/global-parity-evidence/`. The first global receipt (`ci-b3f9229-45956495446.json`) contains 59 passed credential-free cases at the exact published revision; seven typed interop rows reference their exact test cases. `testEvidence` records a verified test-only observation without promoting a row to `passed`, and distinguishes historical from current-source evidence. Non-Realtime cases already present in the Realtime receipt may be referenced directly instead of copying receipts. No offline result proves actual Rust-process or live-provider behavior.

## Domain routing

#63: threads, history, agents, #93 memory, #94 rollout. #64: tools/extensions/MCP and #95 remote control. #65: execution, approvals, sandbox/review and #98 platform adapters. #66: config, accounts, providers, #96 collaboration modes, #97 diagnostics. #67: independent protocol and differential acceptance.

## Release gate (#53 + #91)

The global gate is deliberately red until independently reviewed scope, exact-revision offline evidence, applicable platform and host evidence, optional Rust interop, actual live provider acceptance and the Realtime gate all pass. Reuse `scripts/realtime_parity_evidence.py` for revision-bound offline receipts; extend evidence support to explicit credential-free non-realtime classes instead of running authenticated tests implicitly. Never publish on catalog parity or build success alone. The publish workflow consumes the combined global report, which incorporates the existing Realtime gate and requires both lanes to be ready. This only tightens the existing publication condition; ordinary CI remains enabled and the owner-controlled publication switch is unchanged. Coordination with Agent 5 remains required before any release.

For #89, compare the pinned RPC catalog to the packaged snapshot. Record typed request/result/notification/server-request support separately from native dispatch. Preserve extension data and raw payloads, register new types in the source-generated JSON context, and require schema, round-trip and NativeAOT tests. Catalog drift is reported independently and is not native coverage. #128 tracks the independent Rust thread-name persistence and multi-client notification differences. The consumer migration gate and adapter plan are in [ADVANTAGE-MIGRATION.md](ADVANTAGE-MIGRATION.md).
