#nullable enable
using System;
using System.Collections.Concurrent;

namespace MyM365AgentDecommision.Bot.Services
{
    /// <summary>Persistent scoring weights per session/user.</summary>
    public interface IWeightsStore
    {
        ScoringService.WeightConfig Get(string key);
        void Set(string key, ScoringService.WeightConfig weights);
        void Reset(string key);
    }

    /// <summary>Thread-safe in-memory store (good enough for dev/testing).</summary>
    public sealed class InMemoryWeightsStore : IWeightsStore
    {
        private readonly ConcurrentDictionary<string, ScoringService.WeightConfig> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public ScoringService.WeightConfig Get(string key) =>
            _map.TryGetValue(key, out var w) ? w : ScoringService.DefaultWeights();

        public void Set(string key, ScoringService.WeightConfig weights) =>
            _map[key] = ScoringService.Rebalance(new ScoringService.WeightConfig(weights));

        public void Reset(string key) => _map.TryRemove(key, out _);
    }
}
