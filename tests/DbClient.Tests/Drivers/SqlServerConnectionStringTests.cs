using DbClient.Core.Models;
using DbClient.Drivers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DbClient.Tests.Drivers;

public sealed class SqlServerConnectionStringTests
{
    private static SqlConnectionStringBuilder Build(ConnectionProfile profile, string? password = null) =>
        new(SqlServerDriver.BuildConnectionString(profile, password));

    private static ConnectionProfile Profile() =>
        new() { Kind = DatabaseKind.SqlServer, Host = "db.example", Port = 1433, User = "sa" };

    [Theory]
    [InlineData(SslMode.Disable, "Optional", true)]
    [InlineData(SslMode.Prefer, "Mandatory", true)]
    [InlineData(SslMode.Require, "Mandatory", true)]
    [InlineData(SslMode.VerifyCA, "Mandatory", false)]
    [InlineData(SslMode.VerifyFull, "Mandatory", false)]
    public void MapsSslMode(SslMode ssl, string encrypt, bool trustServerCertificate)
    {
        var profile = Profile();
        profile.SslMode = ssl;

        var b = Build(profile);

        Assert.Equal(SqlConnectionEncryptOption.Parse(encrypt), b.Encrypt);
        Assert.Equal(trustServerCertificate, b.TrustServerCertificate);
    }

    [Theory]
    [InlineData(@"db.example\SQLEXPRESS", 1433, @"db.example\SQLEXPRESS")]
    [InlineData("db.example", 1433, "db.example,1433")]
    [InlineData("db.example", 0, "db.example")]
    public void DataSource_HandlesNamedInstanceAndPort(string host, int port, string expected)
    {
        var profile = Profile();
        profile.Host = host;
        profile.Port = port;

        Assert.Equal(expected, Build(profile).DataSource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankUser_UsesIntegratedSecurity(string user)
    {
        var profile = Profile();
        profile.User = user;

        var b = Build(profile, "ignored");

        Assert.True(b.IntegratedSecurity);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
    }

    [Fact]
    public void SqlAuth_RoundTripsFields()
    {
        var profile = Profile();
        profile.Database = "app";

        var b = Build(profile, "p;a'ss\"w=rd");

        Assert.False(b.IntegratedSecurity);
        Assert.Equal(("sa", "p;a'ss\"w=rd", "app"), (b.UserID, b.Password, b.InitialCatalog));
        Assert.False(b.Pooling);
        Assert.Equal("Qorra", b.ApplicationName);
    }

    [Fact]
    public void EmptyDatabase_LeavesInitialCatalogUnset()
    {
        Assert.Equal("", Build(Profile()).InitialCatalog);
    }
}
