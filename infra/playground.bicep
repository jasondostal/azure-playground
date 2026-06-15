targetScope = 'subscription'

// ═══════════════════════════════════════════════════════════════════════════
// azure-playground — playground.bicep
//
// "Experiment with all the Azure things, quickly and cheaply."
//
// The OPPOSITE of azure-ref-webapp-sql at every axis:
//   • ONE resource group, ONE region, no environments — the RG is the unit of
//     teardown (`make down` = az group delete).
//   • PUBLIC endpoints — no private endpoints, no private DNS, and therefore NO
//     self-hosted agent. Hosted agents / local az / Cloud Shell deploy directly.
//   • Everything OPT-IN and scale-to-zero / serverless / cheapest tier, so an
//     idle playground costs ~$0 even while it "stays active".
//
// Composes platform modules from ../../azure-platform-iac/modules (the canonical
// source — same modules the production reference uses, just dialled to cheap).
//
// Flip services on with the Makefile:  make up SVC=sql,cosmos,storage
// ═══════════════════════════════════════════════════════════════════════════

@description('Azure region')
param location string = 'eastus'

@description('Short base name for resources')
param appName string = 'pg'

@description('Tenant ID — only needed when enableKeyVault or enableSql (Entra) is on')
param tenantId string = tenant().tenantId

// ── Service toggles — default to the cheapest possible footprint ────────────
// Baseline (enableContainerApp) = one scale-to-zero container app ≈ $0 idle.
// Everything else is OFF until you want to play with it.

@description('Deploy a scale-to-zero Container App (the baseline compute). minReplicas=0 → $0 when idle.')
param enableContainerApp bool = true

@description('Deploy a Storage account (pay-per-use, ~$0 idle).')
param enableStorage bool = false

@description('Deploy a Key Vault (standard, RBAC, pennies-per-op).')
param enableKeyVault bool = false

@description('Deploy a Service Bus namespace (Basic tier — queues only).')
param enableServiceBus bool = false

@description('Deploy a Cosmos DB account (serverless — pay-per-request, $0 idle).')
param enableCosmos bool = false

@description('Deploy Azure SQL (Basic tier, ~$5/mo while it exists — turn it OFF when done).')
param enableSql bool = false

// ── Cheap container image for the baseline app (scales to zero) ─────────────
@description('Container image for the baseline app. Default: the public hello-world sample (no ACR needed).')
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

// ── SQL (only used when enableSql) — classic auth for playground simplicity ──
@description('SQL admin login (only when enableSql).')
param sqlAdminLogin string = 'pgadmin'

@description('SQL admin password (only when enableSql — pass via Makefile, never commit).')
@secure()
param sqlAdminPassword string = ''

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
// Baseline compute — Container Apps (Consumption). Scale-to-zero = $0 idle.
// No Log Analytics wired (keeps it free); consumption logging is enough to play.
// ═══════════════════════════════════════════════════════════════════════════

module acaEnv '../../azure-platform-iac/modules/compute/container-app-environment.bicep' = if (enableContainerApp) {
  name: '${appName}-aca-env'
  scope: rg
  params: {
    name: '${appName}-aca-env'
    location: location
    environment: 'playground'
  }
}

module aca '../../azure-platform-iac/modules/compute/container-app.bicep' = if (enableContainerApp) {
  name: '${appName}-aca'
  scope: rg
  params: {
    name: '${appName}-app'
    location: location
    environmentId: acaEnv.outputs.id
    image: containerImage
    targetPort: 80
    external: true
    minReplicas: 0        // scale to zero — the whole point
    maxReplicas: 1
    enableManagedIdentity: true
    environment: 'playground'
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// Opt-in services — all public, all cheapest tier
// ═══════════════════════════════════════════════════════════════════════════

module storage '../../azure-platform-iac/modules/data/storage.bicep' = if (enableStorage) {
  name: '${appName}-storage'
  scope: rg
  params: {
    name: replace('${appName}stg${uniqueString(rg.id)}', '-', '')
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
    name: '${appName}-kv-${uniqueString(rg.id)}'
    location: location
    tenantId: tenantId
    sku: 'standard'
    enablePurgeProtection: false   // playground — let it be hard-deleted
    disablePublicAccess: false
    environment: 'playground'
  }
}

module serviceBus '../../azure-platform-iac/modules/messaging/service-bus.bicep' = if (enableServiceBus) {
  name: '${appName}-sb'
  scope: rg
  params: {
    name: '${appName}-sb-${uniqueString(rg.id)}'
    location: location
    sku: 'Basic'
    disablePublicAccess: false
    environment: 'playground'
  }
}

module cosmos '../../azure-platform-iac/modules/data/cosmos-db.bicep' = if (enableCosmos) {
  name: '${appName}-cosmos'
  scope: rg
  params: {
    name: '${appName}-cosmos-${uniqueString(rg.id)}'
    location: location
    serverless: true               // pay-per-request, $0 idle
    disablePublicAccess: false
    databaseName: 'playground'
    containerName: 'items'
    partitionKeyPath: '/id'
    environment: 'playground'
  }
}

module sqlServer '../../azure-platform-iac/modules/data/sql-server.bicep' = if (enableSql) {
  name: '${appName}-sql'
  scope: rg
  params: {
    name: '${appName}-sql-${uniqueString(rg.id)}'
    location: location
    adminLogin: sqlAdminLogin
    adminPassword: sqlAdminPassword
    disablePublicAccess: false     // public + firewall — playground
    allowAzureServices: true
    entraOnlyAuth: false           // classic SQL auth for quick connect
    environment: 'playground'
  }
}

module sqlDb '../../azure-platform-iac/modules/data/sql-database.bicep' = if (enableSql) {
  name: '${appName}-sqldb'
  scope: rg
  params: {
    name: '${appName}-db'
    location: location
    sqlServerName: sqlServer.outputs.name
    skuName: 'Basic'
    skuTier: 'Basic'
    environment: 'playground'
  }
}

// ── Outputs (only the enabled ones carry real values) ───────────────────────

output resourceGroup string = rg.name
output containerAppFqdn string = enableContainerApp ? aca.outputs.fqdn : ''
output storageName string = enableStorage ? storage.outputs.name : ''
output keyVaultUri string = enableKeyVault ? keyVault.outputs.uri : ''
output serviceBusEndpoint string = enableServiceBus ? serviceBus.outputs.endpoint : ''
output cosmosEndpoint string = enableCosmos ? cosmos.outputs.endpoint : ''
output sqlServerFqdn string = enableSql ? sqlServer.outputs.fqdn : ''
