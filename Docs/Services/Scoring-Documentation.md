
# Scoring Documentation

The Cluster Decommissioning Agent's scoring system provides a sophisticated, configurable, and explainable approach to identifying and prioritizing clusters for decommissioning. This document details the architecture, features, and usage patterns of the scoring system.

## Overview

The scoring system evaluates Azure clusters against multiple factors including age, utilization, health, regional context, and workload constraints. It uses a weighted model that:

- Normalizes diverse metrics to comparable scales
- Supports customizable weighting profiles
- Provides detailed factor-by-factor explanations
- Handles missing data gracefully
- Adjusts for factors where "lower is better"
- Employs optional statistical techniques like winsorization

## Architecture Overview

```mermaid
flowchart LR
  A["Callers<br/><sub>Agent / Services</sub>"] -.-> P["ScoringPlugin"]
  A --> S["ScoringService"]

  %% Inputs & setup
  subgraph Inp["Inputs & Setup"]
    direction LR
    S --> WQ{Resolve Weights}
    WQ -->|Yes| W1["Parse user weights (JSON)"]
    WQ -->|No| W0["GetDefaultWeights()"]
    W1 --> WB["Rebalance → Σw = 1.0"]
    W0 --> WB
    WB --> OPT["Options<br/><sub>winsorization, includeAllNumericBudget,…</sub>"]
    WB --> CAT["FeatureCatalog<br/><sub>name, unit, direction</sub>"]
  end

  %% Data fetch
  subgraph Data["Data Fetch"]
    direction LR
    S --> D["IClusterDataProvider<br/><sub>GetClusterRowDataAsync()</sub>"]
    D --> R["ClusterRow[]"]
  end

  %% Weight prep
  subgraph Prep["Weight Preparation"]
    direction LR
    R --> BUDG{IncludeAllNumericBudget?}
    BUDG -->|Yes| B1["Spread small budget across<br/>unweighted numeric/derived fields"]
    BUDG -->|No| B0["No spread"]
    B1 --> EW["Effective Weights"]
    B0 --> EW
  end

  %% Factor stats
  subgraph Stats["Per-Factor Statistics"]
    direction LR
    R --> S1["Compute min/max per factor<br/><sub>optional winsorization</sub>"]
    S1 --> S2["FactorStats Map"]
  end

  %% Scoring loop
  subgraph Loop["Scoring Loop (per ClusterRow)"]
    direction LR
    S --> L{{For each row}}
    L --> N1["Normalize value<br/><sub>percent→[0..1], min–max</sub>"]
    N1 --> N2["Apply direction<br/><sub>flip if lower-is-better</sub>"]
    N2 --> N3["Contribution = weight × normalized"]
    N3 --> N4["Accumulate total score"]
    N4 --> BR["Build ScoreRowBreakdown<br/><sub>raw, norm, weight, contrib</sub>"]
  end

  %% Outputs
  subgraph Out["Outputs"]
    direction LR
    BR --> AGG["Aggregate all breakdowns"]
    AGG --> SORT["Sort by score (desc)"]
    SORT --> TOP["Ranked results"]
    E4 --> EXO["Single-item breakdown"]
  end

  %% Explain path
  subgraph Explain["ExplainAsync Path"]
    direction LR
    P -.->|ExplainScoreAsync| E1["ExplainAsync(clusterId)"]
    E1 --> E2["Find target row"]
    E2 --> E3["Reuse FactorStats & Weights"]
    E3 --> E4["Recompute breakdown"]
  end

  %% Plugin overlay
  P -.->|ScoreTopNAsync| S
  TOP -->|Top-N slice| P
  EXO --> P

  %% Styles
  style S fill:#eef7ff,stroke:#6aa3ff
  style Inp fill:#f7faff,stroke:#a3c4ff
  style Data fill:#f8fff7,stroke:#85d68c
  style Prep fill:#fff9f2,stroke:#f4b26b
  style Stats fill:#f0f7ff,stroke:#7fb3ff
  style Loop fill:#f7f3ff,stroke:#b49cff
  style Explain fill:#fff7fb,stroke:#f07cc3
  style Out fill:#ffffff,stroke:#bbb,stroke-dasharray: 5 3
  style P fill:#f6fffb,stroke:#76d7c4
```

## Core Components

The scoring system is architected as a two-tier solution designed for both programmatic access and AI-driven interactions:

### ScoringService: The Analytical Engine

At the heart of the system lies the **ScoringService**, a sophisticated analytical engine that implements an explainable, mathematically rigorous scoring model. This service orchestrates multiple advanced capabilities:

