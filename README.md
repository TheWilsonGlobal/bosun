# Bosun

> Engineered by **[TheWilsonGlobal (Wilson Pham)](https://github.com/TheWilsonGlobal)**

An AI-assisted configurator for ship automation systems: engineers edit alarm and I/O configuration for a vessel in a fast data grid, describe changes in plain language to an assistant that drafts them, import customer I/O lists from PDF, ask questions about the project's documents, and watch the result live on a simulated engine room.

Bosun is a fictional demo product. It is a portfolio project built to show a .NET + React + PostgreSQL-style stack with practical LLM integration; it uses no real vessel data and no third-party branding.

## What it does

| Area              | What you get                                                                                                                                                                                                          |
| ----------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Configurator      | A virtualised data grid over about 5,000 tags (server paging, filters, inline edit). Every edit, by a person or the assistant, becomes a **change set** that is reviewed as a diff and approved before it is applied. |
| Assistant (agent) | Describe a change in natural language. A plan, act, verify loop calls tools against the configurator API and builds a **draft** change set. It cannot apply anything: a human approves.                               |
| PDF import        | Extracts tags from variable-layout customer I/O lists (text and scanned pages) into structured rows, checks them against business rules, scores confidence per field and flags rows for review.                       |
| Ask               | Answers questions about the project's documents with citations (hybrid vector and keyword search). Refuses when the answer is not in the documents.                                                                   |
| Live monitoring   | A simulator speaks **Modbus TCP** and **NMEA 0183**; the backend polls it, evaluates alarm limits with delay and hysteresis, and pushes values and alarms over **SignalR** to a mimic screen and an alarm list.       |
| Audit             | Every applied change records who approved it, which model proposed it and with which prompt version.                                                                                                                  |

## Stack

- **Backend:** .NET 10, ASP.NET Core minimal APIs, EF Core 10, SQLite (provider-neutral so PostgreSQL can be added), SignalR
- **Frontend:** React 19, TypeScript (strict), Vite, Tailwind CSS, shadcn/ui, TanStack Table, TanStack Query
- **AI:** Google Gemini over a thin REST client behind an `ILlmClient` port; structured JSON output, retries, cost logging. Embeddings are 768-dimension vectors.
- **Tests:** xUnit (real SQLite files, never the EF in-memory provider), Vitest
- **Tooling:** pnpm workspace for the JS apps, ESLint and Prettier

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

- [docs/running-from-the-repo.md](docs/running-from-the-repo.md): where settings come from, key errors
- [docs/monitoring.md](docs/monitoring.md): the alarm model (states, delay, hysteresis, limit changes)

## Status and known limits

Built and tested: the configurator, change sets and audit, LLM foundation, the agent, PDF extraction, document Q&A and live monitoring.

Not built: the admin console (a placeholder page), an MCP server, PostgreSQL and pgvector, Docker and Linux deployment files, CI, and the architecture decision records.

Things to know:

- Eval results are recorded in `evals/results/`. LLM scores depend on the model and the key's quota, so re-run them rather than trusting old numbers.
- Extraction confidence is weakly calibrated on scanned pages: some wrong rows are auto-accepted.
- Cost figures use a placeholder price table and are estimates.
- Any signed-in user can call Ask, reindex and acknowledge alarms, and the Development environment allows anonymous reads. Fine for a demo, not for production.
- The frontend bundle is about 740 kB with no code splitting.

---

## Author & Credits

- **Engineered by**: [Wilson Pham (@TheWilsonGlobal)](https://github.com/TheWilsonGlobal)
- **GitHub Profile**: [https://github.com/TheWilsonGlobal](https://github.com/TheWilsonGlobal)
