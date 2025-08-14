// Program.cs
#nullable enable
using MyM365AgentDecommision;
using MyM365AgentDecommision.Bot;                  // DecomAgentBot
using MyM365AgentDecommision.Bot.Interfaces;       // IClusterDataProvider
using MyM365AgentDecommision.Bot.Services;         // ScoringService, HybridParsingService
using MyM365AgentDecommision.Infrastructure.Kusto; // KustoSdkDataProvider, factory
using MyM365AgentDecommision.Bot.Plugins;          // ScoringPlugin, EligibilityPlugin, ClusterFilteringPlugin, AdaptiveCardPlugin

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Storage;

using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Web/Host plumbing ----------------
builder.Services.AddControllers();
builder.Services.AddHttpClient("WebClient", client => client.Timeout = TimeSpan.FromSeconds(600));
builder.Services.AddHttpContextAccessor();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// ---------------- SK + LLM ----------------
builder.Services.AddKernel();

// Bind config (see ConfigOptions type in your project)
var config = builder.Configuration.Get<ConfigOptions>() ?? new ConfigOptions();

// Prefer Azure OpenAI (project standard)
builder.Services.AddAzureOpenAIChatCompletion(
    deploymentName: config.Azure.OpenAIDeploymentName,
    endpoint:       config.Azure.OpenAIEndpoint,
    apiKey:         config.Azure.OpenAIApiKey
);

// ---------------- Data & Domain Services ----------------

// Register the Kusto helper factory from configuration
builder.Services.AddSingleton<IKustoQueryHelperFactory>(serviceProvider =>
{
    var logger         = serviceProvider.GetRequiredService<ILogger<DynamicKustoQueryHelperFactory>>();
    var loggerFactory  = serviceProvider.GetRequiredService<ILoggerFactory>();
    var configuration  = serviceProvider.GetRequiredService<IConfiguration>();

    var kustoConfig       = configuration.GetSection("Kusto");
    var oneCapacityConfig = configuration.GetSection("OneCapacityKusto");

    var dcmClusterUri    = kustoConfig["ClusterUri"]        ?? "https://azuredcm.kusto.windows.net";
    var dcmDatabase      = kustoConfig["DatabaseName"]      ?? "AzureDCMDb";
    var onecapClusterUri = oneCapacityConfig["ClusterUri"]  ?? "https://onecapacityfollower.centralus.kusto.windows.net";
    var onecapDatabase   = oneCapacityConfig["DatabaseName"]?? "Shared";

    // If UseUserPromptAuth is true, do NOT use managed identity
    var useManagedIdentity = !bool.Parse(kustoConfig["UseUserPromptAuth"] ?? "false");

    var timeoutSeconds = int.Parse(kustoConfig["TimeoutSeconds"] ?? "300");
    var defaultTimeout = TimeSpan.FromSeconds(timeoutSeconds);

    return new DynamicKustoQueryHelperFactory(
        logger,
        loggerFactory,
        dcmClusterUri,
        dcmDatabase,
        onecapClusterUri,
        onecapDatabase,
        useManagedIdentity,
        tenantId: null,
        clientId: null,
        clientSecret: null,
        certificateThumbprint: null,
        defaultTimeout
    );
});

// (Optional) activity context used by provider (if applicable)
builder.Services.AddSingleton<IActivityContext, DefaultActivityContext>();

// Data provider used by plugins/services
builder.Services.AddSingleton<IClusterDataProvider, KustoSdkDataProvider>();

// Instance domain services used by plugins
builder.Services.AddSingleton<ScoringService>();

// 🔹 Hybrid parsing service (needs Kernel from DI)
builder.Services.AddSingleton<HybridParsingService>(); // NEW: enables hybrid NL→Criteria/Weights in plugins

// NOTE: Filter/Eligibility engines are static; no DI needed.

// Plugins (resolved by KernelPluginFactory inside the agent)
builder.Services.AddTransient<ScoringPlugin>();
builder.Services.AddTransient<EligibilityPlugin>();
builder.Services.AddTransient<ClusterFilteringPlugin>();
builder.Services.AddTransient<AdaptiveCardPlugin>();

// ---------------- Agents Builder Hosting ----------------

// Volatile state for dev. For prod, use a durable IStorage implementation.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// Pull AgentApplicationOptions from config/DI and register defaults
builder.AddAgentApplicationOptions();
builder.Services.AddTransient<AgentApplicationOptions>();

// Register the bot host (routes messages to the SK DecommissionAgent internally)
builder.AddAgent<DecomAgentBot>();

var app = builder.Build();

// ---------------- Optional: Kusto connection check on startup ----------------
if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Playground")
{
    using var scope = app.Services.CreateScope();
    var clusterDataProvider = scope.ServiceProvider.GetRequiredService<IClusterDataProvider>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    Console.WriteLine("Checking Kusto connection...");
    logger.LogInformation("Checking Kusto connection...");
    try
    {
        var connectionResult = await clusterDataProvider.TestConnectionAsync();
        if (connectionResult)
        {
            var dataSourceInfo = await clusterDataProvider.GetDataSourceInfoAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Kusto connection successful: {dataSourceInfo}");
            Console.ResetColor();
            logger.LogInformation("Kusto connection successful: {DataSourceInfo}", dataSourceInfo);

            // Optional smoke test to verify query file loading
            logger.LogInformation("Testing query execution to verify query file loading...");
            Console.WriteLine("Testing query execution to verify query file loading...");
            try
            {
                var testData = await clusterDataProvider.GetClusterDataAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✅ Query executed successfully, returned {testData.Count} clusters");
                Console.ResetColor();
                logger.LogInformation("Query executed successfully, returned {Count} clusters", testData.Count);
            }
            catch (Exception queryEx)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"⚠️ Query execution failed: {queryEx.Message}");
                Console.ResetColor();
                logger.LogWarning(queryEx, "Query execution failed");
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Kusto connection failed. Data-dependent features may not work properly.");
            Console.ResetColor();
            logger.LogWarning("Kusto connection failed. Data-dependent features may not work properly.");
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error checking Kusto connection: {ex.Message}");
        Console.ResetColor();
        logger.LogError(ex, "Error occurred while checking Kusto connection.");
    }
}

// ---------------- Middleware & Endpoints ----------------
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Agents endpoint (Teams/Copilot adapter target)
app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken);
});

// Simple health/root
if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Playground")
{
    app.MapGet("/", () => "Decom Bot");
    app.UseDeveloperExceptionPage();
    app.MapControllers().AllowAnonymous();
}
else
{
    app.MapControllers();
}

app.Run();
