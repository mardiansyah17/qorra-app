# Qorra

Database client for Windows. Postgres, MySQL, SQL Server and SQLite.

I wanted something like TablePlus that starts fast and doesn't choke on big tables, so I wrote one. It's WPF on .NET 10.
A macOS version is planned (shares everything except the UI).

What it does:

- browse schemas, open tables, edit rows and save them in one transaction
- SQL editor with autocomplete, error checking and formatting
- table designer (columns, indexes, foreign keys)
- SSH tunnels and SSL
- export to CSV / JSON / SQL, import connections from DBeaver, DataGrip, HeidiSQL

MariaDB should work through the MySQL driver but I haven't tested it much.

## Install

Grab the installer from [Releases](../../releases). Windows 10 1809+ x64.

## Build

```
dotnet build Qorra.Windows.slnx
dotnet run --project src/DbClient.App
```

Needs the .NET 10 SDK. More in [CONTRIBUTING.md](CONTRIBUTING.md).

The projects are still called `DbClient.*`, that was the working name before Qorra.

## License

MIT
