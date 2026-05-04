# Repository Guidelines

## Project Structure & Module Organization
CheckPay.slnx anchors the .NET 10 solution. `src/CheckPay.Web` handles Blazor UI and hosts the in-process **OCR queue** (`OcrWorker` as a `HostedService`), `src/CheckPay.Application` hosts CQRS handlers, `src/CheckPay.Domain` holds entities and enums, `src/CheckPay.Infrastructure` manages EF Core plus Azure Vision OCR and storage clients. The standalone `src/CheckPay.Worker` console project is a lightweight host template and is **not** required for cheque OCR in the default Web setup. Tests stay in `tests/CheckPay.Tests` with Business/Domain/Infrastructure folders mirroring the production tree. Blueprint docs sit under `docs/`. Prefer `docker-compose.yml` for integrated local/production-like runs; `temp/` is the only place disposable files belong.

## Documentation (required with code changes)
Whenever you change behavior, configuration, deployment, env vars, or user-visible flows, update the same change set’s documentation and examples: root `README.md`, `CLAUDE.md` (changelog + affected sections), this `AGENTS.md` if guidelines shift, plus `docs/` when design docs are impacted, and `docker-compose.yml` / `.env.example` / `appsettings*.json` comments or placeholders as appropriate. Repository rule: `.cursor/rules/sync-docs.mdc` (`alwaysApply`). Do not wait for the user to ask for doc updates.

## Build, Test & Development Commands
- `dotnet restore` then `dotnet build CheckPay.slnx -c Release` before any PR.
- Apply migrations with `dotnet ef database update --project src/CheckPay.Infrastructure --startup-project src/CheckPay.Web`.
- Launch the UI via `dotnet run --project src/CheckPay.Web` (add `dotnet watch run` when iterating). Cheque OCR runs inside this process; use `dotnet run --project src/CheckPay.Worker` only if you explicitly rely on the separate worker host.
- Validate changes with `dotnet test tests/CheckPay.Tests/CheckPay.Tests.csproj --collect:"XPlat Code Coverage"` and paste the summary into your PR.

## Coding Style & Naming Conventions
Stick to C# 10, four-space indentation, nullable reference types, and implicit usings. Classes and public members remain `PascalCase`, locals stay `camelCase`, interfaces keep the `I` prefix, and async methods end with `Async`. Keep each file focused on a single responsibility, prefer guard clauses to deep nesting, reuse abstractions from `CheckPay.Application`, and run `dotnet format --no-restore` before committing.

## Testing Guidelines
xUnit is the accepted framework; store specs beside their feature under `tests/CheckPay.Tests/<Area>/<Feature>Tests.cs`. Name tests `MethodUnderTest_State_Result`, mock Azure clients or DbContext so runs stay deterministic, and cover both success and failure scenarios whenever money movement or OCR parsing is involved. Maintain or increase the prior coverage signal (~60 passing specs) and update fixtures whenever DTOs shift.

When changing OCR amount-validation logic, include parser/normalization tests (e.g., written amount text to decimal) and failure-path tests (`FailOpen` behavior) in the same change set. When changing **check submit → training sample** behavior (`SubmitCheckOcrTrainingSampleFactory` / `CheckSubmitOcrTrainingSamplePageHelper` / `Ocr:Training:*`), extend `tests/CheckPay.Tests/Application/SubmitCheckOcrTrainingSampleFactoryTests.cs` and `tests/CheckPay.Tests/Web/CheckSubmitOcrTrainingSamplePageHelperTests.cs` (diff gating, dedup, template binding). MICR/ABA routing, IBAN mod-97, and optional `prebuilt-check.us` merge are covered in `tests/CheckPay.Tests/Infrastructure/CheckOcrRoutingMicrEuTests.cs`—extend there when touching `CheckOcrVisionReadParser`, `CheckOcrEuInstrumentParser`, or `AzureOcrService` primary-path fusion. Vision-weak amount fallback (`ShouldInvokeDiAmountFallback`) is covered in `tests/CheckPay.Tests/Infrastructure/AzureOcrServiceTests.cs`.

## Commit & Pull Request Guidelines
Changelog entries in `CLAUDE.md` show the expected brevity, so commit subjects follow `<scope>: <imperative summary>` such as `Infrastructure: tighten blob ACL`. Bodies must explain motivation plus impact and reference issue IDs. PRs need a narrative description, testing log, screenshots for UI tweaks, a list of new migrations or settings, and **documentation updates** (`README.md`, `CLAUDE.md`, `AGENTS.md`, `docs/`, `.env.example`, compose files) bundled with any behavior or ops change—see `.cursor/rules/sync-docs.mdc`.

## Security & Configuration Tips
Keep secrets in user-secrets or environment variables and scrub `appsettings.Development.json` before sharing logs. When adding cloud OCR/storage configuration, document required keys in `CLAUDE.md` or `README.md` with redacted placeholders, and never ship temporary data outside `temp/`. Handwritten amount validation calls **Document Intelligence** model **`prebuilt-check.us`** (v4 REST); if the Vision key is Computer Vision–only, set optional `Azure:DocumentIntelligence:DocumentAnalysisEndpoint` / `DocumentAnalysisApiKey` (or `AZURE_DOCUMENT_INTELLIGENCE_*` in Compose) to a DI resource’s endpoint and key.
