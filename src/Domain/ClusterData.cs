#nullable enable
using System;

namespace MyM365AgentDecommision.Bot.Models
{
    /// <summary>
    /// Cluster facts aggregated from AzureDCM + OneCapacity for decommission analysis.
    /// Types are nullable to preserve unknown/missing values from Kusto.
    /// </summary>
    public class ClusterData
    {
        // Identification & Region
        public string? ClusterId { get; set; }
        public string? Region { get; set; }
        public string? AvailabilityZone { get; set; }
        public string? DataCenter { get; set; }
        public string? PhysicalAZ { get; set; }

        // Age & Decommission
        public double? ClusterAgeYears { get; set; }
        public int?    ClusterAgeDays { get; set; }
        public double? DecommissionYearsRemaining { get; set; }

        // Intent
        public string? Intent { get; set; }
        public bool?   IntentIsSellable { get; set; }

        // Hardware & Infra
        public string? Generation { get; set; }
        public string? Manufacturer { get; set; }
        public string? MemCategory { get; set; }
        public string? SKUName { get; set; }
        public int?    Servers { get; set; }
        public int?    NumRacks { get; set; }
        public bool?   IsUltraSSDEnabled { get; set; }
        public bool?   IsSpecialtySKU { get; set; }
        public string? CloudType { get; set; }
        public string? RegionType { get; set; }
        public string? TransitionSKUCategory { get; set; }

        public int?    PhysicalCoresPerNode { get; set; }
        public int?    MPCountInCluster { get; set; }
        public int?    RackCountInCluster { get; set; }
        public bool?   IsLive { get; set; }
        public string? ClusterType { get; set; }
        public bool?   IsTargetMP { get; set; }

        // Core/VM Utilization
        public double? TotalPhysicalCores { get; set; }
        public double? UsedCores { get; set; }
        public double? UsedCores_SQL { get; set; }
        public double? UsedCores_NonSQL { get; set; }
        public double? UsedCores_NonSQL_Spannable { get; set; }
        public double? UsedCores_NonSQL_NonSpannable { get; set; }
        public double? CoreUtilization { get; set; }

        public int?    VMCount { get; set; }
        public int?    VMCount_SQL { get; set; }
        public int?    VMCount_NonSQL { get; set; }
        public int?    VMCount_NonSQL_Spannable { get; set; }
        public int?    VMCount_NonSQL_NonSpannable { get; set; }
        public int?    MaxSupportedVMs { get; set; }
        public double? VMDensity { get; set; }

        // Nodes & Risk
        public int?    TotalNodes { get; set; }
        public int?    OutOfServiceNodes { get; set; }
        public int?    DNG_Nodes { get; set; }
        public double? StrandedCores_DNG { get; set; }
        public double? StrandedCores_TIP { get; set; }
        public double? OutOfServicesPercentage { get; set; }
        public int?    NodeCount_IOwnMachine { get; set; }
        public int?    NodeCount_32VMs { get; set; }
        public double? StrandedCores_32VMs { get; set; }

        // Tenant / Platform
        public bool?   HasPlatformTenant { get; set; }
        public bool?   HasWARP { get; set; }
        public bool?   HasSLB { get; set; }
        public bool?   HasSQL { get; set; }
        public bool?   HasUDGreaterThan10 { get; set; }
        public bool?   HasInstancesGreaterThan10 { get; set; }
        public int?    TotalInstances { get; set; }
        public int?    TenantCount { get; set; }
        public string? TenantWithMaxFD { get; set; }

        // Hot regions (from global-latest snapshot)
        public bool?     IsHotRegion { get; set; }
        public int?      RegionHotnessPriority { get; set; }   // null when not hot
        public string?   HotRegionVMSeries { get; set; }
        public DateTime? LatestHotTimestamp { get; set; }

        // Regional health
        public double?   RegionHealthScore { get; set; }
        public string?   RegionHealthLevel { get; set; }
        public DateTime? RegionHealthProjectedTime { get; set; }
    }
}
