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

**Results (2026-06-16, Central US, B1, 200 samples/path):**

![SSN reveal latency results](docs/ssn-latency-results.png)

| architecture | server p50 | server p95 | client p50 | client p95 |
|---|---|---|---|---|
| Direct → Azure SQL | 1.1 ms | 8 ms | 45 ms | 85 ms |
| Direct → Cosmos | 24 ms | 37 ms | 67 ms | 88 ms |
| API → Azure SQL | 21 ms | 50 ms | 63 ms | 107 ms |
| API → Cosmos | 40 ms | 70 ms | 83 ms | 122 ms |

Server-side the API tier costs ~16–19 ms; client-felt (real Chromium over a LAN) adds a ~45 ms network floor, worst-path p95 still ~122 ms. Nothing approaches "seconds." Graphic generator: [`docs/ssn-latency-chart.html`](docs/ssn-latency-chart.html).

**Status:** live and measured. Locked behind Entra Easy Auth (owner-only).

---

## #2 — Service Bus for synchronous reads (a cautionary tale)

**The point:** the inverse of Exhibit #1. It shows what *actually* earns the "it'll take seconds" reputation — using a message queue for a synchronous read. One in-process call becomes a request/reply round-trip through a broker (~4–6 broker hops), and a naive client poll loop compounds it.

**How it works:** the body sends a request to `reveal-requests`; a background worker in the API limb consumes it, reads SQL, and pushes the answer to `reveal-replies`; the body waits for the reply and times the whole thing. A `?poll=` knob shows how a hand-rolled receive loop makes it worse.

**Bolts:** `sql`, `api`, `sb` (Service Bus Basic).

**Run it:**
```bash
make all SVC=sql,cosmos,api,sb WARM=1 SQL_PASSWORD='...' API_SECRET='...'
# open /exhibits/servicebus.html → Reveal, try the poll modes, Run 10×
```

**Results (2026-06-16, Central US, Basic):**

| path / mode | p50 | vs API tier |
|---|---|---|
| API → SQL (Exhibit #1) | ~20 ms | 1× |
| Service Bus · long-poll | ~450 ms | ~22× |
| Service Bus · poll 1s (naive) | ~1,150 ms | ~57× |

Best case is ~10–25× the API tier; naive polling crosses **a full second** — the literal "seconds," delivered by the wrong tool. Basic tier has no sessions to correlate replies cleanly, so request/reply over the queue also **occasionally loses replies** — slow *and* unreliable. Queues are for async decoupling, not answering a user who's waiting.

**Status:** live and measured.
