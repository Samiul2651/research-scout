# Roadmap

## Open items for v1

| Item | Why |
| --- | --- |
| Add `SEMANTIC_SCHOLAR_API_KEY` on Render | Professor matching hits Semantic Scholar's shared rate limit without it |
| Fill `eval/professors.json` and run the ranking eval | Gives the "good-fit professors in top 5" number |
| Check the evidence phrases in `eval/questions.json` against the PDFs | Some "wrong" citations may be answer-key mistakes |
| Demo GIF in the README | Shows a question, streamed citations and an approval in a few seconds |
| Link the demo from the portfolio site | |

## Small improvements

- **Fewer extra citations.** Tighten the system prompt so the model cites only pages that hold the claim. Target: citation accuracy above 85% without losing the 93% correct-citation rate.
- **Parallel tool calls** in one step, when the model asks for several independent tools.
- **Keep-alive ping** every 14 minutes, only if cold starts hurt the demo (it uses free instance hours).
- **Voyage payment method,** to remove the free-tier waits (the first 200M tokens stay free).

## v2: local engine, Claude through MCP

The idea (planned, not started): keep all the engineering in this C# code, but run the personal parts on my own computer and use them from the Claude desktop app through a local MCP server, the same pattern as my Job Tracker project. Keep only the public demo hosted.

| Part | Where |
| --- | --- |
| Paper loading, search, professor matching, email drafts, eval | My computer |
| New `ResearchScout.Mcp` project: a thin MCP layer over the existing tools | My computer, started by Claude Desktop |
| Public paper Q&A chatbot (own harness + daily cap) | Render |
| Postgres and Qdrant | Cloud (shared, so papers loaded locally appear in the demo) |

Open questions for v2:

- In local use, does Claude in chat do the reasoning (free; tools return search results with pages), or does an `ask` tool run the own harness?
- Should Claude keep a professor outreach tracker (status per professor: to contact, emailed, replied), updated through MCP?
- Should the hosted demo stay paper Q&A only, with professor matching private?
