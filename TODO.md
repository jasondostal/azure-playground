# TODO / backlog

Newest first.

- [ ] **Exhibit #3 (Nervous System) — first live deploy of the Functions tier.** Now wired to
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
