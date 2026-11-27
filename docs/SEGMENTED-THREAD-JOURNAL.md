# Segmented native thread journal

Tracking: [#143](https://github.com/tryAGI/Codex/issues/143), [#41](https://github.com/tryAGI/Codex/issues/41), [#42](https://github.com/tryAGI/Codex/issues/42), [#63](https://github.com/tryAGI/Codex/issues/63), [#92](https://github.com/tryAGI/Codex/issues/92).

## First stage: atomic segmented storage

`CodexSegmentedThreadStorage` implements the existing `ICodexThreadStorage` interface without changing the logical journal schema, reducer, turn admission or lifecycle CAS semantics. A trusted host opts in explicitly:

```csharp
// Both stores must share the same account authorization and durable storage guarantees.
ICodexThreadStorage storage = new CodexSegmentedThreadStorage(rootStore, segmentStore, accountId);
var threads = new CodexThreadStore(storage, accountId);
```

The root store must accept ordinary inline documents and versioned manifest objects. It must compare the root's revision atomically, returning true only after durable commit. The immutable page store implements `ICodexThreadSegmentStorage`; keys are scoped to the account and thread, and a page's SHA-256 identifies its exact bytes. A successful put must confirm durable, retrievable identical bytes. The host must provide its existing authorization, encryption and retention guarantees for pages as well as roots.

1. Read and validate the current root's identity and revision. A known stale revision is rejected before staging pages.
2. Split the candidate document's exact UTF-8 JSON into content-defined pages. The deterministic rolling hash only selects boundaries; SHA-256 verifies content. Each page is at most 512 KiB; all non-final pages are at least 32 KiB. Identical pages can be physically deduplicated by the host. Repeated patterns do not guarantee ideal chunk reuse.
3. Durably stage each distinct candidate page. A successful put is not publication.
4. Publish the single version-1 manifest with the expected root revision. Competing writers cannot both publish the same next revision.
5. A reader captures one root, verifies every page's length and SHA-256, verifies the ordered reconstructed document hash, and checks its thread/account/revision identity before returning a document. Missing or corrupt pages fail closed; no partial document is returned.

The version-1 manifest uses `kind: "codexThreadSegments"`, `version`, `id`, `accountId`, `revision`, `length`, `documentSha256`, and an ordered `pages` array of `sha256` / `length` references. The manifest is bounded, source-generated and rejects unknown or duplicate fields. It has no model or provider authority.

## Recovery and migration boundary

- Existing inline snapshots are read without migration side effects. The first successful segmented write replaces the inline root through the same revision CAS.
- Fence all inline-only writers before enrolling a thread. An older client must not write to a manifest root; there is no implicit downgrade.
- If staging or root CAS throws, do not dispatch any external action based on an uncertain receipt. Re-read the same thread identity and reconcile its existing durable admission. Pages staged by a failed writer may remain unreferenced.
- Do not delete a page merely because the newest manifest no longer refers to it: an older reader or an in-flight candidate may still require it. Reference-aware garbage collection and subtree deletion need explicit host coordination and separate proof. This adapter does not advertise the deletion capability.
- Existing materialized-document, event-count and closure-reservation bounds remain. This stage reduces possible physical write amplification, but still reconstructs a full document on read and stages candidate pages on write. No throughput or latency improvement is claimed without a host measurement.

## Next stages

Use stable semantic pages for observations, sealed transcript frames, handoff receipts and turns. Publish page indexes and reducer checkpoints in the same revision manifest, keeping immutable source events authoritative. Validate incremental replay against full replay, including old journal upgrades, handoff deduplication, late finals, compaction, forks and deletion. Move bounded item/history queries to those indexes. Only then retire the full-document ceiling for a page-aware store, keeping bounded pages, context windows, manifests and explicit recovery. The Advantage consumer migration is outside this initial SDK change.
