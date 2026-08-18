# 💊 SmartDispenser Core

[![CI](https://github.com/kauan287/smartdispenser-core/actions/workflows/ci.yml/badge.svg)](https://github.com/kauan287/smartdispenser-core/actions/workflows/ci.yml)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-9.0-blue.svg)](https://learn.microsoft.com/aspnet/core)
[![EF Core](https://img.shields.io/badge/EF_Core-9.0-green.svg)](https://learn.microsoft.com/ef/core)
[![SignalR](https://img.shields.io/badge/SignalR-Realtime-orange.svg)](https://learn.microsoft.com/aspnet/core/signalr)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **Production-grade ASP.NET Core 9 backend** for a network of smart pharmaceutical vending machines.
> Real-time hardware control via SignalR, cryptographic webhook validation, resilient integration with
> the [HealthTech Agent Orchestrator](https://github.com/kauan287/healthtech-agent-orchestrator),
> and a live Blazor Server dashboard.

---

## 🔗 Part of the MedTech Ecosystem

This project is the **operational backend** for the medication dispensing pipeline.
It consumes the [HealthTech Agent Orchestrator](https://github.com/kauan287/healthtech-agent-orchestrator)
to verify that a prescription has been AI-evaluated, doctor-approved, and cryptographically signed
**before any medication is released**.

```
┌─────────────────────────────────────────────────────────────────────┐
│                     MedTech Ecosystem                               │
│                                                                     │
│  ┌─────────────────────────────────────────┐                       │
│  │  HealthTech Agent Orchestrator (Proj 1) │                       │
│  │  Python · FastAPI · LangGraph           │                       │
│  │                                         │                       │
│  │  Triage Agent → Safety Agent → HITL     │                       │
│  │  Cryptographic SHA-256 Audit Chain      │                       │
│  └────────────────────┬────────────────────┘                       │
│                        │ HTTP (Polly circuit breaker)               │
│                        ▼                                            │
│  ┌─────────────────────────────────────────┐                       │
│  │     SmartDispenser Core (this repo)     │                       │
│  │     C# · ASP.NET Core 9 · SignalR       │                       │
│  │                                         │                       │
│  │  Verify prescription ✓                  │                       │
│  │  Process payment (HMAC webhook) ✓       │                       │
│  │  Send SignalR command → physical machine │                       │
│  │  Blazor real-time dashboard             │                       │
│  └────────────────────┬────────────────────┘                       │
│                        │ SignalR (WebSocket)                        │
│                        ▼                                            │
│         🤖 Smart Vending Machines (Hardware)                        │
└─────────────────────────────────────────────────────────────────────┘
```

A medication is dispensed **only when ALL of the following are true**:
1. 🧠 Triage AI proposed the medication
2. 🛡️ Safety AI confirmed no allergy conflicts
3. 👨‍⚕️ Doctor explicitly approved (HITL)
4. 🔐 SHA-256 audit chain is cryptographically intact
5. 💳 Payment webhook confirmed & signature validated

---

## ✨ Key Features

| Feature | Details |
|---|---|
| **HMAC-SHA256 Webhook Validation** | Middleware validates signature before request reaches controllers; `FixedTimeEquals` prevents timing attacks |
| **Outbox Pattern** | At-least-once delivery with exponential backoff (5s, 25s, 125s…); dead letter after 5 attempts |
| **Machine State Machine** | `Offline → Idle → Dispensing → Error` — illegal transitions throw; prevents double-dispensing |
| **SignalR Hub (Typed)** | Bidirectional machine ↔ server; machines join their own group; dashboard joins "dashboard" group |
| **Polly Resilience Pipeline** | Retry (3×, exponential) + Circuit Breaker (opens after 5 failures) + Timeout (10s) |
| **MediatR CQRS** | Every business action is a Command/Query handled by a dedicated Handler |
| **EF Core + PostgreSQL** | Migrations, JSONB for OutboxMessage payload, unique indexes on idempotency keys |
| **Blazor Server Dashboard** | Real-time machine grid via SignalR — no polling, no REST calls from UI |
| **Background Services** | `OutboxProcessor` (5s poll) + `HeartbeatMonitor` (30s poll) |
| **Rate Limiting** | Webhook endpoint: sliding window, 10 req/s per IP (built-in .NET 8+) |
| **Health Checks** | `/health/live` (always) + `/health/ready` (DB + HealthTech API reachability) |
| **Serilog Structured Logging** | JSON logs with correlation IDs, machine names, log level overrides per namespace |

---

## 🛠️ Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 9 / ASP.NET Core 9 |
| Real-time | SignalR (WebSocket-first with SSE fallback) |
| Dashboard | Blazor Server (Interactive Server Components) |
| CQRS | MediatR 12 |
| ORM | Entity Framework Core 9 + Npgsql |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8) |
| Logging | Serilog + Console/File sinks |
| API Docs | Swagger / OpenAPI |
| Rate Limiting | Built-in `Microsoft.AspNetCore.RateLimiting` |
| Tests | xUnit + Moq + FluentAssertions + coverlet |
| CI/CD | GitHub Actions |
| Containers | Docker + Docker Compose |

---

## 🏗️ Architecture — Clean Architecture (4 Projects)

```
SmartDispenser.Core          ← Domain + Application (no external deps)
  ├── Entities/               Dispenser (state machine), DispenseRequest, Payment, OutboxMessage
  ├── Interfaces/             IDispenserRepository, IHealthTechApiClient, IDispenserHubContext
  └── Commands/               InitiateDispense, ProcessPaymentWebhook, UpdateMachineState

SmartDispenser.Infrastructure ← Adapters for Core interfaces
  ├── Data/                   AppDbContext, EF Migrations
  ├── ExternalServices/       HealthTechApiClient (Polly resilience)
  ├── Repositories/           EF Core repository implementations
  └── SignalR/                IDispenserHubContext implementation

SmartDispenser.API            ← Entry point: Controllers + SignalR Hub + Blazor
  ├── Controllers/            PaymentsController, DispensersController
  ├── Hubs/                   DispenserHub (typed SignalR)
  ├── Middleware/             WebhookSignatureMiddleware (HMAC-SHA256)
  ├── BackgroundServices/     OutboxProcessor, HeartbeatMonitor
  └── Components/Pages/       Dashboard.razor (real-time Blazor)

SmartDispenser.UnitTests      ← xUnit + Moq + FluentAssertions
  ├── Commands/               InitiateDispenseHandlerTests
  └── Middleware/             PaymentWebhookValidationTests + DispenserStateMachineTests
```

---

## 🚀 Quick Start

### Option 1: Docker Compose *(recommended)*

```bash
git clone https://github.com/kauan287/smartdispenser-core.git
cd smartdispenser-core

# Copy env template and set your webhook secret
cp .env.example .env
# Edit WEBHOOK_SECRET (must be 32+ chars)

docker compose up --build
```

- **API + Swagger:** http://localhost:8001
- **Blazor Dashboard:** http://localhost:8001/dashboard
- **Health:** http://localhost:8001/health/live
- **PostgreSQL:** `localhost:5433`

### Option 2: Local Development

```bash
# Requires .NET 9 SDK
dotnet restore SmartDispenser.sln
dotnet watch run --project src/SmartDispenser.API
```

---

## 📡 API Reference

### `POST /api/dispensers/{id}/dispense`
Initiate medication dispensing (verifies prescription with HealthTech API first).

```bash
curl -X POST http://localhost:8001/api/dispensers/{dispenserId}/dispense \
  -H "Content-Type: application/json" \
  -d '{"prescriptionId": "RX-2024-001", "patientIdentifier": "PATIENT-42"}'
```

Response `202 Accepted`:
```json
{ "dispenseRequestId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

Response `422 Unprocessable` (if prescription not approved):
```json
{
  "title": "Dispense request rejected",
  "detail": "⚠️ Audit chain integrity check FAILED — possible data tampering.",
  "status": 422
}
```

### `POST /api/payments/webhook`
Payment gateway webhook (signature validated by middleware).

```bash
# Signature: sha256=<HMAC-SHA256(body, WEBHOOK_SECRET)>
curl -X POST http://localhost:8001/api/payments/webhook \
  -H "Content-Type: application/json" \
  -H "X-Signature-SHA256: sha256=<computed_hmac>" \
  -d '{
    "transactionId": "TX-001",
    "idempotencyKey": "idemp-abc123",
    "dispenseRequestId": "<request-guid>",
    "amount": 15.50,
    "currency": "EUR",
    "status": "confirmed",
    "provider": "redsys",
    "timestamp": "2024-01-01T12:00:00Z"
  }'
```

### SignalR Hub — `wss://localhost:8001/hubs/dispensers`
| Direction | Method | Description |
|---|---|---|
| Machine → Server | `SendHeartbeat(machineId)` | Liveness ping |
| Machine → Server | `ReportInventory(machineId, count)` | Inventory update |
| Machine → Server | `ConfirmDispense(machineId, requestId)` | Dispense ACK |
| Machine → Server | `ReportDispenseFailure(machineId, requestId, error)` | Dispense error |
| Server → Machine | `ReceiveCommand(command)` | Dispense / Cancel / Reboot |
| Server → Dashboard | `MachineStateUpdated(state)` | Real-time state push |

---

## 🧪 Testing

```bash
# Run all unit tests
dotnet test tests/SmartDispenser.UnitTests/ -v normal

# With coverage report
dotnet test tests/SmartDispenser.UnitTests/ --collect:"XPlat Code Coverage"
```

> Tests use Moq to mock all infrastructure (DB, SignalR, HTTP). No network or DB required.

---

## ⚖️ Trade-offs & Technical Decisions

### 1. Outbox Pattern vs. Fire-and-Forget for Webhook Events

**Decision:** Domain events (DispenseCompleted, PaymentConfirmed) are written to an `outbox_messages` table **in the same DB transaction** as the business write, then delivered by a background processor.

**Why:** "Fire-and-forget" async delivery (e.g., send HTTP webhook inside a request handler) fails silently if the server crashes between the DB commit and the HTTP call. The Outbox Pattern guarantees **at-least-once delivery**: if the processor crashes mid-delivery, on restart it finds the message still `Pending` and retries.

**Trade-off:** This introduces a delivery delay (up to 5s polling interval) and eventual consistency. For prescription dispensing, this is acceptable — the machine receives its command synchronously via SignalR; the outbox is for *notification* of external systems (EHR, patient app), not for the core dispense flow.

---

### 2. Polly `StandardResilienceHandler` vs. Manual Policies

**Decision:** Used `Microsoft.Extensions.Http.Resilience`'s `AddStandardResilienceHandler` (Polly v8 built-in for .NET 8+) instead of manually composing `Policy.WrapAsync(retry, circuitBreaker, timeout)`.

**Why:** The standard handler has sane defaults and is correctly ordered (timeout wraps retry wraps circuit breaker). Manual composition is error-prone — wrapping in the wrong order causes unexpected behavior (e.g., timeout applies per-total instead of per-attempt).

**Trade-off:** Less control over individual policy parameters compared to fully custom pipelines. For the HealthTech API integration, the defaults are appropriate (3 retries, 2s exponential backoff, 50% failure ratio for circuit breaker).

---

### 3. MediatR CQRS vs. Direct Service Calls

**Decision:** All business operations go through MediatR `IRequest<T>` + `IRequestHandler<T>` instead of injecting services directly into controllers.

**Why:** Enables adding cross-cutting concerns (logging, validation, caching) via MediatR Pipeline Behaviors without modifying handlers. Each handler is a self-contained class that can be unit tested with just its constructor dependencies. Controllers become thin orchestrators.

**Trade-off:** Adds a layer of indirection. For CRUD operations without business logic, direct service calls would be simpler. MediatR pays off when you need to chain commands (payment confirmed → trigger dispense), add audit trails as behaviors, or scale handler complexity independently.

---

### 4. Blazor Server vs. Blazor WebAssembly for Dashboard

**Decision:** Blazor Server (Interactive Server Components) hosted within the same ASP.NET Core process, not a separate WASM app.

**Why:** Blazor Server has a native SignalR connection back to the server, making real-time updates trivial. WASM would require: (1) a separate project, (2) exposing SignalR through CORS, (3) a public API for all dashboard queries, and (4) significant JS interop for WebSocket handling. For a monitoring dashboard with no offline requirement, Server wins.

**Trade-off:** Every dashboard user requires a persistent SignalR connection to the server. With 1,000 concurrent dashboard users, this is 1,000 open WebSocket connections. For a hospital operations dashboard with tens of concurrent users, this is entirely acceptable.

---

### 5. Machine Identity via Query Parameter vs. JWT in Production

**Decision:** For the portfolio demo, machines authenticate to the SignalR hub via a `?machineId=` query parameter. Production-ready approach: machines hold a device certificate or a long-lived JWT with `sub=machineId`.

**Why:** Device authentication is complex (certificate provisioning, rotation, revocation). The query parameter approach lets the demo run without a PKI infrastructure while the architecture clearly shows where auth would hook in (`OnConnectedAsync`).

**Trade-off:** Without authentication, any client can impersonate a machine by spoofing its ID. The production fix is a single `Context.User.FindFirst("sub")` call in `OnConnectedAsync`, backed by a JWT middleware registered before `MapHub`.

---

## 📄 License

MIT — see [LICENSE](LICENSE) for details.
