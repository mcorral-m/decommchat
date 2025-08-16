#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Services
{
    public sealed class EligibilityRules
    {
        public double? MinAgeYears { get; init; }            // e.g. 6
        public double? MaxUtilization { get; init; }         // fraction 0..1 (e.g. 0.30)
        public bool IgnoreSpecialWorkloads { get; init; }    // true => ignore HasSLB/HasWARP/etc

        // Region/DataCenter scoping (Cloud removed — not present in the data model)
        public string[] IncludeRegions { get; init; } = Array.Empty<string>();
        public string[] ExcludeRegions { get; init; } = Array.Empty<string>();
        public string[] IncludeDataCenters { get; init; } = Array.Empty<string>(); // optional, uses ClusterRow.DataCenter
        public string[] ExcludeDataCenters { get; init; } = Array.Empty<string>(); // optional
    }

    public enum EligibilityFallbackPolicy
    {
        UseDefaultWhenMissing,
        RequireRules,
        UseStoreThenDefault
    }

    public sealed class EligibilityResult
    {
        public string Cluster { get; init; } = "";
        public bool Eligible { get; init; }
        public List<string> Reasons { get; init; } = new();
    }

    public interface IEligibilityRulesStore
    {
        EligibilityRules? Get(string key);
        void Set(string key, EligibilityRules rules);
    }

    public sealed class InMemoryEligibilityRulesStore : IEligibilityRulesStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EligibilityRules> _map
            = new(StringComparer.OrdinalIgnoreCase);

        public EligibilityRules? Get(string key) => _map.TryGetValue(key, out var r) ? r : null;
        public void Set(string key, EligibilityRules rules) => _map[key] = rules;
    }

    public sealed class EligibilityEngine
    {
        // MVP default rule — change freely
        public static bool DefaultEligibilityHeuristic(ClusterRow r, out List<string> reasons)
        {
            reasons = new();
            bool ok = true;

            var ageYears = r.ClusterAgeYears ?? 0;
            // If CoreUtilization is null, fall back to UsedCores/TotalPhysicalCores when available
            double util = r.CoreUtilization
                          ?? ((r.UsedCores.HasValue && r.TotalPhysicalCores.HasValue && r.TotalPhysicalCores.Value > 0)
                                ? r.UsedCores.Value / r.TotalPhysicalCores.Value
                                : 1.0);

            if (ageYears < 6)      { ok = false; reasons.Add("Age < 6y."); }
            if (util > 0.30)       { ok = false; reasons.Add("Utilization > 30%."); }
            if ((r.HasSLB == true) || (r.HasWARP == true))
                                   { ok = false; reasons.Add("Special workload present."); }

            return ok;
        }

        public EligibilityResult Evaluate(ClusterRow row, EligibilityRules rules)
        {
            var reasons = new List<string>();
            bool ok = true;

            // ---------- Region/DC scoping ----------
            if (rules.IncludeRegions.Length > 0 &&
                !rules.IncludeRegions.Contains(row.Region ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                ok = false; reasons.Add("Region not in include list.");
            }

            if (rules.ExcludeRegions.Contains(row.Region ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                ok = false; reasons.Add("Region excluded.");
            }

            if (rules.IncludeDataCenters.Length > 0 &&
                !rules.IncludeDataCenters.Contains(row.DataCenter ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                ok = false; reasons.Add("DataCenter not in include list.");
            }

            if (rules.ExcludeDataCenters.Contains(row.DataCenter ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                ok = false; reasons.Add("DataCenter excluded.");
            }

            // ---------- Core thresholds ----------
            if (rules.MinAgeYears is double minAge && (row.ClusterAgeYears ?? double.MinValue) < minAge)
            {
                ok = false; reasons.Add($"Age < {minAge}y.");
            }

            if (rules.MaxUtilization is double maxU)
            {
                double util = row.CoreUtilization
                              ?? ((row.UsedCores.HasValue && row.TotalPhysicalCores.HasValue && row.TotalPhysicalCores.Value > 0)
                                    ? row.UsedCores.Value / row.TotalPhysicalCores.Value
                                    : 1.0);

                if (util > maxU)
                {
                    ok = false; reasons.Add($"Utilization > {maxU:P0}.");
                }
            }

            // ---------- Special workloads ----------
            if (!rules.IgnoreSpecialWorkloads && ((row.HasSLB == true) || (row.HasWARP == true)))
            {
                ok = false; reasons.Add("Special workload present.");
            }

            return new EligibilityResult { Cluster = row.Cluster ?? row.ClusterId ?? "(unknown)", Eligible = ok, Reasons = reasons };
        }

        public EligibilityResult EvaluateWithFallback(
            ClusterRow row,
            EligibilityRules? rules,
            EligibilityFallbackPolicy policy,
            string? storeKey,
            IEligibilityRulesStore? store)
        {
            if (rules is not null) return Evaluate(row, rules);

            switch (policy)
            {
                case EligibilityFallbackPolicy.RequireRules:
                    return new EligibilityResult
                    {
                        Cluster = row.Cluster ?? row.ClusterId ?? "(unknown)",
                        Eligible = false,
                        Reasons = new() { "No rules provided." }
                    };

                case EligibilityFallbackPolicy.UseStoreThenDefault:
                    var fromStore = (storeKey is not null && store is not null) ? store.Get(storeKey) : null;
                    if (fromStore is not null) return Evaluate(row, fromStore);
                    goto default;

                default: // UseDefaultWhenMissing
                    var ok = DefaultEligibilityHeuristic(row, out var reasons);
                    return new EligibilityResult
                    {
                        Cluster = row.Cluster ?? row.ClusterId ?? "(unknown)",
                        Eligible = ok,
                        Reasons = reasons
                    };
            }
        }

        public IEnumerable<EligibilityResult> EvaluateAll(
            IEnumerable<ClusterRow> rows,
            EligibilityRules? rules,
            EligibilityFallbackPolicy policy,
            string? storeKey,
            IEligibilityRulesStore? store)
            => rows.Select(r => EvaluateWithFallback(r, rules, policy, storeKey, store));
    }
}
