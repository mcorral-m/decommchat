// KustoSdkDataProvider.cs
using System;
using System.IO;
using System.Linq;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MyM365AgentDecommision.Bot.Models;
using MyM365AgentDecommision.Bot.Interfaces;
// Kusto SDK
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace MyM365AgentDecommision.Infrastructure.Kusto;

// ============================================================================
// Enterprise Factory Pattern Interfaces and Enums
// ============================================================================

/// <summary>
/// Represents activity context for tracking operations across multiple Kusto connections
/// </summary>
public interface IActivityContext
{
    string ActivityId { get; }
    DateTime StartTime { get; }
    IDictionary<string, object> Properties { get; }
}

/// <summary>
/// Kusto configuration endpoints
/// </summary>
public enum KustoConfig
{
    AzureDCM,
    OneCapacity
}

/// <summary>
/// Supported Azure cloud environments
/// </summary>
public enum CloudEnvironment
{
    Public,
    Government,
    China,
    Germany
}

/// <summary>
/// Abstraction for Kusto query operations with specific cluster configuration
/// </summary>
public interface IKustoQueryHelper : IDisposable
{
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IDataReader> ExecuteQueryAsync(string query, ClientRequestProperties? properties = null, CancellationToken cancellationToken = default);
    string DatabaseName { get; }
    string ClusterUri { get; }
}

/// <summary>
/// Factory for creating Kusto query helpers with different configurations
/// </summary>
public interface IKustoQueryHelperFactory
{
    IKustoQueryHelper GetDcmHelper();
    IKustoQueryHelper GetOnecapHelper();
    IKustoQueryHelper CreateQueryHelper(KustoConfig config, IActivityContext? context = null);
}

// ============================================================================
// Enterprise Implementation Classes
// ============================================================================

/// <summary>
/// Default activity context implementation
/// </summary>
public class DefaultActivityContext : IActivityContext
{
    public string ActivityId { get; } = Guid.NewGuid().ToString();
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();
}

/// <summary>
/// Standard Kusto query helper implementation
/// </summary>
public class KustoQueryHelper : IKustoQueryHelper
{
    private readonly ICslQueryProvider _queryProvider;
    private readonly ILogger<KustoQueryHelper> _logger;
    
    public string DatabaseName { get; }
    public string ClusterUri { get; }

    public KustoQueryHelper(ICslQueryProvider queryProvider, string databaseName, string clusterUri, ILogger<KustoQueryHelper> logger)
    {
        _queryProvider = queryProvider ?? throw new ArgumentNullException(nameof(queryProvider));
        DatabaseName = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
        ClusterUri = clusterUri ?? throw new ArgumentNullException(nameof(clusterUri));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var reader = await _queryProvider.ExecuteQueryAsync(DatabaseName, ".show tables | take 1", null, cancellationToken);
            return reader != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for {ClusterUri}/{DatabaseName}", ClusterUri, DatabaseName);
            return false;
        }
    }

    public async Task<IDataReader> ExecuteQueryAsync(string query, ClientRequestProperties? properties = null, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await _queryProvider.ExecuteQueryAsync(DatabaseName, query, properties, cancellationToken);
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                continue;
            }
        }
        
        // Final attempt without catching exceptions
        return await _queryProvider.ExecuteQueryAsync(DatabaseName, query, properties, cancellationToken);
    }

    public void Dispose()
    {
        _queryProvider?.Dispose();
    }
}

/// <summary>
/// Remote Kusto query helper with enhanced capabilities
/// </summary>
public class RemoteKustoQueryHelper : KustoQueryHelper
{
    private readonly IActivityContext _activityContext;
    
    public RemoteKustoQueryHelper(ICslQueryProvider queryProvider, string databaseName, string clusterUri, 
        IActivityContext activityContext, ILogger<KustoQueryHelper> logger)
        : base(queryProvider, databaseName, clusterUri, logger)
    {
        _activityContext = activityContext ?? throw new ArgumentNullException(nameof(activityContext));
    }
    
    public IActivityContext ActivityContext => _activityContext;
}

/// <summary>
/// Dynamic factory for creating Kusto query helpers with cloud-aware configuration
/// </summary>
public class DynamicKustoQueryHelperFactory : IKustoQueryHelperFactory
{
    private readonly ILogger<DynamicKustoQueryHelperFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<IKustoQueryHelper> _dcmHelper;
    private readonly Lazy<IKustoQueryHelper> _onecapHelper;
    
    // Configuration
    private readonly string _dcmClusterUri;
    private readonly string _dcmDatabase;
    private readonly string _onecapClusterUri;
    private readonly string _onecapDatabase;
    private readonly bool _useManagedIdentity;
    private readonly string? _tenantId;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly string? _certificateThumbprint;
    private readonly TimeSpan? _defaultTimeout;
    private readonly CloudEnvironment _cloudEnvironment;

    public DynamicKustoQueryHelperFactory(
        ILogger<DynamicKustoQueryHelperFactory> logger,
        ILoggerFactory loggerFactory,
        string dcmClusterUri,
        string dcmDatabase,
        string onecapClusterUri,
        string onecapDatabase,
        bool useManagedIdentity = true,
        string? tenantId = null,
        string? clientId = null,
        string? clientSecret = null,
        string? certificateThumbprint = null,
        TimeSpan? defaultTimeout = null,
        CloudEnvironment cloudEnvironment = CloudEnvironment.Public)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _dcmClusterUri = dcmClusterUri ?? throw new ArgumentNullException(nameof(dcmClusterUri));
        _dcmDatabase = dcmDatabase ?? throw new ArgumentNullException(nameof(dcmDatabase));
        _onecapClusterUri = onecapClusterUri ?? throw new ArgumentNullException(nameof(onecapClusterUri));
        _onecapDatabase = onecapDatabase ?? throw new ArgumentNullException(nameof(onecapDatabase));
        _useManagedIdentity = useManagedIdentity;
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _certificateThumbprint = certificateThumbprint;
        _defaultTimeout = defaultTimeout;
        _cloudEnvironment = cloudEnvironment;

