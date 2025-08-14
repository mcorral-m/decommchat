#nullable enable
using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyM365AgentDecommision.Bot.Services
{
    public static class JsonSanitizer
    {
        /// <summary>
        /// Extract the first balanced JSON object from a string (LLM outputs often contain extra text).
        /// Also strips code-fences, BOM/zero-width characters, and “smart quotes”.
        /// </summary>
        public static string ExtractFirstJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            // Strip Markdown code fences
            var cleaned = Regex.Replace(text, @"^```(?:json)?\s*|\s*```$", string.Empty, RegexOptions.Multiline);

            // Trim BOM / zero-width chars
            cleaned = cleaned.Trim('\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060');

            // Normalize “smart quotes” → "
            cleaned = cleaned
                .Replace('“', '"').Replace('”', '"')
                .Replace('„', '"').Replace('‟', '"');

            // Find the first balanced {...} object
            int start = cleaned.IndexOf('{');
            if (start < 0) return cleaned.Trim();

            int depth = 0;
            bool inStr = false;
            bool esc = false;

            for (int i = start; i < cleaned.Length; i++)
            {
                char c = cleaned[i];

                if (inStr)
                {
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true;  continue; }
                    if (c == '"')  { inStr = false; continue; }
                    continue;
                }

                if (c == '"') { inStr = true; continue; }
                if (c == '{') { depth++;     continue; }
                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return cleaned.Substring(start, i - start + 1).Trim();
                }
            }

            // Fallback: return from first '{' onward
            return cleaned.Substring(start).Trim();
        }

        /// <summary>
        /// Safer Parse with trailing comma/comment tolerance. Returns false + readable error if invalid.
        /// </summary>
        public static bool TryParse(string json, out JsonDocument? doc, out string? error)
        {
            var opts = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            try
            {
                doc = JsonDocument.Parse(json, opts);
                error = null;
                return true;
            }
            catch (JsonException ex)
            {
                doc = null;
                error = BuildPrettyError(json, ex);
                return false;
            }
        }

        private static string BuildPrettyError(string json, JsonException ex)
        {
            var ln = ex.LineNumber?.ToString() ?? "?";
            var bp = ex.BytePositionInLine?.ToString() ?? "?";
            var snippet = GetSnippet(json, ex);
            return $"JSON invalid at line {ln}, byte {bp}: {ex.Message}\n{snippet}";
        }

        private static string GetSnippet(string json, JsonException ex, int context = 60)
        {
            try
            {
                var utf8 = Encoding.UTF8.GetBytes(json);

                long posLong    = ex.BytePositionInLine ?? 0L;        // nullable → long
                long posClamped = Math.Clamp(posLong, 0L, (long)utf8.Length);
                int  pos        = (int)posClamped;

                int start = Math.Max(0, pos - context);
                int len   = Math.Min(utf8.Length - start, context * 2);

                var slice = Encoding.UTF8.GetString(utf8, start, len);
                return $"Near: …{slice}…";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
