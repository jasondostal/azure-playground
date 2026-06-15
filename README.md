# azure-playground

A cheap, always-available sandbox for experimenting with Azure services. Flip a service on, play with it, turn it off. The opposite of a production reference — public endpoints, one resource group, cheapest possible tiers.

It composes the **same** Bicep modules as the production reference ([azure-platform-iac](../azure-platform-iac)), just dialled to cheap. So what you prove here maps directly onto the real thing.

## Cost model: ~$0 at rest

The baseline is one **scale-to-zero Container App** — `minReplicas=0`, so it costs nothing when nothing is hitting it. Every other service is **opt-in** and on its cheapest tier:

| Service | Tier | Idle cost |
|---------|------|-----------|
| Container App (baseline) | Consumption, scale-to-zero | ~$0 |
| Cosmos DB | serverless (pay-per-request) | ~$0 |
| Storage | Standard_LRS (pay-per-use) | ~$0 |
| Key Vault | standard (pay-per-op) | ~pennies |
| Service Bus | Basic | ~pennies |
| Azure SQL | Basic | **~$5/mo while it exists** — turn it off when done |

Only Azure SQL has a standing cost (the Basic tier isn't serverless). Leave it off unless you're actively using it. Everything else can "stay active" for roughly nothing.

## Usage

```bash
make up                      # baseline: scale-to-zero container app
make up SVC=cosmos,storage   # + Cosmos (serverless) + Storage
make up SVC=sql SQL_PASSWORD='S0me-Str0ng-Pass!'   # + Azure SQL (Basic)
make whatif SVC=sb           # preview first
make status                  # what's deployed
make down                    # delete the whole RG — total teardown
```

Services: `sql` · `cosmos` · `storage` · `kv` · `sb` · `aca` (baseline).

Everything lands in one resource group, `rg-pg-playground`. `make down` is `az group delete` on that group — the whole playground vanishes in one call.

## Why public (and why no self-hosted agent)

The production reference is private-by-default, which forces private endpoints + a self-hosted VNet agent. That's correct for prod and wrong for a playground: it adds cost, DNS, and a deploy agent you have to babysit. The playground goes **public** (firewall to your IP where it matters), so a plain `az` login — local, Cloud Shell, or a hosted pipeline agent — deploys it directly. Fast in, fast out.

## Where it sits

| Repo | Role |
|------|------|
| [azure-platform-iac](../azure-platform-iac) | engine — the modules this consumes |
| [azure-project-starter](../azure-project-starter) | factory — generate a real project |
| [azure-iac-patterns](../azure-iac-patterns) | clean standalone module library |
| [azure-ref-webapp-sql](../azure-ref-webapp-sql) | example #1 — the production-shaped monolith (deploy-to-prove, then delete) |
| **azure-playground** (this repo) | example #2 — the cheap, always-on sandbox |

## Adding a service

Each toggle in `infra/playground.bicep` is a `if (enableX)` module pointing at a platform module. To add one: reference the platform module, add an `enableX` param, wire a `case` into the `Makefile`. The platform modules already cover Functions, Event Grid, AI Foundry, APIM (use **Consumption** tier — Developer is slow and ~$50/mo), and more.
