# azure-playground — build the monolith, bolt services on, tear it all down.
# App Service (Free F1) hosting — no containers, no registry. Code is published
# in a throwaway docker SDK container (no local dotnet needed), zipped, and
# zip-deployed to the web apps.
#
#   make publish                  # build + publish both apps to ./dist via docker
#   make up                       # deploy infra (App Service plan + apps + bolts)
#   make deploy                   # zip-deploy code to the apps (run after `up`)
#   make all SVC=sql,cosmos,api WARM=1 SQL_PASSWORD='S0me-Str0ng-Pass!'  # exhibit #1
#
#   make whatif SVC=sql,cosmos,api      # preview infra diff
#   make status / make outputs          # what's deployed / app URL
#   make down                           # delete the whole RG
#
# Services: sql · cosmos · storage · kv · sb · api · aca(baseline body)
# WARM=1 → B1 plan (Always-On, no cold start, ~$13/mo). Default = F1 Free ($0).

LOC          ?= centralus
RG           := rg-pg-playground
DEPLOY       := playground
TEMPLATE     := infra/playground.bicep
PARAMS       := infra/params/playground.bicepparam
SVC          ?=
SQL_PASSWORD ?=
API_SECRET   ?=
EASYAUTH_SECRET ?=
WARM         ?=
SB_TOPICS    ?=
DOTNET       := $(shell command -v dotnet 2>/dev/null || echo $(HOME)/.dotnet/dotnet)

define svc_to_params
extra=""; \
for s in $$(echo "$(SVC)" | tr ',' ' '); do \
  case $$s in \
    sql)            extra="$$extra enableSql=true";; \
    cosmos)         extra="$$extra enableCosmos=true";; \
    storage)        extra="$$extra enableStorage=true";; \
    kv|keyvault)    extra="$$extra enableKeyVault=true";; \
    sb|servicebus)  extra="$$extra enableServiceBus=true";; \
    api)            extra="$$extra enableApi=true";; \
    aca|app)        extra="$$extra enableContainerApp=true";; \
    fn|functions)   extra="$$extra enableFunctions=true";; \
    eg|eventgrid)   extra="$$extra enableEventGrid=true";; \
    "")             ;; \
    *) echo "unknown service: $$s (valid: sql cosmos storage kv sb api aca fn eg)" >&2; exit 1;; \
  esac; \
done; \
[ -n "$(WARM)" ] && extra="$$extra planSku=B1 planTier=Basic"; \
[ -n "$(SB_TOPICS)" ] && extra="$$extra enableTopics=true"
endef

# Publish one app (src/$(1), project Playground.$(2)) into dist/$(1).zip.
define do_publish
	@echo ">> publishing $(1) ($(DOTNET))…"
	$(DOTNET) publish src/$(1)/Playground.$(2).csproj -c Release -o dist/$(1)-out
	@rm -f dist/$(1).zip
	@( cd dist/$(1)-out && zip -qr "$(CURDIR)/dist/$(1).zip" . )
	@rm -rf dist/$(1)-out
	@echo ">> dist/$(1).zip ready"
endef

.PHONY: publish up deploy all whatif status outputs down help

publish: ## Build + publish the apps to ./dist (local dotnet)
	@mkdir -p dist
	$(call do_publish,api,Api)
	$(call do_publish,app,App)
	$(call do_publish,functions,Functions)

up: ## Deploy infra (App Service plan + apps + any SVC=... bolts)
	@$(svc_to_params); \
	echo ">> deploying infra [$(SVC)]$${WARM:+ (warm/B1)}:$$extra"; \
	az deployment sub create --name $(DEPLOY) --location $(LOC) \
	  --template-file $(TEMPLATE) \
	  --parameters $(PARAMS) \
	  $$( [ -n "$$extra" ] && echo --parameters $$extra ) \
	  $$( [ -n "$(SQL_PASSWORD)" ] && echo --parameters sqlAdminPassword='$(SQL_PASSWORD)' ) \
	  $$( [ -n "$(API_SECRET)" ] && echo --parameters apiSharedSecret='$(API_SECRET)' )

