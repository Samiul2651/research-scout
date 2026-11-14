# Development

## What you need

- .NET 10 SDK (`dotnet --version` should start with `10.`; the exact version is pinned in `global.json`)
- Docker, for local Postgres and Qdrant
- A Voyage key and at least one LLM key

## Run it locally

```bash
git clone https://github.com/Samiul2651/research-scout
cd research-scout

# 1. Local Postgres + Qdrant
docker compose up -d

# 2. Settings
cp .env.example .env      # then add VOYAGE_API_KEY and an LLM key
# The local DATABASE_URL and QDRANT_URL in .env.example already match docker-compose.yml

# 3. Load the demo papers
dotnet run --project src/ResearchScout.Eval -- ingest

# 4. Ask from the terminal
dotnet run --project src/ResearchScout.Cli -- ask "Which retriever does the RAG paper use?"

# 5. Or run the web app at http://localhost:5000 (see the console for the exact port)
dotnet run --project src/ResearchScout.Api
```

Stop the stores with `docker compose down` (add `-v` to also delete the data).

You can also point a local `.env` at the production Supabase and Qdrant, which is how the demo papers were loaded. Be careful: anything you ingest then appears on the live site.

## Tests

```bash
dotnet test ResearchScout.slnx
```

| Tests | Run when |
| --- | --- |
| Unit tests (harness, LLM, papers, scout) | Always |
| Postgres and API tests | `TEST_DATABASE_URL` is set, e.g. `postgresql://scout:scout_dev_only@localhost:5432/postgres` |
| Qdrant test | `TEST_QDRANT_URL` is set, e.g. `http://localhost:6334` (gRPC) |

CI runs everything against real Postgres and Qdrant services, then builds the Docker image.

| Test project | What it covers |
| --- | --- |
| `ResearchScout.Harness.Tests` | Loop, tool errors, approvals, budgets, retries, streaming. Uses a scripted fake model (`ScriptedChatClient`) |
| `ResearchScout.Llm.Tests` | Model catalog, switching, prices, error classifier |
| `ResearchScout.Infra.Tests` | Traces, daily spend (including parallel updates), settings |
| `ResearchScout.Papers.Tests` | Semantic Scholar client, cache, PDF links, chunker, BM25, hybrid search, ingest, Qdrant |
| `ResearchScout.Scout.Tests` | Tools, fit scorer, eval scoring |
| `ResearchScout.Api.Tests` | Endpoints, SSE, approvals, admin auth |

## Code rules

Set in `Directory.Build.props` and `.editorconfig`:

- **Warnings are errors** (`TreatWarningsAsErrors`), with `AnalysisLevel` set to `latest-recommended` and code style enforced in the build. A missing `ConfigureAwait(false)`, a culture-sensitive `ToString()` or an undisposed field fails the build.
- Nullable reference types on.
- **The harness stays provider-free.** `ResearchScout.Harness` may only reference `Microsoft.Extensions.AI.Abstractions`. A test fails the build if it gains another package.
- Tools return errors (`ToolResult.Error`) instead of throwing for expected problems.
- Comments and docs use plain, short sentences.

## Project layout

```
research-scout/
├── src/
│   ├── ResearchScout.Harness/   agent loop, tools, approvals, budgets, retries
│   ├── ResearchScout.Llm/       providers, switching, prices
│   ├── ResearchScout.Infra/     EF Core, migrations, traces, spend
│   ├── ResearchScout.Papers/    fetching, chunking, embeddings, search, ingest
│   ├── ResearchScout.Scout/     tools, fit scorer, prompts, eval scoring
│   ├── ResearchScout.Api/       minimal API + wwwroot web page
│   ├── ResearchScout.Cli/       scout command
│   └── ResearchScout.Eval/      eval command
├── tests/                       one test project per src project
├── eval/                        papers.json, questions.json, professors.json, results/
├── docs/                        this documentation
├── profile.md                   research profile for score_fit
├── PLAN.md                      original build plan and task list
├── Dockerfile, render.yaml      deployment
└── docker-compose.yml           local Postgres + Qdrant
```

## Making a change

1. Branch from `main`.
2. Make the change, with a test.
3. `dotnet build` (catches analyzer errors) and `dotnet test`.
4. Push. CI builds, tests, and builds the Docker image. Merging to `main` deploys to Render if `RENDER_DEPLOY_HOOK_URL` is set.
