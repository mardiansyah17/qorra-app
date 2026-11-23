using DbClient.Core.Models;
using DbClient.Drivers.MySql;
using DbClient.Drivers.Postgres;
using MySqlConnector;
using Npgsql;
using CoreSslMode = DbClient.Core.Models.SslMode;

namespace DbClient.Tests.Drivers;

public sealed class ConnectionStringTests
{
    private const string NastyPassword = "p;a'ss\"w=rd";

    private static ConnectionProfile Profile(CoreSslMode ssl = CoreSslMode.Prefer, string database = "") =>
        new() { Host = "db.example", Port = 1234, User = "alice", Database = database, SslMode = ssl };

    [Theory]
    [InlineData(CoreSslMode.Prefer, Npgsql.SslMode.Prefer)]
    [InlineData(CoreSslMode.Disable, Npgsql.SslMode.Disable)]
    [InlineData(CoreSslMode.Require, Npgsql.SslMode.Require)]
    [InlineData(CoreSslMode.VerifyCA, Npgsql.SslMode.VerifyCA)]
    [InlineData(CoreSslMode.VerifyFull, Npgsql.SslMode.VerifyFull)]
    public void Postgres_MapsSslMode(CoreSslMode ssl, Npgsql.SslMode expected)
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresDriver.BuildConnectionString(Profile(ssl), null));
        Assert.Equal(expected, b.SslMode);
    }

    [Theory]
    [InlineData(CoreSslMode.Prefer, MySqlSslMode.Preferred)]
    [InlineData(CoreSslMode.Disable, MySqlSslMode.None)]
    [InlineData(CoreSslMode.Require, MySqlSslMode.Required)]
    [InlineData(CoreSslMode.VerifyCA, MySqlSslMode.VerifyCA)]
    [InlineData(CoreSslMode.VerifyFull, MySqlSslMode.VerifyFull)]
    public void MySql_MapsSslMode(CoreSslMode ssl, MySqlSslMode expected)
    {
        var b = new MySqlConnectionStringBuilder(MySqlDriver.BuildConnectionString(Profile(ssl), null));
        Assert.Equal(expected, b.SslMode);
        Assert.Equal(ssl == DbClient.Core.Models.SslMode.Disable, b.AllowPublicKeyRetrieval);
    }

    [Fact]
    public void Postgres_EmptyDatabase_DefaultsToPostgres()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresDriver.BuildConnectionString(Profile(), null));
        Assert.Equal("postgres", b.Database);
    }

    [Fact]
    public void MySql_EmptyDatabase_StaysEmpty()
    {
        var b = new MySqlConnectionStringBuilder(MySqlDriver.BuildConnectionString(Profile(), null));
        Assert.Equal("", b.Database);
    }

    [Fact]
    public void Postgres_RoundTripsFields()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresDriver.BuildConnectionString(Profile(database: "app"), NastyPassword));

        Assert.Equal(NastyPassword, b.Password);
        Assert.Equal(("db.example", 1234, "alice", "app"), (b.Host, b.Port, b.Username, b.Database));
        Assert.False(b.Pooling);
    }

    [Fact]
    public void MySql_RoundTripsFields()
    {
        var b = new MySqlConnectionStringBuilder(MySqlDriver.BuildConnectionString(Profile(database: "app"), NastyPassword));

        Assert.Equal(NastyPassword, b.Password);
        Assert.Equal(("db.example", 1234u, "alice", "app"), (b.Server, b.Port, b.UserID, b.Database));
        Assert.False(b.Pooling);
    }

    private static ConnectionProfile WithCertificates()
    {
        var p = Profile(CoreSslMode.VerifyCA);
        (p.SslCaFile, p.SslCertFile, p.SslKeyFile) = (@"C:\certs\ca.pem", @"C:\certs\client.pem", @"C:\certs\client.key");
        return p;
    }

    [Fact]
    public void Postgres_MapsCertificateFiles()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresDriver.BuildConnectionString(WithCertificates(), null));
        Assert.Equal((@"C:\certs\ca.pem", @"C:\certs\client.pem", @"C:\certs\client.key"), (b.RootCertificate, b.SslCertificate, b.SslKey));
    }

    [Fact]
    public void MySql_MapsCertificateFiles()
    {
        var b = new MySqlConnectionStringBuilder(MySqlDriver.BuildConnectionString(WithCertificates(), null));
        Assert.Equal((@"C:\certs\ca.pem", @"C:\certs\client.pem", @"C:\certs\client.key"), (b.SslCa, b.SslCert, b.SslKey));
    }
}
