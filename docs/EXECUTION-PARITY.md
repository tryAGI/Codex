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
| `app-server-protocol/src/protocol/v2/process.rs`; `app-server/tests/suite/v2/process_exec.rs` (spawn returns before exit, drain timeout, handle release, cap flags, kill) | `CodexProcessExecution` implements separate process/spawn, writeStdin, resizePty, kill, outputDelta and exited using an explicit contained host adapter and transport response barrier. | Managed lifecycle tests in `CodexProcessExecutionTests`; #101, real adapter pending #104 |
| `exec`, `exec-server` including process_sandbox_tests, sandbox_integrity, regular_file_tests, no_follow | SDK adapter boundary only; transport, real PTY, process containment and drain semantics still need separate acceptance. | #78, #81, #104 |
| `app-server-protocol/src/protocol/v2/fs.rs`; `app-server/tests/suite/v2/fs.rs` | Existing `CodexFileSystem`, `CodexFileSystemNative`, `CodexFileSystemWatches`, `CodexFileSystemTests`, `CodexFileSystemWatchTests`. | Existing code mapped, not independently certified here; #79 |
| `file-system`, `file-watcher/src/file_watcher_tests.rs`, exec-server no_follow and fs_sandbox_windows_tests | Opt-in `CodexMacOsRootedFileSystem` uses a pinned root descriptor and kernel O_NOFOLLOW_ANY for readFile/getMetadata/readDirectory, and rejects all mutations. Existing path-based host is unchanged and is not an enforcement fallback. General rooted mutation, recursive/watch and platform coverage remain open. | #103, #144 |
| `file-search/src/lib.rs`, `snapshot.rs`, `matcher_tests.rs`; `app-server/tests/suite/fuzzy_file_search.rs` | `CodexFileSearch` implements one-shot search and connection-scoped sessions, cancellation and top-50 notifications; `CodexStableDirectoryFileSearchSource` uses a host-protected rooted traversal. Exact Nucleo scores and ignore/snapshot behavior remain separate. | #102, #136, #137 |
| `apply-patch/src/lib.rs`, `apply-patch/src/file_update_tests.rs`, `apply-patch/tests/suite`; command_exec apply_patch line endings test | Existing `CodexPatch`, `CodexPatchUpdate`, `CodexWorkspacePatch`, `CodexWorkspacePatchTransport` and related tests; preconditions and plan digest already represented. | Preserve; differential coverage and authorization integration pending #79, #80 |
| `app-server-protocol/src/protocol/v2/permissions.rs`; `app-server/tests/suite/v2/permission_profile_list.rs`, `request_permissions.rs` | `CodexApprovalStore` models exact-action authorization, expiry, revocation, single-use and durable storage CAS. `CodexPermissionProfileCatalog` implements profile discovery, inheritance-chain resolution, pagination and host-checked selection against current project configuration. `CodexApprovalRpcBridge` binds v2 server-request responses to exact durable approval records. `CodexMacOsSqliteApprovalStorage` supplies real durable CAS and `CodexApprovalDispatcher` gates an actual contained command. `CodexApprovalCommandAdapter` and `CodexApprovalProcessAdapter` integrate the durable consume gate into native launch routing for explicit noninteractive host plans. Production policy/approval resolution, interactive control, patch/tool routing, cross-platform storage, scoped grants and legacy routing remain separate (#158). | #80, #111–#113; coordinate #64 |
| `execpolicy`, `sandboxing`, `network-proxy`; sandboxing platform tests, network-proxy authorization_path_tests and host_policy_tests | `CodexMacOsLeafCommandAdapter` provides actual deny-default Seatbelt for a restricted macOS single-process lane (no fork, no PTY, no direct network, approved stable workspace). General OS and network policy compilation and tree containment remain open. | #81, #104, #132; coordinate #86 and #98 |
| `app-server-protocol/src/protocol/v2/review.rs`; `app-server/tests/suite/v2/review.rs`; `core/src/guardian`; app-server guardian_v2 / guardian_v3_sync_tests | `CodexReviewRpc` validates inline review targets, verifies a committed correlated turn, routes advisory Guardian assessment and exact denial escalation through a trusted adapter and approval store. `CodexGuardianReviewJournal` commits ordered auto-review lifecycle records and binds exact recorded denials to one pending approval. Actual runner, host storage/reviewer integration and strict-review dispatch fence remain separate. | #82, #129, #130, #142 |

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

## Connection-owned process lifecycle (#101)

`CodexProcessExecution` uses an independent `ICodexProcessAdapter` capability rather than
routing rejected sandboxed commands to a host process. It reuses the exact-argv, environment,
PTY and resource-bound validation of command/exec. The authenticated transport supplies a
response callback that orders the spawn response before any process/outputDelta or process/exited
notification. Output emitted before the response is bounded and retained per stream, not delivered early.
After acknowledgment, the process is connection-owned; request cancellation no longer kills it.
The adapter must enforce actual containment and report drain status. Disconnect waits for disposal;
a cleanup error fails the host and does not emit a successful exit. Ambiguous output delivery is
not replayed. Explicit null caps/timeouts are recognized as unbounded requests and rejected by the
native safety limits, not silently treated as omitted fields. There is no unsandboxed fallback.
The host reports exit 124 on its execution deadline only after successful cleanup; an unknown
drain status is omitted rather than invented. Actual sandbox and PTY behavior remain #104.

Local validation on macOS arm64 / .NET 10: 44 tests passed, 0 failed, 0 skipped across
CodexProcessExecutionTests, CodexCommandExecutionTests and CodexApprovalStoreTests.
The admitted build receipt is native-20261127-f6f94c90b4734d368d46ccad20784809.
These tests use synthetic process adapters; they do not certify OS-level isolation.

## Permission profile discovery (#111)

`CodexPermissionProfileCatalog` serves the pinned `permissionProfile/list` contract and resolves
explicit profile IDs against the existing layered configuration source. Built-ins precede configured
profiles; a numeric cursor matches the Rust pagination fixture. Configuration is read only after
authenticated directory authorization. Each page and selection re-evaluates host requirements in the
current cwd. Missing parents, cycles and reserved built-in shadowing fail closed. The complete
parent-first declarations, including unknown network or filesystem constraints, reach the trusted
policy evaluator; only summaries leave the host. Selection is not an execution grant. Policy
compilation, symbolic roots, turn/session grants and platform enforcement remain explicit open work.

Local validation on macOS arm64 / .NET 10: 20 tests passed, 0 failed, 0 skipped across
CodexPermissionProfileCatalogTests and CodexConfigurationResolutionTests.
Admitted build receipt: native-20261127-aaab1c807928486c9f0f015eb58f6738.

## Approval RPC correlation (#112)

`CodexApprovalRpcBridge` connects the v2 command, file-change and permission request families
to `CodexApprovalStore`, reusing the exact numeric/string request identity type from the
tool request ledger. The account and connection partition the durable request ID; a CAS binds
the original method, parameter digest, offered one-action choices, resolved action and expiry.
Only a newly created record authorizes initial request publication. Reattachment, duplicate
responses, stale revisions, mismatched connections/actions, expiry and cancellation cannot
rebind the request or admit a second execution. A reviewer decision is not a dispatch receipt;
the trusted dispatcher must separately consume the approval for the exact resolved action.
Command/file one-action accept, decline and cancel are supported. An empty turn permission
grant is denial; nonempty permission grants, acceptForSession and policy amendments retain raw
response JSON and return Unsupported without approving anything. Legacy approval routing,
turn/session grants and actual durable storage/dispatcher integration remain #112, #111 and #113.

Local validation on macOS arm64 / .NET 10: 32 tests passed, 0 failed, 0 skipped across
CodexApprovalRpcBridgeTests, CodexApprovalStoreTests and CodexToolRequestLedgerTests.
Admitted build receipt: native-20261127-e404d0fab6394321b3620247067b182c. Storage remains a test double.

## macOS sandbox admission (#104)

A deny-default Seatbelt probe for /usr/bin/true with minimal library reads returned exit 134
without output, as did a second probe allowing sysctl reads and self process information.
Runs: run-01a21be07ca771c2b72c721aba3b4672 and run-01a21be0b3d07dca9af883053a304324.
The pinned Rust seatbelt_base_policy.sbpl and seatbelt_read_only_platform_defaults.sbpl showed
that runtime executable mapping and specific system operations are needed for startup.
A source-backed deny-default fixture probe then verified allowed workspace reads/writes and
denied outside reads/writes on actual macOS (codex-65-seatbelt-upstream-fixture-20261127-01).
The restricted leaf probe verified fork denial, loopback denial and launcher exec retaining the
same PID (codex-65-seatbelt-leaf-probe-20261127-01). A narrowed policy also denied symlink
reads and fork and direct network, without the upstream general-purpose runtime allowances
(codex-65-seatbelt-minimal-fixture-20261127-02).

`CodexMacOsLeafCommandAdapter` is a real, opt-in ICodexCommandAdapter for the explicit
macos-leaf-read-only and macos-leaf-workspace profiles, or a host-approved leaf default.
It launches only the fixed /usr/bin/sandbox-exec and exact absolute system executable, passing
workspace/executable through separate SBPL parameters. It clears inherited environment,
allows a limited system runtime (including named sysctl reads and necessary device/IPC
operations), denies fork and direct network, and permits workspace writes only with an explicit
host decision. The kernel rejects access through a symlink to an outside fixture. The host must
provide stable canonical root ownership, exclude external hardlink/inode aliases and prevent
untrusted concurrent root changes. This does not certify arbitrary path replacement races,
general process trees, PTY, process/spawn admission or unsupported legacy/Rust profiles.
Unsupported requests and non-macOS hosts fail closed. No direct target-launch fallback exists.

Actual macOS arm64 / .NET 10 validation: 7 leaf tests passed, 0 failed, 1 platform-specific
non-macOS denial test skipped; combined command/process/leaf suite 41 passed, 0 failed,
1 skipped. Admitted receipt native-20261127-55667b4449484bdc902d3f174f4b1b85.
Verified actual read/write and read-only boundaries, symlink read/write denial, fork denial,
direct loopback denial, binary pipe streaming, termination and disconnect reaping of an owned PID.
The first shared build failed due to an unrelated provider test analyzer error; no analyzer gate
was changed and the repeat passed. General descendant containment and PTY remain #132;
root replacement and external alias handling remain #103. #104 and #81 are not fully accepted.

## Review and Guardian RPC (#82)

`CodexReviewRpc` serves review/start, thread/guardianReview and thread/approveGuardianDeniedAction.
It validates the four Rust review targets and inline-only delivery before authorization and I/O.
The explicit adapter receives a retained operation ID and the current native thread revision.
The review/start response is rejected unless the corresponding turn was actually recorded in
`CodexThreadStore`; an already committed operation cannot start a second review. Guardian
assessment must have the correlated call ID and recognized outcome, risk and authorization
classifications; a null assessment or an allow result is never execution authorization.
Denial escalation requires an exact recorded event digest and a separate, existing pending
approval for the same thread and connection, resolved by the trusted adapter. The authenticated
reviewer is checked by both the review host and approval store. Terminal denied approvals
cannot be revived; successful escalation does not dispatch or amend persistent policy.

Local validation on macOS arm64 / .NET 10: 40 tests passed, 0 failed, 0 skipped across
CodexReviewRpcTests, CodexApprovalRpcBridgeTests and CodexApprovalStoreTests.
Admitted build receipt: native-20261127-4d3ce0e7b85d444596b6b6a351b5b41b.
The tests use the real native thread/approval stores against CAS test doubles and a synthetic
review adapter. Actual review runner, durable review history and notification mapping are
tracked in #129 and #130; no live Guardian or confidential reviewer implementation is claimed.

## Bounded file search (#102)

`CodexFileSearch` implements fuzzyFileSearch, sessionStart, sessionUpdate, sessionStop,
sessionUpdated and sessionCompleted. Result fields preserve the pinned snake_case
file_name and match_type wire contract alongside camelCase session parameters.
The explicit source checks authorization before admitting a session and again on each
search. Per-connection job identities, cancellation-token replacement, generation draining,
bounded top-50 matches and scan/path-memory/time budgets limit retained work. An empty
query sends an empty snapshot; source or delivery failure does not send completion.
No implicit filesystem access is installed. The local stable-directory source requires a
trusted root lease and host ignore predicate; it skips symlinks/reparse points and .git metadata.
It never reads file contents for matching and cannot certify arbitrary mutable root traversal.

The native matcher is a deterministic approximation with basename priority, grapheme
indices and stable UTF-8 scalar order; it does not claim exact Nucleo scores or full fuzzy atom
semantics. It rechecks current root authorization and re-enumerates edited queries rather
than retaining unverified snapshots. Exact ranking and differential receipts remain #136;
full ignore processing, safe link/alias traversal and incremental snapshots remain #137/#103.

Local validation on macOS arm64 / .NET 10: 10 focused file search tests passed, 0 failed,
0 skipped; 36 combined search/filesystem/watch tests passed, 0 failed, 0 skipped.
Admitted receipt native-20261127-92f99fa6ab094cec9c40ac34ceaf7831.
Real fixture traversal, explicit root lease and ignore filtering, relative path validation,
top-50 ranking, Unicode indices, session isolation, revocation, query replacement,
one-shot cancellation and notification draining were checked. No real mutable-root or
exact Rust matcher acceptance is claimed.

## Durable Guardian review lifecycle (#130)

`CodexGuardianReviewJournal` implements item/autoApprovalReview/started and
item/autoApprovalReview/completed with account/connection/thread/turn/review/action
correlation. It retains raw extension fields and all seven pinned action variants, validates
terminal status and timestamps, commits with host-owned durable CAS before notification, and
records notification acceptance separately. A duplicate or ambiguous commit is never a reason
to replay delivery. If the start delivery is uncertain, a terminal decision is durably retained
without overtaking the start. Explicit host reconciliation is still required.

Trusted denial evidence is matched to the original core review identity, timestamps, status,
action type and shared identity-bearing fields. The exact event digest is retained; PathUri
and permission materialization are independently resolved by the trusted adapter. One denial
can bind to only one existing matching pending approval. A reviewer must still authenticate
and decide through `CodexApprovalStore`, and dispatch must consume the approval separately.
An approved, denied, timed-out or aborted review never creates an execution grant.

Local validation on macOS arm64 / .NET 10: 15 focused Guardian journal tests passed,
0 failed, 0 skipped; 55 combined Guardian/review/approval tests passed, 0 failed, 0 skipped.
Admitted receipt native-20261127-13ed91c03d1c4f2389cfb937dd609923.
Storage round-trips through source-generated metadata in the CAS test adapter; this does not
certify real host persistence, actual reviewer execution or live delivery. Strict-review
dispatch fencing, warning notifications and uncertain-publication reconciliation remain #142;
review runner and real history integration remain #129/#130/#113.

## Descriptor-relative rooted reads (#103)

`CodexMacOsRootedFileSystem` is an opt-in macOS arm64 (12+) read-only RPC capability.
The host chooses and authorizes the absolute root, pins its directory descriptor, and
authorizes each disclosure. The root name is checked against the pinned device/inode
before admission; caller paths are resolved relative to the anchor in one openat with
Darwin O_NOFOLLOW_ANY, without a separate path-based reopen. Intermediate and final
symlinks are refused. Opened file/metadata are obtained from the descriptor with fstat;
listing uses fdopendir/readdir and single-component fstatat with AT_SYMLINK_NOFOLLOW.
Cross-device objects, special nodes and regular files with multiple hard links are refused
or omitted from listings. O_NONBLOCK prevents waiting on FIFO opens. Read bytes and
listing entries are bounded. All known fs/* methods are claimed; unsupported mutations,
recursive operations and watches fail closed instead of falling back to the path handler.

The host must still protect the chosen root identity during acquisition, exclude untrusted
devices/mount/alias topology and authorize the pinned object semantics. This does not
certify continuous namespace containment after arbitrary renames or concurrent creation
of hard links. The ordinary CodexFileSystem implementation remains unchanged; hosts
must explicitly select the rooted capability for that authorization scope. Darwin
arm64 is the only validated ABI. Rooted mutations, atomic replacement, recursive
copy/remove and watch registration remain #144; cross-platform and full parity remain #103.

Actual macOS arm64 / .NET 10 validation: 9 focused rooted filesystem tests passed,
0 failed, 1 unsupported-platform test skipped. Combined filesystem/watch/search/rooted
suite: 45 passed, 0 failed, 1 skipped. Admitted build receipt
native-20261127-2b65db79d42840a2bb9116c19ce68bb4. The original durable run
codex-65-rooted-fs-tests-20261127-01 survived worker reconnect and passed after 212s
in CPU admission. Tests cover exact binary reads, descriptor metadata/listing, an
intermediate symlink substituted during authorization, replaced root inode, leaf/internal
symlink rejection, existing external hard links, nonblocking FIFO rejection, authorization,
bounds, foreign connections and disposal. The separate actual descriptor ABI probe
codex-65-rooted-dirent-probe-20261127-01 confirmed Darwin directory/regular/symlink kinds.
No general mutable-root or mutation enforcement claim is made.

## Durable local approval CAS and dispatch (#113)

`CodexMacOsSqliteApprovalStorage` implements the existing ICodexApprovalStorage
contract using the macOS system SQLite library, source-generated JSON and one
atomic insert-if-absent or revision-matched update. Account and request keys
are bound binary parameters; record identity and stored revision are checked on
read. It requires explicit exclusive creation or reopening a valid versioned
database, a private existing canonical directory, and a private regular file.
SQLite rollback journal mode, synchronous EXTRA and fullfsync ON are checked
on each connection. The host must own and stabilize the storage directory,
prevent database replacement/rollback or untrusted writers and supply required
encryption at rest; this adapter is not encrypted by itself. It is opt-in and
macOS-only; other platforms and the consumer's storage remain separate.

`CodexApprovalDispatcher` checks the trusted current host policy and consumes
one exact action through the existing approval store before invoking a trusted
callback bound to the immutable resolved action and effective policy. A failed
or uncertain CAS never dispatches; an error after consume does not recreate the
grant. Expiry and cancellation are checked again before the callback; the actual
adapter must independently enforce the current policy and expiry at admission.
The dispatcher does not recover unknown effects or cancel already-admitted work.

Actual macOS arm64 / .NET 10 validation: 9 focused SQLite approval tests passed,
0 failed/skipped; 47 combined approval/store/bridge/Guardian tests passed, 0
failed/skipped. Admitted receipt native-20261127-3c714616e3fb4b1f85b54b1e2f0ad414.
Verified reopen/account isolation, competing independent SQLite connections,
consume/revoke, a deliberately lost real commit receipt, a failed effect,
RPC binding round-trip, missing/foreign database rejection, and rollback of an
uncommitted update when a separate SQLite fixture process is killed. The real
Seatbelt leaf command ran only after durable approval consumption and did not
run again after reopen. The first build was blocked by a missing history-index
type in concurrent work; the next admitted run passed. No gates were weakened.
Actual production command/exec, process/*, workspace patch and MCP/tool wiring
and non-macOS durable storage are tracked in #158; no Advantage deployment or
power-loss certification is claimed.

## Exact native launch approval adapters (#158)

`CodexApprovalCommandAdapter` and `CodexApprovalProcessAdapter` implement the existing
command/exec and process/spawn adapter contracts. They snapshot the exact request before
trusted preparation, then use the same versioned binary action identity for review and
durable admission. The fingerprint includes the RPC family, host execution ID, connection,
process handle, exact argv, cwd, ordinal environment values (including null versus empty),
terminal/stream flags, requested policy, output cap, timeout, and the trusted effective
enforcement-plan digest and thread/turn/item/environment scope. Neither client process
IDs nor display commands are authority. The host must retain one approval ID per attempt
and independently bind the selected contained launch callback to the exact resolved policy,
including defaults and adapter identity. The callback receives the frozen request and
fresh consumed record and must enforce that policy and deadline at actual admission.

The dispatcher checks current host policy before and again after durable consumption.
Post-consume policy denial, expiry, cancellation, ambiguous CAS or launch failure never
reconstructs a reusable grant. Command and process families cannot share approval. The
wrappers explicitly reject interactive stdin and PTY (subsequent input requires its own
authority), and do not select an unsandboxed fallback. No new process-tree containment
is claimed; the production process adapter and interactive admission remain #132/#158.
Patch and native MCP/function/custom tool routing, consumer wiring, and durable host
mapping of the approval identity to an attempt remain open.

Validation: the native library compiled in admitted run
native-20261127-e907f0be426d4253ba1154e1520059a5. The first shared build was blocked by a
missing parallel Realtime reducer type (native-20261127-8299aa890ef2477bb39f8f89986f9587);
the second by unrelated ThreadAgentMailboxTests.cs xUnit2031, reported in #110.
Nine focused test cases cover fingerprint completeness, immutable snapshots, cross-family
and attempt denial, post-CAS policy revocation/expiry, uncertain consume, interactive denial,
actual native command RPC through the approval RPC bridge/SQLite/Seatbelt on macOS, and
the process RPC response/exit lifecycle using a synthetic contained session. These tests
are not yet locally certified at publication; verify exact-commit CI and the actual macOS
lane separately. No analysis or test gate was disabled.
