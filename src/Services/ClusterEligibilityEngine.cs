#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Eligibility
{
    /// <summary>
    /// Eligibility = a configurable policy gate:
    /// - Baseline rules (MinAgeYears, MaxCoreUtilizationPercent, region allow/deny)
    /// - Optional generic constraints (StringIn/NotIn, BoolEquals, IntRanges, DoubleRanges)
    /// Returns both the eligible rows and detailed "why not" reasons for failures.
    /// Pure C#, testable, no LLM here.
    /// </summary>
    public static class ClusterEligibilityEngine
    {
        // ---------- Public DTOs (serializable from JSON) ----------

        public sealed record IntRange(int? Min = null, int? Max = null);
        public sealed record DoubleRange(double? Min = null, double? Max = null);

        /// <summary>
        /// Configurable policy. You can serialize/deserialize this from JSON.
        /// </summary>
        public sealed record EligibilityRules
        {
            // Baseline policy (most common ask)
            public bool Enabled { get; init; } = true;
            public bool EnforceAge { get; init; } = true;
            public bool EnforceUtilization { get; init; } = true;
            public bool EnforceAllowedRegions { get; init; } = true;
            public bool EnforceExcludedRegions { get; init; } = true;
            public int MinAgeYears { get; init; } = 6;
            public double MaxCoreUtilizationPercent { get; init; } = 30;

            // Region policy (optional)
            public HashSet<string> AllowedRegions { get; init; } = new(StringComparer.OrdinalIgnoreCase); // empty = no allow-list
            public HashSet<string> ExcludedRegions { get; init; } = new(StringComparer.OrdinalIgnoreCase); // empty = none excluded

            // Generic, optional constraints (so you don't change code to add checks)
            public Dictionary<string, HashSet<string>> StringIn    { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> StringNotIn { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool>           BoolEquals   { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, IntRange>       IntRanges    { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, DoubleRange>    DoubleRanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            // Convenience factory for our default
            public static EligibilityRules Default() => new();
        }

        public sealed record IneligibleItem(ClusterRow Row, List<string> Reasons);

        // ---------- Reflection caches (once) ----------

        private static readonly Dictionary<string, Func<ClusterRow, string?>> _stringGetters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, bool?>>   _boolGetters   = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, int?>>    _intGetters    = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, double?>> _doubleGetters = new(StringComparer.OrdinalIgnoreCase);

        // Optional field normalizers for string comparisons
        private static readonly Dictionary<string, Func<string?, string>> _stringNormalizers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Region"] = NormalizeRegion
            };

        static ClusterEligibilityEngine()
        {
            var t = typeof(ClusterRow);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = p.Name;

                if (p.PropertyType == typeof(string))
                    _stringGetters[name] = (ClusterRow r) => (string?)p.GetValue(r);
                else if (p.PropertyType == typeof(bool?))
                    _boolGetters[name] = (ClusterRow r) => (bool?)p.GetValue(r);
                else if (p.PropertyType == typeof(int?))
                    _intGetters[name] = (ClusterRow r) => (int?)p.GetValue(r);
                else if (p.PropertyType == typeof(double?))
                    _doubleGetters[name] = (ClusterRow r) => (double?)p.GetValue(r);
            }
        }

        // ---------- Public API ----------

        /// <summary>
        /// Returns only rows that pass eligibility; emits per-row reasons for failures.
        /// </summary>
        public static List<ClusterRow> FilterEligible(
            IEnumerable<ClusterRow> rows,
            EligibilityRules rules,
            out List<IneligibleItem> ineligible)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            if (rules is null) throw new ArgumentNullException(nameof(rules));

            var ok = new List<ClusterRow>();
            var bad = new List<IneligibleItem>();

            foreach (var r in rows)
            {
                if (IsEligible(r, rules, out var reasons))
                    ok.Add(r);
                else
                    bad.Add(new IneligibleItem(r, reasons));
            }

            ineligible = bad;
            return ok;
        }

        /// <summary>
        /// Evaluate one row. Returns true if eligible; "reasons" explains failures (empty if eligible).
        /// </summary>
        public static bool IsEligible(ClusterRow row, EligibilityRules rules, out List<string> reasons)
        {
            reasons = new List<string>();

            if (!rules.Enabled) return true; // gate disabled → everything passes

            // ---- Baseline checks ----
            // Age
            if (rules.EnforceAge) {
                var ageYears = row.ClusterAgeYears;
                if (!ageYears.HasValue) reasons.Add($"Missing ClusterAgeYears (needs ≥ {rules.MinAgeYears}).");
                else if (ageYears.Value < rules.MinAgeYears) reasons.Add($"Age {ageYears:F1}y < MinAgeYears {rules.MinAgeYears}y.");
            }

            // Utilization (assumed in percent units)
            if (rules.EnforceUtilization) {
                var util = row.CoreUtilization;
                if (!util.HasValue) reasons.Add($"Missing CoreUtilization (needs ≤ {rules.MaxCoreUtilizationPercent}%).");
                else if (util.Value > rules.MaxCoreUtilizationPercent) reasons.Add($"CoreUtilization {util.Value:F1}% > Max {rules.MaxCoreUtilizationPercent:F1}%.");
            }

            // Regions
            var region = NormalizeRegion(row.Region);
            if (rules.EnforceAllowedRegions && rules.AllowedRegions.Count > 0 &&
                !rules.AllowedRegions.Select(NormalizeRegion).Contains(region))
            {
                reasons.Add($"Region '{row.Region ?? "null"}' not in AllowedRegions.");
            }
            if (rules.EnforceExcludedRegions &&
                rules.ExcludedRegions.Select(NormalizeRegion).Contains(region))
            {
                reasons.Add($"Region '{row.Region ?? "null"}' is in ExcludedRegions.");
            }

            // ---- Generic checks (optional) ----
            foreach (var (field, allowSetRaw) in rules.StringIn)
            {
                if (!_stringGetters.TryGetValue(field, out var get)) { reasons.Add($"Field '{field}' not found for StringIn."); continue; }
                var norm = _stringNormalizers.TryGetValue(field, out var n) ? n : Identity;
                var v = norm(get(row));
                var allowSet = allowSetRaw.Select(norm).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(v) || !allowSet.Contains(v))
                    reasons.Add($"{field}='{get(row) ?? "null"}' not in allowed set.");
            }

            foreach (var (field, denySetRaw) in rules.StringNotIn)
            {
                if (!_stringGetters.TryGetValue(field, out var get)) { reasons.Add($"Field '{field}' not found for StringNotIn."); continue; }
                var norm = _stringNormalizers.TryGetValue(field, out var n) ? n : Identity;
                var v = norm(get(row));
                var denySet = denySetRaw.Select(norm).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(v) && denySet.Contains(v))
                    reasons.Add($"{field}='{get(row)}' is in excluded set.");
            }

            foreach (var (field, want) in rules.BoolEquals)
            {
                if (!_boolGetters.TryGetValue(field, out var get)) { reasons.Add($"Field '{field}' not found for BoolEquals."); continue; }
                var v = get(row);
                if (!v.HasValue || v.Value != want)
                    reasons.Add($"{field} must be {want} (actual: {(v.HasValue ? v.Value.ToString() : "null")}).");
            }

            foreach (var (field, range) in rules.IntRanges)
            {
                if (!_intGetters.TryGetValue(field, out var get)) { reasons.Add($"Field '{field}' not found for IntRanges."); continue; }
                var v = get(row);
                if (!v.HasValue)
                {
                    reasons.Add($"{field} missing (needs {(range.Min is not null ? $"≥ {range.Min} " : "")}{(range.Max is not null ? $"≤ {range.Max}" : "")}).");
                }
                else
                {
                    if (range.Min is int min && v.Value < min)
                        reasons.Add($"{field} {v.Value} < {min}.");
                    if (range.Max is int max && v.Value > max)
                        reasons.Add($"{field} {v.Value} > {max}.");
                }
            }

            foreach (var (field, range) in rules.DoubleRanges)
            {
                if (!_doubleGetters.TryGetValue(field, out var get)) { reasons.Add($"Field '{field}' not found for DoubleRanges."); continue; }
                var v = get(row);
                if (!v.HasValue)
                {
                    reasons.Add($"{field} missing (needs {(range.Min is not null ? $"≥ {range.Min:F2} " : "")}{(range.Max is not null ? $"≤ {range.Max:F2}" : "")}).");
                }
                else
                {
                    if (range.Min is double dmin && v.Value < dmin)
                        reasons.Add($"{field} {v.Value:F2} < {dmin:F2}.");
                    if (range.Max is double dmax && v.Value > dmax)
                        reasons.Add($"{field} {v.Value:F2} > {dmax:F2}.");
                }
            }

            return reasons.Count == 0;
        }

        // ---------- Helpers ----------

        private static string Identity(string? s) => (s ?? string.Empty).Trim();

        // Very light region canonicalization (mirrors the filter engine idea)
        private static string NormalizeRegion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var k = raw.Trim().ToLowerInvariant();

            static string Compact(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray());

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["west us"] = "westus", ["west-us"] = "westus", ["westus"] = "westus",
                ["west us 2"] = "westus2", ["west-us-2"] = "westus2", ["westus2"] = "westus2",
                ["east us"] = "eastus", ["east-us"] = "eastus", ["eastus"] = "eastus",
                ["east us 2"] = "eastus2", ["eastus2"] = "eastus2",
                ["west europe"] = "westeurope", ["westeurope"] = "westeurope",
                ["north europe"] = "northeurope", ["northeurope"] = "northeurope",
                ["southeast asia"] = "southeastasia", ["southeastasia"] = "southeastasia",
                ["east asia"] = "eastasia", ["eastasia"] = "eastasia"
            };

            if (map.TryGetValue(k, out var v)) return v;

            var compact = Compact(k);
            if (map.TryGetValue(compact, out v)) return v;

            return compact;
        }
    }
}
