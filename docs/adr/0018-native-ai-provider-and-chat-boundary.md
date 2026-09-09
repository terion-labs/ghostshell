# ADR 0018: Native AI-provider and chat boundary

- Status: Accepted
- Date: 2026-07-23
- Extends: [ADR 0017](0017-native-dotnet-agent-runtime.md)

## Context

ADR 0017 selected an in-process .NET agent loop and deliberately left provider
I/O and application-tool authority outside its first slice. Asura now needs
real provider configuration, model discovery, and streamed conversation without
turning a model adapter into a second route to terminals or other application
operations.

Provider endpoints and streamed payloads are external, untrusted inputs.
Credentials must cross the network boundary without entering durable
definitions, transcripts, diagnostics, or presentation state. The desktop also
needs to remain honest about the difference between provider-backed chat and the
future governed agent that can inspect and operate panels.

## Decision

Add `Asura.Agent.Providers` as a native, in-process boundary. It depends on
the provider-neutral agent loop and application/Core contracts, but has no
terminal, session-host, process, filesystem, native-loading, JavaScript, or
Node.js execution authority. It implements Anthropic and OpenAI-compatible
model discovery and streaming; the official OpenAI endpoint uses the same
OpenAI-compatible adapter.

An `AiProviderProfile` stores only bounded configuration: provider kind,
normalized endpoint, default model, enabled state, order, and an opaque
`SecretRef` when authentication is required. Plain HTTP and unauthenticated
profiles are accepted only for exact loopback endpoints. Endpoint user
information, query strings, and fragments are rejected.

Every request is constructed beneath the configured base path and must retain
the configured scheme, host, and port. Automatic redirects, ambient
credentials, cookies, and proxy discovery are disabled, and the response origin
is rechecked. Model lists, request bodies, response bodies, SSE events, event
counts, provider fragments, and operation durations are bounded. Media types
and JSON/SSE shapes are parsed strictly. Cancellation is propagated, and
provider or protocol failures are mapped to typed, sanitized errors without
surfacing response bodies.

A request-scoped `IAgentProvider` keeps all parser and response state local to
each `StreamAsync` enumeration. Bounded native steering may begin one
replacement enumeration on the same adapter before a superseded enumeration
observes cancellation, so an adapter must support at most two concurrent
streams and must not let cancellation or parser state from one corrupt the
other. Anthropic and OpenAI-compatible conformance tests exercise this overlap
through the same adapter instance while the first transport request
deliberately ignores cancellation.

An API key is resolved for one request with the exact
`SecretScope(AiProvider, profileId)` and
`SecretUsePurpose(AiProviderAuthentication, profileId)`. Credential material is
not added to model messages or durable state. Mutable copies are cleared and
secret material is disposed best-effort; the unavoidable immutable request
header value is kept request-local.

The first desktop composition is intentionally chat-only.
`IAgentChatRuntime` exposes messages, provisional streamed text, cancellation,
and clear operations, but no target, terminal context, tool definition,
approval, or execution method. Each turn supplies an empty tool set and a
system instruction that states the assistant has no application or machine
access. Any model-generated tool proposal remains inert and is never forwarded
to the session host.

Provider-backed chat is not authorization. Connecting model output to terminal,
browser, file, process, MCP, or other application operations requires the
separate M3 target resolver, capability broker, approval policy, durable audit,
and accepted threat-model decisions. Provider adapters will not call those
operations directly.

Pi remains a behavior reference. Asura does not add a Node.js/Pi child
process for provider access or chat; doing so would add a runtime, process
supervision, IPC, and a second package supply chain without supplying the
session-host authorization boundary.

## Consequences

- Users can configure and test supported providers and use bounded native chat
  while terminal and application authority remain disconnected.
- Provider profiles are durable and portable only through opaque credential
  references; actual key values stay in the OS vault.
- Redirect-based endpoints, ambient system proxies, non-loopback plaintext
  HTTP, and permissive response parsing fail closed.
- Asura owns provider-protocol compatibility and must maintain strict
  conformance tests for requests, model discovery, streaming, limits,
  cancellation, and error mapping.
- In-process composition reduces packaging and lifecycle complexity but is not
  treated as a security boundary or permission grant.
- The UI must continue to label this slice as chat-only until the governed
  execution bridge is implemented and accepted.

## Alternatives rejected

- A Node.js/Pi sidecar duplicates runtime, IPC, lifecycle, and supply-chain work
  without removing Asura's authorization responsibilities.
- Giving provider adapters terminal or session-host clients creates a hidden
  control plane around policy, approval, and audit.
- Enabling ambient credentials, proxy discovery, or automatic redirects makes
  the destination and credential recipient less explicit.
- Supplying future tool schemas to the chat-only surface before the broker
  exists would make a presentation feature appear authorized when it is not.
