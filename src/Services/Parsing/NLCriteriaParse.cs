#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// Deterministic NL → Criteria parser (exact include/exclude after canonicalization).
    /// No region "family" expansion: eastus ≠ eastus2.
    /// </summary>
    public static class NaturalLanguageCriteriaParser
    {
        public static ClusterFilterEngine.Criteria Parse(string text)
        {
            var t = " " + (text ?? string.Empty).Trim() + " ";

            var stringIn     = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var stringNotIn  = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var doubleRanges = new Dictionary<string, ClusterFilterEngine.DoubleRange>(StringComparer.OrdinalIgnoreCase);

            string? sortBy = null;
            bool?   sortDescending = null;
            int?    take = null;

            // ---------- Regions include/exclude (mixed case OK) ----------
            foreach (Match m in Regex.Matches(t, @"\bexclude\s+(?:region\s+)?([A-Za-z0-9\s\-]+?)(?=[,.;]|$)", RegexOptions.IgnoreCase))
                AddString("Region", m.Groups[1].Value, stringNotIn);
            foreach (Match m in Regex.Matches(t, @"\b(?:only|include)\s+(?:region\s+)?([A-Za-z0-9\s\-]+?)(?=[,.;]|$)", RegexOptions.IgnoreCase))
                AddString("Region", m.Groups[1].Value, stringIn);
            foreach (Match m in Regex.Matches(t, @"\bregion\s+([A-Za-z0-9\-]+)\b", RegexOptions.IgnoreCase))
                AddString("Region", m.Groups[1].Value, stringIn);

            // ---------- Data center include/exclude ----------
            foreach (Match m in Regex.Matches(t, @"\bexclude\s+data\s*center\s+([A-Za-z0-9\-]+)\b", RegexOptions.IgnoreCase))
                AddString("DataCenter", m.Groups[1].Value, stringNotIn);
            foreach (Match m in Regex.Matches(t, @"\b(?:only|include)\s+data\s*center\s+([A-Za-z0-9\-]+)\b", RegexOptions.IgnoreCase))
                AddString("DataCenter", m.Groups[1].Value, stringIn);
            foreach (Match m in Regex.Matches(t, @"\bdata\s*center\s+([A-Za-z0-9\-]+)\b", RegexOptions.IgnoreCase))
                AddString("DataCenter", m.Groups[1].Value, stringIn);

            // ---------- Age constraints ----------
            foreach (Match m in Regex.Matches(t, @"\bage\s*(>=|≤|<=|>|<)\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase))
                ApplyComparator(doubleRanges, "ClusterAgeYears", m.Groups[1].Value, ParseNum(m.Groups[2].Value));

            foreach (Match m in Regex.Matches(t, @"\b(older than|at least|minimum|min)\s*(\d+(?:[.,]\d+)?)\s*(?:years?|yrs?)", RegexOptions.IgnoreCase))
                SetDoubleMin(doubleRanges, "ClusterAgeYears", ParseNum(m.Groups[2].Value));
            foreach (Match m in Regex.Matches(t, @"\b(younger than|under|less than|maximum|max)\s*(\d+(?:[.,]\d+)?)\s*(?:years?|yrs?)", RegexOptions.IgnoreCase))
                SetDoubleMax(doubleRanges, "ClusterAgeYears", ParseNum(m.Groups[2].Value));

            foreach (Match m in Regex.Matches(t, @"\b(?:age\s*)?(?:between|from)\s*(\d+(?:[.,]\d+)?)\s*(?:to|and)\s*(\d+(?:[.,]\d+)?)\s*(?:years?|yrs?)", RegexOptions.IgnoreCase))
            {
                var a = ParseNum(m.Groups[1].Value);
                var b = ParseNum(m.Groups[2].Value);
                var (min, max) = a <= b ? (a, b) : (b, a);
                doubleRanges["ClusterAgeYears"] = new ClusterFilterEngine.DoubleRange(min, max);
            }

            // ---------- Utilization constraints ----------
            foreach (Match m in Regex.Matches(t, @"\b(effective\s+)?util(?:ization)?\s*(>=|<=|>|<)\s*(\d+(?:[.,]\d+)?)\s*%?", RegexOptions.IgnoreCase))
            {
                var effective = m.Groups[1].Success;
                var op = m.Groups[2].Value;
                var v = ParseNum(m.Groups[3].Value);
                var field = effective ? "EffectiveCoreUtilization" : "CoreUtilization";
                ApplyComparator(doubleRanges, field, op, v);
            }

            foreach (Match m in Regex.Matches(t, @"\b(util(?:ization)?\s+)?(under|less than|below|at most|maximum|max)\s*(\d+(?:[.,]\d+)?)\s*%?", RegexOptions.IgnoreCase))
                SetDoubleMax(doubleRanges, "CoreUtilization", ParseNum(m.Groups[3].Value));
            foreach (Match m in Regex.Matches(t, @"\b(util(?:ization)?\s+)?(over|more than|above|at least|minimum|min)\s*(\d+(?:[.,]\d+)?)\s*%?", RegexOptions.IgnoreCase))
                SetDoubleMin(doubleRanges, "CoreUtilization", ParseNum(m.Groups[3].Value));

            // ---------- Sorting ----------
            if (Regex.IsMatch(t, @"\boldest\s+first\b", RegexOptions.IgnoreCase))
            {
                sortBy = "ClusterAgeYears"; sortDescending = true;
            }
            else if (Regex.IsMatch(t, @"\byoungest\s+first\b", RegexOptions.IgnoreCase))
            {
                sortBy = "ClusterAgeYears"; sortDescending = false;
            }
            else
            {
                var sm = Regex.Match(t, @"\bsort\s+by\s+(age|cluster\s*age|util(?:ization)?|effective\s+util(?:ization)?)\s*(asc|desc)?", RegexOptions.IgnoreCase);
                if (sm.Success)
                {
                    var field = sm.Groups[1].Value.ToLowerInvariant();
                    sortBy = field.Contains("util")
                        ? (field.Contains("effective") ? "EffectiveCoreUtilization" : "CoreUtilization")
                        : "ClusterAgeYears";
                    sortDescending = !string.Equals(sm.Groups[2].Value, "asc", StringComparison.OrdinalIgnoreCase);
                }
            }

            // ---------- Top-N ----------
            var top = Regex.Match(t, @"\btop\s+(\d+)\b", RegexOptions.IgnoreCase);
            if (top.Success && int.TryParse(top.Groups[1].Value, out var takeParsed) && takeParsed > 0)
                take = takeParsed;

            // ---------- Defaults ----------
            var finalSortBy         = string.IsNullOrWhiteSpace(sortBy) ? "ClusterAgeYears" : sortBy!;
            var finalSortDescending = sortDescending ?? true;
            var finalTake           = Math.Max(1, take ?? 10);

            return new ClusterFilterEngine.Criteria
            {
                StringIn     = stringIn,
                StringNotIn  = stringNotIn,
                DoubleRanges = doubleRanges,
                SortBy = finalSortBy,
                SortDescending = finalSortDescending,
                Take = finalTake
            };

            // -------- local helpers --------

            static void AddString(string field, string? value, Dictionary<string, HashSet<string>> bag)
            {
                if (string.IsNullOrWhiteSpace(value)) return;

                var v = field.Equals("Region", StringComparison.OrdinalIgnoreCase)
                    ? Normalization.NormalizeRegion(value)  // public helper
                    : value.Trim();

                if (!bag.TryGetValue(field, out var set)) bag[field] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(v);
            }

            static double ParseNum(string s) =>
                double.Parse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

            static void ApplyComparator(
                Dictionary<string, ClusterFilterEngine.DoubleRange> map,
                string field,
                string op,
                double v)
            {
                map.TryGetValue(field, out var existing);
                double? min = existing?.Min;
                double? max = existing?.Max;

                switch (op)
                {
                    case ">":  min = Math.BitIncrement(v); break;
                    case ">=": min = v; break;
                    case "<":  max = Math.BitDecrement(v); break;
                    case "<=":
                    case "≤":  max = v; break;
                }

                map[field] = new ClusterFilterEngine.DoubleRange(min, max);
            }

            static void SetDoubleMin(Dictionary<string, ClusterFilterEngine.DoubleRange> map, string key, double min)
            {
                map.TryGetValue(key, out var ex);
                map[key] = new ClusterFilterEngine.DoubleRange(min, ex?.Max);
            }

            static void SetDoubleMax(Dictionary<string, ClusterFilterEngine.DoubleRange> map, string key, double max)
            {
                map.TryGetValue(key, out var ex);
                map[key] = new ClusterFilterEngine.DoubleRange(ex?.Min, max);
            }
        }
    }
}
