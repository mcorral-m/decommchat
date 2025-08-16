#nullable enable
using System.Text;
using System.Text.Json;

namespace MyM365AgentDecommision.Bot.Services
{
    public sealed class ExportService
    {
        public sealed record ExportOut(string Path, string MimeType, int RowCount);

        public ExportOut ToCsv(string resultJson, string? fileName = null)
        {
            using var doc = JsonDocument.Parse(NormalizeJson(resultJson));
            var root = doc.RootElement;
            var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };

            var sb = new StringBuilder();
            // collect all keys
            var headers = rows.SelectMany(GetProps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sb.AppendLine(string.Join(",", headers.Select(Escape)));

            foreach (var row in rows)
            {
                var line = string.Join(",", headers.Select(h =>
                {
                    var v = row.TryGetProperty(h, out var el) ? el.ToString() : "";
                    return Escape(v);
                }));
                sb.AppendLine(line);
            }

            var name = string.IsNullOrWhiteSpace(fileName) ? $"decom_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}" : fileName;
            var path = Path.Combine(Path.GetTempPath(), $"{name}.csv");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return new ExportOut(path, "text/csv", rows.Length);

            static IEnumerable<string> GetProps(JsonElement e) =>
                e.ValueKind == JsonValueKind.Object
                    ? e.EnumerateObject().Select(p => p.Name)
                    : Enumerable.Empty<string>();

            static string Escape(string? s)
            {
                var x = s ?? "";
                if (x.Contains('"') || x.Contains(',') || x.Contains('\n')) x = $"\"{x.Replace("\"", "\"\"")}\"";
                return x;
            }

            static string NormalizeJson(string s)
            {
                var ss = s.Trim();
                if (ss.StartsWith("```")) // allow fenced JSON
                {
                    var i = ss.IndexOf('\n');
                    var j = ss.LastIndexOf("```", StringComparison.Ordinal);
                    if (i >= 0 && j > i) ss = ss.Substring(i + 1, j - (i + 1));
                }
                return ss;
            }
        }

        public ExportOut ToJson(string resultJson, string? fileName = null)
        {
            using var doc = JsonDocument.Parse(resultJson);
            var normalized = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });

            var name = string.IsNullOrWhiteSpace(fileName) ? $"decom_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}" : fileName;
            var path = Path.Combine(Path.GetTempPath(), $"{name}.json");
            File.WriteAllText(path, normalized, Encoding.UTF8);
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 1;
            return new ExportOut(path, "application/json", rows);
        }
    }
}
