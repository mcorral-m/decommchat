#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// Generic, one-file filter engine for ClusterRow.
    /// - Uses reflection once to index ALL ClusterRow properties (string?, bool?, int?, double?).
    /// - Criteria are dictionaries keyed by property name (case-insensitive).
    /// - Null numeric values fail range comparisons (so they won't pass Min/Max).
    /// - String comparisons are case-insensitive; Region gets a normalization pass.
    /// </summary>
    public static class ClusterFilterEngine
    {
        // ----------------------------- Criteria DTO -----------------------------

        public sealed record Criteria
        {
            // STRING filters
            // e.g. { ["Region"] = {"westus","westeurope"} }
            public Dictionary<string, HashSet<string>> StringIn { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> StringNotIn { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            // substring contains (any match)
            public Dictionary<string, List<string>> StringContainsAny { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            // BOOL equals
            // e.g. { ["HasSQL"] = true }
            public Dictionary<string, bool> BoolEquals { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            // INT ranges
            // e.g. { ["VMCount"] = new IntRange{ Min=10, Max=100 } }
            public Dictionary<string, IntRange> IntRanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            // DOUBLE ranges
            // e.g. { ["CoreUtilization"] = new DoubleRange{ Max=20 } }
            public Dictionary<string, DoubleRange> DoubleRanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            // Sorting & paging
            public string? SortBy { get; init; }                 // property name (any)
            public bool SortDescending { get; init; } = true;
            public int? Skip { get; init; }
            public int? Take { get; init; }
        }

        public sealed record IntRange(int? Min = null, int? Max = null);
        public sealed record DoubleRange(double? Min = null, double? Max = null);

        // ----------------------------- Reflection Cache -------------------------

        private static readonly Dictionary<string, Func<ClusterRow, string?>> _stringGetters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, bool?>>   _boolGetters   = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, int?>>    _intGetters    = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<ClusterRow, double?>> _doubleGetters = new(StringComparer.OrdinalIgnoreCase);

        // Optional per-field normalizers for STRING compares (e.g., Region)
        private static readonly Dictionary<string, Func<string?, string>> _stringNormalizers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Region"] = NormalizeRegion
            };

        static ClusterFilterEngine()
        {
            var t = typeof(ClusterRow);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = p.Name;

                if (p.PropertyType == typeof(string))
                {
                    _stringGetters[name] = (ClusterRow r) => (string?)p.GetValue(r);
                }
                else if (p.PropertyType == typeof(bool?))
                {
                    _boolGetters[name] = (ClusterRow r) => (bool?)p.GetValue(r);
                }
                else if (p.PropertyType == typeof(int?))
                {
                    _intGetters[name] = (ClusterRow r) => (int?)p.GetValue(r);
                }
                else if (p.PropertyType == typeof(double?))
                {
                    _doubleGetters[name] = (ClusterRow r) => (double?)p.GetValue(r);
                }
                // if you later add more numeric types (long?, decimal?), add more maps here.
            }
        }

        // ----------------------------- Public API --------------------------------

        public static IReadOnlyList<ClusterRow> Apply(IEnumerable<ClusterRow> source, Criteria criteria)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (criteria is null) throw new ArgumentNullException(nameof(criteria));

            IEnumerable<ClusterRow> q = source;

            // ---- STRING IN ----
            foreach (var (field, values) in criteria.StringIn)
            {
                if (!_stringGetters.TryGetValue(field, out var get)) continue;
                var normalizer = _stringNormalizers.TryGetValue(field, out var norm) ? norm : Identity;
                var set = values.Select(normalizer).ToHashSet(StringComparer.OrdinalIgnoreCase);

                q = q.Where(r =>
                {
                    var raw = get(r);
                    var normed = normalizer(raw);
                    return !string.IsNullOrEmpty(normed) && set.Contains(normed);
                });
            }

            // ---- STRING NOT IN ----
            foreach (var (field, values) in criteria.StringNotIn)
            {
                if (!_stringGetters.TryGetValue(field, out var get)) continue;
                var normalizer = _stringNormalizers.TryGetValue(field, out var norm) ? norm : Identity;
                var set = values.Select(normalizer).ToHashSet(StringComparer.OrdinalIgnoreCase);

                q = q.Where(r =>
                {
                    var raw = get(r);
                    var normed = normalizer(raw);
                    return string.IsNullOrEmpty(normed) || !set.Contains(normed);
                });
            }

            // ---- STRING CONTAINS ANY ----
            foreach (var (field, needles) in criteria.StringContainsAny)
            {
                if (!_stringGetters.TryGetValue(field, out var get)) continue;
                var normalizer = _stringNormalizers.TryGetValue(field, out var norm) ? norm : Identity;
                var lowered = needles.Select(n => normalizer(n).ToLowerInvariant()).ToList();

                q = q.Where(r =>
                {
                    var v = normalizer(get(r)).ToLowerInvariant();
                    if (string.IsNullOrEmpty(v)) return false;
                    return lowered.Any(n => v.Contains(n));
                });
            }

            // ---- BOOL EQUALS ----
            foreach (var (field, want) in criteria.BoolEquals)
            {
                if (!_boolGetters.TryGetValue(field, out var get)) continue;
                q = q.Where(r => get(r) is bool b && b == want);
            }

            // ---- INT RANGES ----
            foreach (var (field, range) in criteria.IntRanges)
            {
                if (!_intGetters.TryGetValue(field, out var get)) continue;
                var (min, max) = (range.Min, range.Max);

                q = q.Where(r =>
                {
                    var v = get(r);
                    if (!v.HasValue) return false;
                    if (min.HasValue && v.Value < min.Value) return false;
                    if (max.HasValue && v.Value > max.Value) return false;
                    return true;
                });
            }

            // ---- DOUBLE RANGES ----
            foreach (var (field, range) in criteria.DoubleRanges)
            {
                if (!_doubleGetters.TryGetValue(field, out var get)) continue;
                var (min, max) = (range.Min, range.Max);

                q = q.Where(r =>
                {
                    var v = get(r);
                    if (!v.HasValue) return false;
                    if (min.HasValue && v.Value < min.Value) return false;
                    if (max.HasValue && v.Value > max.Value) return false;
                    return true;
                });
            }

            // ---- SORT ----
            q = Sort(q, criteria.SortBy, criteria.SortDescending);

            // ---- PAGE ----
            if (criteria.Skip is int skip && skip > 0) q = q.Skip(skip);
            if (criteria.Take is int take && take > 0) q = q.Take(take);

            return q.ToList();
        }

        /// <summary>Lists every ClusterRow field the engine knows and its kind (string/bool/int/double).</summary>
        public static IReadOnlyList<(string Field, string Kind)> ListSupportedFields()
        {
            var list = new List<(string,string)>();
            list.AddRange(_stringGetters.Keys.Select(k => (k, "string")));
            list.AddRange(_boolGetters.Keys.Select(k => (k, "bool")));
            list.AddRange(_intGetters.Keys.Select(k => (k, "int")));
            list.AddRange(_doubleGetters.Keys.Select(k => (k, "double")));
            return list.OrderBy(t => t.Item1).ToList();
        }

        // ----------------------------- Helpers -----------------------------------

        private static IEnumerable<ClusterRow> Sort(IEnumerable<ClusterRow> src, string? sortBy, bool desc)
        {
            if (string.IsNullOrWhiteSpace(sortBy)) return src;

            if (_doubleGetters.TryGetValue(sortBy, out var getD))
            {
                var withKey = src.Select(r => new { r, k = getD(r), isNull = getD(r) is null });
                var ordered = desc
                    ? withKey.OrderBy(x => x.isNull).ThenByDescending(x => x.k)
                    : withKey.OrderBy(x => x.isNull).ThenBy(x => x.k);
                return ordered.Select(x => x.r);
            }

            if (_intGetters.TryGetValue(sortBy, out var getI))
            {
                var withKey = src.Select(r => new { r, k = getI(r), isNull = getI(r) is null });
                var ordered = desc
                    ? withKey.OrderBy(x => x.isNull).ThenByDescending(x => x.k)
                    : withKey.OrderBy(x => x.isNull).ThenBy(x => x.k);
                return ordered.Select(x => x.r);
            }

            if (_boolGetters.TryGetValue(sortBy, out var getB))
            {
                var withKey = src.Select(r => new { r, k = getB(r), isNull = getB(r) is null });
                var ordered = desc
                    ? withKey.OrderBy(x => x.isNull).ThenByDescending(x => x.k)
                    : withKey.OrderBy(x => x.isNull).ThenBy(x => x.k);
                return ordered.Select(x => x.r);
            }

            if (_stringGetters.TryGetValue(sortBy, out var getS))
            {
                var norm = _stringNormalizers.TryGetValue(sortBy, out var n) ? n : Identity;
                var withKey = src.Select(r => new { r, k = (norm(getS(r)) ?? string.Empty), isNull = getS(r) is null });
                var ordered = desc
                    ? withKey.OrderBy(x => x.isNull).ThenByDescending(x => x.k, StringComparer.OrdinalIgnoreCase)
                    : withKey.OrderBy(x => x.isNull).ThenBy(x => x.k, StringComparer.OrdinalIgnoreCase);
                return ordered.Select(x => x.r);
            }

            // Unknown field → no sort
            return src;
        }

        private static string Identity(string? s) => (s ?? string.Empty).Trim();

        // Light region normalization; add/extend as needed.
        private static string NormalizeRegion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var key = raw.Trim().ToLowerInvariant();

            static string Compact(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray());

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["west us"]   = "westus",
                ["west-us"]   = "westus",
                ["westus"]    = "westus",
                ["west us 2"] = "westus2",
                ["west-us-2"] = "westus2",
                ["westus2"]   = "westus2",

                ["east us"]   = "eastus",
                ["east-us"]   = "eastus",
                ["eastus"]    = "eastus",
                ["east us 2"] = "eastus2",
                ["eastus2"]   = "eastus2",

                ["west europe"] = "westeurope",
                ["westeurope"]  = "westeurope",
                ["north europe"]= "northeurope",
                ["northeurope"] = "northeurope",

                ["southeast asia"] = "southeastasia",
                ["southeastasia"]  = "southeastasia",
                ["east asia"]      = "eastasia",
                ["eastasia"]       = "eastasia"
            };

            if (map.TryGetValue(key, out var v)) return v;

            var compact = Compact(key);
            if (map.TryGetValue(compact, out v)) return v;

            return compact; // best effort canonical
        }

        // ----------------------------- Multi-Query Plans -------------------------

        // Combine multiple Criteria via UNION / INTERSECT, then optional global sort/page.
        public sealed record MultiQueryPlan
        {
            public string Mode { get; init; } = "Intersect"; // "Union" or "Intersect"
            public List<Criteria> Items { get; init; } = new();
            public string? SortBy { get; init; }
            public bool SortDescending { get; init; } = true;
            public int? Skip { get; init; }
            public int? Take { get; init; }
        }

        // We need a stable equality for set ops (use Cluster id as key).
        private sealed class ClusterIdComparer : IEqualityComparer<ClusterRow>
        {
            public bool Equals(ClusterRow? x, ClusterRow? y) =>
                string.Equals(x?.Cluster, y?.Cluster, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(ClusterRow obj) =>
                (obj.Cluster ?? string.Empty).ToLowerInvariant().GetHashCode();
        }

        public static IReadOnlyList<ClusterRow> ApplyMany(IEnumerable<ClusterRow> source, MultiQueryPlan plan)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (plan is null) throw new ArgumentNullException(nameof(plan));
            if (plan.Items is null || plan.Items.Count == 0) return Array.Empty<ClusterRow>();

            var cmp = new ClusterIdComparer();
            IEnumerable<ClusterRow> acc;

            // Seed with first criteria
            acc = Apply(source, plan.Items[0]);

            // Fold the rest
            for (int i = 1; i < plan.Items.Count; i++)
            {
                var next = Apply(source, plan.Items[i]);

                acc = plan.Mode.Equals("Union", StringComparison.OrdinalIgnoreCase)
                    ? acc.Union(next, cmp)
                    : acc.Intersect(next, cmp);
            }

            // Optional global sort + paging
            acc = Sort(acc, plan.SortBy, plan.SortDescending);
            if (plan.Skip is int skip && skip > 0) acc = acc.Skip(skip);
            if (plan.Take is int take && take > 0) acc = acc.Take(take);

            return acc.ToList();
        }
    }
}
