# Configuration

Secrets and per-environment values come from **environment variables**. Everything else is in `appsettings.json`.

- **Locally:** put the variables in a `.env` file at the repo root. It's git-ignored. Copy `.env.example` to start. The CLI and eval look for `.env` in the current folder and its parents. Values already set in your shell win over `.env`.
- **On Render:** set them in the service's **Environment** tab. `render.yaml` declares them.

## Environment variables

### Required

| Variable | Example | What it's for |
| --- | --- | --- |
| `DATABASE_URL` | `postgresql://postgres.abcd:PASS@aws-1-....pooler.supabase.com:5432/postgres` | Postgres. On Supabase use the **Session pooler** string (IPv4) |
| `VOYAGE_API_KEY` | `pa-...` | Embeddings for papers, questions and the fit score |
| At least one LLM key | see below | The agent's model |

### LLM providers (set the ones you have)

| Variable | Provider |
| --- | --- |
| `ANTHROPIC_API_KEY` | Claude |
| `GEMINI_API_KEY` | Gemini |
| `DEEPSEEK_API_KEY` | DeepSeek (starts with `sk-`) |
| `OPENAI_API_KEY` | OpenAI |

The admin panel only offers providers that have a key.

### Vectors

| Variable | Example | Notes |
| --- | --- | --- |
| `QDRANT_URL` | `https://xxxx.cloud.qdrant.io:6333` | Paste the Qdrant Cloud URL as shown. The client uses gRPC, so port 6333 is switched to 6334 by itself. Local: `http://localhost:6334` |
| `QDRANT_API_KEY` | | Qdrant Cloud only |

If `QDRANT_URL` is empty, vectors are kept **in memory** and lost on restart. That's fine for a quick try only.

### Optional

| Variable | Default | What it does |
| --- | --- | --- |
| `SEMANTIC_SCHOLAR_API_KEY` | none | Raises Semantic Scholar's rate limit. **Needed in practice for professor matching.** Free at semanticscholar.org/product/api |
| `VOYAGE_FREE_TIER` | `false` (`true` in `render.yaml`) | Stay under Voyage's limits for accounts without a payment method (3 requests and 10K tokens per minute). See [Papers and retrieval](04-papers-and-retrieval.md#voyage-free-tier) |

### Live demo (API only)

| Variable | Default | What it does |
| --- | --- | --- |
| `ADMIN_PASSWORD` | none (admin disabled) | Password for the model switcher. Render generates one |
| `DAILY_SPEND_CAP_USD` | `0.50` | Stop answering for the rest of the UTC day once reached |
| `MAX_CONCURRENT_RUNS` | `3` | Runs at the same time (the free host has 512 MB RAM) |
| `ALLOWED_ORIGINS` | none | Comma-separated websites allowed to call the API from a browser, e.g. `https://samiul2651.github.io`. The built-in page needs nothing here |
| `PORT` | `8080` | Set by Render; the app listens on it |

## `appsettings.json`

In `src/ResearchScout.Api/appsettings.json`. The CLI and eval use the same file.

### `Llm`: models and prices

```jsonc
"Llm": {
  "DefaultProvider": "anthropic",
  "DefaultModelId": "claude-haiku-5-5",
  "RequestTimeout": "00:02:00",
  "Models": [
    { "Provider": "anthropic", "ModelId": "claude-haiku-5-5",      "DisplayName": "Claude Haiku 5.5",      "InputUsdPerMillion": 0.10, "OutputUsdPerMillion": 0.50 },
    { "Provider": "gemini",    "ModelId": "gemini-3.5-flash-lite", "DisplayName": "Gemini 3.5 Flash-Lite", "InputUsdPerMillion": 0.30, "OutputUsdPerMillion": 2.50 },
    { "Provider": "deepseek",  "ModelId": "deepseek-flash",        "DisplayName": "DeepSeek V4.1 Flash",   "InputUsdPerMillion": 0.30, "OutputUsdPerMillion": 1.20 }
  ]
}
```

- **Prices drive the budgets.** Cost per response = input tokens × input price ÷ 1,000,000 + output tokens × output price ÷ 1,000,000. Update them when providers change prices.
- If the default model's provider has no key, the first available model is used.
- To add a model, add an entry. It appears in the admin panel as soon as its provider has a key.

### `Embeddings`

| Key | Default |
| --- | --- |
| `Embeddings:Model` | `voyage-4-lite` |
| `Embeddings:Dimensions` | `1024` |

The Qdrant collection's vector size must match `Dimensions`.

### `ProfilePath`

Path to the research profile used by `score_fit`. Default: `profile.md` next to the app. See [Professor matching](05-professor-matching.md#the-research-profile).
