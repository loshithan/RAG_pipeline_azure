# Rag-azure

A small Retrieval-Augmented Generation (RAG) console app in .NET 10. It indexes your text documents in **Azure AI Search** and answers questions about them with a chat model deployed in **Azure AI Foundry**. It also tracks token usage and cost for every call.

## How it works

```
                 ┌──────────────── ingest ────────────────┐
docs/*.txt ──► chunk (800 chars, 100 overlap) ──► embed ──► Azure AI Search index
                                                  (text-embedding-3-small)

                 ┌──────────────── ask ───────────────────┐
question ──► embed ──► hybrid search (keyword + vector, top 3) ──► prompt + context ──► chat model ──► answer + sources
```

1. **Ingest** reads every `.txt` file in `docs/`, splits it into overlapping chunks, creates embeddings, and uploads the chunks to the search index. The index is created automatically if it doesn't exist.
2. **Ask** embeds your question and runs a hybrid (keyword + vector) search. It sends the top 3 chunks to the chat model as context. The model is told to answer only from that context and to cite the source files.
3. **Usage tracking** records the tokens, duration and estimated cost of every embedding and chat call. A background task writes them to a log, so tracking never slows down your requests.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An **Azure AI Search** service. The Free tier is enough for a demo.
- An **Azure AI Foundry** resource with two model deployments:
  - an embedding model, e.g. `text-embedding-3-small` (1536 dimensions)
  - a chat model, e.g. `Phi-4-mini-reasoning`, DeepSeek or GPT-4o-mini

Both deployments must be in the **same Foundry resource**, because the app uses one endpoint and key for both.

## Setup

1. **Create your settings file** from the template:

   ```powershell
   Copy-Item appsettings.example.json appsettings.json
   ```

2. **Fill in `appsettings.json`:**

   | Setting | Where to find it |
   |---|---|
   | `Foundry:Endpoint` | Foundry portal → project overview → **Azure OpenAI endpoint**, with `/openai/v1` added at the end. Don't use the *project* endpoint. |
   | `Foundry:Key` | Foundry portal → project overview → **API key**. One key covers all deployments in the resource. |
   | `Foundry:ChatDeployment` | The deployment name of your chat model |
   | `Foundry:EmbeddingDeployment` | The deployment name of your embedding model |
   | `Foundry:EmbeddingDimensions` | `1536` for text-embedding-3-small, `3072` for text-embedding-3-large |
   | `Search:Endpoint` | `https://<service-name>.search.windows.net` |
   | `Search:Key` | Azure portal → Search service → **Keys** → admin key, or `az search admin-key show --service-name <name> -g <resource-group>` |
   | `Search:IndexName` | Any name. Ingest creates the index. |

   `appsettings.json` is in `.gitignore`. Never commit it.

3. **Add documents:** put `.txt` files in `docs/`. They are copied to the build output automatically.

## Usage

```powershell
dotnet run -- ingest    # build/update the index from docs/*.txt
dotnet run              # ask questions (blank line to quit)
dotnet run -- usage     # all-time token usage and cost report
```

> Note the space: it's `dotnet run -- ingest`, **not** `dotnet run --ingest`. Without the space, the argument never reaches the app, which then skips ingest and starts asking questions.

In Visual Studio, set `ingest` or `usage` under **Project → Properties → Debug → Command line arguments**.

Run `ingest` again whenever you add or change documents. Chunks are uploaded with stable IDs (`<file>-<n>`), so re-running updates the existing chunks instead of duplicating them. Chunks from deleted files, or left over when a file gets shorter, are **not** removed. Delete the index in the Azure portal to start fresh.

### Example

```
Question: how much is the learning budget?

The annual learning budget allocated to each employee is $1,000 ...

Sources: sample.txt
[tokens: embedding 7, chat in 437 / out 272 (reasoning 0) | cost $0.000122]
```

## Token usage & cost tracking

Every Azure call is recorded, and each run prints a summary when it exits:

```
===== Token usage: This session =====
Operation          Model                     Calls      Input     Output   Cost (USD)
chat               Phi-4-mini-reasoning          1        437        272     0.000122
query-embedding    text-embedding-3-small        1          7          0     0.000000
TOTAL                                            2        444        272     0.000122
```

### Log files

The log files are written to the folder the app runs from.

| File | Format | Purpose |
|---|---|---|
| `logs/usage-YYYY-MM-DD.txt` | Human-readable, one file per day | Timestamp, session ID, operation, model, tokens, duration, cost, sources, finish reason, response ID, plus the full **question and answer** |
| `usage-log.jsonl` | One JSON object per line | Machine-readable. Used by `dotnet run -- usage` |

Example text log entry:

```
2026-09-30 12:33:19.244 +05:30 | session 746b7526 | chat             | Phi-4-mini-reasoning   | in    437 | out    272 | reasoning     0 |    6832 ms | $0.000122 | sources=sample.txt hits=2 finish=Stop responseId=chatcmpl-...
    Q: how much is the learning budget?
    A: The annual learning budget allocated to each employee is $1,000 ...
```

Both log files contain users' questions and answers, and both are excluded from git.

### Pricing

Costs are **estimates**: the token counts reported by Azure multiplied by per-model prices. They don't include Azure AI Search charges. Your actual bill is in Azure Cost Management.

Built-in default prices (USD per 1M tokens, pay-as-you-go):

| Model | Input | Output |
|---|---|---|
| Phi-4-mini-reasoning | 0.08 | 0.32 |
| text-embedding-3-small | 0.02 | — |
| text-embedding-3-large | 0.13 | — |

To change a price or add a model, add a `Pricing` section to `appsettings.json`, keyed by **deployment name**:

```json
"Pricing": {
  "Phi-4-mini-reasoning": { "InputPer1M": 0.08, "OutputPer1M": 0.32 },
  "my-gpt-4o-mini":       { "InputPer1M": 0.15, "OutputPer1M": 0.60 }
}
```

A deployment with no price is logged with a warning and counted as $0.

## Project structure

```
Rag-azure/
├── Program.cs                 # config, clients, ingest, ask (search + chat)
├── UsageTracker.cs            # background token/cost tracker and log writer
├── Rag-azure.csproj           # packages; copies appsettings.json and docs/ to output
├── appsettings.example.json   # settings template (safe to commit)
├── appsettings.json           # your real settings (git-ignored)
└── docs/                      # .txt files to index
```

## Troubleshooting

| Error | Cause / fix |
|---|---|
| `The configuration file 'appsettings.json' was not found` | Create it from `appsettings.example.json`. It's copied to `bin/` on build. |
| `The index '...' was not found` (404) | You haven't ingested yet. Run `dotnet run -- ingest` (with the space). |
| `401 Unauthorized` from Foundry | Wrong key, or you used the project endpoint instead of the Azure OpenAI endpoint. |
| `DeploymentNotFound` / 404 from Foundry | `ChatDeployment` / `EmbeddingDeployment` must be the **deployment name**, not the model name. |
| Vector dimension mismatch on ingest | `EmbeddingDimensions` doesn't match the model. If you change it, delete the index and ingest again. |
| `file is locked by: Rag-azure` on build | The app is still running. Close it and build again. |
| Answer ends with `\boxed{...}` | Reasoning models such as Phi-4-mini-reasoning format answers this way. Adjust the system prompt or use a non-reasoning chat model. |

## Limitations

- Only `.txt` files are ingested (no PDF or Word parsing).
- Chunks are split by character count, not by sentence or token boundaries.
- All chunks are uploaded in one batch, which is fine for small demos. The Azure limit is 1000 documents per upload call.
- Azure AI Search Free tier: 3 indexes, 50 MB storage.
