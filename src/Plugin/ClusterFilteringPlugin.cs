#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using MyM365AgentDecommision.Bot.Interfaces;  // IClusterDataProvider
using MyM365AgentDecommision.Bot.Models;      // ClusterRow
using MyM365AgentDecommision.Bot.Services;    // ScoringService, HybridParsingService, JsonSafe

namespace MyM365AgentDecommision.Bot.Plugins
{
    /// <summary>
    /// Filtering + scoring plugin (hybrid NL parsing inside service).
    /// IMPORTANT: In Filter+Score path we ignore Criteria.Take/Sort and only use it for filtering,
    /// then we score and apply the final topN — so excludes always refill the list.
    /// </summary>
    public sealed class ClusterFilteringPlugin
    {
        private readonly IClusterDataProvider _data;
        private readonly ILogger<ClusterFilteringPlugin>? _log;
        private readonly ScoringService _scoringService;
        private readonly HybridParsingService _hybrid;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public ClusterFilteringPlugin(
            IClusterDataProvider data,
            ScoringService scoringService,
            HybridParsingService hybrid,
            ILogger<ClusterFilteringPlugin>? log = null)
        {
            _data = data;
            _scoringService = scoringService;
            _hybrid = hybrid;
            _log = log;
        }

        // -------- DTOs --------
        public sealed record ErrorDto(string Error);
        public sealed record FieldInfo(string Name, string Kind);
        public sealed record FilterResponse(int Total, int Returned, IEnumerable<ClusterRow> Items);
        public sealed record PlanResponse(string Mode, int Total, int Returned, IEnumerable<ClusterRow> Items);

        // -------- Discovery --------

        [KernelFunction, Description("List filterable fields and types supported by the engine.")]
        public string ListFilterableFields()
        {
            var fields = ClusterFilterEngine.ListSupportedFields()
                .Select(t => new FieldInfo(t.Field, t.Kind))
                .ToList();

            return JsonSerializer.Serialize(fields, JsonOpts);
        }

        [KernelFunction, Description("Return an example Criteria JSON template.")]
        public string CriteriaTemplate()
        {
            var crit = new ClusterFilterEngine.Criteria { SortBy = "ClusterAgeYears", SortDescending = true, Take = 10 };
            return JsonSerializer.Serialize(crit, JsonOpts);
        }

        // -------- Hybrid parsing wrappers --------

        [KernelFunction, Description("Parse natural-language filters with hybrid fallback into Criteria JSON.")]
        public async Task<string> ExtractCriteriaAsync(
            [Description("E.g. 'exclude eastUS2, only data center BN4, util<10%, sort by age desc, top 10'")]
            string text,
            CancellationToken ct = default)
        {
            var criteria = await _hybrid.ParseCriteriaHybridAsync(text, ct);
            return JsonSerializer.Serialize(criteria, JsonOpts);
        }

        [KernelFunction, Description("Parse weight instructions with hybrid fallback and return {weightsRaw, weightsNormalized, primaryFactor}.")]
        public async Task<string> ExtractWeightsAsync(
            [Description("User text, JSON, or k=v pairs describing weights")]
            string text,
            CancellationToken ct = default)
        {
            var (raw, normalized) = await _hybrid.ParseWeightsHybridAsync(text, ct);
            var primary = normalized.OrderByDescending(kv => kv.Value).First().Key;
            var payload = new { weightsRaw = raw, weightsNormalized = normalized, primaryFactor = primary };
            return JsonSerializer.Serialize(payload, JsonOpts);
        }

        // -------- Pure filtering (honors criteria.Sort/Take) --------

        [KernelFunction, Description("Filter clusters by one Criteria JSON. Returns {total, returned, items}.")]
        public async Task<string> FilterByCriteriaJsonAsync(
            [Description("JSON encoding of Criteria")] string criteriaJson,
            CancellationToken ct = default)
        {
            try
            {
                var rows = await _data.GetClusterRowDataAsync(ct);

                if (!JsonSafe.TryDeserialize<ClusterFilterEngine.Criteria>(criteriaJson, out var criteria, out var err) || criteria is null)
                    return JsonSerializer.Serialize(new ErrorDto($"Invalid criteria JSON: {err ?? "unknown"}"), JsonOpts);

                var result = ClusterFilterEngine.Apply(rows, criteria);
                var payload = new FilterResponse(rows.Count, result.Count, result);
                return JsonSerializer.Serialize(payload, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                return JsonSerializer.Serialize(new ErrorDto("Operation cancelled."), JsonOpts);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "FilterByCriteriaJsonAsync failed");
                return JsonSerializer.Serialize(new ErrorDto($"Unhandled error: {ex.Message}"), JsonOpts);
            }
        }

