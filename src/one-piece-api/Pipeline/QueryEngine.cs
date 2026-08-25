using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OnePieceApi.Config;
using OnePieceApi.Models;
using OnePieceApi.Observability;
using OnePieceApi.Retrieval;
using OnePieceApi.Safety;

namespace OnePieceApi.Pipeline;

/// <summary>
/// Executes one user query end-to-end: input guardrails, exact and semantic cache lookup,
/// LLM intent routing, route-specific retrieval and generation, output guardrails, cache
/// save, and a full observability trail. Program.cs and the eval harness both drive this
/// class, so interactive runs and regression runs exercise identical code.
/// </summary>
public class QueryEngine(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    SearchService searchService,
    SemanticCacheService semanticCache,
    SqliteDatabaseService databaseService,
    SemanticCacheOptions cacheOptions,
    InputGuardrails inputGuardrails,
    OutputGuardrails outputGuardrails,
    LlmInstrumentation instrumentation,
    PipelineMetrics metrics,
    TraceStore traces,
    ILogger<QueryEngine> logger)
{
    // The router prompt asks for a single word, so cap generation rather than paying for a
    // full-length response on every query. Temperature 0 keeps routing deterministic.
    private static readonly ChatOptions RoutingChatOptions = new() { MaxOutputTokens = 5, Temperature = 0 };

    public async Task<QueryResult> ExecuteAsync(
        string query,
        IProgress<string>? onToken = null,
        Action<PipelineEvent>? onStage = null,
        CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid().ToString("n");
        var stopwatch = Stopwatch.StartNew();
        var decisions = new List<GuardrailDecision>();

        // 1) Input guardrails run before anything reaches an LLM prompt.
        var inputReport = inputGuardrails.Evaluate(query);
        decisions.AddRange(inputReport.Decisions);

        if (!inputReport.Allowed)
        {
            var failure = inputReport.FirstFailure!;
            var refusal = InputGuardrails.GetRefusalMessage(failure);

            metrics.RecordGuardrailBlock(failure.Reason ?? "unknown");
            metrics.RecordQueryOutcome("Blocked", stopwatch.Elapsed);
            logger.LogInformation("Query {TraceId} blocked by {Check}: {Reason}", traceId, failure.Check, failure.Reason);

            WriteQueryTrace(traceId, query, "blocked", RouteKind.None, CacheOutcome.Disabled, stopwatch.Elapsed,
                attributes: new Dictionary<string, object?>
                {
                    ["blockedBy"] = failure.Check.ToString(),
                    ["blockReason"] = failure.Reason,
                });

            return new QueryResult
            {
                Query = query,
                Outcome = QueryOutcome.Blocked,
                Answer = refusal,
                GuardrailDecisions = decisions,
                Cache = CacheOutcome.Disabled,
                TraceId = traceId,
                Elapsed = stopwatch.Elapsed,
            };
        }

        // 2) A byte-identical repeat is a direct key fetch, so check it before spending an
        // embedding round trip on the semantic lookup.
        if (cacheOptions.Enabled)
        {
            var (isExactHit, exactAnswer, exactSources) = await semanticCache.GetExactMatchAsync(query, cancellationToken);
            if (isExactHit && exactAnswer != null)
            {
                metrics.RecordCache("ExactHit");
                metrics.RecordQueryOutcome("Answered", stopwatch.Elapsed);
                WriteQueryTrace(traceId, query, "ok", RouteKind.None, CacheOutcome.ExactHit, stopwatch.Elapsed);

                return new QueryResult
                {
                    Query = query,
                    Outcome = QueryOutcome.Answered,
                    Answer = exactAnswer,
                    Sources = exactSources ?? [],
                    Cache = CacheOutcome.ExactHit,
                    GuardrailDecisions = decisions,
                    TraceId = traceId,
                    Elapsed = stopwatch.Elapsed,
                };
            }
        }

        // 3) Embed lazily: only the semantic cache lookup and the VECTOR route need a vector,
        // so a cache-disabled SQL or GENERAL query never pays for one.
        ReadOnlyMemory<float>? queryVector = null;
        async Task<ReadOnlyMemory<float>> GetQueryVectorAsync()
        {
            queryVector ??= await instrumentation.EmbedAsync(
                embeddingGenerator, traceId, "query_embedding", query, cancellationToken);
            return queryVector.Value;
        }

        if (cacheOptions.Enabled)
        {
            var (isHit, cachedAnswer, cachedSources, similarityScore) = await semanticCache.GetCachedResponseAsync(
                query,
                await GetQueryVectorAsync(),
                cacheOptions.SimilarityThreshold,
                cancellationToken);

            if (isHit && cachedAnswer != null)
            {
                metrics.RecordCache("SemanticHit");
                metrics.RecordQueryOutcome("Answered", stopwatch.Elapsed);
                WriteQueryTrace(traceId, query, "ok", RouteKind.None, CacheOutcome.SemanticHit, stopwatch.Elapsed,
                    attributes: new Dictionary<string, object?> { ["similarity"] = similarityScore });

                return new QueryResult
                {
                    Query = query,
                    Outcome = QueryOutcome.Answered,
                    Answer = cachedAnswer,
                    Sources = cachedSources ?? [],
                    Cache = CacheOutcome.SemanticHit,
                    CacheSimilarity = similarityScore,
                    GuardrailDecisions = decisions,
                    TraceId = traceId,
                    Elapsed = stopwatch.Elapsed,
                };
            }

            metrics.RecordCache("Miss");
            onStage?.Invoke(new PipelineEvent(PipelineStage.SemanticCacheMiss));
        }

        // 4) Classify the query intent using the LLM router.
        var classificationPrompt = $"""
            You are a query router. Classify the user query into exactly one of three categories:
            - "SQL" (if the query requires analytical or quantitative calculations, counts, averages, sorting, or groupings about ratings, episodes, seasons, or release years)
            - "VECTOR" (if the query is asking about character descriptions, storylines, plot details, relationships, or what happens in the episodes)
            - "GENERAL" (if the query is a greeting, general chitchat, help request, or a general knowledge/coding question not about One Piece)

            Respond with exactly one word: either "SQL", "VECTOR", or "GENERAL". Do not write anything else.

            Query: {query}
            Category:
            """;

        onStage?.Invoke(new PipelineEvent(PipelineStage.Routing));
        var routingResponse = await instrumentation.ChatAsync(
            chatClient, traceId, "routing", classificationPrompt, RoutingChatOptions, cancellationToken);

        var intent = RouteParser.ParseIntent(routingResponse.Text);
        var route = intent switch
        {
            "SQL" => RouteKind.Sql,
            "VECTOR" => RouteKind.Vector,
            _ => RouteKind.General,
        };

        string answer;
        var sources = new List<EpisodeRecord>();
        string? generatedSql = null;
        string? sqlResults = null;

        // 5) Execute the selected route.
        if (route == RouteKind.Sql)
        {
            onStage?.Invoke(new PipelineEvent(PipelineStage.RouteSql));

            var sqlPrompt = $"""
                You are a SQLite query generator. Write a single SQLite SELECT statement to answer the user query.
                Database schema:
                Table: Episodes (
                    Id INT,
                    Title TEXT,
                    Overview TEXT,
                    Season INT,
                    EpisodeNumber INT,
                    ReleaseYear INT,
                    Rating REAL
                )

                Respond with ONLY the raw SQL query. Do not write markdown, explanations, or any other text.

                Query: {query}
                SQL:
                """;

            var sqlGenResponse = await instrumentation.ChatAsync(
                chatClient, traceId, "sql_generation", sqlPrompt, cancellationToken: cancellationToken);
            generatedSql = RouteParser.StripCodeFence(sqlGenResponse.Text);
            onStage?.Invoke(new PipelineEvent(PipelineStage.SqlGenerated, generatedSql));

            sqlResults = await databaseService.ExecuteSqlQueryAsync(generatedSql, cancellationToken);

            var answerPrompt = $"""
                You are an expert One Piece assistant. Answer the user's question accurately using the structured SQLite database results provided below.

                Database Results:
                {sqlResults}

                User Question: {query}
                Answer:
                """;

            onStage?.Invoke(new PipelineEvent(PipelineStage.AnswerStart));
            (answer, _) = await instrumentation.StreamChatAsync(
                chatClient, traceId, "answer", answerPrompt, onToken, cancellationToken);
        }
        else if (route == RouteKind.Vector)
        {
            onStage?.Invoke(new PipelineEvent(PipelineStage.RouteVector));

            var matches = await searchService.SearchAsync(await GetQueryVectorAsync(), limit: 5, cancellationToken);
            if (matches.Count == 0)
            {
                metrics.RecordQueryOutcome("NoMatches", stopwatch.Elapsed);
                WriteQueryTrace(traceId, query, "ok", RouteKind.Vector, CacheOutcomeForTrace(), stopwatch.Elapsed);

                return new QueryResult
                {
                    Query = query,
                    Outcome = QueryOutcome.NoMatches,
                    Route = RouteKind.Vector,
                    Answer = "No matching episodes found.",
                    GuardrailDecisions = decisions,
                    TraceId = traceId,
                    Elapsed = stopwatch.Elapsed,
                };
            }

            var sortedMatches = matches.OrderByDescending(e => e.Rating).ToList();

            // Invariant formatting: on a comma-decimal locale a rating would otherwise reach the
            // model as "9,1", disagreeing with the SQL route and reading as a list separator.
            var contextData = string.Join("\n", sortedMatches.Select(e => string.Create(
                CultureInfo.InvariantCulture,
                $"- Title: {e.Title}, Season: {e.Season}, Episode: {e.EpisodeNumber}, Year: {e.ReleaseYear}, Rating: {e.Rating}\n  Overview: {e.Overview}")));

            var answerPrompt = $"""
                You are an expert One Piece assistant. Answer the user's question accurately using ONLY the provided context dataset below.

                Context Dataset (ordered from highest rating to lowest rating):
                {contextData}

                User Question: {query}
                Answer:
                """;

            onStage?.Invoke(new PipelineEvent(PipelineStage.AnswerStart));
            (answer, _) = await instrumentation.StreamChatAsync(
                chatClient, traceId, "answer", answerPrompt, onToken, cancellationToken);
            sources = sortedMatches;
        }
        else
        {
            onStage?.Invoke(new PipelineEvent(PipelineStage.RouteGeneral));

            onStage?.Invoke(new PipelineEvent(PipelineStage.AnswerStart));
            (answer, _) = await instrumentation.StreamChatAsync(
                chatClient, traceId, "answer", query, onToken, cancellationToken);
        }

        // 6) Output guardrails review the answer before it is shown for real or cached.
        var review = outputGuardrails.Review(answer);
        decisions.AddRange(review.Report.Decisions);

        var outcome = QueryOutcome.Answered;
        if (!review.Report.Allowed)
        {
            var failure = review.Report.FirstFailure!;
            metrics.RecordGuardrailBlock(failure.Reason ?? "unknown");
            answer = OutputGuardrails.GetRefusalMessage(failure);
            outcome = QueryOutcome.Refused;
            logger.LogInformation("Answer for {TraceId} withheld by output guardrail: {Reason}", traceId, failure.Reason);
        }
        else
        {
            answer = review.SafeAnswer;
        }

        // 7) Cache safe answers only.
        var cacheOutcome = CacheOutcome.Disabled;
        if (cacheOptions.Enabled && outcome == QueryOutcome.Answered)
        {
            await semanticCache.SaveToCacheAsync(query, await GetQueryVectorAsync(), answer, sources, cancellationToken);
            cacheOutcome = CacheOutcome.Saved;
        }

        metrics.RecordRoute(route.ToString());
        metrics.RecordQueryOutcome(outcome.ToString(), stopwatch.Elapsed);
        WriteQueryTrace(traceId, query, outcome == QueryOutcome.Refused ? "blocked" : "ok", route, cacheOutcome, stopwatch.Elapsed,
            attributes: new Dictionary<string, object?>
            {
                ["generatedSql"] = generatedSql,
                ["sourceCount"] = sources.Count,
            });

        return new QueryResult
        {
            Query = query,
            Outcome = outcome,
            Route = route,
            Answer = answer,
            Sources = sources,
            Cache = cacheOutcome,
            GeneratedSql = generatedSql,
            SqlResults = sqlResults,
            GuardrailDecisions = decisions,
            TraceId = traceId,
            Elapsed = stopwatch.Elapsed,
        };

        CacheOutcome CacheOutcomeForTrace() => cacheOptions.Enabled ? CacheOutcome.Miss : CacheOutcome.Disabled;
    }

    private void WriteQueryTrace(
        string traceId,
        string query,
        string status,
        RouteKind route,
        CacheOutcome cache,
        TimeSpan elapsed,
        IReadOnlyDictionary<string, object?>? attributes = null)
    {
        traces.Write(new TraceEvent
        {
            TraceId = traceId,
            Timestamp = DateTimeOffset.UtcNow,
            Kind = "query",
            Name = "execute",
            Status = status,
            LatencyMs = elapsed.TotalMilliseconds,
            Input = traces.PrepareTextForLog(query),
            Attributes = new Dictionary<string, object?>(attributes ?? new Dictionary<string, object?>())
            {
                ["route"] = route.ToString(),
                ["cache"] = cache.ToString(),
            },
        });
    }
}
