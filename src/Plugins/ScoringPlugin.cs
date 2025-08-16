// ScoringPlugin.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;                         // KernelFunction

using MyM365AgentDecommision.Bot.Interfaces;           // IClusterDataProvider
using MyM365AgentDecommision.Bot.Models;               // ClusterRow
using MyM365AgentDecommision.Bot.Services;             // ScoringService, FilteringEngine, ClusterRowAccessors, IWeightsStore, IRequestContext

namespace MyM365AgentDecommision.Bot.Plugins;

/// <summary>
/// Scoring, explainability, comparison, and what-if utilities used by the Decom agent.
/// </summary>
public sealed class ScoringPlugin
{
    private readonly IClusterDataProvider _data;
    private readonly ScoringService _scoring;
    private readonly FilteringEngine _filter;
    private readonly ILogger<ScoringPlugin> _log;
    private readonly IWeightsStore? _weightsStore; // optional
    private readonly IRequestContext? _req;        // NEW: ambient context (conversation/user)

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false
    };

    public ScoringPlugin(
        IClusterDataProvider data,
        ScoringService scoring,
        FilteringEngine filter,
        ILogger<ScoringPlugin> log,
        IWeightsStore? weightsStore = null,
        IRequestContext? req = null)  // NEW: accept ambient request context
    {
        _data = data;
        _scoring = scoring;
        _filter = filter;
        _log = log;
        _weightsStore = weightsStore;
        _req = req;
    }

    // Resolve a session key: explicit beats ambient; else "default".
    private string ResolveSessionKey(string? sessionKey)
        => !string.IsNullOrWhiteSpace(sessionKey)
            ? sessionKey!
            : (_req?.ConversationId ?? "default");

    // -----------------------------------------------------------------------------
    //  Catalog & weights
    // -----------------------------------------------------------------------------

    [KernelFunction, Description("List all scoring features (raw & derived) with metadata.")]
    public Task<object> ListFeatures()
        => Task.FromResult<object>(_scoring.ListFeatures());

    [KernelFunction, Description("Return the system-default scoring weights.")]
    public Task<object> GetDefaultWeights()
        => Task.FromResult<object>(ScoringService.DefaultWeights());

    [KernelFunction, Description("Return the set of weight keys the user can adjust.")]
    public Task<object> WhatWeightsAreAdjustable()
        => Task.FromResult<object>(ScoringService.DefaultWeights().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());

    // -----------------------------------------------------------------------------
    //  NEW: Dump normalization stats (winsorized min/max) + applied weights
    // -----------------------------------------------------------------------------

    // Expose via your ScoringPlugin so you can ask for it in chat:
    [KernelFunction, Description("Dump the normalization bounds (min/max after winsorization) and weights in use.")]
    public async Task<string> DumpScoringStatsJson(CancellationToken ct = default)
    {
        var opts = new ScoringService.ScoringOptions
        {
            Winsorize = true,
            LowerQuantile = 0.02,
            UpperQuantile = 0.98,
            PerFeatureQuantiles = new()
            {
                // Example override: OOSNodeRatio @ 5/95
                ["OOSNodeRatio"] = (0.05, 0.95),
            },
            PerRowReweightMissing = true,
            // RankNormalizeKeys = new HashSet<string> { "OOSNodeRatio", "StrandedCoresRatio_DNG" },
        };

        // NOTE: If your ScoringService does not expose this helper, replace with your actual stats-dump API.
        var stats = await _scoring.DumpAppliedStatsAsync(null, opts, ct);

        var payload = new
        {
            AppliedWeights = ScoringService.Rebalance(ScoringService.DefaultWeights()),
            Options = opts,
            NumericStats = stats
        };

        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    // -----------------------------------------------------------------------------
    //  Weight persistence (optional, session/ambient scoped)
    // -----------------------------------------------------------------------------

    [KernelFunction, Description("Persist the provided full weight config (JSON) for this sessionKey.")]
    public Task<string> PersistWeightsForSession(
        [Description("Weight config JSON, e.g. {\"ClusterAgeYears\":0.2,\"EffectiveCoreUtilization\":0.3}")] string weightsJson,
        [Description("Arbitrary session key used to recall the weights later (optional). If omitted, ambient conversation id is used.")] string? sessionKey = null)
    {
        var weights = ParseWeights(weightsJson);
        if (_weightsStore is not null)
            _weightsStore.Set(ResolveSessionKey(sessionKey), weights);
        return Task.FromResult("OK");
    }

    [KernelFunction, Description("Reset session weights to system defaults for this sessionKey (or ambient).")]
    public Task<string> ResetWeightsToDefaults([Description("Session key to clear (optional).")] string? sessionKey = null)
    {
        if (_weightsStore is not null)
            _weightsStore.Reset(ResolveSessionKey(sessionKey));
        return Task.FromResult("OK");
    }

    // -----------------------------------------------------------------------------
    //  NEW: Partial overrides helper (merge & rebalance) + convenience endpoints
    // -----------------------------------------------------------------------------

    private ScoringService.WeightConfig MergeAndRebalance(
        ScoringService.WeightConfig baseline,
        IDictionary<string, double?> overrides,
        bool normalizeRest)
    {
        // Start with a copy so we never mutate the caller's instance.
        var result = new ScoringService.WeightConfig(baseline);

        // Clean + validate overrides
        var clean = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides)
        {
            if (v is null) continue;
            if (!result.ContainsKey(k))
            {
                _log.LogWarning("MergeWeights: unknown feature '{feature}' ignored.", k);
                continue;
            }
            var vv = v.Value;
            if (vv < 0)
            {
                _log.LogWarning("MergeWeights: negative weight for '{feature}' clamped to 0.", k);
                vv = 0;
            }
            clean[k] = vv;
        }

        // Apply overrides
        foreach (var (k, vv) in clean) result[k] = vv;

        if (normalizeRest)
        {
            var sumOverrides = clean.Values.Sum();
            if (sumOverrides < 1.0 - 1e-12)
            {
                var remaining = 1.0 - sumOverrides;
                var nonKeys = result.Keys.Where(k => !clean.ContainsKey(k)).ToArray();
                var nonSum = nonKeys.Sum(k => result[k]);
                if (nonSum > 0)
                {
                    foreach (var k in nonKeys)
                        result[k] = result[k] * (remaining / nonSum);
                }
                // else: nothing to scale; final rebalance will handle it
            }
            else
            {
                // Overrides dominate; zero the rest and let final rebalance normalize.
                foreach (var k in result.Keys.ToList())
                    if (!clean.ContainsKey(k)) result[k] = 0;
            }
        }

        return ScoringService.Rebalance(result);
    }

    [KernelFunction, Description("Merge partial weight overrides into current session (or defaults), rebalance, optionally persist; returns the full effective weights as JSON.")]
    public Task<string> SetWeights(
        [Description("Partial overrides JSON, e.g. { \"EffectiveCoreUtilization\": 0.30 }")] string? overridesJson = null,
        [Description("If true, scale non-overridden weights to keep total = 1.0.")] bool normalizeRest = true,
        [Description("Session key for persistence; if omitted, the ambient conversation id is used when a store exists.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var overrides = string.IsNullOrWhiteSpace(overridesJson)
            ? new Dictionary<string, double?>()
            : (SafeDeserialize<Dictionary<string, double?>>(overridesJson) ?? new Dictionary<string, double?>());

        // Pick baseline: store (ambient or explicit) if present, else defaults
        var baseline = (_weightsStore is not null)
            ? _weightsStore.Get(ResolveSessionKey(sessionKey))
            : ScoringService.DefaultWeights();

        var merged = MergeAndRebalance(baseline, overrides, normalizeRest);

        if (_weightsStore is not null)
            _weightsStore.Set(ResolveSessionKey(sessionKey), merged);

        return Task.FromResult(JsonSerializer.Serialize(merged, JsonOpts));
    }

    [KernelFunction, Description("Get current effective weights for a session as JSON (defaults if no store).")]
    public Task<string> GetWeights(
        [Description("Session key; if omitted, ambient conversation id is used when a store exists.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var w = (_weightsStore is not null)
            ? _weightsStore.Get(ResolveSessionKey(sessionKey))
            : ScoringService.DefaultWeights();

        return Task.FromResult(JsonSerializer.Serialize(w, JsonOpts));
    }

    [KernelFunction, Description("Reset weights for a session and return defaults as JSON.")]
    public Task<string> ResetWeights(
        [Description("Session key to reset; if omitted, ambient conversation id is used.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        _weightsStore?.Reset(ResolveSessionKey(sessionKey));
        var w = ScoringService.DefaultWeights();
        return Task.FromResult(JsonSerializer.Serialize(w, JsonOpts));
    }

    [KernelFunction, Description("Score Top-N with optional *partial* weight overrides (server merges & normalizes the rest).")]
    public async Task<object> ScoreTopNWithOverrides(
        [Description("How many results to return.")] int topN = 10,
        [Description("Partial overrides JSON, e.g. { \"EffectiveCoreUtilization\": 0.30 }")] string? overridesJson = null,
        [Description("If true, scale remaining (non-overridden) weights.")] bool normalizeRest = true,
        [Description("Filter criteria JSON for pre-selection.")] string? criteriaJson = null,
        [Description("Optional field for per-group tops (e.g., \"Region\" or \"DataCenter\").")] string? groupBy = null,
        [Description("Paging page number (1-based).")] int page = 1,
        [Description("Page size.")] int pageSize = 25,
        [Description("Session key for persistence (optional; ambient used if omitted).")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        // Derive effective weights
        var baseline = (_weightsStore is not null)
            ? _weightsStore.Get(ResolveSessionKey(sessionKey))
            : ScoringService.DefaultWeights();

        var overrides = string.IsNullOrWhiteSpace(overridesJson)
            ? new Dictionary<string, double?>()
            : (SafeDeserialize<Dictionary<string, double?>>(overridesJson) ?? new Dictionary<string, double?>());

        var effective = MergeAndRebalance(baseline, overrides, normalizeRest);

        if (_weightsStore is not null)
            _weightsStore.Set(ResolveSessionKey(sessionKey), effective);

        // Reuse the existing ScoreTopN path with the finalized vector
        return await ScoreTopN(
            topN: topN,
            weightsJson: JsonSerializer.Serialize(effective, JsonOpts),
            criteriaJson: criteriaJson,
            groupBy: groupBy,
            page: page,
            pageSize: pageSize,
            sessionKey: sessionKey,
            ct: ct);
    }

    // -----------------------------------------------------------------------------
    //  Ranking (Top-N)
    // -----------------------------------------------------------------------------

    public sealed record ScoreTopNRequest(
        int TopN,
        string? WeightsJson,
        string? CriteriaJson,
        string? GroupBy,
        int Page,
        int PageSize,
        string? SessionKey);

    [KernelFunction, Description("Top-N by score with PRE-FILTERING (use criteriaJson; do not post-filter results).")]
    public async Task<object> ScoreTopN(
        [Description("Number of results to select after ranking (<=0 means all).")] int topN = 10,
        [Description("Optional weights JSON; if null, uses persisted (ambient/explicit) or defaults.")] string? weightsJson = null,
        [Description("Optional FilterCriteria JSON (And/Or/Not).")] string? criteriaJson = null,
        [Description("Optional field for per-group tops (e.g., \"Region\" or \"DataCenter\").")] string? groupBy = null,
        [Description("Page number (1-based).")] int page = 1,
        [Description("Page size.")] int pageSize = 25,
        [Description("Optional sessionKey to re-use persisted weights (ambient if omitted).")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        // 1) Weights
        var weights = ResolveWeights(weightsJson, sessionKey);

        // 2) Data + filter (PRE-FILTER BEFORE SCORING to preserve N and allow refill)
        var rows = await _data.GetClusterRowDataAsync(ct);
        var criteria = ParseCriteria(criteriaJson);
        rows = _filter.Apply(rows, criteria).ToList();

        // 3) Score (and ORDER BY score DESC before taking N)
        var scored = _scoring.Score(rows, weights).ToList();

        // 4) Grouped or flat
        if (!string.IsNullOrWhiteSpace(groupBy)
            && ClusterRowAccessors.TryGet(groupBy!, out var acc))
        {
            var grouped = scored
                .GroupBy(s => acc.Getter(s.Row)?.ToString() ?? "(unknown)", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var orderedInGroup = g.OrderByDescending(s => s.Score);
                    var take = (topN > 0 ? orderedInGroup.Take(topN) : orderedInGroup);
                    var items = take
                        .Select(s => _scoring.ToResultItem(s))
                        .ToList();
                    return new { Group = g.Key, Count = items.Count, Items = items };
                })
                .ToList();

            return new
            {
                Kind = "GroupedTopN",
                GroupBy = groupBy,
                Groups = grouped
            };
        }
        else
        {
            var ordered = scored.OrderByDescending(s => s.Score);
            var topSeq = (topN > 0 ? ordered.Take(topN) : ordered).ToList();

            // paging (applied to the already-top set)
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 25;
            var total = topSeq.Count;
            var pageItems = topSeq.Skip((page - 1) * pageSize).Take(pageSize)
                                  .Select(s => _scoring.ToResultItem(s))
                                  .ToList();

            return new
            {
                Kind = "TopN",
                Total = total,
                Page = page,
                PageSize = pageSize,
                Items = pageItems
            };
        }
    }

    // -----------------------------------------------------------------------------
    //  Explain & compare
    // -----------------------------------------------------------------------------

    [KernelFunction, Description("Explain a single cluster's score (top drivers, contributions).")]
    public async Task<object> Explain(
        [Description("Cluster id/name to explain.")] string clusterId,
        [Description("Optional weights JSON.")] string? weightsJson = null,
        [Description("Optional sessionKey to use persisted weights (ambient if omitted).")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var weights = ResolveWeights(weightsJson, sessionKey);
        var all = await _scoring.ScoreAllAsync(weights, options: null, ct);
        var x = all.Rankings.FirstOrDefault(r => string.Equals(r.Cluster, clusterId, StringComparison.OrdinalIgnoreCase));
        if (x is null) return new { Error = $"Cluster '{clusterId}' not found." };

        return new
        {
            Kind = "Explain",
            Cluster = x.Cluster,
            Score = x.Score,
            Factors = x.Factors.OrderByDescending(f => Math.Abs(f.Contribution)).ToList(),
            Weights = all.AppliedWeights
        };
    }

    [KernelFunction, Description("Compare two or more clusters side-by-side.")]
    public async Task<object> Compare(
        [Description("JSON array of cluster ids/names, e.g. [\"XYZ\",\"ABC\"].")] string clusterIdsJson,
        [Description("Optional weights JSON.")] string? weightsJson = null,
        [Description("Optional sessionKey to use persisted weights (ambient if omitted).")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var ids = SafeDeserialize<List<string>>(clusterIdsJson) ?? new List<string>();
        if (ids.Count == 0) return new { Error = "No cluster ids provided." };

        var weights = ResolveWeights(weightsJson, sessionKey);
        var all = await _scoring.ScoreAllAsync(weights, options: null, ct);

        var selected = all.Rankings
            .Where(r => ids.Any(id => string.Equals(id, r.Cluster, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Score)
            .Select(r => new
            {
                r.Cluster,
                r.Score,
                TopFactors = r.Factors.OrderByDescending(f => Math.Abs(f.Contribution)).Take(5).ToList()
            })
            .ToList();

        return new
        {
            Kind = "Compare",
            Count = selected.Count,
            Items = selected,
            Weights = all.AppliedWeights
        };
    }

    // -----------------------------------------------------------------------------
    //  Sensitivity & bulk what-if
    // -----------------------------------------------------------------------------

    [KernelFunction, Description("Apply edits to one cluster (what-if) and show before/after scores + delta.")]
    public async Task<object> Sensitivity(
        [Description("Cluster id/name to edit.")] string clusterId,
        [Description("Edits JSON: e.g. {\"CoreUtilization\":0.25,\"OutOfServiceNodes\":0}")] string editsJson,
        [Description("Optional weights JSON.")] string? weightsJson = null,
        [Description("Optional sessionKey to use persisted weights (ambient if omitted).")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var row = await _data.GetClusterRowDetailsAsync(clusterId, ct);
        if (row is null) return new { Error = $"Cluster '{clusterId}' not found." };

        var weights = ResolveWeights(weightsJson, sessionKey);
        var before = _scoring.Score(new[] { row }, weights).Single();

        var edited = CloneRow(row);
        ApplyEdits(edited, editsJson);

        var after = _scoring.Score(new[] { edited }, weights).Single();

        return new
        {
            Kind = "Sensitivity",
            Cluster = before.Row.Cluster ?? before.Row.ClusterId ?? "(unknown)",
            Before = new { Score = before.Score, Explain = _scoring.ToExplain(before) },
            After = new { Score = after.Score, Explain = _scoring.ToExplain(after) },
            Delta = after.Score - before.Score,
            Edits = SafeDeserialize<Dictionary<string, object?>>(editsJson)
        };
    }

    [KernelFunction, Description("Bulk what-if: apply edits to a filtered cohort; return before/after and deltas.")]
    public async Task<object> BulkWhatIf(
        [Description("FilterCriteria JSON selecting the cohort.")] string criteriaJson,
        [Description("Edits JSON to apply to each row.")] string editsJson,
        [Description("Optional weights JSON.")] string? weightsJson = null,
        [Description("Include before/after and delta if true; otherwise only after.")] bool showDiff = true,
        [Description("Optional sessionKey to use persisted weights (ambient if omitted).")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var weights = ResolveWeights(weightsJson, sessionKey);
        var rows = await _data.GetClusterRowDataAsync(ct);

        var criteria = ParseCriteria(criteriaJson);
        var cohort = _filter.Apply(rows, criteria).ToList();

        if (cohort.Count == 0) return new { Kind = "BulkWhatIf", Count = 0, Items = Array.Empty<object>() };

        var before = _scoring.Score(cohort, weights);

        // Apply edits row-by-row (clone to avoid mutating the original)
        var editedRows = cohort.Select(CloneRow).ToList();
        foreach (var r in editedRows) ApplyEdits(r, editsJson);

        var after = _scoring.Score(editedRows, weights);

        var merged = before.Zip(after, (b, a) => new
        {
            Cluster = a.Row.Cluster ?? a.Row.ClusterId ?? "(unknown)",
            BeforeScore = b.Score,
            AfterScore = a.Score,
            Delta = a.Score - b.Score
        })
        .OrderByDescending(x => x.AfterScore)
        .ToList();

        if (!showDiff)
        {
            return new
            {
                Kind = "BulkWhatIf",
                Count = merged.Count,
                Items = merged.Select(m => new { m.Cluster, Score = m.AfterScore })
            };
        }

        return new
        {
            Kind = "BulkWhatIf",
            Count = merged.Count,
            Items = merged
        };
    }

    // -----------------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------------

    private ScoringService.WeightConfig ResolveWeights(string? weightsJson, string? sessionKey)
    {
        if (!string.IsNullOrWhiteSpace(weightsJson))
            return ParseWeights(weightsJson);

        if (_weightsStore is not null)
            return _weightsStore.Get(ResolveSessionKey(sessionKey));

        return ScoringService.DefaultWeights();
    }

    private static ScoringService.WeightConfig ParseWeights(string json)
    {
        var dict = SafeDeserialize<Dictionary<string, double>>(json)
                   ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        return new ScoringService.WeightConfig(dict);
    }

    private static FilterCriteria ParseCriteria(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new FilterCriteria()
            : (SafeDeserialize<FilterCriteria>(json) ?? new FilterCriteria());

    private static T? SafeDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return default; }
    }

    private static ClusterRow CloneRow(ClusterRow src)
    {
        var dest = new ClusterRow();
        foreach (var p in typeof(ClusterRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            p.SetValue(dest, p.GetValue(src));
        }
        return dest;
    }

    private static void ApplyEdits(ClusterRow row, string editsJson)
    {
        var edits = SafeDeserialize<Dictionary<string, object?>>(editsJson);
        if (edits is null || edits.Count == 0) return;

        foreach (var (key, val) in edits)
        {
            var p = typeof(ClusterRow).GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p is null || !p.CanWrite) continue;

            try
            {
                object? converted = val;
                if (val is JsonElement je) converted = ConvertJsonElement(je, p.PropertyType);
                else if (val is string s && p.PropertyType == typeof(double?))
                {
                    if (double.TryParse(s, out var d)) converted = d;
                }
                else if (val is string s2 && p.PropertyType == typeof(int?))
                {
                    if (int.TryParse(s2, out var i)) converted = i;
                }
                else if (val is string s3 && p.PropertyType == typeof(bool?))
                {
                    if (bool.TryParse(s3, out var b)) converted = b;
                }

                // Handle nullable value types
                if (converted is not null
                    && p.PropertyType.IsGenericType
                    && p.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var inner = Nullable.GetUnderlyingType(p.PropertyType)!;
                    converted = System.Convert.ChangeType(converted, inner);
                }

                p.SetValue(row, converted);
            }
            catch
            {
                // ignore bad edit conversion
            }
        }
    }

    private static object? ConvertJsonElement(JsonElement je, Type target)
    {
        if (je.ValueKind == JsonValueKind.Null) return null;

        // Reference types
        if (target == typeof(string)) return je.ToString();

        // Numeric
        if (target == typeof(double) || target == typeof(double?))
        {
            return je.ValueKind switch
            {
                JsonValueKind.Number => je.TryGetDouble(out var d) ? d : (double?)null,
                JsonValueKind.String => double.TryParse(je.GetString(), out var d2) ? d2 : (double?)null,
                _ => (double?)null
            };
        }

        if (target == typeof(int) || target == typeof(int?))
        {
            return je.ValueKind switch
            {
                JsonValueKind.Number => je.TryGetInt32(out var i) ? i : (int?)null,
                JsonValueKind.String => int.TryParse(je.GetString(), out var i2) ? i2 : (int?)null,
                _ => (int?)null
            };
        }

        if (target == typeof(bool) || target == typeof(bool?))
        {
            return je.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(je.GetString(), out var b) ? b : (bool?)null,
                _ => (bool?)null
            };
        }

        // Fallback: try changing type from the raw text
        var asText = je.ToString();
        try
        {
            if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(Nullable<>))
                target = Nullable.GetUnderlyingType(target)!;
            return System.Convert.ChangeType(asText, target);
        }
        catch
        {
            return null;
        }
    }
}
