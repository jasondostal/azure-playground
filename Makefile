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
WARM         ?=
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
    "")             ;; \
    *) echo "unknown service: $$s (valid: sql cosmos storage kv sb api aca)" >&2; exit 1;; \
  esac; \
done; \
[ -n "$(WARM)" ] && extra="$$extra planSku=B1 planTier=Basic"
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

publish: ## Build + publish both apps to ./dist (local dotnet)
	@mkdir -p dist
	$(call do_publish,api,Api)
	$(call do_publish,app,App)

up: ## Deploy infra (App Service plan + apps + any SVC=... bolts)
	@$(svc_to_params); \
	echo ">> deploying infra [$(SVC)]$${WARM:+ (warm/B1)}:$$extra"; \
	az deployment sub create --name $(DEPLOY) --location $(LOC) \
	  --template-file $(TEMPLATE) \
	  --parameters $(PARAMS) \
	  $$( [ -n "$$extra" ] && echo --parameters $$extra ) \
	  $$( [ -n "$(SQL_PASSWORD)" ] && echo --parameters sqlAdminPassword='$(SQL_PASSWORD)' )

deploy: ## Zip-deploy code to the apps + inject Cosmos key (run after `make up`)
	@set -e; \
	APP=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.appServiceName.value -o tsv); \
	API=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.apiServiceName.value -o tsv); \
	COSMOS=$$(az deployment sub show -n $(DEPLOY) --query properties.outputs.cosmosAccountName.value -o tsv); \
	if [ -n "$$API" ]; then \
	  echo ">> deploying API → $$API"; \
	  az webapp deploy -g $(RG) -n "$$API" --src-path dist/api.zip --type zip; \
	  if [ -n "$$COSMOS" ]; then \
	    echo ">> injecting Cosmos key into $$API"; \
	    KEY=$$(az cosmosdb keys list -g $(RG) -n "$$COSMOS" --query primaryMasterKey -o tsv); \
	    az webapp config appsettings set -g $(RG) -n "$$API" --settings COSMOS_KEY="$$KEY" -o none; \
	  fi; \
	fi; \
	if [ -n "$$APP" ]; then \
	  echo ">> deploying app → $$APP"; \
	  az webapp deploy -g $(RG) -n "$$APP" --src-path dist/app.zip --type zip; \
	fi; \
	echo ">> live at: $$(az deployment sub show -n $(DEPLOY) --query properties.outputs.appUrl.value -o tsv)"

all: ## up + publish + deploy in one shot
	$(MAKE) up SVC="$(SVC)" WARM="$(WARM)" SQL_PASSWORD="$(SQL_PASSWORD)"
	$(MAKE) publish
	$(MAKE) deploy

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
