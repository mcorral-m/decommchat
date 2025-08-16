# Decommissioning Query Catalog (by Intent)

## 1) Quick picks & rankings

* “Show me the top 10 decommission candidates.”
* “Rank all clusters by decommission score (high → low).”
* “Top 18 candidates, exclude west regions.”
* “Top 20 candidates with utilization weight = 100.”
* “Which clusters currently have the highest decommission score?”
* “Show top n across each region (n=5).”
* “Give me top 10 Gen4 compute clusters by score.”
* “Top 15 with stranded cores > 100.”
* “Top 25 in Public cloud only (exclude Gov/China).”

## 2) Filtering (single & compound)

* “Filter clusters in westus2 with low utilization (<15%).”
* “Clusters older than 5 years with utilization < 10%.”
* “Exclude eastus2 and brazilsouth; show BN4 DC only.”
* “Show IsLive = true, ClusterType = Compute, Generation = 4.”
* “Only AvailabilityZone = true and State = Active.”
* “Include clusters with special workloads: SLB or WARP.”
* “Region hotness ≥ 3 and OutOfServiceNodes > 5.”
* “Health incidents last 30 days > 0, utilization < 25%.”
* “StrandedCores ≥ 50, RegionHealthScore ≤ 0.4.”
* “Tenants contains ‘slb’ AND age ≥ 7y; OR utilization ≤ 8% (union).”
* “Use data as of 2025-07-31 (snapshot), not latest.”

## 3) Eligibility checks (pass/fail + reasons)

* “Check eligibility of cluster ABC123.”
* “Eligibility for XYZ with age threshold 6 years and util ≤ 30%.”
* “List ineligible clusters and why they failed.”
* “Which clusters become eligible in the next 90 days?”
* “Show eligibilities using custom rule: age ≥ 8y OR util ≤ 12%.”
* “Re-evaluate eligibility ignoring special workloads.”
* “Eligibility by region summarized (counts pass/fail).”

## 4) Scoring (compute, tune, persist)

* “Score all clusters with default weights.”
* “Re-score with age=0.8, utilization=0.3 (others default).”
* “Adjust scoring weights to prioritize health over age.”
* “Custom weights: age=4, util=0.15, health=0.2, strandedCores=0.1; top 10.”
* “Persist these weights for the session.”
* “Reset weights to system defaults.”
* “What metrics are used for scoring?”
* “What weights can I adjust?”
* “How does our scoring system work (formula + normalization)?”

## 5) Comparison (pairwise & multi)

* “Compare decommissioning factors between XYZ and ABC.”
* “Compare XYZ, ABC, QRS side-by-side (age, util, health, stranded).”
* “Which of XYZ vs ABC is the better candidate and why?”
* “Show contribution breakdown (weight × normalized feature) per cluster.”

## 6) Explainability & “why”

* “Help me understand why cluster XYZ has a high decommission score.”
* “Explain the top 3 drivers of ABC’s score.”
* “Show sensitivity: if XYZ utilization improves to 25%, what’s the new score?”
* “What single change would most reduce ABC’s score?”
* “Why is QRS not a good candidate?”

## 7) Data inspection & discovery

* “Return the raw row for cluster X.”
* “Show me what I can filter from (fields & allowed values).”
* “List schema/columns and short definitions.”
* “Show regions/DCs with cluster counts.”
* “Give me summary stats (mean/median) for age/utilization by region.”
* “List special workload flags present (SLB, WARP, …).”

## 8) Output controls (formatting & export)

* “Give the top 10 as a table with columns: Cluster, Region, Age, Util, Score.”
* “Return JSON only (no narration).”
* “Export results to CSV.”
* “Render an Adaptive Card with top 10.”
* “Paginate results (page 2, page size 25).”
* “Group by region, show top 3 per group.”

## 9) Time windows, trends, snapshots

* “Use last 7 days utilization instead of 30-day rolling.”
* “Trend score for XYZ over the last 6 months.”
* “Re-compute using snapshot 2025-07-01.”
* “Show clusters whose score increased ≥ 0.15 since last month.”

## 10) Constraints & guardrails

* “Exclude clusters with SpecialWorkloads = true.”
* “Only Government cloud.”
* “Only PhysicalAZ = SN4.”
* “Only RegionHealthStatus = Red or Amber.”

## 11) What-if / simulations

* “If we decommission top 10, how many stranded cores are reclaimed?”
* “If age weight = 0 and health weight = 1.0, who are the top 10?”
* “If we cap utilization at 20%, how do scores change for XYZ?”
* “Simulate eligibility threshold from 6y → 7y; who drops out?”

## 12) Governance, audit, reproducibility

* “Show the scoring config used for the last run (weights, normalization).”
* “Show query parameters and timestamp for this result.”
* “Log an explain report for the top 10 (JSON).”
* “Who changed the default weights last, and when?”

## 13) Natural variants you should map to the same intents

* “best candidates / top candidates / rank the best / highest priority”
* “older than / age ≥ / commissioned before / live date before”
* “low utilization / underutilized / spare capacity / idle cores”
* “exclude / without / not in”
* “data center / DC / physical AZ”
* “score / rank / priority / risk score (decom)”
* “eligible / qualifies / passes decom rules”

