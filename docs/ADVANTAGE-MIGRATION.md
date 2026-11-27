# Consumer migration acceptance plan (#92)

Status: proposal, not authorization to modify a consumer or release a package. This document records portable migration requirements only; private deployment and storage details remain in the consumer's own audit. Do not start consumer implementation until #53/#91 have certified one immutable NuGet package and the consumer owner has accepted this plan.

## Scope inventory before changing the consumer

Separate the duplicated managed protocol library from its host integrations. Inventory every project reference, package, namespace import, direct type use, DI registration, RPC dispatcher, source-generated JSON context, fixture and integration test. Check actual API signatures and package dependencies; matching filenames do not prove substitutability. Preserve optional installed-Rust/worker routes and any user-selected transport.

| Domain | Candidate SDK replacement | Required host boundary |
| --- | --- | --- |
| Responses and thread turns | Native agent, thread store, RPC and protocol contracts | Persistent principal/tenant/account binding, task identity, authorization and cancellation |
| Realtime and reasoning handoff | Logical-session journal, transcript accumulator, admission, feedback and reconnect | Media transport, device routing, playback, application activity and event fan-out |
| Storage and history | Versioned thread/journal serialization, CAS and compaction contracts | Durable database transaction, encryption, migration and scoped reads |
| Authentication and account | Credentials, OAuth refresh and account/model/usage contracts | Existing protected secret store, concurrent rotation lock, explicit account and billing identity |
| Tools and agents | Typed registry, MCP, dynamic tools, skill and process contracts | Strict binder, actual consent and worker/device execution, tool receipts, budget and sandbox adapters |
| Usage and diagnostics | Observed provider usage and typed errors | Reservation/settlement ledger, idempotence, cost policy, privacy and UI projections |
| Application automation and external App Server | Native contracts or explicitly chosen optional Rust interoperability | Durable workflow/worker identity, correlation, host-specific tools, lifecycle and existing client integrations |

Identify code that is purely duplicated separately from real host effects. Never replace an existing permission-enforcing host adapter with a generic or unsandboxed executor. Existing user-selected provider and worker routing must remain explicit.

## Migration order

1. Freeze the agreed Rust pin, exact SDK source revision, NuGet version, package digest and compatibility notes; verify the package obtained from the release channel corresponds to that evidence. Check target framework, transitive dependencies, strong-name/namespace changes and NativeAOT. Reject an unverified or merely build-green package.
2. Create a complete read-only caller and storage inventory with exact source references and tests. Compile the consumer against the verified package in an isolated admitted validation environment; adapt only the necessary public interfaces. Keep protocol-independent host policy and media code. Move tests of duplicated pure protocol behavior to SDK evidence while retaining host tests. Do not grant the consumer arbitrary access to SDK internals.
3. Establish a deterministic migration/replay fixture suite on sanitized synthetic or explicitly approved data. Record old/new results without issuing provider requests, executing tools, or producing media twice. Compare raw JSON, typed projections, thread/turn/response/call correlation, ordering, unknown fields, encrypted continuation, transcript finality, retained history and usage.
4. Preserve existing durable identities and history. Make schema upgrades versioned, resumable and CAS-guarded; retain the old readable representation or rollback reader until equivalent behavior is proven. Migration of stored receipts must never rerun their effects. Ambiguous tool, provider and database commits remain unknown, not new admissions. Do not overwrite opaque encrypted continuation or unknown payload fields. Drain/reattach active sessions explicitly rather than moving in-flight authority to another executor.
5. Use one coordinated consumer upgrade. Validate all critical adapters, key-free integration/restart and concurrency tests, platform and opt-in live evidence under the existing consent and budget policy. Verify the serving revision and health; compare ledger and activity observations to the prior baseline. Provide a rollback plan that preserves new writes and action receipts without re-executing them. Do not deploy shadow dual-inference or an automatic paid fallback.
6. Remove obsolete implementation and references only after every caller has migrated and equivalence, durable-data recovery and rollback are demonstrated. Keep optional Rust-process interoperability as an explicit route, not a hidden native fallback.

## Blocking acceptance checks

- Verified package and combined release gate: #53 and #91, including upstream drift and exact-revision SDK, host, Rust interop and live evidence.
- One consumer dependency graph and API/serialization contract audit, no accidental duplicated runtime.
- Native storage migration with account/tenant isolation, compare-and-swap, recovery, unknown fields and durable call/usage receipts.
- Consent/credential/account/usage boundaries: rejection, revocation, refresh races, unknown commit, cross-account denial, no duplicate charges or side effects.
- Realtime media ownership, transcript recovery, handoff/feedback ordering, explicit client playback and device routing, no transport fallback.
- Automation, thread history, tool/process/file/sandbox and optional App Server integrations with credential-free and applicable platform fixtures.
- Existing activity/event clients observe the same durable facts; notification fan-out and raw extension data are preserved.
- Safe rollback and verified deployment evidence. No package release or backend deployment is authorized by this plan itself.

Keep #92 open until the exact private caller audit, migration design and release decision are independently reviewed.
