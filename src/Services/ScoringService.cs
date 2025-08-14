#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MyM365AgentDecommision.Bot.Interfaces;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// Explainable, weight-based decommission scoring aligned to ClusterRow fields.
    /// - Raw + derived factors (units normalized to 0..1 where applicable).
    /// - Min–max with optional winsorization; inversion handled via Direction map.
    /// - Nullable-safe; missing values contribute a neutral 0.5.
    /// - Includes a feature catalog for discoverability (name/kind/unit/direction).
    /// </summary>
    public sealed class ScoringService
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
        }

        public sealed class ScoringOptions
        {
            /// <summary>Apply winsorization before min–max normalization.</summary>
            public bool Winsorize { get; init; } = true;

            /// <summary>Lower/upper quantiles used when winsorizing (0..1).</summary>
            public double LowerQuantile { get; init; } = 0.02;
            public double UpperQuantile { get; init; } = 0.98;

            /// <summary>
            /// If true, distribute a small budget across all other numeric/derived fields
            /// not explicitly weighted by the user (good for “blend everything in a bit”).
            /// </summary>
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

        // Feature catalog for discoverability in chat
        public sealed record FeatureInfo(string Name, string Kind, string Unit, bool HigherIsBetter);

        // --------------------------- Reflection cache --------------------------

        private static readonly Dictionary<string, Func<ClusterRow, double?>> _doubleGet =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, int?>> _intGet =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, bool?>> _boolGet =
            new(StringComparer.OrdinalIgnoreCase);

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

        // ---------------------------- Percent-like keys ------------------------

        // These arrive as percent (0..100) from Kusto at times; coerce to 0..1.
        private static readonly HashSet<string> PercentLike = new(StringComparer.OrdinalIgnoreCase)
        { "CoreUtilization", "VMDensity", "OutOfServicesPercentage" };

        private static double? CoerceToRatio01(string key, double? v)
        {
            if (!v.HasValue) return null;
            // If value appears to be in percent units (>1), convert; otherwise assume already 0..1.
            return (PercentLike.Contains(key) && v.Value > 1.0) ? v.Value / 100.0 : v.Value;
        }

        // ---------------------------- Derived features -------------------------
        // Derived signals purely from fields present in ClusterRow.cs.

        private static readonly Dictionary<string, Func<ClusterRow, double?>> _derived =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Utilization (ratio)
                ["EffectiveCoreUtilization"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.UsedCores is >= 0)
                        ? r.UsedCores!.Value / r.TotalPhysicalCores!.Value
                        : CoerceToRatio01("CoreUtilization", r.CoreUtilization),

                // Health / OOS (ratios)
                ["OOSNodeRatio"] = r =>
                    (r.TotalNodes is > 0 && r.OutOfServiceNodes is >= 0)
                        ? r.OutOfServiceNodes!.Value / (double)r.TotalNodes!.Value
                        : CoerceToRatio01("OutOfServicesPercentage", r.OutOfServicesPercentage),

                ["HealthyNodeRatio"] = r =>
                    (r.TotalNodes is > 0 && r.OutOfServiceNodes is >= 0)
                        ? (r.TotalNodes!.Value - r.OutOfServiceNodes!.Value) / (double)r.TotalNodes!.Value
                        : (r.OutOfServicesPercentage is >= 0 ? 1.0 - CoerceToRatio01("OutOfServicesPercentage", r.OutOfServicesPercentage) : null),

                // DNG / stranding (ratios)
                ["DNGNodeRatio"] = r =>
                    (r.TotalNodes is > 0 && r.DNG_Nodes is >= 0) ? r.DNG_Nodes!.Value / (double)r.TotalNodes!.Value : null,

                ["StrandedCoresRatio_DNG"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.StrandedCores_DNG is >= 0) ? r.StrandedCores_DNG!.Value / r.TotalPhysicalCores!.Value : null,

                ["StrandedCoresRatio_TIP"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.StrandedCores_TIP is >= 0) ? r.StrandedCores_TIP!.Value / r.TotalPhysicalCores!.Value : null,

                ["StrandedCoresRatio_32VMs"] = r =>
                    (r.TotalPhysicalCores is > 0 && r.StrandedCores_32VMs is >= 0) ? r.StrandedCores_32VMs!.Value / r.TotalPhysicalCores!.Value : null,

                // Mix / stickiness (ratios)
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
        // true  => higher is more decom-worthy
        // false => lower is more decom-worthy (so we invert normalization)
        private static readonly Dictionary<string, bool> Direction = new(StringComparer.OrdinalIgnoreCase)
        {
            // Age & timeline
            ["ClusterAgeYears"] = true,
            ["DecommissionYearsRemaining"] = false,

            // Utilization / density (prefer lower)
            ["EffectiveCoreUtilization"] = false,
            ["CoreUtilization"] = false,
            ["VMDensity"] = false,

            // Health / OOS / Stranding
            ["OOSNodeRatio"] = true,
            ["HealthyNodeRatio"] = false,
            ["DNGNodeRatio"] = true,
            ["StrandedCoresRatio_DNG"] = true,
            ["StrandedCoresRatio_TIP"] = true,
            ["StrandedCoresRatio_32VMs"] = true,

            // Region context
            ["RegionHealthScore"] = false,
            ["IsHotRegion"] = false,
            ["RegionHotnessPriority"] = false,

            // Stickiness / constraints (higher = stickier = less decom-worthy)
            ["HasSQL"] = false,
            ["HasSLB"] = false,
            ["HasWARP"] = false,
            ["HasPlatformTenant"] = false,   // fixed spelling
            ["HasUDGreaterThan10"] = false,
            ["HasInstancesGreaterThan10"] = false,
            ["IsTargetMP"] = false,

            // Mix ratios (more SQL/non-spannable often stickier)
            ["SQL_Ratio"] = false,
            ["NonSpannable_Ratio"] = false,
            ["SpannableUtilizationRatio"] = false
        };

        // ----------------------------- Default Weights -------------------------

        public static WeightConfig DefaultWeights()
        {
            var w = new WeightConfig
            {
                // Core trio
                ["ClusterAgeYears"] = 0.25,
                ["EffectiveCoreUtilization"] = 0.25,    // inverted by Direction
                ["RegionHealthScore"] = 0.10,           // inverted

                // Health / stranding
                ["OOSNodeRatio"] = 0.10,
                ["StrandedCoresRatio_DNG"] = 0.08,
                ["StrandedCoresRatio_TIP"] = 0.05,
                ["StrandedCoresRatio_32VMs"] = 0.02,

                // Region & timeline
                ["IsHotRegion"] = 0.03,                 // inverted
                ["DecommissionYearsRemaining"] = 0.04,  // inverted

                // Stickiness
                ["HasSQL"] = 0.025,
                ["HasPlatformTenant"] = 0.015,
                ["HasSLB"] = 0.010,
                ["HasWARP"] = 0.010,
                ["HasUDGreaterThan10"] = 0.010,
                ["HasInstancesGreaterThan10"] = 0.010,

                // Mix
                ["SQL_Ratio"] = 0.015,
                ["NonSpannable_Ratio"] = 0.010,
                ["SpannableUtilizationRatio"] = 0.010
            };

            return Rebalance(w);
        }

        public static WeightConfig Rebalance(WeightConfig w)
        {
            var sum = w.Values.Where(v => v > 0).Sum();
            if (sum <= 0) return w;
            var keys = w.Keys.ToList();
            foreach (var k in keys) w[k] = w[k] / sum;
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
                    Unit: "ratio",
                    HigherIsBetter: Direction.TryGetValue(k, out var hb) ? hb : true));
            }

            // De-dupe; prefer derived metadata
            return list.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .Select(g => g.Last())
                       .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        // ------------------------------- Public API -----------------------------

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

            // Numeric keys we need stats for (raw + derived)
            var numericKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in weights.Keys)
                if (IsNumericOrDerived(k)) numericKeys.Add(k);

            var stats = ComputeStats(rows, numericKeys, options);

            var ranked = new List<ScoreRowBreakdown>(rows.Count);
            foreach (var r in rows)
            {
                double total = 0;
                var expl = new List<FactorContribution>(weights.Count);

                foreach (var (key, w) in weights)
                {
                    if (w <= 0) continue;

                    bool inverted = Direction.TryGetValue(key, out var hiBetter) ? !hiBetter : false;
                    double? raw = GetRawOrDerived(key, r);
                    double norm = Normalize(key, raw, stats, options);
                    if (inverted) norm = 1.0 - norm;

                    var contrib = w * norm;
                    total += contrib;

                    (double? Min, double? Max) mm = stats.TryGetValue(key, out var mms) ? mms : (null, null);
                    expl.Add(new FactorContribution(
                        Property: key,
                        Raw: raw,
                        Normalized: norm,
                        Inverted: inverted,
                        Weight: w,
                        Contribution: contrib,
                        MinSeen: mm.Min,
                        MaxSeen: mm.Max,
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

        public async Task<ScoreRowBreakdown?> ExplainAsync(
            string clusterId,
            WeightConfig? weights = null,
            ScoringOptions? options = null,
            CancellationToken ct = default)
        {
            var res = await ScoreAllAsync(weights, options, ct);
            return res.Rankings.FirstOrDefault(r => string.Equals(r.Cluster, clusterId, StringComparison.OrdinalIgnoreCase));
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
                    if (v.HasValue && double.IsFinite(v.Value)) vals.Add(v.Value);
                }

                if (vals.Count == 0)
                {
                    dict[k] = (null, null);
                    continue;
                }

                vals.Sort();

                if (options.Winsorize && options.LowerQuantile >= 0 && options.UpperQuantile > options.LowerQuantile)
                {
                    var lo = Percentile(vals, options.LowerQuantile);
                    var hi = Percentile(vals, options.UpperQuantile);
                    dict[k] = (lo, hi > lo ? hi : (double?)null);
                }
                else
                {
                    dict[k] = (vals.First(), vals.Last());
                }
            }

            return dict;
        }

    private static double Normalize(string key, double? raw, Dictionary<string, (double? Min, double? Max)> stats, ScoringOptions options)
    {
        if (!raw.HasValue) return 0.5;

        var (min, max) = stats.TryGetValue(key, out var mm) ? mm : (null, null);
        if (!min.HasValue || !max.HasValue) return 0.5;
        
        // Handle case where min and max are very close (but not identical)
        var range = max.Value - min.Value;
        if (Math.Abs(range) < 0.0001) 
        {
            // When all values are nearly identical, return neutral score
            // This prevents division by zero and ensures consistent ranking
            return 0.5;
        }

        // Standard min-max normalization
        var clamped = Math.Min(Math.Max(raw.Value, min.Value), max.Value);
        var norm = (clamped - min.Value) / range;
        
        // Ensure the result is finite and within [0,1]
        return double.IsFinite(norm) ? Math.Clamp(norm, 0, 1) : 0.5;
    }        private static double Percentile(List<double> sorted, double q)
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

        private static void DistributeBudgetAcrossUnknowns(WeightConfig weights, IReadOnlyList<ClusterRow> rows, double budget)
        {
            if (budget <= 0) return;

            var candidates = new HashSet<string>(
                _doubleGet.Keys.Concat(_intGet.Keys).Concat(_derived.Keys),
                StringComparer.OrdinalIgnoreCase
            );

            // Exclude anything already weighted
            candidates.ExceptWith(weights.Keys);

            // Also exclude obvious identifiers/strings we don’t want to score
            candidates.Remove("Cluster");
            candidates.Remove("ClusterId");
            candidates.Remove("Region");
            candidates.Remove("AvailabilityZone");
            candidates.Remove("DataCenter");
            candidates.Remove("PhysicalAZ");
            candidates.Remove("Generation");
            candidates.Remove("Manufacturer");
            candidates.Remove("MemCategory");
            candidates.Remove("SKUName");
            candidates.Remove("RegionHealthLevel");
            candidates.Remove("HotRegionVMSeries");
            candidates.Remove("Intent");

            // Keep only those that actually have numeric data in this dataset
            var eligible = candidates.Where(k => rows.Any(r => GetRawOrDerived(k, r).HasValue)).ToList();
            if (eligible.Count == 0) return;

            var each = budget / eligible.Count;
            foreach (var k in eligible) weights[k] = each;
        }
    }
}
