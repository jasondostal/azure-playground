# TODO / backlog

Frankenapp enhancements, newest first.

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
