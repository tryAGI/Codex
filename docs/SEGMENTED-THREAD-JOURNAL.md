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
3. Durably stage each distinct candidate page not already referenced by the currently captured committed manifest. Retained digest/length references prove durable page availability under the host retention contract. Do not reuse a failed writer's uncommitted staging receipts. A successful put is not publication.
4. Publish the single version-1 manifest with the expected root revision. Competing writers cannot both publish the same next revision.
5. A reader captures one root, verifies every page's length and SHA-256, verifies the ordered reconstructed document hash, and checks its thread/account/revision identity before returning a document. Missing or corrupt pages fail closed; no partial document is returned.

The version-1 manifest uses `kind: "codexThreadSegments"`, `version`, `id`, `accountId`, `revision`, `length`, `documentSha256`, and an ordered `pages` array of `sha256` / `length` references. The manifest is bounded, source-generated and rejects unknown or duplicate fields. It has no model or provider authority.

## Recovery and migration boundary

- Existing inline snapshots are read without migration side effects. The first successful segmented write replaces the inline root through the same revision CAS.
- Fence all inline-only writers before enrolling a thread. An older client must not write to a manifest root; there is no implicit downgrade.
- If staging or root CAS throws, do not dispatch any external action based on an uncertain receipt. Re-read the same thread identity and reconcile its existing durable admission. Pages staged by a failed writer may remain unreferenced.
- Do not delete a page merely because the newest manifest no longer refers to it: an older reader or an in-flight candidate may still require it. Reference-aware garbage collection and subtree deletion need explicit host coordination and separate proof. This adapter does not advertise the deletion capability.
- Existing materialized-document, event-count and closure-reservation bounds remain. Ordinary reads still reconstruct a full document and writes stage candidate pages. No throughput or latency improvement is claimed without a host measurement.

## Stage 2a: revision-bound semantic history reads

Upgrade all manifest consumers before enrolling in version 2. The original constructor still writes version 1 for new threads; once a v2 root is published, newer writers preserve v2 even without explicit enrollment.

```csharp
var storage = new CodexSegmentedThreadStorage(rootStore, segmentStore, accountId, indexHistory: true);
var page = await storage.ReadHistoryPageAsync(threadId, CodexThreadHistoryCollection.Turns,
    pageSize: 32, cancellationToken: ct);
while (page.Next is { } cursor)
{
    // Consume page.Items before continuing; the cursor cannot cross a revision.
    page = await storage.ReadHistoryPageAsync(threadId, CodexThreadHistoryCollection.Turns,
        pageSize: 32, cursor: cursor, cancellationToken: ct);
}
```

Version 2 can add `historyIndex` and its canonical SHA-256 digest in the same root CAS. Each entry names a collection, an optional realtime session ID and an ordered sequence of byte ranges in the original document. A range contains complete original JSON records, their first ordinal/count, offset/length and SHA-256. No source records are duplicated. The collections include turns, realtime observations/boundaries, frames, handoff captures, carryover, admission receipts, and inherited carryover. Realtime collections require an exact session ID.

- Index blocks target 256 KiB and at most 64 records; a larger individual record occupies its own block. Selective reads load only intersecting physical pages, verify each page and the indexed range, and return at most the requested item count (1–256) from one block. The decoded record-byte budget is 4 MiB; boundary physical pages can add up to two partial pages of I/O. An oversized individual record requires the existing full-document API.
- Continuations bind thread, document revision/hash, collection, session and next ordinal. An intervening CAS rejects the cursor rather than silently mixing revisions. One request uses one captured root. Cursors are not authority tokens.
- The root and its index are trusted host-published metadata; selective reads do not attest unread pages or the whole document's semantic validity. Full reads verify the document digest and rebuild the index to verify exact correspondence. Corrupt selected pages/ranges fail closed.
- V1 and inline roots remain readable through the full-document path. If the index exceeds its bounded directory budget (8,192 entries/ranges or a 1 MiB v2 manifest), omit it rather than preventing closure of an otherwise valid journal. The page result exposes `UsedIndex = false` for those fallback reads; missing collections are empty.
- No inference, handoff dispatch or persistence mutation occurs during history reads.

## Stage 2b: append-only observation prefix checkpoints

`ReadRealtimeObservationDeltaAsync` returns a bounded batch and a versioned `CodexRealtimeObservationPrefix`. Unlike a normal pagination cursor, this checkpoint can cross committed revisions if the exact previously consumed observation prefix remains unchanged. The SHA-256 proof covers the account, thread, session, ordered block digests and item counts. A partial final block is verified against its original bytes, even if a later append grew that block. Fully consumed unchanged blocks are compared through the trusted manifest index without re-reading them.

The host must persist the prefix together with its applied reducer state atomically, and only after processing the returned items. A changed/truncated prefix, rollback, wrong scope, same-revision hash mismatch or unavailable index requires explicit verified full replay. The proof is not itself a serialized reducer state, an authority token, or proof of unread page integrity. This API provides the read-side foundation for persistent reducer checkpoints; it does not yet replace the existing reducer or full-document writer.

## Next stages

Move the existing host history/item query surface to the bounded index API. Persist the actual reducer state atomically with the verified observation prefix, coordinate with the native realtime reduction and Rust rollout work, and support page-aware compaction, forks and deletion. Only then retire the full-document ceiling for a page-aware store, keeping bounded pages, context windows, manifests and explicit recovery. The Advantage consumer migration remains outside this SDK change.
