using MyM365AgentDecommision.Bot.Models;

namespace MyM365AgentDecommision.Bot.Interfaces;

/// <summary>
/// Interface for cluster data operations
/// </summary>
public interface IClusterDataProvider
{
    /// <summary>
    /// Gets all cluster data
    /// </summary>
    Task<List<ClusterData>> GetClusterDataAsync();
    
    /// <summary>
    /// Gets detailed information for a specific cluster
    /// </summary>
    /// <param name="clusterId">The cluster identifier</param>
    /// <returns>Detailed cluster information or null if not found</returns>
    Task<ClusterData?> GetClusterDetailsAsync(string clusterId, CancellationToken ct = default);

    /// <summary>
    /// Tests the connection to the data source
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets information about the data source
    /// </summary>
    Task<string> GetDataSourceInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets cluster data as ClusterRow objects
    /// </summary>
    Task<List<ClusterRow>> GetClusterRowDataAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets details for a specific cluster as a ClusterRow object
    /// </summary>
    Task<ClusterRow?> GetClusterRowDetailsAsync(string clusterId, CancellationToken ct = default);
}
