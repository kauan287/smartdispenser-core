.PHONY: help build test run up down clean migrate

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-20s\033[0m %s\n", $$1, $$2}'

build: ## Build the solution
	dotnet build SmartDispenser.sln -c Release

test: ## Run unit tests with coverage
	dotnet test tests/SmartDispenser.UnitTests/ -c Release --collect:"XPlat Code Coverage"

run: ## Run API in development mode (hot reload)
	dotnet watch run --project src/SmartDispenser.API/SmartDispenser.API.csproj

up: ## Start PostgreSQL + API via Docker Compose
	docker compose up --build

down: ## Stop Docker Compose services
	docker compose down

migrate: ## Apply EF Core migrations
	dotnet ef database update --project src/SmartDispenser.Infrastructure --startup-project src/SmartDispenser.API

add-migration: ## Add a new migration (usage: make add-migration name=InitialCreate)
	dotnet ef migrations add $(name) --project src/SmartDispenser.Infrastructure --startup-project src/SmartDispenser.API

format: ## Format code
	dotnet format SmartDispenser.sln

clean: ## Remove build artifacts
	find . -type d -name bin -exec rm -rf {} + 2>/dev/null || true
	find . -type d -name obj -exec rm -rf {} + 2>/dev/null || true
	rm -rf coverage/
