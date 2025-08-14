#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using MyM365AgentDecommision.Bot.Interfaces;
using MyM365AgentDecommision.Bot.Services;

namespace MyM365AgentDecommision.Bot.Plugins
{
    /// <summary>
    /// Scoring plugin with hybrid weight parsing; optional filter via Criteria JSON.
    /// </summary>
    public sealed class ScoringPlugin
    {
        private readonly ScoringService _svc;
        private readonly IClusterDataProvider _dataProvider;
        private readonly HybridParsingService _hybrid;

        public ScoringPlugin(ScoringService svc, IClusterDataProvider dataProvider, HybridParsingService hybrid)
        {
            _svc = svc ?? throw new ArgumentNullException(nameof(svc));
            _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
            _hybrid = hybrid ?? throw new ArgumentNullException(nameof(hybrid));
        }

        [KernelFunction]
        public string ListFeatures()
        {
            var feats = _svc.GetFeatureCatalog();
            return string.Join(", ", feats.Select(f => f.Name));
        }

        /// <summary>NL/JSON weights → {weightsRaw, weightsNormalized, primaryFactor} using hybrid fallback.</summary>
        [KernelFunction]
        public async Task<string> ExtractWeightsAsync(string text, CancellationToken ct = default)
        {
            var (raw, normalized) = await _hybrid.ParseWeightsHybridAsync(text, ct);
            var primary = normalized.OrderByDescending(kv => kv.Value).First().Key;

            var payload = new { weightsRaw = raw, weightsNormalized = normalized, primaryFactor = primary };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Score and rank clusters. Supports hybrid weights and optional filterCriteria (engine JSON).
        /// Deterministic tie-breaks by primary factor's raw value (implemented in ScoringService).
        /// </summary>
        [KernelFunction]
        public async Task<string> ScoreTopNAsync(
            int topN = 10,
            string? weights = null,
            string? filterCriteria = null,
            CancellationToken ct = default)
        {
            // 1) Parse weights (hybrid)
            var (weightsRaw, weightsNormalized) = await _hybrid.ParseWeightsHybridAsync(weights, ct);

            // 2) Scoring options: disable winsorization when exactly one positive-weight factor is used
            var options = (weightsRaw.Count(kv => kv.Value > 0) == 1)
                ? new ScoringService.ScoringOptions { Winsorize = false }
                : new ScoringService.ScoringOptions();

            // 3) Score everything once
            var scoreResult = await _svc.ScoreAllAsync(weightsRaw, options, ct);

            // 4) Apply optional filter plan using the engine
            var rows = await _dataProvider.GetClusterRowDataAsync(ct);
            var clusterLookup = rows.ToDictionary(r => r.Cluster ?? r.ClusterId ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            System.Collections.Generic.HashSet<string>? allowed = null;
            if (!string.IsNullOrWhiteSpace(filterCriteria))
            {
                try
                {
                    var crit = JsonSerializer.Deserialize<ClusterFilterEngine.Criteria>(
                        filterCriteria,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ClusterFilterEngine.Criteria();

                    var filtered = ClusterFilterEngine.Apply(rows, crit);
                    allowed = filtered
                        .Select(r => r.Cluster ?? r.ClusterId ?? string.Empty)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch { /* ignore bad criteria, proceed unfiltered */ }
            }

            // 5) Primary factor for deterministic tie-break
            var primary = weightsNormalized.OrderByDescending(kv => kv.Value).First().Key;

            double TieKey(ScoringService.ScoreRowBreakdown r)
            {
                var f = r.Factors.FirstOrDefault(x => string.Equals(x.Property, primary, StringComparison.OrdinalIgnoreCase));
                if (f is null || f.Raw is null) return double.NaN;
                return f.Inverted ? -f.Raw.Value : f.Raw.Value;
            }

            // 6) Order, filter, take N
            var ranked = scoreResult.Rankings
                .Where(r => allowed is null || allowed.Contains(r.Cluster))
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => TieKey(r))
                .Take(topN)
                .ToList();

            // 7) Response
            var responseObj = new
            {
                clusters = ranked.Select((c, idx) =>
                {
                    clusterLookup.TryGetValue(c.Cluster, out var row);
                    return new
                    {
                        rank = idx + 1,
                        cluster = c.Cluster,
                        score = Math.Round(c.Score, 4),
                        details = row is null ? null : new
                        {
                            region = row.Region,
                            availabilityZone = row.AvailabilityZone,
                            dataCenter = row.DataCenter,
                            ageYears = row.ClusterAgeYears,
                            coreUtilization = row.CoreUtilization,
                            totalCores = row.TotalPhysicalCores,
                            usedCores = row.UsedCores,
                            totalNodes = row.TotalNodes,
                            outOfServiceNodes = row.OutOfServiceNodes,
                            hasSQL = row.HasSQL,
                            hasWARP = row.HasWARP,
                            hasSLB = row.HasSLB,
                            isHotRegion = row.IsHotRegion
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
                }).ToList(),
                summary = new
                {
                    totalConsidered = scoreResult.Rankings.Count,
                    returned = ranked.Count,
                    topNRequested = topN,
                    primaryFactor = primary,
                    weightsRaw,
                    weightsNormalized
                }
            };

            return JsonSerializer.Serialize(responseObj, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
