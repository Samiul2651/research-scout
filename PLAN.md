# Research Scout — PLAN.md (v2)

**One line:** A C#/.NET research assistant, driven by my own agent harness, that (1) answers questions about research papers with exact page citations and (2) ranks professors by how well their recent papers fit my AI4SE / LLM interests, then drafts an outreach email.

**Problem it solves for me:** I'm applying to funded thesis MSc programs (MUN, UNB in Canada; Italy; Sweden) and writing a RAG paper. Right now I read professor pages and papers by hand. This finds the right supervisors and lets me question papers fast, with sources I can check.

**Deploy:** ON — free hosting, public demo link for my CV
**Time box:** 1 weekend (Saturday + Sunday) to build + 1 weekday evening to deploy
**Budget:** under $5 of API spend for the build; live demo capped at **$0.50 per day**

### What changed from v1
| Part | v1 | v2 | Why |
|---|---|---|---|
| Deploy | OFF | **ON** (Render free, Docker) | Live demo link for CV |
| .NET | 9 | **10** | 10 is the current LTS (long-term support) |
| Embeddings | Ollama local | **Voyage AI API** | Free host has 512 MB RAM, can't run Ollama; first 200M Voyage tokens are free |
| Vector DB | Qdrant in Docker | **Qdrant Cloud free cluster** (1 GB RAM, 4 GB disk) | Same Qdrant code, just hosted |
| Storage | SQLite | **Supabase Postgres** | Render free disk is wiped on every restart/sleep |
| UI | Console only (web UI was stretch) | **Small web page** served by the API | A demo link needs a page |
| Approval gate | Console prompt | Console **and** web (SSE event + approve endpoint) | Works in both places |
| Cost guard | Per-run budget | Per-run budget **+ daily $ cap** | Public demo spends my money |

---

## Tech choices

| Part | Choice | Why |
|---|---|---|
| Language | C# / .NET 10 (LTS), ASP.NET Core Web API + console app | My main stack |
| LLM | Anthropic Messages API (Claude Haiku), raw `HttpClient`, **no SDK** | The harness is the point: I build the loop myself |
| Embeddings | Voyage AI API (one model for ingest and queries) | Free tier, no GPU needed on the host |
| Vector DB | Qdrant Cloud free cluster | Already used Qdrant in CodeLens |
| Keyword search | BM25 (in-memory index, rebuilt from Postgres on startup) | Hybrid search = BM25 + vectors |
| Paper data | Semantic Scholar API (authors, papers) + arXiv (PDFs) | Free, no key needed for low volume |
| PDF text | PdfPig | Keeps page numbers for citations |
| Storage | Supabase Postgres via EF Core + Npgsql | Survives restarts; I already use Supabase |
| Hosting | Render free web service, Docker | Free; sleeps after 15 min idle, ~1 min to wake |
| CI/CD | GitHub Actions: build + test → Render deploy hook | Auto deploy on push to `main` |

---

## Architecture

```
ResearchScout.slnx
├── Harness/          # my agent loop — knows nothing about papers
│   ├── AgentLoop       (LLM → tool calls → results → repeat)
│   ├── ToolRegistry    (tools + JSON schemas)
│   ├── AnthropicClient (raw HttpClient, streaming SSE)
│   ├── Budget          (max steps, max tokens, max $ per run, daily $ cap)
│   ├── IApprovalGate   (console or web implementation plugs in)
│   └── ITraceStore     (interface only; Postgres version lives in Infra)
├── Papers/           # ingest + retrieval
│   ├── PaperFetcher    (Semantic Scholar + arXiv, responses cached in Postgres)
│   ├── PdfChunker      (chunks keep paper id + page number)
│   ├── IEmbedder + VoyageEmbedder
│   └── HybridSearch    (BM25 + Qdrant, merged with RRF)
├── Scout/            # the two features, written as tools
│   ├── Tools/…
│   └── FitScorer
├── Infra/            # EF Core DbContext, Postgres trace store, daily spend table
├── Api/              # ASP.NET Core endpoints + SSE + wwwroot (web page)
├── Cli/              # `scout ask`, `scout trace`, `scout ingest`
└── Eval/             # small citation + ranking checks
```

