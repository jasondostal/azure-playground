# azure-playground — system architecture

One subscription, **two resource groups**, three exhibits. Everything is created by a single
`make all` from the subscription-scoped `infra/playground.bicep`; `make down` deletes both
resource groups. A colorized export lives alongside this file as
[`playground-architecture.svg`](playground-architecture.svg).

```mermaid
flowchart TB
  USER(["Browser / curl"])

  subgraph SUB["Azure subscription · Central US"]
    direction TB

    subgraph RG1["rg-pg-playground"]
      direction TB
      subgraph ASP["App Service Plan (B1)"]
        WEB["Web app<br/>Easy Auth — owner only"]
        API["API service<br/>shared-secret gated"]
      end
      SQL[("Azure SQL<br/>Basic")]
      COS[("Cosmos DB<br/>members · items · leases")]
      subgraph SB["Service Bus (Standard)"]
        QING[[ingress]]
        QTGT[[to-target]]
        QDB[[db-events]]
        QREQ[[reveal-requests]]
        QREP[[reveal-replies]]
        TOP{{"topic: events / sub-a · sub-b"}}
      end
      EGT(["Event Grid topic"])
      AI[("App Insights")]
    end

    subgraph RG2["rg-pg-playground-fn"]
      subgraph YP["Consumption (Y1) · scale-to-zero"]
        FN["Functions — .NET 9 isolated<br/>WebhookDirect · EventGridIngress<br/>ApiGet · ApiPost · Publish<br/>ConsumerA · ConsumerB<br/>QueueToTarget · CosmosChangeFeed"]
      end
    end
  end

  USER --> WEB

  %% Exhibit 1 — reveal latency: direct vs API-fronted
  WEB -->|"#1 direct"| SQL
  WEB -->|"#1 direct"| COS
  WEB -->|"#1 via API"| API
  API --> SQL
  API --> COS

  %% Exhibit 2 — Service Bus for synchronous reads (anti-pattern)
  WEB -->|"#2 request"| QREQ
  QREQ --> API
  API -->|"#2 reply"| QREP
  QREP --> WEB

  %% Exhibit 3 — integration tier ("nervous system")
  USER -->|"#3 webhook"| FN
  EGT -->|"#3 event"| FN
  FN --> QING
  FN <-->|"#3 items"| COS
  COS -. "#3 change feed" .-> FN
  FN --> QDB
  FN -->|"#3 publish"| TOP
  TOP --> FN
  QTGT --> FN
  FN -->|"#3 adapter"| API
  FN -.-> AI

  classDef compute fill:#143b2a,stroke:#4ad98a,color:#eafff4;
  classDef data fill:#10243b,stroke:#4a90d9,color:#e9f3ff;
  classDef bus fill:#3b2a10,stroke:#d9a14a,color:#fff3e0;
  classDef integ fill:#2a1340,stroke:#a14ad9,color:#f3e9ff;
  classDef client fill:#1f232b,stroke:#9aa,color:#fff;
  class WEB,API,FN compute;
  class SQL,COS data;
  class QING,QTGT,QDB,QREQ,QREP,TOP bus;
  class EGT,AI integ;
  class USER client;
  style RG1 fill:#0f1620,stroke:#3a4a5a,color:#cfe;
  style RG2 fill:#160f20,stroke:#5a3a5a,color:#ecf;
  style SUB fill:#0c0e14,stroke:#445,color:#ccd;
  style ASP fill:#10261b,stroke:#2a6,color:#cfe;
  style SB fill:#26200f,stroke:#a83,color:#fec;
  style YP fill:#1d1226,stroke:#849,color:#ecf;
```

**Color key:** 🟩 compute (web / API / Functions) · 🟦 data (SQL / Cosmos) · 🟧 messaging (Service Bus) · 🟪 integration (Event Grid / App Insights).

## Exhibit key

| # | Exhibit | What it shows | Services |
|---|---------|---------------|----------|
| 1 | **SSN reveal latency** | An API tier costs ~16–19 ms, not "seconds" | Web app, API, SQL, Cosmos |
| 2 | **Service Bus for sync reads** | The anti-pattern that *does* cost seconds | Web app, API, Service Bus queues |
| 3 | **Nervous System** | The "ESB" is just Functions + a bus (5 reflexes) | Functions, Service Bus (queues + topic), Event Grid, Cosmos, App Insights |

## Why two resource groups

The resource group is the platform's unit of deployment and teardown. The web/API tier and its
data live in `rg-pg-playground`; the integration tier (Functions) lives in
`rg-pg-playground-fn`. That split is required, not cosmetic: a Linux **Consumption (Y1)**
Functions plan cannot share a resource group with a regular App Service plan
(`LinuxDynamicWorkersNotAllowedInResourceGroup`), and its own RG also gives it true
scale-to-zero. The Functions reach Service Bus / Cosmos / SQL in the main RG over connection
strings (same subscription). `make down` removes both.

## Notes

- **Region:** Central US (East US blocks new Azure SQL; East US 2 has no App Service quota for this subscription).
- **Auth:** the web app is locked to a single Entra account via App Service Easy Auth; the API is reached server-to-server with a shared-secret header.
- **Runtimes:** web app + API on **.NET 10**; Functions on **.NET 9 isolated** (no published .NET 10 Linux Functions image yet).
- **Cost:** ~$0 at rest except Azure SQL Basic (~$5/mo) and, while running the fan-out demo, Service Bus Standard (~$0.0135/hr). The B1 plan is ~$13/mo while up.
