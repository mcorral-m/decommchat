#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using MyM365AgentDecommision.Bot.Services;

namespace MyM365AgentDecommision.Bot.Plugins;

/// <summary>
/// Deterministic Adaptive Card builders: Top-N, grouped, compare, explain, sensitivity.
/// Returns Adaptive Card v1.5 JSON strings.
/// </summary>
public sealed class CardPlugin
{
    private readonly CardFactory _cards; // kept for future use; not strictly required by this implementation

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Correct options for JsonDocument.Parse(...)
    private static readonly JsonDocumentOptions DocOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public CardPlugin(CardFactory cards) => _cards = cards;

    [KernelFunction, Description("Render Top-N results as a simple markdown table (no Adaptive Card).")]
    public Task<string> TopNTextTable(
    [Description("Result JSON with either {Items:[...]} or a raw array.")] string resultJson,
    [Description("Optional title.")] string? title = "Top Results",
    CancellationToken ct = default)
    {
        var rows = ExtractRows(resultJson, out _);
        var cols = PickColumns(rows);
        var md = BuildMarkdownTable(title ?? "Top Results", cols, rows);
        return Task.FromResult(md);
    }
    [KernelFunction, Description("Render Top-N as Adaptive Card when possible, otherwise fallback to markdown.")]
    public Task<string> TopNBestEffort(
        [Description("Result JSON with either {Items:[...]} or a raw array.")] string resultJson,
        [Description("Optional title.")] string? title = "Top Results",
        CancellationToken ct = default)
    {
        try
        {
            // Try card
            var rows = ExtractRows(resultJson, out _);
            var cols = PickColumns(rows);
            var card = BuildTableCard(title ?? "Top Results", subtitle: null, cols, rows);
            return Task.FromResult(card);
        }
        catch
        {
            // Fallback to markdown
            var rows = ExtractRows(resultJson, out _);
            var cols = PickColumns(rows);
            var md = BuildMarkdownTable(title ?? "Top Results", cols, rows);
            return Task.FromResult(md);
        }
    }



    // -------------------------------------------------------------------------
    // 2) Grouped Top-N
    // -------------------------------------------------------------------------
    [KernelFunction, Description("Render grouped Top-N (per group) as an Adaptive Card.")]
    public Task<string> GroupedTopNCard(
        [Description("Result JSON containing groups and their top-N rows.")] string resultJson,
        [Description("Field used for grouping (e.g., Region, DC).")] string groupBy,
        [Description("Optional title shown on the card.")] string? title = null,
        CancellationToken ct = default)
    {
        var groups = ExtractGroups(resultJson);
        var body = new List<object>();

        // Header
        body.Add(HeaderBlock(title ?? $"Top by {groupBy}", subtitle: null));

        foreach (var (groupName, rows) in groups)
        {
            var cols = PickColumns(rows);
            body.Add(new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = $"**{groupName}**",
                ["wrap"] = true,
                ["spacing"] = "Medium"
            });

            body.Add(TableElement(cols, rows));
        }

