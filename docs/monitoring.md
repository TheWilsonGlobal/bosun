# Alarm model

The live side raises alarms from Modbus samples against the limits configured in the project (tag alarm limits H, HH, L, LL).

## What an alarm is

- Every limit level of a tag is its own alarm. A tag with H and HH limits can have two open alarms at once; they are raised, acknowledged and cleared independently.
- There is no ISA-18.2 shelving, suppression-by-design, or out-of-service handling. An alarm is either open or closed. If you need those, they are not here.

## States

| State                     | Meaning                                                     |
| ------------------------- | ----------------------------------------------------------- |
| `unacknowledged`          | raised, nobody has acknowledged it, condition still present |
| `acknowledged`            | an operator acknowledged it, condition still present        |
| `returned_unacknowledged` | condition is gone but nobody acknowledged it; stays open    |
| `cleared`                 | closed (acknowledged, and the condition is gone)            |

Transitions: raised -> `unacknowledged`; acknowledge -> `acknowledged`; condition gone while acknowledged -> `cleared`; condition gone while unacknowledged -> `returned_unacknowledged`, and acknowledging that clears it. A condition that comes back after the alarm closed starts a new alarm with a new id.

Events written to the log and pushed to clients: `raised`, `acknowledged`, `returned_to_normal`, `cleared`, `limit_changed`.

## Delay and hysteresis

- The value must be beyond the limit (H/HH above it, L/LL below it; equal to the limit is not in alarm) continuously for `delayS` seconds before the alarm is raised. One sample back inside restarts the delay.
- The delay is measured in the wall-clock time of the samples (their timestamps), not by counting samples.
- A pending delay only counts across an unbroken run of samples. If a tag gets no sample for more than `Monitoring:AlarmPendingMaxGapSeconds` (default 10) or the Modbus link drops, the pending delay is discarded and starts again at the next sample. Active alarms are left alone during a gap; their live values show as stale.
- Once raised, an alarm ends only when the value has moved back by the hysteresis (`limit - hysteresis` for H/HH, `limit + hysteresis` for L/LL).

## Changing a limit while an alarm is active

An applied change set reloads the limits. An active alarm takes the new limit, delay and hysteresis at once (it is judged against them from the next sample), its stored row is updated, and a `limit_changed` event with the old and new numbers in `note` is published and stored. A pending delay restarts. Removing a limit ends its alarm like a return to normal.

## Link problems

- Modbus link down: `disconnected`, all values `stale`.
- Device answers but refuses a register range (mapping mismatch): link `degraded`, only those tags go `stale`, the rest keep flowing.
- A failing SignalR push never affects the link or the alarms; pushes are queued (bounded, oldest dropped) and sent by a separate task, so a slow client cannot block alarm evaluation.

## Observability & Prometheus Metrics

Bosun exports telemetry for external scrapers (Prometheus, VictoriaMetrics, Grafana Agent) via `prometheus-net.AspNetCore` at `/metrics`:

### Authentication

The `/metrics` endpoint is protected by a Bearer token configured via `METRICS_TOKEN` (or `Metrics:Token` in `appsettings.json`). Requests must supply:
`Authorization: Bearer <METRICS_TOKEN>`  
When `METRICS_TOKEN` is unset or empty, `/metrics` returns HTTP 404 to avoid exposing internal metrics by accident.

### Exported Metrics Catalog

| Metric                               | Type      | Labels                                                        | Description                                                                                                       |
| :----------------------------------- | :-------- | :------------------------------------------------------------ | :---------------------------------------------------------------------------------------------------------------- |
| `bosun_link_connected`               | Gauge     | `protocol="modbus\|nmea"`                                     | `1` when connected or degraded, `0` when connecting or disconnected. Series is omitted when protocol is disabled. |
| `bosun_live_tags`                    | Gauge     | None                                                          | Number of live operational tags currently streaming across the telemetry pipeline.                                |
| `bosun_llm_requests`                 | Counter   | `provider`, `model`, `status`                                 | Total count of LLM inference requests made to Gemini or local providers.                                          |
| `bosun_llm_tokens`                   | Counter   | `provider`, `model`, `direction="prompt\|candidate\|thought"` | Total count of tokens consumed across model invocations.                                                          |
| `bosun_llm_request_duration_seconds` | Histogram | `provider`, `model`                                           | Request latency distribution for model inference calls.                                                           |
| `http_request_duration_seconds`      | Histogram | `code`, `method`, `controller`, `action`                      | Standard ASP.NET Core HTTP request duration and error rate metrics.                                               |

### Health & Readiness Probes

| Probe Endpoint                    | Purpose                                           | Criteria                                                                                                                                |
| :-------------------------------- | :------------------------------------------------ | :-------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /health`                     | Liveness check (used by Render and orchestrators) | HTTP 200 when SQLite connection succeeds; 503 if database file is inaccessible. Note: Modbus/NMEA disconnection does not fail liveness. |
| `GET /ready`<br/>`GET /api/ready` | Readiness check (traffic routing & warmup)        | HTTP 200 when SQLite database is responsive AND knowledge base vector index is initialized.                                             |
