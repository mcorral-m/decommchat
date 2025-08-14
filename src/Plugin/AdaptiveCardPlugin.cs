#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.SemanticKernel; // keep signature compatibility
using MyM365AgentDecommision.Bot.Services; // JsonSafe

namespace MyM365AgentDecommision.Bot.Plugins;

/// <summary>
/// Deterministic Adaptive Card builder: converts scored results JSON into an Adaptive Card 1.5.
/// No LLM calls; stable output with actual data rows.
/// </summary>
public sealed class AdaptiveCardPlugin
{
    // ---- DTOs we expect from FilterAndScoreAsync/GetClusterRowAsync ----
    private sealed class TopResult
    {
        public int Total { get; set; }
        public int Filtered { get; set; }
        public int Returned { get; set; }
        public string? PrimaryFactor { get; set; }
        public List<Item>? Items { get; set; }
    }

    private sealed class Item
    {
        public int rank { get; set; }
        public string cluster { get; set; } = "";
        public double score { get; set; }
        public Details? details { get; set; }
    }

    private sealed class Details
    {
        public string? region { get; set; }
        public string? dataCenter { get; set; }
        public string? availabilityZone { get; set; }
        public double? ageYears { get; set; }
        public double? coreUtilization { get; set; }
        public int? totalNodes { get; set; }
        public int? outOfServiceNodes { get; set; }
        public int? totalCores { get; set; }
        public int? usedCores { get; set; }
    }

    // Keep this method name/signature so your agent calls don’t need to change.
    [KernelFunction]
    public Task<string> GetAdaptiveCardForDataAsync(Kernel _kernel, string data)
    {
        // 1) Parse whatever the agent passed into a typed object
        if (!JsonSafe.TryDeserialize<TopResult>(data, out var result, out var err) || result is null)
            return Task.FromResult(BuildErrorCardJson($"Invalid result payload: {err ?? "unknown"}"));

        var items = result.Items ?? new List<Item>();
        if (items.Count == 0)
            return Task.FromResult(BuildInfoCardJson("No matching clusters", "Try broadening your filters or changing the region/date center constraints."));

        // show at most 12 rows to keep card readable
        var rows = items.OrderBy(i => i.rank).Take(Math.Min(12, items.Count)).ToList();

        // 2) Build card object
        var body = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["size"] = "Medium",
                ["weight"] = "Bolder",
                ["text"] = $"Top {rows.Count} Decommission Candidates"
            },
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["isSubtle"] = true,
                ["spacing"] = "Small",
                ["wrap"] = true,
                ["text"] =
                    $"Ranked by composite score. Returned {result.Returned} of {result.Filtered} filtered (total {result.Total})."
                    + (string.IsNullOrWhiteSpace(result.PrimaryFactor) ? "" : $" Primary factor: {result.PrimaryFactor}.")
            },
            // table header
            new Dictionary<string, object?>
            {
                ["type"] = "ColumnSet",
                ["spacing"] = "Medium",
                ["columns"] = new object[]
                {
                    ColHeader("#", "auto"),
                    ColHeader("Cluster", "stretch"),
                    ColHeader("Region/DC", "stretch"),
                    ColHeader("Age (yrs)", "auto"),
                    ColHeader("Util %", "auto"),
                    ColHeader("OOS", "auto"),
                    ColHeader("Score", "auto")
                }
            }
        };

        // 3) Add a row per item
        foreach (var it in rows)
        {
            var d = it.details ?? new Details();
            body.Add(new Dictionary<string, object?>
            {
                ["type"] = "ColumnSet",
                ["columns"] = new object[]
                {
                    ColCell(it.rank.ToString(), "auto", "Default"),
                    ColCell(it.cluster, "stretch", "Default", weight:"Bolder"),
                    ColCell($"{NullDash(d.region)} / {NullDash(d.dataCenter)}", "stretch", "Default"),
                    ColCell(FormatYears(d.ageYears), "auto", "Default"),
                    ColCell(FormatPct(d.coreUtilization), "auto", "Default"),
                    ColCell(FormatOOS(d.outOfServiceNodes, d.totalNodes), "auto", "Default"),
                    ColCell(Math.Round(it.score, 4).ToString("0.####"), "auto", "Default", monospace:true)
                }
            });
        }

        // 4) Assemble card
        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = body
        };

        return Task.FromResult(JsonSerializer.Serialize(card));
    }

    // ---------------- Helpers ----------------

    private static object ColHeader(string text, string width) => new Dictionary<string, object?>
    {
        ["type"] = "Column",
        ["width"] = width,
        ["items"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["weight"] = "Bolder",
                ["spacing"] = "None",
                ["wrap"] = false
            }
        }
    };

    private static object ColCell(string text, string width, string style, string? weight = null, bool monospace = false)
    {
        var tb = new Dictionary<string, object?>
        {
            ["type"] = "TextBlock",
            ["text"] = text,
            ["spacing"] = "None",
            ["wrap"] = false
        };
        if (!string.IsNullOrEmpty(weight)) tb["weight"] = weight;
        if (monospace) tb["fontType"] = "Monospace";

        return new Dictionary<string, object?>
        {
            ["type"] = "Column",
            ["width"] = width,
            ["items"] = new object[] { tb }
        };
    }

    private static string NullDash(object? v) => v switch
    {
        null => "—",
        string s when string.IsNullOrWhiteSpace(s) => "—",
        _ => v.ToString() ?? "—"
    };

    private static string FormatYears(double? years) =>
        years.HasValue ? Math.Round(years.Value, 1).ToString("0.0") : "—";

    private static string FormatPct(double? pct) =>
        pct.HasValue ? Math.Round(pct.Value, 2).ToString("0.##") : "—";

    private static string FormatOOS(int? oos, int? total) =>
        (oos.HasValue && total.HasValue && total.Value > 0) ? $"{oos}/{total}" : "—";

    private static string BuildErrorCardJson(string message)
    {
        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = new object[]
            {
                new Dictionary<string, object?> { ["type"]="TextBlock", ["text"]="Adaptive Card Error", ["weight"]="Bolder", ["size"]="Medium" },
                new Dictionary<string, object?> { ["type"]="TextBlock", ["text"]=message, ["wrap"]=true, ["isSubtle"]=true }
            }
        };
        return JsonSerializer.Serialize(card);
    }

    private static string BuildInfoCardJson(string title, string subtitle)
    {
        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = new object[]
            {
                new Dictionary<string, object?> { ["type"]="TextBlock", ["text"]=title, ["weight"]="Bolder", ["size"]="Medium" },
                new Dictionary<string, object?> { ["type"]="TextBlock", ["text"]=subtitle, ["wrap"]=true, ["isSubtle"]=true }
            }
        };
        return JsonSerializer.Serialize(card);
    }
}
