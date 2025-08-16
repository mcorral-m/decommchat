// src/Services/AuditLog.cs
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MyM365AgentDecommision.Bot.Services;

public interface IAuditLog
{
    AuditRunConfig? GetLastRunConfig();
    void SetLastRunConfig(AuditRunConfig config);

    AuditLogItem LogExplain(string explainJson, string? summary = null, string? actor = null);
    void LogWeightChange(object newWeights, string? summary = null, string? actor = null);

    IReadOnlyList<AuditLogItem> GetWeightHistory(int days);
}

public sealed record AuditRunConfig(
    DateTime TimestampUtc,
    object AppliedWeights,
    object? AppliedRules,
    object? Criteria,
    string? AsOf,
    string? UtilWindow,
    string? Actor
);

public sealed record AuditLogItem(
    string Id,
    DateTime TimestampUtc,
    string Kind,              // "explain" | "weights" | others
    string Summary,
    string? Actor = null
);

/// <summary>
/// Simple in-memory implementation (kept for tests/dev).
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private AuditRunConfig? _lastRunConfig;

    // Keep a rolling buffer of last N items
    private const int MaxItems = 500;
    private readonly ConcurrentQueue<AuditLogItem> _items = new();

    public AuditRunConfig? GetLastRunConfig() => _lastRunConfig;

    public void SetLastRunConfig(AuditRunConfig config)
    {
        lock (_gate)
        {
            _lastRunConfig = config with { TimestampUtc = DateTime.UtcNow };
        }
    }

    public AuditLogItem LogExplain(string explainJson, string? summary = null, string? actor = null)
    {
        var item = new AuditLogItem(
            Id: Guid.NewGuid().ToString("n"),
            TimestampUtc: DateTime.UtcNow,
            Kind: "explain",
            Summary: summary ?? $"explain len={explainJson?.Length ?? 0}",
            Actor: actor
        );
        Enqueue(item);
        return item;
    }

    public void LogWeightChange(object newWeights, string? summary = null, string? actor = null)
    {
        var item = new AuditLogItem(
            Id: Guid.NewGuid().ToString("n"),
            TimestampUtc: DateTime.UtcNow,
            Kind: "weights",
            Summary: summary ?? "weights updated",
            Actor: actor
        );
        Enqueue(item);
    }

    public IReadOnlyList<AuditLogItem> GetWeightHistory(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        return _items.Where(i => i.Kind == "weights" && i.TimestampUtc >= cutoff)
                     .OrderByDescending(i => i.TimestampUtc)
                     .ToList();
    }

    private void Enqueue(AuditLogItem item)
    {
        _items.Enqueue(item);
        while (_items.Count > MaxItems && _items.TryDequeue(out _)) { /* drop oldest */ }
    }
}

/// <summary>
/// File-backed audit log (JSONL for events + JSON for last-run).
/// Safe for concurrent writers via a lock; rotates when file grows large.
/// </summary>
public class FileAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _eventsPath;
    private readonly string _lastRunPath;

    private AuditRunConfig? _lastRunConfigCache;

    private const long MaxBytesBeforeRotate = 10 * 1024 * 1024; // 10MB per log file
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileAuditLog(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
        _eventsPath = Path.Combine(_dir, "audit_log.jsonl");
        _lastRunPath = Path.Combine(_dir, "last_run.json");

        // Lazy-load last run if present
        _lastRunConfigCache = TryLoadLastRun();
    }

    public AuditRunConfig? GetLastRunConfig()
    {
        lock (_gate)
        {
            return _lastRunConfigCache ??= TryLoadLastRun();
        }
    }

    public void SetLastRunConfig(AuditRunConfig config)
    {
        var stamped = config with { TimestampUtc = DateTime.UtcNow };
        lock (_gate)
        {
            _lastRunConfigCache = stamped;
            SafeReplaceJson(_lastRunPath, stamped);
        }
    }

    public AuditLogItem LogExplain(string explainJson, string? summary = null, string? actor = null)
    {
        var item = new AuditLogItem(
            Id: Guid.NewGuid().ToString("n"),
            TimestampUtc: DateTime.UtcNow,
            Kind: "explain",
            Summary: summary ?? $"explain len={explainJson?.Length ?? 0}",
            Actor: actor
        );
        AppendEvent(item);
        return item;
    }

    public void LogWeightChange(object newWeights, string? summary = null, string? actor = null)
    {
        var item = new AuditLogItem(
            Id: Guid.NewGuid().ToString("n"),
            TimestampUtc: DateTime.UtcNow,
            Kind: "weights",
            Summary: summary ?? "weights updated",
            Actor: actor
        );
        AppendEvent(item);
    }

    public IReadOnlyList<AuditLogItem> GetWeightHistory(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        var result = new List<AuditLogItem>();

        try
        {
            if (!File.Exists(_eventsPath)) return result;

            // Read all lines; filter to "weights" and by time window
            // (JSONL keeps one event per line)
            foreach (var line in File.ReadLines(_eventsPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<AuditLogItem>(line, JsonOpts);
                    if (evt is { } e &&
                        e.Kind.Equals("weights", StringComparison.OrdinalIgnoreCase) &&
                        e.TimestampUtc >= cutoff)
                    {
                        result.Add(e);
                    }
                }
                catch { /* skip bad line */ }
            }

            result.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
            return result;
        }
        catch
        {
            // On IO errors, return what we have
            return result;
        }
    }

    // --- internals ----------------------------------------------------------

    private void AppendEvent(AuditLogItem item)
    {
        var json = JsonSerializer.Serialize(item, JsonOpts) + Environment.NewLine;
        lock (_gate)
        {
            RotateIfNeeded_NoLock();
            File.AppendAllText(_eventsPath, json, Encoding.UTF8);
        }
    }

    private void RotateIfNeeded_NoLock()
    {
        try
        {
            var fi = new FileInfo(_eventsPath);
            if (fi.Exists && fi.Length >= MaxBytesBeforeRotate)
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var rotated = Path.Combine(_dir, $"audit_log_{stamp}.jsonl");
                File.Move(_eventsPath, rotated, overwrite: false);
            }
        }
        catch
        {
            // Non-fatal: if rotation fails, keep writing to current file
        }
    }

    private AuditRunConfig? TryLoadLastRun()
    {
        try
        {
            if (!File.Exists(_lastRunPath)) return null;
            var json = File.ReadAllText(_lastRunPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AuditRunConfig>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static void SafeReplaceJson<T>(string path, T payload)
    {
        try
        {
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // best-effort
        }
    }
}

/// <summary>
/// Concrete type used by Program.cs. By default writes under App_Data/audit next to the app.
/// </summary>
public sealed class AuditLog : FileAuditLog
{
    public AuditLog() : base(GetDefaultDirectory()) { }
    public AuditLog(string directory) : base(directory) { }

    private static string GetDefaultDirectory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "audit");
        Directory.CreateDirectory(root);
        return root;
    }
}
