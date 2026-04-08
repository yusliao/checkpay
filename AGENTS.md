# Repository Guidelines

## Project Structure & Module Organization
CheckPay.slnx anchors the .NET 10 solution. `src/CheckPay.Web` handles Blazor UI, `src/CheckPay.Application` hosts CQRS handlers, `src/CheckPay.Domain` holds entities and enums, `src/CheckPay.Infrastructure` manages EF Core plus Azure clients, and `src/CheckPay.Worker` runs OCR pipelines. Tests stay in `tests/CheckPay.Tests` with Business/Domain/Infrastructure folders mirroring the production tree. Blueprint docs sit under `docs/`, deployment defaults live in `railway.json`, and `temp/` is the only place disposable files belong.

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
Keep secrets in user-secrets or environment variables and scrub `appsettings.Development.json` before sharing logs. When adding Azure or Railway configuration, list the required keys in `docs/CLAUDE.md` with redacted placeholders, and never ship temporary data outside `temp/`.

## Cursor Cloud specific instructions

### Required services
| Service | How to start | Default port |
|---|---|---|
| PostgreSQL 16 | `sudo pg_ctlcluster 16 main start` | 5432 |
| MinIO | `MINIO_ROOT_USER=minioadmin MINIO_ROOT_PASSWORD=minioadmin minio server /tmp/minio-data --console-address ":9001" --address ":9000"` | 9000 (API), 9001 (console) |
| CheckPay Web | `ConnectionStrings__DefaultConnection="Host=localhost;Database=checkpay;Username=admin;Password=admin123" ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/CheckPay.Web` | 5000 |

### Database setup (one-time, already done via update script)
The PostgreSQL user `admin` / `admin123` and database `checkpay` are created by the update script. The app auto-runs EF Core migrations and seeds default users on startup.

### Key gotchas
- The default `appsettings.json` connection string uses `Password=admin` which does not match the docker-compose/update-script default of `admin123`. Override via env var `ConnectionStrings__DefaultConnection` as shown above.
- OCR services (Hunyuan, Azure) fall back to mock implementations when credentials are not configured — the app starts and runs fine without them.
- MinIO is required for realistic file upload/download testing. Without it, `MockBlobStorageService` provides fake URLs.
- The OcrWorker runs in-process as a hosted service inside `CheckPay.Web` — no separate Worker process is needed for development.
- Build/test/lint commands are documented in the "Build, Test & Development Commands" section above.
