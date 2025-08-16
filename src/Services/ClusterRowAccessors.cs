#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Services;

public static class ClusterRowAccessors
{
    public sealed class Accessor
    {
        public string Name { get; init; } = "";
        public Type Type { get; init; } = typeof(object);
        public Func<ClusterRow, object?> Getter { get; init; } = _ => null!;
    }

    private static readonly Dictionary<string, Accessor> _byName =
        BuildCache(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, out Accessor acc) => _byName.TryGetValue(name, out acc!);
    public static IEnumerable<string> AllNames() => _byName.Keys.OrderBy(k => k);

    private static Dictionary<string, Accessor> BuildCache(StringComparer cmp)
    {
        var dict = new Dictionary<string, Accessor>(cmp);
        foreach (var p in typeof(ClusterRow).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!p.CanRead) continue;
            var param = Expression.Parameter(typeof(ClusterRow), "r");
            var body = Expression.Convert(Expression.Property(param, p), typeof(object));
            var getter = Expression.Lambda<Func<ClusterRow, object?>>(body, param).Compile();

            var acc = new Accessor { Name = p.Name, Type = p.PropertyType, Getter = getter };
            dict[p.Name] = acc;
        }

        // Synonyms (examples; add more as needed)
        AddAlias(dict, "Age", "ClusterAgeYears");
        AddAlias(dict, "AgeYears", "ClusterAgeYears");
        AddAlias(dict, "Util", "CoreUtilization");
        AddAlias(dict, "Utilization", "CoreUtilization");
        AddAlias(dict, "Health", "RegionHealthScore");
        AddAlias(dict, "Stranded", "StrandedCores_DNG");

        return dict;
    }

    private static void AddAlias(Dictionary<string, Accessor> dict, string alias, string target)
    {
        if (dict.TryGetValue(target, out var acc))
            dict[alias] = acc;
    }
}
