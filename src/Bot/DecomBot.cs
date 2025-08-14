#nullable enable
using System.Collections.Generic;
using System.Text.Json;

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;

using Microsoft.Agents.Core.Models;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

using MyM365AgentDecommision.Bot.Services;   // JsonSanitizer, JsonSafe

// Alias all decommission agent types to avoid name collisions
using Agents = MyM365AgentDecommision.Bot.Agents;

namespace MyM365AgentDecommision.Bot;

/// <summary>
/// Agents Builder bot host that bridges channel messages to the DecommissionAgent (SK).
/// It routes user text to the SK-based agent and renders either Text or an Adaptive Card 1.5.
/// </summary>
public class DecomAgentBot : AgentApplication
{
    private readonly Kernel _kernel;
    private readonly IServiceProvider _serviceProvider;
    private Agents.DecommissionAgent _decomAgent = default!;

    private const string HelpMessage = @"## Decommission Agent Help
    
I can help analyze and identify clusters for decommissioning based on various factors including age, utilization, health, and more.

### Example prompts:
- ""Show me the top 10 decommission candidates""
- ""Filter clusters in region westus2 with low utilization""
- ""Which clusters have the highest score for decommissioning?""
- ""Check eligibility of cluster ABC123""
- ""Compare decommissioning factors between clusters XYZ and ABC""
- ""What metrics are used for scoring?""
- ""Show me clusters older than 5 years with low utilization""
- ""Show me top 18 clusters exclude west region""
- ""Show me all the clusters and rank them by score""
- ""Return the cluster row for x cluster""
- ""Show me top 20 clusters, change utilization weight to 100""
- ""How does our scoring system work""
- ""What weights can I adjust""
- ""Show me what I can filter from""
- ""What makes a cluster a good candidate for decommissioning?""
- ""Help me understand why cluster XYZ has a high decommission score""
- ""Score clusters with higher weight (0.8) on age and lower weight (0.3) on utilization""
- ""Adjust scoring weights to prioritize health issues over age""
- ""Find top candidates with custom weights: age=4, utilization=0.15, health=0.2, strandedCores=0.1""
- ""What factors can I adjust the weights for?""
- MultiQueries: ""Give me top n clusters, change eligibility to x years, exclude region and only show me data center SN4""

### Customizable Scoring Factors:
- Age: How old the cluster is (ClusterAgeYears)
- Utilization: Effective core utilization (EffectiveCoreUtilization)
- Health: Region health and node health metrics (RegionHealthScore, OOSNodeRatio)
- Stranded Resources: Cores that can't be used (StrandedCoresRatio)
- Special Workloads: SQL, SLB, WARP presence (SQL_Ratio, HasSLB, HasWARP)
- Mix Ratios: Spannable vs non-spannable workloads

Type 'help' at any time to see this message again.";

    public DecomAgentBot(AgentApplicationOptions options, Kernel kernel, IServiceProvider serviceProvider)
        : base(options)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);
        OnActivity(ActivityTypes.Message, MessageActivityAsync, rank: RouteRank.Last);
    }

    protected async Task MessageActivityAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        if (turnContext?.Activity == null) return;

        var userMessage = turnContext.Activity.Text?.Trim() ?? string.Empty;
        if (string.Equals(userMessage, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(userMessage, "/help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(userMessage, "?", StringComparison.Ordinal))
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(HelpMessage), cancellationToken);
            return;
        }

        // Persist chat history across turns (Agents SDK state)
        ChatHistory chatHistory = turnState.GetValue("conversation.chatHistory", () => new ChatHistory());

        // Create a DI scope for this turn and pass it to the SK agent
        using var scope = _serviceProvider.CreateScope();
        _decomAgent = new Agents.DecommissionAgent(_kernel, scope.ServiceProvider);

        // Invoke the SK agent
        var envelope = await _decomAgent.InvokeAsync(userMessage, chatHistory);
        if (envelope is null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Sorry, I couldn't produce a response right now. Please try again or type 'help' for examples."),
                cancellationToken);
            return;
        }

        // Render either a card or plain text
        if (envelope.ContentType == Agents.DecomChatContentType.AdaptiveCard)
        {
            await SendAdaptiveCardAsync(turnContext, envelope.Content, cancellationToken);
        }
        else
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(envelope.Content), cancellationToken);
        }
    }

    protected async Task WelcomeMessageAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        foreach (ChannelAccount member in turnContext.Activity.MembersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("🙂 Welcome to the Decommission Agent! Ask me for top-N candidates, filtering, scoring, or eligibility checks. Type 'help' for examples."),
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Safely sends an Adaptive Card 1.5 by sanitizing and validating potentially messy LLM JSON.
    /// Falls back to a minimal error card if parsing fails.
    /// </summary>
    private async Task SendAdaptiveCardAsync(
        ITurnContext turnContext,
        string cardJson,
        CancellationToken cancellationToken)
    {
        // Extract a JSON object from any prose the model produced.
        var extracted = JsonSanitizer.ExtractFirstJsonObject(cardJson);

        // Try to deserialize to a plain object for the attachment payload.
        if (!JsonSafe.TryDeserialize<object>(extracted, out var cardObj, out var error))
        {
            // Use a dictionary so we can set "$schema" which isn't a valid C# identifier.
            var fallback = new Dictionary<string, object?>
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.5",
                ["body"] = new object[]
                {
                    new Dictionary<string, object?> {
                        ["type"]="TextBlock", ["text"]="Adaptive Card generation failed", ["weight"]="Bolder", ["size"]="Medium"
                    },
                    new Dictionary<string, object?> {
                        ["type"]="TextBlock", ["text"]="The generated JSON was invalid. Error details:", ["wrap"]=true
                    },
                    new Dictionary<string, object?> {
                        ["type"]="TextBlock", ["text"]=error ?? "unknown", ["wrap"]=true, ["isSubtle"]=true, ["spacing"]="Small"
                    }
                }
            };

            await turnContext.SendActivityAsync(
                MessageFactory.Attachment(new Attachment {
                    ContentType = "application/vnd.microsoft.card.adaptive",
                    Content = fallback
                }),
                cancellationToken);
            return;
        }

        var attachment = new Attachment {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = cardObj!
        };

        await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);
    }
}
