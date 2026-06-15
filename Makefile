# azure-playground — spin up cheap Azure things, tear them down in one shot.
#
#   make up                       # baseline: one scale-to-zero container app (~$0 idle)
#   make up SVC=sql,cosmos        # + Azure SQL (Basic) + Cosmos (serverless)
#   make up SVC=storage,kv,sb     # + Storage + Key Vault + Service Bus (Basic)
#   make whatif SVC=cosmos        # preview the diff first
#   make status                   # list what's deployed
#   make down                     # delete the whole RG (everything)
#
# Services: sql · cosmos · storage · kv · sb · aca(baseline)
# SQL needs a password:  make up SVC=sql SQL_PASSWORD='S0me-Str0ng-Pass!'

LOC        ?= eastus
RG         := rg-pg-playground
TEMPLATE   := infra/playground.bicep
PARAMS     := infra/params/playground.bicepparam
SVC        ?=
SQL_PASSWORD ?=

define svc_to_params
extra=""; \
for s in $$(echo "$(SVC)" | tr ',' ' '); do \
  case $$s in \
    sql)            extra="$$extra enableSql=true";; \
    cosmos)         extra="$$extra enableCosmos=true";; \
    storage)        extra="$$extra enableStorage=true";; \
    kv|keyvault)    extra="$$extra enableKeyVault=true";; \
    sb|servicebus)  extra="$$extra enableServiceBus=true";; \
    aca|app)        extra="$$extra enableContainerApp=true";; \
    "")             ;; \
    *) echo "unknown service: $$s (valid: sql cosmos storage kv sb aca)" >&2; exit 1;; \
  esac; \
done
endef

.PHONY: up whatif status down outputs help

up: ## Deploy the baseline + any SVC=... services
	@$(svc_to_params); \
	echo ">> deploying playground [$(SVC)]:$$extra"; \
	az deployment sub create --location $(LOC) \
	  --template-file $(TEMPLATE) \
	  --parameters $(PARAMS) \
	  $$( [ -n "$$extra" ] && echo --parameters $$extra ) \
	  $$( [ -n "$(SQL_PASSWORD)" ] && echo --parameters sqlAdminPassword='$(SQL_PASSWORD)' )

whatif: ## Preview the deploy
	@$(svc_to_params); \
	az deployment sub what-if --location $(LOC) \
	  --template-file $(TEMPLATE) \
	  --parameters $(PARAMS) \
	  $$( [ -n "$$extra" ] && echo --parameters $$extra ) \
	  $$( [ -n "$(SQL_PASSWORD)" ] && echo --parameters sqlAdminPassword='$(SQL_PASSWORD)' )

status: ## List everything in the playground RG
	az resource list -g $(RG) -o table 2>/dev/null || echo "(playground RG not deployed)"

outputs: ## Show the last deployment outputs
	az deployment sub show -n playground --query properties.outputs 2>/dev/null || \
	  az deployment sub list --query "[?contains(name,'playground')].name" -o tsv

down: ## Delete the entire playground RG (everything, no undo)
	az group delete --name $(RG) --yes --no-wait && echo ">> teardown started for $(RG)"

help: ## This help
	@grep -E '^[a-z]+:.*##' $(MAKEFILE_LIST) | sed 's/:.*## /  —  /'
