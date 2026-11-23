# Contributing

PRs welcome. If it's something big, open an issue first so we don't both waste time.

## Running it

```
dotnet build Qorra.Windows.slnx
dotnet test --project tests/DbClient.Tests
```

The build treats warnings as errors, so keep it at 0.

Integration tests skip themselves when there's no database. To run them:

```
docker compose up -d --wait
docker compose --profile mssql up -d --wait   # SQL Server too, big image
```

Credentials are in `docker-compose.yml` (test only). If a port won't bind, Windows has probably reserved it,
check `netsh int ipv4 show excludedportrange protocol=tcp`.

Set `DBCLIENT_DATA_DIR` to a temp folder if you don't want to mess with your real connections while testing.

## Where things are

- `DbClient.Core` - connection, SQL parsing/autocomplete/formatting, DDL, settings and stores. No UI.
- `DbClient.Drivers.*` - one per engine, mostly just the SQL dialect.
- `DbClient.ViewModels` - plain net10.0 so the Mac app can reuse it. No WPF in here.
- `DbClient.App` - the WPF app.

All engines go through ADO.NET, so there's one `DatabaseConnection` class and each driver gives it a `SqlDialect`.
If you're adding a feature that needs engine-specific SQL, put it in the dialects.

A few things I care about:

- don't load whole tables, results are streamed in pages
- don't block the UI thread
- passwords stay in the credential store, never in the profile
- use parameters for anything that writes data
- use the theme brushes (`Brush.*` etc.), no hard-coded colors
- ask before adding a NuGet package

## Releasing

1. Bump `<Version>` in `Directory.Build.props`, commit and push.
2. `git tag vX.Y.Z && git push origin vX.Y.Z`. The release workflow runs the tests, builds the installer and
   publishes the release with generated notes.
3. Tidy up the notes on GitHub.

`build.ps1` builds the same installer locally (needs Inno Setup 6) into `artifacts/installer`.

The app looks for `Qorra-Setup-*.exe` in the latest release, and the website links to `releases/latest/download/Qorra-Setup.exe`. The workflow uploads both.