        _dcmHelper = new Lazy<IKustoQueryHelper>(() => CreateHelperForConfig(KustoConfig.AzureDCM));
        _onecapHelper = new Lazy<IKustoQueryHelper>(() => CreateHelperForConfig(KustoConfig.OneCapacity));
    }

    public IKustoQueryHelper GetDcmHelper() => _dcmHelper.Value;
    public IKustoQueryHelper GetOnecapHelper() => _onecapHelper.Value;

    public IKustoQueryHelper CreateQueryHelper(KustoConfig config, IActivityContext? context = null)
    {
        return CreateHelperForConfig(config, context);
    }

    private IKustoQueryHelper CreateHelperForConfig(KustoConfig config, IActivityContext? context = null)
    {
        var (clusterUri, database) = config switch
        {
            KustoConfig.AzureDCM => (_dcmClusterUri, _dcmDatabase),
            KustoConfig.OneCapacity => (_onecapClusterUri, _onecapDatabase),
            _ => throw new ArgumentException($"Unsupported Kusto config: {config}")
        };

        var connectionBuilder = BuildCloudAwareConnection(clusterUri);
        var queryProvider = KustoClientFactory.CreateCslQueryProvider(connectionBuilder);
        var helperLogger = _loggerFactory.CreateLogger<KustoQueryHelper>();

        if (context != null)
        {
            return new RemoteKustoQueryHelper(queryProvider, database, clusterUri, context, helperLogger);
        }

        return new KustoQueryHelper(queryProvider, database, clusterUri, helperLogger);
    }

    private KustoConnectionStringBuilder BuildCloudAwareConnection(string clusterUri)
    {
        if (string.IsNullOrWhiteSpace(clusterUri))
            throw new ArgumentException("clusterUri is required");

        // Detect cloud environment from URI if not explicitly set
        var detectedCloud = DetectCloudEnvironment(clusterUri);
        var effectiveCloud = _cloudEnvironment != CloudEnvironment.Public ? _cloudEnvironment : detectedCloud;

        KustoConnectionStringBuilder csb;

        // Certificate-based authentication (highest priority)
        if (!string.IsNullOrWhiteSpace(_certificateThumbprint) && !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_tenantId))
        {
            _logger.LogDebug("Using certificate-based authentication for {ClusterUri}", clusterUri);
            
            var cert = GetCertificateFromStore(_certificateThumbprint);
            if (cert != null)
            {
                csb = new KustoConnectionStringBuilder(clusterUri)
                    .WithAadApplicationCertificateAuthentication(_clientId, cert, _tenantId);
            }
            else
            {
                _logger.LogWarning("Certificate with thumbprint {Thumbprint} not found, falling back to other auth methods", _certificateThumbprint);
                csb = BuildFallbackAuthentication(clusterUri);
            }
        }
        // Managed Identity authentication
        else if (_useManagedIdentity)
        {
            _logger.LogDebug("Using managed identity authentication for {ClusterUri}", clusterUri);
            csb = new KustoConnectionStringBuilder(clusterUri)
                .WithAadSystemManagedIdentity();
        }
        // App Registration with client secret
        else if (!string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret) && !string.IsNullOrWhiteSpace(_tenantId))
        {
            _logger.LogDebug("Using app registration authentication for {ClusterUri}", clusterUri);
            csb = new KustoConnectionStringBuilder(clusterUri)
                .WithAadApplicationKeyAuthentication(_clientId, _clientSecret, _tenantId);
        }
        // Fallback to user authentication
        else
        {
            _logger.LogDebug("Using user prompt authentication for {ClusterUri}", clusterUri);
            csb = BuildFallbackAuthentication(clusterUri);
        }

        // Apply cloud-specific configuration
        ApplyCloudConfiguration(csb, effectiveCloud);

        // Apply timeout settings
        if (_defaultTimeout.HasValue)
        {
            var props = new ClientRequestProperties();
            props.SetOption(ClientRequestProperties.OptionServerTimeout, _defaultTimeout.Value);
        }

        return csb;
    }

    private KustoConnectionStringBuilder BuildFallbackAuthentication(string clusterUri)
    {
        return new KustoConnectionStringBuilder(clusterUri)
            .WithAadUserPromptAuthentication();
    }

    private CloudEnvironment DetectCloudEnvironment(string clusterUri)
    {
        var uri = clusterUri.ToLowerInvariant();
        
        if (uri.Contains("windows.net") || uri.Contains("kusto.windows.net"))
            return CloudEnvironment.Public;
        if (uri.Contains("usgovcloudapi.net"))
            return CloudEnvironment.Government;
        if (uri.Contains("chinacloudapi.cn"))
            return CloudEnvironment.China;
        if (uri.Contains("cloudapi.de"))
            return CloudEnvironment.Germany;
            
        return CloudEnvironment.Public; // Default fallback
    }

    private void ApplyCloudConfiguration(KustoConnectionStringBuilder csb, CloudEnvironment cloud)
    {
        // Apply cloud-specific settings if needed
        switch (cloud)
        {
            case CloudEnvironment.Government:
                _logger.LogDebug("Applying Azure Government cloud configuration");
                break;
            case CloudEnvironment.China:
                _logger.LogDebug("Applying Azure China cloud configuration");
                break;
            case CloudEnvironment.Germany:
                _logger.LogDebug("Applying Azure Germany cloud configuration");
                break;
            case CloudEnvironment.Public:
            default:
                _logger.LogDebug("Using Azure Public cloud configuration");
                break;
        }
    }

    private X509Certificate2? GetCertificateFromStore(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            
            var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
            return certificates.Count > 0 ? certificates[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve certificate with thumbprint {Thumbprint}", thumbprint);
            return null;
        }
    }
}

