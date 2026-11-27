# Native execution and approval parity (#65)

Baseline: [Rust v0.172.0, 3d86e43b075adeba2193ec5c22ac92f55afd82e6](https://github.com/openai/codex/tree/3d86e43b075adeba2193ec5c22ac92f55afd82e6/codex-rs).
Parent: [#62](https://github.com/tryAGI/Codex/issues/62).
This is the workstream inventory, not a release receipt or an exhaustive audit. A managed adapter
test does not certify real sandbox enforcement, operating-system process containment, or PTY behavior.
Unknown and unsupported rows stay open.

## Initial source-backed capability map

All paths in the Rust column are relative to `codex-rs` at the pinned revision.

| Rust source / test | Native implementation or boundary | Status / owner |
| --- | --- | --- |
| `app-server-protocol/src/protocol/v2/command_exec.rs`; `app-server/src/command_exec.rs`; `app-server/tests/suite/v2/command_exec.rs` | `CodexCommandExecution` dispatches command/exec, /write, /resize, /terminate and outputDelta, using exact argv and a mandatory host adapter. | Managed lifecycle tests in `CodexCommandExecutionTests`; #78 |
| command_exec tests: buffered compatibility, caps, streamed output not duplicated, PTY implies streaming, initial size and resize, pipe writes | Host validates parameters, caps each output stream, reports base64 deltas before the response, delegates stdin/PTY to the owned session. | Synthetic tests, real adapter pending #104 |
| command_exec tests: connection-scoped process IDs, disconnect terminates process; command without streams can be terminated | Per-connection host; duplicate/foreign IDs rejected; capacity held until adapter cleanup; disconnect waits for cleanup. | Synthetic tests, actual OS descendants pending #104 |
| command_exec tests: profile selection, managed deny-read, network proxy isolation, project roots use command cwd, local environment disabled | Explicit policy/profile forwarded to trusted adapter, no default executor or unsandboxed fallback. | Enforcement not implemented/certified by SDK; #81, #104 |
| `app-server-protocol/src/protocol/v2/process.rs`; `app-server/tests/suite/v2/process_exec.rs` (spawn returns before exit, drain timeout, handle release, cap flags, kill) | Separate host process lifecycle is not implemented. Rust host-level process/spawn is not a sandbox fallback. | #101 |
| `exec`, `exec-server` including process_sandbox_tests, sandbox_integrity, regular_file_tests, no_follow | SDK adapter boundary only; transport, real PTY, process containment and drain semantics still need separate acceptance. | #78, #81, #104 |
| `app-server-protocol/src/protocol/v2/fs.rs`; `app-server/tests/suite/v2/fs.rs` | Existing `CodexFileSystem`, `CodexFileSystemNative`, `CodexFileSystemWatches`, `CodexFileSystemTests`, `CodexFileSystemWatchTests`. | Existing code mapped, not independently certified here; #79 |
| `file-system`, `file-watcher/src/file_watcher_tests.rs`, exec-server no_follow and fs_sandbox_windows_tests | Preserve fs/watch behavior; authorization before path-based I/O is not proof of race-free rooted enforcement. | #103 |
| Rust file-search and App Server search contracts | No native file-search session handler identified. | #102 |
| `apply-patch/src/lib.rs`, `apply-patch/src/file_update_tests.rs`, `apply-patch/tests/suite`; command_exec apply_patch line endings test | Existing `CodexPatch`, `CodexPatchUpdate`, `CodexWorkspacePatch`, `CodexWorkspacePatchTransport` and related tests; preconditions and plan digest already represented. | Preserve; differential coverage and authorization integration pending #79, #80 |
| `app-server-protocol/src/protocol/v2/permissions.rs`; `app-server/tests/suite/v2/permission_profile_list.rs`, `request_permissions.rs` | `CodexApprovalStore` models exact-action authorization, expiry, revocation, single-use and durable storage CAS. Effective permission profile resolution and server-request bridge remain separate. | #80, #111–#113; coordinate #64 |
| `execpolicy`, `sandboxing`, `network-proxy`; sandboxing platform tests, network-proxy authorization_path_tests and host_policy_tests | Need actual enforceable OS and network policies, explicit unsupported/denied status; do not infer enforcement from policy DTOs. | #81, #104, coordinate #86 and #98 |
| `app-server-protocol/src/protocol/v2/review.rs`; `app-server/tests/suite/v2/review.rs`; `core/src/guardian`; app-server guardian_v2 / guardian_v3_sync_tests | Review targets and inline turn delivery, correlated guardian decisions, budgets and history; no native contract identified yet. | #82 |

## Adapter rules

An embedding host creates one `CodexCommandExecution` per authenticated connection and
awaits disposal on disconnect. Its adapter must authorize and resolve the effective sandbox,
cwd and environment for the exact argv and enforce them before starting a contained process.
It must settle partial-launch cleanup, await output callbacks, and terminate and reap all owned
descendants on disposal, including when the direct parent has exited. It must reject unavailable
constraints. The SDK never registers a local Process.Start fallback or automatically approves a tool.
The public interface has no dependency on Advantage.

The initial native command host deliberately rejects unlimited output/timeout, imposes finite
host resource bounds, rejects unrecognized execution fields rather than silently dropping constraints,
and leaves `process/spawn` distinct. These are documented differences from upstream, not passed
parity rows. The adapter must explicitly resolve the host default cwd/profile if omitted.

Initial local verification: macOS arm64 / .NET 10, `CodexCommandExecutionTests` passed 18,
failed 0, skipped 0. Tests exercise exact argv, independent caps, streamed binary output, stdin,
resize, terminate, duplicate IDs, owner isolation, invalid requests, denial without retry,
cancellation, capacity retention, disconnect and cleanup error handling against a synthetic adapter.
Real sandbox/PTY/process-tree enforcement and differential receipts remain unverified; do not close
#78 or #65 on this basis. The published first package (98f2433) passed all four repository workflows,
including NativeAOT and the build/test workflow; this is not operating-system sandbox certification.

## Approval authority boundary (#80)

`CodexApprovalStore` accepts only host-generated immutable action correlation (account, connection,
execution attempt, exact-action SHA-256 and optional thread/turn/item/environment identifiers).
A durable CAS-backed adapter is mandatory. An authenticated reviewer authorization callback is
mandatory for each decision. Approved actions are consumed once; deny, cancel, expiry, revoke and
ambiguous storage writes never grant dispatch admission. Consumed is not an execution receipt.
`CodexCommandRequest.ExecutionId` is generated by the host and is independent of reusable client IDs.

`CodexApprovalStoreTests` exercises decision authorization, duplicate request identity, scope isolation,
expiry, revocation, races, lost CAS acknowledgements and recreation of the store against the same
storage adapter. This storage is a test double, not durable host storage certification. The normal
local build was blocked by CS1591 in concurrent unrelated account/OAuth/provider changes; the isolated
remote-main build/test workflow is required to certify this package. No warning gates were changed.

Remaining: effective permission profiles and turn/session grants (#111), server approval/decision RPC
bridge (#112), durable persistence and dispatch integration (#113). Tool transport integration is
coordinated with #64; a model response or elicitation event cannot itself grant permission.
