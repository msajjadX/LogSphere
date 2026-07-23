# Contributing to LogSphere

Thanks for your interest! Contributions of all kinds are welcome — bug reports, docs, tests, features.

## Getting started

1. Fork and clone the repo.
2. Backend: .NET 10 SDK → `cd backend && dotnet build && dotnet test` (98 tests, no database needed).
3. Frontend: Node 22 → `cd frontend && npm ci && npm run build` (includes type-checking).
4. Full stack locally: `cd deploy && cp .env.example .env` (edit secrets) → `docker compose -f docker-compose.bundled-db.yml up -d --build` → http://localhost:8080.

## Pull requests

- Keep PRs focused — one change per PR.
- `dotnet test` and `npm run build` must pass; add tests for new behavior.
- Match the existing code style (file-scoped namespaces, primary constructors, the JSON envelope conventions in `docs/API.md`).
- Database changes go in a **new** numbered file under `db/migrations/` — never edit an applied migration.
- By contributing, you agree your contributions are licensed under the Apache License 2.0.
- **Sign your commits (DCO).** Every commit must carry a `Signed-off-by` line certifying the
  [Developer Certificate of Origin](https://developercertificate.org/) — just commit with
  `git commit -s`. This certifies you have the right to submit the code under the project license
  (and keeps future licensing options clean for the project).

## Reporting bugs

Open an issue with: what you did, what you expected, what happened, and relevant log output
(from `docker logs logsphere-api` — please redact secrets and private data).

## Security issues

Please do NOT open public issues for vulnerabilities — see [SECURITY.md](SECURITY.md).
