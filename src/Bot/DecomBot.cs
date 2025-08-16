// DecomAgentBot.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

using MyM365AgentDecommision.Bot.Plugins;  // ScoringPlugin, ClusterFilteringPlugin, EligibilityPlugin, CardPlugin, ExportPlugin, AuditPlugin
using MyM365AgentDecommision.Bot.Services; // IRequestContext

namespace MyM365AgentDecommision.Bot;

/// <summary>
/// Agents Builder bot host that bridges channel messages to the SK Kernel.
/// It routes user text to the LLM (with function calling) and renders either Text or an Adaptive Card 1.5.
/// </summary>
public sealed class DecomAgentBot : AgentApplication
{
    private readonly Kernel _kernel;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRequestContext _req;   // ambient per-turn context

    // Prefer markdown tables everywhere unless the user explicitly asks for a card.
    private const bool PreferMarkdownTables = true;

    private enum OutputMode { Auto, Markdown, Card, Json }

    private static OutputMode DetectOutputMode(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return OutputMode.Auto;

        var m = message.ToLowerInvariant();

        // strong signals
        if (m.Contains("json only") || m.Contains("return json") || m.Contains("as json")) return OutputMode.Json;
        if (m.Contains("adaptive card") || m.Contains("render a card") || m.Contains("card ui")) return OutputMode.Card;
        if (m.Contains("table") || m.Contains("as a table")) return OutputMode.Markdown;

        // default behavior
        return OutputMode.Auto;
    }

    public DecomAgentBot(
        AgentApplicationOptions options,
        Kernel kernel,
        IServiceProvider serviceProvider,
        IRequestContext req)
        : base(options)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _req = req ?? throw new ArgumentNullException(nameof(req));

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);
        OnActivity(ActivityTypes.Message, MessageActivityAsync, rank: RouteRank.Last);
    }

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
- Utilization: Effective core utilization (CoreUtilization / EffectiveCoreUtilization)
- Health: Region health and node health metrics (RegionHealthScore, OOSNodeRatio)
- Stranded Resources: Cores that can't be used (StrandedCores / StrandedCoresRatio)
- Special Workloads: SQL, SLB, WARP presence (SQL_Ratio, HasSLB, HasWARP)
- Mix Ratios: Spannable vs non-spannable workloads