        [KernelFunction, Description("Run a multi-query plan (Union/Intersect of multiple Criteria). Returns {mode, total, returned, items}.")]
        public async Task<string> FilterByMultiPlanJsonAsync(
            [Description("JSON encoding of ClusterFilterEngine.MultiQueryPlan")] string planJson,
            CancellationToken ct = default)
        {
            try
            {
                var rows = await _data.GetClusterRowDataAsync(ct);

                if (!JsonSafe.TryDeserialize<ClusterFilterEngine.MultiQueryPlan>(planJson, out var plan, out var err)
                    || plan is null || plan.Items is null || plan.Items.Count == 0)
                    return JsonSerializer.Serialize(new ErrorDto($"Invalid plan JSON: {err ?? "Plan.Items is empty."}"), JsonOpts);

                var result = ClusterFilterEngine.ApplyMany(rows, plan);
                var payload = new PlanResponse(plan.Mode, rows.Count, result.Count, result);
                return JsonSerializer.Serialize(payload, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                return JsonSerializer.Serialize(new ErrorDto("Operation cancelled."), JsonOpts);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "FilterByMultiPlanJsonAsync failed");
                return JsonSerializer.Serialize(new ErrorDto($"Unhandled error: {ex.Message}"), JsonOpts);
            }
        }

        // -------- Filter + Score (IGNORE criteria.Take/Sort) --------

