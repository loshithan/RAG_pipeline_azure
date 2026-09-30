using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;

record UsageRecord(
    DateTime TimestampUtc,
    string SessionId,
    string Operation,      // "ingest-embedding", "query-embedding", "chat"
    string Model,          // deployment name
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens,   // already included in OutputTokens; shown for info
    long DurationMs,
    decimal CostUsd,
    string? Details,       // sources, file name, ...
    string? Question = null,
    string? Answer = null);

record ModelPrice(decimal InputPer1M, decimal OutputPer1M);

// Callers hand usage to Record(), which returns immediately. A background task
// reads the queue, keeps the running totals and writes each record to:
//   usage-log.jsonl             machine-readable, used by "dotnet run -- usage"
//   logs/usage-yyyy-MM-dd.txt   human-readable, one line per call
sealed class UsageTracker : IAsyncDisposable
{
    // Defaults (USD per 1M tokens, Azure pay-as-you-go). Override under "Pricing" in appsettings.json.
    static readonly Dictionary<string, ModelPrice> DefaultPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Phi-4-mini-reasoning"]   = new(0.08m, 0.32m),
        ["text-embedding-3-small"] = new(0.02m, 0m),
        ["text-embedding-3-large"] = new(0.13m, 0m),
    };

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly Channel<UsageRecord> _queue = Channel.CreateUnbounded<UsageRecord>(new() { SingleReader = true });
    readonly Dictionary<string, ModelPrice> _prices;
    readonly List<UsageRecord> _session = new();
    readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    readonly string _mode;
    readonly string _logPath;
    readonly string _textLogPath;
    readonly Task _worker;

    public UsageTracker(IConfiguration config, string mode, string logPath = "usage-log.jsonl", string textLogDir = "logs")
    {
        _mode        = mode;
        _logPath     = logPath;
        _textLogPath = Path.Combine(textLogDir, $"usage-{DateTime.Now:yyyy-MM-dd}.txt");
        _prices      = new(DefaultPrices, StringComparer.OrdinalIgnoreCase);

        foreach (var model in config.GetSection("Pricing").GetChildren())
        {
            _prices[model.Key] = new(
                decimal.Parse(model["InputPer1M"]  ?? "0", CultureInfo.InvariantCulture),
                decimal.Parse(model["OutputPer1M"] ?? "0", CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(textLogDir);
        _worker = Task.Run(ProcessQueueAsync);
    }

    public UsageRecord Record(string operation, string model, int inputTokens, int outputTokens = 0,
                              int reasoningTokens = 0, long durationMs = 0, string? details = null,
                              string? question = null, string? answer = null)
    {
        if (!_prices.TryGetValue(model, out var price))
        {
            Console.WriteLine($"[usage] No price configured for '{model}', cost counted as $0.");
            price = new(0m, 0m);
        }

        var cost = (inputTokens * price.InputPer1M + outputTokens * price.OutputPer1M) / 1_000_000m;
        var record = new UsageRecord(DateTime.UtcNow, _sessionId, operation, model,
                                     inputTokens, outputTokens, reasoningTokens, durationMs, cost, details,
                                     question, answer);

        _queue.Writer.TryWrite(record);
        return record;
    }

    async Task ProcessQueueAsync()
    {
        await using var json = new StreamWriter(_logPath, append: true);
        await using var text = new StreamWriter(_textLogPath, append: true);

        await text.WriteLineAsync($"{Now()} | session {_sessionId} | START | mode={_mode} | machine={Environment.MachineName} | user={Environment.UserName}");
        await text.FlushAsync();

        await foreach (var r in _queue.Reader.ReadAllAsync())
        {
            _session.Add(r);

            await json.WriteLineAsync(JsonSerializer.Serialize(r, JsonOptions));
            await json.FlushAsync();

            await text.WriteLineAsync(
                $"{r.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz} | session {r.SessionId} | {r.Operation,-16} | {r.Model,-22} | " +
                $"in {r.InputTokens,6} | out {r.OutputTokens,6} | reasoning {r.ReasoningTokens,5} | {r.DurationMs,7} ms | " +
                $"${r.CostUsd:0.000000}" + (r.Details is null ? "" : $" | {r.Details}"));

            if (r.Question is not null) await text.WriteLineAsync(Indented("Q", r.Question));
            if (r.Answer   is not null) await text.WriteLineAsync(Indented("A", r.Answer));
            await text.FlushAsync();
        }

        await text.WriteLineAsync(
            $"{Now()} | session {_sessionId} | END   | calls={_session.Count} | " +
            $"in={_session.Sum(r => r.InputTokens)} | out={_session.Sum(r => r.OutputTokens)} | " +
            $"total=${_session.Sum(r => r.CostUsd):0.000000}");
    }

    // "    Q: first line" with continuation lines aligned under the text
    static string Indented(string label, string value) =>
        $"    {label}: " + string.Join(Environment.NewLine + "       ", value.Trim().ReplaceLineEndings("\n").Split('\n'));

    static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

    // Finish writing everything still queued, then print this run's totals.
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.Complete();
        await _worker;

        if (_session.Count > 0)
        {
            PrintSummary("This session", _session);
            Console.WriteLine($"Log: {Path.GetFullPath(_textLogPath)}");
        }
    }

    // For "dotnet run -- usage": totals for every run recorded in the log.
    public static void PrintAllTime(string logPath = "usage-log.jsonl")
    {
        if (!File.Exists(logPath))
        {
            Console.WriteLine($"No usage recorded yet ({logPath} not found).");
            return;
        }

        var records = File.ReadLines(logPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<UsageRecord>(line, JsonOptions)!)
            .ToList();

        PrintSummary($"All time (since {records.Min(r => r.TimestampUtc):yyyy-MM-dd HH:mm} UTC)", records);
    }

    static void PrintSummary(string title, IReadOnlyCollection<UsageRecord> records)
    {
        Console.WriteLine($"\n===== Token usage: {title} =====");
        Console.WriteLine($"{"Operation",-18} {"Model",-24} {"Calls",6} {"Input",10} {"Output",10} {"Cost (USD)",12}");

        foreach (var g in records.GroupBy(r => (r.Operation, r.Model)).OrderBy(g => g.Key.Operation))
        {
            Console.WriteLine($"{g.Key.Operation,-18} {g.Key.Model,-24} {g.Count(),6} " +
                              $"{g.Sum(r => r.InputTokens),10:N0} {g.Sum(r => r.OutputTokens),10:N0} {g.Sum(r => r.CostUsd),12:0.000000}");
        }

        Console.WriteLine($"{"TOTAL",-18} {"",-24} {records.Count,6} " +
                          $"{records.Sum(r => r.InputTokens),10:N0} {records.Sum(r => r.OutputTokens),10:N0} {records.Sum(r => r.CostUsd),12:0.000000}");
    }
}
