#nullable enable
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using MyM365AgentDecommision.Bot.Services;

namespace MyM365AgentDecommision.Bot.Plugins
{
    /// <summary>
    /// LLM-callable export helpers. Converts model/tool JSON output into CSV/JSON files
    /// and returns a small envelope with path, mime, and row count.
    ///
    /// Typical call (from the agent/tool plan):
    ///   - Export.ToCsv(resultJson: "<json array or object>", fileName: "top10")
    ///   - Export.ToJson(resultJson: "<json array or object>", fileName: "snapshot_2025_08_15")
    ///
    /// Notes:
    ///  • resultJson can be an array of objects or a single object.
    ///  • fileName is optional; if omitted a timestamped name is used.
    ///  • Paths are written to the temp directory of the host.
    /// </summary>
    public sealed class ExportPlugin
    {
        private readonly ExportService _export;
        private readonly ILogger<ExportPlugin> _log;

        public ExportPlugin(ExportService export, ILogger<ExportPlugin> log)
        {
            _export = export ?? throw new ArgumentNullException(nameof(export));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Return shape for export operations.</summary>
        public sealed record ExportResult(
            string Path,
            string MimeType,
            int RowCount);

        /// <summary>
        /// Export the provided JSON (array or object) to a CSV file and return its path.
        /// </summary>
        [KernelFunction, Description("Export JSON (array/object) to CSV and return a path + metadata.")]
        public Task<ExportResult> ToCsv(
            [Description("A JSON array of objects or a single object to export as rows.")]
            string resultJson,
            [Description("Optional base file name without extension, e.g., 'top10_candidates'.")]
            string? fileName = null,
            CancellationToken ct = default)
        {
            var outp = _export.ToCsv(resultJson, fileName);
            _log.LogInformation("CSV export complete: {Path} ({Rows} rows)", outp.Path, outp.RowCount);
            return Task.FromResult(new ExportResult(outp.Path, outp.MimeType, outp.RowCount));
        }

        /// <summary>
        /// Export the provided JSON (array or object) to a pretty-printed JSON file and return its path.
        /// </summary>
        [KernelFunction, Description("Export JSON (array/object) to a .json file and return a path + metadata.")]
        public Task<ExportResult> ToJson(
            [Description("A JSON array of objects or a single object to save.")]
            string resultJson,
            [Description("Optional base file name without extension, e.g., 'scored_snapshot'.")]
            string? fileName = null,
            CancellationToken ct = default)
        {
            var outp = _export.ToJson(resultJson, fileName);
            _log.LogInformation("JSON export complete: {Path} ({Rows} rows)", outp.Path, outp.RowCount);
            return Task.FromResult(new ExportResult(outp.Path, outp.MimeType, outp.RowCount));
        }
    }
}
