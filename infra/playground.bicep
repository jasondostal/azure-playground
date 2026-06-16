targetScope = 'subscription'

// ═══════════════════════════════════════════════════════════════════════════
// azure-playground — playground.bicep
//
// One living monolith you bolt Azure services onto to learn them.
//   • The BODY  = an App Service web app running the playground GUI.
//   • The LIMB  = an optional second App Service web app (an API tier the GUI calls).
//   • The BOLTS = opt-in services (SQL, Cosmos, Storage, Key Vault, Service Bus),
//                 each on its cheapest tier, flipped on by the Makefile.
//
// Hosting is App Service Free (F1) — $0/mo, zip-deployed from a pre-built
// package (no containers, no registry). Only Azure SQL (Basic, ~$5/mo while it
// exists) carries a standing cost. ONE resource group is the unit of teardown
// (`make down` = az group delete). PUBLIC endpoints, no private DNS, no
// self-hosted agent — a plain `az` login deploys it directly. Composes modules
// from ../../azure-platform-iac/modules.
// ═══════════════════════════════════════════════════════════════════════════

@description('Azure region')
param location string = 'eastus'

@description('Short base name for resources')
param appName string = 'pg'

@description('Tenant ID — only needed when enableKeyVault is on')
param tenantId string = tenant().tenantId

// ── The monolith body + its API limb ───────────────────────────────────────
@description('Deploy the monolith web app (the playground body).')
param enableContainerApp bool = true

@description('Deploy the API limb — a second web app the GUI calls (for the latency exhibit etc.).')
param enableApi bool = false

@description('App Service Plan SKU. F1/Free = $0 (no Always-On). B1/Basic = ~$13/mo, supports Always-On.')
param planSku string = 'F1'

@description('App Service Plan tier matching planSku (Free | Basic | Standard).')
param planTier string = 'Free'

@description('.NET runtime stack for both apps.')
param runtimeStack string = 'DOTNETCORE|10.0'

// ── Bolt-on services — default OFF, cheapest tier when on ───────────────────
@description('Deploy a Storage account (pay-per-use, ~$0 idle).')
param enableStorage bool = false

@description('Deploy a Key Vault (standard, RBAC, pennies-per-op).')
param enableKeyVault bool = false

@description('Deploy a Service Bus namespace (Basic tier — queues only).')
param enableServiceBus bool = false

@description('Deploy a Cosmos DB account (serverless — pay-per-request, $0 idle). Read by the API limb via account key.')
param enableCosmos bool = false

@description('Deploy Azure SQL (Basic tier, ~$5/mo while it exists — turn it OFF when done).')
param enableSql bool = false

@description('Deploy the integration tier — an Azure Functions app (Consumption/Y1, scale-to-zero). Requires enableStorage.')
param enableFunctions bool = false

@description('Deploy an Event Grid custom topic (Exhibit #3 ingress path).')
param enableEventGrid bool = false

@description('Service Bus topics + subscriptions for fan-out (Exhibit #3 scenario 3). Forces Service Bus to Standard (~$0.0135/hr while up). Basic (queues only) when false.')
param enableTopics bool = false

// ── SQL (classic auth for playground simplicity) ────────────────────────────
@description('SQL admin login (only when enableSql).')
param sqlAdminLogin string = 'pgadmin'

@description('SQL admin password (only when enableSql — pass via Makefile, never commit).')
@secure()
param sqlAdminPassword string = ''

@description('Shared secret the body sends to the API limb (X-Playground-Key). Pass via Makefile.')
@secure()
param apiSharedSecret string = ''

// ── Deterministic resource names ────────────────────────────────────────────
var rgId = subscriptionResourceId('Microsoft.Resources/resourceGroups', 'rg-${appName}-playground')
var suffix = uniqueString(rgId)
var sqlServerName = '${appName}-sql-${suffix}'
var sqlDbName = '${appName}-db'
var cosmosName = '${appName}-cosmos-${suffix}'
var appServiceName = '${appName}-app-${suffix}'   // web app names are global DNS
var apiServiceName = '${appName}-api-${suffix}'
var functionsName = '${appName}-fn-${suffix}'
var storageName = replace('${appName}stg${suffix}', '-', '')
var fnStorageName = replace('${appName}fnstg${suffix}', '-', '')
var fnRgName = 'rg-${appName}-playground-fn'
var eventGridName = '${appName}-egt-${suffix}'
var sbSku = enableTopics ? 'Standard' : 'Basic'

var sqlFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
var sqlConn = 'Server=tcp:${sqlFqdn},1433;Initial Catalog=${sqlDbName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var cosmosEndpoint = 'https://${cosmosName}.documents.azure.com:443/'

