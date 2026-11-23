# Contributing to Qorra

Thanks for helping out. Bug reports, fixes, and focused features are all welcome. For anything large, open an issue first so we can agree on the approach before you write the code.

## Setup

- Windows 10/11 with the [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker Desktop, for the integration tests (optional)

```powershell
dotnet build Qorra.Windows.slnx                   # must finish with 0 warnings
dotnet test --project tests/DbClient.Tests        # integration tests skip when no database is running
dotnet run --project src/DbClient.App
```

Integration tests need the test databases:

```powershell
docker compose up -d --wait                       # PostgreSQL (55432), MySQL (53306), SSH (52222)
docker compose --profile mssql up -d --wait       # adds SQL Server (11433), ~1.5 GB image
```

Test credentials: user `dbclient`, password `dbclient_pw`, database `dbclient_test`. SQL Server: `sa` / `DbClient_pw1!`.
If a port fails to bind, Windows may have reserved it: `netsh int ipv4 show excludedportrange protocol=tcp`.

To try the app without touching your own connections, point it at a scratch folder: `$env:DBCLIENT_DATA_DIR = "C:\temp\qorra"`.

## Project layout

```
src/
  DbClient.Core/              net10.0, no UI. Drivers contract, DatabaseConnection, SQL parsing/completion/formatting, DDL, stores
  DbClient.Drivers.Postgres/  Npgsql
  DbClient.Drivers.MySql/     MySqlConnector
  DbClient.Drivers.Sqlite/    Microsoft.Data.Sqlite
  DbClient.Drivers.SqlServer/ Microsoft.Data.SqlClient
  DbClient.ViewModels/        net10.0, CommunityToolkit.Mvvm. Shared by the Windows app and the planned macOS app
  DbClient.App/               WPF app: Views, Themes, Controls, Windows-specific services
tests/DbClient.Tests/         xUnit v3: Core, Drivers, Integration
installer/windows/            Inno Setup script
```

Dependencies point one way: `App -> ViewModels -> Core` and `App -> Drivers -> Core`. Core knows no driver.
`App.xaml.cs` wires everything together by hand, with no DI container.

Key ideas:

- **One connection class.** Every engine is ADO.NET, so `DatabaseConnection` handles all of them. A driver only supplies a
  `SqlDialect` (catalog queries, quoting, paging, parameters). New capabilities go into `DatabaseConnection` plus the dialects,
  not into new interfaces.
- **ViewModels are UI-free.** The project targets plain `net10.0`, so WPF types can't leak in. OS services (clipboard, dialogs,
  Windows Hello) come in through delegates in `Platform`. Filtering and sorting are exposed as predicates and comparers; the
  WPF views wrap them in `ListCollectionView`.
- **Testable logic lives in Core.** SQL splitting, autocomplete, formatting, and DDL generation are tested without any UI.

## Rules that matter

**Performance**

1. Never load a whole table. Results stream in chunks of about 500 rows through `IAsyncEnumerable`.
2. Grids stay virtualized.
3. All database I/O is async and takes a `CancellationToken`.
4. Never block the UI thread. Long work shows a running state and a Stop button.
5. Schema metadata loads lazily when a node opens, then is cached.
6. Cold start stays under one second. Don't touch Npgsql, MySqlConnector, or SQLite types in driver constructors or fields,
   so their assemblies load only when used.

**Security**

- Secrets never go into `ConnectionProfile`. They are stored in `credentials.json`, encrypted with DPAPI.
- Build connection strings with the driver's `ConnectionStringBuilder`, never by concatenation.
- Queries that change data always use parameters.
- The error log records exception types, SQLSTATE, and stack traces only. Messages can contain SQL, values, or credentials.
- Files are written atomically through `AppPaths.WriteAtomic`.

**Code style**

- `TreatWarningsAsErrors` is on. The build must have zero warnings.
- MVVM with `[ObservableProperty]` on partial properties and `[RelayCommand]` (async with `CancellationToken` when cancelable).
- Simple code over abstractions. No interface for a single implementation.
- UI uses the design tokens in `Themes/` through `DynamicResource` (`Brush.*`, `Radius.*`, `Font.*`, `Text.*`). No hard-coded colors.
  Animations use opacity or transforms only, 100–180 ms.
- UI text is short. Labels and tooltips, not explanatory sentences.
- Ask in an issue before adding a NuGet package.

## Pull requests

- Keep each PR to one change, with a test when the logic is non-trivial.
- Run the build and the tests before pushing. CI runs both on Windows and macOS.
- Describe what changed and how you checked it. Screenshots help for UI changes.

By contributing, you agree that your work is licensed under the [MIT License](LICENSE).

## Release

```powershell
.\build.ps1    # dotnet publish (self-contained win-x64, ReadyToRun, trimmed) + Inno Setup
```

The installer lands in `artifacts/installer/`. Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).
