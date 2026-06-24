# TODO / backlog

Newest first.

- [x] **Exhibit #5 — observability (App Map).** Live and verified 2026-06-24 (Central US). Web app +
      API tier instrumented; `pg-ai` (workspace-based, `pg-logs`) provisioned + injected on every
      `make deploy` via `scripts/wire-observability.sh`. Load generator + fault injection at
      `/api/observe/load`, page `/exhibits/observability.html`, KQL pack in `docs/observability-kql.md`.
      Verified via KQL: 3 role nodes (pg-app/pg-api/pg-fn), all dependency edges (HTTP, Cosmos, Service
      Bus), 18 injected failures on pg-api → red `pg-app→pg-api` edge, cross-process trace correlation
      web→SB→Functions confirmed. Workspace-based create succeeded in Central US (no fallback needed).
      **Still to do for the actual demo:** capture a real portal App-Map screenshot into `docs/` (the
      portal render needs a browser — do it live), and consider deploying with `sql` + `WARM=1` so the
      SQL node shows and there's no F1 cold-start in front of an audience.

- [x] **Exhibit #4 — fan-out.** Live and verified 2026-06-17 — one publish reached all four
      consumers (Service Bus A+B, Event Grid A+B). Both paths coded (SB topic+subs; Event Grid
      → Storage Queues), consumers in `src/functions/Fanout4.cs`, publish + receipts endpoints
      in `src/app`, page at `/exhibits/fanout.html`, 8 tests passing. Deploy:
        `make all SVC=cosmos,sb,storage,eg,fn SB_TOPICS=1`  → /exhibits/fanout.html
      (sql/api dropped — #4 doesn't use them.) Live-run finding: Event Grid puts a BinaryData
      payload under `data_base64`, not `data` — fixed both publisher and extractor (see
      EXHIBITS.md #4).

- [ ] **(Post-#4) Event-flow modeling UI.** A view in the app to define event schemas
      and route them — e.g. "customer.created" fires here, goes to consumers X / Y / Z —
      and see the flow laid out visually. A thought for after #4, not before.

- [x] **Exhibit #3 (Nervous System) — first live deploy of the Functions tier.** Done —
      live and verified 2026-06-16 (HTTP → Cosmos → change feed → Service Bus proven). Now wired to
      run on a **Linux Consumption (Y1) plan in its own resource group** (`rg-pg-playground-fn`,
      created by the same sub-scoped bicep; `make down` deletes both RGs). This dodges both
      earlier blockers: the Y1-can't-share-an-RG rule, and the missing dedicated-plan
      `dotnet-isolated` runtime images. Functions app is **.NET 9 isolated** (no .NET 10 Linux
      Functions image exists yet); web/API stay .NET 10. All code/infra/wiring committed.
      Bring it up next session and verify the five scenarios:
        `make all SVC=sql,cosmos,api,sb,storage,kv,eg,fn SB_TOPICS=1 WARM=1 SQL_PASSWORD=… API_SECRET=… EASYAUTH_SECRET=…`
      Unverified risk: the app-service-plan module sets `kind: 'linux'` for the Y1 plan; a Linux
      Consumption plan may want `kind: 'functionapp'`. If the plan/app misbehaves on first
      deploy, adjust there. Easy Auth on the web app is CLI-applied (re-applied by `make deploy`
      via EASYAUTH_SECRET after every `make up`).

- [ ] **Left nav / sidebar** in the app shell so exhibits are findable as they pile up
      (the home page card-grid works for a handful; a persistent left nav scales better).
      Should list every exhibit, highlight the active one, collapse on mobile.
- [ ] **Client-side benchmark** button in each exhibit (browser `performance.now()` +
      `Server-Timing` header) to show real felt latency, not just server-side.
- [ ] Consider a 5th "cold" contender card (scale-to-zero / first-hit) to make the
      cold-start caveat visual.
- [ ] **Codify Easy Auth in IaC.** The owner-only lockdown (Entra app reg + authsettingsV2
      + require-assignment) is currently applied by hand via `az` — it will NOT survive a
      `make down` + `make all`. The app registration persists in Entra; the web app's auth
      wiring does not. Fold it into a `make lockdown` target or the bicep so it's reproducible.
