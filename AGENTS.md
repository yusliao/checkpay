# Repository Guidelines

## Project Structure & Module Organization
CheckPay.slnx anchors the .NET 10 solution. `src/CheckPay.Web` handles Blazor UI, `src/CheckPay.Application` hosts CQRS handlers, `src/CheckPay.Domain` holds entities and enums, `src/CheckPay.Infrastructure` manages EF Core plus Azure clients, and `src/CheckPay.Worker` runs OCR pipelines. Tests stay in `tests/CheckPay.Tests` with Business/Domain/Infrastructure folders mirroring the production tree. Blueprint docs sit under `docs/`. Prefer `docker-compose.yml` for integrated local/production-like runs; `temp/` is the only place disposable files belong.

## Build, Test & Development Commands
- `dotnet restore` then `dotnet build CheckPay.slnx -c Release` before any PR.
- Apply migrations with `dotnet ef database update --project src/CheckPay.Infrastructure --startup-project src/CheckPay.Web`.
- Launch the UI via `dotnet run --project src/CheckPay.Web` (add `dotnet watch run` when iterating) and start background processing with `dotnet run --project src/CheckPay.Worker`.
- Validate changes with `dotnet test tests/CheckPay.Tests/CheckPay.Tests.csproj --collect:"XPlat Code Coverage"` and paste the summary into your PR.

## Coding Style & Naming Conventions
Stick to C# 10, four-space indentation, nullable reference types, and implicit usings. Classes and public members remain `PascalCase`, locals stay `camelCase`, interfaces keep the `I` prefix, and async methods end with `Async`. Keep each file focused on a single responsibility, prefer guard clauses to deep nesting, reuse abstractions from `CheckPay.Application`, and run `dotnet format --no-restore` before committing.

## Testing Guidelines
xUnit is the accepted framework; store specs beside their feature under `tests/CheckPay.Tests/<Area>/<Feature>Tests.cs`. Name tests `MethodUnderTest_State_Result`, mock Azure clients or DbContext so runs stay deterministic, and cover both success and failure scenarios whenever money movement or OCR parsing is involved. Maintain or increase the prior coverage signal (~60 passing specs) and update fixtures whenever DTOs shift.

## Commit & Pull Request Guidelines
Changelog entries in `CLAUDE.md` show the expected brevity, so commit subjects follow `<scope>: <imperative summary>` such as `Infrastructure: tighten blob ACL`. Bodies must explain motivation plus impact and reference issue IDs. PRs need a narrative description, testing log, screenshots for UI tweaks, a list of new migrations or settings, and updates to `README.md` or `docs/` whenever behavior changes.

## Security & Configuration Tips
Keep secrets in user-secrets or environment variables and scrub `appsettings.Development.json` before sharing logs. When adding cloud OCR/storage configuration, document required keys in `CLAUDE.md` or `README.md` with redacted placeholders, and never ship temporary data outside `temp/`.
