# A2A Protocol-Level Streaming Implementation

> **Spec version:** 1.0
> **Date:** 2026-05-14
> **Status:** Approved for implementation

## Goal

Advertise A2A protocol-level streaming in the OpenClaw Agent Card and fully implement the `message:stream` endpoint so A2A clients can receive streamed deltas via SSE instead of waiting for a single batched response.

---

## Architecture

OpenClaw continues to use the A2A SDK endpoint mappings (`MapA2AHttpJson` / `MapA2AJsonRpc`) without manually authoring new HTTP routes. The A2A SDK routes `POST /a2a/message:send` and `POST /a2a/message:stream` through the registered `IAgentHandler` based on the `StreamingResponse` flag in `RequestContext`.

Two files are modified:

| File | Responsibility |
|------|----------------|
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/A2A/OpenClawAgentCardFactory.cs` | Set `Capabilities.Streaming = true` when `MafOptions.EnableStreaming == true` |
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/A2A/OpenClawA2AAgentHandler.cs` | Branch on `context.StreamingResponse` — non-streaming keeps current buffered-message behavior, streaming emits A2A task events via `TaskUpdater` |

The existing `OpenClawA2AExecutionBridge` (`src/OpenClaw.Gateway/A2A/OpenClawA2AExecutionBridge.cs`) already streams OpenClaw runtime deltas; only the A2A event emission layer needs to change.

---

## Agent Card Changes

`OpenClawAgentCardFactory.Create` sets `Capabilities.Streaming` from `MafOptions.EnableStreaming`:

```csharp
Capabilities = new AgentCapabilities
{
    Streaming = _options.EnableStreaming,
    PushNotifications = false
}
```

When `EnableStreaming` is true (the default), clients that resolve the agent card will see `streaming: true` and know `message:stream` is available.

---

## Handler: Non-Streaming Path (unchanged)

`POST /a2a/message:send` continues through the existing code path:

1. Call `_bridge.ExecuteStreamingAsync`, accumulating text deltas into `StringBuilder responseText`.
2. If bridge throws: log, emit `A2A request failed.` message.
3. If runtime error: log, emit error content as message.
4. If zero deltas: emit `[AgentName] Request completed.` message.
5. Emit the single buffered message via `eventQueue.EnqueueMessageAsync(...)`.

This path is unchanged.

---

## Handler: Streaming Path (new)

`POST /a2a/message:stream` arrives with `context.StreamingResponse == true`. The handler uses `TaskUpdater` to emit A2A task lifecycle events instead of buffering:

1. **Initialize task:** `await updater.SubmitAsync()`
2. **Start work:** `await updater.StartWorkAsync(initialMessage)` — transitions task to `Working` state so clients see progress immediately.
3. **Stream deltas:** for each non-empty `AgentStreamEventType.TextDelta`, emit an artifact chunk via:
   ```csharp
   await updater.AddArtifactAsync(
       [Part.FromText(evt.Content)],
       artifactId: "text-delta",
       name: "Streaming response",
       description: "Incremental text from agent",
       lastChunk: false,
       append: true);
   ```
4. **Error handling:** if runtime error or bridge exception:
   - log the error
   - emit `TaskUpdater.FailAsync(errorMessage)` — this transitions task to `Failed` state
   - return (do not continue)
5. **Empty delta:** if no deltas at all, emit one artifact chunk containing `[AgentName] Request completed.`
6. **Complete:** `await updater.CompleteAsync(finalMessage)` — transitions task to `Completed` state with the final text.

The `artifactId` is fixed to `"text-delta"` so all delta chunks append to the same artifact, allowing clients to reassemble the full response incrementally.

---

## Error Handling Summary

| Runtime condition | Non-streaming response | Streaming response |
|-------------------|------------------------|--------------------|
| Text deltas produced | Single `Message` with concatenated text | Task events: Submit → Working → Artifact chunks → Complete |
| No text, no error | `[AgentName] Request completed.` message | Single artifact chunk with fallback text → Complete |
| Runtime `Error` event | Message with error text | `FailAsync` with error text |
| Bridge exception | `A2A request failed.` message | `FailAsync` with error text |
| Cancellation | Propagate `OperationCanceledException` | Propagate `OperationCanceledException` |

---

