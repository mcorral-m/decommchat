using System.ComponentModel;
using System.Text.Json.Serialization;
using MyM365AgentDecommision.Bot;


namespace MyM365AgentDecommision.Bot.Agents;

public enum DecomAgentResponseContentType
{
    [JsonStringEnumMemberName("text")]
    Text,

    [JsonStringEnumMemberName("adaptive-card")]
    AdaptiveCard
}

public sealed class DecomAgentResponse
{
    [JsonPropertyName("contentType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DecomAgentResponseContentType ContentType { get; set; }

    [JsonPropertyName("content")]
    [Description("Plain text or Adaptive Card JSON (as a string).")]
    public string Content { get; set; } = "";
}