**Rule:** `Harness` must not reference `Papers`, `Scout` or `Infra`. Tools, trace store and approval gate plug in from outside through interfaces. That way the harness is reusable for my other projects.

---

## Harness spec (the core)

1. **Loop:** send messages + tool schemas → if the reply has `tool_use` blocks, run each tool, append `tool_result`, repeat → stop on `end_turn`, max steps (default 12), or budget hit.
2. **Tool interface:** `ITool { Name; Description; JsonSchema InputSchema; Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct); bool NeedsApproval; }`
3. **Approval gate:** tools with `NeedsApproval = true` (for example `draft_email`) pause the loop and call `IApprovalGate`.
   - Console: y/n prompt.
   - Web: send SSE event `approval_needed {runId, tool, input}`; wait for `POST /runs/{runId}/approve` or `/reject`; auto-reject after 5 minutes.
4. **Errors:** a tool that throws returns `is_error: true` to the model instead of crashing the loop. Retry API calls on 429/5xx with backoff (max 3 tries).
5. **Budget:**
   - Per run: count input/output tokens from each response; stop and report when over the limit.
   - Per day: before each LLM call, check today's spend (UTC) in Postgres; if it would pass **$0.50**, stop and return "Daily demo limit reached, try tomorrow." Add spend with one atomic `UPDATE … SET spent = spent + @cost` (no read-then-write race).
6. **Tracing:** save each step (prompt size, tool name, input, output preview, tokens, cost, ms) through `ITraceStore`. CLI `trace <runId>` prints it; `GET /runs/{runId}/trace` returns it.
7. **Streaming:** stream text and step events to the client over SSE.

---

## Tools the agent gets

| Tool | Does | Approval |
|---|---|---|
| `find_professors` | Search Semantic Scholar authors by university + topic keywords | No |
| `get_recent_papers` | Last 3 years of papers for one author | No |
| `ingest_paper` | Download arXiv PDF → chunk with page numbers → embed (batched) → Qdrant + chunk text in Postgres | No |
| `search_papers` | Hybrid search over ingested chunks, returns text + paper + page | No |
| `score_fit` | Score an author vs my interest profile (embedding similarity of their abstracts to my profile + recency weight) | No |
| `draft_email` | Draft a short outreach email citing 1–2 of their real papers | **Yes** |

My interest profile lives in `profile.md` (RAG, code intelligence, LLMs for SE, event-driven systems).

---

## Tasks (small, one per bot run)

### Saturday — harness + paper pipeline
- [x] **T1** Create solution, projects and folder layout above. Add `docker-compose.yml` with local Postgres + local Qdrant for dev. Config keys read from env / user secrets: `ANTHROPIC_API_KEY`, `VOYAGE_API_KEY`, `QDRANT_URL`, `QDRANT_API_KEY`, `DATABASE_URL`.
- [ ] **T2** `AnthropicClient`: non-streaming Messages call with tools; typed request/response models.
- [ ] **T3** `ToolRegistry` + `ITool`; serialize tool schemas into the request.
- [ ] **T4** `AgentLoop`: loop, tool dispatch, error-to-model handling, max steps.
- [ ] **T5** `Budget` (per-run limits) + retry with backoff.
- [ ] **T6** `Infra`: EF Core DbContext + first migration (tables: `runs`, `steps`, `professors`, `papers`, `chunks`, `api_cache`, `daily_spend`). `PostgresTraceStore` + `scout trace <runId>`.
- [ ] **T7** `IApprovalGate` + console implementation.
- [ ] **T8** `PaperFetcher`: Semantic Scholar author search + recent papers; arXiv PDF download. Cache API responses in `api_cache`.
- [ ] **T9** `PdfChunker` with PdfPig: ~500-token chunks, 50 overlap, keep page number.
- [ ] **T10** `VoyageEmbedder` (batched calls) + Qdrant upsert/search. Collection vector size comes from config, matching the Voyage model.
- [ ] **T11** BM25 index (built from `chunks` table on startup) + `HybridSearch` with reciprocal rank fusion.