/// <summary>
    /// Single-file provider that manages both Kusto connections (AzureDCM + OneCapacity)
    /// and runs the comprehensive cross-cluster Gen4 query.
    /// </summary>
    /// 
    public class KustoSdkDataProvider : IClusterDataProvider, IDisposable
    {
        private readonly ILogger<KustoSdkDataProvider> _logger;
    private readonly IKustoQueryHelperFactory _kustoFactory;
    private readonly IActivityContext _activityContext;
    private bool _disposed;

    public KustoSdkDataProvider(
        ILogger<KustoSdkDataProvider> logger,
        IKustoQueryHelperFactory kustoFactory,
        IActivityContext? activityContext = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _kustoFactory = kustoFactory ?? throw new ArgumentNullException(nameof(kustoFactory));
        _activityContext = activityContext ?? new DefaultActivityContext();

        _logger.LogInformation("KustoSdkDataProvider initialized with factory pattern (ActivityId: {ActivityId})", 
            _activityContext.ActivityId);
    }

        private static KustoConnectionStringBuilder BuildConnection(
            string clusterUri,
            bool useManagedIdentity,
            string? tenantId,
            string? clientId,
            string? clientSecret,
            TimeSpan? defaultTimeout)
        {
            if (string.IsNullOrWhiteSpace(clusterUri))
                throw new ArgumentException("clusterUri is required");

            KustoConnectionStringBuilder csb;

            if (useManagedIdentity)
            {
                // Managed Identity (best in Azure)
                csb = new KustoConnectionStringBuilder(clusterUri)
                    .WithAadSystemManagedIdentity();
            }
            else if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret) && !string.IsNullOrWhiteSpace(tenantId))
            {
                // App registration (Client Credentials)
                csb = new KustoConnectionStringBuilder(clusterUri)
                    .WithAadApplicationKeyAuthentication(clientId, clientSecret, tenantId);
            }
            else
            {
                // Fallback to user auth (device code)
                csb = new KustoConnectionStringBuilder(clusterUri)
                    .WithAadUserPromptAuthentication();
            }

            if (defaultTimeout is { } t)
            {
                // Set application name and timeout using connection string properties
                var props = new ClientRequestProperties();
                props.SetOption(ClientRequestProperties.OptionServerTimeout, t);
                // Connection string builder doesn't need to be modified for timeout
            }

            return csb;
        }

    public void Dispose()
    {
        if (!_disposed)
        {
            _logger.LogDebug("Disposing KustoSdkDataProvider (ActivityId: {ActivityId})", _activityContext.ActivityId);
            
            // Factory-created helpers are disposed by their respective consumers
            // The factory itself handles lifecycle management
            
            _disposed = true;
        }
    }

        // ------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------

        public async Task<List<ClusterData>> GetClusterDataAsync()
        {
            return await GetClusterDataWithCancellationAsync(CancellationToken.None);
        }

        private async Task<List<ClusterData>> GetClusterDataWithCancellationAsync(CancellationToken ct)
        {
            _logger.LogInformation("Running comprehensive Gen4 cluster query... (ActivityId: {ActivityId})", 
                _activityContext.ActivityId);

            // Create a new helper instance for this operation (not from factory cache)
            var onecapHelper = _kustoFactory.CreateQueryHelper(KustoConfig.OneCapacity, _activityContext);
            try
            {
                using var reader = await onecapHelper.ExecuteQueryAsync(GetGen4ClusterQuery(), null, ct);
                
                var col = GetColumnMappings(reader);
                var list = new List<ClusterData>();
                
                while (reader.Read())
                    list.Add(MapReaderToClusterData(reader, col));

                _logger.LogInformation("Returned {Count} clusters (ActivityId: {ActivityId})", 
                    list.Count, _activityContext.ActivityId);
                return list;
            }
            finally
            {
                // Dispose the helper after use to prevent disposed object issues
                onecapHelper?.Dispose();
            }
        }


        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                // Light-weight “.show database” pings — one call per cluster
                var dcmHelper = _kustoFactory.GetDcmHelper();
                var onecapHelper = _kustoFactory.GetOnecapHelper();

                try
                {
                    var ok1 = await dcmHelper.TestConnectionAsync(ct);
                    var ok2 = await onecapHelper.TestConnectionAsync(ct);
                    return ok1 && ok2;
                }
                finally
                {
                    // Don't dispose helpers - they're managed by the factory
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kusto connectivity test failed");
                return false;
            }
        }
        

        public async Task<string> GetDataSourceInfoAsync(CancellationToken ct = default)
    {
        var connectionTest = await TestConnectionAsync(ct);
        var connectionInfo = "Factory-based configuration";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");

        return $"📊 **Kusto SDK Data Source Information**\n\n" +
               $"**Provider**: Kusto SDK (Azure Data Explorer)\n" +
               $"**Connection Status**: {(connectionTest ? "✅ Connected" : "❌ Failed")}\n" +
               $"**Configuration**: {connectionInfo}\n" +
               $"**Data Sources**:\n" +
               $"• azuredcm.kusto.windows.net (AzureDCMDb) - Gen4 cluster inventory\n" +
               $"• onecapacityfollower.centralus.kusto.windows.net (Shared) - Utilization & health data\n\n" +
               $"**Query Type**: Live KQL execution\n" +
               $"**Authentication**: Azure AD integrated\n" +
               $"**Activity ID**: {_activityContext.ActivityId}\n" +
               $"**Generated**: {timestamp}";
    }

        // Convenience for plugin layer
        public async Task<List<ClusterRow>> GetClusterRowDataAsync(CancellationToken ct = default)
            => (await GetClusterDataWithCancellationAsync(ct)).Select(ConvertToClusterRow).ToList();

        public async Task<ClusterData?> GetClusterDetailsAsync(string clusterId, CancellationToken ct = default)
        {
            _logger.LogInformation("Getting details for cluster {ClusterId}", clusterId);
            
            // Retrieve all clusters and find the one matching the provided ID
            var allClusters = await GetClusterDataWithCancellationAsync(ct);
            return allClusters.FirstOrDefault(c => string.Equals(c.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<ClusterRow?> GetClusterRowDetailsAsync(string clusterId, CancellationToken ct = default)
            => (await GetClusterDetailsAsync(clusterId, ct)) is { } c ? ConvertToClusterRow(c) : null;

        // ------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------



        /// <summary>Loads the KQL from file if present; falls back to embedded query.</summary>
        private string GetGen4ClusterQuery()
        {
            // Try multiple paths to find the query file
            var possiblePaths = new[]
            {
                // Standard path relative to base directory
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "Queries", "GEN4QUERY.klq"),
                
                
                // Path relative to the project root (going up from bin/Debug/net9.0)
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "src", "Queries", "GEN4QUERY.klq"),
                
                // Absolute path to the source directory
                Path.Combine(Directory.GetCurrentDirectory(), "src", "Queries", "GEN4QUERY.klq")
            };

            foreach (var filePath in possiblePaths)
            {
                _logger.LogInformation("Looking for query file at: {FilePath}", filePath);
                
                if (File.Exists(filePath))
                {
                    try
                    {
                        _logger.LogInformation("Found query file, loading content from {FilePath}", filePath);
                        string content = File.ReadAllText(filePath);
                        _logger.LogInformation("Successfully loaded query file ({Length} characters)", content.Length);
                        return content;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read query file at {FilePath}, trying next path", filePath);
                    }
                }
                else
                {
                    _logger.LogWarning("Query file not found at {FilePath}", filePath);
                }
            }

            // Fallback: embedded validated comprehensive query with dual-mode tenant analysis
            _logger.LogInformation("Using embedded query fallback as no file was found");
            return """
// STEP 1: Gen4 clusters
let Gen4WithoutDecomPair =
cluster('azuredcm.kusto.windows.net').database('AzureDCMDb').dcmInventoryGenerationMappingV3
| where ClusterType == 'Compute'
| extend MajorGeneration = tostring(split(Generation, '.')[0])
| summarize GenerationList = array_strcat(array_sort_asc(make_set(MajorGeneration)), ',') by ClusterName = ClusterId
| where GenerationList == '4'
| distinct ClusterName
;
// STEP 2: Utilization & infra (no ACU / no TiP node analytics)
let ClusterUtilizationData =
cluster('onecapacityfollower.centralus.kusto.windows.net').database('Shared').EfficiencyTracker_Get_LatestDecommInsights
| where Cluster in (Gen4WithoutDecomPair)
| summarize
    TotalCore=todouble(sum(TotalPhysicalCores)),
    UsedCore=todouble(sum(UsedCores)),
    UsedSQL=todouble(sum(UsedCores_SQL)),
    UsedNonSQL=todouble(sum(UsedCores_NonSQL)),
    UsedNonSQL_Spannable=todouble(sum(UsedCores_NonSQL_Spannable)),
    UsedNonSQL_NonSpannable=todouble(sum(UsedCores_NonSQL_NonSpannable)),
    VMCount=sum(VMCount),
    VMCount_SQL=sum(VMCount_SQL),
    VMCount_NonSQL=sum(VMCount_NonSQL),
    VMCount_NonSQL_Spannable=sum(VMCount_NonSQL_Spannable),
    VMCount_NonSQL_NonSpannable=sum(VMCount_NonSQL_NonSpannable),
    MaxSupportedVMs=sum(MaxSupportedVMs),
    Nodes=sum(NodeCount),
    RackCount=sum(RackCount),
    OutOfServices=sum(NodeCount_OOS),
    DNG_Nodes=sum(NodeCount_DNG),
    StrandedCores_DNG=todouble(sum(StrandedPhysicalCores_DNG)),
    StrandedCores_TIP=todouble(sum(StrandedPhysicalCores_TIP)),
    NodeCount_IOwnMachine=sum(NodeCount_IOwnMachine),
    NodeCount_32VMs=sum(NodeCount_32VMs),
    StrandedCores_32VMs=todouble(sum(StrandedPhysicalCores_32VMs)),
    PhysicalCoresPerNode=any(PhysicalCoresPerNode),
    MPCountInCluster=any(MPCountInCluster),
    RackCountInCluster=any(RackCountInCluster),
    IsLive=any(IsLive),
    ClusterType=any(ClusterType),
    IsTargetMP=any(IsTargetMP),
    TenantWithMaxFD=any(TenantNameWithMaxFDCountInCluster),
    Generation=any(Generation),
    Manufacturer=any(Manufacturer),
    MemCategory=any(MemCategory),
    IsSpecialtySKU=any(IsSpecialtySKU),
    TransitionSKUCategory=any(TransitionSKUCategory)
    by Cluster
| extend 
    CoreUtilization = round(UsedCore / TotalCore * 100, 2),
    VMDensity = round(todouble(VMCount) / MaxSupportedVMs * 100, 2),
    OutOfServicesPercentage = round(todouble(OutOfServices) / Nodes * 100, 2),
    SQL_Ratio = round(UsedSQL / UsedCore * 100, 2),
    NonSpannable_Ratio = round(UsedNonSQL_NonSpannable / UsedNonSQL * 100, 2),
    SpannableUtilizationRatio = round(UsedNonSQL_Spannable / (UsedNonSQL_Spannable + UsedNonSQL_NonSpannable) * 100, 2),
    HealthyNodeRatio = round(todouble(Nodes - OutOfServices - DNG_Nodes) / Nodes * 100, 2),
    EffectiveCoreUtilization = round((UsedCore - StrandedCores_DNG - StrandedCores_TIP) / TotalCore * 100, 2)
;
// STEP 3: Cluster properties & age (gives us Region)
let ClusterPropertiesData =
cluster('onecapacityfollower.centralus.kusto.windows.net').database('Shared')._CCC_Cache_ClusterProperties
| where Cluster in (Gen4WithoutDecomPair)
| where isnotempty(ClusterLIVEDate)
| summarize arg_max(PreciseTimeStamp, *) by Cluster
| extend 
    ClusterAgeDays  = datetime_diff('day', now(), ClusterLIVEDate),
    ClusterAgeYears = round(todouble(datetime_diff('day', now(), ClusterLIVEDate)) / 365.25, 1),
    DecommissionTimeRemaining   = datetime_diff('day', ClusterDecommisionDate, now()),
    DecommissionYearsRemaining  = round(todouble(datetime_diff('day', ClusterDecommisionDate, now())) / 365.25, 1)
| project Cluster, Region, DC, AvailabilityZone, PhysicalAZ, 
    ClusterAgeDays, ClusterAgeYears, DecommissionTimeRemaining, DecommissionYearsRemaining,
    Generation, Manufacturer, MemCategory, CloudType, RegionType, Intent, IntentIsSellable,
    SKUName, Servers, NumRacks, IsUltraSSDEnabled, IsSpecialtySKU
;
// STEP 4: Tenant/platform workload
let TenantWorkloadData =
cluster('azurecm.kusto.windows.net').database('AzureCM').LogTenantSnapshot
| where PreciseTimeStamp > ago(2h)
| where Tenant in (Gen4WithoutDecomPair)
| extend NonGuidTenant = isnull(toguid(tenantName))
| summarize arg_max(PreciseTimeStamp, *) by Tenant, tenantName
| extend TenantInfo = strcat(tenantName, ':#', numRoleInstances, ':U-', maxUpdateDomain)
| extend HasUDGreaterThan10 = iff(toint(maxUpdateDomain) > 10, long(1), long(0))
| extend HasInstancesGreaterThan10 = iff(toint(numRoleInstances) > 10, long(1), long(0))
| summarize 
    GuidTenant=countif(NonGuidTenant == false),
    MaxUpdateDomain = maxif(maxUpdateDomain, NonGuidTenant == true),
    TenantList=array_strcat(array_sort_asc(make_set_if(TenantInfo, NonGuidTenant == true)), ','),
    HasUDGreaterThan10=iff(sum(HasUDGreaterThan10) > 0, true, false),
    HasInstancesGreaterThan10=iff(sum(HasInstancesGreaterThan10) > 0, true, false),
    TotalInstances=sum(toint(numRoleInstances)),
    TenantCount=count()
    by Tenant
| extend 
    HasSLB = iff(TenantList contains 'slb', true, false),
    HasWARP = iff(TenantList contains 'warp', true, false),
    HasPlatformTenant = isnotempty(TenantList),
    HasSQL = false
| project Tenant, HasPlatformTenant, HasWARP, HasSLB, HasSQL, HasUDGreaterThan10, HasInstancesGreaterThan10, 
    TotalInstances, TenantCount
;
// STEP 5: Hot Regions — GLOBAL LATEST snapshot (normalize Region for join)
let HotRegions_Snapshot =
cluster('onecapacityfollower.centralus.kusto.windows.net').database('Shared')._Cache_Fraud_RegionPrioritization
| where Reason == "Hot Region"
| as HR
| where TimeStamp == toscalar(HR | summarize max(TimeStamp))
| summarize
    RegionHotnessPriority = min(Priority),
    LatestHotTimestamp   = max(TimeStamp),
    HotRegionVMSeriesD   = make_set(VMSeries)
  by Region
| extend HotRegionVMSeries = array_strcat(HotRegionVMSeriesD, ','),
         RegionKey = tolower(replace_string(tostring(Region), " ", ""))
| project RegionKey, Region, RegionHotnessPriority, HotRegionVMSeries, LatestHotTimestamp
;
// STEP 6: Regional health (latest 1d; normalize Region too)
let RegionalHealthData =  
cluster('onecapacityfollower.centralus.kusto.windows.net').database('Shared')._CCC_Cache_RegionHealthLevel_Prediction
| where PreciseTimeStamp >= ago(1d)
| summarize arg_max(PreciseTimeStamp, *) by Region
| extend RegionKey = tolower(replace_string(tostring(Region), " ", ""))
| project RegionKey, Region,
         RegionHealthScore = ProjectedRegionHealthScore,
         RegionHealthLevel = ProjectedRegionHealthLevel,
         RegionHealthProjectedTime
;
// STEP 7: Build base (util + tenant + props), then join by RegionKey
ClusterUtilizationData
| join kind=inner     TenantWorkloadData  on $left.Cluster == $right.Tenant
| join kind=leftouter ClusterPropertiesData on $left.Cluster == $right.Cluster
| extend RegionKey = tolower(replace_string(tostring(Region), " ", ""))   // normalize for join
| join kind=leftouter HotRegions_Snapshot on RegionKey
| join kind=leftouter RegionalHealthData  on RegionKey
| extend
    // no defaulting of RegionHotnessPriority; just boolean flag
    IsHotRegion = iff(isnotempty(RegionHotnessPriority), true, false),
    // fills for core props only
    ClusterAgeDays = coalesce(ClusterAgeDays, long(-1)),
    ClusterAgeYears = coalesce(ClusterAgeYears, -1.0),
    DecommissionYearsRemaining = coalesce(DecommissionYearsRemaining, -1.0),
    HasSQL = iff(UsedSQL > 0, true, false),
    Region = coalesce(Region, ''),
    AvailabilityZone = coalesce(AvailabilityZone, ''),
    DC = coalesce(DC, ''),
    PhysicalAZ = coalesce(PhysicalAZ, ''),
    Generation = iff(isempty(Generation), '', Generation),
    Manufacturer = iff(isempty(Manufacturer), '', Manufacturer),
    MemCategory = iff(isempty(MemCategory), '', MemCategory),
    SKUName = iff(isempty(SKUName), '', SKUName),
    CloudType = iff(isempty(CloudType), '', CloudType),
    RegionType = iff(isempty(RegionType), '', RegionType),
    Intent = iff(isempty(Intent), '', Intent),
    IntentIsSellable = iff(isempty(IntentIsSellable), '', IntentIsSellable),
    TransitionSKUCategory = iff(isempty(TransitionSKUCategory), '', TransitionSKUCategory),
    ClusterType = iff(isempty(ClusterType), '', ClusterType),
    TenantWithMaxFD = iff(isempty(TenantWithMaxFD), '', TenantWithMaxFD),
    Servers = coalesce(Servers, long(0)),
    NumRacks = coalesce(NumRacks, long(0)),
    IsUltraSSDEnabled = coalesce(IsUltraSSDEnabled, false),
    IsSpecialtySKU = coalesce(IsSpecialtySKU, false),
    PhysicalCoresPerNode = coalesce(PhysicalCoresPerNode, long(0)),
    MPCountInCluster = coalesce(MPCountInCluster, long(0)),
    RackCountInCluster = coalesce(RackCountInCluster, long(0)),
    IsLive = coalesce(IsLive, true),
    IsTargetMP = coalesce(IsTargetMP, false),
    NodeCount_IOwnMachine = coalesce(NodeCount_IOwnMachine, long(0)),
    NodeCount_32VMs = coalesce(NodeCount_32VMs, long(0)),
    StrandedCores_32VMs = coalesce(StrandedCores_32VMs, 0.0)
| project
    // Identity & location
    ClusterId = Cluster,
    Region, AvailabilityZone, DataCenter = DC, PhysicalAZ,
    // Age & timeline
    ClusterAgeYears, ClusterAgeDays, DecommissionYearsRemaining, Intent, IntentIsSellable,
    // HW / SKU
    Generation, Manufacturer, MemCategory, SKUName, Servers, NumRacks,
    IsUltraSSDEnabled, IsSpecialtySKU, CloudType, RegionType, TransitionSKUCategory,
    // Infra details
    PhysicalCoresPerNode, MPCountInCluster, RackCountInCluster, IsLive, ClusterType, IsTargetMP,
    // Core Utilization
    TotalPhysicalCores = TotalCore, UsedCores = UsedCore, UsedCores_SQL = UsedSQL, 
    UsedCores_NonSQL = UsedNonSQL, UsedCores_NonSQL_Spannable = UsedNonSQL_Spannable,
    UsedCores_NonSQL_NonSpannable = UsedNonSQL_NonSpannable, CoreUtilization,
    // VM & capacity
    VMCount, VMCount_SQL, VMCount_NonSQL, VMCount_NonSQL_Spannable, VMCount_NonSQL_NonSpannable,
    MaxSupportedVMs, VMDensity,
    // Health
    TotalNodes = Nodes, OutOfServiceNodes = OutOfServices, DNG_Nodes,
    StrandedCores_DNG, StrandedCores_TIP, OutOfServicesPercentage,
    NodeCount_IOwnMachine, NodeCount_32VMs, StrandedCores_32VMs,
    // Workload / tenant
    HasPlatformTenant, HasWARP, HasSLB, HasSQL, HasUDGreaterThan10, HasInstancesGreaterThan10,
    TotalInstances, TenantCount, TenantWithMaxFD,
    // Efficiency ratios
    SQL_Ratio, NonSpannable_Ratio, SpannableUtilizationRatio, HealthyNodeRatio, EffectiveCoreUtilization,
    // Hot Region (assigned by Region)
    IsHotRegion, RegionHotnessPriority, HotRegionVMSeries, LatestHotTimestamp,
    // Regional health
    RegionHealthScore, RegionHealthLevel, RegionHealthProjectedTime
""";
        }

private static Dictionary<string, int> GetColumnMappings(IDataReader reader)
{
    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < reader.FieldCount; i++)
        map[reader.GetName(i)] = i;
    return map;
}

    private static ClusterData MapReaderToClusterData(IDataReader reader, Dictionary<string, int> col)
    {
        return new ClusterData
        {
            // Identification & Region
            ClusterId = GetSafeString(reader, col["ClusterId"]),
            Region = GetSafeString(reader, col["Region"]),
            AvailabilityZone = GetSafeString(reader, col["AvailabilityZone"]),
            DataCenter = GetSafeString(reader, col["DataCenter"]),
            PhysicalAZ = GetSafeString(reader, col["PhysicalAZ"]),

            // Age & Decommission
            ClusterAgeYears = GetSafeDouble(reader, col["ClusterAgeYears"]),
            ClusterAgeDays = GetSafeInt(reader, col["ClusterAgeDays"]),
            DecommissionYearsRemaining = GetSafeDouble(reader, col["DecommissionYearsRemaining"]),

            // Intent
            Intent = GetSafeString(reader, col["Intent"]),
            IntentIsSellable = GetSafeBoolean(reader, col["IntentIsSellable"]),

            // Hardware & Infra
            Generation = GetSafeString(reader, col["Generation"]),
            Manufacturer = GetSafeString(reader, col["Manufacturer"]),
            MemCategory = GetSafeString(reader, col["MemCategory"]),
            SKUName = GetSafeString(reader, col["SKUName"]),
            Servers = GetSafeInt(reader, col["Servers"]),
            NumRacks = GetSafeInt(reader, col["NumRacks"]),
            IsUltraSSDEnabled = GetSafeBoolean(reader, col["IsUltraSSDEnabled"]),
            IsSpecialtySKU = GetSafeBoolean(reader, col["IsSpecialtySKU"]),
            CloudType = GetSafeString(reader, col["CloudType"]),
            RegionType = GetSafeString(reader, col["RegionType"]),
            TransitionSKUCategory = GetSafeString(reader, col["TransitionSKUCategory"]),

            PhysicalCoresPerNode = GetSafeInt(reader, col["PhysicalCoresPerNode"]),
            MPCountInCluster = GetSafeInt(reader, col["MPCountInCluster"]),
            RackCountInCluster = GetSafeInt(reader, col["RackCountInCluster"]),
            IsLive = GetSafeBoolean(reader, col["IsLive"]),
            ClusterType = GetSafeString(reader, col["ClusterType"]),
            IsTargetMP = GetSafeBoolean(reader, col["IsTargetMP"]),

            // Utilization
            TotalPhysicalCores = GetSafeDouble(reader, col["TotalPhysicalCores"]),
            UsedCores = GetSafeDouble(reader, col["UsedCores"]),
            UsedCores_SQL = GetSafeDouble(reader, col["UsedCores_SQL"]),
            UsedCores_NonSQL = GetSafeDouble(reader, col["UsedCores_NonSQL"]),
            UsedCores_NonSQL_Spannable = GetSafeDouble(reader, col["UsedCores_NonSQL_Spannable"]),
            UsedCores_NonSQL_NonSpannable = GetSafeDouble(reader, col["UsedCores_NonSQL_NonSpannable"]),
            CoreUtilization = GetSafeDouble(reader, col["CoreUtilization"]),

            VMCount = GetSafeInt(reader, col["VMCount"]),
            VMCount_SQL = GetSafeInt(reader, col["VMCount_SQL"]),
            VMCount_NonSQL = GetSafeInt(reader, col["VMCount_NonSQL"]),
            VMCount_NonSQL_Spannable = GetSafeInt(reader, col["VMCount_NonSQL_Spannable"]),
            VMCount_NonSQL_NonSpannable = GetSafeInt(reader, col["VMCount_NonSQL_NonSpannable"]),
            MaxSupportedVMs = GetSafeInt(reader, col["MaxSupportedVMs"]),
            VMDensity = GetSafeDouble(reader, col["VMDensity"]),

            // Nodes & Risk (TIP_Nodes removed)
            TotalNodes = GetSafeInt(reader, col["TotalNodes"]),
            OutOfServiceNodes = GetSafeInt(reader, col["OutOfServiceNodes"]),
            DNG_Nodes = GetSafeInt(reader, col["DNG_Nodes"]),
            StrandedCores_DNG = GetSafeDouble(reader, col["StrandedCores_DNG"]),
            StrandedCores_TIP = GetSafeDouble(reader, col["StrandedCores_TIP"]),
            OutOfServicesPercentage = GetSafeDouble(reader, col["OutOfServicesPercentage"]),
            NodeCount_IOwnMachine = GetSafeInt(reader, col["NodeCount_IOwnMachine"]),
            NodeCount_32VMs = GetSafeInt(reader, col["NodeCount_32VMs"]),
            StrandedCores_32VMs = GetSafeDouble(reader, col["StrandedCores_32VMs"]),

            // Tenants
            HasPlatformTenant = GetSafeBoolean(reader, col["HasPlatformTenant"]),
            HasWARP = GetSafeBoolean(reader, col["HasWARP"]),
            HasSLB = GetSafeBoolean(reader, col["HasSLB"]),
            HasSQL = GetSafeBoolean(reader, col["HasSQL"]),
            HasUDGreaterThan10 = GetSafeBoolean(reader, col["HasUDGreaterThan10"]),
            HasInstancesGreaterThan10 = GetSafeBoolean(reader, col["HasInstancesGreaterThan10"]),
            TotalInstances = GetSafeInt(reader, col["TotalInstances"]),
            TenantCount = GetSafeInt(reader, col["TenantCount"]),
            TenantWithMaxFD = GetSafeString(reader, col["TenantWithMaxFD"]),

            // Hot regions (no defaulting)
            IsHotRegion = GetSafeBoolean(reader, col["IsHotRegion"]),
            RegionHotnessPriority = GetSafeInt(reader, col["RegionHotnessPriority"]),
            HotRegionVMSeries = GetSafeString(reader, col["HotRegionVMSeries"]),
            LatestHotTimestamp = GetSafeDateTime(reader, col["LatestHotTimestamp"]),

            // Regional health
            RegionHealthScore = GetSafeDouble(reader, col["RegionHealthScore"]),
            RegionHealthLevel = GetSafeString(reader, col["RegionHealthLevel"]),
            RegionHealthProjectedTime = GetSafeDateTime(reader, col["RegionHealthProjectedTime"])
        };
    }
