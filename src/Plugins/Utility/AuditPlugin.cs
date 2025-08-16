// src/Plugins/AuditPlugin.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using MyM365AgentDecommision.Bot.Services;

namespace MyM365AgentDecommision.Bot.Plugins;

/// <summary>
/// Governance & audit helpers: last-run config, explain logs, weight history.
/// </summary>
public sealed class AuditPlugin
{
    private readonly IAuditLog _audit;

    public AuditPlugin(IAuditLog audit) => _audit = audit;

    public sealed record RunConfig(
        DateTime TimestampUtc,
        object AppliedWeights,
        object? AppliedRules,
        object? Criteria,
        string? AsOf,
        string? UtilWindow,
        string? Actor);

    public sealed record LoggedItem(string Id, DateTime TimestampUtc, string Kind, string Summary);

    /// <summary>Returns the config used by the most recent run (weights, rules, criteria, timestamps).</summary>
    [KernelFunction, Description("Show the scoring/eligibility/filter config used for the last run.")]
    public Task<RunConfig> ShowLastRunConfig(CancellationToken ct = default)
    {
        var cfg = _audit.GetLastRunConfig();
        if (cfg is null)
        {
            // no prior run recorded
            var empty = new RunConfig(
                TimestampUtc: DateTime.MinValue,
                AppliedWeights: new { },
                AppliedRules: null,
                Criteria: null,
                AsOf: null,
                UtilWindow: null,
                Actor: null
            );
            return Task.FromResult(empty);
        }

        var mapped = new RunConfig(
            cfg.TimestampUtc,
            cfg.AppliedWeights,
            cfg.AppliedRules,
            cfg.Criteria,
            cfg.AsOf,
            cfg.UtilWindow,
            cfg.Actor
        );
        return Task.FromResult(mapped);
    }

    /// <summary>Logs an explain JSON for the top-N set; returns a log id for traceability.</summary>
    [KernelFunction, Description("Log an explain report (JSON) for traceability and future review.")]
    public Task<LoggedItem> LogExplainForTopN(
        [Description("Explain JSON (array of per-cluster explanations).")] string resultJson,
        [Description("Optional summary to store with the log record.")] string? summary = null,
        CancellationToken ct = default)
    {
        var item = _audit.LogExplain(resultJson, summary);
        return Task.FromResult(new LoggedItem(item.Id, item.TimestampUtc, item.Kind, item.Summary));
    }

    /// <summary>Shows the recent weight changes for governance (who changed what, when).</summary>
    [KernelFunction, Description("List recent weight changes for governance (default last 7 days).")]
    public Task<IReadOnlyList<LoggedItem>> WeightHistory(
        [Description("Lookback window in days (default 7).")] int days = 7,
        CancellationToken ct = default)
    {
        var items = _audit.GetWeightHistory(days);
        var mapped = new List<LoggedItem>(items.Count);
        foreach (var i in items)
            mapped.Add(new LoggedItem(i.Id, i.TimestampUtc, i.Kind, i.Summary));
        return Task.FromResult<IReadOnlyList<LoggedItem>>(mapped);
    }
}
