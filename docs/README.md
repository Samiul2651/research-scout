# Research Scout documentation

Research Scout is a C#/.NET research assistant built on its own agent harness. It answers questions about research papers with page-level citations, and it finds professors whose recent work fits a research profile.

**Live demo:** https://research-scout-n8fg.onrender.com
The first request after 15 quiet minutes takes about a minute (free hosting).

## Start here

| If you want to… | Read |
| --- | --- |
| Understand what the project does and why | [Overview](01-overview.md) |
| See how the parts fit together | [Architecture](02-architecture.md) |
| Run it on your own computer | [Development](14-development.md) |
| Deploy your own copy | [Deployment](11-deployment.md) |
| Fix something that broke | [Troubleshooting](13-troubleshooting.md) |

## All pages

1. [Overview](01-overview.md): what it does, who it's for, current status
2. [Architecture](02-architecture.md): projects, data flow, the life of one question
3. [Agent harness](03-agent-harness.md): the agent loop, approvals, budgets, retries, tracing, model switching
4. [Papers and retrieval](04-papers-and-retrieval.md): ingest pipeline, chunking, embeddings, hybrid search, citations
5. [Professor matching](05-professor-matching.md): finding researchers, the fit score, email drafts
6. [Tools reference](06-tools-reference.md): the six tools the agent can call
7. [API reference](07-api-reference.md): HTTP endpoints and streamed events
8. [CLI reference](08-cli-reference.md): `scout` and `eval` commands
9. [Configuration](09-configuration.md): environment variables, model list, research profile
10. [Database](10-database.md): Postgres tables and the Qdrant collection
11. [Deployment](11-deployment.md): Supabase, Qdrant Cloud, Voyage, Render, CI/CD
12. [Evaluation](12-evaluation.md): how quality is measured, and the current results
13. [Troubleshooting](13-troubleshooting.md): known errors and their fixes
14. [Development](14-development.md): local setup, tests, code rules
15. [Roadmap](15-roadmap.md): what's next

## Key numbers (Nov 2026)

| Measure | Result |
| --- | --- |
| Retrieval recall@5 | 93% (13 of 14 questions) |
| Questions with a correct page citation | 93% (13 of 14) |
| Invented citations | 0 |
| Correct "not found" on an unanswerable question | 100% |
| Cost of the 15-question eval | $0.05 (DeepSeek V4.1 Flash) |

Details: [Evaluation](12-evaluation.md).
