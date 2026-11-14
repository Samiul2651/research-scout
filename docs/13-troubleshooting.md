# Troubleshooting

Known errors and their fixes. Most of these came up during the first deployment (Nov 2026).

**First step for any failed run:** find its id with `scout runs` (or the "trace" link on the web page), then read `scout trace <runId>` or `GET /runs/{runId}/trace`. The **Status** line says why it stopped.

| Symptom | Cause | Fix |
| --- | --- | --- |
| [Voyage returned 429](#voyage-returned-429) | Voyage free-tier rate limit | `VOYAGE_FREE_TIER=true`, or add a payment method |
| [ModelError: AuthenticationError](#modelerror-authentication-error) | Wrong or truncated LLM key | Create a new key, paste it with the copy button |
| [Could not download the PDF](#could-not-download-the-pdf) | Publisher link blocked (403) or TLS failed | Fixed: arXiv PDFs are now used first |
| [Semantic Scholar 429](#semantic-scholar-429-too-many-requests) | No Semantic Scholar key | Get a free key |
| [Every step costs $0.00000](#every-step-costs-000000) | Streaming dropped the model stamp | Fixed in Nov 2026 |
| [Answers have no citations](#answers-have-no-citations) | Paper not loaded | Run ingest again |
| [Database connection fails on Render](#database-connection-fails-on-render) | Direct (IPv6) Supabase URL | Use the Session pooler string |
| [First request takes about a minute](#first-request-takes-about-a-minute) | Render free tier sleeps | Normal |
| [Daily demo limit reached](#daily-demo-limit-reached) | Daily cap hit | Wait for the next UTC day, or raise the cap |

---

## Voyage returned 429

```
Voyage returned 429: {"detail":"You have not yet added your payment method ... reduced rate limits of 3 RPM and 10K TPM ..."}
```

Voyage accounts without a payment method allow 3 requests and 10,000 tokens per minute. Normal batches are far bigger.

**Fix:** set `VOYAGE_FREE_TIER=true` (already on in `render.yaml`). The client then sends small batches, paces itself, and prints "Voyage free tier: waiting Ns…". If you don't see those lines, the setting isn't loaded: check that it's still in your `.env`.

**Faster fix:** add a payment method on dashboard.voyageai.com. The first 200M tokens are still free, and the limit goes away. Then set `VOYAGE_FREE_TIER=false`.

## ModelError: authentication error

```
Status: ModelError — Model call failed at step 1: AnthropicUnauthorizedException ...
"Authentication Fails, Your api key: ****d373 is invalid"
```

The LLM provider rejected the key. (DeepSeek goes through the Anthropic SDK, which is why the exception says "Anthropic".)

**Fix:**

- Create a new key and copy it with the provider's copy button. A character lost while pasting is the most common cause. DeepSeek keys start with `sk-`.
- Check the account has credit. DeepSeek has no free tier.
- A run that cost $0.00 with 0 tokens means no model call succeeded.

## Could not download the PDF

```
✗ arXiv:2307.03172: Could not download the PDF ...: Response status code does not indicate success: 403 (Forbidden).
✗ arXiv:2002.08155: Could not download the PDF ...: The SSL connection could not be established
```

Semantic Scholar's "open access" link pointed to a publisher site that blocks scripts or has TLS problems.

**Fixed (Nov 2026):** if a paper is on arXiv, the arXiv PDF is used first. Papers that aren't on arXiv still use the open-access link, and some publishers will keep blocking it.

## Semantic Scholar 429 (Too Many Requests)

```
✗ find_professors: Tool 'find_professors' failed: Semantic Scholar returned 429: {"message": "Too Many Requests ..."}
```

Without a key, all users share one public rate limit. Professor matching makes many calls per question.

**Fix:** request a free key at semanticscholar.org/product/api (approval can take a few days). Set `SEMANTIC_SCHOLAR_API_KEY` on Render and in `.env`.

Even with a key, `find_professors` can come back empty, because Semantic Scholar has affiliations for only some authors. Give it names from the faculty page.

## Every step costs $0.00000

The web page showed `$0.00000` for every step, and the daily budget never moved, while the eval showed real costs.

**Cause:** in streaming mode, `ToChatResponse()` didn't copy the "which model answered" stamp onto the joined response, so the cost calculator found no price. The eval doesn't stream, so it wasn't affected.

**Fixed (Nov 2026)** in `AgentLoop`. Covered by the test `Streaming_KeepsStampsFromUpdates_SoCostIsCounted`. If it ever comes back, the daily spend cap stops protecting the demo, so treat it as urgent.

## Answers have no citations

If questions about one paper never get citations, that paper probably isn't loaded. Check `GET /papers` (or the "Papers already ingested" list on the page), then run:

```bash
dotnet run --project src/ResearchScout.Eval -- ingest
```

Each paper prints ✓ or ✗ with a reason. Already-loaded papers are skipped.

## Database connection fails on Render

Render's free tier can't reach Supabase's direct connection address, which is IPv6-only. Use the **Session pooler** string: it contains `pooler.supabase.com:5432` and a username like `postgres.<project-id>`. See [Deployment](11-deployment.md#1-supabase-postgres).

## First request takes about a minute

The free Render instance sleeps after 15 quiet minutes. The first request wakes it up, and that takes about a minute. On startup it also rebuilds the keyword index from Postgres. A keep-alive ping is on the [roadmap](15-roadmap.md), but it uses free instance hours.

## Daily demo limit reached

```
Daily demo limit reached ($0.50 of $0.50). Please try again tomorrow (UTC).
```

The spend cap is working as intended. Wait for the next UTC day, or raise `DAILY_SPEND_CAP_USD` on Render.

## Secrets in stray files

`.env` is git-ignored, but copies like `.env1.txt` or `.env.backup` are **not**. Don't keep copies of `.env` in the repo folder, and check `git status` before `git add .`. If a key was ever pasted somewhere public (or into a chat), delete it at the provider and create a new one.
