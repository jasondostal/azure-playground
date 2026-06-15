using '../playground.bicep'

// Baseline playground config. Service toggles default OFF in playground.bicep;
// the Makefile flips them on per run (make up SVC=sql,cosmos). Override these
// two if you like.

// Region notes for THIS subscription (capacity-constrained):
//   • eastus  — App Service OK, but blocks new Azure SQL servers ("RegionDoesNotAllowProvisioning")
//   • eastus2 — has SQL, but App Service quota is 0 ("SubscriptionIsOverQuotaForSku")
//   • centralus — both available. (also fine: westus2/westus3/southcentralus/canadacentral)
param location = 'centralus'
param appName = 'pg'
