# Exhibit #5 — KQL query pack

The Application Map and the Failures/Performance blades are the pretty front door.
The Logs blade — raw KQL over every request, dependency, trace, custom metric and
custom event — is where Application Insights pulls ahead for anyone who wants to ask
their own questions. These are the queries behind the exhibit. Paste them into the
**Logs** blade of the `pg-ai` component (the page's "Logs (KQL)" link opens it).

> Telemetry lands ~30–90s after a load run. All times are UTC.

---

## 1. The Application Map, as a query

The map is just this join, drawn. Every dependency edge with its call count, failure
rate and p95 — across the web app, the API tier and the Functions app at once.

```kusto
dependencies
| where timestamp > ago(1h)
| summarize calls=count(), failures=countif(success == false),
            p50=percentile(duration, 50), p95=percentile(duration, 95)
            by source=cloud_RoleName, target, type
| extend failRatePct = round(100.0 * failures / calls, 1)
| order by calls desc
```

`type` separates the hops you can't see on a flat log: `SQL` (Azure SQL), `Azure
DocumentDB` (Cosmos), `HTTP` (the web→API call), `Queue Message | Azure Service Bus`,
`Azure Event Grid`.

## 2. Requests by component, with failure rate

The node colour on the map. `cloud_RoleName` is the auto-assigned role — here it's the
App Service / Function site names (`pg-app-*`, `pg-api-*`, `pg-fn-*`).

```kusto
requests
| where timestamp > ago(1h)
| summarize count(), failures=countif(success == false),
            p95=percentile(duration, 95) by cloud_RoleName, name
| extend failRatePct = round(100.0 * failures / count_, 1)
| order by count_ desc
```

## 3. The faulted edges (what fault injection produced)

The injected `?fault=error` calls show up as failed dependencies on the web app's
outbound HTTP to the API, and as failed requests on the API. `?fault=slow` shows as a
fat-tailed duration.

```kusto
dependencies
| where timestamp > ago(1h) and success == false
| summarize failures=count() by target, resultCode, problemId=tostring(customDimensions)
| order by failures desc
```

## 4. End-to-end transaction (one trace across every hop)

Pick any `operation_Id` and replay the whole distributed transaction in order — the
web request, the API request it triggered, and every SQL/Cosmos/bus call underneath,
correlated by W3C trace context with zero manual plumbing.

```kusto
let op = "<paste an operation_Id>";
union requests, dependencies, traces
| where operation_Id == op
| project timestamp, itemType, cloud_RoleName, name, target, duration, success, resultCode
| order by timestamp asc
```

To grab a recent failing `operation_Id`:

```kusto
requests | where timestamp > ago(1h) and success == false
| top 5 by timestamp desc | project timestamp, name, operation_Id
```

## 5. Custom metric — `pg.txn.ms` by outcome

The load generator emits a custom metric per transaction, dimensioned by outcome
(`ok` / `failed`). This is the "cool number" layer on top of the auto-telemetry —
Dynatrace can do custom metrics too, but here it's three lines of SDK and a free query.

```kusto
customMetrics
| where timestamp > ago(1h) and name == "pg.txn.ms"
| extend outcome = tostring(customDimensions.outcome)
| summarize txns=count(), avgMs=round(avg(value), 1),
            p95=round(percentile(value, 95), 1) by outcome, bin(timestamp, 1m)
| order by timestamp asc
```

## 6. Custom event — `pg.txn` breakdown

```kusto
customEvents
| where timestamp > ago(1h) and name == "pg.txn"
| extend outcome = tostring(customDimensions.outcome),
         fault = tostring(customDimensions.fault),
         chain = tostring(customDimensions.chain)
| summarize count() by outcome, fault, chain
| order by count_ desc
```

## 7. Dependency latency percentiles (the Performance blade)

Which backend is actually slow — SQL, Cosmos, the API hop, or the bus?

```kusto
dependencies
| where timestamp > ago(1h)
| summarize p50=percentile(duration,50), p95=percentile(duration,95),
            p99=percentile(duration,99), calls=count() by type, target
| order by p95 desc
```

---

## Why this is the case against Dynatrace (for an Azure-native app)

- **No agent rollout.** The entire instrumentation is one `AddApplicationInsightsTelemetry()`
  call plus one app setting (`APPLICATIONINSIGHTS_CONNECTION_STRING`). No OneAgent on
  every host, no per-host licensing to plan around. The map above drew itself from the
  traffic the other exhibits already make.
- **KQL.** Everything above is ad-hoc, joinable, and free to run. The same telemetry is
  one query away from a workbook, an alert, or an export — without leaving the portal or
  the Azure RBAC boundary.
- **Cost shape.** $0 at rest; you pay per GB ingested. For a credit-union app fleet
  that's bursty and mostly idle, that's a very different bill from host-hour or DPS-based
  pricing.

What this pack does **not** try to claim: Dynatrace's Davis AI causal root-cause and its
full-stack / multicloud host coverage are real strengths App Insights doesn't match. The
defensible position is narrow and strong — *for an Azure-native stack, you already own a
first-class observability platform; turn it on before you buy another one.*
