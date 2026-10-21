# Running the backend from inside the repo

The backend finds its configuration relative to the repository root (the folder that holds `Bosun.sln`), not relative to your current directory. Start it the supported way and the same `.env` is used every time:

```
pnpm run dev:backend      # or: pnpm run dev (backend + frontend + simulator)
```

## Where the settings come from

1. The single root `.env` file. Copy `.env.example` and fill it in. Never commit it.
2. Real environment variables. They win over `.env`, so a stale variable in your shell silently overrides the file. If a key "works in one terminal and not in another", compare `GEMINI_LLM_API_KEY` and `GEMINI_EMBEDDING_API_KEY` in both.
3. `appsettings*.json` and command-line arguments beat both.

## Keys and what the errors mean

| Variable                   | Used for              |
| -------------------------- | --------------------- |
| `GEMINI_LLM_API_KEY`       | chat / agent / /ask   |
| `GEMINI_EMBEDDING_API_KEY` | knowledge base search |

- Empty or missing key: `503 llm_not_configured`. The message names the variable to set.
- `llm_auth_failed` mentioning "rejected the API key": the provider did not accept the key. Fix the variable that the message names (`[rejected key: ...]`).
- `llm_auth_failed` mentioning "denied access for the Google Cloud project": the key is recognised, but its project is blocked, suspended or has the API disabled. Replacing the key with another key from the same project will not help; fix the project in Google AI Studio or Cloud Console, or use a key from a different project.

The database, the embedding cache and the logs live under `data/` in the repo root, wherever you start the process from.
