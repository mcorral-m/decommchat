#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using static MyM365AgentDecommision.Bot.Services.ClusterFilterEngine;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// Hybrid parsing: deterministic first, LLM fallback, then strict validate/normalize.
    /// Exact include/exclude after canonicalization. No region family expansion.
    /// </summary>
    public sealed class HybridParsingService
    {
        private readonly Kernel _kernel;
        private readonly ILogger<HybridParsingService>? _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        private static readonly HashSet<string> AllowedStringFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "Region", "DataCenter", "AvailabilityZone", "DC"
        };

        private static readonly HashSet<string> AllowedDoubleFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "ClusterAgeYears", "CoreUtilization", "EffectiveCoreUtilization",
            "RegionHealthScore", "OOSNodeRatio",
            "StrandedCoresRatio_DNG", "StrandedCoresRatio_TIP", "StrandedCoresRatio_32VMs"
        };

        private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "ClusterAgeYears", "CoreUtilization", "EffectiveCoreUtilization", "RegionHealthScore"
        };

        public HybridParsingService(Kernel kernel, ILogger<HybridParsingService>? log = null)
        {
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            _log = log;
        }

        // ----------------------------- Criteria -----------------------------

        public async Task<Criteria> ParseCriteriaHybridAsync(string text, CancellationToken ct = default)
        {
            var crit = NaturalLanguageCriteriaParser.Parse(text ?? string.Empty);

            var lowConfidence =
                (crit.StringIn is null || crit.StringIn.Count == 0) &&
                (crit.StringNotIn is null || crit.StringNotIn.Count == 0) &&
                (crit.DoubleRanges is null || crit.DoubleRanges.Count == 0);

            if (!lowConfidence)
                return ValidateAndNormalizeCriteria(crit);

            var llmJson = await AskModelForCriteriaJsonAsync(text ?? string.Empty /*, ct*/);
            if (!string.IsNullOrWhiteSpace(llmJson))
            {
                try
                {
                    var llmCrit = JsonSerializer.Deserialize<Criteria>(llmJson, JsonOpts);
                    if (llmCrit is not null)
                        return ValidateAndNormalizeCriteria(llmCrit);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Failed to parse LLM criteria JSON; using deterministic result.");
                }
            }

            return ValidateAndNormalizeCriteria(crit);
        }

        private Criteria ValidateAndNormalizeCriteria(Criteria c)
        {
            // Exact include/exclude maps (canonicalized values)
            var stringIn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var stringNotIn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void AddStringMap(Dictionary<string, HashSet<string>>? src, Dictionary<string, HashSet<string>> dst)
            {
                if (src is null) return;
                foreach (var (field, vals) in src)
                {
                    if (!AllowedStringFields.Contains(field)) continue;

                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in vals ?? Enumerable.Empty<string>())
                    {
                        var s = field.Equals("Region", StringComparison.OrdinalIgnoreCase)
                            ? Normalization.NormalizeRegion(v) // public helper
                            : v?.Trim();

                        if (!string.IsNullOrWhiteSpace(s)) set.Add(s!);
                    }
                    if (set.Count > 0) dst[field] = set;
                }
            }

            AddStringMap(c.StringIn,    stringIn);
            AddStringMap(c.StringNotIn, stringNotIn);

            // Numeric ranges (clamped where appropriate)
            var ranges = new Dictionary<string, DoubleRange>(StringComparer.OrdinalIgnoreCase);
            if (c.DoubleRanges is not null)
            {
                foreach (var (k, r) in c.DoubleRanges)
                {
                    if (!AllowedDoubleFields.Contains(k)) continue;
                    var min = r.Min;
                    var max = r.Max;
                    if (min.HasValue && max.HasValue && min > max)
                        (min, max) = (max, min);

                    static bool IsPct(string key) =>
                        key.Equals("CoreUtilization", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("EffectiveCoreUtilization", StringComparison.OrdinalIgnoreCase) ||
                        key.EndsWith("Ratio", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("Score", StringComparison.OrdinalIgnoreCase);

                    if (IsPct(k))
                    {
                        if (min.HasValue) min = Math.Clamp(min.Value, 0d, 100d);
                        if (max.HasValue) max = Math.Clamp(max.Value, 0d, 100d);
                    }

                    ranges[k] = new DoubleRange(min, max);
                }
            }

            var finalSortBy = (!string.IsNullOrWhiteSpace(c.SortBy) && AllowedSortFields.Contains(c.SortBy!))
                ? c.SortBy!
                : "ClusterAgeYears";
            var finalSortDescending = c.SortDescending;
            var finalTake = Math.Min(Math.Max(1, c.Take ?? 10), 1000);

            return new Criteria
            {
                StringIn = stringIn,
                StringNotIn = stringNotIn,
                DoubleRanges = ranges,
                SortBy = finalSortBy,
                SortDescending = finalSortDescending,
                Take = finalTake
            };
        }

        private async Task<string?> AskModelForCriteriaJsonAsync(string userText/*, CancellationToken ct*/)
        {
            var prompt = $$"""
            You are a strict JSON generator. Output ONLY a JSON object matching:

            {
              "StringIn":      { "<FieldName>": ["value1","value2"] },
              "StringNotIn":   { "<FieldName>": ["value1","value2"] },
              "DoubleRanges":  { "<NumericField>": { "Min": 0, "Max": 10 } },
              "SortBy":        "ClusterAgeYears | CoreUtilization | EffectiveCoreUtilization | RegionHealthScore",
              "SortDescending": true,
              "Take":          10
            }

            Rules:
            - Allowed string fields: Region, DataCenter, AvailabilityZone, DC.
            - Allowed numeric fields: ClusterAgeYears, CoreUtilization, EffectiveCoreUtilization, RegionHealthScore, OOSNodeRatio, StrandedCoresRatio_DNG, StrandedCoresRatio_TIP, StrandedCoresRatio_32VMs.
            - Normalize region names to Azure canonical (e.g., "eastUS2" -> "eastus2").
            - Omit fields you cannot infer.
            - Do NOT wrap in backticks. Do NOT include any explanation.

            User request:
            {{userText}}
            """;

            try
            {
                var result = await _kernel.InvokePromptAsync(prompt, new KernelArguments());
                var text = result?.ToString() ?? string.Empty;
                return ExtractJsonBlock(text);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "AskModelForCriteriaJsonAsync failed");
                return null;
            }
        }

        // ----------------------------- Weights -----------------------------

        public async Task<(ScoringService.WeightConfig weightsRaw, ScoringService.WeightConfig weightsNormalized)>
            ParseWeightsHybridAsync(string? text, CancellationToken ct = default)
        {
            var raw = ScoringText.ParseWeightsFlexible(text);
            if (raw is not null && raw.Any(kv => kv.Value > 0))
            {
                var norm = ScoringService.Rebalance(new ScoringService.WeightConfig(raw));
                return (new ScoringService.WeightConfig(raw), norm);
            }

            var llmJson = await AskModelForWeightsJsonAsync(text ?? string.Empty /*, ct*/);
            if (!string.IsNullOrWhiteSpace(llmJson))
            {
                try
                {
                    var draft = JsonSerializer.Deserialize<ScoringService.WeightConfig>(llmJson, JsonOpts)
                               ?? new ScoringService.WeightConfig();
                    var validated = ValidateWeights(draft);
                    var norm = ScoringService.Rebalance(new ScoringService.WeightConfig(validated));
                    return (validated, norm);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Failed to parse LLM weights JSON; defaulting.");
                }
            }

            var def = ScoringService.DefaultWeights();
            var defNorm = ScoringService.Rebalance(new ScoringService.WeightConfig(def));
            return (def, defNorm);
        }

        private static ScoringService.WeightConfig ValidateWeights(ScoringService.WeightConfig draft)
        {
            var canon = new ScoringService.WeightConfig();
            foreach (var (k, v) in draft)
            {
                if (double.IsNaN(v) || v < 0) continue;
                var key = CanonKey(k);
                canon[key] = v;
            }
            if (canon.Count == 0) canon = ScoringService.DefaultWeights();
            return canon;

            static string CanonKey(string key)
            {
                var probe = $"{key}=1";
                var parsed = ScoringText.ParseWeightsFlexible(probe);
                return parsed?.Keys.FirstOrDefault() ?? key;
            }
        }

        private async Task<string?> AskModelForWeightsJsonAsync(string userText/*, CancellationToken ct*/)
        {
            var prompt = $$"""
            You are a strict JSON generator. Output ONLY a JSON object mapping factor names to numeric weights.
            Allowed keys (use these exact spellings if present):
              "ClusterAgeYears","EffectiveCoreUtilization","RegionHealthScore","OOSNodeRatio",
              "StrandedCoresRatio_DNG","StrandedCoresRatio_TIP","StrandedCoresRatio_32VMs",
              "HasSQL","HasSLB","HasWARP","SQL_Ratio","NonSpannable_Ratio","SpannableUtilizationRatio"

            Rules:
            - Omit keys you cannot infer.
            - Weights must be non-negative numbers.
            - No backticks. No explanations.

            User request:
            {{userText}}
            """;

            try
            {
                var result = await _kernel.InvokePromptAsync(prompt, new KernelArguments());
                var text = result?.ToString() ?? string.Empty;
                return ExtractJsonBlock(text);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "AskModelForWeightsJsonAsync failed");
                return null;
            }
        }

        // ----------------------------- Utils -----------------------------

        private static string? ExtractJsonBlock(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var cleaned = Regex.Replace(text, @"^```(?:json)?\s*|\s*```$", string.Empty, RegexOptions.Multiline);

            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return cleaned.Substring(start, end - start + 1).Trim();
            }
            return cleaned.Trim();
        }
    }
}
