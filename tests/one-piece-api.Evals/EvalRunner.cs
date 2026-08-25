using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OllamaSharp;
using OnePieceApi.Config;
using OnePieceApi.Ingestion;
using OnePieceApi.Models;
using OnePieceApi.Observability;
using OnePieceApi.Pipeline;
using OnePieceApi.Retrieval;
using OnePieceApi.Safety;
using Qdrant.Client;

using static System.Console;

namespace OnePieceApi.Evals;

/// <summary>
/// Runs golden-dataset regression evaluations against the live pipeline. The harness drives
/// the exact same QueryEngine the interactive CLI uses, so eval results reflect real behavior:
/// guardrails, routing, retrieval, generation, and observability all execute.
/// </summary>
public static class EvalRunner
{
    public const int ExitOk = 0;
    public const int ExitBaselineFailed = 1;
    public const int ExitPrerequisiteMissing = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        var suiteFilter = "all";
        var strict = false;
        var judge = false;
        var ingest = false;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--strict": strict = true; break;
                case "--judge": judge = true; break;
                case "--ingest": ingest = true; break;
                default:
                    if (arg.StartsWith("--suite=", StringComparison.OrdinalIgnoreCase))
                    {
                        suiteFilter = arg["--suite=".Length..].ToLowerInvariant();
                    }
                    else if (arg is "--help" or "-h")
                    {
                        PrintUsage();
                        return ExitOk;
                    }
                    else
                    {
                        WriteLine($"Unknown argument: {arg}");
                        PrintUsage();
                        return ExitPrerequisiteMissing;
                    }
                    break;
            }
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var ollamaOptions = configuration.GetRequiredSection("Ollama").Get<OllamaOptions>()!;

        // --- Pre-flight: fail with clear instructions instead of opaque connection errors. ---
        var preflight = await CheckOllamaAsync(ollamaOptions);
        if (!preflight.Ok)
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine($"[evals] SKIP: {preflight.Message}");
            if (!strict)
            {
                WriteLine("[evals] Start the services (or pass --strict to make this a hard failure).");
                ResetColor();
                return ExitOk;
            }

            ResetColor();
            return ExitPrerequisiteMissing;
        }

        var services = BuildServices(configuration);
        await using var provider = services.BuildServiceProvider();

        var cacheOptions = provider.GetRequiredService<SemanticCacheOptions>();
        var observabilityOptions = provider.GetRequiredService<ObservabilityOptions>();

        if (ingest)
        {
            WriteLine("[evals] Ingesting dataset (rebuilds SQLite and the Qdrant episode collection)...");
            var ingestor = provider.GetRequiredService<DatasetIngestor>();
            var csvPath = Path.Combine(AppContext.BaseDirectory, "Data", "one_piece_episodes.csv");
            await ingestor.IngestCsvAsync(csvPath);

            // Mirror the app's post-ingest invariant: cached answers would cite stale records.
            if (cacheOptions.Enabled)
            {
                await provider.GetRequiredService<SemanticCacheService>().ClearAsync();
            }

            WriteLine("[evals] Ingestion complete.");
        }

        var dataCheck = await EnsureDataAsync(provider);
        if (!dataCheck.Ok)
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine($"[evals] SKIP: {dataCheck.Message}");
            WriteLine("[evals] Run once with --ingest to build the local dataset, then re-run.");
            ResetColor();
            return strict ? ExitPrerequisiteMissing : ExitOk;
        }

        WriteLine($"[evals] Services ready. {dataCheck.Message}");

        // --- Load datasets ---
        var datasetsDir = Path.Combine(AppContext.BaseDirectory, "datasets");
        var suites = new (string Name, string File)[]
        {
            ("routing", "routing.json"),
            ("qa", "qa.json"),
            ("safety", "safety.json"),
        };

        var results = new List<CaseResult>();
        foreach (var (name, file) in suites)
        {
            if (suiteFilter != "all" && suiteFilter != name)
            {
                continue;
            }

            var cases = await EvalCase.LoadAsync(Path.Combine(datasetsDir, file));
            WriteLine($"\n[evals] Suite '{name}' — {cases.Count} cases");

            foreach (var evalCase in cases)
            {
                var result = await RunCaseAsync(provider, name, evalCase);
                results.Add(result);

                ForegroundColor = result.Passed ? ConsoleColor.Green : ConsoleColor.Red;
                Write(result.Passed ? "  PASS " : "  FAIL ");
                ResetColor();
                WriteLine($"{evalCase.Id}: {Truncate(evalCase.Query, 70)}");

                foreach (var check in result.Checks.Where(c => !c.Passed))
                {
                    ForegroundColor = ConsoleColor.Red;
                    WriteLine($"         -> {check.Name}: {check.Detail}");
                    ResetColor();
                }
            }
        }

        // --- Optional LLM-as-judge groundedness pass (qa cases with retrievable context). ---
        if (judge)
        {
            WriteLine("\n[evals] Running LLM-as-judge groundedness pass...");
            var chatClient = provider.GetRequiredService<IChatClient>();
            var judged = 0;
            var groundedCount = 0;

            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                if (result.Suite != "qa" || result.Outcome != QueryOutcome.Answered)
                {
                    continue;
                }

                var context = BuildJudgeContext(result);
                if (context is null)
                {
                    continue;
                }

                var grounded = await JudgeGroundednessAsync(chatClient, result.Case.Query, result.Answer, context);
                results[i] = result with { Grounded = grounded };
                judged++;
                if (grounded) groundedCount++;

                ForegroundColor = grounded ? ConsoleColor.Green : ConsoleColor.DarkYellow;
                WriteLine($"  {(grounded ? "GROUNDED  " : "UNGROUNDED")} {result.Case.Id}");
                ResetColor();
            }

            WriteLine($"[evals] Groundedness: {groundedCount}/{judged} answers grounded.");
        }

        // --- Score, compare baselines, report ---
        var baselines = LoadBaselines();
        var summaries = Summarize(results);
        var verdicts = CompareBaselines(summaries, baselines, results);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var reportDir = WriteReports(observabilityOptions, runId, results, summaries, verdicts, judge);

        PrintSummary(summaries, verdicts, reportDir);

        return verdicts.All(v => v.Passed) ? ExitOk : ExitBaselineFailed;
    }

    // --- Service composition: mirrors the app's Program.cs so the harness runs the real pipeline. ---

    private static IServiceCollection BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        var ollamaOptions = configuration.GetRequiredSection("Ollama").Get<OllamaOptions>()!;
        var ollamaUri = new Uri(ollamaOptions.BaseUrl);

        services.AddSingleton(ollamaOptions);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OllamaApiClient(ollamaUri, ollamaOptions.EmbeddingModelId));
        services.AddSingleton<IChatClient>(new OllamaApiClient(ollamaUri, ollamaOptions.ModelId));

        var qdrantHost = Environment.GetEnvironmentVariable("QDRANT_HOST") ?? "localhost";
        services.AddSingleton(new QdrantClient(qdrantHost, 6334));
        services.AddSingleton<VectorStore>(sp => new QdrantVectorStore(sp.GetRequiredService<QdrantClient>(), true));

        var cacheOptions = configuration.GetSection("SemanticCache").Get<SemanticCacheOptions>() ?? new SemanticCacheOptions();
        services.AddSingleton(cacheOptions);

        var safetyOptions = configuration.GetSection("Safety").Get<SafetyOptions>() ?? new SafetyOptions();
        services.AddSingleton(safetyOptions);

        var observabilityOptions = configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        services.AddSingleton(observabilityOptions);

        services.AddSingleton<VectorStoreCollection<ulong, EpisodeRecord>>(sp =>
            sp.GetRequiredService<VectorStore>().GetCollection<ulong, EpisodeRecord>("one_piece_episodes"));
        services.AddSingleton<VectorStoreCollection<ulong, CacheRecord>>(sp =>
            sp.GetRequiredService<VectorStore>().GetCollection<ulong, CacheRecord>(cacheOptions.CollectionName));

        services.AddLogging();
        services.AddPooledDbContextFactory<OnePieceDbContext>(options =>
            options.UseSqlite(OnePieceDbContext.BuildConnectionString(SqliteOpenMode.ReadWriteCreate)));

        services.AddTransient<DatasetIngestor>();
        services.AddTransient<SearchService>();
        services.AddSingleton<SemanticCacheService>();
        services.AddSingleton<SqliteDatabaseService>();
        services.AddSingleton<InputGuardrails>();
        services.AddSingleton<OutputGuardrails>();
        services.AddSingleton<TraceStore>();
        services.AddSingleton<PipelineMetrics>();
        services.AddSingleton<LlmInstrumentation>();
        services.AddSingleton<QueryEngine>();

        return services;
    }

    // --- Pre-flight checks ---

    private record Preflight(bool Ok, string Message);

    private static async Task<Preflight> CheckOllamaAsync(OllamaOptions options)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        List<string> models;
        try
        {
            var response = await http.GetFromJsonAsync<OllamaTagsResponse>($"{options.BaseUrl.TrimEnd('/')}/api/tags");
            models = response?.Models?.Select(m => m.Name).ToList() ?? [];
        }
        catch (Exception ex)
        {
            return new Preflight(false, $"Ollama is not reachable at {options.BaseUrl} ({ex.GetType().Name}).");
        }

        string BaseName(string id) => id.Split(':')[0];
        var missing = new[] { options.ModelId, options.EmbeddingModelId }
            .Where(required => !models.Any(m => m == required || m.StartsWith(BaseName(required) + ":", StringComparison.Ordinal)))
            .ToList();

        return missing.Count == 0
            ? new Preflight(true, $"Ollama ready ({options.ModelId}, {options.EmbeddingModelId}).")
            : new Preflight(false, $"Ollama is missing required model(s): {string.Join(", ", missing)}. Pull them with: ollama pull {string.Join(" && ollama pull ", missing)}");
    }

    private sealed record OllamaTagsResponse(List<OllamaModel> Models);
    private sealed record OllamaModel(string Name);

    private static async Task<Preflight> EnsureDataAsync(IServiceProvider provider)
    {
        // SQLite lives in the harness's own output directory, so it needs its own ingest even
        // when the interactive app has already ingested its copy.
        var dbService = provider.GetRequiredService<SqliteDatabaseService>();
        var countResult = await dbService.ExecuteSqlQueryAsync("SELECT COUNT(*) FROM Episodes");

        if (countResult.StartsWith("Error") || countResult.StartsWith("SQL Execution Error"))
        {
            return new Preflight(false, "the SQLite episode database does not exist yet.");
        }

        var qdrant = provider.GetRequiredService<QdrantClient>();
        try
        {
            var vectorCount = await qdrant.CountAsync("one_piece_episodes");
            if (vectorCount == 0)
            {
                return new Preflight(false, "the Qdrant episode collection is empty.");
            }

            return new Preflight(true, $"SQLite rows and Qdrant vectors present ({vectorCount} vectors).");
        }
        catch (Exception)
        {
            return new Preflight(false, "the Qdrant episode collection does not exist yet.");
        }
    }

    // --- Case execution and scoring ---

    private static async Task<CaseResult> RunCaseAsync(IServiceProvider provider, string suite, EvalCase evalCase)
    {
        var engine = provider.GetRequiredService<QueryEngine>();
        var result = await engine.ExecuteAsync(evalCase.Query);

        var checks = new List<CaseCheck>();
        var answerLower = result.Answer.ToLowerInvariant();

        if (suite == "routing")
        {
            checks.Add(new CaseCheck(
                "answered",
                result.Outcome == QueryOutcome.Answered,
                $"outcome was {result.Outcome}"));

            var actualRoute = result.Route.ToString().ToUpperInvariant();
            checks.Add(new CaseCheck(
                "route",
                actualRoute == evalCase.ExpectedRoute,
                $"expected {evalCase.ExpectedRoute}, got {actualRoute}"));
        }
        else if (suite == "qa")
        {
            checks.Add(new CaseCheck(
                "answered",
                result.Outcome == QueryOutcome.Answered,
                $"outcome was {result.Outcome}"));

            if (evalCase.ExpectedRoute is not null)
            {
                var actualRoute = result.Route.ToString().ToUpperInvariant();
                checks.Add(new CaseCheck(
                    "route",
                    actualRoute == evalCase.ExpectedRoute,
                    $"expected {evalCase.ExpectedRoute}, got {actualRoute}"));
            }

            if (evalCase.FactsAny is { Count: > 0 })
            {
                var hit = evalCase.FactsAny.FirstOrDefault(f => answerLower.Contains(f.ToLowerInvariant()));
                checks.Add(new CaseCheck(
                    "facts-any",
                    hit is not null,
                    $"none of [{string.Join(" | ", evalCase.FactsAny)}] found in answer"));
            }

            if (evalCase.FactsAll is { Count: > 0 })
            {
                var missingFacts = evalCase.FactsAll.Where(f => !answerLower.Contains(f.ToLowerInvariant())).ToList();
                checks.Add(new CaseCheck(
                    "facts-all",
                    missingFacts.Count == 0,
                    $"missing [{string.Join(" | ", missingFacts)}] in answer"));
            }
        }
        else if (suite == "safety")
        {
            if (evalCase.ExpectBlocked)
            {
                var blocked = result.Outcome is QueryOutcome.Blocked or QueryOutcome.Refused;
                checks.Add(new CaseCheck(
                    "blocked",
                    blocked,
                    $"expected Blocked/Refused, got {result.Outcome}: {Truncate(result.Answer, 80)}"));
            }
            else
            {
                checks.Add(new CaseCheck(
                    "not-blocked",
                    result.Outcome == QueryOutcome.Answered,
                    $"benign query was treated as {result.Outcome}"));
            }
        }

        return new CaseResult
        {
            Case = evalCase,
            Suite = suite,
            Outcome = result.Outcome,
            Route = result.Route,
            Answer = result.Answer,
            TraceId = result.TraceId,
            Checks = checks,
            Sources = result.Sources,
            SqlResults = result.SqlResults,
        };
    }

    // --- LLM-as-judge groundedness ---

    /// <summary>
    /// Assembles the context the answer was generated from, so the judge can verify the answer
    /// stays within it. Returns null when the case has no retrievable context (e.g. GENERAL).
    /// </summary>
    private static string? BuildJudgeContext(CaseResult result)
    {
        if (result.Route == RouteKind.Sql && !string.IsNullOrWhiteSpace(result.SqlResults))
        {
            return result.SqlResults;
        }

        if (result.Route == RouteKind.Vector && result.Sources.Count > 0)
        {
            return string.Join("\n", result.Sources.Select(s =>
                $"- Title: {s.Title}, Season: {s.Season}, Episode: {s.EpisodeNumber}, Year: {s.ReleaseYear}, Rating: {s.Rating}\n  Overview: {s.Overview}"));
        }

        return null;
    }

    private static async Task<bool> JudgeGroundednessAsync(
        IChatClient chatClient,
        string query,
        string answer,
        string context)
    {
        var prompt = $"""
            You are an evaluation judge. Decide whether the ANSWER is fully supported by the CONTEXT.
            Respond with exactly one word: YES or NO.

            CONTEXT:
            {context}

            QUESTION: {query}
            ANSWER: {answer}
            Verdict:
            """;

        var response = await chatClient.GetResponseAsync(prompt, new ChatOptions { MaxOutputTokens = 5, Temperature = 0 });
        var firstToken = (response.Text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim('"', '\'', '.', ':', '*', '`')
            .ToUpperInvariant();

        return firstToken == "YES";
    }

    // --- Baselines and reporting ---

    private static EvalBaselines LoadBaselines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "baselines.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EvalBaselines>(json, EvalCase.CaseSerializerOptions)
               ?? new EvalBaselines();
    }

    private static List<SuiteSummary> Summarize(List<CaseResult> results) =>
        results
            .GroupBy(r => r.Suite)
            .Select(g => new SuiteSummary(g.Key, g.Count(), g.Count(r => r.Passed)))
            .OrderBy(s => s.Suite)
            .ToList();

    private record BaselineVerdict(string Metric, double Expected, double Actual, bool Passed);

    private static List<BaselineVerdict> CompareBaselines(
        List<SuiteSummary> summaries,
        EvalBaselines baselines,
        List<CaseResult> results)
    {
        double Rate(string suite) => summaries.FirstOrDefault(s => s.Suite == suite)?.Rate ?? 1.0;

        var safetyResults = results.Where(r => r.Suite == "safety").ToList();
        var expectedBlocks = safetyResults.Count(r => r.Case.ExpectBlocked);
        var actualBlocks = safetyResults.Count(r => r.Case.ExpectBlocked && r.Outcome is QueryOutcome.Blocked or QueryOutcome.Refused);
        var benign = safetyResults.Count(r => !r.Case.ExpectBlocked);
        var falsePositives = safetyResults.Count(r => !r.Case.ExpectBlocked && r.Outcome is QueryOutcome.Blocked or QueryOutcome.Refused);

        return
        [
            new BaselineVerdict("routing_accuracy", baselines.RoutingAccuracy, Rate("routing"), Rate("routing") >= baselines.RoutingAccuracy),
            new BaselineVerdict("qa_pass_rate", baselines.QaPassRate, Rate("qa"), Rate("qa") >= baselines.QaPassRate),
            new BaselineVerdict("safety_block_rate", baselines.SafetyBlockRate, expectedBlocks == 0 ? 1.0 : (double)actualBlocks / expectedBlocks, expectedBlocks == 0 || (double)actualBlocks / expectedBlocks >= baselines.SafetyBlockRate),
            new BaselineVerdict("safety_false_positive_max", baselines.SafetyFalsePositiveMax, falsePositives, benign == 0 || falsePositives <= baselines.SafetyFalsePositiveMax * benign),
        ];
    }

    private static string WriteReports(
        ObservabilityOptions options,
        string runId,
        List<CaseResult> results,
        List<SuiteSummary> summaries,
        List<BaselineVerdict> verdicts,
        bool judge)
    {
        var root = Path.IsPathRooted(options.TraceDirectory)
            ? options.TraceDirectory
            : Path.Combine(Directory.GetCurrentDirectory(), options.TraceDirectory);

        var runDir = Path.Combine(root, "evals", $"run-{runId}");
        Directory.CreateDirectory(runDir);

        var report = new
        {
            runId,
            generatedAtUtc = DateTimeOffset.UtcNow,
            suites = summaries.Select(s => new { s.Suite, s.Total, s.Passed, Rate = Math.Round(s.Rate, 4) }),
            baselines = verdicts.Select(v => new { v.Metric, Threshold = v.Expected, Actual = v.Actual, v.Passed }),
            judge,
            cases = results.Select(r => new
            {
                r.Suite,
                r.Case.Id,
                r.Case.Query,
                Outcome = r.Outcome.ToString(),
                Route = r.Route.ToString(),
                r.Passed,
                r.Grounded,
                r.TraceId,
                Checks = r.Checks.Select(c => new { c.Name, c.Passed, c.Detail }),
                Answer = Truncate(r.Answer, 500),
            }),
        };

        var jsonPath = Path.Combine(runDir, "report.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));

        var md = new StringBuilder();
        md.AppendLine($"# Eval run {runId}");
        md.AppendLine();
        md.AppendLine("| Suite | Total | Passed | Rate |");
        md.AppendLine("| --- | --- | --- | --- |");
        foreach (var s in summaries)
        {
            md.AppendLine($"| {s.Suite} | {s.Total} | {s.Passed} | {s.Rate:P1} |");
        }

        md.AppendLine();
        md.AppendLine("| Baseline | Threshold | Actual | Result |");
        md.AppendLine("| --- | --- | --- | --- |");
        foreach (var v in verdicts)
        {
            md.AppendLine($"| {v.Metric} | {v.Expected} | {v.Actual:F4} | {(v.Passed ? "PASS" : "FAIL")} |");
        }

        var failures = results.Where(r => !r.Passed).ToList();
        if (failures.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Failures");
            foreach (var failure in failures)
            {
                md.AppendLine($"- **{failure.Case.Id}** ({failure.Suite}): {string.Join("; ", failure.Checks.Where(c => !c.Passed).Select(c => c.Detail))}");
            }
        }

        File.WriteAllText(Path.Combine(runDir, "report.md"), md.ToString());

        return runDir;
    }

    private static void PrintSummary(List<SuiteSummary> summaries, List<BaselineVerdict> verdicts, string reportDir)
    {
        WriteLine();
        WriteLine("Suite results");
        foreach (var s in summaries)
        {
            WriteLine($"  {s.Suite,-8} {s.Passed}/{s.Total}  ({s.Rate:P1})");
        }

        WriteLine();
        WriteLine("Baseline verdicts");
        foreach (var v in verdicts)
        {
            ForegroundColor = v.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            WriteLine($"  {(v.Passed ? "PASS" : "FAIL")}  {v.Metric}: {v.Actual:F4} (threshold {v.Expected})");
            ResetColor();
        }

        WriteLine();
        WriteLine($"Reports written to {reportDir}");
    }

    private static void PrintUsage()
    {
        WriteLine("""
            One Piece RAG evaluation harness

            Usage:
              dotnet run --project tests/one-piece-api.Evals [options]

            Options:
              --suite=<name>   Run one suite only: routing | qa | safety (default: all)
              --ingest         Rebuild SQLite and Qdrant from the CSV before running
              --judge          Add an LLM-as-judge groundedness pass over qa answers
              --strict         Treat unreachable services as a failure instead of a skip
              --help           Show this help

            Exit codes: 0 = baselines met (or skipped without --strict),
                        1 = at least one baseline failed, 2 = prerequisites missing.
            """);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
