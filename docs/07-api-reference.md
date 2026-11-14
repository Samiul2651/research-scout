# API reference

The API is an ASP.NET Core minimal API in `src/ResearchScout.Api/Program.cs`. It also serves the web page from `wwwroot`.

Base URL (live): `https://research-scout-n8fg.onrender.com`

## Endpoints

| Method | Path | Auth | What it does |
| --- | --- | --- | --- |
| GET | `/health` | none | Liveness check. No database call. |
| GET | `/status` | none | Current model, today's spend, daily cap, number of papers |
| GET | `/papers` | none | Ingested papers, newest first (max 200) |
| POST | `/ask` | none, rate-limited | Runs the agent on a question; streams server-sent events |
| POST | `/runs/{runId}/approvals/{callId}` | none | Approves or rejects a paused tool call |
| GET | `/runs/{runId}/trace` | none | The full trace of one run |
| GET | `/admin/llm` | `X-Admin-Password` | Current model and the models you can switch to |
| PUT | `/admin/llm` | `X-Admin-Password` | Sets the model for the next runs |

## Limits

| Limit | Value | Applies to |
| --- | --- | --- |
| Questions per visitor (IP) | 10 per hour | `POST /ask` |
| Runs at the same time | 3 (`MAX_CONCURRENT_RUNS`) | `POST /ask`; returns 503 "The demo is busy" when full |
| Admin attempts | 10 per minute per IP | `/admin/*` |
| Question length | 1–1000 characters | `POST /ask` |
| Daily spend | $0.50 (`DAILY_SPEND_CAP_USD`) | All runs; the agent refuses politely once reached |
| Browser origins (CORS) | only those in `ALLOWED_ORIGINS` | Calls from other websites; the built-in page is same-origin |

---

## `GET /health`

```json
{ "status": "ok" }
```

## `GET /status`

```json
{
  "model": "DeepSeek V4.1 Flash",
  "dailySpentUsd": 0.0123,
  "dailyCapUsd": 0.50,
  "papers": 5
}
```

## `GET /papers`

```json
[
  { "id": "arxiv:2303.12570", "title": "RepoCoder: ...", "year": 2023, "url": "https://www.semanticscholar.org/paper/...", "pages": 14 }
]
```

## `POST /ask`

Request:

```json
{ "question": "In the RAG paper (arXiv:2005.11401), which retriever is used?" }
```

The response is a **server-sent events** stream (`text/event-stream`). Events arrive in this order:

| Event | When | Data |
| --- | --- | --- |
| `run` | Once, at the start | `{ runId, model }` |
| `tool` | The model asked for a tool | `{ step, name, input }` |
| `approval` | A tool needs a human answer | `{ runId, callId, tool, input, expiresInSeconds }` |
| `step` | A model call finished | `{ number, inputTokens, outputTokens, costUsd, ms, tools: [{ name, input, output, isError, approved }] }` |
| `text` | A piece of the answer arrived | `{ delta }` |
| `done` | The run finished | `{ runId, status, finalText, ... }` |
| `error` | Something failed on the server | `{ message }` |

Notes:

- Tool output in `step` events is cut at 600 characters. The full output is in the trace.
- Closing the connection cancels the run.
- Errors before the stream starts: `400` (empty or too-long question), `429` (rate limit), `503` (busy).

Example with curl:

```bash
curl -N -X POST https://research-scout-n8fg.onrender.com/ask \
  -H "Content-Type: application/json" \
  -d '{"question":"Which retriever does the RAG paper (arXiv:2005.11401) use?"}'
```

## `POST /runs/{runId}/approvals/{callId}`

Answers an `approval` event. Unanswered requests are rejected after **5 minutes**.

```json
{ "approve": true }
```

```json
{ "approve": false, "reason": "Wrong professor" }
```

Returns `200 { "ok": true }`, or `404` if nothing is waiting (already answered or timed out).

## `GET /runs/{runId}/trace`

```json
{
  "id": "ea53c31f-...",
  "userMessage": "...",
  "provider": "deepseek",
  "model": "deepseek-flash",
  "status": "Completed",
  "stopReason": null,
  "finalText": "...",
  "inputTokens": 6543,
  "outputTokens": 299,
  "costUsd": 0.0023,
  "startedAt": "2026-11-14T13:10:02Z",
  "finishedAt": "2026-11-14T13:10:07Z",
  "steps": [
    {
      "number": 1, "model": "deepseek-flash", "inputTokens": 1803, "outputTokens": 96,
      "costUsd": 0.00066, "attempts": 1, "durationMs": 2700, "textPreview": "",
      "toolCalls": [ { "name": "search_papers", "input": "...", "output": "...", "isError": false } ]
    }
  ]
}
```

Returns `404` for an unknown run.

## `GET /admin/llm`

Header: `X-Admin-Password: <ADMIN_PASSWORD>`

```json
{
  "current": { "provider": "deepseek", "modelId": "deepseek-flash" },
  "available": [
    { "provider": "deepseek", "modelId": "deepseek-flash", "displayName": "DeepSeek V4.1 Flash", "inputUsdPerMillion": 0.30, "outputUsdPerMillion": 1.20 }
  ]
}
```

Only models whose provider has an API key are listed.

## `PUT /admin/llm`

Header: `X-Admin-Password: <ADMIN_PASSWORD>`

```json
{ "provider": "anthropic", "modelId": "claude-haiku-5-5" }
```

Returns `200 { "current": ... }`, or `400` if the model is unknown or its key isn't set. The change applies **from the next run**.

Admin responses: `401` for a wrong password; `503` if `ADMIN_PASSWORD` isn't set. The password check uses constant-time comparison.
