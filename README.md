# azure-playground

My proud little Azure frankenstein. One living web app you bolt Azure services onto to learn them — flip a service on, build an exhibit against it, leave it (or rip it off). Does it *need* a service bus stapled to its forehead? No. But here we are.

The opposite of a production reference: **public endpoints, one resource group, cheapest possible tiers**. It composes the **same** Bicep modules as the production reference ([azure-platform-iac](../azure-platform-iac)) — just dialled to cheap — so what you prove here maps onto the real thing.

## Anatomy

| Part | What it is |
|------|-----------|
| **Body** | An App Service web app (`src/app`) — the GUI, the lab home, the exhibits. |
| **Limb** | An optional second App Service web app (`src/api`) — an API tier the GUI calls. |
| **Bolts** | Opt-in services (SQL, Cosmos, Storage, Key Vault, Service Bus), cheapest tier, flipped on by the Makefile. |

Both apps are .NET 10, hosted on **App Service Free (F1)** — no containers, no image registry. Code is published locally and **zip-deployed** (server-side it just runs the package).

## Cost model

Everything is ~$0 at rest **except Azure SQL** (Basic ≈ $5/mo while it exists — turn it off when done).

| Service | Tier | Idle cost |
|---------|------|-----------|
| App Service (body + limb) | **F1 Free** | **$0** |
| Cosmos DB | serverless (pay-per-request) | ~$0 |
| Storage | Standard_LRS (pay-per-use) | ~$0 |
| Key Vault / Service Bus | standard / Basic | ~pennies |
| App Insights + Log Analytics | workspace-based | **$0 idle** (pay per GB ingested) |
| Azure SQL | Basic | **~$5/mo while it exists** |

`WARM=1` upgrades the plan to **B1 (~$13/mo)** with Always-On — use it only when you want zero cold-start (e.g. a clean latency demo). F1 cold-starts on first hit; for the latency exhibit that doesn't matter (the benchmark warms each path before measuring).

## Prerequisites

- `az` logged into the target subscription (`az login`)
- .NET 10 SDK locally (`~/.dotnet` — `dotnet build` / `publish`)
- `make`, `zip`

## Usage

```bash
make all SVC=sql,cosmos,api WARM=1 SQL_PASSWORD='S0me-Str0ng-Pass!'  # exhibit #1, warm
# …or step by step:
make up      SVC=sql,cosmos,api          # deploy infra (plan + apps + bolts)
make publish                              # build + zip both apps
make deploy                               # push code, inject Cosmos key, print URL

make whatif  SVC=sql,cosmos,api           # preview the infra diff first
make status                               # what's deployed
make outputs                              # app URL + endpoints
make down                                 # delete the whole RG — total teardown
```

Services: `sql` · `cosmos` · `storage` · `kv` · `sb` · `api` · `aca` (the body).
Everything lands in one resource group, `rg-pg-playground`; `make down` deletes it whole.

## Why public (and why no self-hosted agent)

The production reference is private-by-default, which forces private endpoints + a self-hosted VNet agent. Correct for prod, wrong for a playground. The playground goes **public** so a plain `az` login deploys it directly. Fast in, fast out.

## Architecture

See [docs/playground-architecture.md](docs/playground-architecture.md) for the full topology (two resource groups, all services, and how the three exhibits flow through them).

## Exhibits

See [EXHIBITS.md](EXHIBITS.md) for the running log of what's been added. Exhibit #1: **SSN reveal latency** — does fronting a database with an API "take seconds"? (No.) Exhibit #5: **the App Map draws itself** — turn on Application Insights and watch the whole topology, call rates and failures appear from traffic alone, no agent install ([KQL pack](docs/observability-kql.md)).

**Observability is always-on:** `make deploy` provisions a workspace-based Application Insights (`pg-ai`) and injects its connection string into the web + API apps. It's free at rest, so every exhibit's traffic shows up on the App Map for free — that's the point of Exhibit #5.

## Adding a service / exhibit

Each toggle in `infra/playground.bicep` is an `if (enableX)` module pointing at a platform module. New exhibit = a page under `src/app/wwwroot/exhibits/`, its endpoints in `src/app/Program.cs` (and `src/api` if it needs the limb), and flip whatever `enableX` bolt it depends on. The platform modules already cover Functions, Event Grid, AI Foundry, APIM, and more.
