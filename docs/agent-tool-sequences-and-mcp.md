# Tool sequences and external MCP access

## Sequences

`agent.run_sequence` runs existing tools serially in one model call. For example:

```json
{
  "steps": [
    { "tool": "terminal.submit_text", "arguments": { "panel_id": "<observed-panel-id>", "text": "ls" } },
    { "tool": "terminal.read_screen", "arguments": { "panel_id": "<observed-panel-id>" }, "delay_ms": 250 }
  ]
}
```

Use the exact tool names and argument schemas in the supplied manifest. Exact-panel runs omit `panel_id` where their individual tool schema omits it. Browser references and document revisions must come from observations. If an action changes the document and invalidates those references, obtain a new snapshot before preparing the next sequence.

There are 1–32 steps. Each optional `delay_ms` runs **before** its step, from 0 to 10,000 ms, with at most 30,000 ms of explicit delays in the whole sequence. Existing condition-based wait tools can also be used as steps. Each step gets fresh scope checks, its own authorization and audit, and the ordinary visible approval when policy requires it. A sequence never grants permission to its children.

Envelope validation rejects unknown tools, nested sequences, user-question/capability intrinsics, and invalid delays before dispatch. Each tool validates its own arguments immediately before its execution. The sequence stops on the first denied, failed, cancelled, or uncertain action. Earlier effects are not rolled back. Only run sequences whose arguments are already known; result substitution and scripting are not supported.

The result includes ordered `results` with zero-based `index`, tool, `ok`, code, and content, plus `executed_steps` and `remaining_steps`. `executed_steps` counts attempted calls, including the failed call. Payloads above 4 KiB are replaced with `content_omitted: true` so receipts stay within the result budget. Read a large observation separately when needed. Transport cancellation can prevent delivery of the receipt; inspect current state before retrying mutations.

## Enable the MCP server

Open **Settings → Agent → Use Asura with another agent** and turn on **Enable MCP server**. The server is off by default. The switch starts or stops it immediately, and the app remembers your choice for the active profile. On subsequent launches it starts after the profile is unlocked. It binds only to `127.0.0.1`.

The default port is `18765`. To change it, enter a port from 1024 to 65535 and select **Apply / retry**. The status reports whether the server is running and shows startup failures, such as an occupied port, without blocking the app. Use **Copy URL** and **Copy token** to configure your agent; no environment variables or manual file edits are needed.

The endpoint is `http://127.0.0.1:18765/mcp`. When a token is first needed, Asura generates a 256-bit token in `mcp-token` inside the active profile's data directory. On Unix, the file is created with owner-only read/write permissions. Existing files with broader permissions or symbolic links are rejected. The token is never printed in application logs.

Configure your harness's Streamable HTTP MCP connection with that URL and the header `Authorization: Bearer <copied-token>`. Use the literal `127.0.0.1` hostname. The token is checked on every request, including discovery and requests that resume a legacy MCP session. Cross-origin browser requests and unexpected Host headers are rejected. This is a local bearer-token connection, not an OAuth authorization server or a remotely exposed service.

The official C# SDK 2.2.0 serves the [2026-07-28 protocol](https://modelcontextprotocol.io/specification/2026-07-28) natively, including `server/discover` and per-request metadata. The same endpoint supports the `2025-11-25` initialize/session flow for older clients. The SDK handles protocol headers, negotiation, and transport framing. Modern clients must send both their protocol metadata and `MCP-Protocol-Version` header as required by the specification.

Call `asura.workspaces` to obtain IDs of open workspaces with an attached agent runtime. Pass `workspace_id` with each tool invocation. `tools/list` adds that field to the existing native tool definitions. For `agent.run_sequence`, supply `workspace_id` once at the top level; every step stays inside that workspace. Tool arguments, panel IDs and browser references retain their usual validation. Internal user-question and capability-request intrinsics are not exposed. This endpoint exposes Asura's native tools; it does not establish downstream third-party MCP connections for the external harness.

Calls do not invoke an LLM or require provider credentials. They use the workspace's configured capability policy, ordinary desktop approval cards, live target checks and action audit. Permission changes are made in Asura, not through a remote approval tool. Internal and external operators share one governed workspace run at a time. Clear the current run in the agent panel before switching operators. An uncertain action revokes the external run; inspect the workspace and clear that run before continuing.

Turn off **Enable MCP server** in Settings to stop accepting connections and keep it off on later launches. Select **Replace token** to disconnect existing clients and invalidate their credential immediately, then **Copy token** and update your agent. Shutdown stops the HTTP server before disposing workspace hosts.

## Implementation boundaries

Sequences and external calls use `GovernedAgentRuntime.ExecuteProposalAsync`. They do not call terminal, browser, file or process adapters directly. The MCP server lives in `Asura.Mcp.Server`; the existing `Asura.Mcp` project remains the client boundary for downstream MCP servers. Dependency versions are centrally pinned and transport tests exercise both protocol generations, bearer authentication, and origin/Host rejection.
