// Program.cs
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MyM365AgentDecommision.Bot.Orchestration; // DecomQueryOrchestratorPlugin

using Microsoft.Agents.Hosting.AspNetCore; // IAgentHttpAdapter, AddCloudAdapter()
using Microsoft.Agents.Builder.App;        // IAgent
using Microsoft.Agents.Builder;
using Microsoft.Agents.Storage;            // IStorage, MemoryStorage

using MyM365AgentDecommision;                              // ConfigOptions
using MyM365AgentDecommision.Bot;                          // DecomAgentBot : IAgent
using MyM365AgentDecommision.Bot.Interfaces;               // IClusterDataProvider
using MyM365AgentDecommision.Bot.Services;                 // Engines, stores, services
using MyM365AgentDecommision.Infrastructure.Kusto;         // IKustoQueryHelperFactory, DynamicKustoQueryHelperFactory, KustoSdkDataProvider
using MyM365AgentDecommision.Bot.Plugins;                  // Plugins

var builder = WebApplication.CreateBuilder(args);

// ---------------- Web/Host plumbing ----------------
builder.Services.AddControllers();
builder.Services.AddHttpClient("WebClient", client => client.Timeout = TimeSpan.FromSeconds(600));
builder.Services.AddHttpContextAccessor();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// ---------------- Config binding ----------------
var config = builder.Configuration.Get<ConfigOptions>() ?? new ConfigOptions();

// ---------------- SK + LLM (Azure OpenAI) ----------------
if (string.IsNullOrWhiteSpace(config.Azure.OpenAIEndpoint) ||
    string.IsNullOrWhiteSpace(config.Azure.OpenAIDeploymentName) ||
    string.IsNullOrWhiteSpace(config.Azure.OpenAIApiKey))
{
    throw new InvalidOperationException(
        "Azure OpenAI settings missing. Ensure Azure:OpenAIEndpoint, Azure:OpenAIDeploymentName, Azure:OpenAIApiKey are set.");
}
// REMOVE the generic AddKernel(); we'll register a custom Kernel below
// builder.Services.AddKernel();

// Register chat completion so DI can construct the orchestrator (IChatCompletionService)
builder.Services.AddAzureOpenAIChatCompletion(
    deploymentName: config.Azure.OpenAIDeploymentName,
    endpoint:       config.Azure.OpenAIEndpoint,
    apiKey:         config.Azure.OpenAIApiKey
);

// ---------------- Data & Domain Services ----------------

// 1) Kusto helper factory (shared)
builder.Services.AddSingleton<IKustoQueryHelperFactory>(sp =>
{
    var logger        = sp.GetRequiredService<ILogger<DynamicKustoQueryHelperFactory>>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var configuration = sp.GetRequiredService<IConfiguration>();

    var kustoConfig       = configuration.GetSection("Kusto");
    var oneCapacityConfig = configuration.GetSection("OneCapacityKusto");

    var dcmClusterUri    = kustoConfig["ClusterUri"]        ?? "https://azuredcm.kusto.windows.net";
    var dcmDatabase      = kustoConfig["DatabaseName"]      ?? "AzureDCMDb";
    var onecapClusterUri = oneCapacityConfig["ClusterUri"]  ?? "https://onecapacityfollower.centralus.kusto.windows.net";
    var onecapDatabase   = oneCapacityConfig["DatabaseName"]?? "Shared";

    var useManagedIdentity = !bool.Parse(kustoConfig["UseUserPromptAuth"] ?? "false");
    var timeoutSeconds     = int.Parse(kustoConfig["TimeoutSeconds"] ?? "300");
    var defaultTimeout     = TimeSpan.FromSeconds(timeoutSeconds);

    return new DynamicKustoQueryHelperFactory(
        logger, loggerFactory,
        dcmClusterUri, dcmDatabase,
        onecapClusterUri, onecapDatabase,
        useManagedIdentity,
        tenantId: null, clientId: null, clientSecret: null, certificateThumbprint: null,
        defaultTimeout
    );
});

// 2) Data provider — matches your ctor (logger, IKustoQueryHelperFactory, IActivityContext?)
builder.Services.AddSingleton<IClusterDataProvider>(sp =>
{
    var log     = sp.GetRequiredService<ILogger<KustoSdkDataProvider>>();
    var factory = sp.GetRequiredService<IKustoQueryHelperFactory>();
    // activityContext is optional; provider will create DefaultActivityContext if null
    return new KustoSdkDataProvider(log, factory);
});

// 3) Core engines & stores
builder.Services.AddSingleton<FilteringEngine>();
builder.Services.AddSingleton<EligibilityEngine>();
builder.Services.AddSingleton<IWeightsStore, InMemoryWeightsStore>();                 // scoring weights store
builder.Services.AddSingleton<IEligibilityRulesStore, InMemoryEligibilityRulesStore>(); // ✅ added to fix InvalidOperationException
builder.Services.AddSingleton<ScoringService>();
builder.Services.AddSingleton<CardFactory>();

// If other services need a request context:
builder.Services.AddSingleton<MyM365AgentDecommision.Bot.Services.IRequestContext,
                             MyM365AgentDecommision.Bot.Services.RequestContext>();

// 4) Audit store (concrete type!)
builder.Services.AddSingleton<IAuditLog, AuditLog>();

// 5) Plugins
builder.Services.AddTransient<ScoringPlugin>();
builder.Services.AddTransient<ClusterFilteringPlugin>();
builder.Services.AddTransient<EligibilityPlugin>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddTransient<ExportPlugin>();
builder.Services.AddTransient<CardPlugin>();
builder.Services.AddTransient<AuditPlugin>();
builder.Services.AddSingleton<DecomQueryOrchestratorPlugin>();

// --- Kernel: build and import plugins as SK tools ---
builder.Services.AddSingleton<Kernel>(sp =>
{
    var kb = Kernel.CreateBuilder();

    // Ensure chat completion is available to the kernel
    kb.AddAzureOpenAIChatCompletion(
        deploymentName: config.Azure.OpenAIDeploymentName,
        endpoint:       config.Azure.OpenAIEndpoint,
        apiKey:         config.Azure.OpenAIApiKey);

    var kernel = kb.Build();

    // Existing plugin imports
    kernel.ImportPluginFromObject(sp.GetRequiredService<ScoringPlugin>(), "scoring");
    kernel.ImportPluginFromObject(sp.GetRequiredService<EligibilityPlugin>(), "eligibility");
    kernel.ImportPluginFromObject(sp.GetRequiredService<ExportPlugin>(), "export");
    kernel.ImportPluginFromObject(sp.GetRequiredService<AuditPlugin>(), "audit");

    // NEW: orchestrator import
    kernel.ImportPluginFromObject(sp.GetRequiredService<DecomQueryOrchestratorPlugin>(), "decom_orchestrator");

    return kernel;
});

// 6) Agents SDK wiring
builder.Services.AddSingleton<IStorage, MemoryStorage>(); // simple in-memory state for dev
builder.Services.AddCloudAdapter();                       // registers concrete IAgentHttpAdapter 
builder.AddAgentApplicationOptions();
builder.AddAgent<DecomAgentBot>();                       // your bot implements IAgent

//-------------------------------//
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

            // Optional smoke test
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

app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken);
});

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
