# ADR 0005: Agent sidecar behind the Asura capability broker

- Status: Superseded by [ADR 0017](0017-native-dotnet-agent-runtime.md)
- Date: 2026-07-22

> Historical record only. This sidecar proposal must not be implemented.
> Asura's accepted agent runtime is native .NET; Pi remains reference
> material and neither Pi nor Node.js is packaged or launched.

## Context

Asura needs provider/model flexibility, streaming, steering, compaction, and tool calls, but authorization, secrets, target resolution, and audit are product responsibilities. The agent must control remote terminals without installing software on remote machines.

## Superseded decision

The superseded proposal would have run a version-pinned local Pi sidecar for the first M3 integration spike behind a small versioned JSON-RPC/stdio adapter. The proposed sidecar would have received model context and opaque Asura tool schemas, but no engine objects, connection credentials, provider secret values, or authority to execute tools directly.

The proposal kept panel/screen/workspace target resolution, effective policy, approvals, `SecretRef` resolution, typed application operations, bounded output, and requested/completed audit transitions in Asura. `Off`, `Ask`, `Auto`, and `YOLO` would have been enforced in the session host. Cancellation would have closed provider streams and pending tool work without bypassing audit.

The proposed adapter was intended to be replaceable by a native .NET loop, with Core, Application, and Protocol independent of TypeScript types and Pi session files. The proposal required the spike to record the selected Pi revision, license compatibility, process supervision, upgrade/rollback, and provider compatibility before shipping.

## Consequences of the superseded proposal

- The first implementation would have reused a mature local agent loop while keeping trust decisions in Asura.
- Sidecar process supervision and protocol compatibility would have become release responsibilities.
- Remote machines receive only terminal input and return terminal output through existing connections; no remote agent is installed.
- If license, stability, or packaging evidence had been unacceptable, the same ports would have supported an ADR-approved alternative.

## Alternatives rejected

- Letting a provider or sidecar call local tools directly creates a hidden control plane.
- Putting secrets in prompts makes non-disclosure unverifiable.
- Binding the domain to one provider SDK would make provider and local-model support expensive.