## Cancellation

Both paths check for `OperationCanceledException` and rethrow it. The A2A SDK handles task transition to `Canceled` state automatically on cancellation.

---

## A2A REST SSE Format

The A2A SDK serializes `StreamResponse` events as Server-Sent Events (SSE). Each line is:

```
data: <JSON>
```

where `<JSON>` is the JSON representation of an `A2A.StreamResponse`. For A2A SDK v1.0.0-preview2, the SSE content type is `text/event-stream`.

The streaming path emits the following `StreamResponse` events in order:

```
data: {"task":{"id":"<taskId>","status":{"state":"submitted"}}}
data: {"statusUpdate":{"taskId":"<taskId>","status":{"state":"working"}}}
data: {"artifactUpdate":{"taskId":"<taskId>","artifact":{...},"append":true,"lastChunk":false}}
... (one per TextDelta chunk) ...
data: {"statusUpdate":{"taskId":"<taskId>","status":{"state":"completed","message":{...}}}}
```

---

## JSON-RPC Streaming

JSON-RPC clients use the same `SendStreamingMessageAsync` path through `IAgentHandler`. The A2A SDK provides `JsonRpcStreamedResult` which writes `StreamResponse` objects as SSE. OpenClaw's `MapA2AJsonRpc` registration uses the same handler, so the streaming behavior applies to both HTTP+JSON and JSON-RPC.

---

## Test Plan

### Discovery

- `AgentCard_Creates_DefaultSkill_When_NoneConfigured`: update assertion `Assert.False(card.Capabilities!.Streaming)` → `Assert.True(card.Capabilities!.Streaming)` when `EnableStreaming = true`.
- Add a new test: when `EnableStreaming = false`, `card.Capabilities!.Streaming` is `false`.

### Non-Streaming Regression

- `MessageSend_BridgeException_Returns_Agent_Error_Message` — unchanged.
- `MessageSend_BridgeCompletesWithoutText_Returns_Fallback_Agent_Message` — unchanged.
- `MessageSend_WithoutMessageId_Returns_Agent_Message` — unchanged.

### Streaming (new tests in `A2AHttpEndpointTests`)

1. `MessageStream_Returns_SseContentType`: POST to `/a2a/message:stream`, assert `Content-Type: text/event-stream`.
2. `MessageStream_Emits_Task_Submitted_And_Working`: verify first two events are submitted task and working status update.
3. `MessageStream_Emits_Artifact_Delta_Chunks`: verify artifact update events contain text chunks, all share `artifactId: "text-delta"`, with `append: true` and `lastChunk: false`.
4. `MessageStream_Emits_Completed_Task_With_Final_Message`: verify final event is `statusUpdate` with `state: "completed"` and a message part.
5. `MessageStream_BridgeException_Emits_Failed_Task`: verify final event is `statusUpdate` with `state: "failed"`.
6. `MessageStream_NoDeltas_Emits_Fallback_Artifact_Then_Complete`: verify the single artifact chunk contains fallback text.

### JSON-RPC Streaming (new test in `A2AIntegrationTests`)

7. `MessageStream_ViaJsonRpc_Emits_Streaming_Events`: POST JSON-RPC streaming request, verify SSE events match the same sequence as REST.

---

## Files Modified

| File | Change |
|------|--------|
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/A2A/OpenClawAgentCardFactory.cs:27-31` | Set `Capabilities.Streaming = _options.EnableStreaming` |
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/A2A/OpenClawA2AAgentHandler.cs` | Add streaming branch in `ExecuteAsync`, use `TaskUpdater` for streaming path |
| `src/OpenClaw.Tests/A2AHttpEndpointTests.cs` | Update discovery test assertion, add streaming SSE tests |
| `src/OpenClaw.Tests/A2AIntegrationTests.cs` | Add JSON-RPC streaming test |
| `docs/a2a.md` | Update "Streaming" section to reflect full protocol streaming support |

---

## No Placeholders

- All task events, states, and JSON field names are drawn directly from A2A SDK v1.0.0-preview2 `StreamResponse`, `TaskUpdater`, and `TaskStatusUpdateEvent` APIs confirmed by reading NuGet XML documentation.
- Test assertions use actual type names and property values from those APIs.
- File paths are exact relative paths from repository root.