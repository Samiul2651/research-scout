# Overview

## The problem

I'm applying to funded, thesis-based MSc programs in AI for software engineering. Two jobs took most of the time:

- **Reading papers.** Generic chatbots answer questions about papers, but they often invent citations, and you can't check them.
- **Finding supervisors.** Reading every professor's page and recent papers by hand is slow.

Research Scout does both, and every answer can be checked.

## What it does

### 1. Paper Q&A with page citations

- Give it an arXiv id (or a DOI or Semantic Scholar id). It downloads the PDF, splits it into chunks that each belong to exactly one page, and indexes them.
- Ask a question. The agent searches the paper and answers with tags like `[arxiv:2005.11401 p.3]`, each linking to the exact PDF page.
- If the answer isn't in the paper, it says "Not found in the ingested papers" instead of guessing.

### 2. Professor matching

- Ask "Which professors at Memorial University work on LLMs for code?"
- It looks up researchers on Semantic Scholar and scores how well their last 3 years of papers fit my research profile ([`profile.md`](../profile.md)).
- It can draft a short outreach email that only cites papers the professor really wrote. Drafts are saved only after a human approves them, and nothing is ever sent.

## What makes it interesting technically

- **Own agent harness.** No agent framework, and no `FunctionInvokingChatClient`. The loop, tool dispatch, approvals, budgets, retries and tracing are all in about 300 lines in `ResearchScout.Harness`. See [Agent harness](03-agent-harness.md).
- **Switchable models.** Claude, Gemini, DeepSeek and OpenAI sit behind one `IChatClient`. An admin picks the model on the web page at runtime.
- **Hybrid retrieval.** BM25 keyword search plus vector search, merged with reciprocal rank fusion. See [Papers and retrieval](04-papers-and-retrieval.md).
- **Measured, not guessed.** An eval checks that each cited page really contains the answer. See [Evaluation](12-evaluation.md).
- **Safe public demo.** Per-run and daily spend caps, a per-visitor rate limit, and a limit on runs at once.

## Who it's for

- Me, as a working tool for grad school applications.
- Reviewers and hiring managers, as a live, checkable demo of LLM-agent and RAG engineering in C#.

## Current status (Nov 2026)

| Area | Status |
| --- | --- |
| Harness, tools, paper pipeline, API, web page, CLI | Done |
| Eval: retrieval, citations, refusal | Done, results committed |
| Eval: professor ranking | Waiting for names in `eval/professors.json` |
| Live deployment (Render + Supabase + Qdrant Cloud) | Live |
| Professor matching on the live site | Works on Semantic Scholar's free keyless pool; can be slow when it's busy (see [Troubleshooting](13-troubleshooting.md#semantic-scholar-429-too-many-requests)) |

## Tech stack

C# / .NET 10 · ASP.NET Core minimal API · Microsoft.Extensions.AI · Anthropic, Google GenAI and OpenAI SDKs · EF Core + PostgreSQL (Supabase) · Qdrant · Voyage AI embeddings · PdfPig · xUnit · Docker · GitHub Actions · Render.
