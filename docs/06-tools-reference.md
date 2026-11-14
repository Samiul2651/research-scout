# Tools reference

These are the six tools the agent can call. They live in `src/ResearchScout.Scout/Tools`. Each one implements the harness's `ITool` interface, and the model sees only its name, description and input schema.

| Tool | What it does | Needs approval |
| --- | --- | --- |
| [`ingest_paper`](#ingest_paper) | Downloads, chunks and indexes a paper | No |
| [`search_papers`](#search_papers) | Searches ingested papers; returns cite tags | No |
| [`find_professors`](#find_professors) | Finds researchers at a university on a topic | No |
| [`get_recent_papers`](#get_recent_papers) | Lists one author's recent papers | No |
| [`score_fit`](#score_fit) | Scores researchers 0–100 against the profile | No |
| [`draft_email`](#draft_email) | Saves an outreach email draft | **Yes** |

Errors are returned to the model as `Error: ...`, never thrown, so the model can try something else.

---

## `ingest_paper`

Downloads a paper's PDF (arXiv or open access), splits it into page-tagged chunks and indexes it for `search_papers`. Safe to call again for an already ingested paper.

| Input | Type | Required | Notes |
| --- | --- | --- | --- |
| `paperId` | string | yes | `arXiv:2106.15928`, `DOI:10.1145/...`, or a Semantic Scholar paper id |

Returns the paper id to use with `search_papers` (for example `arxiv:2106.15928`), the title, pages and chunk count. Details: [Papers and retrieval](04-papers-and-retrieval.md).

## `search_papers`

Searches the text of ingested papers with hybrid keyword + meaning search.

| Input | Type | Required | Notes |
| --- | --- | --- | --- |
| `query` | string | yes | What to look for, in plain words |
| `paperIds` | string[] | no | Only search these papers, e.g. `["arxiv:2106.15928"]` |
| `limit` | integer | no | 1–10 results (default 6) |

Each result has the paper id, title, page, text, and a `cite` tag like `[arxiv:2106.15928 p.4]` to put after any claim that uses it.

## `find_professors`

Finds researchers at a university who publish on a topic, using Semantic Scholar. Returns author ids, names, affiliations and h-index.

| Input | Type | Required | Notes |
| --- | --- | --- | --- |
| `university` | string | yes | As it appears in affiliations, e.g. `Memorial University of Newfoundland` |
| `topic` | string | yes | e.g. `large language models for code` |
| `names` | string[] | no | Professor names (e.g. from a faculty page) to look up directly |

Affiliation data is incomplete, so an empty result does not mean nobody works on the topic.

## `get_recent_papers`

Lists one author's papers from the last few years, newest first, with venue, abstract and whether a free PDF exists.

| Input | Type | Required | Notes |
| --- | --- | --- | --- |
| `authorId` | string | yes | Semantic Scholar author id from `find_professors` |
| `years` | integer | no | 1–10 years back (default 3) |
| `limit` | integer | no | 1–30 papers (default 10) |

Use a returned `paperId` with `ingest_paper` to read the paper.

## `score_fit`

Scores up to 10 researchers (0–100) by how well their last 3 years of papers match the research profile, favouring recent work. Returns each score with the best-matching papers as evidence.

| Input | Type | Required | Notes |
| --- | --- | --- | --- |
| `authorIds` | string[] | yes | Semantic Scholar author ids from `find_professors` (max 10) |

The formula is in [Professor matching](05-professor-matching.md#the-fit-score).

## `draft_email`

Saves a short outreach email draft to a professor. **The user must approve it first, and it is never sent.**

| Input | Type | Required | Notes |
| --- | --- | --- | --- |
| `authorId` | string | yes | Semantic Scholar author id |
| `professorName` | string | yes | |
| `subject` | string | yes | |
| `body` | string | yes | Under 200 words |
| `citedPaperIds` | string[] | yes | 1–2 paper ids (from `get_recent_papers`) mentioned in the email |

Each cited paper is checked against the professor's actual papers before saving.

---

## Adding a tool

1. Create a class in `src/ResearchScout.Scout/Tools` that extends `ScoutTool` (or implements `ITool`).
2. Give it a clear `Name`, a `Description` the model can act on, and a JSON `InputSchema`.
3. Return `ToolResult.Ok(...)` or `ToolResult.Error(...)`. Don't throw for expected problems.
4. Set `NeedsApproval => true` if it changes anything a human should check first.
5. Register it in `ScoutServices.CreateTools` (add one `.Add(new YourTool(...))` line).
6. Add a test in `tests/ResearchScout.Scout.Tests`.
