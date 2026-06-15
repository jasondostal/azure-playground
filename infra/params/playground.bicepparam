using '../playground.bicep'

// Baseline playground config. Service toggles default OFF in playground.bicep;
// the Makefile flips them on per run (make up SVC=sql,cosmos). Override these
// two if you like.

param location = 'eastus'
param appName = 'pg'
