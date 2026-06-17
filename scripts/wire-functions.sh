#!/usr/bin/env bash
# Post-deploy wiring for Exhibit #3 (the integration tier). Idempotent. Runs from
# `make deploy`. Provisions Service Bus entities, Cosmos containers, App Insights,
# injects connection settings, deploys the Functions code, and subscribes the
# Event Grid topic to the EventGridIngress function. No-op if Functions are off.
set -uo pipefail
RG=rg-pg-playground
DEPLOY=playground
out() { az deployment sub show -n "$DEPLOY" --query "properties.outputs.$1.value" -o tsv 2>/dev/null; }

FN=$(out functionsName)
[ -z "$FN" ] && { echo ">> functions not enabled; skipping integration wiring"; exit 0; }
FN_RG=$(out functionsResourceGroup); FN_RG=${FN_RG:-$RG}   # Functions live in their own RG
FNURL=$(out functionsUrl); SBNS=$(out serviceBusNamespace); COSMOS=$(out cosmosAccountName); EGT=$(out eventGridName)
LOC=$(az group show -n "$RG" --query location -o tsv)
echo ">> wiring integration tier: $FN"

SBCONN=""; COSMOSCONN=""; AICONN=""; SKU="Basic"

# 1. Service Bus entities (queues always; topic + subs only on Standard)
if [ -n "$SBNS" ]; then
  for q in ingress to-target db-events; do
    az servicebus queue create -g "$RG" --namespace-name "$SBNS" -n "$q" -o none 2>/dev/null && echo "   queue: $q"
  done
  SKU=$(az servicebus namespace show -g "$RG" -n "$SBNS" --query sku.name -o tsv)
  if [ "$SKU" = "Standard" ]; then
    az servicebus topic create -g "$RG" --namespace-name "$SBNS" -n events -o none 2>/dev/null || true
    for s in sub-a sub-b; do
      az servicebus topic subscription create -g "$RG" --namespace-name "$SBNS" --topic-name events -n "$s" -o none 2>/dev/null || true
    done
    echo "   topic: events (+ sub-a, sub-b)"
  fi
  SBCONN=$(az servicebus namespace authorization-rule keys list -g "$RG" --namespace-name "$SBNS" --name RootManageSharedAccessKey --query primaryConnectionString -o tsv)
fi

# 2. Cosmos containers (items = monitored, leases = change-feed bookkeeping)
if [ -n "$COSMOS" ]; then
  for c in items leases; do
    az cosmosdb sql container create -g "$RG" -a "$COSMOS" -d playground -n "$c" -p /id -o none 2>/dev/null && echo "   cosmos container: $c"
  done
  CKEY=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --query primaryMasterKey -o tsv)
  CEP=$(az cosmosdb show -g "$RG" -n "$COSMOS" --query documentEndpoint -o tsv)
  COSMOSCONN="AccountEndpoint=${CEP};AccountKey=${CKEY};"
fi

# 3. Application Insights (observability)
az extension add -n application-insights -y >/dev/null 2>&1 || true
az monitor app-insights component create -g "$RG" -a pg-ai -l "$LOC" --kind web -o none 2>/dev/null || true
AICONN=$(az monitor app-insights component show -g "$RG" -a pg-ai --query connectionString -o tsv 2>/dev/null)

# 4. Function app settings (connections + AI; disable fan-out consumers off Standard)
SETTINGS=()
[ -n "$SBCONN" ]    && SETTINGS+=("ServiceBusConnection=$SBCONN")
[ -n "$COSMOSCONN" ] && SETTINGS+=("CosmosDbConnection=$COSMOSCONN")
[ -n "$AICONN" ]    && SETTINGS+=("APPLICATIONINSIGHTS_CONNECTION_STRING=$AICONN")
if [ "$SKU" = "Standard" ]; then
  SETTINGS+=("AzureWebJobs.ConsumerA.Disabled=false" "AzureWebJobs.ConsumerB.Disabled=false")
else
  SETTINGS+=("AzureWebJobs.ConsumerA.Disabled=true" "AzureWebJobs.ConsumerB.Disabled=true")
fi
az functionapp config appsettings set -g "$FN_RG" -n "$FN" --settings "${SETTINGS[@]}" -o none && echo "   app settings set"

# 5. Deploy the Functions code. Linux Consumption rejects WEBSITE_RUN_FROM_PACKAGE=1
#    (the function-app module presets it); config-zip sets its own package URL, so
#    clear it first or the deploy 400s.
az functionapp config appsettings delete -g "$FN_RG" -n "$FN" --setting-names WEBSITE_RUN_FROM_PACKAGE -o none 2>/dev/null || true
az functionapp deployment source config-zip -g "$FN_RG" -n "$FN" --src dist/functions.zip -o none && echo "   code deployed"

