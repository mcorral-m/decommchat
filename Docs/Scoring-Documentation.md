
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



The scoring layer consists of a  **ScoringService** (the explainable, math-y core) and **ScoringPlugin** (the SK-facing wrapper). ScoringService implements a weight-based model over `ClusterRow` fields with clear explainability: it supports raw and derived factors, coerces percent-like inputs to 0..1, applies optional winsorization before min–max normalization, inverts factors where “lower is better,” and treats missing values neutrally (0.5)【turn5file5L15-L21】【turn5file10L18-L29】. It exposes a feature catalog for discovery (name, kind, unit, higher-is-better)【turn5file2L34-L47】, a default weight profile centered on age, utilization, health, stranding, region context, and stickiness (with normalization via `Rebalance`)【turn5file11L25-L35】【turn5file11L60-L70】, and a direction map that flips contributions for “bad-when-high” signals like hot regions or sticky workloads【turn5file11L5-L13】【turn5file11L19-L23】. At runtime, `ScoreAllAsync` pulls all rows from the data provider, optionally spreads a small “IncludeAllNumericBudget” across unweighted numeric/derived fields, computes per-factor min/max (winsorized if configured), then builds ranked `ScoreRowBreakdown` records with per-factor raw value, normalized value, weight, and contribution; `ExplainAsync` returns the same breakdown for a single cluster【turn5file0L31-L43】【turn5file0L50-L58】【turn5file1L50-L58】. The **ScoringPlugin** is what the agent calls: its prompt advertises `ScoreTopNAsync` and `ExplainScoreAsync`, letting the model pass custom weights and (optionally) pre-filter criteria; results come back as top-N with factor highlights so they’re easy to render in text or Adaptive Cards【turn5file12L21-L35】. Concretely, the plugin deserializes an optional weights JSON, invokes `ScoreAllAsync`, takes the top N, and serializes a compact object containing cluster id, score, and the top contributing factors—falling back to defaults if weights aren’t supplied【turn5file8L3-L11】【turn5file8L18-L25】【turn5file8L26-L49】. Everything is wired up via DI: the app registers a singleton `ScoringService`, and the agent adds the ScoringPlugin as a tool so the LLM can call it directly during a conversation【turn5file7L24-L25】【turn5file14L57-L61】.
