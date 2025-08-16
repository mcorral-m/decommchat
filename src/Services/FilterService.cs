#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Services;

public enum FilterOp { Eq, Ne, Lt, Lte, Gt, Gte, In, NotIn, Like, NotLike }

public sealed class FilterClause
{
    public string Field { get; init; } = "";
    public FilterOp Op { get; init; }
    public object? Value { get; init; }
}

public sealed class FilterCriteria
{
    public string? AsOf { get; init; } // audit only (MVP)
    public List<FilterClause> And { get; init; } = new();
    public List<FilterClause> Or { get; init; } = new();
    public List<FilterClause> Not { get; init; } = new();
}

public sealed class FilteringEngine
{
   public IEnumerable<ClusterRow> Apply(IEnumerable<ClusterRow> rows, FilterCriteria c)
{
    var q = rows;

    // AND: every clause must match
    if (c.And.Any())
        q = q.Where(r => c.And.All(cl => Match(r, cl)));

    // OR: at least one clause must match
    if (c.Or.Any())
        q = q.Where(r => c.Or.Any(cl => Match(r, cl)));

    // NOT: none of the clauses may match  ✅ (fixed)
    // OLD (too weak): q = q.Where(r => c.Not.All(cl => Match(r, cl)));
    if (c.Not.Any())
        q = q.Where(r => !c.Not.Any(cl => Match(r, cl)));

    return q;
}

    private static bool Match(ClusterRow r, FilterClause cl)
    {
        if (!ClusterRowAccessors.TryGet(cl.Field, out var acc)) return false;
        var v = acc.Getter(r);
        return Compare(v, cl.Value, cl.Op, acc.Type);
    }

    private static bool Compare(object? left, object? right, FilterOp op, Type t)
    {
        // Null-safe casts for common primitives
        double? ToDouble(object? o) =>
            o is null ? null :
            o is IConvertible conv ? Convert.ToDouble(conv) :
            double.TryParse(o.ToString(), out var d) ? d : null;

        string? ToStr(object? o) => o?.ToString();

        switch (op)
        {
            case FilterOp.Eq:    return string.Equals(ToStr(left), ToStr(right), StringComparison.OrdinalIgnoreCase);
            case FilterOp.Ne:    return !string.Equals(ToStr(left), ToStr(right), StringComparison.OrdinalIgnoreCase);
            case FilterOp.Lt:    return ToDouble(left) <  ToDouble(right);
            case FilterOp.Lte:   return ToDouble(left) <= ToDouble(right);
            case FilterOp.Gt:    return ToDouble(left) >  ToDouble(right);
            case FilterOp.Gte:   return ToDouble(left) >= ToDouble(right);
            case FilterOp.In:    return right is IEnumerable<object> set && set.Any(x => string.Equals(ToStr(left), ToStr(x), StringComparison.OrdinalIgnoreCase));
            case FilterOp.NotIn: return right is IEnumerable<object> set2 && !set2.Any(x => string.Equals(ToStr(left), ToStr(x), StringComparison.OrdinalIgnoreCase));
            case FilterOp.Like:    return (ToStr(left) ?? "").Contains(ToStr(right) ?? "", StringComparison.OrdinalIgnoreCase);
            case FilterOp.NotLike: return !(ToStr(left) ?? "").Contains(ToStr(right) ?? "", StringComparison.OrdinalIgnoreCase);
            default: return false;
        }
    }

    public IEnumerable<ClusterRow> SortBy(IEnumerable<ClusterRow> rows, string? field, bool desc)
    {
        if (string.IsNullOrWhiteSpace(field) || !ClusterRowAccessors.TryGet(field, out var acc))
            return rows;

        return desc
            ? rows.OrderByDescending(r => acc.Getter(r))
            : rows.OrderBy(r => acc.Getter(r));
    }
}
