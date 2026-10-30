# Bosun

> Engineered by **[TheWilsonGlobal (Wilson Pham)](https://github.com/TheWilsonGlobal)**  
> **Live Demo**: [https://bosun.wnshare.one](https://bosun.wnshare.one) (Mirror: [https://bosun-mbzr.onrender.com](https://bosun-mbzr.onrender.com))

An AI-assisted configurator for ship automation systems: engineers edit alarm and I/O configuration for a vessel in a fast data grid, describe changes in plain language to an assistant that drafts them, import customer I/O lists from PDF, ask questions about the project's documents, and watch the result live on a simulated engine room.

Bosun is a portfolio showcase project built to demonstrate a modern industrial .NET 10 + React 19 architecture with practical LLM integration, deterministic rule verification, and high-frequency SCADA telemetry. It uses synthetic maritime data and no third-party branding.

## What it does

| Area              | What you get                                                                                                                                                                                                          |
| ----------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Configurator      | Virtualized `DataTable` over ~5,000 tags with sticky header, pinned footer showing range indicators, inside-body scrolling, and inline edits. Every edit becomes a **change set** reviewed as a diff before application. |
| Assistant (agent) | Describe a change in natural language. A plan-act-verify-repair loop calls tools against the configurator API and builds a **draft** change set. Cannot directly apply anything: a human approves. Chat history features inline run expansion and step inspection. |
| Tool Registry & Telemetry | Live diagnostic view in Settings (`/settings`) detailing the **11 registered agent tools** (Read, Write/Propose, Verify) with parameter signatures, safety constraints, and sequential run step/payload inspection. |
| PDF import        | Extracts tags from variable-layout customer I/O lists (text and scanned pages) into structured rows, checks them against business rules, scores confidence per field, and flags anomalies for review.                 |
| Ask (RAG)         | Answers questions about vessel documents with citations using hybrid dense vector + FTS5 BM25 search. Strictly refuses when answers are unsupported by documentation.                                                |
| Live monitoring   | A simulator speaks **Modbus TCP** and **NMEA 0183**; backend polls it, evaluates alarm limits with sample-driven delay and hysteresis, and broadcasts values and alarms over **SignalR** to an interactive mimic screen and alarm list. |
| Observability     | **Prometheus metrics (`/metrics`)** with Bearer token authentication, tracking HTTP rates/latencies, live connection gauges (`bosun_link_connected`), active tags (`bosun_live_tags`), and LLM usage. Dedicated DB-backed **Readiness Probe (`/ready`)**. |
| Audit             | Immutable audit ledger where each applied change records who approved it, which model proposed it, with which prompt version, and a compact 1-line view with click-to-open detail modal diffs.                     |

## Stack

- **Backend:** .NET 10, ASP.NET Core minimal APIs, EF Core 10, SQLite (WAL mode, provider-neutral for PostgreSQL), SignalR, `prometheus-net.AspNetCore` 8.2.1
- **Frontend:** React 19, TypeScript (strict), Vite, Tailwind CSS, shadcn/ui, TanStack Table, TanStack Query
- **AI:** Google Gemini (`gemini-3.5-flash-lite`, `gemini-embedding-2`) behind an `ILlmClient` port; structured JSON output, self-repair loops, token logging
- **Deployment:** Multi-stage `Dockerfile`, `docker-entrypoint.sh`, `render.yaml` Blueprint with persistent disk (`/app/data`), custom domain with Cloudflare SSL
- **Tests:** 967 .NET tests (100% branch coverage on rules engine) + 305 frontend tests (**1,272+ automated tests total**)
- **Tooling:** pnpm workspace, ESLint, Prettier, `dotnet format`

## Quick start

You need the **.NET SDK 10**, **Node 24** and **pnpm**. Docker is not needed.

```bash
pnpm install
cp .env.example .env     # then fill in the two Gemini keys
pnpm run db:reset        # creates and seeds data/db/bosun.db (about 5,000 tags)
pnpm run dev             # API + frontend + admin + simulator
```

Run these from inside the repository: the API finds the root `.env` by walking up to `Bosun.sln`, and the database path in `.env` is resolved from the repository root, whatever your current directory is.

| Service                 | URL / port                                                           |
| ----------------------- | -------------------------------------------------------------------- |
| API                     | http://127.0.0.1:5480 (API reference at `/scalar`, Development only) |
| Configurator (frontend) | http://127.0.0.1:5481                                                |
| Admin                   | http://127.0.0.1:5482/admin/ (placeholder page for now)              |
| Simulator Modbus / NMEA | 5483 / 5484 (skipped if those ports are already in use)              |

Use `127.0.0.1` rather than `localhost`: on some Windows machines the IPv6 fallback adds a noticeable delay to every connection.

### Signing in

| User       | Password                                                 | Role                                                          |
| ---------- | -------------------------------------------------------- | ------------------------------------------------------------- |
| `admin`    | `ADMIN_PASSWORD` from `.env` (`admin123` in the example) | admin                                                         |
| `approver` | `approver-dev-only`                                      | second approver for SIL/ESD change sets, **Development only** |

Outside Development, set `APPROVER_USERNAME` / `APPROVER_PASSWORD` and a real `ADMIN_PASSWORD`; the example defaults are for local use only.

## Configuration

One root `.env` (git-ignored; `.env.example` documents every variable). Real environment variables override it, so a stale variable in your shell can silently beat the file.

| Variable                                          | Purpose                                      |
| ------------------------------------------------- | -------------------------------------------- |
| `LLM_PROVIDER`, `LLM_MODEL`, `GEMINI_LLM_API_KEY` | chat, agent, extraction and Ask              |
| `EMBEDDING_MODEL`, `GEMINI_EMBEDDING_API_KEY`     | knowledge-base search (`EMBEDDING_DIMS=768`) |
| `DB_PROVIDER`, `DB_CONNECTION`                    | `sqlite` and `Data Source=data/db/bosun.db`     |
| `METRICS_TOKEN`                                   | Bearer auth token for `/metrics` endpoint    |
| `BACKEND_PORT`, `FRONTEND_PORT`, `ADMIN_PORT`     | service ports                                |
| `SIM_MODBUS_PORT`, `SIM_NMEA_PORT`                | simulator ports                              |

`MCP_PORT` and `MCP_TOKEN` are reserved for an MCP server that is not built yet.

The backend reads `.env` once at startup, so **restart it after changing a key**. If the AI features fail, the error names the variable that was rejected; see [docs/running-from-the-repo.md](docs/running-from-the-repo.md) for what each error means (for example, a key whose Google Cloud project is suspended cannot be fixed by swapping in another key from the same project).

## Commands

| Command                                     | What it does                                                       |
| ------------------------------------------- | ------------------------------------------------------------------ |
| `pnpm run dev`                              | start API, frontend, admin and simulator together                  |
| `pnpm run dev:backend` / `pnpm run sim`     | start only the API / only the simulator                            |
| `pnpm run test`                             | all .NET tests, then the frontend and admin tests                  |
| `pnpm run test:coverage`                    | rules-engine tests with a 100% branch-coverage threshold           |
| `pnpm run test:live`                        | tests that call the real Gemini API                                |
| `pnpm run evals -- --suite agent --runs 3`  | LLM evals: `agent`, `extraction`, `rag`, `safety` (**cost money**) |
| `pnpm run db:reset`                         | recreate and seed the database                                     |
| `pnpm run kb:index`                         | (re)index the knowledge-base documents                             |
| `pnpm run fixtures:generate`                | regenerate the extraction test PDFs                                |
| `pnpm run lint` / `format` / `format:check` | ESLint, Prettier, `dotnet format`                                  |
| `pnpm run build`                            | release build of everything                                        |

## How the AI is kept safe

- **The model never applies changes.** There is no apply tool. Applying needs a signed-in human, and a test asserts the agent's tool surface cannot write configuration directly.
- Changes touching SIL/ESD safety tags need a **second approver**.
- Validation is **deterministic C#** (`Bosun.Rules`, 100% branch coverage), not a prompt. The agent reads the rule violations and repairs its draft, up to a limit.
- Every agent run has budgets for tool calls, wall-clock time and tokens, and stops cleanly with a partial draft.
- Text from PDFs and documents is treated as **untrusted data**. The safety eval includes a PDF with a hidden prompt-injection.
- A citation in an answer proves the passage was retrieved for that question, **not** that it supports the sentence. Treat citations as pointers to read.

## Repository layout

```
apps/
  backend/
    src/Bosun.Domain          entities, no ASP.NET or EF references
    src/Bosun.Rules           validation engine (pure C#)
    src/Bosun.Application     use cases, change sets, alarm evaluation, RAG
    src/Bosun.Infrastructure  EF Core, Gemini client, PDF, Modbus/NMEA services
    src/Bosun.Protocols       Modbus TCP client, NMEA 0183 parser
    src/Bosun.Agent           agent loop, tools, guardrails, prompts
    src/Bosun.Extraction      PDF to structured tags pipeline
    src/Bosun.Evals           eval runner
    src/Bosun.Api             ASP.NET Core host: endpoints, SignalR hubs, auth
    src/Bosun.Migrations.Sqlite
    tests/                    one xUnit project per area
  frontend/                   React configurator
  admin/                      React admin SPA (base /admin/), placeholder
  simulator/                  Modbus TCP server and NMEA talker
evals/                        eval cases, fixtures and recorded results
seed/                         synthetic vessel, mimic layout and project documents
docs/                         running from the repo, alarm model
scripts/                      dev, dotnet and demo helpers
```

## Documentation

- [docs/architecture.md](docs/architecture.md): high-level request pipeline, core system modules, SCADA topology, and production deployment
- [docs/agent.md](docs/agent.md): ReAct reasoning loop, state graph, tool registry, write gate, prompt injection defense, and SignalR streaming
- [docs/monitoring.md](docs/monitoring.md): the alarm model (states, delay, hysteresis, limit changes) and Prometheus metrics telemetry
- [docs/running-from-the-repo.md](docs/running-from-the-repo.md): environment variables, key configurations, and error diagnostics

## Status and known limits

**Built, verified & live in production:**
- High-density virtualized `DataTable` with zebra striping, sticky header, and pinned footer.
- Maker-Checker change sets and tamper-evident audit ledger with modal JSON diff inspection.
- Autonomous ReAct Agent with self-healing rule repairs, 11-tool registry, inline run history expansion, and telemetry inspection in Settings.
- PDF I/O table extraction with confidence scoring and injection detection.
- Document Q&A (Ask) with hybrid dense vector + BM25 keyword search and strict ungrounded refusal.
- Industrial Modbus TCP and NMEA 0183 protocol simulation and real-time SignalR telemetry broadcasting.
- Observability: Prometheus `/metrics` exporter, DB-backed `/ready` readiness probe, `/health` endpoint.
- Production Cloud Deployment: Multi-stage Docker image, Render Blueprint with persistent SQLite disk, Cloudflare custom domain.

**Out of scope / deferred:**
- Standalone multi-tenant Admin Console (placeholder SPA built).
- MCP (Model Context Protocol) remote server.
- PostgreSQL 17 + pgvector migration (database abstraction is provider-neutral and ready).

Things to know:

- Eval results are recorded in `evals/results/`. LLM scores depend on the model and the key's quota, so re-run them rather than trusting old numbers.
- Extraction confidence is weakly calibrated on scanned pages: some wrong rows are auto-accepted.
- Cost figures use list prices from `apps/backend/src/Bosun.Api/llm-pricing.json` and are estimates.
- The frontend bundle is about 600 kB minified (184 kB gzip).

---

## Author & Credits

- **Engineered by**: [Wilson Pham (@TheWilsonGlobal)](https://github.com/TheWilsonGlobal)
- **GitHub Profile**: [https://github.com/TheWilsonGlobal](https://github.com/TheWilsonGlobal)