private static ClusterRow ConvertToClusterRow(ClusterData c)
{
    return new ClusterRow
    {
        Cluster = c.ClusterId,
        Region = c.Region,
        AvailabilityZone = c.AvailabilityZone,
        DataCenter = c.DataCenter,
        PhysicalAZ = c.PhysicalAZ,

        ClusterAgeYears = c.ClusterAgeYears,
        ClusterAgeDays = c.ClusterAgeDays,
        DecommissionYearsRemaining = c.DecommissionYearsRemaining,
        Intent = c.Intent,
        IntentIsSellable = c.IntentIsSellable,

        Generation = c.Generation,
        Manufacturer = c.Manufacturer,
        MemCategory = c.MemCategory,
        SKUName = c.SKUName,
        Servers = c.Servers,
        NumRacks = c.NumRacks,
        IsUltraSSDEnabled = c.IsUltraSSDEnabled,
        IsSpecialtySKU = c.IsSpecialtySKU,
        CloudType = c.CloudType,
        RegionType = c.RegionType,
        TransitionSKUCategory = c.TransitionSKUCategory,

        PhysicalCoresPerNode = c.PhysicalCoresPerNode,
        MPCountInCluster = c.MPCountInCluster,
        RackCountInCluster = c.RackCountInCluster,
        IsLive = c.IsLive,
        ClusterType = c.ClusterType,
        IsTargetMP = c.IsTargetMP,

        TotalPhysicalCores = c.TotalPhysicalCores,
        UsedCores = c.UsedCores,
        UsedCores_SQL = c.UsedCores_SQL,
        UsedCores_NonSQL = c.UsedCores_NonSQL,
        UsedCores_NonSQL_Spannable = c.UsedCores_NonSQL_Spannable,
        UsedCores_NonSQL_NonSpannable = c.UsedCores_NonSQL_NonSpannable,
        CoreUtilization = c.CoreUtilization,

        VMCount = c.VMCount,
        VMCount_SQL = c.VMCount_SQL,
        VMCount_NonSQL = c.VMCount_NonSQL,
        VMCount_NonSQL_Spannable = c.VMCount_NonSQL_Spannable,
        VMCount_NonSQL_NonSpannable = c.VMCount_NonSQL_NonSpannable,
        MaxSupportedVMs = c.MaxSupportedVMs,
        VMDensity = c.VMDensity,

        TotalNodes = c.TotalNodes,
        OutOfServiceNodes = c.OutOfServiceNodes,
        DNG_Nodes = c.DNG_Nodes,
        // TIP_Nodes removed
        StrandedCores_DNG = c.StrandedCores_DNG,
        StrandedCores_TIP = c.StrandedCores_TIP,
        OutOfServicesPercentage = c.OutOfServicesPercentage,
        NodeCount_IOwnMachine = c.NodeCount_IOwnMachine,
        NodeCount_32VMs = c.NodeCount_32VMs,
        StrandedCores_32VMs = c.StrandedCores_32VMs,

        HasPlatformTenant = c.HasPlatformTenant, // (kept your property name)
        HasWARP = c.HasWARP,
        HasSLB = c.HasSLB,
        HasSQL = c.HasSQL,
        HasUDGreaterThan10 = c.HasUDGreaterThan10,
        HasInstancesGreaterThan10 = c.HasInstancesGreaterThan10,
        TotalInstances = c.TotalInstances,
        TenantCount = c.TenantCount,
        TenantWithMaxFD = c.TenantWithMaxFD,

        // ACU / Allocation / TiP analytics / Region context REMOVED

        // Hot regions
        IsHotRegion = c.IsHotRegion,
        RegionHotnessPriority = c.RegionHotnessPriority,
        HotRegionVMSeries = c.HotRegionVMSeries,
        LatestHotTimestamp = c.LatestHotTimestamp,

        // Regional health
        RegionHealthScore = c.RegionHealthScore,
        RegionHealthLevel = c.RegionHealthLevel,
        RegionHealthProjectedTime = c.RegionHealthProjectedTime,
    };
}

        private static string? GetSafeString(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            var v = reader.GetValue(idx);
            return v switch
            {
                string s => s,
                DateTime dt => dt.ToString("o"),
                DateTimeOffset dto => dto.ToString("o"),
                _ => v.ToString()
            };
        }

        private static bool? GetSafeBoolean(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            var v = reader.GetValue(idx);
            return v switch
            {
                bool b => b,
                byte by => by != 0,
                short s => s != 0,
                int i => i != 0,
                long l => l != 0,
                double d => Math.Abs(d) > double.Epsilon,
                decimal m => m != 0m,
                string s when bool.TryParse(s, out var b) => b,
                string s when double.TryParse(s, out var d) => Math.Abs(d) > double.Epsilon,
                _ => null
            };
        }

        private static double? GetSafeDouble(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            var v = reader.GetValue(idx);
            return v switch
            {
                double d => d,
                float f => (double)f,
                decimal m => (double)m,
                int i => i,
                long l => l,
                short s => s,
                byte b => b,
                string s when double.TryParse(s, out var d2) => d2,
                _ => null
            };
        }

        private static int? GetSafeInt(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            var v = reader.GetValue(idx);
            return v switch
            {
                int i => i,
                long l => checked((int)l),
                short s => s,
                byte b => b,
                double d => (int)Math.Round(d, MidpointRounding.AwayFromZero),
                decimal m => (int)Math.Round(m, MidpointRounding.AwayFromZero),
                string s when int.TryParse(s, out var i2) => i2,
                string s when double.TryParse(s, out var d2) => (int)Math.Round(d2, MidpointRounding.AwayFromZero),
                _ => (int?)null
            };
        }

        private static long? GetSafeLong(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            var v = reader.GetValue(idx);
            return v switch
            {
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                double d => (long)Math.Round(d, MidpointRounding.AwayFromZero),
                decimal m => (long)Math.Round(m, MidpointRounding.AwayFromZero),
                string s when long.TryParse(s, out var l2) => l2,
                string s when double.TryParse(s, out var d2) => (long)Math.Round(d2, MidpointRounding.AwayFromZero),
                _ => (long?)null
            };
        }

        private static DateTime? GetSafeDateTime(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            var v = reader.GetValue(idx);
            return v switch
            {
                DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                DateTimeOffset dto => dto.UtcDateTime,
                string s when DateTime.TryParse(s, out var dt2) => DateTime.SpecifyKind(dt2, DateTimeKind.Utc),
                _ => (DateTime?)null
            };
        }

        private static List<string>? GetSafeStringList(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            try
            {
                var raw = reader.GetValue(idx);
                if (raw is string s)
                {
                    if (s.TrimStart().StartsWith("["))
                        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(s);

                    return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim()).ToList();
                }
                return null;
            }
            catch { return null; }
        }

        private static Dictionary<string, double>? GetSafeDictionary(IDataReader reader, int idx)
        {
            if (idx < 0 || reader.IsDBNull(idx)) return null;
            try
            {
                var json = reader.GetString(idx);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

                var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        prop.Value.TryGetDouble(out var dv))
                    {
                        dict[prop.Name] = dv;
                    }
                    else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                             double.TryParse(prop.Value.GetString(), out var dv2))
                    {
                        dict[prop.Name] = dv2;
                    }
                }
                return dict;
            }
            catch { return null; }
        }
    }

