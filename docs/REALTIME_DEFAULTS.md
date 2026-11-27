# Realtime model and voice selection

The realtime defaults and voice catalogs were reviewed against
[Codex rust-v0.172.0](https://github.com/openai/codex/tree/3d86e43b075adeba2193ec5c22ac92f55afd82e6).
See core/realtime_conversation.rs (model selection), protocol/src/protocol.rs
(RealtimeVoicesList), codex-api/src/endpoint/realtime_websocket/protocol.rs
(voice_or_default), and codex-api/src/endpoint/realtime_call.rs (ChatGPT call
override).

| Adapter | Model default | Adapter voice | ChatGPT WebRTC call voice |
| --- | --- | --- | --- |
| V1 | gpt-realtime-1.5 | cove | cove |
| V2 | gpt-realtime-1.5 | marin | not supported |
| V3 | gpt-live-1.1-codex | marin | cove |

V3 now includes marin in its voice list. Explicit model or voice settings are
not replaced. Unset or null Model/Voice selects the actual destination default.
The Voice getter preserves the V3 ChatGPT call default for compatibility;
ResolveVoice selects the actual destination (false for direct WebSocket or API).
To continue requesting the old model explicitly set Model = "gpt-live-1-codex".
Requested model is not proof of provider-reported identity.

This is a scoped review of defaults, not a change to the broader fingerprinted
protocol baseline or a claim of live-provider acceptance.
