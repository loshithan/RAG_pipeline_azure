using System.ClientModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

// ---------- Config ----------
var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

string foundryEndpoint     = config["Foundry:Endpoint"]!;
string foundryKey          = config["Foundry:Key"]!;
string chatDeployment      = config["Foundry:ChatDeployment"]!;       // e.g. your DeepSeek deployment name
string embeddingDeployment = config["Foundry:EmbeddingDeployment"]!;  // e.g. text-embedding-3-small
int    embeddingDims       = int.Parse(config["Foundry:EmbeddingDimensions"] ?? "1536");

string searchEndpoint = config["Search:Endpoint"]!;
string searchKey      = config["Search:Key"]!;
string indexName      = config["Search:IndexName"] ?? "rag-demo";

// ---------- Clients ----------
var openAi = new OpenAIClient(
    new ApiKeyCredential(foundryKey),
    new OpenAIClientOptions { Endpoint = new Uri(foundryEndpoint) });

EmbeddingClient embeddingClient = openAi.GetEmbeddingClient(embeddingDeployment);
ChatClient chatClient           = openAi.GetChatClient(chatDeployment);

var searchCredential = new AzureKeyCredential(searchKey);
var indexClient  = new SearchIndexClient(new Uri(searchEndpoint), searchCredential);
var searchClient = indexClient.GetSearchClient(indexName);

// ---------- Entry point ----------
// dotnet run -- ingest   -> build index and upload docs from ./docs
// dotnet run -- usage    -> show all-time token usage and cost
// dotnet run             -> ask questions
if (args.Length > 0 && args[0].Equals("usage", StringComparison.OrdinalIgnoreCase))
{
    UsageTracker.PrintAllTime();
    return;
}

// Runs in the background; prints this session's totals when disposed at exit
bool isIngest = args.Length > 0 && args[0].Equals("ingest", StringComparison.OrdinalIgnoreCase);
await using var usage = new UsageTracker(config, isIngest ? "ingest" : "ask");

if (isIngest)
{
    await Ingest();
    return;
}

Console.WriteLine("RAG demo ready. Type a question (blank line to quit).");
while (true)
{
    Console.Write("\nQuestion: ");
    var question = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(question)) break;

    Console.WriteLine("\n" + await Ask(question));
}

// ---------- Ingestion ----------
async Task Ingest()
{
    Console.WriteLine("Creating/updating index...");
    await indexClient.CreateOrUpdateIndexAsync(BuildIndex());

    var docs = new List<SearchDocument>();

    foreach (var file in Directory.GetFiles("docs", "*.txt"))
    {
        var text   = await File.ReadAllTextAsync(file);
        var chunks = Chunk(text, size: 800, overlap: 100);
        Console.WriteLine($"{Path.GetFileName(file)}: {chunks.Count} chunks");

        var sw         = Stopwatch.StartNew();
        var embeddings = (await embeddingClient.GenerateEmbeddingsAsync(chunks)).Value;
        usage.Record("ingest-embedding", embeddingDeployment, embeddings.Usage.InputTokenCount,
            durationMs: sw.ElapsedMilliseconds,
            details: $"file={Path.GetFileName(file)} chars={text.Length} chunks={chunks.Count}");
        var safeName   = Regex.Replace(Path.GetFileNameWithoutExtension(file), "[^a-zA-Z0-9_-]", "_");

        for (int i = 0; i < chunks.Count; i++)
        {
            docs.Add(new SearchDocument
            {
                ["id"]            = $"{safeName}-{i}",
                ["content"]       = chunks[i],
                ["source"]        = Path.GetFileName(file),
                ["contentVector"] = embeddings[i].ToFloats().ToArray()
            });
        }
    }

    // Fine for a small demo (limit is 1000 docs per upload call)
    await searchClient.UploadDocumentsAsync(docs);
    Console.WriteLine($"Uploaded {docs.Count} chunks. Now run: dotnet run");
}