**Intelligent Data Processing**
- **Multi-Factor Analysis**: Evaluates clusters across diverse dimensions including operational metrics, business constraints, and regional contexts
- **Adaptive Normalization**: Employs statistical techniques to ensure fair comparison across heterogeneous metrics:
  - Automatically detects and converts percentage-based inputs to a standardized 0-1 range
  - Applies configurable winsorization to handle outliers without distorting the overall distribution
  - Implements min-max normalization post-winsorization for consistent scaling
- **Directional Intelligence**: Maintains a sophisticated understanding of metric semantics, automatically inverting scores for "lower-is-better" factors (e.g., high utilization or sticky workloads indicate poor decommissioning candidates)
- **Graceful Degradation**: Handles missing or incomplete data elegantly by assigning neutral scores (0.5), ensuring partial data doesn't unfairly penalize or favor clusters

**Feature Discovery and Management**
- **Self-Documenting Feature Catalog**: Exposes a comprehensive catalog that enables dynamic feature discovery, including:
  - Feature metadata (name, data type, unit of measurement)
  - Directional indicators (whether higher values are favorable)
  - Statistical properties and valid ranges
- **Flexible Weight Profiles**: Ships with an expertly-tuned default weight configuration that balances:
  - Temporal factors (cluster age and lifecycle stage)
  - Utilization metrics (resource consumption patterns)
  - Health indicators (operational stability)
  - Business constraints (stranding risk, regional dependencies)
  - Workload characteristics (stickiness, migration complexity)
- **Dynamic Rebalancing**: Automatically normalizes weight distributions to ensure mathematical consistency (Σw = 1.0)

**Advanced Scoring Pipeline**
The service executes a sophisticated multi-stage pipeline:
1. **Data Acquisition**: Retrieves comprehensive cluster data via the injected `IClusterDataProvider`
2. **Weight Distribution**: Optionally distributes a configurable "exploration budget" across unweighted factors, ensuring no potentially valuable signal is completely ignored
3. **Statistical Analysis**: Computes factor-specific statistics (min, max, distribution) with optional outlier handling
4. **Score Computation**: For each cluster:
   - Normalizes raw values using computed statistics
   - Applies directional adjustments based on factor semantics
   - Calculates weighted contributions
   - Aggregates into a final interpretable score
5. **Explainability Generation**: Produces detailed breakdowns showing:
   - Raw factor values
   - Normalized scores
   - Applied weights
   - Individual contributions to the final score

### ScoringPlugin: The AI Integration Layer

The **ScoringPlugin** serves as an intelligent bridge between the analytical engine and AI agents, implementing the Semantic Kernel plugin pattern:

**Natural Language Interface**
- **Intuitive Method Exposure**: Presents scoring capabilities through AI-friendly methods:
  - `ScoreTopNAsync`: Returns the highest-scoring clusters with customizable result count
  - `ExplainScoreAsync`: Provides detailed scoring breakdown for individual clusters
- **Flexible Parameter Handling**: Accepts both structured parameters and natural language descriptions, with intelligent parsing and validation

**Adaptive Result Formatting**
- **Context-Aware Serialization**: Dynamically adjusts output format based on the consumption context:
  - Compact summaries for conversational responses
  - Detailed breakdowns for analytical deep-dives
  - Adaptive Card-ready structures for rich UI rendering
- **Top Factor Highlighting**: Automatically identifies and emphasizes the most significant contributing factors, making results immediately actionable

**Robust Error Handling**
- **Graceful Fallbacks**: Seamlessly reverts to default configurations when custom parameters are invalid or incomplete
- **Informative Error Messages**: Provides clear, actionable feedback for configuration issues
- **Defensive Programming**: Validates all inputs and handles edge cases to ensure consistent operation

### Dependency Injection and Integration

The entire scoring system is elegantly integrated into the application through modern dependency injection patterns:

```csharp
// Core service registration
builder.Services.AddSingleton<ScoringService>();
builder.Services.AddTransient<ScoringPlugin>();

// Agent integration
kernel.Plugins.AddFromType<ScoringPlugin>();
```

This architecture ensures:
- **Testability**: All components can be easily mocked and tested in isolation
- **Extensibility**: New scoring factors or algorithms can be added without disrupting existing functionality
- **Performance**: Singleton service pattern ensures efficient resource utilization
- **Flexibility**: Plugin-based architecture allows for easy integration with different AI models and frameworks

The scoring system represents a perfect balance between mathematical rigor and practical usability, providing operations teams with powerful, explainable insights while remaining accessible through natural language interactions.