#nullable enable
using System;
using System.Text.Json;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>
    /// Centralized safe JSON handling for strings that may include prose, code fences, etc.
    /// Uses JsonSanitizer first, then System.Text.Json with tolerant options.
    /// </summary>
    public static class JsonSafe
    {
        private static readonly JsonSerializerOptions Tolerant = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static bool LooksLikeJson(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim('\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060', ' ', '\t', '\r', '\n');
            return t.StartsWith('{') || t.StartsWith('[');
        }

        public static bool TryDeserialize<T>(string raw, out T? value, out string? error)
        {
            // Try object extraction first
            var candidate = JsonSanitizer.ExtractFirstJsonObject(raw);
            if (!JsonSanitizer.TryParse(candidate, out var _, out var parseError))
            {
                // Maybe an array or still salvageable
                var alt = LooksLikeJson(raw) ? raw : candidate;
                try
                {
                    value = JsonSerializer.Deserialize<T>(alt, Tolerant);
                    error = null;
                    return value is not null;
                }
                catch (Exception ex)
                {
                    value = default;
                    error = parseError ?? ex.Message;
                    return false;
                }
            }

            try
            {
                value = JsonSerializer.Deserialize<T>(candidate, Tolerant);
                error = null;
                return value is not null;
            }
            catch (Exception ex)
            {
                value = default;
                error = ex.Message;
                return false;
            }
        }
    }
}
