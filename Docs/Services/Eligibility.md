# Eligibility (Engine & Plugin) Documentation

## Architecture Overview

The eligibility stack has two pieces: a pure C# policy engine (**ClusterEligibilityEngine**) and a Semantic Kernel plugin (**EligibilityPlugin**) that exposes those rules to the DecommissionAgent and other callers. The engine is a configurable **gate**: it applies baseline checks (age, utilization, region allow/deny) plus optional generic constraints (string in/not-in, booleans, and numeric ranges) and returns both the **passing rows** and detailed **“why not” reasons** for failures. It’s testable, reflection-driven, and does not depend on the LLM.&#x20;

---

## ClusterEligibilityEngine (Service)

### Rules schema

`EligibilityRules` is a JSON-serializable record used to configure the policy. Defaults: `Enabled=true`, `MinAgeYears=6`, `MaxCoreUtilizationPercent=30`, with allow/deny region lists and optional generic constraints that let you extend checks without changing code.  &#x20;

Key fields:

* **Baseline toggles.** `EnforceAge`, `EnforceUtilization`, `EnforceAllowedRegions`, `EnforceExcludedRegions`.&#x20;
* **Thresholds.** `MinAgeYears` (years), `MaxCoreUtilizationPercent` (percent units).&#x20;
* **Region policy.** `AllowedRegions`, `ExcludedRegions` (case-insensitive sets).&#x20;
* **Generic constraints.** `StringIn`, `StringNotIn`, `BoolEquals`, `IntRanges`, `DoubleRanges`.&#x20;

### Evaluation logic

* **Gate off.** If `Enabled=false`, everything passes.&#x20;
* **Age.** Fails if age is missing or `< MinAgeYears`.&#x20;
* **Utilization.** Interpreted as percent; fails if missing or `> MaxCoreUtilizationPercent`.&#x20;
* **Region allow/deny.** Region is normalized; fails if not in allow-list (when enforced) or appears in deny-list.&#x20;
* **Generic checks.**

  * `StringIn`/`StringNotIn`: normalization per field (e.g., Region) and case-insensitive matching.&#x20;
  * `BoolEquals`: must match desired boolean.&#x20;

### Reflection cache & normalization

On first use, the engine reflects over `ClusterRow` to build getter maps for `string`, `bool?`, `int?`, and `double?` so checks are fast and field-name driven. Region has a built-in normalizer for consistent comparisons.  &#x20;

### Public API

* `FilterEligible(rows, rules, out ineligible)`: returns passing rows and collects failures with reasons.&#x20;
* `IsEligible(row, rules, out reasons)`: evaluates a single row; reasons list is empty if eligible.&#x20;

---

## EligibilityPlugin (SK Tool)

### Purpose & shape

The plugin is DI-backed with access to the data provider; it serializes results with web-style `System.Text.Json` options so it’s LLM-friendly. It returns **compact JSON** for summaries, lists of ineligible rows with reasons, and single-cluster explanations. &#x20;

### Typed DTOs

* `ApplySummary { Total, Eligible, Ineligible }`
* `IneligibleDto { Row, Reasons }`
* `ApplyResponse { Summary, Eligible[], Ineligible[], AppliedRules }`
* `ExplainResponse { ClusterId, Eligible, Reasons[], AppliedRules }`&#x20;

### Core operations

* **GetRulesTemplate()** — returns the default `EligibilityRules` JSON (good starting point).&#x20;
* **ValidateRules(rulesJson)** — parses and normalizes a rules JSON, or returns a structured error.&#x20;
* **ApplyToAllAsync(rulesJson?)** — loads all `ClusterRow` items from the provider, applies rules via the engine, and returns eligible, ineligible (with reasons), and a summary; cancellation and unhandled errors are mapped to error DTOs.&#x20;
* **ApplyToList(clustersJson, rulesJson?)** — same as above, but operates on a caller-supplied JSON list of `ClusterRow`.&#x20;
* **ExplainClusterAsync(clusterId, rulesJson?)** — evaluates a single cluster by ID, returning pass/fail and reasons; reports “not found” clearly.&#x20;

