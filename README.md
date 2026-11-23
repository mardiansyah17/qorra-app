# Qorra

A fast, native database client for Windows. Opens in under a second, streams large tables without loading them, and stays out of your way.

Supports **PostgreSQL**, **MySQL**, **SQL Server**, and **SQLite**. MariaDB works through the MySQL driver but is less tested.

## Features

- **Data grid**: virtualized and streamed in pages, so million-row tables open instantly. Edit cells, insert and delete rows, review the SQL, then save in one transaction.
- **SQL editor**: context-aware autocomplete (joins from foreign keys, aliases, CTEs), live error checking against the server, formatter, explain, run statement at cursor or the whole script.
- **Schema browser**: tables, views, columns, keys, indexes, functions, enums, and sequences, loaded lazily.
- **Table designer**: edit columns, indexes, foreign keys, and checks, with a preview of the generated `ALTER` script.
- **Connections**: SSH tunnels (password, key, or agent), SSL, read-only mode, color tags with safety prompts for production.
- **Workspace**: tabs, filters, and unsaved queries are restored per connection.
- **Import & export**: CSV, JSON, SQL `INSERT`, Markdown; import connections from DBeaver, DataGrip, and HeidiSQL.

Passwords are encrypted with Windows DPAPI and never stored in the connection list.

## Install

Download the latest installer from [Releases](../../releases).

Requires Windows 10 (1809) or later, x64.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Qorra.Windows.slnx
dotnet run --project src/DbClient.App
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for tests, architecture, and the installer build.

## License

[MIT](LICENSE)