Type 'help' at any time to see this message again.";

    // NOTE: private (not protected) to avoid CS0628 in a sealed type
    private async Task MessageActivityAsync(
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

        // ---- Set ambient context for this turn (used by plugins for persistence keys) ----
        var conversationId = turnContext.Activity.Conversation?.Id ?? "default";
        var actor = turnContext.Activity.From?.Name ?? turnContext.Activity.From?.Id ?? "user";
        _req.Set(conversationId, actor);
        try
        {
            // Persist chat history across turns (Agents SDK state)
            ChatHistory chatHistory = turnState.GetValue("conversation.chatHistory", () => new ChatHistory());
            EnsureSystemMessage(chatHistory);
            chatHistory.AddUserMessage(userMessage);

            // Create a DI scope for this turn and import plugins into a child kernel
            using var scope = _serviceProvider.CreateScope();
            var child = _kernel.Clone();

            child.ImportPluginFromObject(scope.ServiceProvider.GetRequiredService<ScoringPlugin>(),           nameof(ScoringPlugin));
            child.ImportPluginFromObject(scope.ServiceProvider.GetRequiredService<ClusterFilteringPlugin>(), nameof(ClusterFilteringPlugin));
            child.ImportPluginFromObject(scope.ServiceProvider.GetRequiredService<EligibilityPlugin>(),      nameof(EligibilityPlugin));
            child.ImportPluginFromObject(scope.ServiceProvider.GetRequiredService<CardPlugin>(),             nameof(CardPlugin));
            child.ImportPluginFromObject(scope.ServiceProvider.GetRequiredService<ExportPlugin>(),           nameof(ExportPlugin));
            // Optional:
            // child.ImportPluginFromObject(scope.ServiceProvider.GetRequiredService<AuditPlugin>(),        nameof(AuditPlugin));

            var chat = child.GetRequiredService<IChatCompletionService>();

            // Auto invoke any imported kernel functions
            var settings = new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
            };

            ChatMessageContent result;
            try
            {
                // Correct 4-arg overload: (history, settings, kernel, cancellationToken)
                result = await chat.GetChatMessageContentAsync(chatHistory, settings, child, cancellationToken);
            }
            catch (Exception ex)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text($"Sorry, something went wrong calling the model: {ex.Message}"),
                    cancellationToken);
                return;
            }

            var assistantText = result?.Content?.ToString() ?? string.Empty;

            // ---- Flexible output routing (JSON / Card / Markdown) ----
            var mode = DetectOutputMode(userMessage);

            // Only allow Adaptive Cards when explicitly requested, OR when you turn off the default markdown preference
            var allowAdaptive =
                mode == OutputMode.Card ||
                (!PreferMarkdownTables && mode == OutputMode.Auto);

            if (mode == OutputMode.Json)
            {
                // Raw JSON only — no card detection, no narration
                var jsonOnly = assistantText?.Trim();
                if (string.IsNullOrWhiteSpace(jsonOnly))
                    jsonOnly = "{}";

                // If the model wrapped JSON in a code fence, strip it
                jsonOnly = ExtractFirstJsonObject(jsonOnly);

                await turnContext.SendActivityAsync(MessageFactory.Text(jsonOnly), cancellationToken);
                chatHistory.AddAssistantMessage(jsonOnly);
            }
            else if (allowAdaptive && TryExtractAdaptiveCardJson(assistantText, out var cardJson))
            {
                // Explicitly asked for a card (or markdown preference disabled): send the card
                await SendAdaptiveCardAsync(turnContext, cardJson, cancellationToken);
                chatHistory.AddAssistantMessage("(sent an Adaptive Card)");
            }
            else
            {
                // Fallback (and default): plain text/markdown
                var textOut = string.IsNullOrWhiteSpace(assistantText)
                    ? "I didn't get a usable response. Try rephrasing or type 'help' for examples."
                    : assistantText;

                await turnContext.SendActivityAsync(MessageFactory.Text(textOut), cancellationToken);
                chatHistory.AddAssistantMessage(textOut);
            }

            // Save updated history back into turn state
            turnState.SetValue("conversation.chatHistory", chatHistory);
        }
        finally
        {
            // Ensure we don't leak ambient context across requests
            _req.Clear();
        }
    }

    // NOTE: private (not protected) to avoid CS0628 in a sealed type
    private async Task WelcomeMessageAsync(
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
        var extracted = ExtractFirstJsonObject(cardJson);

        if (!TryDeserialize(extracted, out object? cardObj, out var error))
        {
            // Fallback card with error details
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
                MessageFactory.Attachment(new Attachment
                {
                    ContentType = "application/vnd.microsoft.card.adaptive",
                    Content = fallback
                }),
                cancellationToken);
            return;
        }

        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = cardObj!
        };

        await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);
    }

    // ------------------------------- Helpers -----------------------------------

    private static void EnsureSystemMessage(ChatHistory history)
    {
        // Add a single system primer the first time
        bool hasSystem = false;
        foreach (var m in history)
        {
            if (m.Role == AuthorRole.System) { hasSystem = true; break; }
        }
        if (hasSystem) return;

        var system = new StringBuilder()
            .AppendLine("You are the Decommission Agent. Be precise, concise, and deterministic.")
            .AppendLine("You have tools (SK functions) for filtering, scoring, eligibility, cards, export, and audit.")
            .AppendLine("- Use filtering & scoring tools to compute results rather than inventing data.")
            .AppendLine("- Output controls the user can request explicitly:")
            .AppendLine("  • \"json only\" / \"return json\": return raw JSON only (no narration, no card).")
            .AppendLine("  • \"table\": return a markdown table (preferred default).")
            .AppendLine("  • \"adaptive card\": return an Adaptive Card v1.5 JSON (and nothing else).")
            .AppendLine("- Prefer markdown tables by default unless the user asks for an Adaptive Card.")
            .AppendLine("- If the user asks to adjust weights, call ScoringPlugin methods; respect per-conversation persistence if available.")
            .AppendLine("- For plain questions, respond with short text.")
            .AppendLine("Never fabricate field names; prefer the ones returned by the plugins.")
            .ToString();

        history.AddSystemMessage(system);
    }

    private static string ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "{}";

        // Prefer fenced code block ```json ... ```
        int fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            int fenceEnd = text.IndexOf("```", fenceStart + 3, StringComparison.Ordinal);
            if (fenceEnd > fenceStart)
            {
                var block = text.Substring(fenceStart + 3, fenceEnd - (fenceStart + 3));
                var trimmed = block.Trim();
                if (trimmed.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(4).TrimStart();
                return trimmed.Trim();
            }
        }

        // Otherwise, scan for first balanced {...}
        int start = text.IndexOf('{');
        if (start < 0) return text.Trim();

        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1).Trim();
                }
            }
        }

        // No balanced braces; return as-is
        return text.Trim();
    }

    private static bool TryDeserialize(string json, out object? obj, out string? error)
    {
        try
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            obj = JsonSerializer.Deserialize<object>(json, opts);
            error = null;
            return obj is not null;
        }
        catch (Exception ex)
        {
            obj = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryExtractAdaptiveCardJson(string text, out string cardJson)
    {
        cardJson = string.Empty;
        var candidate = ExtractFirstJsonObject(text);
        try
        {
            using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var t) &&
                string.Equals(t.GetString(), "AdaptiveCard", StringComparison.OrdinalIgnoreCase))
            {
                cardJson = candidate;
                return true;
            }
        }
        catch
        {
            // ignore parse errors; fall back to text
        }
        return false;
    }
}