# 6. Event Grid custom topic → EventGridIngress function
if [ -n "$EGT" ]; then
  TOPIC_ID=$(az eventgrid topic show -g "$RG" -n "$EGT" --query id -o tsv)
  FN_ID=$(az functionapp show -g "$FN_RG" -n "$FN" --query id -o tsv)
  az eventgrid event-subscription create --name fn-ingress \
    --source-resource-id "$TOPIC_ID" \
    --endpoint-type azurefunction \
    --endpoint "${FN_ID}/functions/EventGridIngress" \
    --event-delivery-schema cloudeventschemav1_0 -o none 2>/dev/null \
    && echo "   Event Grid → EventGridIngress" \
    || echo "   (Event Grid subscription deferred — re-run 'make deploy' once the function is warm)"
fi

# ─────────────────────────────────────────────────────────────────────────────
# 7. Exhibit #4 — fan-out, two ways. Service Bus topic 'fanout' (+ f-sub-a/b) is
#    the SNS→SQS twin (one service); Event Grid → two Storage Queues is the same
#    pattern from cheaper parts. Consumers write receipts to Cosmos 'fanout'.
# ─────────────────────────────────────────────────────────────────────────────
STG=$(out storageName)

# Cosmos receipts container (idempotent)
[ -n "$COSMOS" ] && az cosmosdb sql container create -g "$RG" -a "$COSMOS" -d playground -n fanout -p /id -o none 2>/dev/null \
  && echo "   cosmos container: fanout"

# Service Bus topic + two subscriptions — Standard tier only (queues-only on Basic)
if [ -n "$SBNS" ] && [ "$SKU" = "Standard" ]; then
  az servicebus topic create -g "$RG" --namespace-name "$SBNS" -n fanout -o none 2>/dev/null || true
  for s in f-sub-a f-sub-b; do
    az servicebus topic subscription create -g "$RG" --namespace-name "$SBNS" --topic-name fanout -n "$s" -o none 2>/dev/null || true
  done
  echo "   topic: fanout (+ f-sub-a, f-sub-b)"
fi

# Storage queues the Event Grid topic fans out into + the Functions connection.
STGCONN=""
if [ -n "$STG" ]; then
  STGCONN=$(az storage account show-connection-string -g "$RG" -n "$STG" --query connectionString -o tsv)
  for q in fanout-a fanout-b; do
    az storage queue create --name "$q" --account-name "$STG" --connection-string "$STGCONN" -o none 2>/dev/null \
      && echo "   storage queue: $q"
  done
fi

# Function settings for #4: storage connection + enable/disable consumers per tier.
# (Fan4SbA/B need the topic → Standard; Fan4EgA/B need the storage queues.)
FANSET=()
[ -n "$STGCONN" ] && FANSET+=("FanoutStorageConnection=$STGCONN")
if [ "$SKU" = "Standard" ]; then
  FANSET+=("AzureWebJobs.Fan4SbA.Disabled=false" "AzureWebJobs.Fan4SbB.Disabled=false")
else
  FANSET+=("AzureWebJobs.Fan4SbA.Disabled=true" "AzureWebJobs.Fan4SbB.Disabled=true")
fi
if [ -n "$STGCONN" ]; then
  FANSET+=("AzureWebJobs.Fan4EgA.Disabled=false" "AzureWebJobs.Fan4EgB.Disabled=false")
else
  FANSET+=("AzureWebJobs.Fan4EgA.Disabled=true" "AzureWebJobs.Fan4EgB.Disabled=true")
fi
az functionapp config appsettings set -g "$FN_RG" -n "$FN" --settings "${FANSET[@]}" -o none && echo "   fan-out (#4) settings set"

# Event Grid → Storage Queue subscriptions (filtered to the #4 event type so they
# don't collide with the #3 ingress subscription on the same topic).
if [ -n "$EGT" ] && [ -n "$STG" ]; then
  EG_TOPIC_ID=$(az eventgrid topic show -g "$RG" -n "$EGT" --query id -o tsv)
  STG_ID=$(az storage account show -g "$RG" -n "$STG" --query id -o tsv)
  for q in a b; do
    az eventgrid event-subscription create --name "fanout-$q" \
      --source-resource-id "$EG_TOPIC_ID" \
      --endpoint-type storagequeue \
      --endpoint "${STG_ID}/queueServices/default/queues/fanout-$q" \
      --included-event-types pg.fanout -o none 2>/dev/null \
      && echo "   Event Grid → queue fanout-$q" \
      || echo "   (EG→fanout-$q deferred — re-run 'make deploy')"
  done
fi

echo ">> integration tier wired: $FNURL"
