# CLAUDE.md

Agent guide for this repo. Architecture, project layout, and setup live in [README.md](README.md) — read it instead of re-deriving; this file only carries what README does not.

## Commands

```bash
dotnet build one-piece-api.slnx          # build solution
dotnet test                              # xUnit, no external services needed
dotnet run --project src/one-piece-api/one-piece-api.csproj   # interactive query CLI
docker compose -f .devcontainer/docker-compose.yml up -d      # Qdrant + Ollama + model pull
```

Run a single test: `dotnet test --filter FullyQualifiedName~SemanticCacheServiceTests`

## Layout

- `src/one-piece-api/` — the only app project. Namespace root is `OnePieceApi` (not `one-piece-api`).
- `tests/one-piece-api.Tests/` — xUnit. Namespace root is `one_piece_api.Tests`.
- `src/one-piece-api/Data/one_piece_episodes.csv` — 958-row dataset. Do not read it whole; `head` it or grep it.

## Conventions

- .NET 10 (`net10.0`), C# top-level statements in `Program.cs`, nullable + implicit usings enabled.
- 4-space indent, LF, UTF-8, final newline — enforced by `.editorconfig`. Match it.
- Config is bound to records in `Models/ConfigOptions.cs` and registered as singletons; add new settings there rather than reading `IConfiguration` ad hoc.
- DI wiring all happens in `Program.cs` as the composition root.

## Gotchas

- Running the app requires Ollama on `:11434` and Qdrant gRPC on `:6334`. Tests do not — they use `Mocks/MockCacheCollection.cs`. Never start containers just to run tests.
- Qdrant host comes from the `QDRANT_HOST` env var, defaulting to `localhost` (it is `qdrant` inside the devcontainer).
- `Ingestion:RunOnStart` is `true` in `appsettings.json`: every app start **wipes and rebuilds** the SQLite tables and the episode vector collection. Set it to `false` when iterating, and expect a slow first run when it is on.
- SQLite lands in the build output (`bin/.../one_piece.db`), so `dotnet clean` or a rebuild discards ingested data.
- Ignore stale `net9.0` folders under `bin/` — leftovers from before the .NET 10 upgrade.
- One model id (`qwen2.5-coder:1.5b`) serves chat, routing, and embeddings. Changing `Ollama:ModelId` changes embedding dimensions, which invalidates every existing Qdrant collection.

## Boundaries

- Do not commit or push unless asked.
- Do not edit anything under `bin/`, `obj/`, or `.git/`.
- `src/one-piece-api/Retrieval/SqliteDatabaseService.cs` intentionally executes only read-only `SELECT` statements. Keep that restriction when touching SQL paths.
