# Contributing

Thanks for taking the time to improve fx-bitemporal.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (the integration tests start PostgreSQL 17 through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property, golden and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required: `feat:`, `fix:`, `docs:`, `test:`,
`build:`, `ci:`, `refactor:`, `perf:`, `chore:`; `feat!:` or a `BREAKING CHANGE:` footer for breaking changes. The
CHANGELOG is written by hand from them at release time.

## Changing temporal behaviour

The planner (`AssertionPlanner`, `BatchPlanner`, `ObservationPolicy`) is the contract between the in-memory model and
the PostgreSQL store. A change to it:

1. needs a scenario test in `tests/FxBitemporal.UnitTests/Temporal/BitemporalScenarioTests.cs` that a reader can check
   by hand;
2. must keep the FsCheck properties green (no bitemporal overlap, every past answer reproducible, idempotent replay,
   batch equals sequential) and the model-agreement integration test;
3. must not change any answer as of a known time that has already passed. If it would, it is not a refactoring but a
   data migration, and needs an ADR.

## Reports and golden files

The revaluation report's bytes are part of the API contract (see `docs/adr/0005-canonical-reports-with-content-hash.md`).
If a golden test fails, do not regenerate the file until you have understood why the bytes changed.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one. Do not edit the old ADR in
place. Use the existing files as the format (MADR: context, options considered, decision, consequences).

## Database migrations

Migrations are numbered SQL files in `src/FxBitemporal.Api/Persistence/Migrations/` (`NNNN_name.sql`), embedded and
applied by `--migrate-only` as the owner role. Never edit a migration that has been applied anywhere: the runner
compares checksums and refuses to continue. Add a new, higher-numbered file instead.