// ═══════════════════════════════════════════════════════════════════════════
// One resource group — the whole playground, and the unit of teardown
// ═══════════════════════════════════════════════════════════════════════════

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${appName}-playground'
  location: location
  tags: {
    purpose: 'playground'
    managedBy: 'azure-playground'
    costProfile: 'cheap'
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// Bolt-on services
// ═══════════════════════════════════════════════════════════════════════════

module sqlServer '../../azure-platform-iac/modules/data/sql-server.bicep' = if (enableSql) {
  name: '${appName}-sql'
  scope: rg
  params: {
    name: sqlServerName
    location: location
    adminLogin: sqlAdminLogin
    adminPassword: sqlAdminPassword
    disablePublicAccess: false   // public + firewall — playground
    allowAzureServices: true     // let the App Services reach it
    entraOnlyAuth: false         // classic SQL auth for quick connect
    environment: 'playground'
  }
}

module sqlDb '../../azure-platform-iac/modules/data/sql-database.bicep' = if (enableSql) {
  name: '${appName}-sqldb'
  scope: rg
  params: {
    name: sqlDbName
    location: location
    sqlServerName: sqlServer.outputs.name
    skuName: 'Basic'
    skuTier: 'Basic'
    environment: 'playground'
  }
}

module storage '../../azure-platform-iac/modules/data/storage.bicep' = if (enableStorage) {
  name: '${appName}-storage'
  scope: rg
  params: {
    name: storageName
    location: location
    sku: 'Standard_LRS'
    disablePublicAccess: false
    environment: 'playground'
  }
}

module keyVault '../../azure-platform-iac/modules/security/key-vault.bicep' = if (enableKeyVault) {
  name: '${appName}-kv'
  scope: rg
  params: {
    name: '${appName}-kv-${suffix}'
    location: location
    tenantId: tenantId
    sku: 'standard'
    enablePurgeProtection: false
    disablePublicAccess: false
    environment: 'playground'
  }
}

var sbNamespaceName = '${appName}-sb-${suffix}'
module serviceBus '../../azure-platform-iac/modules/messaging/service-bus.bicep' = if (enableServiceBus) {
  name: '${appName}-sb'
  scope: rg
  params: {
    name: sbNamespaceName
    location: location
    sku: sbSku
    disablePublicAccess: false
    disableLocalAuth: false   // demo: use the SAS connection string
    environment: 'playground'
  }
}

// Cosmos — serverless, with local (key) auth enabled so the F1 API limb can
// connect without a managed identity. The key itself is injected into the API's
// app settings post-deploy by the Makefile (`az cosmosdb keys list`), because
// reading it here via listKeys() is unsafe when Cosmos is toggled off.
module cosmos '../../azure-platform-iac/modules/data/cosmos-db.bicep' = if (enableCosmos) {
  name: '${appName}-cosmos'
  scope: rg
  params: {
    name: cosmosName
    location: location
    serverless: true
    disablePublicAccess: false
    disableLocalAuth: false      // F1 has no MI → use the account key
    databaseName: 'playground'
    containerName: 'members'
    partitionKeyPath: '/id'
    environment: 'playground'
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// Compute — one App Service Plan, the API limb, and the monolith body
// ═══════════════════════════════════════════════════════════════════════════

module plan '../../azure-platform-iac/modules/compute/app-service-plan.bicep' = if (enableContainerApp || enableApi) {
  name: '${appName}-asp'
  scope: rg
  params: {
    name: '${appName}-asp'
    location: location
    skuName: planSku
    skuTier: planTier
    osKind: 'linux'
    environment: 'playground'
  }
}

module api '../../azure-platform-iac/modules/compute/app-service.bicep' = if (enableApi) {
  name: '${appName}-api'
  scope: rg
  params: {
    name: apiServiceName
    location: location
    appServicePlanId: plan.outputs.id
    runtimeStack: runtimeStack
    alwaysOn: planTier != 'Free'   // F1 forbids Always-On; B1+ enables it
    enableManagedIdentity: false   // F1: no managed identity
    environment: 'playground'
    appSettings: {
      SQL_CONNECTION: enableSql ? sqlConn : ''
      COSMOS_ENDPOINT: enableCosmos ? cosmosEndpoint : ''
      API_SHARED_SECRET: apiSharedSecret
      // COSMOS_KEY injected post-deploy by the Makefile (az cosmosdb keys list)
    }
  }
}

module app '../../azure-platform-iac/modules/compute/app-service.bicep' = if (enableContainerApp) {
  name: '${appName}-app'
  scope: rg
  params: {
    name: appServiceName
    location: location
    appServicePlanId: plan.outputs.id
    runtimeStack: runtimeStack
    alwaysOn: planTier != 'Free'
    enableManagedIdentity: false
    environment: 'playground'
    appSettings: {
      SQL_CONNECTION: enableSql ? sqlConn : ''
      API_BASE: enableApi ? 'https://${api.outputs.defaultHostName}' : ''
      COSMOS_ENDPOINT: enableCosmos ? cosmosEndpoint : ''
      API_SHARED_SECRET: apiSharedSecret
      // COSMOS_KEY injected post-deploy by the Makefile (az cosmosdb keys list)
    }
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// Exhibit #3 — the integration tier (Functions on Consumption + Event Grid).
// SB entities, Cosmos containers, App Insights, the EG→function subscription,
// and connection settings are wired post-deploy by the Makefile (idempotent CLI).
// ═══════════════════════════════════════════════════════════════════════════

// The Functions app lives in its OWN resource group on a Linux Consumption (Y1)
// plan — true scale-to-zero. A Linux Y1 plan can't share a resource group with a
// regular App Service plan ("LinuxDynamicWorkersNotAllowedInResourceGroup"), and
// the dedicated-plan dotnet-isolated runtime images aren't published. Its own RG
// dodges both. The function reaches Service Bus / Cosmos / Storage in the main RG
// over connection strings (same subscription). `make down` deletes both RGs.
// (.NET 9 isolated — no .NET 10 Linux Functions image exists yet.)
resource rgFn 'Microsoft.Resources/resourceGroups@2024-03-01' = if (enableFunctions) {
  name: fnRgName
  location: location
  tags: { purpose: 'playground-functions', managedBy: 'azure-playground', costProfile: 'cheap' }
}

module fnStorage '../../azure-platform-iac/modules/data/storage.bicep' = if (enableFunctions) {
  name: '${appName}-fnstg'
  scope: rgFn
  params: {
    name: fnStorageName
    location: location
    sku: 'Standard_LRS'
    disablePublicAccess: false
    environment: 'playground'
  }
}

module fnPlan '../../azure-platform-iac/modules/compute/app-service-plan.bicep' = if (enableFunctions) {
  name: '${appName}-fnplan'
  scope: rgFn
  params: {
    name: '${appName}-fnplan'
    location: location
    skuName: 'Y1'           // Consumption — scale-to-zero
    skuTier: 'Dynamic'
    osKind: 'linux'
    environment: 'playground'
  }
}

module functions '../../azure-platform-iac/modules/compute/function-app.bicep' = if (enableFunctions) {
  name: '${appName}-fn'
  scope: rgFn
  dependsOn: [fnStorage]
  params: {
    name: functionsName
    location: location
    appServicePlanId: fnPlan.outputs.id
    storageAccountName: fnStorageName
    runtimeStack: 'dotnet-isolated'
    runtimeVersion: '9'    // .NET 10 isolated Functions image isn't published on Linux yet; 9 is GA
    identityBasedStorage: false   // use the function storage key (simplest)
    environment: 'playground'
    appSettings: {
      // Connection strings + AI + target injected post-deploy by the Makefile.
      TARGET_API_BASEURL: enableApi ? 'https://${api.outputs.defaultHostName}' : ''
    }
  }
}

module eventGrid '../../azure-platform-iac/modules/messaging/eventgrid-topic.bicep' = if (enableEventGrid) {
  name: '${appName}-egt'
  scope: rg
  params: {
    name: eventGridName
    location: location
    inputSchema: 'CloudEventSchemaV1_0'
    environment: 'playground'
  }
}

// ── Outputs ─────────────────────────────────────────────────────────────────

output resourceGroup string = rg.name
output appServiceName string = enableContainerApp ? appServiceName : ''
output apiServiceName string = enableApi ? apiServiceName : ''
output appUrl string = enableContainerApp ? 'https://${app.outputs.defaultHostName}' : ''
output apiUrl string = enableApi ? 'https://${api.outputs.defaultHostName}' : ''
output sqlServerFqdn string = enableSql ? sqlServer.outputs.fqdn : ''
output cosmosEndpoint string = enableCosmos ? cosmos.outputs.endpoint : ''
output cosmosAccountName string = enableCosmos ? cosmosName : ''
output storageName string = enableStorage ? storage.outputs.name : ''
output keyVaultUri string = enableKeyVault ? keyVault.outputs.uri : ''
output serviceBusEndpoint string = enableServiceBus ? serviceBus.outputs.endpoint : ''
output serviceBusNamespace string = enableServiceBus ? sbNamespaceName : ''
output functionsName string = enableFunctions ? functionsName : ''
output functionsResourceGroup string = enableFunctions ? fnRgName : ''
output functionsUrl string = enableFunctions ? 'https://${functions.outputs.defaultHostName}' : ''
output eventGridName string = enableEventGrid ? eventGridName : ''
output eventGridEndpoint string = enableEventGrid ? eventGrid.outputs.endpoint : ''
