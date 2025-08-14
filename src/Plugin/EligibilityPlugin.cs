#nullable enable
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using MyM365AgentDecommision.Bot.Interfaces;    // IClusterDataProvider
using MyM365AgentDecommision.Bot.Models;        // ClusterRow
using MyM365AgentDecommision.Bot.Services;
using MyM365AgentDecommision.Bot.Eligibility;
namespace MyM365AgentDecommision.Bot.Plugins;

public sealed class EligibilityPlugin
{
    private readonly IClusterDataProvider _data;
    private readonly ILogger<EligibilityPlugin>? _log;

    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public EligibilityPlugin(IClusterDataProvider data, ILogger<EligibilityPlugin>? log = null)
    {
        _data = data;
        _log = log;
    }

    // ----------------------------- Typed DTOs ---------------------------------

    public sealed record ErrorDto(string Error);

    public sealed record ApplySummary(int Total, int Eligible, int Ineligible);

    public sealed record IneligibleDto(ClusterRow Row, List<string> Reasons);

    public sealed record ApplyResponse(
        ApplySummary Summary,
        IReadOnlyList<ClusterRow> Eligible,
        IReadOnlyList<IneligibleDto> Ineligible,
        ClusterEligibilityEngine.EligibilityRules AppliedRules
    );

    public sealed record ExplainResponse(
        string ClusterId,
        bool Eligible,
        List<string> Reasons,
        ClusterEligibilityEngine.EligibilityRules AppliedRules
    );

    // ----------------------------- Core APIs ----------------------------------

    [KernelFunction, Description("Return a default EligibilityRules JSON template.")]
    public string GetRulesTemplate()
        => JsonSerializer.Serialize(ClusterEligibilityEngine.EligibilityRules.Default(), J);

    [KernelFunction, Description("Validate and normalize an EligibilityRules JSON; returns the normalized JSON or an error.")]
    public string ValidateRules(string rulesJson)
    {
        try
        {
            var rules = ParseRulesOrDefault(rulesJson);
            return JsonSerializer.Serialize(rules, J);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorDto($"Invalid rules JSON: {ex.Message}"), J);
        }
    }

    [KernelFunction, Description("Apply eligibility to ALL clusters from the data provider using provided rules JSON. Returns eligible, ineligible with reasons, and summary.")]
    public async Task<string> ApplyToAllAsync(string? rulesJson = null, CancellationToken ct = default)
    {
        try
        {
            var rows  = await _data.GetClusterRowDataAsync(ct); // provider rows
            var rules = ParseRulesOrDefault(rulesJson);

            var eligible = ClusterEligibilityEngine.FilterEligible(rows, rules, out var ineligible); // engine
            var resp = new ApplyResponse(
                Summary: new ApplySummary(Total: rows.Count, Eligible: eligible.Count, Ineligible: ineligible.Count),
                Eligible: eligible,
                Ineligible: ineligible.Select(i => new IneligibleDto(i.Row, i.Reasons)).ToList(),
                AppliedRules: rules
            );
            return JsonSerializer.Serialize(resp, J);
        }
        catch (OperationCanceledException) { return JsonSerializer.Serialize(new ErrorDto("Operation cancelled."), J); }
        catch (Exception ex)
        {
            _log?.LogError(ex, "ApplyToAllAsync failed");
            return JsonSerializer.Serialize(new ErrorDto($"Unhandled error: {ex.Message}"), J);
        }
    }

    [KernelFunction, Description("Apply eligibility to a provided clusters JSON (List<ClusterRow>). Returns eligible, ineligible with reasons, and summary.")]
    public string ApplyToList(string clustersJson, string? rulesJson = null)
    {
        try
        {
            var rows  = JsonSerializer.Deserialize<List<ClusterRow>>(clustersJson, J) ?? new();
            var rules = ParseRulesOrDefault(rulesJson);

            var eligible = ClusterEligibilityEngine.FilterEligible(rows, rules, out var ineligible);
            var resp = new ApplyResponse(
                Summary: new ApplySummary(Total: rows.Count, Eligible: eligible.Count, Ineligible: ineligible.Count),
                Eligible: eligible,
                Ineligible: ineligible.Select(i => new IneligibleDto(i.Row, i.Reasons)).ToList(),
                AppliedRules: rules
            );
            return JsonSerializer.Serialize(resp, J);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "ApplyToList failed");
            return JsonSerializer.Serialize(new ErrorDto($"Invalid input: {ex.Message}"), J);
        }
    }

    [KernelFunction, Description("Explain eligibility for one clusterId using provider + rules JSON. Returns reasons and a pass/fail flag.")]
    public async Task<string> ExplainClusterAsync(string clusterId, string? rulesJson = null, CancellationToken ct = default)
    {
        try
        {
            var rules = ParseRulesOrDefault(rulesJson);
            var row = await _data.GetClusterRowDetailsAsync(clusterId, ct);
            if (row is null)
                return JsonSerializer.Serialize(new ErrorDto($"Cluster '{clusterId}' not found."), J);

            var ok = ClusterEligibilityEngine.IsEligible(row, rules, out var reasons);
            var resp = new ExplainResponse(clusterId, ok, reasons, rules);
            return JsonSerializer.Serialize(resp, J);
        }
        catch (OperationCanceledException) { return JsonSerializer.Serialize(new ErrorDto("Operation cancelled."), J); }
        catch (Exception ex)
        {
            _log?.LogError(ex, "ExplainClusterAsync failed");
            return JsonSerializer.Serialize(new ErrorDto($"Unhandled error: {ex.Message}"), J);
        }
    }

    // ----------------------------- Convenience -------------------------------

    [KernelFunction, Description("Quickly build a basic rules JSON from parameters (minAgeYears, maxUtilPercent, include/exclude regions).")]
    public string BuildRules(
        int? minAgeYears = 6,
        double? maxCoreUtilizationPercent = 30,
        string? includeRegionsCsv = null,
        string? excludeRegionsCsv = null,
        bool enabled = true,
        bool enforceAge = true,
        bool enforceUtilization = true,
        bool enforceAllowedRegions = true,
        bool enforceExcludedRegions = true)
    {
        try
        {
            var r = ClusterEligibilityEngine.EligibilityRules.Default() with
            {
                Enabled = enabled,
                EnforceAge = enforceAge,
                EnforceUtilization = enforceUtilization,
                EnforceAllowedRegions = enforceAllowedRegions,
                EnforceExcludedRegions = enforceExcludedRegions,
                MinAgeYears = minAgeYears ?? 6,
                MaxCoreUtilizationPercent = maxCoreUtilizationPercent ?? 30
            };

            if (!string.IsNullOrWhiteSpace(includeRegionsCsv))
                r = r with { AllowedRegions = includeRegionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) };

            if (!string.IsNullOrWhiteSpace(excludeRegionsCsv))
                r = r with { ExcludedRegions = excludeRegionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) };

            return JsonSerializer.Serialize(r, J);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorDto($"Failed to build rules: {ex.Message}"), J);
        }
    }

    // ----------------------------- Helpers -----------------------------------

    private static ClusterEligibilityEngine.EligibilityRules ParseRulesOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ClusterEligibilityEngine.EligibilityRules.Default();
        return JsonSerializer.Deserialize<ClusterEligibilityEngine.EligibilityRules>(json!, J)
               ?? ClusterEligibilityEngine.EligibilityRules.Default();
    }
}
