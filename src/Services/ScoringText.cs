#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// Shared text utilities for scoring: flexible weight parsing and factor descriptions.
    /// </summary>
    public static class ScoringText
    {
        private static readonly string[] AgeKeys    = { "age", "cluster age", "clusterage", "age years", "ageyears" };
        private static readonly string[] UtilKeys   = { "util", "utilization", "core utilization", "coreutilization", "effective core utilization", "effectivecoreutilization" };
        private static readonly string[] HealthKeys = { "health", "region health", "regionhealth" };
        private static readonly string[] StrndKeys  = { "stranded", "stranded cores", "strandedcores", "stranded_ratio" };

        public static ScoringService.WeightConfig? ParseWeightsFlexible(string? weightsSpec)
        {
            if (string.IsNullOrWhiteSpace(weightsSpec)) return null;

            // 1) JSON
            try
            {
                var w = JsonSerializer.Deserialize<ScoringService.WeightConfig>(weightsSpec);
                if (w is not null) return Canonicalize(w);
            }
            catch { /* ignore */ }

            // 2) "k=v" pairs (comma separated)
            var dictPairs = new ScoringService.WeightConfig();
            foreach (var part in weightsSpec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2) continue;
                if (double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    dictPairs[CanonFeature(kv[0])] = v;
            }
            if (dictPairs.Count > 0) return dictPairs;

            // 3) Natural language
            var text = weightsSpec.Trim();
            var dictNL = new ScoringService.WeightConfig();

            if (TryFindNumberNearAny(text, AgeKeys, out var age))     dictNL["ClusterAgeYears"] = age;
            if (TryFindNumberNearAny(text, UtilKeys, out var util))   dictNL["EffectiveCoreUtilization"] = util;
            if (TryFindNumberNearAny(text, HealthKeys, out var h))    dictNL["RegionHealthScore"] = h;
            if (TryFindNumberNearAny(text, StrndKeys, out var s))     dictNL["StrandedCoresRatio_DNG"] = s;

            return dictNL.Count == 0 ? null : dictNL;
        }

        public static string GetFactorDescription(string factorName) => factorName switch
        {
            "ClusterAgeYears" => "Age of the cluster in years (older → higher score).",
            "EffectiveCoreUtilization" => "Effective core usage; lower utilization raises decom priority.",
            "RegionHealthScore" => "Region health score; poorer health raises decom priority.",
            "OOSNodeRatio" => "Ratio of out-of-service nodes; higher suggests maintenance issues.",
            "StrandedCoresRatio_DNG" => "Cores stranded due to Do Not Grow status.",
            "StrandedCoresRatio_TIP" => "Cores stranded due to test/dev workloads.",
            "StrandedCoresRatio_32VMs" => "Cores stranded due to 32-core VM constraints.",
            "IsHotRegion" => "Whether the cluster is in a high-demand region (penalizes decom).",
            "DecommissionYearsRemaining" => "Years until planned retirement; fewer years raises priority.",
            "HasSQL" => "Hosts SQL workloads; harder to decommission.",
            "HasSLB" => "Hosts load-balancer workloads; harder to decommission.",
            "HasWARP" => "Hosts WARP workloads; harder to decommission.",
            "SQL_Ratio" => "Proportion of SQL VMs.",
            "NonSpannable_Ratio" => "Proportion of non-spannable utilization.",
            "SpannableUtilizationRatio" => "Spannable utilization ratio.",
            _ => $"Scoring factor: {factorName}"
        };

        private static ScoringService.WeightConfig Canonicalize(ScoringService.WeightConfig w)
        {
            var canon = new ScoringService.WeightConfig();
            foreach (var (k, v) in w) canon[CanonFeature(k)] = v;
            return canon;
        }

        private static string CanonFeature(string key) => key.Trim().ToLowerInvariant() switch
        {
            "age" or "cluster age" or "clusterage" or "age years" or "ageyears"   => "ClusterAgeYears",
            "util" or "utilization" or "core utilization" or "coreutilization"
                or "effective core utilization" or "effectivecoreutilization"     => "EffectiveCoreUtilization",
            "health" or "region health" or "regionhealth"                          => "RegionHealthScore",
            "stranded" or "stranded cores" or "strandedcores" or "stranded_ratio"  => "StrandedCoresRatio_DNG",
            _ => key
        };

        private static bool TryFindNumberNearAny(string text, string[] keys, out double value)
        {
            value = 0;
            foreach (var key in keys)
            {
                var p1 = new Regex(@"\b" + Regex.Escape(key) + @"\b[^\d%]*?[\(]?\s*(\d+(?:[.,]\d+)?)(\s*%)?\s*[\)]?",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var m = p1.Match(text);
                if (!m.Success)
                {
                    var p2 = new Regex(@"[\(]?\s*(\d+(?:[.,]\d+)?)(\s*%)?\s*[\)]?[^\w%]*?\b" + Regex.Escape(key) + @"\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    m = p2.Match(text);
                }
                if (m.Success)
                {
                    var token = m.Groups[1].Value.Replace(',', '.');
                    if (double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        if (m.Groups[2].Success) v /= 100.0;
                        value = v;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