### Convenience builder

* **BuildRules(...)** — quickly craft a rules JSON from common parameters: `minAgeYears` (default 6), `maxCoreUtilizationPercent` (default 30), and include/exclude region CSVs, with enforcement toggles. &#x20;

### Data path

All plugin calls that need data use the provider’s **ClusterRow** endpoints (e.g., `GetClusterRowDataAsync`, `GetClusterRowDetailsAsync`), so eligibility always runs on the same, mapped view the rest of the system uses. &#x20;

### Error handling

`OperationCanceledException` is returned as `"Operation cancelled."`; malformed input (rules JSON, clusters JSON) is logged and returned as `ErrorDto(...)`, keeping tool outputs predictable for the agent. &#x20;

---

## Agent Integration

The **DecommissionAgent** prompt advertises the eligibility tool explicitly—`BuildRules` for fast rule creation and per-cluster checks/Batch apply—so the model can combine it with filtering and scoring based on user intent. &#x20;

---

## Quick examples

### 1) Default gate (age ≥ 6y, util ≤ 30%, allow/deny regions enforced)

```json
// Plugin: GetRulesTemplate()
{
  "Enabled": true,
  "EnforceAge": true,
  "EnforceUtilization": true,
  "EnforceAllowedRegions": true,
  "EnforceExcludedRegions": true,
  "MinAgeYears": 6,
  "MaxCoreUtilizationPercent": 30,
  "AllowedRegions": [],
  "ExcludedRegions": [],
  "StringIn": {}, "StringNotIn": {}, "BoolEquals": {},
  "IntRanges": {}, "DoubleRanges": {}
}
```

(Returned by `GetRulesTemplate()`.)&#x20;

### 2) Build a rules JSON on the fly

```json
// Plugin: BuildRules(minAgeYears=7, maxCoreUtilizationPercent=25, includeRegionsCsv="eastus,eastus2", excludeRegionsCsv="westus2")
{
  "Enabled": true,
  "EnforceAge": true,
  "EnforceUtilization": true,
  "EnforceAllowedRegions": true,
  "EnforceExcludedRegions": true,
  "MinAgeYears": 7,
  "MaxCoreUtilizationPercent": 25,
  "AllowedRegions": ["eastus","eastus2"],
  "ExcludedRegions": ["westus2"],
  "...": "generic constraints omitted"
}
```

(Built by `BuildRules(...)` with normalization and trimming.)&#x20;

### 3) Apply to all clusters

```text
EligibilityPlugin.ApplyToAllAsync(rulesJson)
 → {
     "Summary": { "Total": 245, "Eligible": 61, "Ineligible": 184 },
     "Eligible": [ ...ClusterRow... ],
     "Ineligible": [ { "Row": { ... }, "Reasons": [ "Age 3.9y < MinAgeYears 6y.", "Region 'westus2' is in ExcludedRegions." ] } ],
     "AppliedRules": { ...normalized... }
   }
```

(Provider → engine → structured JSON response.)&#x20;

### 4) Explain one cluster

```text
EligibilityPlugin.ExplainClusterAsync("CLUSTER123", rulesJson?)
 → { "ClusterId":"CLUSTER123", "Eligible":false, "Reasons":[ "CoreUtilization 41.2% > Max 30.0%." ], "AppliedRules":{...} }
```

(Uses provider’s `GetClusterRowDetailsAsync` + `IsEligible`.)&#x20;

---

## Notes & Best Practices

* Keep **percent** inputs in **percent units** (e.g., utilization 0–100) to match rule semantics.&#x20;
* Prefer **BuildRules** for common flows; use **ValidateRules** to harden LLM-constructed JSON before batch operations.&#x20;
* For hybrid flows (filter → eligibility → score), the agent orchestration already knows when to call each plugin based on user intent.&#x20;