        [KernelFunction, Description("Filter and score clusters with optional NL filters and weights. Returns top N candidates (always refilled after excludes).")]
        public async Task<string> FilterAndScoreAsync(
            [Description("Optional engine Criteria JSON for filtering (ranking fields ignored)")] string? filterCriteria = null,
            [Description("Optional natural-language filter (e.g., 'exclude eastUS2, only BN4, older than 7 years')")] string? nlFilter = null,
            [Description("Optional weight configuration (JSON, 'k=v' pairs, or natural language)")] string? weightConfig = null,
            [Description("Number of top-scored clusters to return")] int topN = 10,
            CancellationToken ct = default)
        {
            try
            {
                var allRows = await _data.GetClusterRowDataAsync(ct);

                // 1) Build a FILTER-ONLY criteria (strip Sort/Take) from JSON criteria if provided
                IReadOnlyList<ClusterRow> filtered = allRows;
                if (!string.IsNullOrWhiteSpace(filterCriteria))
                {
                    if (!JsonSafe.TryDeserialize<ClusterFilterEngine.Criteria>(filterCriteria, out var crit, out var err) || crit is null)
                        return JsonSerializer.Serialize(new ErrorDto($"Invalid filter criteria JSON: {err ?? "unknown"}"), JsonOpts);

                    var filterOnly = ToFilterOnlyCriteria(crit);
                    filtered = ClusterFilterEngine.Apply(allRows, filterOnly);
                }

                // 2) Merge NL filter (also FILTER-ONLY), intersect with current filtered set
                if (!string.IsNullOrWhiteSpace(nlFilter))
                {
                    var extra = await _hybrid.ParseCriteriaHybridAsync(nlFilter, ct);
                    var extraOnly = ToFilterOnlyCriteria(extra);

                    // Apply extra filter to the full set first, then intersect IDs (ensures proper filtering semantics)
                    var extraFiltered = ClusterFilterEngine.Apply(allRows, extraOnly);

                    var allowedIds = new HashSet<string>(
                        extraFiltered.Select(r => r.Cluster ?? r.ClusterId ?? string.Empty),
                        StringComparer.OrdinalIgnoreCase);

                    filtered = filtered.Where(r => allowedIds.Contains(r.Cluster ?? r.ClusterId ?? string.Empty)).ToList();
                }

                // 3) Parse weights, score ALL rows (for consistent normalization), then pick topN among 'filtered'
                var (weightsRaw, weightsNormalized) = await _hybrid.ParseWeightsHybridAsync(weightConfig, ct);
                var options = (weightsRaw.Count(kv => kv.Value > 0) == 1)
                    ? new ScoringService.ScoringOptions { Winsorize = false }
                    : new ScoringService.ScoringOptions();

                var scoreResult = await _scoringService.ScoreAllAsync(weightsRaw, options, ct);

                var allowed = new HashSet<string>(
                    filtered.Select(r => r.Cluster ?? r.ClusterId ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

                var primary = weightsNormalized.OrderByDescending(kv => kv.Value).First().Key;

                double TieKey(ScoringService.ScoreRowBreakdown r)
                {
                    var f = r.Factors.FirstOrDefault(x => string.Equals(x.Property, primary, StringComparison.OrdinalIgnoreCase));
                    if (f is null || f.Raw is null) return double.NaN;
                    return f.Inverted ? -f.Raw.Value : f.Raw.Value;
                }

                var ranked = scoreResult.Rankings
                    .Where(r => allowed.Contains(r.Cluster))
                    .OrderByDescending(r => r.Score)
                    .ThenByDescending(r => TieKey(r))
                    .Take(Math.Min(topN, allowed.Count))
                    .ToList();

                var rowLookup = allRows.ToDictionary(r => r.Cluster ?? r.ClusterId ?? string.Empty, StringComparer.OrdinalIgnoreCase);

                var result = new
                {
                    Total = allRows.Count,
                    Filtered = filtered.Count,
                    Returned = ranked.Count,
                    PrimaryFactor = primary,
                    CustomWeightsApplied = true,
                    weightsRaw,
                    weightsNormalized,
                    Items = ranked.Select((c, idx) =>
                    {
                        rowLookup.TryGetValue(c.Cluster, out var r);
                        return new
                        {
                            rank = idx + 1,
                            cluster = c.Cluster,
                            score = Math.Round(c.Score, 4),
                            details = r is null ? null : new
                            {
                                region = r.Region,
                                availabilityZone = r.AvailabilityZone,
                                dataCenter = r.DataCenter,
                                ageYears = r.ClusterAgeYears,
                                coreUtilization = r.CoreUtilization,
                                totalCores = r.TotalPhysicalCores,
                                usedCores = r.UsedCores,
                                totalNodes = r.TotalNodes,
                                outOfServiceNodes = r.OutOfServiceNodes,
                                hasSQL = r.HasSQL,
                                hasSLB = r.HasSLB,
                                hasWARP = r.HasWARP,
                                isHotRegion = r.IsHotRegion
                            },
                            factors = c.Factors
                                .Where(f => f.Contribution > 0.0001)
                                .OrderByDescending(f => f.Contribution)
                                .Select(f => new
                                {
                                    property = f.Property,
                                    rawValue = f.Raw,
                                    normalizedScore = Math.Round(f.Normalized, 4),
                                    weight = Math.Round(f.Weight, 4),
                                    contribution = Math.Round(f.Contribution, 4),
                                    inverted = f.Inverted,
                                    description = ScoringText.GetFactorDescription(f.Property)
                                })
                                .Take(12)
                                .ToList()
                        };
                    }).ToList()
                };

                return JsonSerializer.Serialize(result, JsonOpts);
            }
            catch (OperationCanceledException)
            {
                return JsonSerializer.Serialize(new ErrorDto("Operation cancelled."), JsonOpts);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "FilterAndScoreAsync failed");
                return JsonSerializer.Serialize(new ErrorDto($"Unhandled error: {ex.Message}"), JsonOpts);
            }
        }

        // ------- local: turn criteria into FILTER-ONLY (ignore Sort/Take) -------
        private static ClusterFilterEngine.Criteria ToFilterOnlyCriteria(ClusterFilterEngine.Criteria c)
        {
            // Clone with only filtering pieces; use a massive Take to avoid truncation.
            return new ClusterFilterEngine.Criteria
            {
                StringIn = c.StringIn,
                StringNotIn = c.StringNotIn,
                DoubleRanges = c.DoubleRanges,
                // ranking fields neutralized:
                SortBy = "ClusterAgeYears",     // arbitrary placeholder; Apply may require non-null
                SortDescending = true,
                Take = int.MaxValue             // prevent early slicing
            };
        }
    }
}
