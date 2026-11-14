# Evaluation

The eval checks two things: does search find the right page, and does the agent cite pages that really hold the answer. It lives in `src/ResearchScout.Eval`, with the scoring rules in `src/ResearchScout.Scout/Evaluation/EvalScoring.cs`.

## The test set

- **5 papers** (`eval/papers.json`): RAG (2005.11401), CodeSearchNet (1909.09436), CodeBERT (2002.08155), Lost in the Middle (2307.03172), RepoCoder (2303.12570).
- **15 hand-written questions** (`eval/questions.json`): 14 answerable, and 1 trick question that the papers can't answer ("According to the CodeBERT paper, what is the population of Dhaka?").
- Each answerable question has **evidence phrases**: words that must appear on the page that answers it. Example: "Which retriever does the RAG paper use?" → evidence `DPR`.

## How each measure is worked out

| Measure | How it's worked out |
| --- | --- |
| **Retrieval recall@5** | Answerable questions where at least one of the top 5 search results is on a page containing **every** evidence phrase ÷ answerable questions. No LLM, free |
| **Citation accuracy** | Correct citations ÷ all citations, over answerable questions. A citation is correct if the cited page contains every evidence phrase |
| **Questions with a correct citation** | Answerable questions with at least one correct citation ÷ answerable questions |
| **Invented citations** | Citation tags pointing to a page that doesn't exist in the ingested papers |
| **Refusal** | Unanswerable questions where the answer says "Not found" |
| **Ranking (top 5)** | Known good-fit professors who land in the top 5 of `score_fit` ÷ known good-fit professors |

Evidence matching ignores case and differences in spacing.

## Results (14 Nov 2026)

Model: DeepSeek V4.1 Flash (`deepseek/deepseek-flash`). Raw results: `eval/results/20261114-123958-retrieval.json` and `eval/results/20261114-124756-citations.json`.

| Measure | Result | Worked out as |
| --- | --- | --- |
| Retrieval recall@5 | **93%** | 13 ÷ 14 answerable questions |
| Questions with a correct citation | **93%** | 13 ÷ 14 answerable questions |
| Citation accuracy | **70%** | 31 correct ÷ 44 citations (the trick question isn't counted) |
| Invented citations | **0** | |
| Refusal | **100%** | 1 ÷ 1 unanswerable question |
| Cost | **$0.05** | 15 questions |

Per question:

| Question | Citations | Correct |
| --- | --- | --- |
| rag-1 (model variants) | 3 | 3 |
| rag-2 (retriever) | 3 | 3 |
| rag-3 (generator) | 2 | 2 |
| rag-4 (QA datasets) | 4 | 3 |
| csn-1 (languages) | 2 | 2 |
| csn-2 (challenge metric) | 2 | 2 |
| csn-3 (test-set metric) | 5 | 1 |
| cb-1 (pre-training objectives) | 3 | 2 |
| cb-2 (base architecture) | 1 | 1 |
| cb-3 (training data) | 3 | 0 |
| lim-1 (best positions) | 6 | 5 |
| lim-2 (tasks) | 3 | 1 |
| rc-1 (method) | 4 | 4 |
| rc-2 (benchmark) | 3 | 2 |
| none-1 (trick question) | 4 | refused correctly |

### What the numbers say

- **Search works well.** 13 of 14 questions find the right page in the top 5.
- **The agent nearly always cites a right page** (13 of 14), and it never invents a page.
- **Citation accuracy is lower (70%)** because the model often adds one or two extra, loosely related citations next to the right one. csn-3 (1 of 5) and cb-3 (0 of 3) are the weakest.
- Some "wrong" citations may be **answer-key problems**: the evidence phrases are starter facts and haven't all been checked against the PDFs page by page. Fixing them could raise the number.

## Running the eval

```bash
dotnet run --project src/ResearchScout.Eval -- ingest      # load the 5 papers (embeddings only)
dotnet run --project src/ResearchScout.Eval -- retrieval   # recall@5, no LLM, free
dotnet run --project src/ResearchScout.Eval -- citations   # 15 agent runs, a few cents
dotnet run --project src/ResearchScout.Eval -- ranking     # needs names in eval/professors.json
```

Use `--model provider/modelId` to test another model, e.g. `--model gemini/gemini-3.5-flash-lite`.

## Ranking eval (not run yet)

`eval/professors.json` needs, for each case:

- `candidates`: the professors to rank (for example the whole CS faculty list of one university).
- `knownGoodFit`: the ones you already know fit you.

The eval then checks how many of `knownGoodFit` land in the top 5.

## Adding questions

Add an entry to `eval/questions.json`:

```json
{ "id": "rag-5", "question": "In the RAG paper (arXiv:2005.11401), ...", "paperId": "arxiv:2005.11401", "evidence": ["exact words from the answer page"] }
```

For a trick question, use `"unanswerable": true` and no evidence. Check that the evidence phrases appear on the right page of the PDF before trusting the numbers.
