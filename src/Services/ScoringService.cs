#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MyM365AgentDecommision.Bot.Interfaces;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// ScoringService v2 — capacity-aware, robust, and explainable.
    /// Key improvements over v1:
    /// 1) Size-aware signal: IdleCoreVolume = (1 - EffectiveCoreUtilization) * TotalPhysicalCores
    /// 2) Per-row reweighting: if a feature is missing on a row, redistribute its weight to present features
    /// 3) Per-feature winsorization overrides (e.g., OOSNodeRatio at 5/95 instead of global 2/98)
    /// 4) Optional rank (CDF) normalization for very spiky features
    /// 5) Cleaner defaults: remove flat/noisy features; prefer HotnessRank over IsHotRegion
    /// </summary>
    public class ScoringService
    {
        private readonly IClusterDataProvider _data;
        private readonly ILogger<ScoringService> _log;

        public ScoringService(IClusterDataProvider data, ILogger<ScoringService> log)
        {
            _data = data;
            _log = log;
        }

        // ------------------------------ DTOs -----------------------------------

        public sealed class WeightConfig : Dictionary<string, double>
        {
            public WeightConfig() : base(StringComparer.OrdinalIgnoreCase) { }
            public WeightConfig(IDictionary<string, double> init) : base(init, StringComparer.OrdinalIgnoreCase) { }
            public static WeightConfig Default() => DefaultWeights();
        }

        public sealed class ScoringOptions
        {
            /// <summary>Apply winsorization before min–max normalization.</summary>
            public bool Winsorize { get; init; } = true;

            /// <summary>Lower/upper quantiles used when winsorizing (0..1) when no per-feature override exists.</summary>
            public double LowerQuantile { get; init; } = 0.02;
            public double UpperQuantile { get; init; } = 0.98;

            /// <summary>Per-feature winsorization cutoffs. Example: { "OOSNodeRatio": (0.05, 0.95) }.</summary>
            public Dictionary<string, (double Lower, double Upper)>? PerFeatureQuantiles { get; init; }

            /// <summary>If true, redistribute the weight of missing features across present ones for each row.</summary>
            public bool PerRowReweightMissing { get; init; } = true;

            /// <summary>Keys to normalize by empirical CDF (rank) instead of min–max (after clamping to winsor bounds).</summary>
            public HashSet<string>? RankNormalizeKeys { get; init; }

            /// <summary>If true, distribute a small budget across all other numeric/derived fields not explicitly weighted.</summary>
            public bool IncludeAllNumeric { get; init; } = false;

            /// <summary>Budget to spread across auto-included fields (e.g., 0.10 = 10%).</summary>
            public double IncludeAllNumericBudget { get; init; } = 0.0;
        }

        public sealed record FactorContribution(
            string Property,
            double? Raw,
            double Normalized,
            bool Inverted,
            double Weight,
            double Contribution,
            double? MinSeen,
            double? MaxSeen,
            string? Note = null
        );

        public sealed record Scored(
            ClusterRow Row,
            double Score,
            IReadOnlyList<FactorContribution> Factors
        );

        public sealed record ResultItem(
            string Cluster,
            string? Region,
            string? DataCenter,
            double AgeYears,
            double UtilPct,
            double Score,
            bool Eligible
        );

        public sealed record FeatureInfo(
            string Name,
            string Kind,             // raw | derived
            string Unit,             // count/ratio | percent | bool | score
            bool HigherIsBetter
        );

        public sealed record ScoreRowBreakdown(
            string Cluster,
            double Score,
            IReadOnlyList<FactorContribution> Factors
        );

        public sealed record ScoreResult(
            IReadOnlyList<ScoreRowBreakdown> Rankings,
            WeightConfig AppliedWeights,
            IReadOnlyDictionary<string, (double? Min, double? Max)> NumericStats
        );

        // ------------------------- Reflection getters --------------------------

        private static readonly Dictionary<string, Func<ClusterRow, double?>> _doubleGet = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, int?>> _intGet = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, bool?>> _boolGet = new(StringComparer.OrdinalIgnoreCase);

        static ScoringService()
        {
            var t = typeof(ClusterRow);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType == typeof(double?))
                    _doubleGet[p.Name] = (ClusterRow r) => (double?)p.GetValue(r);
                else if (p.PropertyType == typeof(int?))
                    _intGet[p.Name] = (ClusterRow r) => (int?)p.GetValue(r);
                else if (p.PropertyType == typeof(bool?))
                    _boolGet[p.Name] = (ClusterRow r) => (bool?)p.GetValue(r);
            }
        }

        // -------------------------- Percent-like keys ------------------------

        private static readonly HashSet<string> PercentLike = new(StringComparer.OrdinalIgnoreCase)
        { "CoreUtilization", "VMDensity", "OutOfServicesPercentage" };

        private static double? CoerceToRatio01(string key, double? v)
        {
            if (!v.HasValue) return null;
            return (PercentLike.Contains(key) && v.Value > 1.0) ? v.Value / 100.0 : v.Value;
        }

        // ---------------------------- Derived features -------------------------

        private static readonly Dictionary<string, Func<ClusterRow, double?>> _derived =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["EffectiveCoreUtilization"] = r =>
                {
                    if (r.TotalPhysicalCores is > 0 && r.UsedCores is >= 0)
                        return r.UsedCores!.Value / r.TotalPhysicalCores!.Value;
                    return r.CoreUtilization;
                },

                // NEW: size-aware idle capacity (higher → more decom-worthy)
                ["IdleCoreVolume"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.UsedCores is >= 0)
                        ? (1.0 - (r.UsedCores!.Value / r.TotalPhysicalCores!.Value)) * r.TotalPhysicalCores!.Value
                        : (double?)null,

                // Stranding
                ["StrandedCoresRatio_DNG"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.StrandedCores_DNG is >= 0)
                        ? r.StrandedCores_DNG!.Value / r.TotalPhysicalCores!.Value : null,

                ["StrandedCoresRatio_TIP"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.StrandedCores_TIP is >= 0)
                        ? r.StrandedCores_TIP!.Value / r.TotalPhysicalCores!.Value : null,

                ["StrandedCoresRatio_32VMs"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.StrandedCores_32VMs is >= 0)
                        ? r.StrandedCores_32VMs!.Value / r.TotalPhysicalCores!.Value : null,

                ["StrandedCoresTotal"] = r =>
                    new [] { r.StrandedCores_DNG, r.StrandedCores_TIP, r.StrandedCores_32VMs }
                        .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Sum(),

                // Node health
                ["OOSNodeRatio"] = r =>
                    (r.TotalNodes is > 0 && r.OutOfServiceNodes is >= 0)
                        ? r.OutOfServiceNodes!.Value / (double)r.TotalNodes!.Value : null,

                ["HealthyNodeRatio"] = r =>
                    (r.TotalNodes is > 0 && r.OutOfServiceNodes is >= 0)
                        ? 1.0 - (r.OutOfServiceNodes!.Value / (double)r.TotalNodes!.Value) : null,

                ["DNGNodeRatio"] = r =>
                    (r.TotalNodes is > 0 && r.DNG_Nodes is >= 0)
                        ? r.DNG_Nodes!.Value / (double)r.TotalNodes!.Value : null,

                // Hotness (prefer numeric rank over bool)
                ["HotnessRank"] = r => r.RegionHotnessPriority is int v ? (double)v : (double?)null,

                // Workload mix
                ["SQL_Ratio"] = r =>
                    (r.VMCount is > 0 && r.VMCount_SQL is >= 0) ? r.VMCount_SQL!.Value / (double)r.VMCount!.Value : null,

                ["NonSpannable_Ratio"] = r =>
                    (r.UsedCores_NonSQL is > 0 && r.UsedCores_NonSQL_NonSpannable is >= 0)
                        ? r.UsedCores_NonSQL_NonSpannable!.Value / r.UsedCores_NonSQL!.Value : null,

                ["SpannableUtilizationRatio"] = r =>
                    (r.UsedCores is > 0 && r.UsedCores_NonSQL_Spannable is >= 0)
                        ? r.UsedCores_NonSQL_Spannable!.Value / r.UsedCores!.Value : null
            };

        // ---------------------------- Directions -------------------------------

        private static readonly Dictionary<string, bool> Direction = new(StringComparer.OrdinalIgnoreCase)
        {
            // Age & timeline
            ["ClusterAgeYears"] = true,
            ["DecommissionYearsRemaining"] = false,

            // Utilization / density (prefer lower)
            ["EffectiveCoreUtilization"] = false,
            ["CoreUtilization"] = false,
            ["VMDensity"] = false,

            // Size-aware idle capacity (prefer higher)
            ["IdleCoreVolume"] = true,

            // Health / OOS / Stranding
            ["OOSNodeRatio"] = true,
            ["HealthyNodeRatio"] = false,
            ["DNGNodeRatio"] = true,
            ["StrandedCoresRatio_DNG"] = true,
            ["StrandedCoresRatio_TIP"] = true,
            ["StrandedCoresRatio_32VMs"] = true,
            ["StrandedCoresTotal"] = true,

            // Region health (lower health = better)
            ["RegionHealthScore"] = false,

            // Hot regions (penalize)
            ["IsHotRegion"] = false,
            ["HotnessRank"] = false,

            // Stickiness / special workloads present → penalize
            ["HasSQL"] = false,
            ["HasPlatformTenant"] = false,
            ["HasSLB"] = false,
            ["HasWARP"] = false,
            ["HasUDGreaterThan10"] = false,
            ["HasInstancesGreaterThan10"] = false,
            ["IsTargetMP"] = false,

            // Mix ratios (more SQL/non-spannable often stickier)
            ["SQL_Ratio"] = false,
            ["NonSpannable_Ratio"] = false,
            ["SpannableUtilizationRatio"] = false
        };

        // ----------------------------- Default Weights -------------------------

        /// <summary>
        /// Opinionated v2 defaults (sum to 1). Capacity-aware and robust to flat signals.
        /// </summary>
        public static WeightConfig DefaultWeights()
        {
            var w = new WeightConfig
            {
                ["ClusterAgeYears"] = 0.2126,
                ["EffectiveCoreUtilization"] = 0.2415,
                ["IdleCoreVolume"] = 0.1159,
                ["RegionHealthScore"] = 0.0773,
                ["OOSNodeRatio"] = 0.1449,
                ["StrandedCoresRatio_DNG"] = 0.0580,
                ["StrandedCoresRatio_TIP"] = 0.0193,
                ["DecommissionYearsRemaining"] = 0.0483,
                ["HotnessRank"] = 0.0193,
                ["HasSQL"] = 0.0290,
                ["HasPlatformTenant"] = 0.0145,
                ["HasWARP"] = 0.0145,
                ["HasSLB"] = 0.0048
            };

            return Rebalance(w);
        }

        public static WeightConfig Rebalance(WeightConfig w)
        {
            var sum = Math.Max(1e-9, w.Values.Where(v => v > 0).Sum());
            var keys = w.Keys.ToList();
            foreach (var k in keys) w[k] = Math.Max(0, w[k]) / sum;
            return w;
        }

        // ------------------------------- Catalog --------------------------------

        public IReadOnlyList<FeatureInfo> GetFeatureCatalog()
        {
            var list = new List<FeatureInfo>();

            foreach (var k in _doubleGet.Keys.Concat(_intGet.Keys))
            {
                list.Add(new FeatureInfo(
                    Name: k,
                    Kind: "raw",
                    Unit: PercentLike.Contains(k) ? "percent" : "count/ratio",
                    HigherIsBetter: Direction.TryGetValue(k, out var hb) ? hb : true));
            }

            foreach (var k in _boolGet.Keys)
            {
                list.Add(new FeatureInfo(
                    Name: k,
                    Kind: "raw",
                    Unit: "bool",
                    HigherIsBetter: Direction.TryGetValue(k, out var hb) ? hb : true));
            }

            foreach (var k in _derived.Keys)
            {
                list.Add(new FeatureInfo(
                    Name: k,
                    Kind: "derived",
                    Unit: k.Contains("Ratio", StringComparison.OrdinalIgnoreCase) ? "ratio" : "count/ratio",
                    HigherIsBetter: Direction.TryGetValue(k, out var hb) ? hb : true));
            }

            list.Add(new FeatureInfo("Score", "score", "score", true));
            return list.OrderBy(f => f.Kind).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IReadOnlyList<FeatureInfo> ListFeatures() => GetFeatureCatalog();

        // ------------------------------- Scoring --------------------------------

        public List<Scored> Score(IEnumerable<ClusterRow> rowsEnum, WeightConfig? weights = null, ScoringOptions? options = null)
        {
            var rows = rowsEnum?.ToList() ?? new List<ClusterRow>();
            weights ??= DefaultWeights();
            options ??= new ScoringOptions();

            if (options.IncludeAllNumeric && options.IncludeAllNumericBudget > 0)
                DistributeBudgetAcrossUnknowns(weights, rows, options.IncludeAllNumericBudget);

            Rebalance(weights);

            var numericKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in weights.Keys)
                if (IsNumericOrDerived(k)) numericKeys.Add(k);

            var stats = ComputeStats(rows, numericKeys, options);

            // Build sorted value lists for rank normalization if needed
            Dictionary<string, List<double>>? sortedForRank = null;
            if (options.RankNormalizeKeys is { Count: > 0 })
                sortedForRank = BuildSortedValueLists(rows, numericKeys);

            var scored = new List<Scored>(rows.Count);
            foreach (var r in rows)
            {
                // Per-row reweighting: only the weights for keys with a present raw value will be used
                Dictionary<string, double> effectiveWeights;
                if (options.PerRowReweightMissing)
                {
                    var present = weights.Where(kv => kv.Value > 0 && GetRawOrDerived(kv.Key, r).HasValue).ToList();
                    var sumW = present.Sum(kv => kv.Value);
                    if (sumW <= 0)
                    {
                        // Fallback: use original weights to avoid divide-by-zero
                        effectiveWeights = new Dictionary<string, double>(weights, StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        effectiveWeights = present.ToDictionary(kv => kv.Key, kv => kv.Value / sumW, StringComparer.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    effectiveWeights = new Dictionary<string, double>(weights, StringComparer.OrdinalIgnoreCase);
                }

                double total = 0;
                var expl = new List<FactorContribution>(effectiveWeights.Count);

                foreach (var (key, w) in effectiveWeights)
                {
                    if (w <= 0) continue;

                    bool inverted = Direction.TryGetValue(key, out var hiBetter) ? !hiBetter : false;
                    double? raw = GetRawOrDerived(key, r);

                    // Missing values contribute nothing when per-row reweighting is enabled
                    if (!raw.HasValue && options.PerRowReweightMissing) continue;

                    double norm = Normalize(key, raw, stats, options);

                    // If rank normalization is requested for this key, override norm with empirical CDF
                    if (raw.HasValue && options.RankNormalizeKeys is { Count: > 0 } && options.RankNormalizeKeys.Contains(key)
                        && sortedForRank != null && sortedForRank.TryGetValue(key, out var vec) && vec.Count > 0)
                    {
                        var mm = stats.TryGetValue(key, out var mms) ? mms : (null, null);
                        // Clamp to winsor bounds before ranking
                        var clamped = ClampToBounds(raw!.Value, mm);
                        norm = EmpiricalCdf(vec, clamped);
                    }

                    if (inverted) norm = 1.0 - norm;

                    var contrib = w * norm;
                    total += contrib;

                    (double? Min, double? Max) mm2 = stats.TryGetValue(key, out var mms2) ? mms2 : (null, null);
                    expl.Add(new FactorContribution(
                        Property: key,
                        Raw: raw,
                        Normalized: norm,
                        Inverted: inverted,
                        Weight: w,
                        Contribution: contrib,
                        MinSeen: mm2.Min,
                        MaxSeen: mm2.Max,
                        Note: _derived.ContainsKey(key) ? "derived" : null
                    ));
                }

                scored.Add(new Scored(r, Math.Round(total, 6), expl));
            }

            scored = scored.OrderByDescending(x => x.Score)
                           .ThenBy(x => x.Row.CoreUtilization ?? 1e9)
                           .ToList();

            return scored;
        }

        public async Task<ScoreResult> ScoreAllAsync(
            WeightConfig? weights = null,
            ScoringOptions? options = null,
            CancellationToken ct = default)
        {
            var rows = await _data.GetClusterRowDataAsync(ct);
            weights ??= DefaultWeights();
            options ??= new ScoringOptions();

            if (options.IncludeAllNumeric && options.IncludeAllNumericBudget > 0)
                DistributeBudgetAcrossUnknowns(weights, rows, options.IncludeAllNumericBudget);

            Rebalance(weights);

            var numericKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in weights.Keys)
                if (IsNumericOrDerived(k)) numericKeys.Add(k);

            var stats = ComputeStats(rows, numericKeys, options);
            Dictionary<string, List<double>>? sortedForRank = null;
            if (options.RankNormalizeKeys is { Count: > 0 })
                sortedForRank = BuildSortedValueLists(rows, numericKeys);

            var ranked = new List<ScoreRowBreakdown>(rows.Count);
            foreach (var r in rows)
            {
                Dictionary<string, double> effectiveWeights;
                if (options.PerRowReweightMissing)
                {
                    var present = weights.Where(kv => kv.Value > 0 && GetRawOrDerived(kv.Key, r).HasValue).ToList();
                    var sumW = present.Sum(kv => kv.Value);
                    effectiveWeights = (sumW > 0)
                        ? present.ToDictionary(kv => kv.Key, kv => kv.Value / sumW, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, double>(weights, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    effectiveWeights = new Dictionary<string, double>(weights, StringComparer.OrdinalIgnoreCase);
                }

                double total = 0;
                var expl = new List<FactorContribution>(effectiveWeights.Count);

                foreach (var (key, w) in effectiveWeights)
                {
                    if (w <= 0) continue;
                    bool inverted = Direction.TryGetValue(key, out var hiBetter) ? !hiBetter : false;
                    double? raw = GetRawOrDerived(key, r);
                    if (!raw.HasValue && options.PerRowReweightMissing) continue;

                    double norm = Normalize(key, raw, stats, options);

                    if (raw.HasValue && options.RankNormalizeKeys is { Count: > 0 } && options.RankNormalizeKeys.Contains(key)
                        && sortedForRank != null && sortedForRank.TryGetValue(key, out var vec) && vec.Count > 0)
                    {
                        var mm = stats.TryGetValue(key, out var mms) ? mms : (null, null);
                        var clamped = ClampToBounds(raw!.Value, mm);
                        norm = EmpiricalCdf(vec, clamped);
                    }

                    if (inverted) norm = 1.0 - norm;

                    var contrib = w * norm;
                    total += contrib;

                    (double? Min, double? Max) mm2 = stats.TryGetValue(key, out var mms2) ? mms2 : (null, null);
                    expl.Add(new FactorContribution(
                        Property: key,
                        Raw: raw,
                        Normalized: norm,
                        Inverted: inverted,
                        Weight: w,
                        Contribution: contrib,
                        MinSeen: mm2.Min,
                        MaxSeen: mm2.Max,
                        Note: _derived.ContainsKey(key) ? "derived" : null
                    ));
                }

                ranked.Add(new ScoreRowBreakdown(r.Cluster ?? (r.ClusterId ?? "(unknown)"), Math.Round(total, 6), expl));
            }

            var ordered = ranked.OrderByDescending(x => x.Score).ToList();

            return new ScoreResult(
                Rankings: ordered,
                AppliedWeights: weights,
                NumericStats: stats
            );
        }

        /// <summary>
        /// Dumps the exact normalization bounds (min/max after winsorization) the scorer would use,
        /// given the same rows, weights, and options. Use this to mirror normalization offline.
        /// </summary>
        public async Task<Dictionary<string,(double? Min,double? Max)>> DumpAppliedStatsAsync(
            WeightConfig? weights = null,
            ScoringOptions? options = null,
            CancellationToken ct = default)
        {
            // 1) Load rows just like ScoreAllAsync
            var rows = await _data.GetClusterRowDataAsync(ct);

            // 2) Apply the same knobs as scoring
            weights ??= DefaultWeights();
            options ??= new ScoringOptions();

            if (options.IncludeAllNumeric && options.IncludeAllNumericBudget > 0)
                DistributeBudgetAcrossUnknowns(weights, rows, options.IncludeAllNumericBudget);

            Rebalance(weights);

            // 3) Build the same numeric/derived key set we score on
            var numericKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in weights.Keys)
                if (IsNumericOrDerived(k)) numericKeys.Add(k);

            // 4) Compute and return the post-winsor min/max per feature
            var stats = ComputeStats(rows, numericKeys, options);
            return new Dictionary<string,(double? Min,double? Max)>(stats, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<ScoreRowBreakdown?> ExplainAsync(
            string clusterId,
            WeightConfig? weights = null,
            ScoringOptions? options = null,
            CancellationToken ct = default)
        {
            var res = await ScoreAllAsync(weights, options, ct);
            return res.Rankings.FirstOrDefault(r => string.Equals(r.Cluster, clusterId, StringComparison.OrdinalIgnoreCase));
        }

        // -------------------------- UI helper converters -----------------------

        public ResultItem ToResultItem(Scored s, bool? eligibleOverride = null)
        {
            var r = s.Row;
            bool eligible = eligibleOverride ?? DefaultEligibilityHeuristic(r);

            // Normalize CoreUtilization to a 0..1 ratio for display too (handles 0–1 and 0–100 inputs)
            var utilRatio =
                CoerceToRatio01("CoreUtilization", r.CoreUtilization)
                ?? (r.TotalPhysicalCores is > 0 && r.UsedCores is >= 0
                        ? r.UsedCores!.Value / r.TotalPhysicalCores!.Value
                        : 0.0);

            return new ResultItem(
                Cluster: r.Cluster ?? r.ClusterId ?? "(unknown)",
                Region: r.Region,
                DataCenter: r.DataCenter,
                AgeYears: r.ClusterAgeYears ?? 0,
                UtilPct: utilRatio * 100.0,  // now correctly scaled
                Score: Math.Round(s.Score, 6),
                Eligible: eligible
            );
        }

        public object ToExplain(Scored s)
        {
            var top = s.Factors.OrderByDescending(f => Math.Abs(f.Contribution)).ToList();
            return new
            {
                Cluster = s.Row.Cluster ?? s.Row.ClusterId ?? "(unknown)",
                Score = s.Score,
                Factors = top.Select(f => new { f.Property, f.Raw, f.Normalized, f.Inverted, f.Weight, f.Contribution, f.MinSeen, f.MaxSeen, f.Note }).ToList()
            };
        }

        // ------------------------------ Helpers --------------------------------

        private static bool IsNumericOrDerived(string key) =>
            _derived.ContainsKey(key) || _doubleGet.ContainsKey(key) || _intGet.ContainsKey(key) || _boolGet.ContainsKey(key);

        private static double? GetRawOrDerived(string key, ClusterRow r)
        {
            double? raw = _derived.TryGetValue(key, out var f) ? f(r)
                       : _doubleGet.TryGetValue(key, out var gd) ? gd(r)
                       : _intGet.TryGetValue(key, out var gi) ? gi(r)
                       : _boolGet.TryGetValue(key, out var gb) ? (gb(r) is bool b ? (b ? 1.0 : 0.0) : (double?)null)
                       : null;

            return CoerceToRatio01(key, raw);
        }

        private static Dictionary<string, (double? Min, double? Max)> ComputeStats(
            IReadOnlyList<ClusterRow> rows,
            HashSet<string> keys,
            ScoringOptions options)
        {
            var dict = new Dictionary<string, (double? Min, double? Max)>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in keys)
            {
                var vals = new List<double>(rows.Count);

                foreach (var r in rows)
                {
                    var v = GetRawOrDerived(k, r);
                    if (v.HasValue && double.IsFinite(v.Value))
                        vals.Add(v.Value);
                }

                if (vals.Count == 0)
                {
                    dict[k] = (null, null);
                    continue;
                }

                vals.Sort();

                if (options.Winsorize && vals.Count >= 5)
                {
                    // per-feature overrides (if present)
                    (double loQ, double hiQ) = options.PerFeatureQuantiles != null && options.PerFeatureQuantiles.TryGetValue(k, out var cut)
                        ? (Math.Clamp(cut.Lower, 0.0, 0.49), Math.Clamp(cut.Upper, 0.51, 1.0))
                        : (Math.Clamp(options.LowerQuantile, 0.0, 0.49), Math.Clamp(options.UpperQuantile, 0.51, 1.0));

                    var lo = Percentile(vals, loQ);
                    var hi = Percentile(vals, hiQ);
                    dict[k] = (lo, hi);
                }
                else
                {
                    dict[k] = (vals.First(), vals.Last());
                }
            }

            return dict;
        }

        private static double Normalize(string key, double? raw,
            IReadOnlyDictionary<string, (double? Min, double? Max)> stats,
            ScoringOptions options)
        {
            if (!raw.HasValue) return 0.5;
            var mm = stats.TryGetValue(key, out var v) ? v : (null, null);
            var min = mm.Min; var max = mm.Max;
            if (!min.HasValue || !max.HasValue) return 0.5;
            if (Math.Abs(max.Value - min.Value) < 1e-9) return 0.5;
            var clamped = ClampToBounds(raw.Value, mm);
            var norm = (clamped - min.Value) / (max.Value - min.Value);
            if (!double.IsFinite(norm)) return 0.5;
            return Math.Clamp(norm, 0.0, 1.0);
        }

        private static double ClampToBounds(double raw, (double? Min, double? Max) mm)
        {
            var min = mm.Min!.Value; var max = mm.Max!.Value;
            return Math.Min(Math.Max(raw, min), max);
        }

        private static double Percentile(List<double> sorted, double q)
        {
            if (sorted.Count == 0) return double.NaN;
            if (q <= 0) return sorted.First();
            if (q >= 1) return sorted.Last();
            var pos = (sorted.Count - 1) * q;
            var lower = (int)Math.Floor(pos);
            var upper = (int)Math.Ceiling(pos);
            if (lower == upper) return sorted[lower];
            var frac = pos - lower;
            return sorted[lower] * (1 - frac) + sorted[upper] * frac;
        }

        private static Dictionary<string, List<double>> BuildSortedValueLists(IReadOnlyList<ClusterRow> rows, HashSet<string> keys)
        {
            var dict = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                var vals = new List<double>(rows.Count);
                foreach (var r in rows)
                {
                    var v = GetRawOrDerived(k, r);
                    if (v.HasValue && double.IsFinite(v.Value)) vals.Add(v.Value);
                }
                vals.Sort();
                dict[k] = vals;
            }
            return dict;
        }

        private static double EmpiricalCdf(List<double> sorted, double x)
        {
            if (sorted.Count == 0) return 0.5;
            int lo = 0, hi = sorted.Count; // upper_bound
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= x) lo = mid + 1; else hi = mid;
            }
            // lo = index of first element > x → fraction <= x is lo / n
            return (double)lo / sorted.Count;
        }

        private static void DistributeBudgetAcrossUnknowns(WeightConfig weights, IReadOnlyList<ClusterRow> rows, double budget)
        {
            var candidates = new HashSet<string>(_doubleGet.Keys.Concat(_intGet.Keys).Concat(_derived.Keys), StringComparer.OrdinalIgnoreCase);

            // Exclude categorical/noisy
            candidates.Remove("RegionHealthLevel");
            candidates.Remove("HotRegionVMSeries");
            candidates.Remove("Intent");
            candidates.Remove("Region");
            candidates.Remove("DataCenter");
            candidates.Remove("AvailabilityZone");
            candidates.Remove("PhysicalAZ");
            candidates.Remove("Cluster");
            candidates.Remove("ClusterId");
            candidates.Remove("CloudType");
            candidates.Remove("RegionType");
            candidates.Remove("Manufacturer");
            candidates.Remove("MemCategory");
            candidates.Remove("SKUName");

            var eligible = candidates.Where(k => rows.Any(r => GetRawOrDerived(k, r).HasValue)).ToList();
            if (eligible.Count == 0) return;

            var each = budget / eligible.Count;
            foreach (var k in eligible) if (!weights.ContainsKey(k)) weights[k] = each;
        }

        private static bool DefaultEligibilityHeuristic(ClusterRow r)
        {
            var ageOk  = (r.ClusterAgeYears ?? 0) >= 6;
            var utilOk = (r.CoreUtilization ?? (r.UsedCores.HasValue && r.TotalPhysicalCores > 0
                           ? r.UsedCores!.Value / r.TotalPhysicalCores!.Value : 1.0)) <= 0.30;
            var sticky = (r.HasSLB == true) || (r.HasWARP == true);
            return ageOk && utilOk && !sticky;
        }
    }
}
