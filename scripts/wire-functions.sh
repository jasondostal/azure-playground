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
az functionapp config appsettings set -g "$RG" -n "$FN" --settings "${SETTINGS[@]}" -o none && echo "   app settings set"

# 5. Deploy the Functions code
az functionapp deployment source config-zip -g "$RG" -n "$FN" --src dist/functions.zip -o none && echo "   code deployed"

# 6. Event Grid custom topic → EventGridIngress function
if [ -n "$EGT" ]; then
  TOPIC_ID=$(az eventgrid topic show -g "$RG" -n "$EGT" --query id -o tsv)
  FN_ID=$(az functionapp show -g "$RG" -n "$FN" --query id -o tsv)
  az eventgrid event-subscription create --name fn-ingress \
    --source-resource-id "$TOPIC_ID" \
    --endpoint-type azurefunction \
    --endpoint "${FN_ID}/functions/EventGridIngress" \
    --event-delivery-schema cloudeventschemav1_0 -o none 2>/dev/null \
    && echo "   Event Grid → EventGridIngress" \
    || echo "   (Event Grid subscription deferred — re-run 'make deploy' once the function is warm)"
fi

echo ">> integration tier wired: $FNURL"
