# ClusterFiltering & FilterService Documentation

## Architecture Overview

The filtering stack has two layers: a pure, in-memory engine (`ClusterFilterEngine`) that understands how to filter, sort, and page `ClusterRow` records; and a Semantic Kernel plugin (`ClusterFilteringPlugin`) that exposes those capabilities to the DecommissionAgent (and the model), plus a few convenience endpoints for discovery and “filter → score” workflows. The plugin pulls fresh data via the data provider and returns compact JSON payloads. It also knows about scoring so it can hand off to `ScoringService` when you want “filter + rank” in one call. &#x20;

## Filter engine (ClusterFilterEngine)

The engine is a single static class that indexes all `ClusterRow` properties once (string?, bool?, int?, double?) into getter maps for fast lookups at runtime. It also allows optional, per-field normalization for string comparisons—today only `Region` is normalized—so human inputs like “West US” match the canonical value used in data. Null numeric values are treated as failing range checks by design. &#x20;

### Criteria model

Filtering is expressed as a composable `Criteria` record:

* String sets: `StringIn`, `StringNotIn`, plus substring `StringContainsAny`.
* Booleans: `BoolEquals`.
* Numeric ranges: `IntRanges`, `DoubleRanges`.
* Optional `SortBy`/`SortDescending` and `Skip`/`Take` for ordering and paging.&#x20;

### Execution pipeline

`Apply(source, criteria)` starts with the input set and folds each criteria family in order:

* string IN / NOT IN with normalization (e.g., region) and case-insensitive matching;
* substring contains-any;
* boolean equals;
* int and double ranges (nulls fail);
* then a type-aware `Sort` and optional paging.  &#x20;

You can also enumerate every filterable field the engine knows via `ListSupportedFields()`, which returns `(Field, Kind)` pairs derived from the reflection cache.&#x20;

### Multi-criteria plans

For advanced scenarios, `MultiQueryPlan` lets you apply multiple `Criteria` blocks and combine them with `Union` or `Intersect`, followed by a global sort/page. The fold uses a stable equality on `Cluster` id so set ops behave as expected.  &#x20;

## Plugin (ClusterFilteringPlugin)

The plugin is DI-backed and constructed with the data provider, scoring service, and an optional logger. It uses a consistent JSON serializer profile suitable for LLM tool calls (case-insensitive, ignore nulls). &#x20;

### Core operations

* **Filter by one criteria** — `FilterByCriteriaJsonAsync(criteriaJson)`
  Parses a `ClusterFilterEngine.Criteria` from JSON, fetches `ClusterRow` data, applies the filter, and returns `{ total, returned, items }`. Invalid JSON yields an `ErrorDto`.&#x20;

* **Plan across many criteria** — `FilterByMultiPlanJsonAsync(planJson)`
  Accepts a `MultiQueryPlan` JSON (with `Mode`, `Items`, and optional global sort/page), applies `ApplyMany`, and returns `{ mode, total, returned, items }`. Empty `Items` is rejected with a clear error.&#x20;

* **Discovery helpers**
  `ListFilterableFields()` reflects the engine’s supported fields; `CriteriaTemplate()` emits a minimal, working criteria (e.g., sort by age, take 10) to guide tool-use. &#x20;

### “Filter + Score” convenience

* **End-to-end** — `FilterAndScoreAsync(filterCriteria?, weightConfig?, topN=10)`

  1. Loads all rows; 2) optionally applies `Criteria`; 3) optionally parses custom weights; 4) calls `ScoreAllAsync`; 5) keeps only scored clusters that survived filtering; 6) trims to `topN`. The weights are rebalanced and validated by `ScoringService`. Errors (bad criteria/weights, cancellations) are surfaced as structured JSON.    &#x20;

* **Weighting utilities & catalog**
  `GetDefaultWeights()` returns the system profile; `CreateCustomWeights("factor=weight,…")` builds and normalizes a config; `GetScorableFactors()` lists every scorable feature with units and whether “higher is better,” plus human-readable descriptions.   &#x20;

### Data path

All plugin calls that need data fetch `ClusterRow` via the provider’s convenience method, which runs the KQL, maps to `ClusterData`, and converts to `ClusterRow` structs used by the engine.&#x20;

### Error handling

Each entry point catches `OperationCanceledException` explicitly and returns an `ErrorDto("Operation cancelled.")`. Malformed JSON (criteria/plan/weights) is logged and reported back in a structured error, keeping the tool channel predictable for the agent.  &#x20;

---

### Quick examples

* **Find older, low-util clusters (sorted by age):**

```json
{
  "StringIn": { "Region": ["eastus","eastus2"] },
  "DoubleRanges": { "EffectiveCoreUtilization": { "Max": 30 } },
  "IntRanges": { "VMCount": { "Min": 10 } },
  "SortBy": "ClusterAgeYears",
  "SortDescending": true,
  "Take": 10
}
```

Feed this JSON to `FilterByCriteriaJsonAsync`. The engine will normalize region strings, apply the range cutoffs (nulls fail), and page the result.  &#x20;

* **Intersect two groups (very old) ∩ (non-SQL):**

```json
{
  "Mode": "Intersect",
  "Items": [
    { "IntRanges": { "ClusterAgeYears": { "Min": 7 } } },
    { "BoolEquals": { "HasSQL": false } }
  ],
  "SortBy": "EffectiveCoreUtilization",
  "SortDescending": false,
  "Take": 20
}
```

Send to `FilterByMultiPlanJsonAsync`; the plan will fold each `Criteria` and intersect by `Cluster` id, then sort and page globally. &#x20;

* **Filter and score in one go:**
  Call `FilterAndScoreAsync(filterCriteria, weightConfig, 15)`—the plugin will apply the criteria, compute scores with your weights (or defaults), and return the top 15 among the survivors, along with counts and whether custom weights were applied. &#x20;
