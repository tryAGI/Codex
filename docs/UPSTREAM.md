# Protocol provenance

Reviewed upstream: OpenAI Codex `6ff1e1ca3b30fc8b914720681fcf5fe62e95f856` (`rust-v0.166.0`).
Every file listed below is fingerprinted in `ProtocolBaseline.json`, and a daily workflow opens a
review issue when a newer stable release changes one of them.

Notes from the `rust-v0.166.0` review:

- `apply_patch` removed its LF-normalizing update mode and always preserves line endings, so
  `CodexPatch.Update` now defaults to `PreserveLineEndings`; `NormalizeToLf` reproduces older releases.
- The realtime WebSocket gained a transport-only close (a normal close frame without
  `session.close`) for observers of a frontend-owned call: `CodexRealtimeConnection.CloseTransportAsync`.
- Assistant messages may carry the new `partial_answer` phase, and output items gained optional
  `encrypted_content`/`status`; both pass through this library unchanged as raw JSON.
- Token refresh gained an optional diagnostic `refresh_reason` parameter; it is not sent.

The official TypeScript SDK invokes `codex exec`; a literal port would keep the binary dependency.
This library instead implements the relevant network contracts documented by the official Rust source:

- `codex-rs/codex-api/src/endpoint/realtime_call.rs`: ChatGPT JSON SDP/session creation,
  `realtime/calls?intent=quicksilver&architecture=avas`, response Location/call ID.
- `codex-rs/codex-api/src/endpoint/realtime_websocket/methods.rs`: sideband `/v1/live/{call_id}`.
- `methods_frameless_bidi.rs`, `protocol_frameless_bidi.rs`: session, delegation, transcript and
  UTF-8-safe 500-byte context appends.
- `realtime_websocket/methods_common.rs`: `conversation_item_create_message` maps frameless
  conversation text to `session.context.append`, discarding the role. `AppendUserTextAsync` uses this
  V3 route with the speakable channel; it promises no preserved user-item identity, provider
  acknowledgement, reply or physical effect.
- `codex-rs/core/src/realtime_conversation.rs`: `quicksilver=v2`, `gpt-live-1-codex` and the V3 lifecycle.
- `codex-rs/protocol/src/protocol.rs`: `RealtimeVoicesList::builtin`; V3 uses the V1 ChatGPT voice
  catalog and defaults to `cove`, not the V2 default `marin`.
- `codex-rs/core/src/client.rs`: Responses stream, `store=false`, encrypted reasoning continuation.
- `codex-rs/core/src/compact_remote_v2_attempt.rs` and `compact_remote_v2.rs`: normal Responses with
  `compaction_trigger`, parallel tools and no output schema, exactly one compaction output, terminal
  response ID/usage. Additional output items are ignored, never executed by this client.
- `codex-rs/protocol/src/models.rs`: `compaction`, `compaction_summary` alias and payload-free
  `compaction_trigger`; `context_compaction` is a different item and is not accepted as a checkpoint.
  Function/custom call identity, freeform `input` and the corresponding output types.
- `codex-rs/tools/src/tool_spec.rs` and `responses_api.rs`: function/custom/namespace advertisements;
  `codex-rs/protocol/src/tool_name.rs`: default namespace aliases.
- `codex-rs/login/src/device_code_auth.rs` and `oauth/client.rs`: device authorization and token exchange.
- `codex-rs/apply-patch/src/parser.rs`, `streaming_parser.rs`, `seek_sequence.rs`, `text_file.rs`,
  `file_update.rs`: marker grammar, context indices, fuzzy/EOF lookup and line-ending-preserving
  reconstruction (plus the legacy LF mode of earlier releases). The C# implementation performs pure text operations only; path resolution, filesystem
  effects and process interception are the host's.
- `codex-rs/apply-patch/src/lib.rs`: sequential add/delete/update/move behaviour, including
  overwriting an existing add/move destination. `CodexWorkspacePatch.Plan` evaluates virtual byte
  contents and keeps every original hash/absence precondition. Net atomic planning differs from
  Rust's sequential writes and partial-effect delta.

Source: https://github.com/openai/codex/tree/6ff1e1ca3b30fc8b914720681fcf5fe62e95f856

OpenAI's source is Apache-2.0. This implementation is independently written from the protocol;
no upstream prompt, product policy or credential is copied. `CodexDeviceAuthorization.OfficialCodexClientId`
is the public OAuth client identifier the open-source Codex CLI ships with, not a secret.

These are internal Codex contracts and may change without notice. Unit tests cover the wire shapes;
a live authenticated run is a separate check.
