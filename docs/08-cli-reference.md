# CLI reference

There are two command-line tools. Both read settings from environment variables, or from a `.env` file in the current folder or any parent folder (see [Configuration](09-configuration.md)).

Run them from the repo root with `dotnet run --project <project> -- <command>`.

## `scout` (`src/ResearchScout.Cli`)

```bash
dotnet run --project src/ResearchScout.Cli -- <command>
```

| Command | What it does |
| --- | --- |
| `ask "question" [--model p/m]` | Runs the full Research Scout agent once and prints the answer |
| `ingest <paperId>` | Loads one paper, e.g. `ingest arXiv:2005.11401` |
| `search "query"` | Hybrid search over ingested papers (no LLM, free) |
| `chat "question" [options]` | Runs the bare harness with two demo tools, a clock and a calculator (for testing the loop and models) |
| `models` | Lists models that have an API key; `*` marks the default |
| `runs` | Lists recent runs with status and cost |
| `trace <runId>` | Prints one run step by step: tokens, cost, tool inputs and outputs |
| `db migrate` | Creates or updates the database tables |

`chat` options:

| Option | Default |
| --- | --- |
| `--model provider/modelId` | the current default |
| `--max-steps N` | 12 |
| `--max-cost USD` | 0.05 |

Examples:

```bash
# Load a paper, then ask about it
dotnet run --project src/ResearchScout.Cli -- ingest arXiv:2005.11401
dotnet run --project src/ResearchScout.Cli -- ask "How does the RAG paper evaluate retrieval?"

# Check what happened in a run
dotnet run --project src/ResearchScout.Cli -- runs
dotnet run --project src/ResearchScout.Cli -- trace 7bb6d20d-6857-4f61-a320-7b05413c00d0
```

`ask` uses a console approval gate: when the agent wants to save an email draft, you're asked to approve it in the terminal.

## `eval` (`src/ResearchScout.Eval`)

```bash
dotnet run --project src/ResearchScout.Eval -- <command> [--model provider/modelId] [--eval-dir path]
```

| Command | What it does | Cost |
| --- | --- | --- |
| `ingest` | Loads the papers in `eval/papers.json` | Embeddings only |
| `retrieval` | Retrieval recall@5 over `eval/questions.json` | Free (no LLM) |
| `citations` | Runs the agent on every question; scores citation accuracy and refusal | A few cents |
| `ranking` | Checks known good-fit professors land in the top 5 (`eval/professors.json`) | A few cents |
| `all` | All of the above | |

Results are saved as JSON in `eval/results/`, named by date and command. Details: [Evaluation](12-evaluation.md).
