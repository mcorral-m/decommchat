// DecomQueryOrchestratorPlugin.cs
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
using Microsoft.SemanticKernel;                    // [KernelFunction]
using Microsoft.SemanticKernel.ChatCompletion;    // IChatCompletionService, ChatHistory

using MyM365AgentDecommision.Bot.Plugins;         // ScoringPlugin, EligibilityPlugin, ExportPlugin, AuditPlugin

namespace MyM365AgentDecommision.Bot.Orchestration;

public sealed class DecomQueryOrchestratorPlugin
{
    private readonly IChatCompletionService _chat;
    private readonly ScoringPlugin _scoring;
    private readonly EligibilityPlugin? _eligibility;
    private readonly ExportPlugin? _export;
    private readonly AuditPlugin? _audit;
    private readonly ILogger<DecomQueryOrchestratorPlugin> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false
    };

    public DecomQueryOrchestratorPlugin(
        IChatCompletionService chat,
        ScoringPlugin scoring,
        ILogger<DecomQueryOrchestratorPlugin> log,
        EligibilityPlugin? eligibility = null,
        ExportPlugin? export = null,
        AuditPlugin? audit = null)
    {
        _chat = chat;
        _scoring = scoring;
        _eligibility = eligibility;
        _export = export;
        _audit = audit;
        _log = log;
    }

    // ========================================================================
    // Public Entry
    // ========================================================================

    [KernelFunction, Description("Execute a natural-language decommissioning request (filter, score, rank, explain, compare, what-if, export).")]
    public async Task<object> ExecuteDecomQuery(
        [Description("Example: 'Top 10 candidates, exclude westus2; age>=7, util<12%; group by region'")]
        string query,
        [Description("Optional session key for weight persistence etc.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        // 1) NL → JSON plan
        var plan = await ParseQueryToPlanAsync(query, ct);
        Canonicalize(plan);

        // 2) Weights (optional overrides from plan)
        var weightsJson = plan.Weights is { Count: > 0 }
            ? SerializeWeights(MergeAndRebalance(DefaultWeights(), plan.Weights!, plan.NormalizeRest ?? true))
            : null;

        // 3) Build FilterCriteria JSON from plan conditions
        var criteriaJson = BuildCriteriaJson(plan);

        // 4) Delegate by intent to existing plugins
        var topN = plan.TopN > 0 ? plan.TopN : 10;
        var page = plan.Output?.Page is > 0 ? plan.Output!.Page : 1;
        var pageSize = plan.Output?.PageSize is > 0 ? plan.Output!.PageSize : 25;

        object result;

        switch ((plan.Intent ?? "rank").ToLowerInvariant())
        {
            case "group_topn":
            case "rank":
            case "top":
            case "score":
            {
                result = await _scoring.ScoreTopN(
                    topN: topN,
                    weightsJson: weightsJson,
                    criteriaJson: criteriaJson,
                    groupBy: plan.GroupBy,
                    page: page,
                    pageSize: pageSize,
                    sessionKey: sessionKey,
                    ct: ct);
                break;
            }

            case "explain":
            {
                if (string.IsNullOrWhiteSpace(plan.ExplainId))
                    return new { Kind = "Explain", Error = "No cluster id provided." };

                result = await _scoring.Explain(
                    clusterId: plan.ExplainId!,
                    weightsJson: weightsJson,
                    sessionKey: sessionKey,
                    ct: ct);
                break;
            }

            case "compare":
            {
                var ids = plan.CompareIds ?? Array.Empty<string>();
                if (ids.Length == 0) return new { Kind = "Compare", Error = "No cluster ids provided." };

                result = await _scoring.Compare(
                    clusterIdsJson: JsonSerializer.Serialize(ids, JsonOpts),
                    weightsJson: weightsJson,
                    sessionKey: sessionKey,
                    ct: ct);
                break;
            }

            case "whatif":
            {
                if (string.IsNullOrWhiteSpace(plan.ExplainId))
                    return new { Kind = "WhatIf", Error = "Provide a cluster id (explainId) for the what-if." };
                if (plan.Edits is null || plan.Edits.Count == 0)
                    return new { Kind = "WhatIf", Error = "Provide edits, e.g. {\"CoreUtilization\":\"25%\"}." };

                var editsJson = JsonSerializer.Serialize(plan.Edits, JsonOpts);

                result = await _scoring.Sensitivity(
                    clusterId: plan.ExplainId!,
                    editsJson: editsJson,
                    weightsJson: weightsJson,
                    sessionKey: sessionKey,
                    ct: ct);
                break;
            }

            default:
            {
                // Fallback to ranking behavior
                result = await _scoring.ScoreTopN(
                    topN: topN,
                    weightsJson: weightsJson,
                    criteriaJson: criteriaJson,
                    groupBy: plan.GroupBy,
                    page: page,
                    pageSize: pageSize,
                    sessionKey: sessionKey,
                    ct: ct);
                break;
            }
        }

        // 5) Optional: basic audit hook
        // Your current AuditPlugin in this repo doesn’t expose LogNote().
        // If you later add a method (e.g., LogEvent or LogRunConfig), call it here.
        // try { _audit?.LogEvent("NL Query executed", JsonSerializer.Serialize(new { query, plan, sessionKey }, JsonOpts)); } catch {}

        return result;
    }

    // ========================================================================
    // LLM Planner
    // ========================================================================

    private async Task<Plan> ParseQueryToPlanAsync(string user, CancellationToken ct)
    {
        var system = """
You are a planner for a decommissioning assistant. Convert the user's request into a JSON plan.
ONLY return JSON (no prose). If something is unspecified, omit it.

Plan schema:
{
  "intent": "rank|group_topn|explain|compare|whatif|score",
  "topN": 10,
  "groupBy": "Region|DataCenter|...",
  "conditions": [
     {"field":"Region","op":"NotIn","value":["westus2"]},
     {"field":"ClusterAgeYears","op":"Gte","value":7},
     {"field":"CoreUtilization","op":"Lt","value":0.12},
     {"field":"Tenants","op":"Contains","value":"slb"}
  ],
  "weights": { "ClusterAgeYears":0.2, "EffectiveCoreUtilization":0.3, "RegionHealthScore":0.1 },
  "normalizeRest": true,
  "compareIds": ["XYZ","ABC"],
  "explainId": "XYZ",
  "edits": { "CoreUtilization": "25%" },
  "output": { "format":"table|json", "columns":["Cluster","Region","AgeYears","UtilPct","Score"], "page":1, "pageSize":25 }
}
Field synonyms to map:
- Age/AgeYears => ClusterAgeYears
- Utilization/Util/UtilPct => CoreUtilization  (percent strings like "12%" mean 0.12)
- OOS/OOSRatio => OutOfServiceNodes/OOSNodeRatio
- Health => RegionHealthScore
- DC => DataCenter
- AZ => AvailabilityZone
""";

        var examples = """
USER: Top 10 candidates, exclude westus2
JSON: {"intent":"rank","topN":10,"conditions":[{"field":"Region","op":"NotIn","value":["westus2"]}]}

USER: Top 5 per region with age>=7y and util<12%
JSON: {"intent":"group_topn","topN":5,"groupBy":"Region","conditions":[{"field":"ClusterAgeYears","op":"Gte","value":7},{"field":"CoreUtilization","op":"Lt","value":0.12}]}

USER: Explain BN4PrdApp05
JSON: {"intent":"explain","explainId":"BN4PrdApp05"}

USER: Compare BN4PrdApp05 vs BL5PrdApp30
JSON: {"intent":"compare","compareIds":["BN4PrdApp05","BL5PrdApp30"]}

USER: Top 12 with weights age=0.6 util=0.2 health=0.4
JSON: {"intent":"rank","topN":12,"weights":{"ClusterAgeYears":0.6,"EffectiveCoreUtilization":0.2,"RegionHealthScore":0.4},"normalizeRest":true}
""";

        var chat = new ChatHistory();
        chat.AddSystemMessage(system);
        chat.AddSystemMessage(examples);
        chat.AddUserMessage(user);

        // SK signature compatibility: (history, settings=null, kernel=null, cancellationToken)
        var msg = await _chat.GetChatMessageContentAsync(chat, null, null, ct);
        var content = msg.Content?.Trim();

        if (string.IsNullOrWhiteSpace(content))
            return new Plan(); // minimal default

        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var idx = content.IndexOf('\n');
            if (idx >= 0) content = content[(idx + 1)..].Trim();
            if (content.EndsWith("```", StringComparison.Ordinal)) content = content[..^3].Trim();
        }

        var plan = SafeDeserialize<Plan>(content) ?? new Plan();
        return plan;
    }

    // ========================================================================
    // Plan model & helpers
    // ========================================================================

    private sealed class Plan
    {
        public string? Intent { get; set; } = "rank";
        public int TopN { get; set; } = 10;
        public string? GroupBy { get; set; }
        public List<Cond>? Conditions { get; set; }
        public Dictionary<string, double>? Weights { get; set; }
        public bool? NormalizeRest { get; set; }
        public string[]? CompareIds { get; set; }
        public string? ExplainId { get; set; }
        public Dictionary<string, object?>? Edits { get; set; }
        public OutputSpec? Output { get; set; }
    }

    private sealed class Cond
    {
        public string Field { get; set; } = "";
        public string Op { get; set; } = "Eq"; // Eq|Ne|Gt|Gte|Lt|Lte|In|NotIn|Contains|StartsWith|EndsWith
        public JsonElement Value { get; set; }
    }

    private sealed class OutputSpec
    {
        public string? Format { get; set; }    // "table" | "json"
        public List<string>? Columns { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
    }

    private static readonly Dictionary<string, string> Canon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Age"] = "ClusterAgeYears",
        ["AgeYears"] = "ClusterAgeYears",
        ["Util"] = "CoreUtilization",
        ["Utilization"] = "CoreUtilization",
        ["UtilPct"] = "CoreUtilization",
        ["OOS"] = "OutOfServiceNodes",
        ["OOSRatio"] = "OOSNodeRatio",
        ["Health"] = "RegionHealthScore",
        ["DC"] = "DataCenter",
        ["AZ"] = "AvailabilityZone",
    };

    private static void Canonicalize(Plan plan)
    {
        if (plan.Conditions != null)
        {
            foreach (var c in plan.Conditions)
            {
                if (Canon.TryGetValue(c.Field, out var mapped)) c.Field = mapped;

                c.Op = c.Op switch
                {
                    ">=" or "gte" or "GTE" => "Gte",
                    ">" or "gt" or "GT" => "Gt",
                    "<=" or "lte" or "LTE" => "Lte",
                    "<" or "lt" or "LT" => "Lt",
                    "!=" or "<>" or "ne" or "NE" => "Ne",
                    "=" or "eq" or "EQ" => "Eq",
                    "notin" or "NOTIN" => "NotIn",
                    "in" or "IN" => "In",
                    "contains" or "CONTAINS" => "Contains",
                    "startswith" or "STARTSWITH" => "StartsWith",
                    "endswith" or "ENDSWITH" => "EndsWith",
                    _ => c.Op
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.GroupBy) && Canon.TryGetValue(plan.GroupBy!, out var g))
            plan.GroupBy = g;
    }

    private static string? BuildCriteriaJson(Plan plan)
    {
        if (plan.Conditions is null || plan.Conditions.Count == 0) return null;

        var list = new List<object>();
        foreach (var c in plan.Conditions)
        {
            object? val = ConvertJsonValue(c.Value);

            // allow "%": "12%" -> 0.12 for percent-like fields
            if (val is string s && s.EndsWith("%", StringComparison.Ordinal))
            {
                if (double.TryParse(s.TrimEnd('%'), out var d)) val = d / 100.0;
            }

            list.Add(new { Field = c.Field, Op = c.Op, Value = val });
        }

        var payload = new { And = list };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private static object? ConvertJsonValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetDouble(out var d) ? d : (object?)null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => el.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.Null => null,
            _ => el.ToString()
        };
    }

    // ========================================================================
    // Weights helpers
    // ========================================================================

    private static Dictionary<string, double> DefaultWeights() => new(StringComparer.OrdinalIgnoreCase)
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

    private static Dictionary<string, double> MergeAndRebalance(
        Dictionary<string, double> baseline,
        IDictionary<string, double> overrides,
        bool normalizeRest)
    {
        var result = new Dictionary<string, double>(baseline, StringComparer.OrdinalIgnoreCase);

        var clean = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides)
        {
            if (!result.ContainsKey(k)) continue;
            clean[k] = Math.Max(0, v);
        }

        foreach (var (k, v) in clean) result[k] = v;

        if (normalizeRest)
        {
            var sumOverrides = clean.Values.Sum();
            if (sumOverrides < 1.0 - 1e-12)
            {
                var remaining = 1.0 - sumOverrides;
                var nonKeys = result.Keys.Where(k => !clean.ContainsKey(k)).ToArray();
                var nonSum = nonKeys.Sum(k => result[k]);
                if (nonSum > 0)
                {
                    foreach (var k in nonKeys)
                        result[k] = result[k] * (remaining / nonSum);
                }
            }
            else
            {
                foreach (var k in result.Keys.ToList())
                    if (!clean.ContainsKey(k)) result[k] = 0;
            }
        }

        var sum = Math.Max(1e-9, result.Values.Sum());
        foreach (var k in result.Keys.ToList())
            result[k] = result[k] / sum;

        return result;
    }

    private static string SerializeWeights(Dictionary<string, double> w)
        => JsonSerializer.Serialize(w, JsonOpts);

    // ========================================================================
    // Safe JSON
    // ========================================================================

    private static T? SafeDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return default; }
    }
}
