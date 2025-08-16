// EligibilityPlugin.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;                         // [KernelFunction]
using MyM365AgentDecommision.Bot.Interfaces;           // IClusterDataProvider
using MyM365AgentDecommision.Bot.Models;               // ClusterRow
using MyM365AgentDecommision.Bot.Services;             // FilteringEngine, EligibilityEngine, EligibilityRules, IEligibilityRulesStore, ClusterRowAccessors

namespace MyM365AgentDecommision.Bot.Plugins;

/// <summary>
/// Eligibility (pass/fail + reasons), summary, and "eligible soon" projections.
/// Wraps EligibilityEngine + FilteringEngine so the model can call deterministic functions.
/// </summary>
public sealed class EligibilityPlugin
{
    private readonly IClusterDataProvider _data;
    private readonly FilteringEngine _filter;
    private readonly EligibilityEngine _elig;
    private readonly IEligibilityRulesStore _rulesStore;
    private readonly ILogger<EligibilityPlugin> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public EligibilityPlugin(
        IClusterDataProvider data,
        FilteringEngine filter,
        EligibilityEngine elig,
        IEligibilityRulesStore rulesStore,
        ILogger<EligibilityPlugin> log)
    {
        _data = data;
        _filter = filter;
        _elig = elig;
        _rulesStore = rulesStore;
        _log = log;
    }

    // -------------------------------------------------------------------------
    // Templates & validation
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Return a template of allowed eligibility rule fields and example values.")]
    public Task<object> GetRulesTemplate()
    {
        var template = new
        {
            MinAgeYears = 6.0,                // double? years
            MaxUtilization = 0.30,            // double? 0..1
            IgnoreSpecialWorkloads = false,   // bool
            IncludeRegions = Array.Empty<string>(),
            ExcludeRegions = Array.Empty<string>(),
            IncludeDataCenters = Array.Empty<string>(), // optional (ClusterRow.DataCenter)
            ExcludeDataCenters = Array.Empty<string>()
        };
        return Task.FromResult<object>(template);
    }

