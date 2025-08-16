// src/Plugins/ClusterFilterPlugin.cs
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
using MyM365AgentDecommision.Bot.Services;             // FilteringEngine, FilterCriteria, ClusterRowAccessors

namespace MyM365AgentDecommision.Bot.Plugins;

public sealed class ClusterFilteringPlugin
{
    private readonly IClusterDataProvider _data;
    private readonly FilteringEngine _filter;
    private readonly ILogger<ClusterFilteringPlugin> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ClusterFilteringPlugin(
        IClusterDataProvider data,
        FilteringEngine filter,
        ILogger<ClusterFilteringPlugin> log)
    {
        _data = data;
        _filter = filter;
        _log = log;
    }

    // -------------------------------------------------------------------------
    // Field discovery & schema
    // -------------------------------------------------------------------------

    [KernelFunction, Description("List filterable fields from the ClusterRow model with type & example operators.")]
    public Task<object> ListFields()
    {
        // Use Accessors.AllNames() + TryGet to avoid nullability issues
        var items = ClusterRowAccessors.AllNames()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                ClusterRowAccessors.TryGet(name, out var acc);
                var t = acc?.Type ?? typeof(object);
                var u = Nullable.GetUnderlyingType(t) ?? t;

                return new
                {
                    Name = name,
                    Type = u.Name, // display type name
                    IsNumeric = IsNumericType(u),
                    IsBoolean = u == typeof(bool),
                    IsString = u == typeof(string),
                    ExampleOps = new[] { "=", "!=", "<", ">", "<=", ">=", "contains", "in", "not in", "is null", "not null" }
                };
            })
            .ToList();

        return Task.FromResult<object>(new
        {
            Kind = "Fields",
            Count = items.Count,
            Items = items
        });
    }

    [KernelFunction, Description("Describe schema of ClusterRow (property name, type, and optional description).")]
    public Task<object> DescribeSchema()
    {
        var props = typeof(ClusterRow).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new
            {
                Name = p.Name,
                Type = (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).Name,
                Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description
            })
            .ToList();

        return Task.FromResult<object>(new
        {
            Kind = "Schema",
            Count = props.Count,
            Properties = props
        });
    }

    // -------------------------------------------------------------------------
    // Core filter
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Filter clusters using FilterCriteria JSON. Optional sort & paging.")]
    public async Task<object> Filter(
        [Description("FilterCriteria JSON.")] string criteriaJson,
        [Description("Optional field to order by (e.g., \"Region\" or \"ClusterAgeYears\").")] string? orderBy = null,
        [Description("If true, order descending.")] bool descending = false,
        [Description("Page number (1-based).")] int page = 1,
        [Description("Page size.")] int pageSize = 25,
        CancellationToken ct = default)
    {
        var rows = await _data.GetClusterRowDataAsync(ct);

        var criteria = ParseCriteria(criteriaJson);
        var filtered = _filter.Apply(rows, criteria).ToList();

        // Optional ordering
        if (!string.IsNullOrWhiteSpace(orderBy) && ClusterRowAccessors.TryGet(orderBy!, out var acc))
        {
            filtered = (descending
                ? filtered.OrderByDescending(r => acc.Getter(r))
                : filtered.OrderBy(r => acc.Getter(r))
            ).ToList();
        }

        // Paging
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 25;
        var total = filtered.Count;
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new
        {
            Kind = "Filter",
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items
        };
    }

    // -------------------------------------------------------------------------
    // Minimal plan executor
    // -------------------------------------------------------------------------

    public sealed record PlanStep(string Op, JsonElement? Criteria);

    [KernelFunction, Description("Execute a minimal filter plan. Supports steps: [{\"op\":\"filter\",\"criteria\":{...}}, ...].")]
    public async Task<object> ExecutePlan(
        [Description("Plan JSON. If not an array, treated as a single FilterCriteria.")] string planJson,
        CancellationToken ct = default)
    {
        var rows = await _data.GetClusterRowDataAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(planJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var cur = rows;
                foreach (var stepEl in doc.RootElement.EnumerateArray())
                {
                    var op = stepEl.TryGetProperty("op", out var opEl) ? (opEl.GetString() ?? "") : "";
                    if (!stepEl.TryGetProperty("criteria", out var critEl)) continue;

                    if (!string.Equals(op, "filter", StringComparison.OrdinalIgnoreCase)) continue;

                    var crit = critEl.Deserialize<FilterCriteria>(JsonOpts) ?? new FilterCriteria();
                    cur = _filter.Apply(cur, crit).ToList();
                }

                var list = cur.ToList();
                return new
                {
                    Kind = "PlanResult",
                    Total = list.Count,
                    Items = list
                };
            }
            else
            {
                // Treat as a single FilterCriteria
                var criteria = SafeDeserialize<FilterCriteria>(planJson) ?? new FilterCriteria();
                var selected = _filter.Apply(rows, criteria).ToList();
                return new
                {
                    Kind = "PlanResult",
                    Total = selected.Count,
                    Items = selected
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ExecutePlan: failed to parse planJson");
            // Fallback: treat as criteria
            var criteria = SafeDeserialize<FilterCriteria>(planJson) ?? new FilterCriteria();
            var selected = _filter.Apply(rows, criteria).ToList();
            return new
            {
                Kind = "PlanResult",
                Total = selected.Count,
                Items = selected,
                Warning = "Plan parsing failed; executed as a single FilterCriteria."
            };
        }
    }

    // -------------------------------------------------------------------------
    // Distinct values
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Return distinct values (and counts) for a field.")]
    public async Task<object> Distinct(
        [Description("Field name (e.g., Region, DataCenter, Generation).")] string field,
        CancellationToken ct = default)
    {
        if (!ClusterRowAccessors.TryGet(field, out var acc))
            return new { Error = $"Unknown field '{field}'." };

        var rows = await _data.GetClusterRowDataAsync(ct);

        var values = rows
            .Select(r => acc.Getter(r)?.ToString() ?? "(null)") // ensure non-null strings
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            Kind = "Distinct",
            Field = field,
            Count = values.Count,
            Items = values
        };
    }

    // -------------------------------------------------------------------------
    // Summary stats
    // -------------------------------------------------------------------------

    [KernelFunction, Description("Summary stats for a numeric field (min/max/mean/median/p25/p75). If non-numeric, returns category counts.")]
    public async Task<object> SummaryStats(
        [Description("Field name to summarize (e.g., ClusterAgeYears, CoreUtilization, StrandedCores).")] string field,
        [Description("Optional FilterCriteria JSON to pre-filter.")] string? criteriaJson = null,
        CancellationToken ct = default)
    {
        var rows = await _data.GetClusterRowDataAsync(ct);
        var criteria = ParseCriteria(criteriaJson);
        var selected = _filter.Apply(rows, criteria).ToList();

        if (!ClusterRowAccessors.TryGet(field, out var acc))
            return new { Error = $"Unknown field '{field}'." };

        var type = Nullable.GetUnderlyingType(acc.Type) ?? acc.Type;

        // Numeric summary
        if (IsNumericType(type))
        {
            var nums = selected.Select(r => TryToDouble(acc.Getter(r)))
                               .Where(v => v.HasValue)
                               .Select(v => v!.Value)
                               .OrderBy(v => v)
                               .ToList();

            var count = nums.Count;
            var nulls = selected.Count - count;

            if (count == 0)
            {
                return new
                {
                    Kind = "SummaryStats",
                    Field = field,
                    Count = 0,
                    Nulls = nulls
                };
            }

            static double Median(IReadOnlyList<double> arr)
            {
                var n = arr.Count;
                if (n == 0) return double.NaN;
                if (n % 2 == 1) return arr[n / 2];
                return (arr[(n / 2) - 1] + arr[n / 2]) / 2.0;
            }

            static double Percentile(IReadOnlyList<double> arr, double p) // p in [0,1]
            {
                if (arr.Count == 0) return double.NaN;
                if (p <= 0) return arr[0];
                if (p >= 1) return arr[^1];
                var idx = (arr.Count - 1) * p;
                var lo = (int)Math.Floor(idx);
                var hi = (int)Math.Ceiling(idx);
                if (lo == hi) return arr[lo];
                return arr[lo] + (arr[hi] - arr[lo]) * (idx - lo);
            }

            var sum = nums.Sum();
            var mean = sum / count;

            return new
            {
                Kind = "SummaryStats",
                Field = field,
                Count = count,
                Nulls = nulls,
                Min = nums.First(),
                P25 = Percentile(nums, 0.25),
                Median = Median(nums),
                P75 = Percentile(nums, 0.75),
                Max = nums.Last(),
                Mean = mean
            };
        }

        // Non-numeric: category counts (use non-null strings)
        var cats = selected
            .Select(r => acc.Getter(r)?.ToString() ?? "(null)")
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            Kind = "CategoryCounts",
            Field = field,
            Count = cats.Count,
            Items = cats
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static FilterCriteria ParseCriteria(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new FilterCriteria()
            : (SafeDeserialize<FilterCriteria>(json) ?? new FilterCriteria());

    private static T? SafeDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return default; }
    }

    private static bool IsNumericType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(byte) || u == typeof(short) || u == typeof(int) ||
               u == typeof(long) || u == typeof(float) || u == typeof(double) ||
               u == typeof(decimal);
    }

    private static double? TryToDouble(object? v)
    {
        if (v is null) return null;
        if (v is double d) return d;
        if (v is float f) return f;
        if (v is int i) return i;
        if (v is long l) return l;
        if (v is decimal m) return (double)m;
        if (v is string s && double.TryParse(s, out var ds)) return ds;
        return null;
    }
}