SearchIndex BuildIndex() => new(indexName)
{
    Fields =
    {
        new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
        new SearchableField("content"),
        new SimpleField("source", SearchFieldDataType.String) { IsFilterable = true },
        new VectorSearchField("contentVector", embeddingDims, "vector-profile")
    },
    VectorSearch = new VectorSearch
    {
        Profiles   = { new VectorSearchProfile("vector-profile", "hnsw-config") },
        Algorithms = { new HnswAlgorithmConfiguration("hnsw-config") }
    }
};

// Keeps log lines on one line and reasonably short
static string Shorten(string s, int max = 120)
{
    s = s.ReplaceLineEndings(" ").Replace("\"", "'");
    return s.Length <= max ? s : s[..max] + "...";
}

static List<string> Chunk(string text, int size, int overlap)
{
    var chunks = new List<string>();
    text = text.Replace("\r\n", "\n").Trim();
    for (int start = 0; start < text.Length; start += size - overlap)
    {
        int len = Math.Min(size, text.Length - start);
        chunks.Add(text.Substring(start, len));
        if (start + len >= text.Length) break;
    }
    return chunks;
}

// ---------- Retrieval + generation ----------
async Task<string> Ask(string question)
{
    // 1. Embed the question
    // (batch call because only the collection result reports token usage)
    var sw              = Stopwatch.StartNew();
    var queryEmbeddings = (await embeddingClient.GenerateEmbeddingsAsync([question])).Value;
    var queryEmbedding  = queryEmbeddings[0];
    var embedUsage      = usage.Record("query-embedding", embeddingDeployment, queryEmbeddings.Usage.InputTokenCount,
        durationMs: sw.ElapsedMilliseconds,
        details: $"question=\"{Shorten(question)}\"");

    // 2. Hybrid search: keyword (question text) + vector
    var options = new SearchOptions
    {
        Size = 3,
        Select = { "content", "source" },
        VectorSearch = new()
        {
            Queries =
            {
                new VectorizedQuery(queryEmbedding.ToFloats())
                {
                    KNearestNeighborsCount = 3,
                    Fields = { "contentVector" }
                }
            }
        }
    };

    var response = await searchClient.SearchAsync<SearchDocument>(question, options);

    var hits = new List<(string Source, string Content)>();
    await foreach (var r in response.Value.GetResultsAsync())
        hits.Add((r.Document["source"].ToString()!, r.Document["content"].ToString()!));

    if (hits.Count == 0) return "No relevant documents found.";

    // 3. Build the prompt
    var context = string.Join("\n\n---\n\n", hits.Select(h => $"[{h.Source}]\n{h.Content}"));

    ChatMessage[] messages =
    {
        new SystemChatMessage(
            "Answer using only the provided context. If the answer is not in the context, say you don't know. " +
            "Mention the source file name(s) you used."),
        new UserChatMessage($"Context:\n{context}\n\nQuestion: {question}")
    };

    // 4. Call the LLM
    sw.Restart();
    ChatCompletion completion = (await chatClient.CompleteChatAsync(messages)).Value;
    var chatMs = sw.ElapsedMilliseconds;
    var answer = completion.Content[0].Text;

    // DeepSeek reasoning models may include <think>...</think>; strip it
    answer = Regex.Replace(answer, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();

    var chatUsage = usage.Record("chat", chatDeployment,
        completion.Usage.InputTokenCount,
        completion.Usage.OutputTokenCount,
        completion.Usage.OutputTokenDetails?.ReasoningTokenCount ?? 0,
        durationMs: chatMs,
        details: $"sources={string.Join(",", hits.Select(h => h.Source).Distinct())} " +
                 $"hits={hits.Count} finish={completion.FinishReason} responseId={completion.Id}",
        question: question,
        answer: answer);

    return answer + "\n\nSources: " + string.Join(", ", hits.Select(h => h.Source).Distinct()) +
        $"\n[tokens: embedding {embedUsage.InputTokens}, chat in {chatUsage.InputTokens} / out {chatUsage.OutputTokens}" +
        $" (reasoning {chatUsage.ReasoningTokens}) | cost ${embedUsage.CostUsd + chatUsage.CostUsd:0.000000}]";
}