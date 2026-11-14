# Agent harness

The harness lives in `src/ResearchScout.Harness`. It decides everything about a run, and it knows nothing about papers, professors or LLM providers. Its only package is `Microsoft.Extensions.AI.Abstractions`, and a test checks that.

## The loop

`AgentLoop.RunAsync` does this:

1. Send the conversation and the tool declarations to the model.
2. If the model asks for tools, run them **in order**, add the results to the conversation, and go back to 1.
3. If the model gives a final answer, stop.

It also stops when:

| Stop reason | Status |
| --- | --- |
| Final answer | `Completed` |
| 12 model calls without a final answer (`MaxSteps`) | `MaxStepsReached` |
| The run's token or cost budget is used up | `BudgetExceeded` |
| Today's spend for the public demo hit the cap | `SpendLimitReached` |
| A model error that retries can't fix | `ModelError` |

If the caller cancels (for example the visitor closes the tab), the run stops at once and no status event is sent.

## Tools

- A tool implements `ITool`: `Name`, `Description`, `InputSchema` (JSON schema), `NeedsApproval` and `RunAsync`.
- Tools are added to a `ToolRegistry`. The model only gets **declarations** (name, description, schema as `AIFunctionDeclaration`), so nothing else can run a tool behind the harness's back.
- **Errors never crash a run.** An unknown tool, bad arguments, an exception or a "not found" all go back to the model as `Error: ...`, and the model can try something else. Stack traces are never sent.

The six Research Scout tools are listed in [Tools reference](06-tools-reference.md).

## Approval gate

- Tools with `NeedsApproval = true` (here: `draft_email`) pause the run until a human answers.
- **CLI:** a console prompt (`ConsoleApprovalGate`).
- **Web page:** an `approval` event shows Approve / Reject buttons. The answer comes back through `POST /runs/{runId}/approvals/{callId}`. No answer within **5 minutes** counts as a rejection.
- With no gate set up, the default is to **reject**.
- A rejection goes back to the model with the reason, so it can adjust.

## Budgets

| Limit | Default | Where |
| --- | --- | --- |
| Tokens per run | 200,000 | `RunBudget.MaxTotalTokens` |
| Cost per run | $0.05 | `RunBudget.MaxCostUsd` |
| Model calls per run | 12 | `AgentRunOptions.MaxSteps` |
| Spend per day (public demo) | $0.50 | `DAILY_SPEND_CAP_USD` |

- Cost is worked out per response from the price list in `appsettings.json` (USD per million input and output tokens). `CatalogCostCalculator` uses the provider and model that `SwitchableChatClient` stamps on each response.
- Daily spend is **one atomic Postgres upsert** (`DailySpendGuard`), so parallel runs can't lose updates. Once the cap is reached, the demo refuses politely until the next UTC day.

## Retries

- Exponential backoff with jitter: about 1s, then 2s, with 3 tries in total (`RetryPolicy`).
- Only temporary failures are retried: network errors, timeouts, HTTP 408, 429 and 5xx (including Anthropic's 529 "overloaded").
- `ProviderErrorClassifier` in `ResearchScout.Llm` knows each SDK's exception types, because the harness can't.
- **A streamed answer is never retried once text has been shown,** so text never appears twice.

## Streaming

- With `Stream = true` (the web page), the loop uses `GetStreamingResponseAsync` and sends each text piece to the observer as it arrives.
- The pieces are joined back into one `ChatResponse` with `ToChatResponse()`. That method doesn't copy the pieces' `AdditionalProperties`, so the loop copies them over itself. Without this, the "which model answered" stamp is lost and every streamed step would cost $0 (fixed Nov 2026, covered by `Streaming_KeepsStampsFromUpdates_SoCostIsCounted`).

## Tracing

- Every run and step is saved to Postgres (`runs`, `steps`): model, tokens, cost, time, number of tries, each tool's input, output and approval.
- Read a trace with `scout trace <runId>` or `GET /runs/{runId}/trace`.
- `IAgentObserver` gets events while the run happens (start, tool, step, text, finish). The API's `SseRunObserver` turns them into server-sent events.

## Provider switching

- `SwitchableChatClient` wraps one client per provider and picks the current model for each call.
- The admin chooses the model on the web page (`PUT /admin/llm`). The choice is saved in `app_settings` and applies **from the next run**.
- **One run stays on one model.** Gemini's thought signatures and Claude's thinking blocks don't carry over to other providers, so switching mid-run would break the conversation.
- Only providers with an API key are offered. If the default model has no key, the first available model is used.

| Provider | How it's called |
| --- | --- |
| Claude | Anthropic SDK |
| Gemini | Google GenAI SDK |
| OpenAI | `Microsoft.Extensions.AI.OpenAI` |
| DeepSeek | Anthropic SDK pointed at `https://api.deepseek.com/anthropic` |
