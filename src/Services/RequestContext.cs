// RequestContext.cs
#nullable enable
using System;
using System.Threading;

namespace MyM365AgentDecommision.Bot.Services
{
    public interface IRequestContext
    {
        string ConversationId { get; }
        string Actor { get; }
        void Set(string conversationId, string actor);
        void Clear();
    }

    /// <summary>
    /// Ambient, per-async-flow context for the current turn.
    /// Uses AsyncLocal so plugins can read it without changing SK signatures.
    /// </summary>
    public sealed class RequestContext : IRequestContext
    {
        private sealed class Holder { public string? Conv; public string? Actor; }
        private static readonly AsyncLocal<Holder?> _state = new();

        public string ConversationId => _state.Value?.Conv ?? "default";
        public string Actor          => _state.Value?.Actor ?? "user";

        public void Set(string conversationId, string actor)
        {
            _state.Value ??= new Holder();
            _state.Value.Conv  = conversationId ?? "default";
            _state.Value.Actor = actor ?? "user";
        }

        public void Clear() => _state.Value = null;
    }
}