deploy: ## Zip-deploy code to the apps + inject Cosmos key (run after `make up`)
	@set -e; \
	APP=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.appServiceName.value -o tsv); \
	API=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.apiServiceName.value -o tsv); \
	COSMOS=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.cosmosAccountName.value -o tsv); \
	SBNS=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.serviceBusNamespace.value -o tsv); \
	FNURL=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.functionsUrl.value -o tsv); \
	EGEP=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.eventGridEndpoint.value -o tsv); \
	KEY=""; SBCONN=""; \
	if [ -n "$$COSMOS" ]; then KEY=$$(az cosmosdb keys list -g $(RG) -n "$$COSMOS" --query primaryMasterKey -o tsv); fi; \
	if [ -n "$$SBNS" ]; then SBCONN=$$(az servicebus namespace authorization-rule keys list -g $(RG) --namespace-name "$$SBNS" --name RootManageSharedAccessKey --query primaryConnectionString -o tsv); fi; \
	if [ -n "$$API" ]; then \
	  echo ">> deploying API → $$API"; \
	  az webapp deploy -g $(RG) -n "$$API" --src-path dist/api.zip --type zip --track-status false; \
	  [ -n "$$KEY" ] && { echo ">> Cosmos key → $$API"; az webapp config appsettings set -g $(RG) -n "$$API" --settings COSMOS_KEY="$$KEY" -o none; }; \
	  [ -n "$$SBCONN" ] && { echo ">> SB conn → $$API"; az webapp config appsettings set -g $(RG) -n "$$API" --settings SERVICEBUS_CONNECTION="$$SBCONN" -o none; }; \
	fi; \
	if [ -n "$$APP" ]; then \
	  echo ">> deploying app → $$APP"; \
	  az webapp deploy -g $(RG) -n "$$APP" --src-path dist/app.zip --type zip --track-status false; \
	  [ -n "$$KEY" ] && { echo ">> Cosmos key → $$APP"; az webapp config appsettings set -g $(RG) -n "$$APP" --settings COSMOS_KEY="$$KEY" -o none; }; \
	  [ -n "$$SBCONN" ] && { echo ">> SB conn → $$APP"; az webapp config appsettings set -g $(RG) -n "$$APP" --settings SERVICEBUS_CONNECTION="$$SBCONN" -o none; }; \
	  [ -n "$(EASYAUTH_SECRET)" ] && { echo ">> Easy Auth secret → $$APP"; az webapp config appsettings set -g $(RG) -n "$$APP" --settings MICROSOFT_PROVIDER_AUTHENTICATION_SECRET="$(EASYAUTH_SECRET)" -o none; }; \
	  [ -n "$$FNURL" ] && az webapp config appsettings set -g $(RG) -n "$$APP" --settings FUNCTIONS_BASEURL="$$FNURL" EVENTGRID_ENDPOINT="$$EGEP" -o none; \
	fi; \
	echo ">> live at: $$(az deployment sub show -n $(DEPLOY) --query properties.outputs.appUrl.value -o tsv)"
	@bash scripts/wire-functions.sh

all: ## up + publish + deploy in one shot
	$(MAKE) up SVC="$(SVC)" WARM="$(WARM)" SB_TOPICS="$(SB_TOPICS)" SQL_PASSWORD="$(SQL_PASSWORD)" API_SECRET="$(API_SECRET)"
	$(MAKE) publish
	$(MAKE) deploy EASYAUTH_SECRET="$(EASYAUTH_SECRET)"

whatif: ## Preview the infra diff
	@$(svc_to_params); \
	az deployment sub what-if --name $(DEPLOY) --location $(LOC) \
	  --template-file $(TEMPLATE) \
	  --parameters $(PARAMS) \
	  $$( [ -n "$$extra" ] && echo --parameters $$extra ) \
	  $$( [ -n "$(SQL_PASSWORD)" ] && echo --parameters sqlAdminPassword='$(SQL_PASSWORD)' )

status: ## List everything in the playground RG
	az resource list -g $(RG) -o table 2>/dev/null || echo "(playground RG not deployed)"

outputs: ## Show deployment outputs (app URL etc.)
	az deployment sub show -n $(DEPLOY) --query properties.outputs 2>/dev/null || echo "(not deployed)"

down: ## Delete the entire playground RG (everything, no undo)
	az group delete --name $(RG) --yes --no-wait && echo ">> teardown started for $(RG)"

help: ## This help
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | sed 's/:.*## /  —  /'