## 14) Multi-query patterns (union / intersect)

### A) Pipeline chains (do X then Y then Z)

* “Use snapshot 2025-07-31, filter Gen4 Compute in westus2 & eastus2, age≥6y util<15%, then check eligibility with age threshold=7y, then score with health=1.0 age=0.6 util=0.2, then return top 20 as a table.”
* “Filter BN4 DC only, exclude special workloads, then rank by score, then explain top 5 (factor breakdown) and export CSV.”
* “Eligibility first with age≥8y OR util≤12%, then intersect with clusters having strandedCores≥100, then score using age=0.7 stranded=0.4 util=0.2, then group by region and top 3 per region.”
* “Get latest data, filter low-util (<10%) & in service, score with defaults, simulate util=25% for the top 10, show new scores vs old and delta.”
* “Filter Gov cloud only → eligibility (age≥6y) → score (age=0.9, health=0.9, util=0.1) → compare XYZ vs ABC with contribution chart → save weights for session.”

### B) Set algebra combos (union / intersect / difference)

* “Return (age≥7y AND util<15%) INTERSECT (RegionHealthStatus in \[Red, Amber]) then EXCEPT (special workloads) then score and top 15.”
* “UNION of (clusters with strandedCores>200) and (clusters with OutOfServiceNodes>10), then INTERSECT with eligible(age≥6y), then rank and explain top 10.”
* “Show DIFF between (eligible today) and (eligible on 2025-06-30) → list newly eligible then score those only and export JSON.”

### C) Per-group tops & partitions

* “Filter Gen4 Compute, exclude west\* regions, then top 5 per region by score with weights: age=0.8 health=0.6 util=0.2; include columns Cluster,Region,Age,Util,Score.”
* “Group by DC (BN4, SN4, CQ2), within each top 3 with util<12% and age≥7y, then eligibility report (pass/fail counts) per DC.”
* “For each cloud (Public, Gov, China), top 10 candidates and export 3 CSVs.”

### D) Weight overrides + rules in one go

* “Apply custom weights (age=0.6, util=0.1, health=1.0, stranded=0.3), normalize by z-score, then eligibility age=7y util≤20%, then rank top 12 and render Adaptive Card.”
* “Run three weight sets: (W1: health-heavy), (W2: age-heavy), (W3: balanced). For westus2, show top 5 under each scheme side-by-side with contribution breakdowns.”

### E) Time windows, snapshots, & trends

* “Use utilization=last14d window but eligibility age from asOf=2025-07-01; score with defaults; compare to last30d scoring; show clusters whose score changed ≥0.2.”
* “For XYZ, show 6-month score trend, then simulate if age weight=0 starting next month; forecast score under that change.”

### F) What-if & sensitivity (bulk)

* “Take top 10 from defaults, then what-if: set util=25% cap on all, recompute scores; show before/after table with rank shifts.”
* “For clusters in BN4, reduce OutOfServiceNodes by 50% hypothetically, recompute, then re-rank and diff vs baseline.”

### G) Multi-entity compare + explain

* “Compare XYZ, ABC, QRS; for each: eligibility (age≥6y) → score (health=0.9 age=0.5 util=0.2) → explain top 3 drivers; then highlight the best candidate and why.”
* “Pairwise XYZ vs ABC under two weight configs; show tornado sensitivity for each feature.”

### H) Mixed output controls (tables, JSON, paging, export)

* “Filter eastus2, age≥7y util<12%, score, then page 2 (pageSize=25) as JSON only; also export CSV of the first 50.”
* “Top 20 with columns (Cluster, DC, Region, AgeYears, UtilPct, Score, Eligible) and sort by (Score desc, then UtilPct asc).”

### I) Governance, audit, and persistence

* “Run eligibility (age≥7y) and score (health-first weights), then log config & timestamp, persist weights for session, and attach explain JSON for top 10.”
* “Re-use the last saved weights, re-rank Public cloud, and show audit trail of weight changes past 7 days.”

### J) Multi-segment side-by-side

* “Build two cohorts: C1=westus2 older≥8y util<10%, C2=eastus2 older≥8y util<10%; score both with same weights; show side-by-side top 10 and median metrics per cohort.”
* “Compare BN4 vs SN4 DCs: eligibility → score → top 5 each → export two CSVs.”

### K) Advanced filters + set ops + what-if in one line

* “(RegionHotness≥3 OR OutOfServiceNodes>5) AND (Gen=4 & IsLive=true), EXCEPT (Tenants contains ‘slb’), then eligibility age≥6y, then score with age=0.7 stranded=0.3 health=0.6, then what-if: set util=20% for any with util>40%, recompute and show delta, top 15 final.”

### L) Multi-query with per-intent overrides

* “Use asOf=2025-08-01 for eligibility, but latest for util; exclude brazilsouth, only DC=SN4; score (age=0.8 health=0.7 util=0.2), group by region, top 3 per region, Adaptive Card + CSV.”