        var cardObj = AdaptiveCard(body);
        var json = JsonSerializer.Serialize(cardObj, JsonOpts);
        return Task.FromResult(json);
    }

    // -------------------------------------------------------------------------
    // 3) Compare (side-by-side)
    // -------------------------------------------------------------------------
    [KernelFunction, Description("Render a side-by-side comparison card for multiple clusters.")]
    public Task<string> CompareCard(
        [Description("Compare JSON with clusters and key metrics/factors.")] string compareJson,
        [Description("Optional title shown on the card.")] string? title = null,
        CancellationToken ct = default)
    {
        // Expect array of objects for clusters. Identify each by Cluster/Name/Id.
        var rows = ExtractRows(compareJson, out _);
        // Re-pivot into metric rows: Metric | C1 | C2 | ...
        var idField = FirstPresentField(rows, "Cluster", "Name", "Id", "ClusterId") ?? "Cluster";
        var clusterNames = rows.Select(r => (r.TryGetValue(idField, out var v) ? v?.ToString() : null) ?? "(unknown)").ToList();

        // All keys union minus idField
        var metricNames = rows.SelectMany(r => r.Keys).Where(k => !k.Equals(idField, StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        // Build a row-per-metric with values per cluster
        var outRows = new List<Dictionary<string, object?>>();
        foreach (var m in metricNames)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Metric"] = m };
            for (int i = 0; i < rows.Count; i++)
            {
                var label = clusterNames[i];
                row[label] = rows[i].TryGetValue(m, out var val) ? val : null;
            }
            outRows.Add(row);
        }

        var cols = new List<string> { "Metric" };
        cols.AddRange(clusterNames);

        var body = new List<object>
        {
            HeaderBlock(title ?? "Compare"),
            TableElement(cols, outRows)
        };

        var cardObj = AdaptiveCard(body);
        var json = JsonSerializer.Serialize(cardObj, JsonOpts);
        return Task.FromResult(json);
    }

    // -------------------------------------------------------------------------
    // 4) Explain (top drivers)
    // -------------------------------------------------------------------------
    [KernelFunction, Description("Render an explanation card showing top drivers of a cluster's score.")]
    public Task<string> ExplainCard(
        [Description("Explain JSON containing factor contributions for one cluster.")] string explainJson,
        [Description("How many top drivers to show (default 3).")] int topK = 3,
        [Description("Optional title shown on the card.")] string? title = null,
        CancellationToken ct = default)
    {
        // Accept either { Factors:[{Name,Contribution,Weight,Value}, ...] } or raw array
        var (factors, clusterId) = ExtractFactors(explainJson);
        if (factors.Count == 0)
        {
            return Task.FromResult(ErrorCard("No explain data"));
        }

        // Top K by absolute contribution, fallback by Weight or Value
        var top = factors
            .OrderByDescending(f => Math.Abs(f.Contribution ?? 0))
            .ThenByDescending(f => f.Weight ?? 0)
            .ThenByDescending(f => Math.Abs(f.Value ?? 0))
            .Take(Math.Max(1, topK))
            .ToList();

        var rows = top.Select(f => new Dictionary<string, object?>
        {
            ["Factor"] = f.Name,
            ["Weight"] = f.Weight,
            ["Value"] = f.Value,
            ["Contribution"] = f.Contribution
        }).ToList();

        var cols = new List<string> { "Factor", "Weight", "Value", "Contribution" };

        var body = new List<object>
        {
            HeaderBlock(title ?? $"Explain{(string.IsNullOrWhiteSpace(clusterId) ? "" : $" — {clusterId}")}"),
            TableElement(cols, rows)
        };

        var cardObj = AdaptiveCard(body);
        var json = JsonSerializer.Serialize(cardObj, JsonOpts);
        return Task.FromResult(json);
    }

    // -------------------------------------------------------------------------
    // 5) Sensitivity (before/after with delta)
    // -------------------------------------------------------------------------
    [KernelFunction, Description("Render a sensitivity card showing before/after score and delta.")]
    public Task<string> SensitivityCard(
        [Description("Before/After JSON with original and edited values + scores.")] string beforeAfterJson,
        [Description("Optional title shown on the card.")] string? title = null,
        CancellationToken ct = default)
    {
        var (before, after, name) = ExtractBeforeAfter(beforeAfterJson);

        var scoreBefore = TryToDouble(FirstOrDefault(before, "Score", "score", "DecomScore"));
        var scoreAfter = TryToDouble(FirstOrDefault(after, "Score", "score", "DecomScore"));
        double? delta = (scoreBefore.HasValue && scoreAfter.HasValue) ? (scoreAfter - scoreBefore) : null;

        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Metric"] = "Score", ["Before"] = scoreBefore, ["After"] = scoreAfter, ["Δ"] = delta }
        };

        // Show any edited numeric fields that changed
        var numericKeys = after.Keys.Where(k => IsNumericString(FirstOrDefault(after, k))).ToList();
        foreach (var k in numericKeys)
        {
            var b = TryToDouble(FirstOrDefault(before, k));
            var a = TryToDouble(FirstOrDefault(after, k));
            if (b != a)
            {
                rows.Add(new() { ["Metric"] = k, ["Before"] = b, ["After"] = a, ["Δ"] = (b.HasValue && a.HasValue) ? (a - b) : null });
            }
        }

        var cols = new List<string> { "Metric", "Before", "After", "Δ" };

        var body = new List<object>
        {
            HeaderBlock(title ?? $"Sensitivity{(string.IsNullOrWhiteSpace(name) ? "" : $" — {name}")}"),
            TableElement(cols, rows)
        };

        var cardObj = AdaptiveCard(body);
        var json = JsonSerializer.Serialize(cardObj, JsonOpts);
        return Task.FromResult(json);
    }

    // -------------------------------------------------------------------------
    // Helpers: JSON extraction
    // -------------------------------------------------------------------------

    private static List<Dictionary<string, object?>> ExtractRows(string json, out JsonElement rootOut)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocOpts);
            var root = doc.RootElement;
            rootOut = root;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return root.EnumerateArray().Select(ObjToDict).ToList();
            }
            if (TryProp(root, "Items", out arr) || TryProp(root, "items", out arr) ||
                TryProp(root, "Rows", out arr) || TryProp(root, "rows", out arr))
            {
                if (arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().Select(ObjToDict).ToList();
            }
            // Fallback: single object -> single row
            if (root.ValueKind == JsonValueKind.Object)
                return new List<Dictionary<string, object?>> { ObjToDict(root) };

            return new List<Dictionary<string, object?>>();
        }
        catch
        {
            rootOut = default;
            return new List<Dictionary<string, object?>>();
        }
    }

    private static List<(string Group, List<Dictionary<string, object?>> Rows)> ExtractGroups(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocOpts);
            var root = doc.RootElement;
            var result = new List<(string, List<Dictionary<string, object?>>)>();

            // Case: { Groups: [ { Group:"westus2", Items:[...] }, ... ] }
            if (TryProp(root, "Groups", out var groups) || TryProp(root, "groups", out groups))
            {
                foreach (var g in groups.EnumerateArray())
                {
                    var name = (g.TryGetProperty("Group", out var gName) ? gName.GetString()
                              : g.TryGetProperty("group", out var gName2) ? gName2.GetString()
                              : null) ?? "(group)";

                    if (g.TryGetProperty("Items", out var items) || g.TryGetProperty("items", out items) ||
                        g.TryGetProperty("Rows", out items) || g.TryGetProperty("rows", out items))
                    {
                        var rows = items.ValueKind == JsonValueKind.Array
                                   ? items.EnumerateArray().Select(ObjToDict).ToList()
                                   : new List<Dictionary<string, object?>>();
                        result.Add((name, rows));
                    }
                }
                return result;
            }

            // Case: plain object keyed by groups: { "westus2":[...], "eastus2":[...] }
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Array)
                    {
                        result.Add((p.Name, p.Value.EnumerateArray().Select(ObjToDict).ToList()));
                    }
                }
                return result;
            }

            return result;
        }
        catch
        {
            return new List<(string, List<Dictionary<string, object?>>)>();
        }
    }

    private static (List<Factor> Factors, string ClusterId) ExtractFactors(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocOpts);
            var root = doc.RootElement;

            // cluster id hint
            var name = FirstString(root, "Cluster", "cluster", "Name", "name", "Id", "id", "ClusterId", "clusterId");

            JsonElement arr;
            if (TryProp(root, "Factors", out arr) || TryProp(root, "factors", out arr) ||
                TryProp(root, "Contributions", out arr) || TryProp(root, "contributions", out arr))
            {
                var list = FactorArray(arr);
                return (list, name ?? "");
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                return (FactorArray(root), name ?? "");
            }

            // Single factor object?
            if (root.ValueKind == JsonValueKind.Object)
            {
                var f = FactorFrom(root);
                return (new List<Factor> { f }, name ?? "");
            }
        }
        catch { /* ignore */ }
        return (new List<Factor>(), "");
    }

    private static (Dictionary<string, object?> Before, Dictionary<string, object?> After, string Name)
        ExtractBeforeAfter(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocOpts);
            var root = doc.RootElement;

            var name = FirstString(root, "Cluster", "cluster", "Name", "name", "Id", "id", "ClusterId", "clusterId") ?? "";

            if (TryProp(root, "Before", out var b) || TryProp(root, "before", out b) ||
                TryProp(root, "Original", out b) || TryProp(root, "original", out b))
            {
                var a = root.TryGetProperty("After", out var a1) ? a1
                        : root.TryGetProperty("after", out var a2) ? a2
                        : root.TryGetProperty("Edited", out var a3) ? a3
                        : root.TryGetProperty("edited", out var a4) ? a4
                        : default;

                return (ObjToDict(b), a.ValueKind == JsonValueKind.Object ? ObjToDict(a) : new(), name);
            }

            // Fallback: array [before, after]
            if (root.ValueKind == JsonValueKind.Array)
            {
                var arr = root.EnumerateArray().ToList();
                var before = arr.Count > 0 && arr[0].ValueKind == JsonValueKind.Object ? ObjToDict(arr[0]) : new();
                var after = arr.Count > 1 && arr[1].ValueKind == JsonValueKind.Object ? ObjToDict(arr[1]) : new();
                return (before, after, name);
            }
        }
        catch { /* ignore */ }

        return (new(), new(), "");
    }

    // -------------------------------------------------------------------------
    // Helpers: Adaptive Card construction
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> AdaptiveCard(List<object> body) => new()
    {
        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["type"] = "AdaptiveCard",
        ["version"] = "1.5",
        ["body"] = body
    };

    private static object HeaderBlock(string title, string? subtitle = null)
    {
        var col = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true }
        };
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            col.Add(new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = subtitle, ["wrap"] = true, ["spacing"] = "Small", ["isSubtle"] = true });
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "Container",
            ["items"] = col
        };
    }

    private static string BuildTableCard(string title, string? subtitle, List<string> cols, List<Dictionary<string, object?>> rows)
    {
        var body = new List<object>
        {
            HeaderBlock(title, subtitle),
            TableElement(cols, rows)
        };

        var cardObj = AdaptiveCard(body);
        return JsonSerializer.Serialize(cardObj, JsonOpts);
    }

    private static object TableElement(List<string> cols, List<Dictionary<string, object?>> rows)
    {
        // AdaptiveCard 1.5 Table
        var columns = cols.Select(_ => new Dictionary<string, object?> { ["width"] = 1 }).ToList();

        var headerCells = cols.Select(c => new Dictionary<string, object?>
        {
            ["type"] = "TableCell",
            ["items"] = new object[] { new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = $"**{c}**", ["wrap"] = true } }
        }).ToList();

        var headerRow = new Dictionary<string, object?>
        {
            ["type"] = "TableRow",
            ["cells"] = headerCells
        };

        var dataRows = new List<object>();
        foreach (var r in rows)
        {
            var cells = cols.Select(c => new Dictionary<string, object?>
            {
                ["type"] = "TableCell",
                ["items"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = ToDisplay(r.TryGetValue(c, out var v) ? v : null), ["wrap"] = true }
                }
            }).ToList();

            dataRows.Add(new Dictionary<string, object?>
            {
                ["type"] = "TableRow",
                ["cells"] = cells
            });
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "Table",
            ["columns"] = columns,
            ["firstRowAsHeader"] = true,
            ["rows"] = new object[] { headerRow }.Concat(dataRows).ToArray()
        };
    }

    private static List<string> PickColumns(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return new List<string>();

        // Prefer a common, readable set if present
        var preferred = new[]
        {
            "Cluster","Region","DataCenter","ClusterAgeYears","CoreUtilization",
            "RegionHealthScore","OutOfServiceNodes","StrandedCores","Score","Eligible"
        };

        var present = preferred.Where(p => rows.Any(r => r.ContainsKey(p))).ToList();
        if (present.Count >= 3) return present;

        // Fallback: union of keys from first row
        return rows.First().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ToDisplay(object? v)
    {
        if (v is null) return "";
        if (v is double d) return d.ToString("0.####");
        if (v is float f) return f.ToString("0.####");
        if (v is decimal m) return m.ToString("0.####");
        if (v is bool b) return b ? "true" : "false";
        return v.ToString() ?? "";
    }

    // -------------------------------------------------------------------------
    // Helpers: JSON utilities
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> ObjToDict(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (obj.ValueKind != JsonValueKind.Object) return dict;

        foreach (var p in obj.EnumerateObject())
        {
            dict[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => (p.Value.TryGetInt64(out var l) ? (object)l :
                                            p.Value.TryGetDouble(out var d) ? d : p.Value.ToString()),
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Array => p.Value.ToString(),
                JsonValueKind.Object => p.Value.ToString(),
                _ => p.Value.ToString()
            };
        }
        return dict;
    }

    private static bool TryProp(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static string? FirstPresentField(List<Dictionary<string, object?>> rows, params string[] names)
    {
        foreach (var n in names)
        {
            if (rows.Count > 0 && rows[0].ContainsKey(n)) return n;
        }
        return null;
    }

    private static object? FirstOrDefault(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (obj.TryGetValue(k, out var v)) return v;
        }
        return null;
    }

    private static bool IsNumericString(object? v)
    {
        if (v is null) return false;
        if (v is double or float or int or long or decimal) return true;
        return v is string s && double.TryParse(s, out _);
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

    // -------------------------------------------------------------------------
    // Error card convenience
    // -------------------------------------------------------------------------
    private static string ErrorCard(string message)
    {
        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = new object[]
            {
                new Dictionary<string, object?> { ["type"]="TextBlock", ["text"]="Adaptive Card error", ["weight"]="Bolder", ["size"]="Medium" },
                new Dictionary<string, object?> { ["type"]="TextBlock", ["text"]=message, ["wrap"]=true, ["isSubtle"]=true }
            }
        };
        return JsonSerializer.Serialize(card, JsonOpts);
    }

    private sealed class Factor
    {
        public string Name { get; init; } = "";
        public double? Contribution { get; init; }
        public double? Weight { get; init; }
        public double? Value { get; init; }
    }

    private static List<Factor> FactorArray(JsonElement arr)
    {
        var list = new List<Factor>();
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(FactorFrom(el));
        }
        return list;
    }

    private static Factor FactorFrom(JsonElement el)
    {
        string name = "";
        double? contrib = null, weight = null, value = null;

        if (el.ValueKind == JsonValueKind.Object)
        {
            name = TryGetString(el, "Name", "name", "Factor", "factor") ?? "";
            contrib = TryGetDouble(el, "Contribution", "contribution", "ScoreContribution");
            weight = TryGetDouble(el, "Weight", "weight");
            value = TryGetDouble(el, "Value", "value");
        }
        return new Factor { Name = name, Contribution = contrib, Weight = weight, Value = value };
    }

    private static string? TryGetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static double? TryGetDouble(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
            }
        }
        return null;
    }
    private static string BuildMarkdownTable(string title, List<string> cols, List<Dictionary<string, object?>> rows)
{
    static string Esc(string? s)
        => (s ?? "").Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ").Trim();

    var sb = new System.Text.StringBuilder();
    if (!string.IsNullOrWhiteSpace(title))
        sb.AppendLine($"**{title}**");

    // header
    sb.AppendLine("| " + string.Join(" | ", cols) + " |");
    sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");

    // rows
    foreach (var r in rows)
    {
        var cells = cols.Select(c =>
        {
            r.TryGetValue(c, out var v);
            return Esc(ToDisplay(v));
        });
        sb.AppendLine("| " + string.Join(" | ", cells) + " |");
    }

    return sb.ToString();
}

}
