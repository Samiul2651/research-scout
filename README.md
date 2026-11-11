# Research Scout

A C#/.NET research assistant, driven by my own agent harness (no agent SDK), that:

1. answers questions about research papers with exact `[paper, page]` citations, and
2. ranks professors by how well their recent papers fit my AI4SE / LLM interests, then drafts an outreach email.

> Work in progress. See [PLAN.md](PLAN.md) for the full plan and task list.

## Project layout

| Project | What it holds |
|---|---|
| `ResearchScout.Harness` | The agent loop. Knows nothing about papers or providers. References no other project; only package is `Microsoft.Extensions.AI.Abstractions`. |
| `ResearchScout.Llm` | Provider plumbing: Claude, Gemini, DeepSeek, OpenAI behind one `IChatClient`, switchable at runtime |
| `ResearchScout.Papers` | Paper fetching, PDF chunking, embeddings, hybrid search |
| `ResearchScout.Scout` | The agent's tools and the fit scorer |
| `ResearchScout.Infra` | EF Core + Postgres (traces, cache, daily spend) |
| `ResearchScout.Api` | ASP.NET Core API, SSE stream, web page |
| `ResearchScout.Cli` | `scout ask`, `scout trace`, `scout ingest` |
| `ResearchScout.Eval` | Citation accuracy + ranking checks |

## Run locally

Needs the .NET 10 SDK and Docker.

```bash
cp .env.example .env          # then fill in your API keys
docker compose up -d          # local Postgres + Qdrant
dotnet build ResearchScout.slnx
dotnet test ResearchScout.slnx
```
