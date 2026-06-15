# Exhibits

A running log of what's been bolted onto the frankenstein, and why.

---

## #1 — SSN reveal latency

**The question:** does putting an API tier in front of the database add latency a user would actually notice when unmasking an SSN? A common assumption is that an API call adds seconds; this measures whether that holds.

**The measurement:** reveal one synthetic SSN three ways and time each, server-side, over N samples (one warmup per path excluded):

1. **Direct → Azure SQL** — the GUI queries SQL in-process (the tightly-coupled .NET monolith).
2. **API → Azure SQL** — the GUI calls the API limb, which queries the same SQL.
3. **API → Cosmos** — the GUI calls the API limb, which reads from Cosmos (serverless).

The delta between #1 and #2 **is** the cost of the API tier (network hop + JSON). Same row, same database — the only variable is the API.

**Bolts:** `sql`, `cosmos`, `api`.

**Run it:**
```bash
make all SVC=sql,cosmos,api WARM=1 SQL_PASSWORD='S0me-Str0ng-Pass!'
# open the printed URL → /exhibits/ssn-latency.html → Reveal, then Run benchmark
```

**The honest caveat (shown in the exhibit, not hidden):** the headline numbers are *warm* (`WARM=1` → B1, Always-On, same region). What actually *can* cost seconds is a **cold start** (scale-to-zero / F1 idle unload) or a **cross-region** call — both are choices you control, not inherent to "having an API." On F1 the benchmark still reads clean because it warms each path before measuring; cold start only hits the very first request.

**Data:** one synthetic member (`640-01-2345`) — not real PII.

**Status:** built. Awaiting first live run for the numbers.
