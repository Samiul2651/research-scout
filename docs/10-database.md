# Database

## Postgres

EF Core with Npgsql and snake_case names (`EFCore.NamingConventions`). The model is in `src/ResearchScout.Infra/ScoutDbContext.cs`; migrations are in `src/ResearchScout.Infra/Migrations`.

**Tables are created automatically on startup** (API, CLI and eval). You can also run `scout db migrate`.

| Table | Key | What it holds |
| --- | --- | --- |
| `runs` | `id` (run id) | One row per agent run: question, provider, model, status, stop reason, final text, tokens, cost, start and finish times. Indexed by `started_at` |
| `steps` | `id`; unique (`run_id`, `number`) | One row per model call: model, tokens, cost, attempts, duration, text preview, and every tool call as `jsonb` |
| `app_settings` | `key` | Saved settings, such as the admin's chosen model |
| `daily_spend` | `day` (UTC date) | Total spend per day. Updated with one atomic upsert so parallel runs don't lose updates |
| `api_cache` | `key` | Cached Semantic Scholar responses (raw JSON, kept 7 days) |
| `papers` | `id` (`arxiv:...` or `s2:...`) | Ingested papers: title, authors, year, url, page count, ingest time |
| `chunks` | unique (`paper_id`, `index`) | Chunk text, page number and position. Source for the BM25 index |
| `email_drafts` | | Approved outreach drafts: author id, professor, subject, body, cited papers, run id. Indexed by `created_at` |

Money columns use `numeric(12,6)`.

### Useful queries

```sql
-- Today's spend
select * from daily_spend order by day desc limit 7;

-- Recent runs that failed
select id, started_at, status, stop_reason from runs
where status <> 'Completed' order by started_at desc limit 20;

-- Chunks per paper
select paper_id, count(*) as chunks, max(page) as pages from chunks group by paper_id;
```

## Qdrant

| Setting | Value |
| --- | --- |
| Collection | `chunks` |
| Vector size | 1024 (from `Embeddings:Dimensions`) |
| Distance | cosine |
| Point payload | `paper_id` and `page` (the text itself lives in Postgres) |
| Payload index | keyword index on `paper_id`, so a search can be limited to some papers |

The collection is created on first use. If Qdrant isn't configured, an in-memory store is used instead (lost on restart).

## What happens on a restart

The free Render instance has no persistent disk. On startup the app:

1. Creates or updates the Postgres tables.
2. Rebuilds the BM25 keyword index from the `chunks` table.
3. Connects to Qdrant (vectors are already there).

Nothing is lost, because all state lives in Postgres and Qdrant.