    [KernelFunction, Description("Validate a rules JSON payload and return the normalized rule object.")]
    public Task<object> ValidateRules(
        [Description("Rules JSON (e.g., {\"MinAgeYears\":6,\"MaxUtilization\":0.30})")] string rulesJson)
    {
        var rules = ParseRules(rulesJson);
        return Task.FromResult<object>(rules);
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Save rules for a session key.")]
    public Task<string> SaveRules(
        [Description("Rules JSON. See GetRulesTemplate for fields.")] string rulesJson,
        [Description("Session key to associate rules with.")] string sessionKey)
    {
        var rules = ParseRules(rulesJson);
        _rulesStore.Set(sessionKey, rules);
        return Task.FromResult("OK");
    }

    [KernelFunction, Description("Reset session rules (stores an empty/default rule set).")]
    public Task<string> ResetRules([Description("Session key to clear")] string sessionKey)
    {
        _rulesStore.Set(sessionKey, new EligibilityRules());
        return Task.FromResult("OK");
    }

    // -------------------------------------------------------------------------
    // Apply / Explain / Summary
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Apply eligibility rules to (optionally filtered) rows.")]
    public async Task<object> Apply(
        [Description("Optional FilterCriteria JSON.")] string? criteriaJson = null,
        [Description("Optional rules JSON. If omitted, uses session or default per fallback.")] string? rulesJson = null,
        [Description("Fallback policy: UseStoreThenDefault | UseDefaultWhenMissing | RequireRules")] string fallback = "UseStoreThenDefault",
        [Description("Optional sessionKey for persisted rules.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var policy = ParsePolicy(fallback);
        var rules  = !string.IsNullOrWhiteSpace(rulesJson) ? ParseRules(rulesJson) : null;

        var rows = await _data.GetClusterRowDataAsync(ct);
        var criteria = ParseCriteria(criteriaJson);
        var selected = _filter.Apply(rows, criteria).ToList();

        var results = _elig.EvaluateAll(selected, rules, policy, sessionKey, _rulesStore)
                           .Select(r => new { r.Cluster, r.Eligible, r.Reasons })
                           .ToList();

        return new
        {
            Kind = "Eligibility",
            Count = results.Count,
            Items = results,
            Fallback = policy.ToString(),
            AppliedRules = rules ?? (_rulesStore.Get(sessionKey ?? string.Empty) ?? new EligibilityRules())
        };
    }

    [KernelFunction, Description("Explain eligibility (pass/fail + reasons) for a single cluster.")]
    public async Task<object> ExplainCluster(
        [Description("Cluster id/name.")] string clusterId,
        [Description("Optional rules JSON.")] string? rulesJson = null,
        [Description("Fallback policy: UseDefaultWhenMissing | UseStoreThenDefault | RequireRules")] string fallback = "UseDefaultWhenMissing",
        [Description("Optional sessionKey for persisted rules.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var policy = ParsePolicy(fallback);
        var rules  = !string.IsNullOrWhiteSpace(rulesJson) ? ParseRules(rulesJson) : null;

        var row = await _data.GetClusterRowDetailsAsync(clusterId, ct);
        if (row is null) return new { Error = $"Cluster '{clusterId}' not found." };

        var r = _elig.EvaluateWithFallback(row, rules, policy, sessionKey, _rulesStore);
        return new
        {
            Kind = "ExplainEligibility",
            Cluster = r.Cluster,
            Eligible = r.Eligible,
            Reasons = r.Reasons,
            Fallback = policy.ToString(),
            AppliedRules = rules ?? (_rulesStore.Get(sessionKey ?? string.Empty) ?? new EligibilityRules())
        };
    }

    [KernelFunction, Description("Summarize eligibility pass/fail counts grouped by a field (e.g., Region or DataCenter).")]
    public async Task<object> SummaryBy(
        [Description("Grouping field (e.g., Region, DataCenter).")] string field = "Region",
        [Description("Optional FilterCriteria JSON.")] string? criteriaJson = null,
        [Description("Optional rules JSON.")] string? rulesJson = null,
        [Description("Fallback policy: UseStoreThenDefault | UseDefaultWhenMissing | RequireRules")] string fallback = "UseStoreThenDefault",
        [Description("Optional sessionKey for persisted rules.")] string? sessionKey = null,
        CancellationToken ct = default)
    {
        var policy = ParsePolicy(fallback);
        var rules  = !string.IsNullOrWhiteSpace(rulesJson) ? ParseRules(rulesJson) : null;

        var rows = await _data.GetClusterRowDataAsync(ct);
        var criteria = ParseCriteria(criteriaJson);
        var selected = _filter.Apply(rows, criteria).ToList();

        if (!ClusterRowAccessors.TryGet(field, out var acc))
        {
            return new { Error = $"Unknown group-by field '{field}'. Use ListFields/DescribeSchema to discover." };
        }

        var evals = _elig.EvaluateAll(selected, rules, policy, sessionKey, _rulesStore).ToList();

        var grouped = evals.GroupBy(e =>
        {
            var row = selected.FirstOrDefault(r => (r.Cluster ?? r.ClusterId ?? "") == e.Cluster);
            return row is null ? "(unknown)" : (acc.Getter(row)?.ToString() ?? "(unknown)");
        }, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new
        {
            Group = g.Key,
            Total = g.Count(),
            Pass = g.Count(x => x.Eligible),
            Fail = g.Count(x => !x.Eligible)
        })
        .ToList();

        return new
        {
            Kind = "EligibilitySummary",
            GroupBy = field,
            Groups = grouped,
            Fallback = policy.ToString(),
            AppliedRules = rules ?? (_rulesStore.Get(sessionKey ?? string.Empty) ?? new EligibilityRules())
        };
    }

    // -------------------------------------------------------------------------
    // Eligible soon (age threshold projection)
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Which clusters become eligible within N days if only age changes?")]
    public async Task<object> EligibleSoon(
        [Description("Lookahead window in days (default 90).")] int days = 90,
        [Description("Optional FilterCriteria JSON to pre-filter the cohort.")] string? criteriaJson = null,
        [Description("Optional rules JSON (must include MinAgeYears to be meaningful).")] string? rulesJson = null,
        CancellationToken ct = default)
    {
        var rows = await _data.GetClusterRowDataAsync(ct);
        var criteria = ParseCriteria(criteriaJson);
        var selected = _filter.Apply(rows, criteria).ToList();

        var rules = !string.IsNullOrWhiteSpace(rulesJson) ? ParseRules(rulesJson) : new EligibilityRules { MinAgeYears = 6 };

        // We simulate aging by +days/365.0 and re-evaluate.
        var yearsDelta = Math.Max(0.0, days) / 365.0;

        var soon = new List<object>();
        foreach (var r in selected)
        {
            // First check current eligibility (if already eligible, skip)
            var now = _elig.Evaluate(r, rules);
            if (now.Eligible) continue;

            // Clone and age the cluster
            var aged = CloneRow(r);
            var currentAge = aged.ClusterAgeYears ?? 0;
            aged.ClusterAgeYears = currentAge + yearsDelta;

            var then = _elig.Evaluate(aged, rules);
            if (then.Eligible)
            {
                soon.Add(new
                {
                    Cluster = r.Cluster ?? r.ClusterId ?? "(unknown)",
                    CurrentAgeYears = currentAge,
                    AgeYearsInDays = yearsDelta,
                    EligibleInDays = days,
                    ReasonsNow = now.Reasons,
                    Notes = "Eligible when only age increases; other failing conditions must already be satisfied."
                });
            }
        }

        return new
        {
            Kind = "EligibleSoon",
            Days = days,
            Count = soon.Count,
            Items = soon
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static EligibilityRules ParseRules(string json)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<EligibilityRules>(json, JsonOpts) ?? new EligibilityRules();
            // Normalize arrays to non-null
            rules = new EligibilityRules
            {
                MinAgeYears = rules.MinAgeYears,
                MaxUtilization = rules.MaxUtilization,
                IgnoreSpecialWorkloads = rules.IgnoreSpecialWorkloads,
                IncludeRegions = rules.IncludeRegions ?? Array.Empty<string>(),
                ExcludeRegions = rules.ExcludeRegions ?? Array.Empty<string>(),
                IncludeDataCenters = rules.IncludeDataCenters ?? Array.Empty<string>(),
                ExcludeDataCenters = rules.ExcludeDataCenters ?? Array.Empty<string>(),
            };
            return rules;
        }
        catch
        {
            return new EligibilityRules();
        }
    }

    private static FilterCriteria ParseCriteria(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new FilterCriteria()
            : (SafeDeserialize<FilterCriteria>(json) ?? new FilterCriteria());

    private static T? SafeDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return default; }
    }

    private static EligibilityFallbackPolicy ParsePolicy(string s)
        => Enum.TryParse<EligibilityFallbackPolicy>(s, ignoreCase: true, out var p)
           ? p
           : EligibilityFallbackPolicy.UseStoreThenDefault;

    private static ClusterRow CloneRow(ClusterRow src)
    {
        var dest = new ClusterRow();
        foreach (var p in typeof(ClusterRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            p.SetValue(dest, p.GetValue(src));
        }
        return dest;
    }
}
