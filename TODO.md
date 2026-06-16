# TODO / backlog

Newest first.

- [ ] **Exhibit #3 (Nervous System) — Functions host won't start on Linux.** All code/infra
      is built, committed, and deployed EXCEPT the Functions compute. Root cause: Azure has no
      published Linux runtime image for `dotnet-isolated` on a **dedicated** plan
      (`ImageNotFoundFailure` pulling `…dotnet-isolated{9,10}-appservice-stage3`), and a Linux
      **Consumption (Y1)** plan can't live in an RG that already has the B1 plan
      (`LinuxDynamicWorkersNotAllowedInResourceGroup`). The five scenarios, bicep bolts,
      Makefile wiring, page, and docs are all done. Resume options:
        1. Functions on **Linux Consumption (Y1) in a SEPARATE RG** (`rg-pg-playground-fn`);
           `make down` deletes both. The supported, reliable path for Linux .NET-isolated.
        2. **Windows** Consumption/dedicated Functions (no Linux container image; needs a
           Windows plan + the function-app module taught Windows, different RG-mix rules).
        3. Wait for the `dotnet-isolated` Linux **dedicated** images to publish, then it works
           as-is on the shared B1.
      Note: Easy Auth on the web app is applied via CLI (not IaC), so a `make down` + redeploy
      needs the auth re-applied (see below).

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