### Sunday — features, eval, web page
- [ ] **T12** Wrap the tools in the table above as `ITool` classes.
- [ ] **T13** `FitScorer` + `score_fit` tool.
- [ ] **T14** System prompt: answers must cite `[paper, page]` for each claim; say "not found" instead of guessing.
- [ ] **T15** Streaming in `AnthropicClient` + SSE endpoint `POST /ask`.
- [ ] **T16** Daily $ cap in `Budget` (uses `daily_spend` table, atomic update) + web `IApprovalGate` (SSE event + approve/reject endpoints, 5-min timeout).
- [ ] **T17** Web page in `Api/wwwroot` (plain HTML + JS, no framework): ask box, streamed answer, citation links, step list, approve/reject buttons, "today's demo budget used" bar. Designer bot makes the mockup first.
- [ ] **T18** `Eval`: 15 hand-written questions over 5 papers → check the cited page holds the answer (citation accuracy); 1 university → check my known good-fit professors land in the top 5.

### Weekday evening — deploy
- [ ] **D1** Create Supabase project + Qdrant Cloud free cluster; run EF migration against Supabase; ingest the 5 demo papers + 1 university's professors into the live stores.
- [ ] **D2** `Dockerfile` (multi-stage, .NET 10 runtime image) + `GET /health` (no DB call).
- [ ] **D3** Production setup: CORS (only my portfolio domain + the Render URL), rate limiting on `/ask` (ASP.NET built-in limiter, as a basic floor), HTTPS-only, no keys in logs.
- [ ] **D4** GitHub Actions: build + run tests on every PR; on push to `main`, call the Render deploy hook.
- [ ] **D5** Create the Render free web service from the repo; set env vars; smoke test both "done means" questions on the live URL.
- [ ] **T19** README: problem, live demo link, architecture diagram, harness design, eval numbers, demo GIF, note about ~1 min cold start.

### Stretch (only if time is left)
- [ ] Parallel tool calls in one step
- [ ] Keep-alive ping every 14 min (only if cold starts hurt the demo; uses free instance hours)
- [ ] Link the demo from my portfolio site

---

## Review checklist (strict reviewer bot)
- Harness has no references to `Papers` / `Scout` / `Infra`.
- No API key in code, logs or traces; keys from user secrets / env vars only.
- No HTTP or DB calls inside loops that could be one batch call (embed chunks in batches; insert chunks in one batch).
- Every external call has a timeout and cancellation token.
- Tool errors never crash the loop.
- Daily spend update is atomic (no read-then-write).
- Nothing important is written to local disk (Render free disk is wiped).

## Done means
- `scout ask "Which MUN professors work on LLMs for code?"` returns a ranked list with reasons and paper links.
- `scout ask "How does paper X evaluate retrieval?"` returns an answer with `[paper, page]` citations.
- Both questions work on the **live URL**, streamed in the web page.
- Eval prints citation accuracy and top-5 hit rate.
- Trace of any run can be printed step by step (CLI) and fetched (API).
- After $0.50 of spend in a day, the live demo refuses politely.

## CV bullet (target, fill real numbers after eval)
**Research Scout** — C#, .NET, Anthropic API, Qdrant, Voyage AI, PostgreSQL, Docker · [live demo]
- Built my own LLM agent harness from scratch (tool registry, approval gates, token + daily cost budgets, step tracing, SSE streaming) with no agent SDK.
- Built hybrid RAG (BM25 + vectors, RRF) over research papers with page-level citations; [X]% citation accuracy on a [N]-question eval set.
- Ranks supervisors by matching their recent papers to a research profile; good-fit professors in the top 5 for [X]/[Y] test cases.
- Deployed with Docker + GitHub Actions CI/CD to free hosting, with a daily spend cap for the public demo.
