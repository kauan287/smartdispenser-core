.PHONY: help build test test-postgres run smoke up down migration format clean

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-15s\033[0m %s\n", $$1, $$2}'

build: ## Build the solution
	dotnet build SmartDispenser.sln -c Release

test: ## Run unit and integration tests (InMemory database)
	dotnet test SmartDispenser.sln -c Release

test-postgres: ## Run integration tests against the docker compose PostgreSQL on :5433
	TEST_POSTGRES_CONNECTION="Host=localhost;Port=5433;Database=smartdispenser_tests;Username=smartdispenser;Password=smartdispenser" \
		dotnet test tests/SmartDispenser.IntegrationTests -c Release

run: ## Run the API on http://localhost:5000 with the InMemory database
	dotnet run --project src/SmartDispenser.API

smoke: ## Smoke-test a running instance: make smoke URL=http://localhost:8001 WEBHOOK_SECRET=...
	scripts/smoke.sh $(or $(URL),http://localhost:5000)

up: ## Start PostgreSQL and the API with Docker Compose
	docker compose up --build

down: ## Stop Docker Compose services and delete the database volume
	docker compose down -v

migration: ## Add an EF Core migration: make migration name=AddSomething
	dotnet tool restore
	ASPNETCORE_ENVIRONMENT=Production Webhook__Secret=design-time-only-secret-32-characters \
		dotnet ef migrations add $(name) --project src/SmartDispenser.Infrastructure \
		--startup-project src/SmartDispenser.API --output-dir Data/Migrations

format: ## Format the code
	dotnet format SmartDispenser.sln

clean: ## Remove build output
	dotnet clean SmartDispenser.sln
	rm -rf coverage/
