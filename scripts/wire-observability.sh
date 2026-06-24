#!/usr/bin/env bash
# Exhibit #5 — observability wiring. Idempotent. Runs from `make deploy`, always
# (observability is cross-cutting, not a per-exhibit toggle). Ensures a Log
# Analytics workspace + an Application Insights component exist, then injects the
# connection string into the web + API apps so their already-deployed SDK starts
# emitting. App Insights is free at rest (pay per GB ingested), so leaving it on
# costs nothing until you actually generate load.
#
# This is the whole "no agent rollout" story: provision one resource, set one app
# setting, and the App Map draws itself from traffic.
set -uo pipefail
RG=rg-pg-playground
DEPLOY=playground
AI=pg-ai
WS=pg-logs
out() { az deployment sub show -n "$DEPLOY" --query "properties.outputs.$1.value" -o tsv 2>/dev/null; }

APP=$(out appServiceName); API=$(out apiServiceName)
LOC=$(az group show -n "$RG" --query location -o tsv 2>/dev/null)
[ -z "$LOC" ] && { echo ">> playground RG not deployed; skipping observability wiring"; exit 0; }

echo ">> wiring observability: $AI"
az extension add -n application-insights -y >/dev/null 2>&1 || true

# 1. Log Analytics workspace (workspace-based App Insights is the current model).
WSID=$(az monitor log-analytics workspace show -g "$RG" -n "$WS" --query id -o tsv 2>/dev/null)
if [ -z "$WSID" ]; then
  WSID=$(az monitor log-analytics workspace create -g "$RG" -n "$WS" -l "$LOC" \
    --query id -o tsv 2>/dev/null)
  echo "   workspace: $WS"
fi

# 2. Application Insights component, bound to the workspace (idempotent).
if [ -n "$WSID" ]; then
  az monitor app-insights component create -g "$RG" -a "$AI" -l "$LOC" --kind web \
    --workspace "$WSID" -o none 2>/dev/null || true
else
  # Fallback: classic create (lets Azure pick a default workspace) if the explicit
  # workspace path failed for a region/quota reason — better instrumented than not.
  az monitor app-insights component create -g "$RG" -a "$AI" -l "$LOC" --kind web -o none 2>/dev/null || true
fi

AICONN=$(az monitor app-insights component show -g "$RG" -a "$AI" --query connectionString -o tsv 2>/dev/null)
AIID=$(az monitor app-insights component show -g "$RG" -a "$AI" --query id -o tsv 2>/dev/null)
if [ -z "$AICONN" ]; then
  echo "   !! App Insights not ready (region/quota?) — apps will run un-instrumented; re-run 'make deploy'"
  exit 0
fi

# 3. Inject the connection string + resource id into whichever apps exist. The web
#    app also gets AI_RESOURCE_ID so its /api/observe/links can deep-link the portal.
for site in "$API" "$APP"; do
  [ -z "$site" ] && continue
  SET=("APPLICATIONINSIGHTS_CONNECTION_STRING=$AICONN")
  [ "$site" = "$APP" ] && SET+=("AI_RESOURCE_ID=$AIID")
  az webapp config appsettings set -g "$RG" -n "$site" --settings "${SET[@]}" -o none \
    && echo "   App Insights → $site"
done

SUB=$(az account show --query id -o tsv 2>/dev/null)
echo ">> App Map: https://portal.azure.com/#@/resource${AIID}/applicationMap"
echo ">> observability wired (workspace-based AI '$AI' in $RG)"
